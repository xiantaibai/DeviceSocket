using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Metrics;

namespace DeviceSocket.Tests.Runtime;

/// <summary>
/// 验证 Gateway 连接峰值、生命周期累计值和心跳指标的原子更新语义。
/// </summary>
public sealed class RuntimeMetricsTests
{
    /// <summary>连接关闭后当前值应下降，但历史峰值和累计打开、关闭数量必须保留。</summary>
    [Fact]
    public void ConnectionLifecycle_ShouldPreservePeakAndTotals()
    {
        var metrics = new RuntimeMetrics();

        metrics.ConnectionOpened(DeviceProtocol.QGdw3761);
        metrics.ConnectionOpened(DeviceProtocol.Dlt6452007);
        metrics.ConnectionClosed(DeviceProtocol.QGdw3761);
        metrics.HeartbeatReceived();
        metrics.HeartbeatConfirmationSent();

        Assert.Equal(1, metrics.TcpConnections);
        Assert.Equal(2, metrics.PeakTcpConnections);
        Assert.Equal(2, metrics.OpenedConnections);
        Assert.Equal(1, metrics.ClosedConnections);
        Assert.Equal(1, metrics.HeartbeatsReceived);
        Assert.Equal(1, metrics.HeartbeatConfirmationsSent);
    }
}
