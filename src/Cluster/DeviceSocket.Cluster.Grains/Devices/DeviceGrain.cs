using System.Security.Cryptography;
using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Cluster.Contracts.Gateways;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Rpc.Contracts.Gateway;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DeviceSocket.Cluster.Grains.Devices;

/// <summary>
/// 串行管理单台设备的当前路由、普通消息和可靠消息实际发送顺序。
/// </summary>
public sealed class DeviceGrain(
    [PersistentState("device", "DeviceStore")] IPersistentState<DeviceGrainState> state,
    IGatewayCommandInvoker gatewayInvoker,
    ILogger<DeviceGrain> logger)
    : Grain, IDeviceGrain
{
    private static readonly TimeSpan DefaultReliableMessageTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 注册当前连接；只有网关或 Session 发生变化时才递增 SessionEpoch。
    /// </summary>
    public async Task<DeviceRouteSnapshot> RegisterConnectionAsync(DeviceRouteRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.GatewayNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.GatewayRpcEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.SessionId);

        var previous = state.State.Route;
        var sameSession = previous.IsOnline &&
                          string.Equals(previous.GatewayNodeId, registration.GatewayNodeId, StringComparison.Ordinal) &&
                          string.Equals(previous.SessionId, registration.SessionId, StringComparison.Ordinal);
        var epoch = sameSession ? Math.Max(previous.SessionEpoch, 1) : Math.Max(previous.SessionEpoch + 1, 1);

        state.State.Route = new DeviceRouteSnapshot
        {
            DeviceId = this.GetPrimaryKeyString(),
            IsOnline = true,
            GatewayNodeId = registration.GatewayNodeId,
            GatewayRpcEndpoint = registration.GatewayRpcEndpoint,
            SessionId = registration.SessionId,
            SessionEpoch = epoch,
            Protocol = registration.Protocol,
            LastSeenAt = registration.ConnectedAt,
            LeaseExpiresAt = registration.LeaseExpiresAt
        };
        await state.WriteStateAsync();
        return CopyRoute(state.State.Route);
    }

    /// <summary>
    /// 条件刷新在线时间；迟到的旧 Session 心跳不能延长新连接或覆盖路由。
    /// </summary>
    public async Task<bool> TouchAsync(
        string gatewayNodeId,
        string sessionId,
        DateTimeOffset seenAt,
        DateTimeOffset leaseExpiresAt)
    {
        var route = state.State.Route;
        if (!Matches(route, gatewayNodeId, sessionId))
            return false;

        route.LastSeenAt = seenAt;
        route.LeaseExpiresAt = leaseExpiresAt;
        await state.WriteStateAsync();
        return true;
    }

    /// <summary>
    /// 条件标记设备离线，避免旧连接关闭事件误删设备重连后的新路由。
    /// </summary>
    public async Task<bool> DisconnectAsync(string gatewayNodeId, string sessionId, DateTimeOffset disconnectedAt)
    {
        var route = state.State.Route;
        if (!Matches(route, gatewayNodeId, sessionId))
            return false;

        route.IsOnline = false;
        route.LastSeenAt = disconnectedAt;
        route.LeaseExpiresAt = disconnectedAt;
        await state.WriteStateAsync();
        return true;
    }

    /// <summary>
    /// 返回当前路由；租约过期时投影为离线，但保留原路由字段用于排查。
    /// </summary>
    public Task<DeviceRouteSnapshot> GetRouteAsync()
    {
        var route = CopyRoute(state.State.Route);
        route.DeviceId = this.GetPrimaryKeyString();
        route.IsOnline = route.IsOnline && route.LeaseExpiresAt > DateTimeOffset.UtcNow;
        return Task.FromResult(route);
    }

    /// <summary>
    /// 尽力发送普通消息；失败结果直接返回，不创建 CommandGrain，也不安排自动重试。
    /// </summary>
    public async Task<BestEffortSendResult> SendBestEffortMessageAsync(BestEffortDeviceMessage request)
    {
        if (request is null)
            return RejectedBestEffortResult(string.Empty, "消息不能为空。");

        var msgId = string.IsNullOrWhiteSpace(request.MsgId) ? Guid.NewGuid().ToString("N") : request.MsgId.Trim();
        if (string.IsNullOrWhiteSpace(request.Protocol) || request.Payload is not { Length: > 0 })
            return RejectedBestEffortResult(msgId, "协议和原始报文不能为空。");

        var result = await DispatchCoreAsync(new ReliableCommandAttempt
        {
            MsgId = msgId,
            AttemptNo = 1,
            DeviceId = this.GetPrimaryKeyString(),
            Protocol = NormalizeProtocol(request.Protocol),
            Payload = request.Payload,
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? msgId : request.TraceId.Trim()
        });
        return ToBestEffortResult(result);
    }

    /// <summary>
    /// 先把重要消息持久化到独立 CommandGrain，再立即尝试发送；未指定时五分钟后到期。
    /// </summary>
    public async Task<ReliableMessageReceipt> SendReliableMessageAsync(ReliableDeviceMessage request)
    {
        if (request is null)
            return RejectedReceipt(string.Empty, "INVALID_MESSAGE", "消息不能为空。");
        if (string.IsNullOrWhiteSpace(request.MsgId))
            return RejectedReceipt(string.Empty, "INVALID_MSG_ID", "可靠消息必须提供 MsgId。");

        var msgId = request.MsgId.Trim();
        if (string.IsNullOrWhiteSpace(request.Protocol) || request.Payload is not { Length: > 0 })
            return RejectedReceipt(msgId, "INVALID_MESSAGE", "协议和原始报文不能为空。");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.Add(DefaultReliableMessageTtl);
        if (expiresAt <= now)
            return RejectedReceipt(msgId, "MESSAGE_EXPIRED", "消息到期时间必须晚于当前时间。", expiresAt);

        var commandGrain = GrainFactory.GetGrain<IDeviceCommandGrain>(msgId);
        var createResult = await commandGrain.CreateAsync(new ReliableCommandRegistration
        {
            DeviceId = this.GetPrimaryKeyString(),
            Protocol = NormalizeProtocol(request.Protocol),
            Payload = request.Payload,
            PayloadHash = Convert.ToHexString(SHA256.HashData(request.Payload)),
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? msgId : request.TraceId.Trim(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            RequireDeviceConfirmation = request.RequireDeviceConfirmation
        });

        if (!createResult.ContentMatches)
            return RejectedReceipt(msgId, "MSG_ID_CONTENT_CONFLICT", "该 MsgId 已用于另一条消息，不能更换设备、协议或报文。", createResult.Snapshot.ExpiresAt);

        if (!IsTerminal(createResult.Snapshot.Status))
        {
            var attempt = await commandGrain.BeginAttemptAsync();
            if (attempt is not null)
            {
                // 首次发送由当前 DeviceGrain 执行，CommandGrain 只记录结果并在之后安排恢复提醒。
                var attemptResult = await DispatchCoreAsync(attempt);
                createResult.Snapshot = await commandGrain.RecordAttemptResultAsync(attemptResult);
            }
        }

        return ToReceipt(createResult.Snapshot);
    }

    /// <summary>
    /// 执行 CommandGrain 安排的一次后台发送，并把结果写回对应 CommandGrain。
    /// </summary>
    public async Task DispatchReliableAttemptAsync(ReliableCommandAttempt attempt)
    {
        CommandAttemptResult result;
        if (!string.Equals(attempt.DeviceId, this.GetPrimaryKeyString(), StringComparison.OrdinalIgnoreCase))
        {
            result = new CommandAttemptResult
            {
                MsgId = attempt.MsgId,
                AttemptNo = attempt.AttemptNo,
                Outcome = CommandAttemptOutcome.PermanentFailure,
                Code = "DEVICE_ID_MISMATCH",
                Message = "消息目标设备与当前 DeviceGrain 不一致。",
                OccurredAt = DateTimeOffset.UtcNow
            };
        }
        else
        {
            result = await DispatchCoreAsync(attempt);
        }

        try
        {
            await GrainFactory
                .GetGrain<IDeviceCommandGrain>(attempt.MsgId)
                .RecordAttemptResultAsync(result);
        }
        catch (Exception exception)
        {
            // 单向后台调用不能把异常返回调度方；Reminder 会在下一次触发时恢复未记录完成的尝试。
            logger.LogWarning(
                exception,
                "可靠消息发送结果写回失败: {MsgId} {AttemptNo}",
                attempt.MsgId,
                attempt.AttemptNo);
        }
    }

    private async Task<CommandAttemptResult> DispatchCoreAsync(ReliableCommandAttempt attempt)
    {
        var now = DateTimeOffset.UtcNow;
        var route = state.State.Route;
        if (!route.IsOnline || route.LeaseExpiresAt <= now)
            return AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, "DEVICE_OFFLINE", "设备当前不在线或路由已经过期。");
        if (!string.Equals(route.Protocol, attempt.Protocol, StringComparison.OrdinalIgnoreCase))
            return AttemptResult(attempt, CommandAttemptOutcome.PermanentFailure, "PROTOCOL_MISMATCH", "消息协议与设备当前连接协议不一致。");

        var gatewayNode = await GrainFactory.GetGrain<IGatewayNodeGrain>(route.GatewayNodeId).GetSnapshotAsync();
        if (gatewayNode.LeaseExpiresAt <= now || string.IsNullOrWhiteSpace(gatewayNode.RpcEndpoint))
            return AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, "GATEWAY_UNAVAILABLE", "设备所在网关租约已经失效。");

        try
        {
            var response = await gatewayInvoker.SendAsync(gatewayNode.RpcEndpoint, new SendCommandRequest
            {
                MsgId = attempt.MsgId,
                AttemptNo = attempt.AttemptNo,
                DeviceId = attempt.DeviceId,
                SessionId = route.SessionId,
                SessionEpoch = route.SessionEpoch,
                Protocol = attempt.Protocol,
                Payload = attempt.Payload,
                TraceId = attempt.TraceId
            });
            return response.Status switch
            {
                GatewayDispatchStatus.WrittenToSocket or GatewayDispatchStatus.Duplicate =>
                    AttemptResult(attempt, CommandAttemptOutcome.WrittenToSocket, "WRITTEN_TO_SOCKET", response.Message, response.GatewayNodeId, response.BytesWritten),
                GatewayDispatchStatus.SessionNotFound or GatewayDispatchStatus.StaleSession or GatewayDispatchStatus.GatewayStopping =>
                    AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, response.Status.ToString(), response.Message, response.GatewayNodeId),
                GatewayDispatchStatus.SendFailed =>
                    AttemptResult(attempt, CommandAttemptOutcome.DeliveryUncertain, response.Status.ToString(), response.Message, response.GatewayNodeId),
                _ => AttemptResult(attempt, CommandAttemptOutcome.PermanentFailure, response.Status.ToString(), response.Message, response.GatewayNodeId)
            };
        }
        catch (Exception exception)
        {
            // 网络异常时无法判断 Gateway 是否已经写入，保持相同 AttemptNo 交给 CommandGrain 重试。
            logger.LogWarning(exception, "调用网关结果不确定: {MsgId} {AttemptNo} {DeviceId}", attempt.MsgId, attempt.AttemptNo, attempt.DeviceId);
            return AttemptResult(attempt, CommandAttemptOutcome.DeliveryUncertain, "GATEWAY_CALL_UNCERTAIN", "调用网关结果不确定，将使用相同 AttemptNo 重试。");
        }
    }

    private static CommandAttemptResult AttemptResult(
        ReliableCommandAttempt attempt,
        CommandAttemptOutcome outcome,
        string code,
        string message,
        string? gatewayNodeId = null,
        int bytesWritten = 0) => new()
        {
            MsgId = attempt.MsgId,
            AttemptNo = attempt.AttemptNo,
            Outcome = outcome,
            Code = code,
            Message = message,
            GatewayNodeId = gatewayNodeId,
            BytesWritten = bytesWritten,
            OccurredAt = DateTimeOffset.UtcNow
        };

    private static BestEffortSendResult ToBestEffortResult(CommandAttemptResult result)
    {
        var status = result.Outcome switch
        {
            CommandAttemptOutcome.WrittenToSocket => BestEffortSendStatus.WrittenToSocket,
            CommandAttemptOutcome.PermanentFailure => BestEffortSendStatus.Rejected,
            CommandAttemptOutcome.DeliveryUncertain => BestEffortSendStatus.DeliveryUncertain,
            _ when result.Code == "DEVICE_OFFLINE" => BestEffortSendStatus.DroppedDeviceOffline,
            _ => BestEffortSendStatus.DroppedGatewayUnavailable
        };
        return new BestEffortSendResult
        {
            Success = status == BestEffortSendStatus.WrittenToSocket,
            MsgId = result.MsgId,
            Status = status,
            Message = result.Message,
            GatewayNodeId = result.GatewayNodeId,
            BytesWritten = result.BytesWritten
        };
    }

    private static BestEffortSendResult RejectedBestEffortResult(string msgId, string message) => new()
    {
        MsgId = msgId,
        Status = BestEffortSendStatus.Rejected,
        Message = message
    };

    private static ReliableMessageReceipt ToReceipt(ReliableCommandSnapshot snapshot) => new()
    {
        Accepted = true,
        Code = snapshot.Status.ToString(),
        MsgId = snapshot.MsgId,
        Status = snapshot.Status,
        Message = snapshot.Message,
        AttemptNo = snapshot.AttemptNo,
        ExpiresAt = snapshot.ExpiresAt
    };

    private static ReliableMessageReceipt RejectedReceipt(
        string msgId,
        string code,
        string message,
        DateTimeOffset expiresAt = default) => new()
        {
            Accepted = false,
            Code = code,
            MsgId = msgId,
            Status = ReliableMessageStatus.Failed,
            Message = message,
            ExpiresAt = expiresAt
        };

    private static bool IsTerminal(ReliableMessageStatus status) =>
        status is ReliableMessageStatus.Confirmed or
            ReliableMessageStatus.Completed or
            ReliableMessageStatus.Expired or
            ReliableMessageStatus.Failed or
            ReliableMessageStatus.Cancelled;

    private static bool Matches(DeviceRouteSnapshot route, string gatewayNodeId, string sessionId) =>
        route.IsOnline &&
        string.Equals(route.GatewayNodeId, gatewayNodeId, StringComparison.Ordinal) &&
        string.Equals(route.SessionId, sessionId, StringComparison.Ordinal);

    private static string NormalizeProtocol(string protocol)
    {
        var value = protocol.Trim();
        if (value.Equals("376.1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Q/GDW376.1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Q/GDW 376.1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("QGdw3761", StringComparison.OrdinalIgnoreCase))
        {
            return "QGdw3761";
        }

        if (value.Equals("645", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("DLT645", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("DL/T645-2007", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("DL/T 645-2007", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Dlt6452007", StringComparison.OrdinalIgnoreCase))
        {
            return "Dlt6452007";
        }

        return value;
    }

    private static DeviceRouteSnapshot CopyRoute(DeviceRouteSnapshot source) => new()
    {
        DeviceId = source.DeviceId,
        IsOnline = source.IsOnline,
        GatewayNodeId = source.GatewayNodeId,
        GatewayRpcEndpoint = source.GatewayRpcEndpoint,
        SessionId = source.SessionId,
        SessionEpoch = source.SessionEpoch,
        Protocol = source.Protocol,
        LastSeenAt = source.LastSeenAt,
        LeaseExpiresAt = source.LeaseExpiresAt
    };
}
