using DeviceSocket.Core.Protocols.Common;

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
                return CreateUnmatchedResult(entry.Category);

            var matchedIndex = FindMatchedRequest(entry.Metadata);
            if (matchedIndex < 0)
                return CreateUnmatchedResult(entry.Category);

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
    /// 把包含结构化协议字段的业务或心跳发送记录加入有限等待队列。
    /// </summary>
    /// <param name="entry">已经成功发送的通信记录。</param>
    private void TrackPendingRequest(DebugTrafficEntry entry)
    {
        if (entry.Metadata is null ||
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
    /// <returns>未关联结果。</returns>
    private static DebugTrafficCorrelationResult CreateUnmatchedResult(DebugTrafficCategory category)
    {
        var status = category switch
        {
            DebugTrafficCategory.Business => "主动上报 / 未匹配",
            DebugTrafficCategory.Heartbeat => "心跳消息",
            DebugTrafficCategory.Error => "异常消息",
            _ => "状态消息"
        };
        return new DebugTrafficCorrelationResult(category, status, null, false);
    }
}
