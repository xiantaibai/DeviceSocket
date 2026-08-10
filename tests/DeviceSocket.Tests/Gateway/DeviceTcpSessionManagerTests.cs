using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Infrastructure.Memory.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证设备 TCP Session 的超时清理、非法报文阈值、重复登录关闭和发送背压语义。
/// </summary>
public sealed class DeviceTcpSessionManagerTests
{
    /// <summary>
    /// 未登录连接使用登录超时，已认证连接只按最后合法报文时间计算空闲超时。
    /// </summary>
    [Fact]
    public async Task CloseExpiredSessionsAsync_ShouldDistinguishLoginAndIdleTimeouts()
    {
        var options = CreateOptions();
        var manager = CreateManager(options);
        var connectedAt = DateTimeOffset.UtcNow;
        var unauthenticated = new FakeSession("unauthenticated");
        var authenticated = new FakeSession("authenticated");
        manager.RegisterSession(unauthenticated, DeviceProtocol.QGdw3761, connectedAt);
        manager.RegisterSession(authenticated, DeviceProtocol.QGdw3761, connectedAt);
        Assert.True(manager.TryAuthenticate(authenticated.SessionId, "3761:3301-00001", connectedAt));
        manager.RecordValidMessage(authenticated.SessionId, connectedAt.AddSeconds(20));

        var firstClosed = await manager.CloseExpiredSessionsAsync(connectedAt.AddSeconds(31));

        Assert.Equal(1, firstClosed);
        Assert.Equal(DeviceSessionCloseReason.LoginTimeout, unauthenticated.CloseReason);
        Assert.Null(authenticated.CloseReason);

        var secondClosed = await manager.CloseExpiredSessionsAsync(connectedAt.AddSeconds(81));

        Assert.Equal(1, secondClosed);
        Assert.Equal(DeviceSessionCloseReason.IdleTimeout, authenticated.CloseReason);
        Assert.Equal(1, manager.LoginTimeoutClosures);
        Assert.Equal(1, manager.IdleTimeoutClosures);
        Assert.Equal(2, manager.SessionScanCount);
        Assert.Equal(2, manager.LastSessionScanSessions);
        Assert.Equal(1, manager.LastSessionScanCandidates);
        Assert.Equal(1, manager.LastSessionScanClosed);
        Assert.NotNull(manager.LastSessionScanAt);
        Assert.True(manager.MaximumSessionScanDurationMilliseconds >= manager.LastSessionScanDurationMilliseconds);
    }

    /// <summary>
    /// 登录超时扫描已经生成候选列表后，尚未开始关闭的 Session 若完成认证，不得被旧扫描结果误关。
    /// </summary>
    [Fact]
    public async Task CloseExpiredSessionsAsync_ShouldRecheckLateAuthenticationBeforeClosing()
    {
        var options = CreateOptions();
        options.MaxConcurrentSessionCloses = 1;
        var manager = CreateManager(options);
        var connectedAt = DateTimeOffset.UtcNow;
        var now = connectedAt.AddSeconds(options.LoginTimeoutSeconds + 1);
        var sessions = Enumerable.Range(0, 16)
            .Select(index => new FakeSession($"late-login-{index}") { BlockClose = true })
            .ToArray();
        foreach (var session in sessions)
            manager.RegisterSession(session, DeviceProtocol.QGdw3761, connectedAt);

        var cleanup = manager.CloseExpiredSessionsAsync(now);
        await Task.WhenAny(sessions.Select(session => session.CloseStarted.Task))
            .WaitAsync(TimeSpan.FromSeconds(2));
        var firstClosing = Assert.Single(sessions.Where(session => session.CloseStarted.Task.IsCompleted));
        foreach (var session in sessions.Where(session => !ReferenceEquals(session, firstClosing)))
            Assert.True(manager.TryAuthenticate(session.SessionId, $"3761:3301-{session.SessionId}", now));
        foreach (var session in sessions)
            session.ReleaseClose.TrySetResult();

        var closed = await cleanup;

        Assert.Equal(1, closed);
        Assert.Equal(DeviceSessionCloseReason.LoginTimeout, firstClosing.CloseReason);
        Assert.All(
            sessions.Where(session => !ReferenceEquals(session, firstClosing)),
            session => Assert.Null(session.CloseReason));
    }

