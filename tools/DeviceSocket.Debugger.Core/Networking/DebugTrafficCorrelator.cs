using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 在有限时间窗口内按协议、设备和序号关联发送请求与接收回复。
/// </summary>
public sealed class DebugTrafficCorrelator
{
    private const int MaximumPendingRequestCount = 512;
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(2);
    private readonly object _syncRoot = new();
    private readonly List<PendingRequest> _pendingRequests = [];
    private QGdw3761ServerCompatibilityProfile _qGdw3761ServerProfile;

    /// <summary>
    /// 初始化请求回复关联器；默认保留 Gather 老服务的 AFN=00H/F1 固定序号兼容行为。
    /// </summary>
    /// <param name="qGdw3761ServerProfile">Q/GDW 376.1 服务端兼容类型。</param>
    public DebugTrafficCorrelator(
        QGdw3761ServerCompatibilityProfile qGdw3761ServerProfile =
            QGdw3761ServerCompatibilityProfile.GatherTcpCenter)
    {
        _qGdw3761ServerProfile = qGdw3761ServerProfile;
    }

    /// <summary>
    /// 保存一条等待回复的发送请求所需的最小关联字段。
    /// </summary>
    /// <param name="Timestamp">请求成功写入 TCP 流的时间。</param>
    /// <param name="Category">发送请求的分类。</param>
    /// <param name="Metadata">从发送帧提取的协议字段。</param>
    private sealed record PendingRequest(
        DateTimeOffset Timestamp,
        DebugTrafficCategory Category,
        DebugTrafficMetadata Metadata);

