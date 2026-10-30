namespace DeviceSocket.LoadTester.Metrics;

/// <summary>保存 GatherV4 设备侧命令事实；正确 F1 写出与服务端任务完成分开解释，支持跨分片求和。</summary>
public sealed record GatherV4CommandSummary
{
    /// <summary>获取收到的命令帧数，包含疑似重发。</summary>
    public long ReceivedFrames { get; init; }
    /// <summary>获取连接内有界观察窗口去重后的命令数，不等于服务端业务 CommandId 数。</summary>
    public long ObservedCommands { get; init; }
    /// <summary>获取成功写出的正确 F1 数量，包含收到重发后的再次确认。</summary>
    public long CorrectConfirmationsSent { get; init; }
    /// <summary>获取按故障模式故意不回复的次数。</summary>
    public long IntentionalDrops { get; init; }
    /// <summary>获取成功写出的错误 A3/PSEQ、F2 或重复确认附加帧数，不算正常成功。</summary>
    public long FaultResponsesSent { get; init; }
    /// <summary>获取回复写出失败次数。</summary>
    public long WriteFailures { get; init; }
    /// <summary>获取观察窗口内同一会话相同完整帧再次到达次数。</summary>
    public long SuspectedRepeatedFrames { get; init; }
    /// <summary>获取已正确确认后相同帧再次到达次数；序号复用可能产生相同帧，不能直接断言重复执行。</summary>
    public long RepeatedAfterConfirmation { get; init; }
    /// <summary>获取正确确认后观察到另一条不同命令的次数。</summary>
    public long FollowingCommands { get; init; }
    /// <summary>获取因单设备待回复队列达到上限而拒绝的应答数。</summary>
    public long QueueRejected { get; init; }
    /// <summary>获取断线、缩容或结束时取消的待回复数。</summary>
    public long CancelledResponses { get; init; }
    /// <summary>获取当前仍排队的延迟应答数量。</summary>
    public long PendingResponses { get; init; }
    /// <summary>获取从收到命令到正确 F1 写出的延迟直方图。</summary>
    public HistogramSnapshot ConfirmationLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取从前一条正确 F1 写出到后续不同命令到达的等待直方图。</summary>
    public HistogramSnapshot FollowingCommandLatency { get; init; } = new MergeableHistogram().Snapshot();

    /// <summary>合并各节点设备侧事实，累计计数求和、直方图合并；空集合产生全零结果。</summary>
    /// <param name="summaries">同一运行的各节点快照。</param>
    /// <returns>全局设备命令观测。</returns>
    public static GatherV4CommandSummary Merge(IEnumerable<GatherV4CommandSummary> summaries)
    {
        var source = summaries.ToArray();
        if (source.Length == 0)
            return new();
        return new()
        {
            ReceivedFrames = source.Sum(item => item.ReceivedFrames),
            ObservedCommands = source.Sum(item => item.ObservedCommands),
            CorrectConfirmationsSent = source.Sum(item => item.CorrectConfirmationsSent),
            IntentionalDrops = source.Sum(item => item.IntentionalDrops),
            FaultResponsesSent = source.Sum(item => item.FaultResponsesSent),
            WriteFailures = source.Sum(item => item.WriteFailures),
            SuspectedRepeatedFrames = source.Sum(item => item.SuspectedRepeatedFrames),
            RepeatedAfterConfirmation = source.Sum(item => item.RepeatedAfterConfirmation),
            FollowingCommands = source.Sum(item => item.FollowingCommands),
            QueueRejected = source.Sum(item => item.QueueRejected),
            CancelledResponses = source.Sum(item => item.CancelledResponses),
            PendingResponses = source.Sum(item => item.PendingResponses),
            ConfirmationLatency = MergeableHistogram.Merge(source.Select(item => item.ConfirmationLatency)),
            FollowingCommandLatency = MergeableHistogram.Merge(source.Select(item => item.FollowingCommandLatency))
        };
    }
}
