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

    private static DeviceConnectionLimiter CreateLimiter(int maximum) =>
        new(maximum);
}
