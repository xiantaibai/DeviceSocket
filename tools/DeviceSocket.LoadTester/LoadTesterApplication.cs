using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.CommandLine;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Runtime;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester;

/// <summary>
/// 作为 LoadTester 进程的应用编排入口，负责命令解析、配置加载、资源预检、负载执行、容错收尾和报告落盘。
/// </summary>
internal static class LoadTesterApplication
{
    /// <summary>
    /// 解析命令行并执行负载测试或报告合并命令。
    /// </summary>
    /// <param name="args">进程命令行参数。</param>
    /// <returns>符合命令行约定的进程退出码。</returns>
    internal static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await ExecuteAsync(CommandLineParser.Parse(args), cancellation.Token);
        }
        catch (ConfigurationValidationException exception)
        {
            Console.Error.WriteLine("配置校验失败：");
            foreach (var error in exception.Errors)
                Console.Error.WriteLine($"- {error}");
            return 2;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            CommandLineParser.PrintHelp();
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"配置 JSON 解析失败：{exception.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("测试已取消。");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"负载测试失败：{exception}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// 执行已经完成语法解析的命令。
    /// </summary>
    /// <param name="command">待执行的强类型命令。</param>
    /// <param name="cancellationToken">取消当前命令的令牌。</param>
    /// <returns>命令执行结果对应的进程退出码。</returns>
    private static async Task<int> ExecuteAsync(LoadTesterCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case HelpCommand:
                CommandLineParser.PrintHelp();
                return 0;
            case RunLoadTesterCommand run:
                return await RunLoadTestAsync(run, cancellationToken);
            case MergeReportCommand merge:
                var aggregate = await ReportMerger.MergeAsync(
                    merge.InputDirectory,
                    merge.OutputDirectory,
                    cancellationToken);
                Console.WriteLine($"已合并 {aggregate.NodeCount} 个节点报告，结果：{(aggregate.Acceptance.Passed ? "通过" : "失败")}");
                return aggregate.Acceptance.Passed ? 0 : 3;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    /// <summary>
    /// 加载场景配置、运行当前分片并写入节点报告。
    /// </summary>
    /// <param name="command">负载测试运行命令。</param>
    /// <param name="cancellationToken">取消测试的令牌。</param>
    /// <returns>测试执行及验收结果对应的进程退出码。</returns>
    private static async Task<int> RunLoadTestAsync(
        RunLoadTesterCommand command,
        CancellationToken cancellationToken)
    {
        var options = await LoadTestConfigurationLoader.LoadAsync(
            command.ConfigurationPath,
            command.Overrides,
            cancellationToken);
        var devices = DeviceAddressAllocator.Allocate(options);
        var preflight = await ResourcePreflight.CheckAsync(options, devices.Count, cancellationToken);
        PrintRunSummary(options, devices, preflight);

        var outputDirectory = GetNodeOutputDirectory(options);
        var metrics = new LoadTestMetrics();
        var onlineRegistry = new DeviceOnlineRegistry();
        var protocol = LoadTestProtocolFactory.Create(options);
        var initialRate = options.Connection.StartMode == ConnectionStartMode.Burst
            ? 0
            : options.Connection.ConnectionsPerSecond;
        using var pacer = new ConnectionPacer(initialRate, metrics);
        using var connectionGate = new SemaphoreSlim(options.Connection.MaxConcurrentConnecting);
        var faultController = new FaultInjectionController();
        var runner = options.Role == LoadTestRole.ApiOnly
            ? null
            : new LoadTestRunner(
                options,
                devices,
                protocol,
                metrics,
                onlineRegistry,
                pacer,
                connectionGate,
                faultController);
        GatherCommandWorkloadRunner? commandRunner = null;
        ScenarioExecutionResult? execution = null;
        Exception? executionFailure = null;
        var cancellationObserved = false;
        var runStartedAtUtc = DateTimeOffset.UtcNow;
        var reporter = new TimeSeriesReporter(
            metrics,
            options,
            devices.Count,
            outputDirectory);
        try
        {
            commandRunner = options.Role == LoadTestRole.DevicesOnly || !options.CommandWorkload.Enabled
                ? null
                : new GatherCommandWorkloadRunner(
                    options,
                    devices,
                    metrics,
                    options.Role == LoadTestRole.Combined &&
                    options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
                        ? onlineRegistry
                        : null);
            // 多节点必须在统一启动时刻之前各自完成基线采样，避免先启动的分片进入后启动分片的环境基线。
            await reporter.CaptureBaselineAsync(cancellationToken);
            await WaitForScheduledStartAsync(options.Distributed.StartAtUtc, cancellationToken);
            reporter.Start();
            if (options.Role == LoadTestRole.ApiOnly)
            {
                var startedAt = DateTimeOffset.UtcNow;
                await commandRunner!.RunAsync(waitForOnline: false, cancellationToken);
                var endedAt = DateTimeOffset.UtcNow;
                execution = new ScenarioExecutionResult(
                    startedAt,
                    endedAt,
                    metrics.Snapshot(endedAt),
                    [],
                    Cancelled: false);
            }
            else
            {
                Func<CancellationToken, Task>? workload = commandRunner is null
                    ? null
                    : token => commandRunner.RunAsync(waitForOnline: true, token);
                execution = await runner!.RunAsync(workload, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved = true;
            Console.Error.WriteLine("测试已取消，正在根据取消前指标生成报告。");
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            Console.Error.WriteLine($"负载测试运行异常，正在生成中止报告：{exception}");
        }
        finally
        {
            commandRunner?.Dispose();
            try
            {
                await reporter.DisposeAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 采样任务失败时仍继续尝试写最终报告；若磁盘本身不可写，最终写入会给出更直接的错误。
                executionFailure ??= exception;
                Console.Error.WriteLine($"周期采样停止异常，正在继续生成中止报告：{exception.Message}");
            }
        }
        var gatewayObservation = reporter.GatewaySummary;

        execution ??= new ScenarioExecutionResult(
            runStartedAtUtc,
            DateTimeOffset.UtcNow,
            metrics.Snapshot(DateTimeOffset.UtcNow),
            [],
            cancellationObserved);

        var expectedOnline = checked((int)execution.Measurement.ExpectedOnline);
        var acceptance = AcceptanceEvaluator.Evaluate(
            execution.Measurement,
            options.Acceptance,
            expectedOnline,
            gatewayObservation,
            options.Role);
        var commandPhases = commandRunner?.Results.ToArray() ?? [];
        var cancelled = execution.Cancelled || cancellationObserved;
        var runStatus = ResolveRunStatus(executionFailure, cancelled, commandPhases, acceptance);
        using var resourceSampler = new ResourceSampler();
        await Task.Delay(20, CancellationToken.None);
        var report = new NodeReport
        {
            RunId = options.Distributed.RunId,
            NodeName = Environment.MachineName,
            ShardIndex = options.Distributed.ShardIndex,
            ShardCount = options.Distributed.ShardCount,
            LocalDeviceCount = devices.Count,
            StartedAtUtc = execution.StartedAtUtc,
            EndedAtUtc = execution.EndedAtUtc,
            Options = options,
            Metrics = execution.Measurement,
            Resources = resourceSampler.Sample(),
            GatewayObservation = gatewayObservation,
            Phases = execution.Phases,
            CommandPhases = commandPhases,
            Acceptance = acceptance,
            PreflightWarnings = preflight.Warnings,
            RunStatus = runStatus,
            FailureReason = ResolveFailureReason(executionFailure, cancelled, commandPhases)
        };
        await NodeReportWriter.WriteAsync(report, outputDirectory, CancellationToken.None);

        Console.WriteLine($"测试完成，报告目录：{outputDirectory}");
        Console.WriteLine($"运行状态：{runStatus}，验收结果：{(acceptance.Passed ? "通过" : "失败")}");
        if (cancelled)
            return 130;
        if (executionFailure is not null)
            return 1;
        return acceptance.Passed && runStatus == LoadTestRunStatus.Passed ? 0 : 3;
    }

    /// <summary>
    /// 根据异常、取消、HTTP 阶段跳过和自动验收结果计算节点运行状态，避免把“报告成功写出”误认为“压测通过”。
    /// </summary>
    /// <param name="executionFailure">运行期间未恢复的异常；正常完成时为空。</param>
    /// <param name="cancelled">是否由用户或外部取消令牌终止。</param>
    /// <param name="commandPhases">已经完成、跳过或失败的 HTTP 指令阶段。</param>
    /// <param name="acceptance">根据最终指标计算的自动验收结果。</param>
    /// <returns>写入节点报告并决定退出码的执行状态。</returns>
    private static LoadTestRunStatus ResolveRunStatus(
        Exception? executionFailure,
        bool cancelled,
        IReadOnlyList<CommandPhaseResult> commandPhases,
        AcceptanceResult acceptance)
    {
        if (cancelled)
            return LoadTestRunStatus.Cancelled;
        if (executionFailure is not null)
            return LoadTestRunStatus.Aborted;
        if (commandPhases.Any(item => item.Status == CommandPhaseStatus.Skipped))
            return LoadTestRunStatus.CompletedWithSkippedPhases;
        return acceptance.Passed ? LoadTestRunStatus.Passed : LoadTestRunStatus.Failed;
    }

    /// <summary>
    /// 生成写入最终报告的安全失败原因，不包含异常堆栈和可能存在于内部对象中的敏感配置。
    /// </summary>
    /// <param name="executionFailure">运行期间未恢复的异常。</param>
    /// <param name="cancelled">是否由用户或外部令牌取消。</param>
    /// <param name="commandPhases">已经完成、跳过或失败的 HTTP 指令阶段。</param>
    /// <returns>取消、异常或阶段跳过的简要中文说明；完全正常结束时为空。</returns>
    private static string? ResolveFailureReason(
        Exception? executionFailure,
        bool cancelled,
        IReadOnlyList<CommandPhaseResult> commandPhases)
    {
        if (cancelled)
            return "测试由用户或外部取消令牌终止，报告保留取消前累计指标。";
        if (executionFailure is not null)
            return $"{executionFailure.GetType().Name}: {executionFailure.Message}";

        var skipped = commandPhases
            .Where(item => item.Status == CommandPhaseStatus.Skipped)
            .Select(item => $"{item.Name}: {item.StatusReason ?? "在线门槛未满足"}")
            .ToArray();
        return skipped.Length == 0
            ? null
            : $"HTTP 指令阶段已跳过：{string.Join("；", skipped)}";
    }

    /// <summary>
    /// 在多机测试中等待统一 UTC 启动时刻。
    /// </summary>
    /// <param name="startAtUtc">计划启动时间；为空时立即返回。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>等待完成的异步任务。</returns>
    private static async Task WaitForScheduledStartAsync(
        DateTimeOffset? startAtUtc,
        CancellationToken cancellationToken)
    {
        if (startAtUtc is not { } startAt)
            return;
        var delay = startAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
            return;
        Console.WriteLine($"等待统一启动时间 {startAt:O}，剩余 {delay.TotalSeconds:F1} 秒。");
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// 计算当前 RunId 和分片独占的报告输出目录。
    /// </summary>
    /// <param name="options">完整负载测试配置。</param>
    /// <returns>当前节点报告目录的绝对路径。</returns>
    private static string GetNodeOutputDirectory(LoadTestOptions options)
    {
        var runId = SanitizeRunId(options.Distributed.RunId);
        return Path.GetFullPath(Path.Combine(
            options.Reporting.OutputDirectory,
            runId,
            $"shard-{options.Distributed.ShardIndex:D3}"));
    }

    /// <summary>
    /// 清理 RunId 中不能安全用于目录名的字符。
    /// </summary>
    /// <param name="runId">原始运行编号。</param>
    /// <returns>可安全用作单个目录名称的运行编号。</returns>
    internal static string SanitizeRunId(string runId)
    {
        var sanitized = new string(runId.Select(item =>
            char.IsLetterOrDigit(item) || item is '-' or '_' or '.' ? item : '_').ToArray());
        // Windows 会忽略目录末尾的点和空格；清理后为空也不能形成“.”或“..”路径穿越片段。
        sanitized = sanitized.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "_" : sanitized;
    }

    /// <summary>
    /// 输出目标服务、兼容配置、分片和启动前资源检查摘要。
    /// </summary>
    /// <param name="options">完整负载测试配置。</param>
    /// <param name="devices">当前分片实际负责的虚拟设备。</param>
    /// <param name="preflight">启动前资源检查结果。</param>
    private static void PrintRunSummary(
        LoadTestOptions options,
        IReadOnlyList<VirtualDeviceDescriptor> devices,
        ResourcePreflightResult preflight)
    {
        Console.WriteLine($"RunId: {options.Distributed.RunId}");
        Console.WriteLine($"角色: {options.Role}");
        Console.WriteLine($"目标: {options.Target.Protocol}/{options.Target.CompatibilityProfile} {options.Target.Host}:{options.Target.Port}");
        Console.WriteLine($"分片: {options.Distributed.ShardIndex + 1}/{options.Distributed.ShardCount}，本地设备 {devices.Count:N0}/{options.Devices.Count:N0}");
        if (devices.Count > 0)
            Console.WriteLine($"本分片首尾设备: {devices[0].DeviceId} ～ {devices[^1].DeviceId}");
        Console.WriteLine($"场景: {options.Scenario.Name}");
        foreach (var fact in preflight.Facts)
            Console.WriteLine($"[检查] {fact}");
        foreach (var warning in preflight.Warnings)
            Console.WriteLine($"[警告] {warning}");
    }
}
