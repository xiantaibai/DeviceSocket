using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;
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
    /// 接收一次最小 HTTP/1.1 请求，返回老 BaseDto 成功 JSON 并返回请求体供断言。
    /// </summary>
    /// <param name="listener">回环 TCP HTTP 监听器。</param>
    /// <returns>客户端发送的 UTF-8 JSON 请求体。</returns>
    private static async Task<string> ServeOneSuccessAsync(TcpListener listener)
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
        var responseBody = "{\"success\":true,\"code\":200,\"msg\":\"操作成功\"}";
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
}
