using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.CommandLine;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester;

internal static class LoadTesterApplication
{
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
        PrintRunSummary(options, devices.Count, preflight);

        var outputDirectory = GetNodeOutputDirectory(options);
        var metrics = new LoadTestMetrics();
        var protocol = LoadTestProtocolFactory.Create(options.Target.Protocol);
        var initialRate = options.Connection.StartMode == ConnectionStartMode.Burst
            ? 0
            : options.Connection.ConnectionsPerSecond;
        using var pacer = new ConnectionPacer(initialRate, metrics);
        using var connectionGate = new SemaphoreSlim(options.Connection.MaxConcurrentConnecting);
        var faultController = new FaultInjectionController();
        var runner = new LoadTestRunner(
            options,
            devices,
            protocol,
            metrics,
            pacer,
            connectionGate,
            faultController);

        ScenarioExecutionResult execution;
        var reporter = new TimeSeriesReporter(
            metrics,
            options.Reporting,
            options.ServerObservation,
            outputDirectory);
        try
        {
            // 多节点必须在统一启动时刻之前各自完成基线采样，避免先启动的分片进入后启动分片的环境基线。
            await reporter.CaptureBaselineAsync(cancellationToken);
            await WaitForScheduledStartAsync(options.Distributed.StartAtUtc, cancellationToken);
            reporter.Start();
            execution = await runner.RunAsync(cancellationToken);
        }
        finally
        {
            await reporter.DisposeAsync();
        }
        var gatewayObservation = reporter.GatewaySummary;

        var expectedOnline = checked((int)execution.Measurement.ExpectedOnline);
        var acceptance = AcceptanceEvaluator.Evaluate(
            execution.Measurement,
            options.Acceptance,
            expectedOnline,
            gatewayObservation);
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
            Acceptance = acceptance,
            PreflightWarnings = preflight.Warnings
        };
        await NodeReportWriter.WriteAsync(report, outputDirectory, CancellationToken.None);

        Console.WriteLine($"测试完成，报告目录：{outputDirectory}");
        Console.WriteLine($"验收结果：{(acceptance.Passed ? "通过" : "失败")}");
        if (execution.Cancelled)
            return 130;
        return acceptance.Passed ? 0 : 3;
    }

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

    private static string GetNodeOutputDirectory(LoadTestOptions options)
    {
        var runId = SanitizeRunId(options.Distributed.RunId);
        return Path.GetFullPath(Path.Combine(
            options.Reporting.OutputDirectory,
            runId,
            $"shard-{options.Distributed.ShardIndex:D3}"));
    }

    internal static string SanitizeRunId(string runId)
    {
        var sanitized = new string(runId.Select(item =>
            char.IsLetterOrDigit(item) || item is '-' or '_' or '.' ? item : '_').ToArray());
        // Windows 会忽略目录末尾的点和空格；清理后为空也不能形成“.”或“..”路径穿越片段。
        sanitized = sanitized.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "_" : sanitized;
    }

    private static void PrintRunSummary(
        LoadTestOptions options,
        int localDeviceCount,
        ResourcePreflightResult preflight)
    {
        Console.WriteLine($"RunId: {options.Distributed.RunId}");
        Console.WriteLine($"目标: {options.Target.Protocol} {options.Target.Host}:{options.Target.Port}");
        Console.WriteLine($"分片: {options.Distributed.ShardIndex + 1}/{options.Distributed.ShardCount}，本地设备 {localDeviceCount:N0}/{options.Devices.Count:N0}");
        Console.WriteLine($"场景: {options.Scenario.Name}");
        foreach (var fact in preflight.Facts)
            Console.WriteLine($"[检查] {fact}");
        foreach (var warning in preflight.Warnings)
            Console.WriteLine($"[警告] {warning}");
    }
}
