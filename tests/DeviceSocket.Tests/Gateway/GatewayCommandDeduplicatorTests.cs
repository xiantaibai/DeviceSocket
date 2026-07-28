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
        Assert.All(responses, response => Assert.Equal(GatewayDispatchStatus.WrittenToSocket, response.Status));
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
}