    /// <summary>
    /// 空闲扫描已经生成候选列表后，尚未开始关闭的 Session 若收到合法心跳，不得被旧活动时间误关。
    /// </summary>
    [Fact]
    public async Task CloseExpiredSessionsAsync_ShouldRecheckLateHeartbeatBeforeClosing()
    {
        var options = CreateOptions();
        options.MaxConcurrentSessionCloses = 1;
        var manager = CreateManager(options);
        var connectedAt = DateTimeOffset.UtcNow;
        var now = connectedAt.AddSeconds(options.IdleTimeoutSeconds + 1);
        var sessions = Enumerable.Range(0, 16)
            .Select(index => new FakeSession($"late-heartbeat-{index}") { BlockClose = true })
            .ToArray();
        foreach (var session in sessions)
        {
            manager.RegisterSession(session, DeviceProtocol.QGdw3761, connectedAt);
            Assert.True(manager.TryAuthenticate(session.SessionId, $"3761:3301-{session.SessionId}", connectedAt));
        }

        var cleanup = manager.CloseExpiredSessionsAsync(now);
        await Task.WhenAny(sessions.Select(session => session.CloseStarted.Task))
            .WaitAsync(TimeSpan.FromSeconds(2));
        var firstClosing = Assert.Single(sessions.Where(session => session.CloseStarted.Task.IsCompleted));
        foreach (var session in sessions.Where(session => !ReferenceEquals(session, firstClosing)))
            manager.RecordValidMessage(session.SessionId, now);
        foreach (var session in sessions)
            session.ReleaseClose.TrySetResult();

        var closed = await cleanup;

        Assert.Equal(1, closed);
        Assert.Equal(DeviceSessionCloseReason.IdleTimeout, firstClosing.CloseReason);
        Assert.All(
            sessions.Where(session => !ReferenceEquals(session, firstClosing)),
            session => Assert.Null(session.CloseReason));
    }

    /// <summary>
    /// SuperSocket 官方中间件直接关闭空闲 Session 时应补记指标，业务已经发起关闭时不能重复计数。
    /// </summary>
    [Fact]
    public async Task RecordSuperSocketIdleTimeout_ShouldIgnoreApplicationInitiatedClosure()
    {
        var options = CreateOptions();
        var manager = CreateManager(options);
        var frameworkClosed = new FakeSession("framework-idle");
        var applicationClosed = new FakeSession("application-idle");
        manager.RegisterSession(frameworkClosed, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow);
        manager.RegisterSession(applicationClosed, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow);

        manager.RecordSuperSocketIdleTimeout(frameworkClosed.SessionId);
        await manager.CloseSessionAsync(applicationClosed.SessionId, DeviceSessionCloseReason.IdleTimeout);
        manager.RecordSuperSocketIdleTimeout(applicationClosed.SessionId);

        Assert.Equal(2, manager.IdleTimeoutClosures);
    }

    /// <summary>
    /// Session 从管理器移除后必须立即封闭生命周期，迟到的登录不能重新增加认证连接计数。
    /// </summary>
    [Fact]
    public void UnregisterSession_ShouldRejectLateAuthentication()
    {
        var manager = CreateManager(CreateOptions());
        var session = new FakeSession("closed-before-login");
        manager.RegisterSession(session, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow);

        Assert.True(manager.UnregisterSession(session.SessionId));
        Assert.False(manager.TryAuthenticate(session.SessionId, "3761:3301-00001", DateTimeOffset.UtcNow));
        Assert.Equal(0, manager.AuthenticatedSessionCount);
    }

    /// <summary>
    /// 合法帧应清零连续错误计数，只有新的连续非法帧达到阈值才关闭连接。
    /// </summary>
    [Fact]
    public async Task RecordProtocolErrorAsync_ShouldCloseOnlyAfterConsecutiveThreshold()
    {
        var options = CreateOptions();
        options.MaxConsecutiveProtocolErrors = 3;
        var manager = CreateManager(options);
        var session = new FakeSession("protocol-errors");
        manager.RegisterSession(session, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow);

        Assert.False(await manager.RecordProtocolErrorAsync(session.SessionId));
        Assert.False(await manager.RecordProtocolErrorAsync(session.SessionId));
        manager.RecordValidMessage(session.SessionId, DateTimeOffset.UtcNow);
        Assert.False(await manager.RecordProtocolErrorAsync(session.SessionId));
        Assert.False(await manager.RecordProtocolErrorAsync(session.SessionId));
        Assert.True(await manager.RecordProtocolErrorAsync(session.SessionId));

        Assert.Equal(DeviceSessionCloseReason.ProtocolErrorLimit, session.CloseReason);
        Assert.Equal(1, manager.ProtocolErrorClosures);
    }

