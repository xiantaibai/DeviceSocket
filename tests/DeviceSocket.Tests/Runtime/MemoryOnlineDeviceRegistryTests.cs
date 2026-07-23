using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Infrastructure.Memory.Connections;

namespace DeviceSocket.Tests.Runtime;

/// <summary>
/// 验证内存在线设备注册表在重连替换场景下的并发安全语义。
/// </summary>
public sealed class MemoryOnlineDeviceRegistryTests
{
    /// <summary>
    /// 验证旧 Session 延迟关闭时不会误删同一设备的新连接。
    /// </summary>
    [Fact]
    public void OldSessionClose_DoesNotRemoveReplacementConnection()
    {
        var registry = new MemoryOnlineDeviceRegistry();
        var first = new FakeConnection("first");
        var second = new FakeConnection("second");

        registry.Register("3761:1234-00001", first, DateTimeOffset.UtcNow);
        var registration = registry.Register("3761:1234-00001", second, DateTimeOffset.UtcNow);

        Assert.Same(first, registration.ReplacedConnection);
        Assert.False(registry.RemoveSession("first"));
        Assert.True(registry.TryGetConnection("3761:1234-00001", out var current));
        Assert.Same(second, current);
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// 为注册表测试提供不执行真实网络写入的设备连接。
    /// </summary>
    private sealed class FakeConnection(string sessionId) : IDeviceConnection
    {
        public string SessionId { get; } = sessionId;
        public DeviceProtocol Protocol => DeviceProtocol.QGdw3761;
        public string? RemoteEndPoint => "127.0.0.1:10000";
        /// <summary>
        /// 模拟一次立即成功的异步下发。
        /// </summary>
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
