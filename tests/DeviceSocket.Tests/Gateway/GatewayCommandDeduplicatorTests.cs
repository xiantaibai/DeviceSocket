using DeviceSocket.Host.Rpc;
using DeviceSocket.Rpc.Contracts.Gateway;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证 Gateway 使用 MsgId 和 AttemptNo 控制可靠消息的重复写入。
/// </summary>
public sealed class GatewayCommandDeduplicatorTests
{
    /// <summary>
    /// 同一尝试的并发调用只能执行一次 Socket 写入工厂。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SameAttempt_ExecutesFactoryOnce()
    {
        var deduplicator = new GatewayCommandDeduplicator();
        var executionCount = 0;

        async Task<SendCommandResponse> SendAsync()
        {
            Interlocked.Increment(ref executionCount);
            await Task.Delay(20);
            return new SendCommandResponse
            {
                MsgId = "msg-1",
                Status = GatewayDispatchStatus.WrittenToSocket
            };
        }

        var responses = await Task.WhenAll(
            deduplicator.ExecuteAsync("msg-1", 1, SendAsync),
            deduplicator.ExecuteAsync("msg-1", 1, SendAsync));

        Assert.Equal(1, executionCount);
        Assert.Contains(responses, response => response.Status == GatewayDispatchStatus.WrittenToSocket);
        Assert.Contains(responses, response => response.Status == GatewayDispatchStatus.Duplicate);
    }

    /// <summary>
    /// 明确失败后的下一次正式重试使用新 AttemptNo，因此必须允许再次执行。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DifferentAttempt_ExecutesFactoryAgain()
    {
        var deduplicator = new GatewayCommandDeduplicator();
        var executionCount = 0;

        Task<SendCommandResponse> SendAsync()
        {
            Interlocked.Increment(ref executionCount);
            return Task.FromResult(new SendCommandResponse
            {
                MsgId = "msg-2",
                Status = GatewayDispatchStatus.SessionNotFound
            });
        }

        await deduplicator.ExecuteAsync("msg-2", 1, SendAsync);
        await deduplicator.ExecuteAsync("msg-2", 2, SendAsync);

        Assert.Equal(2, executionCount);
    }

    /// <summary>
    /// 首次 Socket 写入成功后的顺序 RPC 重放必须返回 Duplicate，调用方可恢复 AwaitingConfirmation 但不能再次写设备。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SequentialReplay_ReturnsDuplicateWithoutFactoryExecution()
    {
        var deduplicator = new GatewayCommandDeduplicator();
        var executionCount = 0;

        Task<SendCommandResponse> SendAsync()
        {
            Interlocked.Increment(ref executionCount);
            return Task.FromResult(new SendCommandResponse
            {
                MsgId = "msg-sequential",
                Status = GatewayDispatchStatus.WrittenToSocket,
                OccurredAt = DateTimeOffset.UtcNow
            });
        }

        var first = await deduplicator.ExecuteAsync("msg-sequential", 1, SendAsync);
        var replay = await deduplicator.ExecuteAsync("msg-sequential", 1, SendAsync);

        Assert.Equal(GatewayDispatchStatus.WrittenToSocket, first.Status);
        Assert.Equal(GatewayDispatchStatus.Duplicate, replay.Status);
        Assert.Equal(first.OccurredAt, replay.OccurredAt);
        Assert.Equal(1, executionCount);
    }

    /// <summary>
    /// 失败结果只合并同时到达的调用，后续同 AttemptNo 必须重新执行工厂并复用协议 PSEQ 完成安全重发。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FailedAttempt_AllowsSequentialRetry()
    {
        var deduplicator = new GatewayCommandDeduplicator();
        var executionCount = 0;

        Task<SendCommandResponse> SendAsync()
        {
            Interlocked.Increment(ref executionCount);
            return Task.FromResult(new SendCommandResponse
            {
                MsgId = "msg-failed",
                Status = GatewayDispatchStatus.SendFailed
            });
        }

        var first = await deduplicator.ExecuteAsync("msg-failed", 1, SendAsync);
        var retry = await deduplicator.ExecuteAsync("msg-failed", 1, SendAsync);

        Assert.Equal(GatewayDispatchStatus.SendFailed, first.Status);
        Assert.Equal(GatewayDispatchStatus.SendFailed, retry.Status);
        Assert.Equal(2, executionCount);
    }
}
