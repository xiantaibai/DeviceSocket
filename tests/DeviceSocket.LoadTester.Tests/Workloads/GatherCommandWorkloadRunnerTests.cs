using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Runtime;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Tests.Workloads;

/// <summary>
/// 验证 HTTP 指令驱动使用老接口字段、业务 success 判定和独立指标。
/// </summary>
public sealed class GatherCommandWorkloadRunnerTests
{
    /// <summary>
    /// 验证 apiOnly 运行器发送正确读表 JSON，并把 BaseDto success=true 计入 API 成功指标。
    /// </summary>
    [Fact]
    public async Task ApiOnly_SendsReadingRequestAndRecordsBusinessSuccess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = TestOptionsFactory.Create();
            options.Role = LoadTestRole.ApiOnly;
            options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            options.Devices.Count = 1;
            options.CommandWorkload = new CommandWorkloadOptions
            {
                Enabled = true,
                BaseUrl = $"http://127.0.0.1:{port}",
                AuthorizationHeaderName = string.Empty,
                MeterIdStart = 7_001,
                OperatorUserId = 99,
                Phases =
                [
                    new CommandPhaseOptions
                    {
                        Name = "one",
                        TargetDeviceCount = 1,
                        RequestsPerDevice = 1,
                        RequestsPerSecond = 100,
                        MaxConcurrency = 1,
                        ShowResult = true
                    }
                ]
            };
            var devices = DeviceAddressAllocator.Allocate(options);
            var metrics = new LoadTestMetrics();
            var serverTask = ServeOneSuccessAsync(listener);
            using var runner = new GatherCommandWorkloadRunner(options, devices, metrics);

            await runner.RunAsync(waitForOnline: false, CancellationToken.None);
            var requestBody = await serverTask;

            using var document = JsonDocument.Parse(requestBody);
            Assert.True(document.RootElement.GetProperty("show_result").GetBoolean());
            Assert.Equal(7_001, document.RootElement.GetProperty("data").GetProperty("meter_id").GetInt32());
            Assert.Equal(99, document.RootElement.GetProperty("data").GetProperty("oper_uid").GetInt32());
            Assert.Equal(6, document.RootElement.GetProperty("data").GetProperty("oper_source").GetInt32());
            var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
            Assert.Equal(1, snapshot.ApiRequestsStarted);
            Assert.Equal(1, snapshot.ApiRequestsSucceeded);
            Assert.Equal(0, snapshot.ApiRequestsFailed);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 验证 onlineRegistry 会用固定序号范围之外的在线设备补足阶段数量，而不是只过滤前 N 台设备。
    /// </summary>
    [Fact]
    public async Task OnlineRegistry_ReplacesOfflineFixedPrefixWithAnotherOnlineDevice()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = TestOptionsFactory.Create();
            options.Role = LoadTestRole.Combined;
            options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            options.Devices.Count = 2;
            options.CommandWorkload = new CommandWorkloadOptions
            {
                Enabled = true,
                BaseUrl = $"http://127.0.0.1:{port}",
                AuthorizationHeaderName = string.Empty,
                MeterIdStart = 7_001,
                TargetDeviceSource = CommandTargetSource.OnlineRegistry,
                OnlineStableSeconds = 0,
                OnlineWaitTimeoutSeconds = 1,
                Phases =
                [
                    new CommandPhaseOptions
                    {
                        Name = "replace-offline-prefix",
                        TargetDeviceCount = 1,
                        RequestsPerDevice = 1,
                        RequestsPerSecond = 100,
                        MaxConcurrency = 1,
                        MinimumOnlineCount = 1,
                        OnlineStableSeconds = 0,
                        OnlineWaitTimeoutSeconds = 1
                    }
                ]
            };
            var devices = DeviceAddressAllocator.Allocate(options);
            var metrics = new LoadTestMetrics();
            var onlineRegistry = new DeviceOnlineRegistry();
            onlineRegistry.RegisterOnline(1);
            var serverTask = ServeOneSuccessAsync(listener);
            using var runner = new GatherCommandWorkloadRunner(options, devices, metrics, onlineRegistry);

            await runner.RunAsync(waitForOnline: true, CancellationToken.None);
            var requestBody = await serverTask;

