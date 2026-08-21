using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;

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
    FaultInjectionController faultController)
{
    private readonly Dictionary<int, DeviceHandle> _handles = [];

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
                metrics.SetExpectedOnline(desired, phaseStartedAt);
                pacer.UpdateRate(ResolveConnectionRate(phase));
                faultController.Apply(phase.Faults);
                Console.WriteLine(
                    $"[阶段开始] {phase.Name} ({phase.Kind})，持续 {phase.DurationSeconds} 秒，" +
                    $"本地目标设备 {desired:N0}/{devices.Count:N0}");
                await ResizeAsync(desired);
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
                phaseResults.Add(new PhaseResult(
                    phase.Name,
                    phase.Kind,
                    phaseStartedAt,
                    phaseEndedAt,
                    desired,
                    snapshot));
                Console.WriteLine(
                    $"[阶段结束] {phase.Name}，在线 {snapshot.ProtocolOnline:N0}/{snapshot.ExpectedOnline:N0}，" +
                    $"累计重连 {snapshot.ReconnectsScheduled:N0}");
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
                    LastRecoveryMilliseconds = measurement.LastRecoveryMilliseconds
                };
            }

            return new ScenarioExecutionResult(
                startedAt,
                DateTimeOffset.UtcNow,
                measurement,
                phaseResults,
                cancelled);
        }
        finally
        {
            faultController.Reset();
            await StopAllAsync();
        }
    }

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

    private int ResolveConnectionRate(ScenarioPhaseOptions phase)
    {
        if (phase.ConnectionsPerSecond.HasValue)
            return phase.ConnectionsPerSecond.Value;
        return options.Connection.StartMode == ConnectionStartMode.Burst
            ? 0
            : options.Connection.ConnectionsPerSecond;
    }

    private int GetLocalTargetCount(double percentage)
    {
        var globalTarget = (int)Math.Ceiling(options.Devices.Count * percentage / 100d);
        return devices.Count(device => device.GlobalOrdinal < globalTarget);
    }

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
                faultController);
            var task = session.RunAsync(cancellation.Token);
            _handles.Add(descriptor.GlobalOrdinal, new DeviceHandle(descriptor, session, cancellation, task));
        }
    }

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
}