    /// <summary>
    /// 新 Session 注册同一设备后必须关闭旧 Session，在线注册表最终只保留新连接。
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ShouldCloseReplacedSession()
    {
        var options = CreateOptions();
        var limiter = new DeviceConnectionLimiter(options);
        var manager = CreateManager(options, limiter);
        var first = new FakeSession("first");
        var second = new FakeSession("second");
        AdmitAndRegister(limiter, manager, first);
        AdmitAndRegister(limiter, manager, second);
        var registry = new MemoryOnlineDeviceRegistry();
        var coordinator = new DeviceConnectionCoordinator(
            registry,
            new NullDeviceRoutePublisher(),
            new NullTrafficSink(),
            manager);
        const string deviceId = "3761:3301-00001";

        Assert.NotNull(await coordinator.RegisterAsync(first.SessionId, "10.0.0.1:1000", DeviceProtocol.QGdw3761, deviceId, DateTimeOffset.UtcNow));
        Assert.NotNull(await coordinator.RegisterAsync(second.SessionId, "10.0.0.2:1000", DeviceProtocol.QGdw3761, deviceId, DateTimeOffset.UtcNow));

        Assert.Equal(DeviceSessionCloseReason.DuplicateLogin, first.CloseReason);
        Assert.True(registry.TryGetConnection(deviceId, out var current));
        Assert.Equal(second.SessionId, current!.SessionId);
        Assert.Equal(1, manager.DuplicateLoginClosures);
    }

    /// <summary>
    /// 单 Session 发送队列达到上限时应拒绝新增下发并主动关闭慢连接，不能无限积压等待任务。
    /// </summary>
    [Fact]
    public async Task SendAsync_ShouldCloseSessionWhenBackpressureLimitIsReached()
    {
        var options = CreateOptions();
        options.MaxPendingSendMessagesPerSession = 1;
        var manager = CreateManager(options);
        var session = new FakeSession("slow-send") { BlockSend = true };
        manager.RegisterSession(session, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow);

        var firstSend = manager.SendAsync(session.SessionId, new byte[16]).AsTask();
        await session.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await manager.SendAsync(session.SessionId, new byte[16]));
        session.ReleaseSend.TrySetResult();
        await firstSend;

        Assert.Contains("背压", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceSessionCloseReason.SendBackpressure, session.CloseReason);
        Assert.Equal(1, manager.SendBackpressureClosures);
        Assert.Equal(0, manager.PendingSendMessages);
        Assert.Equal(0, manager.PendingSendBytes);
        Assert.Equal(1, manager.PeakPendingSendMessages);
        Assert.Equal(16, manager.PeakPendingSendBytes);
    }

    private static DeviceConnectionOptions CreateOptions() => new()
    {
        MaxConnections = 100,
        MaxUnauthenticatedConnections = 100,
        MaxConnectionsPerSecond = 0,
        LoginTimeoutSeconds = 30,
        IdleTimeoutSeconds = 60,
        IdleScanIntervalSeconds = 1,
        MaxConcurrentSessionCloses = 4,
        MaxConsecutiveProtocolErrors = 5,
        SendTimeoutSeconds = 5,
        MaxPendingSendMessagesPerSession = 4,
        MaxPendingSendBytesPerSession = 1_024,
        MaxPendingSendBytes = 4_096
    };

    private static DeviceTcpSessionManager CreateManager(
        DeviceConnectionOptions options,
        DeviceConnectionLimiter? limiter = null) => new(
        limiter ?? new DeviceConnectionLimiter(options),
        Options.Create(options),
        NullLogger<DeviceTcpSessionManager>.Instance);

    private static void AdmitAndRegister(
        DeviceConnectionLimiter limiter,
        DeviceTcpSessionManager manager,
        FakeSession session)
    {
        Assert.True(limiter.TryAcquire(session.SessionId, session.RemoteAddress, DateTimeOffset.UtcNow, out _, out _, out _));
        Assert.True(manager.RegisterSession(session, DeviceProtocol.QGdw3761, DateTimeOffset.UtcNow));
    }

    /// <summary>提供可控制写入、关闭阻塞和关闭原因的测试 Session。</summary>
    /// <param name="sessionId">测试 Session 唯一编号。</param>
    private sealed class FakeSession(string sessionId) : IDeviceSessionControl
    {
        public string SessionId { get; } = sessionId;
        public string? RemoteAddress => "10.0.0.1";
        public bool BlockSend { get; init; }
        public bool BlockClose { get; init; }
        public DeviceSessionCloseReason? CloseReason { get; private set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>模拟立即发送或由测试控制释放的慢写入。</summary>
        public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            if (BlockSend)
                await ReleaseSend.Task.WaitAsync(cancellationToken);
        }

        /// <summary>记录首次主动关闭原因，并按测试需要阻塞底层关闭以放大扫描后的并发窗口。</summary>
        public async ValueTask CloseAsync(DeviceSessionCloseReason reason)
        {
            CloseReason ??= reason;
            CloseStarted.TrySetResult();
            if (BlockClose)
                await ReleaseClose.Task;
        }
    }

    /// <summary>在连接协调器测试中丢弃通信日志。</summary>
    private sealed class NullTrafficSink : IDeviceTrafficSink
    {
        /// <summary>测试中忽略通信日志，并报告写入成功。</summary>
        public bool TryWrite(DeviceTrafficRecord record) => true;
    }
}
