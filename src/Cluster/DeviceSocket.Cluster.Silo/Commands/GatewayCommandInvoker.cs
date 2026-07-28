using System.Collections.Concurrent;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Rpc.Contracts.Gateway;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace DeviceSocket.Cluster.Silo.Commands;

/// <summary>
/// Silo 内部的 Gateway 调用器，按 RPC 地址复用 HTTP/2 Channel。
/// </summary>
public sealed class GatewayCommandInvoker : IGatewayCommandInvoker, IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 通过 MagicOnion 调用目标 Gateway；调用异常交由 DeviceGrain 判断是否需要可靠重试。
    /// </summary>
    public async Task<SendCommandResponse> SendAsync(string rpcEndpoint, SendCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcEndpoint);
        var channel = _channels.GetOrAdd(
            rpcEndpoint,
            static endpoint => GrpcChannel.ForAddress(endpoint));
        var client = MagicOnionClient.Create<IGatewayCommandService>(channel);
        return await client.SendCommandAsync(request);
    }

    /// <summary>
    /// Silo 停止时释放全部复用 Channel。
    /// </summary>
    public void Dispose()
    {
        foreach (var channel in _channels.Values)
            channel.Dispose();

        _channels.Clear();
    }
}
