using System.Security.Cryptography;
using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Cluster.Contracts.Gateways;
using DeviceSocket.Cluster.Contracts.Scheduling;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Rpc.Contracts.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DeviceSocket.Cluster.Grains.Devices;

/// <summary>
/// 按 DeviceId 串行管理设备路由和唯一可靠命令执行通道，状态中不保存完整命令队列。
/// </summary>
public sealed partial class DeviceGrain(
    [PersistentState("device", "DeviceStore")] IPersistentState<DeviceGrainState> state,
    IGatewayCommandInvoker gatewayInvoker,
    ICommandSchedulingStore schedulingStore,
    IOptions<DeviceCommandSchedulingOptions> schedulingOptions,
    ILogger<DeviceGrain> logger)
    : Grain, IDeviceGrain
{
    private readonly DeviceCommandSchedulingOptions _options = schedulingOptions.Value;

    /// <summary>
    /// 激活时只补齐可索引设备编号和稳定分片，不因普通查询产生数据库写入。
    /// </summary>
    /// <param name="cancellationToken">激活取消信号。</param>
    /// <returns>激活准备任务。</returns>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        EnsureDeviceIdentity();
        return base.OnActivateAsync(cancellationToken);
    }

    /// <summary>
    /// 注册当前连接；只有网关或 Session 发生变化时才递增 SessionEpoch，并在上线后恢复待执行命令。
    /// </summary>
    /// <param name="registration">Gateway 提供的设备路由信息。</param>
    /// <returns>持久化后的当前设备路由。</returns>
    public async Task<DeviceRouteSnapshot> RegisterConnectionAsync(DeviceRouteRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.GatewayNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.GatewayRpcEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.SessionId);
        EnsureDeviceIdentity();

        var previous = state.State.Route;
        var sameSession = previous.IsOnline &&
                          string.Equals(previous.GatewayNodeId, registration.GatewayNodeId, StringComparison.Ordinal) &&
                          string.Equals(previous.SessionId, registration.SessionId, StringComparison.Ordinal);
        var epoch = sameSession ? Math.Max(previous.SessionEpoch, 1) : Math.Max(previous.SessionEpoch + 1, 1);

        state.State.Route = new DeviceRouteSnapshot
        {
            DeviceId = state.State.DeviceId,
            IsOnline = true,
            GatewayNodeId = registration.GatewayNodeId,
            GatewayRpcEndpoint = registration.GatewayRpcEndpoint,
            SessionId = registration.SessionId,
            SessionEpoch = epoch,
            Protocol = registration.Protocol,
            LastSeenAt = registration.ConnectedAt,
            LeaseExpiresAt = registration.LeaseExpiresAt
        };
        state.State.PersistedRouteAt = registration.ConnectedAt;
        await state.WriteStateAsync();
        DeviceCommandRuntimeMetrics.RecordRouteWrite("register");

        if (state.State.HasPendingWork)
            await ProcessWorkAsync();

        return CopyRoute(state.State.Route);
    }

    /// <summary>
    /// 条件刷新在线时间；每次调用都校验 Session，但按配置合并 SQL 写入以抑制百万设备心跳写放大。
    /// </summary>
    /// <param name="gatewayNodeId">上报活动的 Gateway 节点编号。</param>
    /// <param name="sessionId">上报活动的连接会话编号。</param>
    /// <param name="seenAt">Gateway 观察到设备活动的时间。</param>
    /// <param name="leaseExpiresAt">本次活动对应的路由租约到期时间。</param>
    /// <returns>当前 Session 匹配时返回 true。</returns>
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
        if (_options.RoutePersistenceInterval == TimeSpan.Zero ||
            seenAt - state.State.PersistedRouteAt >= _options.RoutePersistenceInterval)
        {
            state.State.PersistedRouteAt = seenAt;
            await state.WriteStateAsync();
            DeviceCommandRuntimeMetrics.RecordRouteWrite("touch");
        }

        return true;
    }

    /// <summary>
    /// 条件标记设备离线，避免旧连接关闭事件误删设备重连后的新路由。
    /// </summary>
    /// <param name="gatewayNodeId">关闭连接的 Gateway 节点编号。</param>
    /// <param name="sessionId">关闭的连接会话编号。</param>
    /// <param name="disconnectedAt">连接关闭时间。</param>
    /// <returns>当前 Session 匹配并成功持久化离线状态时返回 true。</returns>
    public async Task<bool> DisconnectAsync(string gatewayNodeId, string sessionId, DateTimeOffset disconnectedAt)
    {
        var route = state.State.Route;
        if (!Matches(route, gatewayNodeId, sessionId))
            return false;

        route.IsOnline = false;
        route.LastSeenAt = disconnectedAt;
        route.LeaseExpiresAt = disconnectedAt;
        state.State.PersistedRouteAt = disconnectedAt;
        await state.WriteStateAsync();
        DeviceCommandRuntimeMetrics.RecordRouteWrite("disconnect");
        return true;
    }

    /// <summary>
    /// 返回当前路由；租约过期时投影为离线，但保留原路由字段用于诊断。
    /// </summary>
    /// <returns>当前设备路由快照。</returns>
    public Task<DeviceRouteSnapshot> GetRouteAsync()
    {
        EnsureDeviceIdentity();
        var route = CopyRoute(state.State.Route);
        route.DeviceId = state.State.DeviceId;
        route.IsOnline = route.IsOnline && route.LeaseExpiresAt > DateTimeOffset.UtcNow;
        return Task.FromResult(route);
    }

    /// <summary>
    /// 尽力发送非业务控制消息；存在可靠命令占用设备通道时直接拒绝，不创建状态或自动重试。
    /// </summary>
    /// <param name="request">无需可靠状态的完整设备报文。</param>
    /// <returns>本次即时发送结果。</returns>
    public async Task<BestEffortSendResult> SendBestEffortMessageAsync(BestEffortDeviceMessage request)
    {
        if (request is null)
            return RejectedBestEffortResult(string.Empty, "消息不能为空。");
        if (!string.IsNullOrWhiteSpace(state.State.ActiveCommandId))
            return RejectedBestEffortResult(request.MsgId, "设备存在等待回复的可靠命令，尽力消息不能插队。");

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
    /// 受理单条可靠命令；CommandGrain 持久化成功且设备游标确认接管后才允许实际下发。
    /// </summary>
    /// <param name="request">需要设备必答、超时和重试的命令。</param>
    /// <returns>可靠子系统受理结果。</returns>
    public Task<ReliableMessageReceipt> SendReliableMessageAsync(ReliableDeviceMessage request) =>
        AcceptReliableMessageAsync(request, processImmediately: true);

    /// <summary>
    /// 批量受理同一设备的可靠命令；受理阶段分配顺序号，执行阶段仍严格逐条一问一答。
    /// </summary>
    /// <param name="requests">按调用方顺序提供的可靠命令。</param>
    /// <returns>保持输入顺序的逐条受理结果。</returns>
    public async Task<ReliableMessageBatchReceipt> SendReliableMessagesAsync(ReliableDeviceMessage[] requests)
    {
        if (requests is not { Length: > 0 })
            return new ReliableMessageBatchReceipt();
        if (requests.Length > _options.MaxSameDeviceBatchSize)
        {
            var rejected = requests
                .Select(request => RejectedReceipt(request?.MsgId ?? string.Empty, "BATCH_TOO_LARGE", $"单设备批量上限为 {_options.MaxSameDeviceBatchSize} 条。"))
                .ToArray();
            return new ReliableMessageBatchReceipt { TotalCount = requests.Length, Items = rejected };
        }

        var receipts = new ReliableMessageReceipt[requests.Length];
        for (var index = 0; index < requests.Length; index++)
            receipts[index] = await AcceptReliableMessageAsync(requests[index], processImmediately: false);

        if (receipts.Any(receipt => receipt.Accepted))
            await ProcessWorkAsync();

        return new ReliableMessageBatchReceipt
        {
            TotalCount = receipts.Length,
            AcceptedCount = receipts.Count(receipt => receipt.Accepted),
            Items = receipts
        };
    }

    /// <summary>
    /// 接收设备协议回复；DeviceGrain 先校验当前命令和连接代次，再由 CommandGrain 完成状态转换。
    /// </summary>
    /// <param name="reply">Gateway 匹配出的设备回复。</param>
    /// <returns>处理后的命令状态或稳定拒绝码。</returns>
    public async Task<ReliableMessageReceipt> ReceiveCommandReplyAsync(DeviceCommandReply reply)
    {
        if (reply is null || string.IsNullOrWhiteSpace(reply.MsgId))
            return RejectedReceipt(string.Empty, "INVALID_REPLY", "设备回复或 MsgId 不能为空。");
        if (!string.Equals(reply.DeviceId, this.GetPrimaryKeyString(), StringComparison.OrdinalIgnoreCase))
            return RejectedReceipt(reply.MsgId, "DEVICE_ID_MISMATCH", "回复设备与当前 DeviceGrain 不一致。");
        if (!string.Equals(state.State.ActiveCommandId, reply.MsgId, StringComparison.Ordinal))
            return RejectedReceipt(reply.MsgId, "NOT_ACTIVE_COMMAND", "回复不属于当前占用设备通道的命令。");
        if (reply.SessionEpoch != state.State.Route.SessionEpoch)
            return RejectedReceipt(reply.MsgId, "STALE_SESSION_REPLY", "回复来自设备重连前的旧 Session。");

        var snapshot = await GrainFactory.GetGrain<IDeviceCommandGrain>(reply.MsgId).ConfirmAsync(new DeviceCommandConfirmation
        {
            MsgId = reply.MsgId,
            AttemptNo = reply.AttemptNo,
            SessionEpoch = reply.SessionEpoch,
            Success = reply.Success,
            Message = reply.Message,
            ConfirmedAt = reply.ReceivedAt,
            Payload = reply.Payload,
            TraceId = reply.TraceId
        });

        if (IsTerminal(snapshot.Status))
        {
            state.State.ActiveCommandId = null;
            await state.WriteStateAsync();
            await ProcessWorkAsync();
        }
        else
        {
            await PersistNextWakeAsync(snapshot.NextRetryAt ?? snapshot.ExpiresAt, hasPendingWork: true);
        }

        return ToReceipt(snapshot);
    }

    /// <summary>
    /// 通过设备统一入口取消可靠命令，并在取消 Active 命令后立即释放一问一答通道。
    /// </summary>
    /// <param name="msgId">需要取消的全局命令编号。</param>
    /// <returns>取消后的命令回执或设备不匹配拒绝。</returns>
    public async Task<ReliableMessageReceipt> CancelReliableMessageAsync(string msgId)
    {
        if (string.IsNullOrWhiteSpace(msgId))
            return RejectedReceipt(string.Empty, "INVALID_MSG_ID", "取消命令必须提供 MsgId。");

        var command = GrainFactory.GetGrain<IDeviceCommandGrain>(msgId.Trim());
        var before = await command.GetSnapshotAsync();
        if (!string.Equals(before.DeviceId, this.GetPrimaryKeyString(), StringComparison.OrdinalIgnoreCase))
            return RejectedReceipt(msgId, "DEVICE_ID_MISMATCH", "命令不属于当前设备。");

        await command.CancelAsync();
        var snapshot = await command.GetSnapshotAsync();
        if (string.Equals(state.State.ActiveCommandId, msgId, StringComparison.Ordinal) &&
            snapshot.Status == ReliableMessageStatus.Cancelled)
        {
            state.State.ActiveCommandId = null;
            await state.WriteStateAsync();
            await ProcessWorkAsync();
        }

        return ToReceipt(snapshot);
    }

    /// <summary>
    /// 接收分片调度器的版本化唤醒；旧版本或提前到达的唤醒不会推进命令状态。
    /// </summary>
    /// <param name="wakeup">调度器观察到的设备版本和到期时间。</param>
    public async Task WakeScheduledCommandAsync(DeviceCommandWakeup wakeup)
    {
        if (wakeup is null || wakeup.ScheduleVersion != state.State.ScheduleVersion)
            return;
        if (state.State.NextWakeAt is { } nextWakeAt && nextWakeAt > DateTimeOffset.UtcNow)
        {
            await NotifySchedulerAsync(nextWakeAt);
            return;
        }

        await ProcessWorkAsync();
    }

    /// <summary>
    /// 修复 CommandGrain 已持久化而设备游标尚未确认接管的中断窗口。
    /// </summary>
    /// <param name="recovery">待修复命令的索引信息。</param>
    public async Task RecoverPendingCommandAsync(DeviceCommandEnqueueRecovery recovery)
    {
        if (recovery is null || string.IsNullOrWhiteSpace(recovery.MsgId) || recovery.DeviceSequence <= 0)
            return;

        var command = GrainFactory.GetGrain<IDeviceCommandGrain>(recovery.MsgId);
        var snapshot = await command.MarkEnqueuedAsync(recovery.DeviceSequence);
        if (snapshot.Status != ReliableMessageStatus.Queued)
            return;

        EnsureDeviceIdentity();
        state.State.NextDeviceSequence = Math.Max(state.State.NextDeviceSequence, recovery.DeviceSequence);
        var dueAt = string.IsNullOrWhiteSpace(state.State.ActiveCommandId)
            ? Min(state.State.NextWakeAt, recovery.DueAt)
            : state.State.NextWakeAt;
        await PersistNextWakeAsync(dueAt ?? DateTimeOffset.UtcNow, hasPendingWork: true);
    }

    /// <summary>
    /// 兼容旧版后台发送入口；只有当前占用设备通道的命令才允许执行，避免绕过设备级串行约束。
    /// </summary>
    /// <param name="attempt">CommandGrain 已持久化的一次发送尝试。</param>
    public async Task DispatchReliableAttemptAsync(ReliableCommandAttempt attempt)
    {
        if (attempt is null || !string.Equals(state.State.ActiveCommandId, attempt.MsgId, StringComparison.Ordinal))
            return;

        var result = await DispatchCoreAsync(attempt);
        var snapshot = await GrainFactory
            .GetGrain<IDeviceCommandGrain>(attempt.MsgId)
            .RecordAttemptResultAsync(result);
        await ApplyCommandResultAsync(snapshot);
    }

    /// <summary>
    /// 验证、持久化并接管一条可靠命令；可选择在批量受理结束后统一启动设备泵送。
    /// </summary>
    /// <param name="request">可靠命令请求。</param>
    /// <param name="processImmediately">受理成功后是否立即执行设备泵送。</param>
    /// <returns>受理结果。</returns>
    private async Task<ReliableMessageReceipt> AcceptReliableMessageAsync(
        ReliableDeviceMessage request,
        bool processImmediately)
    {
        if (request is null)
            return RejectedReceipt(string.Empty, "INVALID_MESSAGE", "消息不能为空。");
        if (string.IsNullOrWhiteSpace(request.MsgId))
            return RejectedReceipt(string.Empty, "INVALID_MSG_ID", "可靠命令必须提供 MsgId。");

        var msgId = request.MsgId.Trim();
        if (msgId.Length > 128)
            return RejectedReceipt(msgId, "INVALID_MSG_ID", "可靠命令 MsgId 长度不能超过 128。");
        if (string.IsNullOrWhiteSpace(request.Protocol) || request.Payload is not { Length: > 0 })
            return RejectedReceipt(msgId, "INVALID_MESSAGE", "协议和原始报文不能为空。");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.Add(_options.DefaultCommandTtl);
        // 未指定延迟时使用稳定的 UnixEpoch 表示“立即”，避免相同 MsgId 重试因服务端当前时间不同而误判内容冲突。
        var notBefore = request.NotBefore ?? DateTimeOffset.UnixEpoch;
        var responseTimeout = request.ResponseTimeout ?? _options.DefaultResponseTimeout;
        var maxAttempts = request.MaxAttempts == 0 ? _options.DefaultMaxAttempts : request.MaxAttempts;
        if (expiresAt <= now || notBefore >= expiresAt)
            return RejectedReceipt(msgId, "MESSAGE_EXPIRED", "最早发送时间必须早于命令到期时间。", expiresAt);
        if (responseTimeout <= TimeSpan.Zero || maxAttempts <= 0)
            return RejectedReceipt(msgId, "INVALID_RETRY_POLICY", "回复超时和最大尝试次数必须大于零。", expiresAt);

        EnsureDeviceIdentity();
        state.State.NextDeviceSequence++;
        var reservedSequence = state.State.NextDeviceSequence;
        // 先持久化顺序号只会在故障时产生空洞，不会导致两个命令取得相同设备顺序号。
        await state.WriteStateAsync();

        var commandGrain = GrainFactory.GetGrain<IDeviceCommandGrain>(msgId);
        var createResult = await commandGrain.CreateAsync(new ReliableCommandRegistration
        {
            DeviceId = state.State.DeviceId,
            Protocol = NormalizeProtocol(request.Protocol),
            Payload = request.Payload,
            PayloadHash = Convert.ToHexString(SHA256.HashData(request.Payload)),
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? msgId : request.TraceId.Trim(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            NotBefore = notBefore,
            ResponseTimeout = responseTimeout,
            Priority = request.Priority,
            DeviceSequence = reservedSequence,
            MaxAttempts = maxAttempts,
            SchedulerShard = state.State.SchedulerShard,
            ResponseMatcher = request.ResponseMatcher?.Trim() ?? string.Empty,
            RequireDeviceConfirmation = true
        });

        if (!createResult.ContentMatches)
            return RejectedReceipt(msgId, "MSG_ID_CONTENT_CONFLICT", "该 MsgId 已用于另一条命令，不能更换设备、协议、报文或执行策略。", createResult.Snapshot.ExpiresAt);

        if (!createResult.Created)
        {
            if (createResult.Snapshot.Status == ReliableMessageStatus.EnqueuePending)
            {
                await RecoverPendingCommandAsync(new DeviceCommandEnqueueRecovery
                {
                    MsgId = msgId,
                    DeviceSequence = createResult.Snapshot.DeviceSequence,
                    DueAt = createResult.Snapshot.NotBefore
                });
            }

            return ToReceipt(createResult.Snapshot);
        }

        var dueAt = string.IsNullOrWhiteSpace(state.State.ActiveCommandId)
            ? Min(state.State.NextWakeAt, notBefore)
            : state.State.NextWakeAt;
        await PersistNextWakeAsync(dueAt ?? now, hasPendingWork: true);
        createResult.Snapshot = await commandGrain.MarkEnqueuedAsync(reservedSequence);
        DeviceCommandRuntimeMetrics.RecordAcceptedCommand();

        if (processImmediately && notBefore <= DateTimeOffset.UtcNow)
        {
            await ProcessWorkAsync();
            createResult.Snapshot = await commandGrain.GetSnapshotAsync();
        }

        return ToReceipt(createResult.Snapshot);
    }

    /// <summary>
    /// 在单个 Grain 激活内泵送设备命令，任何时刻只允许一个命令占用等待回复通道。
    /// </summary>
    private async Task ProcessWorkAsync()
    {
        for (var processed = 0; processed < _options.DevicePumpLimit; processed++)
        {
            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(state.State.ActiveCommandId))
            {
                var command = GrainFactory.GetGrain<IDeviceCommandGrain>(state.State.ActiveCommandId);
                var snapshot = await command.GetSnapshotAsync();
                if (IsTerminal(snapshot.Status))
                {
                    state.State.ActiveCommandId = null;
                    await state.WriteStateAsync();
                    continue;
                }

                if (snapshot.NextRetryAt is { } future && future > now)
                {
                    await PersistNextWakeAsync(future, hasPendingWork: true);
                    return;
                }

                if (snapshot.Status == ReliableMessageStatus.AwaitingConfirmation)
                    snapshot = await command.HandleDueAsync(snapshot.ActionVersion, now);

                if (IsTerminal(snapshot.Status))
                {
                    state.State.ActiveCommandId = null;
                    await state.WriteStateAsync();
                    continue;
                }
                if (snapshot.NextRetryAt is { } delayed && delayed > now)
                {
                    await PersistNextWakeAsync(delayed, hasPendingWork: true);
                    return;
                }

                var attempt = await command.BeginAttemptAsync();
                if (attempt is null)
                {
                    snapshot = await command.GetSnapshotAsync();
                    await ApplyCommandResultAsync(snapshot);
                    return;
                }

                var result = await DispatchCoreAsync(attempt);
                snapshot = await command.RecordAttemptResultAsync(result);
                if (IsTerminal(snapshot.Status))
                {
                    state.State.ActiveCommandId = null;
                    await state.WriteStateAsync();
                    continue;
                }

                await PersistNextWakeAsync(snapshot.NextRetryAt ?? snapshot.ExpiresAt, hasPendingWork: true);
                return;
            }

            var next = await schedulingStore.FindNextCommandAsync(state.State.DeviceId, now);
            if (next is null)
            {
                await PersistNextWakeAsync(null, hasPendingWork: false);
                return;
            }
            if (next.DueAt > now)
            {
                await PersistNextWakeAsync(next.DueAt, hasPendingWork: true);
                return;
            }

            state.State.ActiveCommandId = next.MsgId;
            await state.WriteStateAsync();
        }

        // 集中终态清理时让出 Grain 调度器，避免单台异常积压设备长期独占执行线程。
        await PersistNextWakeAsync(DateTimeOffset.UtcNow.AddMilliseconds(50), hasPendingWork: true);
    }

    /// <summary>
    /// 根据 CommandGrain 快照释放设备通道或安排下一次唤醒。
    /// </summary>
    /// <param name="snapshot">刚完成状态转换的命令快照。</param>
    private async Task ApplyCommandResultAsync(ReliableCommandSnapshot snapshot)
    {
        if (IsTerminal(snapshot.Status))
        {
            state.State.ActiveCommandId = null;
            await state.WriteStateAsync();
            await ProcessWorkAsync();
            return;
        }

        await PersistNextWakeAsync(snapshot.NextRetryAt ?? snapshot.ExpiresAt, hasPendingWork: true);
    }

    /// <summary>
    /// 通过当前有效 Gateway 路由发送一次报文，并把网络不确定性转换成稳定结果类别。
    /// </summary>
    /// <param name="attempt">已由 CommandGrain 持久化的一次尝试。</param>
    /// <returns>供 CommandGrain 幂等记录的发送结果。</returns>
    private async Task<CommandAttemptResult> DispatchCoreAsync(ReliableCommandAttempt attempt)
    {
        var now = DateTimeOffset.UtcNow;
        var route = state.State.Route;
        if (!route.IsOnline || route.LeaseExpiresAt <= now)
            return AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, "DEVICE_OFFLINE", "设备当前不在线或路由已经过期。", sessionEpoch: route.SessionEpoch);
        if (!string.Equals(route.Protocol, attempt.Protocol, StringComparison.OrdinalIgnoreCase))
            return AttemptResult(attempt, CommandAttemptOutcome.PermanentFailure, "PROTOCOL_MISMATCH", "命令协议与设备当前连接协议不一致。", sessionEpoch: route.SessionEpoch);

        var gatewayNode = await GrainFactory.GetGrain<IGatewayNodeGrain>(route.GatewayNodeId).GetSnapshotAsync();
        if (gatewayNode.LeaseExpiresAt <= now || string.IsNullOrWhiteSpace(gatewayNode.RpcEndpoint))
            return AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, "GATEWAY_UNAVAILABLE", "设备所在网关租约已经失效。", sessionEpoch: route.SessionEpoch);

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
                    AttemptResult(attempt, CommandAttemptOutcome.WrittenToSocket, "WRITTEN_TO_SOCKET", response.Message, response.GatewayNodeId, response.BytesWritten, route.SessionEpoch),
                GatewayDispatchStatus.SessionNotFound or GatewayDispatchStatus.StaleSession or GatewayDispatchStatus.GatewayStopping =>
                    AttemptResult(attempt, CommandAttemptOutcome.RetryableFailure, response.Status.ToString(), response.Message, response.GatewayNodeId, sessionEpoch: route.SessionEpoch),
                GatewayDispatchStatus.SendFailed =>
                    AttemptResult(attempt, CommandAttemptOutcome.DeliveryUncertain, response.Status.ToString(), response.Message, response.GatewayNodeId, sessionEpoch: route.SessionEpoch),
                _ => AttemptResult(attempt, CommandAttemptOutcome.PermanentFailure, response.Status.ToString(), response.Message, response.GatewayNodeId, sessionEpoch: route.SessionEpoch)
            };
        }
        catch (Exception exception)
        {
            LogGatewayCallUncertain(logger, exception, attempt.MsgId, attempt.AttemptNo, attempt.DeviceId);
            return AttemptResult(attempt, CommandAttemptOutcome.DeliveryUncertain, "GATEWAY_CALL_UNCERTAIN", "调用网关结果不确定，将使用相同 AttemptNo 重试。", sessionEpoch: route.SessionEpoch);
        }
    }

    /// <summary>
    /// 持久化设备下一动作并通知固定分片调度器；通知失败由 SQL 恢复扫描兜底。
    /// </summary>
    /// <param name="dueAt">下一动作时间；空值表示设备没有待处理命令。</param>
    /// <param name="hasPendingWork">设备是否仍有当前或等待命令。</param>
    private async Task PersistNextWakeAsync(DateTimeOffset? dueAt, bool hasPendingWork)
    {
        EnsureDeviceIdentity();
        if (state.State.NextWakeAt == dueAt && state.State.HasPendingWork == hasPendingWork)
            return;

        state.State.NextWakeAt = dueAt;
        state.State.HasPendingWork = hasPendingWork;
        state.State.ScheduleVersion++;
        await state.WriteStateAsync();
        if (dueAt is { } next)
            await NotifySchedulerAsync(next);
    }

    /// <summary>
    /// 把已持久化的最新设备版本发送给对应 SchedulerGrain。
    /// </summary>
    /// <param name="dueAt">下一动作时间。</param>
    private async Task NotifySchedulerAsync(DateTimeOffset dueAt)
    {
        try
        {
            await GrainFactory
                .GetGrain<IDeviceCommandSchedulerGrain>(state.State.SchedulerShard)
                .ScheduleAsync(new DeviceCommandSchedule
                {
                    DeviceId = state.State.DeviceId,
                    ScheduleVersion = state.State.ScheduleVersion,
                    DueAt = dueAt
                });
        }
        catch (Exception exception)
        {
            LogSchedulerNotificationFailed(logger, exception, state.State.DeviceId, state.State.ScheduleVersion);
        }
    }

    /// <summary>
    /// 为旧状态补齐设备主键和稳定分片，避免依赖路由是否已经注册。
    /// </summary>
    /// <exception cref="InvalidOperationException">设备编号超过 SQL 索引允许的 256 个字符。</exception>
    private void EnsureDeviceIdentity()
    {
        if (!string.IsNullOrWhiteSpace(state.State.DeviceId))
            return;

        state.State.DeviceId = this.GetPrimaryKeyString();
        if (state.State.DeviceId.Length > 256)
            throw new InvalidOperationException("设备编号长度不能超过 256。");
        state.State.SchedulerShard = DeviceCommandShardResolver.Resolve(state.State.DeviceId, _options.ShardCount);
        state.State.Route.DeviceId = state.State.DeviceId;
    }

    /// <summary>
    /// 创建携带尝试版本和 SessionEpoch 的稳定发送结果。
    /// </summary>
    /// <param name="attempt">产生结果的发送尝试。</param>
    /// <param name="outcome">发送结果类别。</param>
    /// <param name="code">稳定结果代码。</param>
    /// <param name="message">结果说明。</param>
    /// <param name="gatewayNodeId">实际处理节点编号。</param>
    /// <param name="bytesWritten">明确写入的字节数。</param>
    /// <param name="sessionEpoch">发送绑定的连接代次。</param>
    /// <returns>可幂等写回 CommandGrain 的结果。</returns>
    private static CommandAttemptResult AttemptResult(
        ReliableCommandAttempt attempt,
        CommandAttemptOutcome outcome,
        string code,
        string message,
        string? gatewayNodeId = null,
        int bytesWritten = 0,
        long sessionEpoch = 0) => new()
        {
            MsgId = attempt.MsgId,
            AttemptNo = attempt.AttemptNo,
            Outcome = outcome,
            Code = code,
            Message = message,
            GatewayNodeId = gatewayNodeId,
            BytesWritten = bytesWritten,
            OccurredAt = DateTimeOffset.UtcNow,
            SessionEpoch = sessionEpoch,
            ActionVersion = attempt.ActionVersion
        };

    /// <summary>
    /// 把内部尝试结果投影成不持久化的尽力发送结果。
    /// </summary>
    /// <param name="result">Gateway 发送结果。</param>
    /// <returns>尽力发送 API 结果。</returns>
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

    /// <summary>
    /// 创建参数或设备通道原因导致的尽力发送拒绝结果。
    /// </summary>
    /// <param name="msgId">调用方消息编号。</param>
    /// <param name="message">拒绝原因。</param>
    /// <returns>拒绝结果。</returns>
    private static BestEffortSendResult RejectedBestEffortResult(string msgId, string message) => new()
    {
        MsgId = msgId,
        Status = BestEffortSendStatus.Rejected,
        Message = message
    };

    /// <summary>
    /// 把命令快照投影成统一受理回执。
    /// </summary>
    /// <param name="snapshot">当前命令快照。</param>
    /// <returns>可靠命令回执。</returns>
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

    /// <summary>
    /// 创建未进入可靠命令子系统的稳定拒绝回执。
    /// </summary>
    /// <param name="msgId">调用方命令编号。</param>
    /// <param name="code">稳定拒绝码。</param>
    /// <param name="message">拒绝原因。</param>
    /// <param name="expiresAt">调用方提供或服务端计算的到期时间。</param>
    /// <returns>未受理回执。</returns>
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

    /// <summary>
    /// 判断命令是否已经进入不可逆终态。
    /// </summary>
    /// <param name="status">生命周期状态。</param>
    /// <returns>终态返回 true。</returns>
    private static bool IsTerminal(ReliableMessageStatus status) =>
        status is ReliableMessageStatus.Confirmed or
            ReliableMessageStatus.CompletedWithoutConfirmation or
            ReliableMessageStatus.Expired or
            ReliableMessageStatus.Failed or
            ReliableMessageStatus.Cancelled;

    /// <summary>
    /// 判断活动事件是否仍属于当前在线 Gateway Session。
    /// </summary>
    /// <param name="route">当前持久化路由。</param>
    /// <param name="gatewayNodeId">事件 Gateway 节点。</param>
    /// <param name="sessionId">事件 Session。</param>
    /// <returns>严格匹配且在线时返回 true。</returns>
    private static bool Matches(DeviceRouteSnapshot route, string gatewayNodeId, string sessionId) =>
        route.IsOnline &&
        string.Equals(route.GatewayNodeId, gatewayNodeId, StringComparison.Ordinal) &&
        string.Equals(route.SessionId, sessionId, StringComparison.Ordinal);

    /// <summary>
    /// 把常见协议别名归一化为 Gateway 注册使用的标准名称。
    /// </summary>
    /// <param name="protocol">调用方协议名称。</param>
    /// <returns>标准协议名称或修剪后的原值。</returns>
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

    /// <summary>
    /// 深复制设备路由，防止调用方修改 Grain 内部状态。
    /// </summary>
    /// <param name="source">源路由。</param>
    /// <returns>独立路由快照。</returns>
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

    /// <summary>
    /// 返回两个可空时间中更早的非空时间。
    /// </summary>
    /// <param name="left">已有调度时间。</param>
    /// <param name="right">新命令调度时间。</param>
    /// <returns>更早时间；两者均为空时返回空。</returns>
    private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left <= right ? left : right;
    }

    [LoggerMessage(2001, LogLevel.Warning, "调用网关结果不确定: {MsgId} {AttemptNo} {DeviceId}")]
    private static partial void LogGatewayCallUncertain(
        ILogger logger,
        Exception exception,
        string msgId,
        int attemptNo,
        string deviceId);

    [LoggerMessage(2002, LogLevel.Warning, "设备调度通知发送失败，将由持久化扫描恢复: {DeviceId} {ScheduleVersion}")]
    private static partial void LogSchedulerNotificationFailed(
        ILogger logger,
        Exception exception,
        string deviceId,
        long scheduleVersion);
}
