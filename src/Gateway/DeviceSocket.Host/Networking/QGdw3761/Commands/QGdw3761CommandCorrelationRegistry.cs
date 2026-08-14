using System.Collections.Concurrent;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Rpc.Contracts.Gateway;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// 在 Gateway 进程内按 TCP Session 保存一条 376.1 活动命令，负责 PSEQ 分配、重试复用和响应窗口判断。
/// </summary>
public sealed class QGdw3761CommandCorrelationRegistry(
    IOptions<QGdw3761CommandCorrelationOptions> options) : IDeviceSessionResourceCleaner
{
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _lastSequences = new(StringComparer.Ordinal);
    private readonly int _maximumPendingCommands = options.Value.MaxPendingCommands;
    private long _nextVersion;

    /// <summary>取得当前节点尚未整体到期的 Session 命令关联数。</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// 校验可靠 376.1 下行帧，为新 MsgId 分配 PSEQ；同一 MsgId 的不同 AttemptNo 复用 PSEQ 并刷新回复窗口。
    /// </summary>
    /// <param name="request">Silo 发送的可靠命令请求。</param>
    /// <param name="sessionId">当前真实 TCP Session 编号。</param>
    /// <param name="sessionEpoch">Orleans 当前连接代次。</param>
    /// <param name="now">开始本次 Socket 写入的 Gateway 时间。</param>
    /// <param name="prepared">成功时返回实际下发报文和 PSEQ。</param>
    /// <param name="error">失败时返回可供 RPC 调用方诊断的稳定说明。</param>
    /// <returns>报文和关联均已准备完成时返回 true。</returns>
    public bool TryPrepare(
        SendCommandRequest request,
        string sessionId,
        long sessionEpoch,
        DateTimeOffset now,
        out QGdw3761PreparedCommand? prepared,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        prepared = null;
        error = string.Empty;

        if (request.ResponseTimeout <= TimeSpan.Zero || request.ExpiresAt <= now)
        {
            error = "ResponseTimeout 必须大于零，且可靠命令必须尚未整体到期。";
            return false;
        }

        var read = QGdw3761Codec.ReadCompleteFrame(request.Payload.ToArray());
        if (read.Status != FrameReadStatus.PackageReady || read.Consumed != request.Payload.Length ||
            read.Package is not { IsValid: true } frame)
        {
            error = "可靠命令 Payload 不是完整且校验正确的 376.1 报文。";
            return false;
        }

        if (frame.IsUpstream || !frame.IsPrimaryMessage || frame.Raw.Length < 18)
        {
            error = "可靠命令必须是包含数据单元标识的 376.1 主站下行启动帧。";
            return false;
        }

        if (!string.Equals(frame.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            error = "可靠命令报文地址与目标 DeviceId 不一致。";
            return false;
        }

        if (_pending.Count >= _maximumPendingCommands && !_pending.ContainsKey(sessionId))
        {
            error = $"376.1 命令关联数已达到节点上限 {_maximumPendingCommands}。";
            return false;
        }

        var replyDeadline = Min(now.Add(request.ResponseTimeout), request.ExpiresAt);
        while (true)
        {
            if (_pending.TryGetValue(sessionId, out var current))
            {
                if (string.Equals(current.MsgId, request.MsgId, StringComparison.Ordinal) &&
                    !MatchesCommandIdentity(current, frame, request.DeviceId))
                {
                    error = "同一 MsgId 的 376.1 AFN、DA 或 DT 已改变，拒绝破坏重试幂等性。";
                    return false;
                }

                if (string.Equals(current.MsgId, request.MsgId, StringComparison.Ordinal) &&
                    request.AttemptNo < current.AttemptNo)
                {
                    error = "收到早于当前关联版本的 AttemptNo。";
                    return false;
                }

                // 同一逻辑命令的正式重试必须复用 PSEQ；新的 MsgId 表示 Orleans 已释放设备通道，可以安全替换旧关联。
                var sequence = string.Equals(current.MsgId, request.MsgId, StringComparison.Ordinal)
                    ? current.Sequence
                    : NextSequence(sessionId);
                var replacement = CreatePending(
                    request,
                    sessionId,
                    sessionEpoch,
                    frame,
                    sequence,
                    replyDeadline);
                if (!_pending.TryUpdate(sessionId, replacement, current))
                    continue;

                prepared = CreatePrepared(frame, sequence, replyDeadline);
                return true;
            }

            var allocatedSequence = NextSequence(sessionId);
            var created = CreatePending(
                request,
                sessionId,
                sessionEpoch,
                frame,
                allocatedSequence,
                replyDeadline);
            if (!_pending.TryAdd(sessionId, created))
                continue;

            prepared = CreatePrepared(frame, allocatedSequence, replyDeadline);
            return true;
        }
    }

    /// <summary>
    /// 尝试把 AFN=00H/F3 终端确认与当前逻辑命令关联，并原子取得一次 Orleans 发布权。
    /// </summary>
    /// <param name="sessionId">接收报文的 TCP Session 编号。</param>
    /// <param name="response">已经通过 376.1 完整性和校验和检查的上行帧。</param>
    /// <param name="receivedAt">报文抵达 Gateway 的时间。</param>
    /// <param name="match">匹配成功时返回 Orleans 回执所需上下文。</param>
    /// <returns>匹配、迟到、重复或错配等分类结果。</returns>
    public QGdw3761CommandReplyMatchStatus TryAcquireReply(
        string sessionId,
        QGdw3761Frame response,
        DateTimeOffset receivedAt,
        out QGdw3761CommandReplyMatch? match)
    {
        ArgumentNullException.ThrowIfNull(response);
        match = null;

        if (!IsPerDataUnitConfirmation(response))
            return QGdw3761CommandReplyMatchStatus.InvalidConfirmation;

        while (true)
        {
            if (!_pending.TryGetValue(sessionId, out var current))
                return QGdw3761CommandReplyMatchStatus.NoPendingCommand;

            if (receivedAt >= current.ExpiresAt)
            {
                TryRemoveExact(sessionId, current);
                return QGdw3761CommandReplyMatchStatus.CommandExpired;
            }

            if (!MatchesReply(current, response))
                return QGdw3761CommandReplyMatchStatus.CorrelationMismatch;

            if (receivedAt >= current.ReplyDeadline)
                return QGdw3761CommandReplyMatchStatus.LateReply;
            if (current.ReplyInFlight)
                return QGdw3761CommandReplyMatchStatus.DuplicateReply;

            var acquired = current with { ReplyInFlight = true };
            if (!_pending.TryUpdate(sessionId, acquired, current))
                continue;

            var errorCode = response.DataUnitPayload[5];
            match = new QGdw3761CommandReplyMatch
            {
                Version = acquired.Version,
                SessionId = acquired.SessionId,
                DeviceId = acquired.DeviceId,
                MsgId = acquired.MsgId,
                AttemptNo = acquired.AttemptNo,
                SessionEpoch = acquired.SessionEpoch,
                TraceId = acquired.TraceId,
                Success = errorCode == 0,
                ErrorCode = errorCode
            };
            return QGdw3761CommandReplyMatchStatus.Matched;
        }
    }

    /// <summary>
    /// Socket 完整写入成功后按实际完成时间校准本次回复截止时间；快速回复已经消费关联时本方法安全无操作。
    /// </summary>
    /// <param name="sessionId">实际写入的 TCP Session 编号。</param>
    /// <param name="msgId">逻辑命令编号。</param>
    /// <param name="attemptNo">本次正式发送次数。</param>
    /// <param name="writtenAt">完整报文写入完成时间。</param>
    /// <param name="responseTimeout">写入完成后允许回复的时长。</param>
    public void MarkWritten(
        string sessionId,
        string msgId,
        int attemptNo,
        DateTimeOffset writtenAt,
        TimeSpan responseTimeout)
    {
        while (_pending.TryGetValue(sessionId, out var current) &&
               string.Equals(current.MsgId, msgId, StringComparison.Ordinal) &&
               current.AttemptNo == attemptNo)
        {
            var updated = current with
            {
                ReplyDeadline = Min(writtenAt.Add(responseTimeout), current.ExpiresAt)
            };
            if (_pending.TryUpdate(sessionId, updated, current))
                return;
        }
    }

    /// <summary>
    /// 完成一次确定性的 Orleans 回复处理，并仅在版本仍匹配时删除关联。
    /// </summary>
    /// <param name="match">此前通过 TryAcquireReply 取得的发布上下文。</param>
    public void CompleteReply(QGdw3761CommandReplyMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_pending.TryGetValue(match.SessionId, out var current) && current.Version == match.Version)
            TryRemoveExact(match.SessionId, current);
    }

    /// <summary>
    /// 设备返回可重试失败后关闭当前回复窗口但保留 PSEQ，下一 AttemptNo 下发时重新开启窗口。
    /// </summary>
    /// <param name="match">此前通过 TryAcquireReply 取得的发布上下文。</param>
    public void SuspendReply(QGdw3761CommandReplyMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        while (_pending.TryGetValue(match.SessionId, out var current) && current.Version == match.Version)
        {
            var suspended = current with
            {
                ReplyInFlight = false,
                ReplyDeadline = DateTimeOffset.MinValue
            };
            if (_pending.TryUpdate(match.SessionId, suspended, current))
                return;
        }
    }

    /// <summary>
    /// Orleans 暂时不可用时释放发布权，使终端重复确认或命令正式重试可以再次提交同一逻辑回复。
    /// </summary>
    /// <param name="match">此前通过 TryAcquireReply 取得的发布上下文。</param>
    public void ReleaseReply(QGdw3761CommandReplyMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        while (_pending.TryGetValue(match.SessionId, out var current) && current.Version == match.Version)
        {
            var released = current with { ReplyInFlight = false };
            if (_pending.TryUpdate(match.SessionId, released, current))
                return;
        }
    }

    /// <inheritdoc />
    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _pending.TryRemove(sessionId, out _);
        _lastSequences.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 删除超过命令整体 ExpiresAt 的关联；仅超出单次回复窗口的关联会保留 PSEQ，供正式重试继续使用。
    /// </summary>
    /// <param name="now">本轮清理使用的统一时间。</param>
    /// <returns>实际删除的关联数量。</returns>
    public int RemoveExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var item in _pending)
        {
            if (item.Value.ExpiresAt > now)
                continue;
            if (TryRemoveExact(item.Key, item.Value))
                removed++;
        }

        return removed;
    }

    /// <summary>
    /// 创建不可变关联版本，版本号用于阻止迟到发布结果删除新命令。
    /// </summary>
    /// <param name="request">可靠命令 RPC 请求。</param>
    /// <param name="sessionId">当前 TCP Session。</param>
    /// <param name="sessionEpoch">当前 Orleans 路由代次。</param>
    /// <param name="frame">原始下行帧。</param>
    /// <param name="sequence">分配或复用的 PSEQ。</param>
    /// <param name="replyDeadline">本次回复截止时间。</param>
    /// <returns>可写入并发字典的关联快照。</returns>
    private PendingCommand CreatePending(
        SendCommandRequest request,
        string sessionId,
        long sessionEpoch,
        QGdw3761Frame frame,
        byte sequence,
        DateTimeOffset replyDeadline) => new(
            Interlocked.Increment(ref _nextVersion),
            sessionId,
            request.DeviceId,
            request.MsgId,
            request.AttemptNo,
            sessionEpoch,
            request.TraceId,
            sequence,
            frame.Afn,
            frame.Da1,
            frame.Da2,
            frame.Dt1,
            frame.Dt2,
            replyDeadline,
            request.ExpiresAt,
            ReplyInFlight: false);

    /// <summary>
    /// 把 PSEQ 和 CON 写入报文并投影为下发结果。
    /// </summary>
    /// <param name="frame">原始下行帧。</param>
    /// <param name="sequence">本次逻辑命令 PSEQ。</param>
    /// <param name="replyDeadline">本次回复截止时间。</param>
    /// <returns>带新校验和的实际下发报文。</returns>
    private static QGdw3761PreparedCommand CreatePrepared(
        QGdw3761Frame frame,
        byte sequence,
        DateTimeOffset replyDeadline) => new()
        {
            Payload = QGdw3761Codec.PrepareReliableDownstreamCommand(frame, sequence),
            Sequence = sequence,
            ReplyDeadline = replyDeadline
        };

    /// <summary>
    /// 为当前 Session 的新逻辑命令分配循环 PSEQ；只有一问一答通道释放后才会调用，因此 4 位回绕不会产生并发歧义。
    /// </summary>
    /// <param name="sessionId">当前 TCP Session 编号。</param>
    /// <returns>0 到 15 的 PSEQ。</returns>
    private byte NextSequence(string sessionId) => checked((byte)_lastSequences.AddOrUpdate(
        sessionId,
        0,
        static (_, current) => (current + 1) & 0x0F));

    /// <summary>
    /// 判断同一 MsgId 的重试是否仍表示相同协议数据单元。
    /// </summary>
    /// <param name="pending">已存在的关联。</param>
    /// <param name="frame">新尝试携带的下行帧。</param>
    /// <param name="deviceId">RPC 目标设备。</param>
    /// <returns>设备和 AFN/DA/DT 全部一致时返回 true。</returns>
    private static bool MatchesCommandIdentity(PendingCommand pending, QGdw3761Frame frame, string deviceId) =>
        string.Equals(pending.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
        pending.Afn == frame.Afn &&
        pending.Da1 == frame.Da1 &&
        pending.Da2 == frame.Da2 &&
        pending.Dt1 == frame.Dt1 &&
        pending.Dt2 == frame.Dt2;

    /// <summary>
    /// 判断上行 F3 回显的 PSEQ 与原 AFN/DA/DT 是否完整对应当前关联。
    /// </summary>
    /// <param name="pending">当前 Session 活动关联。</param>
    /// <param name="response">终端上行确认。</param>
    /// <returns>全部协议关联字段相同时返回 true。</returns>
    private static bool MatchesReply(PendingCommand pending, QGdw3761Frame response) =>
        string.Equals(pending.DeviceId, response.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        response.Sequence == pending.Sequence &&
        response.DataUnitPayload[0] == pending.Afn &&
        response.DataUnitPayload[1] == pending.Da1 &&
        response.DataUnitPayload[2] == pending.Da2 &&
        response.DataUnitPayload[3] == pending.Dt1 &&
        response.DataUnitPayload[4] == pending.Dt2;

    /// <summary>
    /// 校验终端报文是否为 AFN=00H/F3 逐数据单元从动确认。
    /// </summary>
    /// <param name="response">待判断的上行帧。</param>
    /// <returns>方向、主从位、AFN、Fn 和负载长度均符合约定时返回 true。</returns>
    private static bool IsPerDataUnitConfirmation(QGdw3761Frame response) =>
        response.IsValid &&
        response.IsUpstream &&
        !response.IsPrimaryMessage &&
        response.Afn == 0x00 &&
        response.Functions.Count == 1 &&
        response.Functions[0] == 3 &&
        response.DataUnitPayload.Length >= 6;

    /// <summary>
    /// 返回两个绝对时间中的较早值，保证单次回复窗口不越过命令整体到期时间。
    /// </summary>
    /// <param name="left">第一个绝对时间。</param>
    /// <param name="right">第二个绝对时间。</param>
    /// <returns>较早时间。</returns>
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    /// <summary>
    /// 只在字典中的值仍是预期不可变快照时删除，避免清理线程误删同 Session 刚更新的新命令。
    /// </summary>
    /// <param name="sessionId">字典键。</param>
    /// <param name="expected">读取时观察到的不可变快照。</param>
    /// <returns>本次是否删除了完全匹配的键值对。</returns>
    private bool TryRemoveExact(string sessionId, PendingCommand expected) =>
        ((ICollection<KeyValuePair<string, PendingCommand>>)_pending)
        .Remove(new KeyValuePair<string, PendingCommand>(sessionId, expected));

    /// <summary>
    /// 单个 Session 当前逻辑命令的不可变内部快照；更新均使用 Compare-And-Swap，避免锁住全部在线设备。
    /// </summary>
    private sealed record PendingCommand(
        long Version,
        string SessionId,
        string DeviceId,
        string MsgId,
        int AttemptNo,
        long SessionEpoch,
        string TraceId,
        byte Sequence,
        byte Afn,
        byte Da1,
        byte Da2,
        byte Dt1,
        byte Dt2,
        DateTimeOffset ReplyDeadline,
        DateTimeOffset ExpiresAt,
        bool ReplyInFlight);
}
