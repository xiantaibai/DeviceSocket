using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 按场景阶段调整当前分片的虚拟设备数量、连接速率和故障参数，并在设备保持在线期间并行协调可选 HTTP 指令工作负载。
/// </summary>
/// <param name="options">已经完成路径解析和合法性校验的完整负载配置。</param>
/// <param name="devices">仅属于当前分片的虚拟设备描述集合。</param>
/// <param name="protocol">所有设备共享的无状态协议适配器。</param>
/// <param name="metrics">本节点共享指标容器。</param>
/// <param name="onlineRegistry">供设备会话维护并供 HTTP 指令工作负载读取的在线注册表。</param>
/// <param name="pacer">支持阶段动态调速的连接节流器。</param>
/// <param name="connectionGate">单节点连接与登录并发闸门。</param>
/// <param name="faultController">向全部设备原子发布阶段故障参数的控制器。</param>
internal sealed class LoadTestRunner(
    LoadTestOptions options,
    IReadOnlyList<VirtualDeviceDescriptor> devices,
    ILoadTestProtocol protocol,
    LoadTestMetrics metrics,
    IDeviceOnlineRegistry onlineRegistry,
    ConnectionPacer pacer,
    SemaphoreSlim connectionGate,
    FaultInjectionController faultController,
    GatherCommandTraceRegistry? commandTraceRegistry = null)
{
    private readonly Dictionary<int, DeviceHandle> _handles = [];

    /// <summary>
    /// 获取最近一次正常完成或异常中止前保存的执行快照；尚未执行时为空。
    /// </summary>
    internal ScenarioExecutionResult? LastExecution { get; private set; }

    /// <summary>
    /// 执行 TCP 设备阶段，并可并行运行独立 HTTP 工作负载；阶段结束后会等待工作负载完成再关闭设备连接。
    /// </summary>
    /// <param name="concurrentWorkload">与设备阶段并行启动的工作负载；为空时只运行设备模拟。</param>
    /// <param name="cancellationToken">取消整轮测试的令牌。</param>
    /// <returns>停止设备前的测量快照、阶段结果和取消状态。</returns>
    internal async Task<ScenarioExecutionResult> RunAsync(
        Func<CancellationToken, Task>? concurrentWorkload,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var phaseResults = new List<PhaseResult>();
        var measurement = metrics.Snapshot(startedAt);
        var cancelled = false;
        var workloadTask = concurrentWorkload?.Invoke(cancellationToken);

        try
        {
            var phases = GetPhases();
            for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
            {
                var phase = phases[phaseIndex];
                if (phase.Kind == ScenarioPhaseKind.Cooldown &&
                    workloadTask is not null &&
                    !workloadTask.IsCompleted)
                {
                    // HTTP 指令未排空前保持上一阶段设备在线，避免冷却断线把业务超时误判为服务端阻塞。
                    Console.WriteLine("[HTTP等待] 指令阶段尚未结束，保持当前设备在线后再进入冷却。");
                    await workloadTask;
                }
                var phaseStartedAt = DateTimeOffset.UtcNow;
                var desired = GetLocalTargetCount(phase.TargetDevicePercentage);
                metrics.SetCurrentPhase(phase.Name, phase.Kind, phase.ExpectedServerRestart);
                metrics.SetExpectedOnline(desired, phaseStartedAt);
                pacer.UpdateRate(ResolveConnectionRate(phase));
                faultController.Apply(phase.Faults);
                var phaseStartedMetrics = metrics.Snapshot(phaseStartedAt);
                Console.WriteLine(
                    $"[阶段开始] {phase.Name} ({phase.Kind})，持续 {phase.DurationSeconds} 秒，" +
                    $"本地目标设备 {desired:N0}/{devices.Count:N0}");
                await ResizeAsync(desired);
                var manualAction = await RunManualActionAsync(phase, cancellationToken);
                using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var disconnectTask = RunDisconnectWaveAsync(phase.Faults, phaseCancellation.Token);

                try
                {
                    var phaseDelay = Task.Delay(TimeSpan.FromSeconds(phase.DurationSeconds), cancellationToken);
                    if (workloadTask is not null && !workloadTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(phaseDelay, workloadTask);
                        if (completed == workloadTask)
                            await workloadTask;
                    }
                    await phaseDelay;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                finally
                {
                    phaseCancellation.Cancel();
                    try
                    {
                        await disconnectTask;
                    }
                    catch (OperationCanceledException) when (phaseCancellation.IsCancellationRequested)
                    {
                    }
                }

                var phaseEndedAt = DateTimeOffset.UtcNow;
                var snapshot = metrics.Snapshot(phaseEndedAt);
                var onlineRange = metrics.GetCurrentPhaseOnlineRange();
                phaseResults.Add(new PhaseResult(
                    phase.Name,
                    phase.Kind,
                    phaseStartedAt,
                    phaseEndedAt,
                    desired,
                    snapshot,
                    phaseStartedMetrics,
                    PhaseMetricDelta.Between(phaseStartedMetrics, snapshot),
                    onlineRange.Minimum,
                    onlineRange.Maximum,
                    phase.ExpectedServerRestart,
                    manualAction.PromptedAtUtc,
                    manualAction.WindowStartedAtUtc,
                    manualAction.WindowEndedAtUtc));
                Console.WriteLine(
                    $"[阶段结束] {phase.Name}，在线 {snapshot.ProtocolOnline:N0}/{snapshot.ExpectedOnline:N0}，" +
                    $"阶段重连 {snapshot.ReconnectsScheduled - phaseStartedMetrics.ReconnectsScheduled:N0}，" +
                    $"阶段最低在线 {onlineRange.Minimum:N0}");
                if (phase.Kind != ScenarioPhaseKind.Cooldown)
                    measurement = snapshot;
                if (cancelled)
                    break;
            }

            if (!cancelled && workloadTask is not null)
            {
                await workloadTask;
                // HTTP 阶段可能晚于最后一个非冷却阶段结束；累计值取最新快照，但在线仪表保留最后测试阶段，不能被正常冷却到零覆盖。
                measurement = metrics.Snapshot(DateTimeOffset.UtcNow) with
                {
                    ExpectedOnline = measurement.ExpectedOnline,
                    Connecting = measurement.Connecting,
                    TcpConnected = measurement.TcpConnected,
                    ProtocolOnline = measurement.ProtocolOnline,
                    BackoffWaiting = measurement.BackoffWaiting,
                    OutageActive = measurement.OutageActive,
                    LastRecoveryMilliseconds = measurement.LastRecoveryMilliseconds,
                    Outages = measurement.Outages,
                    WorstRecoveryMilliseconds = measurement.WorstRecoveryMilliseconds,
                    CurrentStableOnlineMilliseconds = measurement.CurrentStableOnlineMilliseconds,
                    MaximumStableOnlineMilliseconds = measurement.MaximumStableOnlineMilliseconds,
                    CurrentPhaseName = measurement.CurrentPhaseName
                };
            }

            LastExecution = new ScenarioExecutionResult(
                startedAt,
                DateTimeOffset.UtcNow,
                measurement,
                phaseResults,
                cancelled);
            return LastExecution;
        }
        catch
        {
            var failedAt = DateTimeOffset.UtcNow;
            // 必须在 finally 关闭全部设备前保留现场，否则异常报告会把在线数和连接状态误写成清理后的零值。
            LastExecution = new ScenarioExecutionResult(
                startedAt,
                failedAt,
                metrics.Snapshot(failedAt),
                phaseResults,
                cancelled);
            throw;
        }
        finally
        {
            faultController.Reset();
            await StopAllAsync();
        }
    }

    /// <summary>
    /// 获取显式阶段列表；未配置阶段时根据顶层场景时长构造单一全量稳态阶段。
    /// </summary>
    /// <returns>至少包含一个阶段的有序列表。</returns>
    private IReadOnlyList<ScenarioPhaseOptions> GetPhases()
    {
        if (options.Scenario.Phases.Count > 0)
            return options.Scenario.Phases;
        return
        [
            new ScenarioPhaseOptions
            {
                Name = options.Scenario.Name,
                Kind = ScenarioPhaseKind.Steady,
                DurationSeconds = options.Scenario.DurationSeconds,
                TargetDevicePercentage = 100
            }
        ];
    }

    /// <summary>
    /// 解析当前阶段的全局连接速率，并换算为本节点连接调度器使用的本地速率。
    /// </summary>
    /// <param name="phase">当前 TCP 场景阶段；阶段未覆盖速率时继承顶层连接配置。</param>
    /// <returns>当前分片连接数/秒；Burst 模式且阶段未覆盖速率时返回零。</returns>
    private double ResolveConnectionRate(ScenarioPhaseOptions phase)
    {
        if (phase.ConnectionsPerSecond.HasValue)
            return ResolveLocalConnectionRate(
                phase.ConnectionsPerSecond.Value,
                options.Distributed.ShardCount);
        return options.Connection.StartMode == ConnectionStartMode.Burst
            ? 0
            : ResolveLocalConnectionRate(
                options.Connection.ConnectionsPerSecond,
                options.Distributed.ShardCount);
    }

    /// <summary>
    /// 把场景中“所有分片合计”的连接速率换算为单个分片速率。
    /// </summary>
    /// <param name="globalConnectionsPerSecond">所有分片合计连接数/秒；零表示不启用限速。</param>
    /// <param name="shardCount">共同执行本轮测试的分片总数，必须大于零。</param>
    /// <returns>当前分片应使用的连接数/秒；使用小数避免低速场景因整数取整被放大或退化为 Burst。</returns>
    internal static double ResolveLocalConnectionRate(
        int globalConnectionsPerSecond,
        int shardCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalConnectionsPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        return globalConnectionsPerSecond / (double)shardCount;
    }

    /// <summary>
    /// 按全局设备序号和目标百分比计算当前分片应启动的设备数，保证多分片合计值与全局目标一致。
    /// </summary>
    /// <param name="percentage">全局目标设备百分比，取值由配置校验限制在 0～100。</param>
    /// <returns>当前分片应保持运行的设备数。</returns>
    private int GetLocalTargetCount(double percentage)
    {
        var globalTarget = (int)Math.Ceiling(options.Devices.Count * percentage / 100d);
        return devices.Count(device => device.GlobalOrdinal < globalTarget);
    }

    /// <summary>
    /// 在启用手工动作的阶段显示准备提示和操作窗口倒计时；所有分片等待相同时间，仅协调节点按配置输出提示。
    /// </summary>
    /// <param name="phase">当前阶段及其手工动作配置。</param>
    /// <param name="cancellationToken">取消整轮测试的令牌。</param>
    /// <returns>操作提示和窗口的 UTC 时间标记；未启用时全部为空。</returns>
    private async Task<ManualActionTiming> RunManualActionAsync(
        ScenarioPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        var action = phase.ManualAction;
        if (!action.Enabled)
            return new ManualActionTiming(null, null, null);

        var shouldPrompt = !action.CoordinatorOnlyPrompt || options.Distributed.ShardIndex == 0;
        var promptedAt = DateTimeOffset.UtcNow;
        if (shouldPrompt)
        {
            Console.WriteLine();
            Console.WriteLine($"[需要人工操作] 阶段：{phase.Name}");
            Console.WriteLine($"[需要人工操作] {action.Message}");
            Console.WriteLine(
                $"[需要人工操作] {action.PreparationSeconds} 秒后开始操作窗口，" +
                $"窗口持续 {action.OperationSeconds} 秒。测试程序会自动等待，无需按键确认。");
        }

        await RunCountdownAsync(
            action.PreparationSeconds,
            "准备倒计时",
            shouldPrompt,
            cancellationToken);
        var windowStartedAt = DateTimeOffset.UtcNow;
        if (shouldPrompt)
        {
            Console.WriteLine();
            Console.WriteLine($"[请立即操作] {action.Message}");
            Console.WriteLine($"[请立即操作] 请在 {action.OperationSeconds} 秒内完成，期间连接与掉线指标已经开始记录。");
        }
        await RunCountdownAsync(
            action.OperationSeconds,
            "操作窗口",
            shouldPrompt,
            cancellationToken);
        var windowEndedAt = DateTimeOffset.UtcNow;
        if (shouldPrompt)
            Console.WriteLine("[人工操作窗口结束] 继续执行当前阶段的稳定性观察。");

        return new ManualActionTiming(promptedAt, windowStartedAt, windowEndedAt);
    }

    /// <summary>
    /// 执行可取消的秒级倒计时，并在长窗口中按五秒间隔、最后五秒按每秒输出，兼顾可操作性与控制台噪声。
    /// </summary>
    /// <param name="seconds">倒计时总秒数；零表示不等待。</param>
    /// <param name="title">控制台倒计时标签。</param>
    /// <param name="writeConsole">是否在当前分片输出倒计时。</param>
    /// <param name="cancellationToken">取消整轮测试的令牌。</param>
    /// <returns>倒计时完成的异步任务。</returns>
    private static async Task RunCountdownAsync(
        int seconds,
        string title,
        bool writeConsole,
        CancellationToken cancellationToken)
    {
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            if (writeConsole && (remaining <= 5 || remaining == seconds || remaining % 5 == 0))
                Console.WriteLine($"[{title}] 剩余 {remaining} 秒");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// 将当前分片设备会话扩缩到指定数量；缩容会等待旧任务完整退出后再释放取消源。
    /// </summary>
    /// <param name="desired">当前分片期望运行的设备数。</param>
    /// <returns>全部新增会话已启动且待移除会话已退出时完成。</returns>
    private async Task ResizeAsync(int desired)
    {
        var desiredDevices = devices.Take(desired).Select(item => item.GlobalOrdinal).ToHashSet();
        var stopping = _handles
            .Where(item => !desiredDevices.Contains(item.Key))
            .Select(item => item.Value)
            .ToArray();
        foreach (var handle in stopping)
            handle.Cancellation.Cancel();
        if (stopping.Length > 0)
        {
            await Task.WhenAll(stopping.Select(item => item.Task));
            foreach (var handle in stopping)
            {
                _handles.Remove(handle.Descriptor.GlobalOrdinal);
                handle.Cancellation.Dispose();
            }
        }

        foreach (var descriptor in devices.Take(desired))
        {
            if (_handles.ContainsKey(descriptor.GlobalOrdinal))
                continue;
            var cancellation = new CancellationTokenSource();
            var session = new VirtualDeviceSession(
                descriptor,
                options,
                protocol,
                metrics,
                onlineRegistry,
                pacer,
                connectionGate,
                faultController,
                commandTraceRegistry);
            var task = session.RunAsync(cancellation.Token);
            _handles.Add(descriptor.GlobalOrdinal, new DeviceHandle(descriptor, session, cancellation, task));
        }
    }

    /// <summary>
    /// 把目标在线数先降为零，再取消并等待所有设备会话退出，避免正常收尾被记录成掉线事件。
    /// </summary>
    /// <returns>所有设备任务已退出且资源已释放时完成。</returns>
    private async Task StopAllAsync()
    {
        metrics.SetExpectedOnline(0, DateTimeOffset.UtcNow);
        foreach (var handle in _handles.Values)
            handle.Cancellation.Cancel();
        await Task.WhenAll(_handles.Values.Select(item => item.Task));
        foreach (var handle in _handles.Values)
            handle.Cancellation.Dispose();
        _handles.Clear();
    }

    /// <summary>
    /// 按阶段故障比例、批大小和间隔向稳定顺序的设备发送主动断线请求。
    /// </summary>
    /// <param name="faults">当前阶段故障注入参数。</param>
    /// <param name="cancellationToken">阶段结束时取消剩余断线批次的令牌。</param>
    /// <returns>全部计划断线批次已发出或阶段已取消时完成。</returns>
    private async Task RunDisconnectWaveAsync(
        FaultInjectionOptions faults,
        CancellationToken cancellationToken)
    {
        if (faults.DisconnectPercentage <= 0 || _handles.Count == 0)
            return;

        var count = Math.Clamp(
            (int)Math.Ceiling(_handles.Count * faults.DisconnectPercentage / 100d),
            1,
            _handles.Count);
        var targets = _handles.Values
            .OrderBy(item => item.Descriptor.GlobalOrdinal)
            .Take(count)
            .ToArray();
        var batchSize = faults.DisconnectBatchSize <= 0
            ? targets.Length
            : Math.Min(faults.DisconnectBatchSize, targets.Length);
        for (var offset = 0; offset < targets.Length; offset += batchSize)
        {
            foreach (var handle in targets.Skip(offset).Take(batchSize))
                handle.Session.RequestDisconnect();
            if (offset + batchSize < targets.Length && faults.DisconnectIntervalMilliseconds > 0)
                await Task.Delay(faults.DisconnectIntervalMilliseconds, cancellationToken);
        }
    }

    /// <summary>
    /// 绑定单台设备的稳定描述、运行会话、独立取消源和后台任务，供场景扩缩容和主动断线统一管理。
    /// </summary>
    /// <param name="Descriptor">设备身份与全局序号。</param>
    /// <param name="Session">负责通信生命周期的虚拟会话。</param>
    /// <param name="Cancellation">仅停止当前设备后台任务的取消源。</param>
    /// <param name="Task">当前设备运行任务。</param>
    private sealed record DeviceHandle(
        VirtualDeviceDescriptor Descriptor,
        VirtualDeviceSession Session,
        CancellationTokenSource Cancellation,
        Task Task);

    /// <summary>
    /// 保存人工操作提示、窗口开始和窗口结束时间，写入阶段报告后可与服务端 PID、连接和 ACK 指标对齐。
    /// </summary>
    /// <param name="PromptedAtUtc">首次显示或逻辑触发提示的 UTC 时间。</param>
    /// <param name="WindowStartedAtUtc">正式操作窗口开始的 UTC 时间。</param>
    /// <param name="WindowEndedAtUtc">正式操作窗口结束的 UTC 时间。</param>
    private sealed record ManualActionTiming(
        DateTimeOffset? PromptedAtUtc,
        DateTimeOffset? WindowStartedAtUtc,
        DateTimeOffset? WindowEndedAtUtc);
}