    /// <summary>
    /// 观察一条通信记录；发送记录会进入等待队列，接收记录会尝试匹配并返回最终分类。
    /// </summary>
    /// <param name="entry">待观察的发送、接收、异常或状态记录。</param>
    /// <returns>分类、匹配状态和响应耗时。</returns>
    /// <exception cref="ArgumentNullException">通信记录为 null 时抛出。</exception>
    public DebugTrafficCorrelationResult Observe(DebugTrafficEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_syncRoot)
        {
            RemoveExpiredRequests(entry.Timestamp);
            if (entry.Direction == TrafficDirection.Send)
            {
                TrackPendingRequest(entry);
                return new DebugTrafficCorrelationResult(entry.Category, "等待回复", null, false);
            }

            if (entry.Direction != TrafficDirection.Receive || entry.Metadata is null)
                return CreateUnmatchedResult(entry.Category, entry.Metadata);

            var matchedIndex = FindMatchedRequest(entry.Metadata);
            if (matchedIndex < 0)
                return CreateUnmatchedResult(entry.Category, entry.Metadata);

            var request = _pendingRequests[matchedIndex];
            _pendingRequests.RemoveAt(matchedIndex);
            var responseTime = entry.Timestamp - request.Timestamp;
            if (responseTime < TimeSpan.Zero)
                responseTime = TimeSpan.Zero;

            return new DebugTrafficCorrelationResult(
                request.Category,
                $"已匹配 · {responseTime.TotalMilliseconds:F0} ms",
                responseTime,
                true);
        }
    }

    /// <summary>
    /// 清空所有尚未收到回复的请求，通常在用户清空记录或重新连接时调用。
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
            _pendingRequests.Clear();
    }

    /// <summary>
    /// 切换 Q/GDW 376.1 服务端兼容规则，并清除旧规则下尚未匹配的请求，避免跨模式误关联。
    /// </summary>
    /// <param name="profile">新的服务端兼容类型。</param>
    public void SetQGdw3761ServerProfile(QGdw3761ServerCompatibilityProfile profile)
    {
        lock (_syncRoot)
        {
            if (_qGdw3761ServerProfile == profile)
                return;
            _qGdw3761ServerProfile = profile;
            _pendingRequests.Clear();
        }
    }

    /// <summary>
    /// 把包含结构化协议字段的业务或心跳发送记录加入有限等待队列。
    /// </summary>
    /// <param name="entry">已经成功发送的通信记录。</param>
    private void TrackPendingRequest(DebugTrafficEntry entry)
    {
        if (!entry.TrackResponse ||
            entry.Metadata is null ||
            entry.Category is not (
                DebugTrafficCategory.Business or
                DebugTrafficCategory.Heartbeat or
                DebugTrafficCategory.System))
        {
            return;
        }

        _pendingRequests.Add(new PendingRequest(entry.Timestamp, entry.Category, entry.Metadata));
        if (_pendingRequests.Count > MaximumPendingRequestCount)
            _pendingRequests.RemoveAt(0);
    }

    /// <summary>
    /// 从最新请求开始查找与接收帧对应的设备和序号，降低四位序号循环带来的误匹配概率。
    /// </summary>
    /// <param name="received">接收帧的结构化协议字段。</param>
    /// <returns>匹配请求的集合索引；未找到时返回 -1。</returns>
    private int FindMatchedRequest(DebugTrafficMetadata received)
    {
        if (_qGdw3761ServerProfile == QGdw3761ServerCompatibilityProfile.GatherTcpCenter &&
            IsGatherLegacyLinkConfirmation(received))
            return FindLatestPendingRequest(received);

        for (var index = _pendingRequests.Count - 1; index >= 0; index--)
        {
            var sent = _pendingRequests[index].Metadata;
            if (sent.Protocol != received.Protocol ||
                !string.Equals(sent.DeviceId, received.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sent.Protocol == DeviceProtocol.QGdw3761)
            {
                if (sent.Sequence.HasValue && sent.Sequence == received.Sequence)
                    return index;
                continue;
            }

            if (string.Equals(sent.InstructionKey, received.InstructionKey, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// 判断接收帧是否为 Gather 老 TCP 服务返回的 AFN=00H/F1 通用链路确认。
    /// </summary>
    /// <param name="received">接收帧的结构化协议字段。</param>
    /// <returns>属于老服务通用链路确认时返回 true。</returns>
    private static bool IsGatherLegacyLinkConfirmation(DebugTrafficMetadata received) =>
        received.Protocol == DeviceProtocol.QGdw3761 &&
        received.ApplicationFunctionCode == 0x00 &&
        received.Functions.Count == 1 &&
        received.Functions[0] == 1;

    /// <summary>
    /// 为不回显请求序号的老服务 AFN=00H/F1 确认查找同设备最近一条请求。
    /// </summary>
    /// <param name="received">已经确认是 AFN=00H/F1 的接收帧元数据。</param>
    /// <returns>同设备最近请求的集合索引；未找到时返回 -1。</returns>
    private int FindLatestPendingRequest(DebugTrafficMetadata received)
    {
        for (var index = _pendingRequests.Count - 1; index >= 0; index--)
        {
            var sent = _pendingRequests[index].Metadata;
            if (sent.Protocol != received.Protocol ||
                !string.Equals(sent.DeviceId, received.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Gather 老服务会把 AFN=02H/F1、F2、F3 都回复成 AFN=00H/F1，且 RSEQ 可能固定为 0。
            // 此时不能按固定 RSEQ 命中更早的请求，只能由同设备最近一次发送的类别决定确认帧类别。
            return index;
        }

        return -1;
    }

    /// <summary>
    /// 删除超过关联时间窗口的请求，避免序号循环后把很早的发送误认为当前回复。
    /// </summary>
    /// <param name="now">当前正在处理记录的时间。</param>
    private void RemoveExpiredRequests(DateTimeOffset now)
    {
        var earliest = now - CorrelationWindow;
        _pendingRequests.RemoveAll(request => request.Timestamp < earliest);
    }

    /// <summary>
    /// 为未关联记录生成清晰的状态说明，区分主动业务上报、心跳、异常和系统状态。
    /// </summary>
    /// <param name="category">记录当前分类。</param>
    /// <param name="metadata">可选协议方向元数据，用于区分主站下行与终端主动上报。</param>
    /// <returns>未关联结果。</returns>
    private static DebugTrafficCorrelationResult CreateUnmatchedResult(
        DebugTrafficCategory category,
        DebugTrafficMetadata? metadata)
    {
        var isQGdwServerDownstream =
            metadata?.Protocol == DeviceProtocol.QGdw3761 &&
            metadata.IsUpstream == false &&
            metadata.IsPrimaryMessage == true;
        var status = isQGdwServerDownstream
            ? "服务端下行 / 待应答"
            : category switch
            {
                DebugTrafficCategory.Business => "主动上报 / 未匹配",
                DebugTrafficCategory.Heartbeat => "心跳消息",
                DebugTrafficCategory.Error => "异常消息",
                _ => "状态消息"
            };
        return new DebugTrafficCorrelationResult(category, status, null, false);
    }
}
