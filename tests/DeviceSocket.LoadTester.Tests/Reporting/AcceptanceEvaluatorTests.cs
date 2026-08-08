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
}
