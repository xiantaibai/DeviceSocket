using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DeviceSocket.Cluster.Grains.Commands;

/// <summary>
/// 按 MsgId 管理单条可靠命令，持久化幂等内容和运行状态，但不创建命令级 Timer 或 Reminder。
/// </summary>
public sealed class DeviceCommandGrain(
    [PersistentState("command", "CommandStore")] IPersistentState<DeviceCommandGrainState> state,
    ICommandSchedulingStore schedulingStore,
    IOptions<DeviceCommandSchedulingOptions> schedulingOptions)
    : Grain, IDeviceCommandGrain
{
    private readonly DeviceCommandSchedulingOptions _options = schedulingOptions.Value;

    /// <summary>
    /// 幂等创建可靠命令；相同 MsgId 只能对应相同设备、协议、报文和执行策略。
    /// </summary>
    /// <param name="registration">需要首次持久化的不可变命令内容。</param>
    /// <returns>是否首次创建、内容是否一致及当前快照。</returns>
    public async Task<ReliableCommandCreateResult> CreateAsync(ReliableCommandRegistration registration)
    {
        ValidateRegistration(registration);

        if (state.State.Initialized)
        {
            return new ReliableCommandCreateResult
            {
                Created = false,
                ContentMatches = ContentMatches(state.State.Snapshot, registration),
                Snapshot = CopySnapshot(state.State.Snapshot)
            };
        }

        state.State.Initialized = true;
        state.State.Snapshot = new ReliableCommandSnapshot
        {
            MsgId = this.GetPrimaryKeyString(),
            DeviceId = registration.DeviceId,
            Protocol = registration.Protocol,
            Payload = registration.Payload.ToArray(),
            PayloadHash = registration.PayloadHash,
            TraceId = registration.TraceId,
            Status = ReliableMessageStatus.EnqueuePending,
            Message = "可靠命令已持久化，等待设备执行游标接管。",
            CreatedAt = registration.CreatedAt,
            UpdatedAt = registration.CreatedAt,
            ExpiresAt = registration.ExpiresAt,
            NotBefore = registration.NotBefore,
            NextRetryAt = registration.NotBefore,
            ResponseTimeout = registration.ResponseTimeout,
            MaxAttempts = registration.MaxAttempts,
            Priority = registration.Priority,
            DeviceSequence = registration.DeviceSequence,
            SchedulerShard = registration.SchedulerShard,
            ResponseMatcher = registration.ResponseMatcher,
            RequireDeviceConfirmation = registration.RequireDeviceConfirmation,
            EnqueueRecoveryAt = registration.CreatedAt.Add(_options.EnqueueRecoveryDelay),
            ActionVersion = 1
        };
        await WriteStateAsync();

        return new ReliableCommandCreateResult
        {
            Created = true,
            ContentMatches = true,
            Snapshot = CopySnapshot(state.State.Snapshot)
        };
    }

    /// <summary>
    /// 幂等确认 DeviceGrain 已接管命令，使其可以参与设备级顺序选择。
    /// </summary>
    /// <param name="deviceSequence">DeviceGrain 分配的设备内顺序号。</param>
    /// <returns>标记后的命令快照。</returns>
    public async Task<ReliableCommandSnapshot> MarkEnqueuedAsync(long deviceSequence)
    {
        if (!state.State.Initialized)
            return EmptySnapshot();

        var snapshot = state.State.Snapshot;
        if (snapshot.DeviceSequence != deviceSequence || IsTerminal(snapshot.Status))
            return CopySnapshot(snapshot);
        if (snapshot.Status == ReliableMessageStatus.EnqueuePending)
        {
            snapshot.Status = ReliableMessageStatus.Queued;
            snapshot.Message = "可靠命令已进入设备等待队列。";
            snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            snapshot.EnqueueRecoveryAt = null;
            snapshot.ActionVersion++;
            await WriteStateAsync();
        }

        return CopySnapshot(snapshot);
    }

    /// <summary>
    /// 在命令到达允许时间后开始一次正式发送；结果不确定时复用 AttemptNo，其余重试递增 AttemptNo。
    /// </summary>
    /// <returns>允许发送时返回报文和版本，否则返回空。</returns>
    public async Task<ReliableCommandAttempt?> BeginAttemptAsync()
    {
        if (!state.State.Initialized)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (await ExpireIfNeededAsync(now) || IsTerminal(state.State.Snapshot.Status))
            return null;

        var snapshot = state.State.Snapshot;
        if (snapshot.Status is ReliableMessageStatus.EnqueuePending or ReliableMessageStatus.AwaitingConfirmation)
            return null;
        if (snapshot.NextRetryAt is { } nextRetryAt && nextRetryAt > now)
            return null;
        if (snapshot.Status != ReliableMessageStatus.Dispatching && snapshot.AttemptNo >= snapshot.MaxAttempts)
        {
            await MarkFailedAsync(now, "已达到最大尝试次数，停止重试。");
            return null;
        }

        // Dispatching 表示上次结果不确定，复用 AttemptNo 可以让 Gateway 继续按 MsgId + AttemptNo 去重。
        if (snapshot.Status != ReliableMessageStatus.Dispatching)
            snapshot.AttemptNo++;

        snapshot.Status = ReliableMessageStatus.Dispatching;
        snapshot.Message = $"正在执行第 {snapshot.AttemptNo} 次发送。";
        snapshot.UpdatedAt = now;
        snapshot.NextRetryAt = now.Add(_options.UncertainRetryDelay);
        snapshot.ReplyDeadline = null;
        snapshot.ActionVersion++;
        await WriteStateAsync();

        return new ReliableCommandAttempt
        {
            MsgId = snapshot.MsgId,
            AttemptNo = snapshot.AttemptNo,
            DeviceId = snapshot.DeviceId,
            Protocol = snapshot.Protocol,
            Payload = snapshot.Payload.ToArray(),
            TraceId = snapshot.TraceId,
            ActionVersion = snapshot.ActionVersion
        };
    }

    /// <summary>
    /// 记录 Gateway 发送结果；旧尝试或旧动作版本不会覆盖当前状态。
    /// </summary>
    /// <param name="result">Gateway 下发结果及所绑定的连接代次。</param>
    /// <returns>写入后的命令快照。</returns>
    public async Task<ReliableCommandSnapshot> RecordAttemptResultAsync(CommandAttemptResult result)
    {
        if (!state.State.Initialized)
            return EmptySnapshot();

        var snapshot = state.State.Snapshot;
        if (!string.Equals(result.MsgId, snapshot.MsgId, StringComparison.Ordinal) ||
            result.AttemptNo != snapshot.AttemptNo ||
            result.ActionVersion != snapshot.ActionVersion ||
            IsTerminal(snapshot.Status))
        {
            return CopySnapshot(snapshot);
        }

        await ApplyAttemptResultAsync(result);
        return CopySnapshot(state.State.Snapshot);
    }

    /// <summary>
    /// 处理到期唤醒；只有动作版本一致时才推进回复超时、整体过期或重试耗尽状态。
    /// </summary>
    /// <param name="actionVersion">调度器观察到的命令动作版本。</param>
    /// <param name="occurredAt">本次唤醒时间。</param>
    /// <returns>处理后的命令快照。</returns>
    public async Task<ReliableCommandSnapshot> HandleDueAsync(long actionVersion, DateTimeOffset occurredAt)
    {
        if (!state.State.Initialized)
            return EmptySnapshot();

        var snapshot = state.State.Snapshot;
        if (snapshot.ActionVersion != actionVersion || IsTerminal(snapshot.Status))
            return CopySnapshot(snapshot);

        var now = occurredAt == default ? DateTimeOffset.UtcNow : occurredAt;
        if (await ExpireIfNeededAsync(now))
            return CopySnapshot(snapshot);
        if (snapshot.NextRetryAt is { } nextRetryAt && nextRetryAt > now)
            return CopySnapshot(snapshot);

        if (snapshot.Status == ReliableMessageStatus.AwaitingConfirmation)
        {
            if (snapshot.AttemptNo >= snapshot.MaxAttempts)
            {
                await MarkFailedAsync(now, "设备回复超时且已达到最大尝试次数。");
            }
            else
            {
                snapshot.Status = ReliableMessageStatus.RetryPending;
                snapshot.Message = "等待设备回复超时，已进入重试等待。";
                snapshot.UpdatedAt = now;
                snapshot.ReplyDeadline = null;
                snapshot.NextRetryAt = now.Add(GetRetryDelay(snapshot.AttemptNo));
                snapshot.ActionVersion++;
                await WriteStateAsync();
            }
        }

        return CopySnapshot(snapshot);
    }

    /// <summary>
    /// 记录设备协议确认；只接受当前等待回复的尝试和连接代次。
    /// </summary>
    /// <param name="confirmation">协议层确认结果。</param>
    /// <returns>确认后的命令快照；迟到回复返回原状态。</returns>
    public async Task<ReliableCommandSnapshot> ConfirmAsync(DeviceCommandConfirmation confirmation)
    {
        if (!state.State.Initialized)
            return EmptySnapshot();

        var snapshot = state.State.Snapshot;
        if (!string.Equals(confirmation.MsgId, snapshot.MsgId, StringComparison.Ordinal) ||
            snapshot.Status != ReliableMessageStatus.AwaitingConfirmation ||
            confirmation.AttemptNo != snapshot.AttemptNo ||
            confirmation.SessionEpoch != snapshot.ActiveSessionEpoch ||
            IsTerminal(snapshot.Status))
        {
            return CopySnapshot(snapshot);
        }

        var confirmedAt = confirmation.ConfirmedAt == default ? DateTimeOffset.UtcNow : confirmation.ConfirmedAt;
        if (confirmation.Success)
        {
            snapshot.Status = ReliableMessageStatus.Confirmed;
            snapshot.Message = string.IsNullOrWhiteSpace(confirmation.Message)
                ? "设备已返回成功确认。"
                : confirmation.Message;
            snapshot.NextRetryAt = null;
        }
        else if (snapshot.AttemptNo >= snapshot.MaxAttempts)
        {
            snapshot.Status = ReliableMessageStatus.Failed;
            snapshot.Message = string.IsNullOrWhiteSpace(confirmation.Message)
                ? "设备返回失败确认且已达到最大尝试次数。"
                : confirmation.Message;
            snapshot.NextRetryAt = null;
        }
        else
        {
            snapshot.Status = ReliableMessageStatus.RetryPending;
            snapshot.Message = string.IsNullOrWhiteSpace(confirmation.Message)
                ? "设备返回失败确认，等待重试。"
                : confirmation.Message;
            snapshot.NextRetryAt = confirmedAt.Add(GetRetryDelay(snapshot.AttemptNo));
        }

        snapshot.UpdatedAt = confirmedAt;
        snapshot.ReplyDeadline = null;
        if (IsTerminal(snapshot.Status))
            snapshot.TerminalAt = confirmedAt;
        snapshot.ActionVersion++;
        await WriteStateAsync();
        return CopySnapshot(snapshot);
    }

    /// <summary>
    /// 取消尚未结束的可靠命令；终态命令保持不变。
    /// </summary>
    /// <returns>本次是否成功进入取消终态。</returns>
    public async Task<bool> CancelAsync()
    {
        if (!state.State.Initialized || IsTerminal(state.State.Snapshot.Status))
            return false;

        var snapshot = state.State.Snapshot;
        snapshot.Status = ReliableMessageStatus.Cancelled;
        snapshot.Message = "可靠命令已由调用方取消。";
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        snapshot.NextRetryAt = null;
        snapshot.ReplyDeadline = null;
        snapshot.TerminalAt = snapshot.UpdatedAt;
        snapshot.ActionVersion++;
        await WriteStateAsync();
        return true;
    }

    /// <summary>
    /// 查询当前可靠命令快照；查询时发现整体过期会先持久化 Expired 状态。
    /// </summary>
    /// <returns>当前命令快照。</returns>
    public async Task<ReliableCommandSnapshot> GetSnapshotAsync()
    {
        if (!state.State.Initialized)
            return EmptySnapshot();

        await ExpireIfNeededAsync(DateTimeOffset.UtcNow);
        return CopySnapshot(state.State.Snapshot);
    }

    /// <summary>
    /// 清空达到热数据保留期限的终态 Payload，并保留哈希、状态、时间和诊断字段。
    /// </summary>
    /// <param name="terminalThrough">允许压缩的最晚终态时间。</param>
    /// <returns>本次首次完成 Payload 压缩时返回 true。</returns>
    public async Task<bool> CompactPayloadAsync(DateTimeOffset terminalThrough)
    {
        if (!state.State.Initialized || !IsTerminal(state.State.Snapshot.Status))
            return false;

        var snapshot = state.State.Snapshot;
        var terminalAt = EnsureTerminalAt(snapshot);
        if (terminalAt > terminalThrough || snapshot.PayloadCompactedAt is not null)
            return false;

        // PayloadHash 足以继续校验同 MsgId 的幂等内容，终态命令无需再保留可能很大的原始报文。
        snapshot.Payload = [];
        snapshot.PayloadCompactedAt = DateTimeOffset.UtcNow;
        await WriteStateAsync(recordStateTransition: false);
        DeviceCommandRuntimeMetrics.RecordPayloadCompacted();
        return true;
    }

    /// <summary>
    /// 物理删除达到总保留期限的终态状态；Grain 串行执行保证清理不会与回复或状态迁移并发覆盖。
    /// </summary>
    /// <param name="terminalThrough">允许删除的最晚终态时间。</param>
    /// <returns>本次物理删除状态时返回 true。</returns>
    public async Task<bool> PurgeAsync(DateTimeOffset terminalThrough)
    {
        if (!state.State.Initialized || !IsTerminal(state.State.Snapshot.Status))
            return false;

        var snapshot = state.State.Snapshot;
        if (EnsureTerminalAt(snapshot) > terminalThrough)
            return false;

        var msgId = snapshot.MsgId;
        await state.ClearStateAsync();
        state.State = new DeviceCommandGrainState();
        await schedulingStore.CommandStateDeletedAsync(msgId);
        DeviceCommandRuntimeMetrics.RecordCommandPurged();
        DeactivateOnIdle();
        return true;
    }

    /// <summary>
    /// 先持久化 CommandGrain 状态，再通知可替换索引实现；SQL 实现已在同一写入中原子维护索引列。
    /// </summary>
    /// <param name="recordStateTransition">是否记录生命周期状态持久化指标；Payload 压缩不属于状态迁移。</param>
    private async Task WriteStateAsync(bool recordStateTransition = true)
    {
        await state.WriteStateAsync();
        await schedulingStore.CommandStateChangedAsync(state.State.Snapshot);
        if (recordStateTransition)
            DeviceCommandRuntimeMetrics.RecordCommandState(state.State.Snapshot.Status);
    }

    /// <summary>
    /// 根据一次 Gateway 结果设置等待回复、重试或终态，并计算下一次调度时间。
    /// </summary>
    /// <param name="result">当前动作版本对应的发送结果。</param>
    private async Task ApplyAttemptResultAsync(CommandAttemptResult result)
    {
        var snapshot = state.State.Snapshot;
        var occurredAt = result.OccurredAt == default ? DateTimeOffset.UtcNow : result.OccurredAt;
        if (snapshot.ExpiresAt <= occurredAt)
        {
            await MarkExpiredAsync(occurredAt);
            return;
        }

        snapshot.Message = result.Message;
        snapshot.UpdatedAt = occurredAt;
        snapshot.ActiveSessionEpoch = result.SessionEpoch;
        switch (result.Outcome)
        {
            case CommandAttemptOutcome.WrittenToSocket when snapshot.RequireDeviceConfirmation:
                snapshot.Status = ReliableMessageStatus.AwaitingConfirmation;
                snapshot.ReplyDeadline = Min(occurredAt.Add(snapshot.ResponseTimeout), snapshot.ExpiresAt);
                snapshot.NextRetryAt = snapshot.ReplyDeadline;
                break;
            case CommandAttemptOutcome.WrittenToSocket:
                snapshot.Status = ReliableMessageStatus.CompletedWithoutConfirmation;
                snapshot.NextRetryAt = null;
                snapshot.ReplyDeadline = null;
                break;
            case CommandAttemptOutcome.RetryableFailure when snapshot.AttemptNo < snapshot.MaxAttempts:
                snapshot.Status = ReliableMessageStatus.RetryPending;
                snapshot.NextRetryAt = Min(occurredAt.Add(GetRetryDelay(snapshot.AttemptNo)), snapshot.ExpiresAt);
                snapshot.ReplyDeadline = null;
                break;
            case CommandAttemptOutcome.DeliveryUncertain:
                snapshot.Status = ReliableMessageStatus.Dispatching;
                snapshot.NextRetryAt = Min(occurredAt.Add(_options.UncertainRetryDelay), snapshot.ExpiresAt);
                snapshot.ReplyDeadline = null;
                break;
            default:
                snapshot.Status = ReliableMessageStatus.Failed;
                snapshot.NextRetryAt = null;
                snapshot.ReplyDeadline = null;
                break;
        }

        if (IsTerminal(snapshot.Status))
            snapshot.TerminalAt = occurredAt;
        snapshot.ActionVersion++;
        await WriteStateAsync();
    }

    /// <summary>
    /// 在命令超过整体有效期时进入 Expired 终态。
    /// </summary>
    /// <param name="now">当前统一时间。</param>
    /// <returns>本次是否执行了过期转换。</returns>
    private async Task<bool> ExpireIfNeededAsync(DateTimeOffset now)
    {
        if (IsTerminal(state.State.Snapshot.Status) || state.State.Snapshot.ExpiresAt > now)
            return false;

        await MarkExpiredAsync(now);
        return true;
    }

    /// <summary>
    /// 持久化整体过期终态并清除下一动作。
    /// </summary>
    /// <param name="now">过期被观察到的时间。</param>
    private async Task MarkExpiredAsync(DateTimeOffset now)
    {
        var snapshot = state.State.Snapshot;
        snapshot.Status = ReliableMessageStatus.Expired;
        snapshot.Message = "可靠命令已超过到期时间，停止自动重试。";
        snapshot.UpdatedAt = now;
        snapshot.NextRetryAt = null;
        snapshot.ReplyDeadline = null;
        snapshot.TerminalAt = now;
        snapshot.ActionVersion++;
        await WriteStateAsync();
    }

    /// <summary>
    /// 持久化不可继续重试的失败终态。
    /// </summary>
    /// <param name="now">失败发生时间。</param>
    /// <param name="message">稳定失败原因。</param>
    private async Task MarkFailedAsync(DateTimeOffset now, string message)
    {
        var snapshot = state.State.Snapshot;
        snapshot.Status = ReliableMessageStatus.Failed;
        snapshot.Message = message;
        snapshot.UpdatedAt = now;
        snapshot.NextRetryAt = null;
        snapshot.ReplyDeadline = null;
        snapshot.TerminalAt = now;
        snapshot.ActionVersion++;
        await WriteStateAsync();
    }

    /// <summary>
    /// 为升级前已经进入终态但尚无 TerminalAt 的状态补齐稳定时间，SQL 迁移使用相同 UpdatedAt 规则。
    /// </summary>
    /// <param name="snapshot">待检查的终态快照。</param>
    /// <returns>可用于清理资格判断的终态时间。</returns>
    private static DateTimeOffset EnsureTerminalAt(ReliableCommandSnapshot snapshot)
    {
        snapshot.TerminalAt ??= snapshot.UpdatedAt == default
            ? DateTimeOffset.UtcNow
            : snapshot.UpdatedAt;
        return snapshot.TerminalAt.Value;
    }

    /// <summary>
    /// 根据正式尝试次数取得退避时间，超过配置数组后持续使用最后一级延迟。
    /// </summary>
    /// <param name="attemptNo">从一开始的正式尝试次数。</param>
    /// <returns>本次失败后的退避时间。</returns>
    private TimeSpan GetRetryDelay(int attemptNo)
    {
        var index = Math.Clamp(attemptNo - 1, 0, _options.RetryDelayMilliseconds.Length - 1);
        return TimeSpan.FromMilliseconds(_options.RetryDelayMilliseconds[index]);
    }

    /// <summary>
    /// 比较幂等创建所涉及的不可变内容，防止相同 MsgId 被复用于不同命令。
    /// </summary>
    /// <param name="snapshot">已持久化命令。</param>
    /// <param name="registration">本次创建请求。</param>
    /// <returns>不可变内容完全一致时返回 true。</returns>
    private static bool ContentMatches(ReliableCommandSnapshot snapshot, ReliableCommandRegistration registration) =>
        string.Equals(snapshot.DeviceId, registration.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.Protocol, registration.Protocol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.PayloadHash, registration.PayloadHash, StringComparison.OrdinalIgnoreCase) &&
        snapshot.RequireDeviceConfirmation == registration.RequireDeviceConfirmation &&
        snapshot.NotBefore == registration.NotBefore &&
        snapshot.ResponseTimeout == registration.ResponseTimeout &&
        snapshot.Priority == registration.Priority &&
        snapshot.MaxAttempts == registration.MaxAttempts &&
        string.Equals(snapshot.ResponseMatcher, registration.ResponseMatcher, StringComparison.Ordinal);

    /// <summary>
    /// 验证首次持久化所需字段及时间边界。
    /// </summary>
    /// <param name="registration">待验证的可靠命令。</param>
    /// <exception cref="ArgumentException">必填字符串或报文为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">时间、序号、超时或尝试次数无效。</exception>
    private static void ValidateRegistration(ReliableCommandRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.PayloadHash);
        ArgumentOutOfRangeException.ThrowIfZero(registration.Payload.Length);
        if (registration.DeviceSequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(registration), "设备命令顺序号必须大于零。");
        if (registration.ExpiresAt <= registration.CreatedAt || registration.NotBefore >= registration.ExpiresAt)
            throw new ArgumentOutOfRangeException(nameof(registration), "发送时间必须早于命令到期时间。");
        if (registration.ResponseTimeout <= TimeSpan.Zero || registration.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(registration), "回复超时和最大尝试次数必须大于零。");
    }

    /// <summary>
    /// 判断命令是否已经进入不可逆的终态。
    /// </summary>
    /// <param name="status">待判断的生命周期状态。</param>
    /// <returns>终态返回 true。</returns>
    private static bool IsTerminal(ReliableMessageStatus status) =>
        status is ReliableMessageStatus.Confirmed or
            ReliableMessageStatus.CompletedWithoutConfirmation or
            ReliableMessageStatus.Expired or
            ReliableMessageStatus.Failed or
            ReliableMessageStatus.Cancelled;

    /// <summary>
    /// 返回两个绝对时间中较早的一个，确保重试和回复等待不超过整体到期时间。
    /// </summary>
    /// <param name="left">第一个时间。</param>
    /// <param name="right">第二个时间。</param>
    /// <returns>较早时间。</returns>
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    /// <summary>
    /// 构造尚未初始化 Grain 的空快照并保留主键，便于调用方诊断。
    /// </summary>
    /// <returns>只包含 MsgId 的快照。</returns>
    private ReliableCommandSnapshot EmptySnapshot() => new() { MsgId = this.GetPrimaryKeyString() };

    /// <summary>
    /// 深复制可变报文字节，避免调用方修改 Grain 内部状态。
    /// </summary>
    /// <param name="source">源快照。</param>
    /// <returns>与源状态等价的独立快照。</returns>
    private static ReliableCommandSnapshot CopySnapshot(ReliableCommandSnapshot source) => new()
    {
        MsgId = source.MsgId,
        DeviceId = source.DeviceId,
        Protocol = source.Protocol,
        Payload = source.Payload.ToArray(),
        PayloadHash = source.PayloadHash,
        TraceId = source.TraceId,
        Status = source.Status,
        AttemptNo = source.AttemptNo,
        Message = source.Message,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ExpiresAt = source.ExpiresAt,
        NextRetryAt = source.NextRetryAt,
        RequireDeviceConfirmation = source.RequireDeviceConfirmation,
        NotBefore = source.NotBefore,
        ReplyDeadline = source.ReplyDeadline,
        ResponseTimeout = source.ResponseTimeout,
        MaxAttempts = source.MaxAttempts,
        Priority = source.Priority,
        DeviceSequence = source.DeviceSequence,
        ActionVersion = source.ActionVersion,
        SchedulerShard = source.SchedulerShard,
        ResponseMatcher = source.ResponseMatcher,
        EnqueueRecoveryAt = source.EnqueueRecoveryAt,
        ActiveSessionEpoch = source.ActiveSessionEpoch,
        TerminalAt = source.TerminalAt,
        PayloadCompactedAt = source.PayloadCompactedAt
    };
}
