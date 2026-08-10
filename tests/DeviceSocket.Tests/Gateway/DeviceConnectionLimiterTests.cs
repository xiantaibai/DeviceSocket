using System.Collections.Concurrent;
using DeviceSocket.Host.Networking.Common;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证 Gateway 全局连接配额在并发接入和关闭时保持准确。
/// </summary>
public sealed class DeviceConnectionLimiterTests
{
    /// <summary>
    /// 大量 Session 并发抢占时，成功数量不得超过配置上限。
    /// </summary>
    [Fact]
    public void TryAcquire_ShouldNotExceedConfiguredLimitUnderConcurrency()
    {
        const int maximum = 32;
        const int attempts = 1_000;
        var limiter = CreateLimiter(maximum);
        var admittedSessions = new ConcurrentBag<string>();

        Parallel.For(0, attempts, index =>
        {
            var sessionId = $"session-{index}";
            if (limiter.TryAcquire(sessionId, out _, out _))
                admittedSessions.Add(sessionId);
        });

        Assert.Equal(maximum, admittedSessions.Count);
        Assert.Equal(maximum, limiter.ConnectionCount);
        Assert.Equal(attempts - maximum, limiter.RejectedConnections);

        foreach (var sessionId in admittedSessions)
            Assert.True(limiter.Release(sessionId, out _));

        Assert.Equal(0, limiter.ConnectionCount);
    }

    /// <summary>
    /// 被拒绝或重复关闭的 Session 不得错误释放其他连接的配额。
    /// </summary>
    [Fact]
    public void Release_ShouldOnlyReleaseAdmittedSessionOnce()
    {
        var limiter = CreateLimiter(1);

        Assert.True(limiter.TryAcquire("accepted", out _, out _));
        Assert.False(limiter.TryAcquire("rejected", out _, out _));
        Assert.False(limiter.Release("rejected", out var countAfterRejectedClose));
        Assert.Equal(1, countAfterRejectedClose);
        Assert.True(limiter.Release("accepted", out var countAfterAcceptedClose));
        Assert.Equal(0, countAfterAcceptedClose);
        Assert.False(limiter.Release("accepted", out var countAfterDuplicateClose));
        Assert.Equal(0, countAfterDuplicateClose);
    }

    /// <summary>
    /// 未认证容量达到上限时拒绝新连接，已有 Session 完成认证后应立即释放一个未认证名额。
    /// </summary>
    [Fact]
    public void MarkAuthenticated_ShouldReleaseUnauthenticatedCapacity()
    {
        var limiter = new DeviceConnectionLimiter(new DeviceConnectionOptions
        {
            MaxConnections = 10,
            MaxUnauthenticatedConnections = 1,
            MaxConnectionsPerSecond = 0
        });
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("first", "10.0.0.1", now, out _, out _, out _));
        Assert.False(limiter.TryAcquire("second", "10.0.0.2", now, out _, out _, out var rejection));
        Assert.Equal(DeviceConnectionRejectionReason.UnauthenticatedCapacity, rejection);

        Assert.True(limiter.MarkAuthenticated("first"));
        Assert.True(limiter.TryAcquire("second", "10.0.0.2", now, out _, out _, out _));
        Assert.Equal(1, limiter.UnauthenticatedConnectionCount);
    }

    /// <summary>
    /// 单 IP 并发限制和每秒速率限制应返回可区分的拒绝原因，并允许下一秒继续接入。
    /// </summary>
    [Fact]
    public void TryAcquire_ShouldEnforcePerIpAndRateLimits()
    {
        var limiter = new DeviceConnectionLimiter(new DeviceConnectionOptions
        {
            MaxConnections = 10,
            MaxUnauthenticatedConnections = 10,
            MaxConnectionsPerIp = 1,
            MaxConnectionsPerSecond = 2
        });
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000);

        Assert.True(limiter.TryAcquire("first", "10.0.0.1", now, out _, out _, out _));
        Assert.False(limiter.TryAcquire("same-ip", "10.0.0.1", now, out _, out _, out var perIp));
        Assert.Equal(DeviceConnectionRejectionReason.PerIpCapacity, perIp);
        Assert.True(limiter.TryAcquire("second", "10.0.0.2", now, out _, out _, out _));
        Assert.False(limiter.TryAcquire("rate-limited", "10.0.0.3", now, out _, out _, out var rate));
        Assert.Equal(DeviceConnectionRejectionReason.RateLimit, rate);
        Assert.True(limiter.TryAcquire("next-second", "10.0.0.3", now.AddSeconds(1), out _, out _, out _));
    }

    private static DeviceConnectionLimiter CreateLimiter(int maximum) =>
        new(maximum);
}
