using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Cluster.Contracts.Gateways;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Rpc.Contracts.Gateway;
using Orleans;
using System.Security.Cryptography;

namespace DeviceSocket.CommandCenter.Host.Commands;

/// <summary>
/// 统一完成指令建档、设备路由查询、精确网关调用和状态落库。
/// </summary>
public sealed class CommandDispatchService(
    IClusterClient clusterClient,
    GatewayCommandClient gatewayClient,
    IConfiguration configuration,
    ILogger<CommandDispatchService> logger)
{
    /// <summary>
    /// 向设备下发完整原始报文；相同 MsgId 重试时复用原指令状态。
    /// </summary>
    public async Task<CommandDispatchResult> DispatchAsync(
        string deviceId,
        string protocol,
        byte[] payload,
        string? requestedMsgId,
        string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(payload);

        var now = DateTimeOffset.UtcNow;
        var msgId = string.IsNullOrWhiteSpace(requestedMsgId) ? Guid.NewGuid().ToString("N") : requestedMsgId.Trim();
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var ttlSeconds = Math.Max(configuration.GetValue<int?>("CommandCenter:CommandTtlSeconds") ?? 30, 1);
        var deviceGrain = clusterClient.GetGrain<IDeviceGrain>(deviceId);
        var command = await deviceGrain.RegisterCommandAsync(new DeviceCommandRegistration
        {
            MsgId = msgId,
            TraceId = traceId,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(ttlSeconds),
            Protocol = protocol,
            PayloadHash = payloadHash
        });

        if (!string.Equals(command.Protocol, protocol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.PayloadHash, payloadHash, StringComparison.Ordinal))
            return new CommandDispatchResult(false, msgId, deviceId, CommandDeliveryStatus.Failed, "该 MsgId 已经用于另一条指令，拒绝改变报文内容。");

        if (command.Status is CommandDeliveryStatus.WrittenToSocket or CommandDeliveryStatus.DeviceConfirmed)
            return new CommandDispatchResult(true, msgId, deviceId, command.Status, "该 MsgId 已经成功处理，本次未重复发送。");

        if (command.ExpiresAt <= now)
            return await FailAsync(deviceGrain, msgId, deviceId, "指令已经过期。", now);

        var route = await deviceGrain.GetRouteAsync();
        if (!route.IsOnline)
            return await RetryAsync(deviceGrain, msgId, deviceId, "设备当前不在线。", now);
        if (!string.Equals(route.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
            return await FailAsync(deviceGrain, msgId, deviceId, "请求协议与设备当前连接协议不一致。", now);

        var gatewayNode = await clusterClient.GetGrain<IGatewayNodeGrain>(route.GatewayNodeId).GetSnapshotAsync();
        if (gatewayNode.LeaseExpiresAt <= now || string.IsNullOrWhiteSpace(gatewayNode.RpcEndpoint))
            return await RetryAsync(deviceGrain, msgId, deviceId, "设备所在网关租约已经失效。", now);

        await UpdateAsync(deviceGrain, msgId, CommandDeliveryStatus.Routing, "正在调用目标网关。", now);
        try
        {
            var response = await gatewayClient.SendAsync(gatewayNode.RpcEndpoint, new SendCommandRequest
            {
                MsgId = msgId,
                DeviceId = deviceId,
                SessionId = route.SessionId,
                SessionEpoch = route.SessionEpoch,
                Protocol = protocol,
                Payload = payload,
                TraceId = traceId
            });

            var success = response.Status is GatewayDispatchStatus.WrittenToSocket or GatewayDispatchStatus.Duplicate;
            var status = success ? CommandDeliveryStatus.WrittenToSocket : ToFailureStatus(response.Status);
            await UpdateAsync(deviceGrain, msgId, status, response.Message, DateTimeOffset.UtcNow);
            logger.LogInformation(
                "CommandDispatch MsgId={MsgId} TraceId={TraceId} DeviceId={DeviceId} GatewayNodeId={GatewayNodeId} Status={Status} Bytes={Bytes}",
                msgId,
                traceId,
                deviceId,
                response.GatewayNodeId,
                response.Status,
                response.BytesWritten);
            return new CommandDispatchResult(success, msgId, deviceId, status, response.Message, response.GatewayNodeId, response.BytesWritten);
        }
        catch (Exception exception)
        {
            // RPC 失败时无法判断网关是否已经写入，因此保持同一 MsgId 进入待重试状态。
            logger.LogWarning(exception, "调用网关失败: {MsgId} {DeviceId} {GatewayNodeId}", msgId, deviceId, route.GatewayNodeId);
            return await RetryAsync(deviceGrain, msgId, deviceId, "调用网关失败，请使用同一 MsgId 重试。", DateTimeOffset.UtcNow, route.GatewayNodeId);
        }
    }

    private static CommandDeliveryStatus ToFailureStatus(GatewayDispatchStatus status) => status switch
    {
        GatewayDispatchStatus.SessionNotFound or GatewayDispatchStatus.StaleSession or GatewayDispatchStatus.SendFailed => CommandDeliveryStatus.RetryPending,
        _ => CommandDeliveryStatus.Failed
    };

    private static async Task<CommandDispatchResult> RetryAsync(
        IDeviceGrain grain,
        string msgId,
        string deviceId,
        string message,
        DateTimeOffset updatedAt,
        string? gatewayNodeId = null)
    {
        await UpdateAsync(grain, msgId, CommandDeliveryStatus.RetryPending, message, updatedAt);
        return new CommandDispatchResult(false, msgId, deviceId, CommandDeliveryStatus.RetryPending, message, gatewayNodeId);
    }

    private static async Task<CommandDispatchResult> FailAsync(
        IDeviceGrain grain,
        string msgId,
        string deviceId,
        string message,
        DateTimeOffset updatedAt)
    {
        await UpdateAsync(grain, msgId, CommandDeliveryStatus.Failed, message, updatedAt);
        return new CommandDispatchResult(false, msgId, deviceId, CommandDeliveryStatus.Failed, message);
    }

    private static Task<DeviceCommandSnapshot?> UpdateAsync(
        IDeviceGrain grain,
        string msgId,
        CommandDeliveryStatus status,
        string message,
        DateTimeOffset updatedAt) => grain.UpdateCommandAsync(new DeviceCommandStatusUpdate
        {
            MsgId = msgId,
            Status = status,
            Message = message,
            UpdatedAt = updatedAt
        });
}