            using var document = JsonDocument.Parse(requestBody);
            Assert.Equal(7_002, document.RootElement.GetProperty("data").GetProperty("meter_id").GetInt32());
            var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
            Assert.Equal(1, snapshot.ApiTargetsRequired);
            Assert.Equal(1, snapshot.ApiTargetsOnline);
            Assert.Equal(0, snapshot.ApiRequestsSkippedOffline);
            Assert.Equal(1, snapshot.ApiRequestsStarted);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 验证 onlineRegistry 来源只会请求注册表中的在线设备，离线候选被跳过并单独计量。
    /// </summary>
    [Fact]
    public async Task OnlineRegistry_SkipsOfflineTargetAndRequestsOnlineDevice()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = TestOptionsFactory.Create();
            options.Role = LoadTestRole.Combined;
            options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            options.Devices.Count = 2;
            options.CommandWorkload = new CommandWorkloadOptions
            {
                Enabled = true,
                BaseUrl = $"http://127.0.0.1:{port}",
                AuthorizationHeaderName = string.Empty,
                MeterIdStart = 7_001,
                TargetDeviceSource = CommandTargetSource.OnlineRegistry,
                OnlineStableSeconds = 0,
                OnlineWaitTimeoutSeconds = 1,
                Phases =
                [
                    new CommandPhaseOptions
                    {
                        Name = "online-targets",
                        TargetDeviceCount = 2,
                        RequestsPerDevice = 1,
                        RequestsPerSecond = 100,
                        MaxConcurrency = 1,
                        MinimumOnlineCount = 0,
                        OnlineStableSeconds = 0,
                        OnlineWaitTimeoutSeconds = 1,
                        OnlineGateFailureAction = OnlineGateFailureAction.RunPhase
                    }
                ]
            };
            var devices = DeviceAddressAllocator.Allocate(options);
            var metrics = new LoadTestMetrics();
            var onlineRegistry = new DeviceOnlineRegistry();
            onlineRegistry.RegisterOnline(1);
            var serverTask = ServeOneSuccessAsync(listener);
            using var runner = new GatherCommandWorkloadRunner(options, devices, metrics, onlineRegistry);

            await runner.RunAsync(waitForOnline: true, CancellationToken.None);
            var requestBody = await serverTask;

