using System.Collections.Concurrent;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Metrics;

/// <summary>以原子计数汇总全部 GatherV4 会话的命令观测，不依赖 HTTP 请求登记，不存储设备编号或原始报文。</summary>
internal sealed class GatherV4CommandMetrics
{
    private long _received, _observed, _correct, _drops, _faults, _failures, _repeated, _after, _following, _rejected, _cancelled, _pending;
    private readonly MergeableHistogram _confirmationLatency = new();
    private readonly MergeableHistogram _followingLatency = new();
    private readonly ConcurrentQueue<GatherV4CommandSample> _samples = new();

    /// <summary>保留最近 500 条应答样本，成功与失败均可查看，不按设备创建文件。</summary>
    /// <param name="sample">当前分片应答事实。</param>
    internal void RecordSample(GatherV4CommandSample sample)
    {
        _samples.Enqueue(sample);
        while (_samples.Count > 500) _samples.TryDequeue(out _);
    }

    /// <summary>复制有界样本供最终 JSONL 落盘。</summary>
    /// <returns>当前最近样本，最多 500 条。</returns>
    internal IReadOnlyList<GatherV4CommandSample> GetSamples() => _samples.ToArray();

    /// <summary>记录命令帧到达；疑似重发只增加帧数，不增加观察窗口内唯一命令数。</summary>
    /// <param name="repeated">是否与同会话近期帧相同。</param>
    /// <param name="afterConfirmation">重复帧是否出现在正确确认之后。</param>
    internal void Received(bool repeated, bool afterConfirmation)
    {
        Interlocked.Increment(ref _received);
        if (repeated) Interlocked.Increment(ref _repeated);
        else Interlocked.Increment(ref _observed);
        if (afterConfirmation) Interlocked.Increment(ref _after);
    }

    /// <summary>记录正确 F1 已写入 TCP 流，不代表主站已经消费成功。</summary>
    /// <param name="latency">收到命令到写出确认的耗时。</param>
    internal void Confirmed(TimeSpan latency) { Interlocked.Increment(ref _correct); _confirmationLatency.Record(latency); }
    /// <summary>记录正确确认之后另一条不同命令到达。</summary>
    /// <param name="latency">前一次正确确认至本次下行的间隔。</param>
    internal void Following(TimeSpan latency) { Interlocked.Increment(ref _following); _followingLatency.Record(latency); }
    /// <summary>记录故意不回复，故障样本不计为正常回复成功。</summary>
    internal void Dropped() => Interlocked.Increment(ref _drops);
    /// <summary>记录一帧成功写出的故障应答。</summary>
    internal void FaultSent() => Interlocked.Increment(ref _faults);
    /// <summary>记录应答写入失败。</summary>
    internal void WriteFailed() => Interlocked.Increment(ref _failures);
    /// <summary>记录待回复队列满造成的显式拒绝。</summary>
    internal void Rejected() => Interlocked.Increment(ref _rejected);
    /// <summary>记录加入延迟调度的待回复项。</summary>
    internal void Queued() => Interlocked.Increment(ref _pending);
    /// <summary>移除一条排队回复；断线取消会独立累计。</summary>
    /// <param name="cancelled">是否因会话结束取消。</param>
    internal void Dequeued(bool cancelled) { Interlocked.Decrement(ref _pending); if (cancelled) Interlocked.Increment(ref _cancelled); }

    /// <summary>读取可序列化快照供时间序列、节点及合并报告使用。</summary>
    /// <returns>原子读取的累计量与可合并直方图。</returns>
    internal GatherV4CommandSummary Snapshot() => new()
    {
        ReceivedFrames = Interlocked.Read(ref _received),
        ObservedCommands = Interlocked.Read(ref _observed),
        CorrectConfirmationsSent = Interlocked.Read(ref _correct),
        IntentionalDrops = Interlocked.Read(ref _drops),
        FaultResponsesSent = Interlocked.Read(ref _faults),
        WriteFailures = Interlocked.Read(ref _failures),
        SuspectedRepeatedFrames = Interlocked.Read(ref _repeated),
        RepeatedAfterConfirmation = Interlocked.Read(ref _after),
        FollowingCommands = Interlocked.Read(ref _following),
        QueueRejected = Interlocked.Read(ref _rejected),
        CancelledResponses = Interlocked.Read(ref _cancelled),
        PendingResponses = Interlocked.Read(ref _pending),
        ConfirmationLatency = _confirmationLatency.Snapshot(),
        FollowingCommandLatency = _followingLatency.Snapshot()
    };
}
