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
    /// 验证同一 Session 的重复合法报文只刷新活动时间，不会被识别为设备重连。
    /// </summary>
    [Fact]
    public void SameSessionRegister_OnlyTouchesExistingConnection()
    {
        var registry = new MemoryOnlineDeviceRegistry();
        var connectedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var seenAt = DateTimeOffset.UtcNow;
        var connection = new FakeConnection("same-session");

        var first = registry.Register("3761:1234-00001", connection, connectedAt);
        var second = registry.Register("3761:1234-00001", connection, seenAt);

        Assert.True(first.IsNewSession);
        Assert.False(second.IsNewSession);
        Assert.Equal(connectedAt, second.Current.ConnectedAt);
        Assert.Equal(seenAt, second.Current.LastSeenAt);
    }

    /// <summary>
    /// 验证 Orleans 返回的世代号只能写入仍然匹配的 Session，旧登录迟到不会污染新连接。
    /// </summary>
    [Fact]
    public void SessionEpochUpdate_RequiresCurrentSession()
    {
        var registry = new MemoryOnlineDeviceRegistry();
        registry.Register("3761:1234-00001", new FakeConnection("first"), DateTimeOffset.UtcNow);
        registry.Register("3761:1234-00001", new FakeConnection("second"), DateTimeOffset.UtcNow);

        Assert.False(registry.TryUpdateSessionEpoch("3761:1234-00001", "first", 1));
        Assert.True(registry.TryUpdateSessionEpoch("3761:1234-00001", "second", 2));
        Assert.True(registry.TryGetSnapshot("3761:1234-00001", out var snapshot));
        Assert.Equal(2, snapshot!.SessionEpoch);
    }

    /// <summary>
    /// 验证连接删除会返回被删除快照，供网关向 Orleans 发布精确断开事件。
    /// </summary>
    [Fact]
    public void RemoveSession_ReturnsRemovedSnapshot()
    {
        var registry = new MemoryOnlineDeviceRegistry();
        registry.Register("645:123456789012", new FakeConnection("session"), DateTimeOffset.UtcNow);

        var removed = registry.RemoveSession("session", out var snapshot);

        Assert.True(removed);
        Assert.Equal("645:123456789012", snapshot!.DeviceId);
        Assert.Equal("session", snapshot.SessionId);
        Assert.Equal(0, registry.Count);
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
