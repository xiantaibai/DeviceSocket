using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证客户端在线数、重连次数和 Gateway 观测值的自动验收规则。
/// </summary>
public sealed class AcceptanceEvaluatorTests
{
    /// <summary>
    /// 验证容量场景可以按服务端认证设备峰值通过，而不依赖客户端瞬时绑定判断。
    /// </summary>
    [Fact]
    public void GatewayCapacityThreshold_UsesObservedMaximum()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumObservedAuthenticatedDevices = 490,
            MaximumObservedAuthenticatedDevices = 500,
            MinimumReconnects = 1
        };
        var metrics = new MetricSnapshot { ProtocolOnline = 700, ReconnectsScheduled = 20 };
        var gateway = new GatewayObservationSummary(10, 0, 10, 10, 510, 510, 500, 500, null);

        var result = AcceptanceEvaluator.Evaluate(metrics, options, 1000, gateway);

        Assert.True(result.Passed);
        Assert.Equal(4, result.Checks.Count);
    }

    /// <summary>
    /// 验证只设置容量上限时，启动前状态 API 不可用也不能被误判为零设备并通过。
    /// </summary>
    [Fact]
    public void MissingGatewayBaseline_FailsMaximumOnlyRule()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MaximumObservedAuthenticatedDevices = 500
        };
        var metrics = new MetricSnapshot();
        var gateway = new GatewayObservationSummary(1, 1, null, null, 0, 0, 0, 0, null);

        var result = AcceptanceEvaluator.Evaluate(metrics, options, 0, gateway);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "gatewayObservationAvailable" && !item.Passed);
    }

    /// <summary>
    /// 验证 apiOnly 角色忽略默认 TCP 在线门槛，并按 API 成功率和 P95 延迟验收。
    /// </summary>
    [Fact]
    public void ApiOnly_UsesOnlyApiThresholds()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = 0.99,
            MaximumProtocolErrorRate = 0,
            MinimumApiSuccessRate = 0.95,
            MaximumApiP95Milliseconds = 1_000
        };
        var histogram = new MergeableHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(500));
        var metrics = new MetricSnapshot
        {
            ApiRequestsStarted = 100,
            ApiRequestsSucceeded = 98,
            ApiRequestsFailed = 2,
            ApiRequestLatency = histogram.Snapshot()
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Checks.Count);
    }

    /// <summary>
    /// 验证 API 目标覆盖率把离线跳过请求计入分母，避免少量已发送请求全部成功时误判整轮通过。
    /// </summary>
    [Fact]
    public void ApiTargetCoverageRate_IncludesOfflineSkippedRequests()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumApiSuccessRate = 0.95,
            MinimumApiTargetCoverageRate = 0.95
        };
        var metrics = new MetricSnapshot
        {
            ApiRequestsStarted = 90,
            ApiRequestsSucceeded = 90,
            ApiRequestsSkippedOffline = 10
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly);

        Assert.False(result.Passed);
        Assert.Contains(
            result.Checks,
            item => item.Name == "apiSuccessRate" && item.Passed && item.Actual == 1);
        Assert.Contains(
            result.Checks,
            item => item.Name == "apiTargetCoverageRate" && !item.Passed && item.Actual == 0.9);
    }

    /// <summary>
    /// 验证多分片节点使用全局在线数和重连数门槛时会换算本地配额，避免每台机器都被要求完成全局数量。
    /// </summary>
    [Fact]
    public void GlobalCountThresholds_AreDistributedForNodeAcceptance()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumFinalOnlineCount = 9_800,
            MinimumReconnects = 9_800
        };
        var metrics = new MetricSnapshot
        {
            ProtocolOnline = 2_450,
            ReconnectsScheduled = 2_450
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 2_500,
            shardIndex: 3,
            shardCount: 4);

        Assert.True(result.Passed);
        Assert.Contains(
            result.Checks,
            item => item.Name == "finalOnlineCount" && item.Threshold == 2_450);
        Assert.Contains(
            result.Checks,
            item => item.Name == "reconnects" && item.Threshold == 2_450);
    }
}
