using System.Collections.Concurrent;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Rpc.Contracts.Gateway;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 记录所有下发请求并返回确定写入结果的 Gateway 测试替身。
/// </summary>
public sealed class TestGatewayCommandInvoker : IGatewayCommandInvoker
{
    private static readonly ConcurrentQueue<SendCommandRequest> Requests = new();

    /// <summary>
    /// 记录完整请求并返回 WrittenToSocket，实际设备确认仍需测试显式调用回复入口。
    /// </summary>
    /// <param name="rpcEndpoint">DeviceGrain 选择的 Gateway RPC 地址。</param>
    /// <param name="request">待下发命令。</param>
    /// <returns>明确已写入 Socket 的模拟结果。</returns>
    public Task<SendCommandResponse> SendAsync(string rpcEndpoint, SendCommandRequest request)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new SendCommandResponse
        {
            MsgId = request.MsgId,
            Status = GatewayDispatchStatus.WrittenToSocket,
            Message = "测试网关已写入 Socket。",
            BytesWritten = request.Payload.Length,
            GatewayNodeId = "gateway-test"
        });
    }

    /// <summary>
    /// 返回指定设备和 MsgId 已记录的发送请求，按发生顺序排列。
    /// </summary>
    /// <param name="deviceId">设备编号。</param>
    /// <param name="msgId">命令编号。</param>
    /// <returns>匹配请求数组。</returns>
    public SendCommandRequest[] Find(string deviceId, string msgId) => Requests
        .Where(request =>
            string.Equals(request.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.MsgId, msgId, StringComparison.Ordinal))
        .ToArray();

    /// <summary>
    /// 返回指定设备的全部发送请求，按发生顺序排列。
    /// </summary>
    /// <param name="deviceId">设备编号。</param>
    /// <returns>设备请求数组。</returns>
    public SendCommandRequest[] FindByDevice(string deviceId) => Requests
        .Where(request => string.Equals(request.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
}
