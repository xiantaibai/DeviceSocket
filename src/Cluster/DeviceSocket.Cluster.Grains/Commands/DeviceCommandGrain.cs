using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace DeviceSocket.Cluster.Grains.Commands;

/// <summary>
/// 按 MsgId 管理单条可靠消息，负责持久化、幂等发送、重试、到期和设备确认。
/// </summary>
public sealed class DeviceCommandGrain(
    [PersistentState("command", "CommandStore")] IPersistentState<DeviceCommandGrainState> state,
    ILogger<DeviceCommandGrain> logger)
    : Grain, IDeviceCommandGrain, IRemindable
{
    private const string RetryReminderName = "ReliableMessageRetry";
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialAttemptRecoveryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UncertainRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 幂等创建可靠消息；相同 MsgId 只能对应同一设备、协议、报文和确认要求。
    /// </summary>
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
            Status = ReliableMessageStatus.Pending,
            Message = "可靠消息已持久化，等待首次发送。",
            CreatedAt = registration.CreatedAt,
            UpdatedAt = registration.CreatedAt,
            ExpiresAt = registration.ExpiresAt,
            NextRetryAt = registration.CreatedAt,
            RequireDeviceConfirmation = registration.RequireDeviceConfirmation
        };
        await state.WriteStateAsync();
        // 首次发送由当前 DeviceGrain 立即执行；一分钟提醒只用于进程在记录结果前退出后的恢复，
        // 避免提醒过早回调仍在等待 CommandGrain 的同一个 DeviceGrain 而形成循环等待。
        await ScheduleReminderAsync(registration.CreatedAt.Add(InitialAttemptRecoveryDelay));

        return new ReliableCommandCreateResult
        {
            Created = true,
            ContentMatches = true,
            Snapshot = CopySnapshot(state.State.Snapshot)
        };
    }

    /// <summary>
    /// 开始一次允许执行的发送；结果不确定时复用 AttemptNo，其余重试才递增 AttemptNo。
    /// </summary>
    public async Task<ReliableCommandAttempt?> BeginAttemptAsync()
    {
        if (!state.State.Initialized)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (await ExpireIfNeededAsync(now) || IsTerminal(state.State.Snapshot.Status))
            return null;
        if (state.State.Snapshot.NextRetryAt is { } nextRetryAt && nextRetryAt > now)
            return null;

        return await BeginAttemptCoreAsync(now);
    }

    /// <summary>
    /// 记录 Gateway 发送结果并持久化下一状态；过期或旧尝试结果不会覆盖新状态。
    /// </summary>
    public async Task<ReliableCommandSnapshot> RecordAttemptResultAsync(CommandAttemptResult result)
    {
        if (!state.State.Initialized)
            return new ReliableCommandSnapshot { MsgId = this.GetPrimaryKeyString() };

        var snapshot = state.State.Snapshot;
        if (!string.Equals(result.MsgId, snapshot.MsgId, StringComparison.Ordinal) ||
            result.AttemptNo != snapshot.AttemptNo ||
            IsTerminal(snapshot.Status))
        {
            return CopySnapshot(snapshot);
        }

        await ApplyAttemptResultAsync(result);
        return CopySnapshot(state.State.Snapshot);
    }

    /// <summary>
    /// 记录协议层设备确认；成功确认进入终态，失败确认在到期前进入待重试状态。
    /// </summary>
    public async Task<ReliableCommandSnapshot> ConfirmAsync(DeviceCommandConfirmation confirmation)
    {
        if (!state.State.Initialized)
            return new ReliableCommandSnapshot { MsgId = this.GetPrimaryKeyString() };

        var snapshot = state.State.Snapshot;
        if (!string.Equals(confirmation.MsgId, snapshot.MsgId, StringComparison.Ordinal) ||
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
        else
        {
            snapshot.Status = ReliableMessageStatus.RetryPending;
            snapshot.Message = string.IsNullOrWhiteSpace(confirmation.Message)
                ? "设备返回失败确认，等待重试。"
                : confirmation.Message;
            snapshot.NextRetryAt = confirmedAt.Add(GetRetryDelay(snapshot.AttemptNo));
        }

        snapshot.UpdatedAt = confirmedAt;
        await state.WriteStateAsync();
        await UpdateReminderAsync();
        return CopySnapshot(snapshot);
    }

    /// <summary>
    /// 取消尚未结束的可靠消息；终态消息不会被覆盖。
    /// </summary>
    public async Task<bool> CancelAsync()
    {
        if (!state.State.Initialized || IsTerminal(state.State.Snapshot.Status))
            return false;

        var snapshot = state.State.Snapshot;
        snapshot.Status = ReliableMessageStatus.Cancelled;
        snapshot.Message = "可靠消息已由调用方取消。";
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        snapshot.NextRetryAt = null;
        await state.WriteStateAsync();
        await RemoveReminderAsync();
        return true;
    }

    /// <summary>
    /// 查询当前可靠消息快照；查询时发现已到期会同步持久化 Expired 状态。
    /// </summary>
    public async Task<ReliableCommandSnapshot> GetSnapshotAsync()
    {
        if (!state.State.Initialized)
            return new ReliableCommandSnapshot { MsgId = this.GetPrimaryKeyString() };

        await ExpireIfNeededAsync(DateTimeOffset.UtcNow);
        return CopySnapshot(state.State.Snapshot);
    }

    /// <summary>
    /// Orleans 持久化提醒触发重试；Silo 重启后仍可由可替换的持久化存储恢复调度。
    /// </summary>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RetryReminderName, StringComparison.Ordinal) ||
            !state.State.Initialized)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (await ExpireIfNeededAsync(now) || IsTerminal(state.State.Snapshot.Status))
        {
            await RemoveReminderAsync();
            return;
        }

        if (state.State.Snapshot.NextRetryAt is { } nextRetryAt && nextRetryAt > now)
        {
            await ScheduleReminderAsync(nextRetryAt);
            return;
        }

        var attempt = await BeginAttemptCoreAsync(now);
        if (attempt is null)
            return;

        try
        {
            // 单向调用在消息进入 Orleans 调度队列后立即返回，避免 DeviceGrain 同时调用本 Grain 时形成等待环。
            await GrainFactory
                .GetGrain<IDeviceGrain>(attempt.DeviceId)
                .DispatchReliableAttemptAsync(attempt);
            await ScheduleReminderAsync(now.Add(UncertainRetryDelay));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "可靠消息自动重试发生未处理异常: {MsgId} {AttemptNo}",
                attempt.MsgId,
                attempt.AttemptNo);
            await ApplyAttemptResultAsync(new CommandAttemptResult
            {
                MsgId = attempt.MsgId,
                AttemptNo = attempt.AttemptNo,
                Outcome = CommandAttemptOutcome.DeliveryUncertain,
                Code = "RETRY_DISPATCH_UNCERTAIN",
                Message = "自动重试结果不确定，将保持相同 AttemptNo 再次尝试。",
                OccurredAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task<ReliableCommandAttempt?> BeginAttemptCoreAsync(DateTimeOffset now)
    {
        var snapshot = state.State.Snapshot;
        if (IsTerminal(snapshot.Status) || snapshot.ExpiresAt <= now)
            return null;

        // Dispatching 表示上次调用结果不确定，保持序号可让 Gateway 用 MsgId + AttemptNo 去重。
        if (snapshot.Status != ReliableMessageStatus.Dispatching)
            snapshot.AttemptNo++;

        snapshot.Status = ReliableMessageStatus.Dispatching;
        snapshot.Message = $"正在执行第 {snapshot.AttemptNo} 次发送。";
        snapshot.UpdatedAt = now;
        snapshot.NextRetryAt = now.Add(UncertainRetryDelay);
        await state.WriteStateAsync();

        return new ReliableCommandAttempt
        {
            MsgId = snapshot.MsgId,
            AttemptNo = snapshot.AttemptNo,
            DeviceId = snapshot.DeviceId,
            Protocol = snapshot.Protocol,
            Payload = snapshot.Payload.ToArray(),
            TraceId = snapshot.TraceId
        };
    }

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
        switch (result.Outcome)
        {
            case CommandAttemptOutcome.WrittenToSocket when snapshot.RequireDeviceConfirmation:
                snapshot.Status = ReliableMessageStatus.AwaitingConfirmation;
                snapshot.NextRetryAt = occurredAt.Add(GetRetryDelay(snapshot.AttemptNo));
                break;
            case CommandAttemptOutcome.WrittenToSocket:
                snapshot.Status = ReliableMessageStatus.Completed;
                snapshot.NextRetryAt = null;
                break;
            case CommandAttemptOutcome.RetryableFailure:
                snapshot.Status = ReliableMessageStatus.RetryPending;
                snapshot.NextRetryAt = occurredAt.Add(GetRetryDelay(snapshot.AttemptNo));
                break;
            case CommandAttemptOutcome.DeliveryUncertain:
                // 不确定是否写入时继续保持 Dispatching，下一次会复用同一 AttemptNo。
                snapshot.Status = ReliableMessageStatus.Dispatching;
                snapshot.NextRetryAt = occurredAt.Add(UncertainRetryDelay);
                break;
            default:
                snapshot.Status = ReliableMessageStatus.Failed;
                snapshot.NextRetryAt = null;
                break;
        }

        await state.WriteStateAsync();
        await UpdateReminderAsync();
    }

    private async Task<bool> ExpireIfNeededAsync(DateTimeOffset now)
    {
        if (IsTerminal(state.State.Snapshot.Status) || state.State.Snapshot.ExpiresAt > now)
            return false;

        await MarkExpiredAsync(now);
        return true;
    }

    private async Task MarkExpiredAsync(DateTimeOffset now)
    {
        var snapshot = state.State.Snapshot;
        snapshot.Status = ReliableMessageStatus.Expired;
        snapshot.Message = "可靠消息已超过到期时间，停止自动重试。";
        snapshot.UpdatedAt = now;
        snapshot.NextRetryAt = null;
        await state.WriteStateAsync();
        await RemoveReminderAsync();
    }

    private Task UpdateReminderAsync() =>
        IsTerminal(state.State.Snapshot.Status) || state.State.Snapshot.NextRetryAt is null
            ? RemoveReminderAsync()
            : ScheduleReminderAsync(state.State.Snapshot.NextRetryAt.Value);

    private async Task ScheduleReminderAsync(DateTimeOffset retryAt)
    {
        var dueTime = retryAt - DateTimeOffset.UtcNow;
        if (dueTime < TimeSpan.Zero)
            dueTime = TimeSpan.Zero;

        await this.RegisterOrUpdateReminder(RetryReminderName, dueTime, ReminderPeriod);
    }

    private async Task RemoveReminderAsync()
    {
        var reminder = await this.GetReminder(RetryReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }

    private static TimeSpan GetRetryDelay(int attemptNo) => attemptNo switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(3),
        3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    private static bool ContentMatches(
        ReliableCommandSnapshot snapshot,
        ReliableCommandRegistration registration) =>
        string.Equals(snapshot.DeviceId, registration.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.Protocol, registration.Protocol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.PayloadHash, registration.PayloadHash, StringComparison.OrdinalIgnoreCase) &&
        snapshot.RequireDeviceConfirmation == registration.RequireDeviceConfirmation;

    private static void ValidateRegistration(ReliableCommandRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.PayloadHash);
        ArgumentOutOfRangeException.ThrowIfZero(registration.Payload.Length);
        if (registration.ExpiresAt <= registration.CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(registration), "可靠消息到期时间必须晚于创建时间。");
    }

    private static bool IsTerminal(ReliableMessageStatus status) =>
        status is ReliableMessageStatus.Confirmed or
            ReliableMessageStatus.Completed or
            ReliableMessageStatus.Expired or
            ReliableMessageStatus.Failed or
            ReliableMessageStatus.Cancelled;

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
        RequireDeviceConfirmation = source.RequireDeviceConfirmation
    };
}
