using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.QGdw3761.Commands;
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
    QGdw3761CommandCorrelationRegistry qGdw3761Correlations,
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

        var payload = request.Payload;
        // 路由注册回执写回本地在线表存在极短竞态；SessionId 已匹配时优先使用 Silo 请求携带的正数代次，避免合法设备回复被标记为旧连接。
        var effectiveSessionEpoch = request.SessionEpoch > 0 ? request.SessionEpoch : snapshot.SessionEpoch;
        if (request.RequireDeviceConfirmation && snapshot.Protocol == DeviceProtocol.QGdw3761)
        {
            if (!qGdw3761Correlations.TryPrepare(
                    request,
                    snapshot.SessionId,
                    effectiveSessionEpoch,
                    DateTimeOffset.UtcNow,
                    out var prepared,
                    out var correlationError) || prepared is null)
            {
                return Response(request.MsgId, GatewayDispatchStatus.InvalidCommand, correlationError);
            }

            payload = prepared.Payload;
        }

        try
        {
            using var auditContext = DeviceSendAuditContext.Push(
                request.MsgId,
                request.TraceId,
                "MagicOnionCommand",
                request.AttemptNo);
            await connection.SendAsync(payload, Context.CallContext.CancellationToken);
            var writtenAt = DateTimeOffset.UtcNow;
            if (request.RequireDeviceConfirmation && snapshot.Protocol == DeviceProtocol.QGdw3761)
            {
                qGdw3761Correlations.MarkWritten(
                    snapshot.SessionId,
                    request.MsgId,
                    request.AttemptNo,
                    writtenAt,
                    request.ResponseTimeout);
            }
            metrics.MessageSent();
            return Response(request.MsgId, GatewayDispatchStatus.WrittenToSocket, "报文已经写入设备 Socket。", payload.Length, writtenAt);
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
                ToTruncatedHex(payload),
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

    /// <summary>
    /// 创建携带 Gateway 实际处理时间的稳定 RPC 结果，使 Silo 回复窗口与协议关联表使用相同时间基准。
    /// </summary>
    /// <param name="msgId">逻辑消息编号。</param>
    /// <param name="status">Gateway 确定性处理状态。</param>
    /// <param name="message">结果说明。</param>
    /// <param name="bytesWritten">实际写入字节数。</param>
    /// <param name="occurredAt">处理发生时间；空值使用当前 UTC 时间。</param>
    /// <returns>MagicOnion 下发回执。</returns>
    private SendCommandResponse Response(
        string msgId,
        GatewayDispatchStatus status,
        string message,
        int bytesWritten = 0,
        DateTimeOffset? occurredAt = null) =>
        new()
        {
            MsgId = msgId,
            Status = status,
            Message = message,
            BytesWritten = bytesWritten,
            GatewayNodeId = _options.NodeId,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };

    private static string ToTruncatedHex(byte[] bytes)
    {
        const int maximumBytes = 256;
        var hex = HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, maximumBytes)));
        return bytes.Length > maximumBytes ? $"{hex}..." : hex;
    }
}
