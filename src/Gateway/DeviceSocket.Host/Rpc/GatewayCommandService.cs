using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Rpc.Contracts.Gateway;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Rpc;

/// <summary>
/// 实现 Cluster Silo 到指定 Gateway 的 MagicOnion Unary 下发调用。
/// </summary>
public sealed class GatewayCommandService(
    IOnlineDeviceRegistry registry,
    GatewayCommandDeduplicator deduplicator,
    RuntimeMetrics metrics,
    IRecentStore<DeviceErrorEvent> errors,
    IOptions<GatewayNodeOptions> options,
    ILogger<GatewayCommandService> logger)
    : ServiceBase<IGatewayCommandService>, IGatewayCommandService
{
    private readonly GatewayNodeOptions _options = options.Value;

    /// <summary>
    /// 对 MsgId 做进程内幂等后校验路由围栏，并把报文写入当前真实 Socket。
    /// </summary>
    public async UnaryResult<SendCommandResponse> SendCommandAsync(SendCommandRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.MsgId))
            return Response(request?.MsgId ?? string.Empty, GatewayDispatchStatus.InvalidCommand, "MsgId 不能为空。");
        if (request.AttemptNo <= 0)
            return Response(request.MsgId, GatewayDispatchStatus.InvalidCommand, "AttemptNo 必须大于零。");

        return await deduplicator.ExecuteAsync(request.MsgId, request.AttemptNo, () => SendCoreAsync(request));
    }

    private async Task<SendCommandResponse> SendCoreAsync(SendCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) || request.Payload is not { Length: > 0 })
            return Response(request.MsgId, GatewayDispatchStatus.InvalidCommand, "设备编号和原始报文不能为空。");

        if (!registry.TryGetSnapshot(request.DeviceId, out var snapshot) || snapshot is null ||
            !registry.TryGetConnection(request.DeviceId, out var connection) || connection is null)
            return Response(request.MsgId, GatewayDispatchStatus.SessionNotFound, "设备不在当前网关在线。");

        if (!string.Equals(snapshot.SessionId, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(connection.SessionId, request.SessionId, StringComparison.Ordinal) ||
            (snapshot.SessionEpoch > 0 && request.SessionEpoch > 0 && snapshot.SessionEpoch != request.SessionEpoch))
            return Response(request.MsgId, GatewayDispatchStatus.StaleSession, "设备已经重连，请刷新路由后重试。");

        if (!string.Equals(snapshot.Protocol.ToString(), request.Protocol, StringComparison.OrdinalIgnoreCase))
            return Response(request.MsgId, GatewayDispatchStatus.InvalidCommand, "请求协议与设备当前连接协议不一致。");

        try
        {
            using var auditContext = DeviceSendAuditContext.Push(
                request.MsgId,
                request.TraceId,
                "MagicOnionCommand",
                request.AttemptNo);
            await connection.SendAsync(request.Payload, Context.CallContext.CancellationToken);
            metrics.MessageSent();
            return Response(request.MsgId, GatewayDispatchStatus.WrittenToSocket, "报文已经写入设备 Socket。", request.Payload.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !Context.CallContext.CancellationToken.IsCancellationRequested)
        {
            metrics.SendFailed();
            errors.Add(new DeviceErrorEvent(
                DateTimeOffset.UtcNow,
                "MagicOnionSend",
                "DEVICE_SEND_FAILED",
                exception.Message,
                snapshot.Protocol,
                request.DeviceId,
                request.SessionId,
                snapshot.RemoteEndPoint,
                ToTruncatedHex(request.Payload),
                request.TraceId));
            ClusterLogMessages.MagicOnionSendFailed(
                logger,
                request.MsgId,
                request.DeviceId,
                request.SessionId,
                exception);
            return Response(request.MsgId, GatewayDispatchStatus.SendFailed, "写入设备 Socket 失败，请使用同一 MsgId 查询或重试。");
        }
    }

    private SendCommandResponse Response(string msgId, GatewayDispatchStatus status, string message, int bytesWritten = 0) => new()
    {
        MsgId = msgId,
        Status = status,
        Message = message,
        BytesWritten = bytesWritten,
        GatewayNodeId = _options.NodeId
    };

    private static string ToTruncatedHex(byte[] bytes)
    {
        const int maximumBytes = 256;
        var hex = HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, maximumBytes)));
        return bytes.Length > maximumBytes ? $"{hex}..." : hex;
    }
}
