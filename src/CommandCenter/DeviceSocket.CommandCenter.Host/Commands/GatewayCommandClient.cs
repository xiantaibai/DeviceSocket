using System.Collections.Concurrent;
using DeviceSocket.Rpc.Contracts.Gateway;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace DeviceSocket.CommandCenter.Host.Commands;

/// <summary>
/// 按网关 RPC 地址复用 gRPC Channel，避免每条指令重新建立 HTTP/2 连接。
/// </summary>
public sealed class GatewayCommandClient : IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 调用目标网关的 MagicOnion Unary 服务并返回确定性处理结果。
    /// </summary>
    public async Task<SendCommandResponse> SendAsync(string rpcEndpoint, SendCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcEndpoint);
        var channel = _channels.GetOrAdd(rpcEndpoint, static endpoint => GrpcChannel.ForAddress(endpoint));
        var client = MagicOnionClient.Create<IGatewayCommandService>(channel);
        return await client.SendCommandAsync(request);
    }

    /// <summary>
    /// 应用停止时释放全部复用 Channel。
    /// </summary>
    public void Dispose()
    {
        foreach (var channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }
}