            using var document = JsonDocument.Parse(requestBody);
            Assert.Equal(7_002, document.RootElement.GetProperty("data").GetProperty("meter_id").GetInt32());
            var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
            Assert.Equal(2, snapshot.ApiTargetsRequired);
            Assert.Equal(1, snapshot.ApiTargetsOnline);
            Assert.Equal(1, snapshot.ApiRequestsSkippedOffline);
            Assert.Equal(1, snapshot.ApiRequestsStarted);
            Assert.Equal(1, snapshot.ApiRequestsSucceeded);
            Assert.Equal(0, snapshot.ApiRequestsFailed);
            var phase = Assert.Single(runner.Results);
            Assert.Equal(2, phase.ApiTargetsRequired);
            Assert.Equal(1, phase.ApiTargetsOnline);
            Assert.Equal(1, phase.ApiRequestsSkippedOffline);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 验证设备在进入有界队列后离线时，消费者会在真正调用 HTTP API 前再次拦截并记录跳过。
    /// </summary>
    [Fact]
    public async Task OnlineRegistry_DeviceDisconnectsAfterQueue_DoesNotSendHttpRequest()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Devices.Count = 1;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:1",
            AuthorizationHeaderName = string.Empty,
            MeterIdStart = 7_001,
            TargetDeviceSource = CommandTargetSource.OnlineRegistry,
            OnlineStableSeconds = 0,
            OnlineWaitTimeoutSeconds = 1,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "disconnect-after-queue",
                    TargetDeviceCount = 1,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 100,
                    MaxConcurrency = 1,
                    MinimumOnlineCount = 0,
                    OnlineStableSeconds = 0,
                    OnlineWaitTimeoutSeconds = 1,
                    OnlineGateFailureAction = OnlineGateFailureAction.RunPhase
                }
            ]
        };
        var devices = DeviceAddressAllocator.Allocate(options);
        var metrics = new LoadTestMetrics();
        using var runner = new GatherCommandWorkloadRunner(
            options,
            devices,
            metrics,
            new DisconnectAfterFirstEligibilityRegistry());

        await runner.RunAsync(waitForOnline: true, CancellationToken.None);

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(0, snapshot.ApiRequestsStarted);
        Assert.Equal(1, snapshot.ApiRequestsSkippedOffline);
        var phase = Assert.Single(runner.Results);
        Assert.Equal(1, phase.ApiTargetsOnline);
        Assert.Equal(1, phase.ApiRequestsSkippedOffline);
    }

    /// <summary>
    /// 验证 combined 角色等待在线超时后把阶段记录为跳过，而不是抛出异常导致整轮 TCP 测试没有最终报告。
    /// </summary>
    [Fact]
    public async Task OnlineGateTimeout_SkipsPhaseWithoutThrowing()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Devices.Count = 1;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:1",
            AuthorizationHeaderName = string.Empty,
            MeterIdStart = 7_001,
            OnlineStableSeconds = 0,
            OnlineWaitTimeoutSeconds = 1,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "requires-online",
                    TargetDeviceCount = 1,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 1,
                    MaxConcurrency = 1,
                    MinimumOnlineCount = 1,
                    OnlineStableSeconds = 0,
                    OnlineWaitTimeoutSeconds = 1,
                    OnlineGateFailureAction = OnlineGateFailureAction.SkipPhase
                }
            ]
        };
        var devices = DeviceAddressAllocator.Allocate(options);
        var metrics = new LoadTestMetrics();
        using var runner = new GatherCommandWorkloadRunner(options, devices, metrics);

        await runner.RunAsync(waitForOnline: true, CancellationToken.None);

        var result = Assert.Single(runner.Results);
        Assert.Equal(CommandPhaseStatus.Skipped, result.Status);
        Assert.False(result.OnlineGateSatisfied);
        Assert.Equal(1, result.RequiredOnlineCount);
        Assert.Equal(0, result.MaximumObservedOnlineCount);
        Assert.Equal(0, result.ApiRequestsStarted);
        Assert.Contains("实际峰值", result.StatusReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证老 API 的 407 数据预校验拒绝会进入逐请求明细，并从阶段估算入队数中排除。
    /// </summary>
    [Fact]
    public async Task Business407_IsClassifiedAsPrevalidationAndWrittenToPhaseResult()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = TestOptionsFactory.Create();
            options.Role = LoadTestRole.ApiOnly;
            options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            options.Devices.Count = 1;
            options.CommandWorkload = new CommandWorkloadOptions
            {
                Enabled = true,
                BaseUrl = $"http://127.0.0.1:{port}",
                AuthorizationHeaderName = string.Empty,
                MeterIdStart = 7_001,
                Phases =
                [
                    new CommandPhaseOptions
                    {
                        Name = "prevalidation",
                        TargetDeviceCount = 1,
                        RequestsPerSecond = 100,
                        MaxConcurrency = 1
                    }
                ]
            };
            var devices = DeviceAddressAllocator.Allocate(options);
            var metrics = new LoadTestMetrics();
            var traceRegistry = new GatherCommandTraceRegistry(new DownstreamDiagnosticsOptions());
            var serverTask = ServeOneResponseAsync(
                listener,
                "{\"success\":false,\"code\":407,\"msg\":\"设备已停用\"}");
            using var runner = new GatherCommandWorkloadRunner(
                options,
                devices,
                metrics,
                commandTraceRegistry: traceRegistry);

            await runner.RunAsync(waitForOnline: false, CancellationToken.None);
            await serverTask;

            var phase = Assert.Single(runner.Results);
            Assert.Equal(1, phase.ApiRequestsStarted);
            Assert.Equal(1, phase.ApiPrevalidationRejected);
            Assert.Equal(0, phase.ApiEstimatedEnqueued);
            Assert.Equal(1, phase.Errors["API_BUSINESS_407"]);
            var error = Assert.Single(runner.ErrorDetails);
            Assert.True(error.PrevalidationRejected);
            Assert.Equal(7_001, error.MeterId);
            Assert.Equal("API_BUSINESS_407", error.ErrorCode);
            Assert.Equal("设备已停用", error.ErrorMessage);
            var trace = Assert.Single(runner.CommandTraces);
            Assert.True(trace.PrevalidationRejected);
            Assert.Equal(CommandTraceOutcome.PrevalidationRejected, trace.Outcome);
            Assert.Null(trace.FirstDownstreamAtUtc);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 验证排空阶段会等待设备侧收到和应答指标追平，并把端到端交付率写入独立阶段结果。
    /// </summary>
    [Fact]
    public async Task DrainEnabled_WaitsForDeviceObservationAndRecordsDeliveryRate()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = TestOptionsFactory.Create();
            options.Role = LoadTestRole.ApiOnly;
            options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            options.Devices.Count = 1;
            options.CommandWorkload = new CommandWorkloadOptions
            {
                Enabled = true,
                BaseUrl = $"http://127.0.0.1:{port}",
                AuthorizationHeaderName = string.Empty,
                MeterIdStart = 7_001,
                Phases =
                [
                    new CommandPhaseOptions
                    {
                        Name = "drain",
                        TargetDeviceCount = 1,
                        RequestsPerSecond = 100,
                        MaxConcurrency = 1,
                        DrainEnabled = true,
                        MinimumDeliveryRate = 1,
                        MinimumResponseRate = 1,
                        DrainTimeoutSeconds = 3,
                        QuietSeconds = 0
                    }
                ]
            };
            var devices = DeviceAddressAllocator.Allocate(options);
            var metrics = new LoadTestMetrics();
            var serverTask = ServeOneSuccessAsync(listener);
            using var runner = new GatherCommandWorkloadRunner(options, devices, metrics);

            var runTask = runner.RunAsync(waitForOnline: false, CancellationToken.None);
            await serverTask;
            metrics.GatherReadingCommandReceived();
            metrics.GatherReadingCommandResponded(TimeSpan.Zero);
            await runTask;

            var phase = Assert.Single(runner.Results);
            Assert.True(phase.DrainSatisfied);
            Assert.Equal(1, phase.ApiEstimatedEnqueued);
            Assert.Equal(1, phase.GatherReadingCommandsReceived);
            Assert.Equal(1, phase.GatherReadingCommandsResponded);
            Assert.Equal(0, phase.GatherReadingCommandsUnobserved);
            Assert.Equal(1, phase.GatherReadingDeliveryRate);
            Assert.Equal(1, phase.GatherReadingResponseRate);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 接收一次最小 HTTP/1.1 请求，返回老 BaseDto 成功 JSON 并返回请求体供断言。
    /// </summary>
    /// <param name="listener">回环 TCP HTTP 监听器。</param>
    /// <returns>客户端发送的 UTF-8 JSON 请求体。</returns>
    private static async Task<string> ServeOneSuccessAsync(TcpListener listener)
        => await ServeOneResponseAsync(listener, "{\"success\":true,\"code\":200,\"msg\":\"操作成功\"}");

    /// <summary>
    /// 接收一次最小 HTTP/1.1 请求并返回指定老 BaseDto JSON。
    /// </summary>
    /// <param name="listener">回环 TCP HTTP 监听器。</param>
    /// <param name="responseBody">要返回的 UTF-8 JSON 响应体。</param>
    /// <returns>客户端发送的 UTF-8 JSON 请求体。</returns>
    private static async Task<string> ServeOneResponseAsync(TcpListener listener, string responseBody)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[8_192];
        var count = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            count += await stream.ReadAsync(buffer.AsMemory(count));
            headerEnd = FindHeaderEnd(buffer.AsSpan(0, count));
        }
        var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var contentLengthLine = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(item => item.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var contentLength = int.Parse(contentLengthLine.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var bodyOffset = headerEnd + 4;
        while (count - bodyOffset < contentLength)
            count += await stream.ReadAsync(buffer.AsMemory(count));
        var body = Encoding.UTF8.GetString(buffer, bodyOffset, contentLength);
        var responseBytes = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\nConnection: close\r\n\r\n{responseBody}");
        await stream.WriteAsync(responseBytes);
        return body;
    }

    /// <summary>
    /// 在接收缓冲区中查找 HTTP 头部结束标记。
    /// </summary>
    /// <param name="input">当前已接收字节。</param>
    /// <returns>CRLFCRLF 起始偏移；尚未接收完整时返回 -1。</returns>
    private static int FindHeaderEnd(ReadOnlySpan<byte> input) => input.IndexOf("\r\n\r\n"u8);

    /// <summary>
    /// 在阶段快照和第一次入队检查时报告在线，第二次发送检查时报告离线，用于稳定复现排队期间断线。
    /// </summary>
    private sealed class DisconnectAfterFirstEligibilityRegistry : IDeviceOnlineRegistry
    {
        private int _eligibilityChecks;

        /// <inheritdoc />
        public int Count => 1;

        /// <inheritdoc />
        public void RegisterOnline(int globalOrdinal)
        {
        }

        /// <inheritdoc />
        public void UnregisterOnline(int globalOrdinal)
        {
        }

        /// <inheritdoc />
        public bool IsOnline(int globalOrdinal) =>
            globalOrdinal == 0 && Interlocked.Increment(ref _eligibilityChecks) == 1;

        /// <inheritdoc />
        public IReadOnlyList<int> GetOnlineOrdinalsSnapshot() => [0];
    }
}
