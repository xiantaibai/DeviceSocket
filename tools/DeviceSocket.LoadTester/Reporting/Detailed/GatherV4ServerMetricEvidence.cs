namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>表示 GatherV4 Collector 的一个低基数指标在有效压测时间窗内的聚合证据，缺失指标不补零。</summary>
internal sealed record GatherV4ServerMetricEvidence
{
    /// <summary>获取指标名，processing_complete 在名称后附加固定操作后缀。</summary>
    public required string Name { get; init; }
    /// <summary>获取有效窗口中跨实例及固定维度聚合的峰值。</summary>
    public double Maximum { get; init; }
    /// <summary>获取最后采样时刻聚合值；只能作为窗口末端证据。</summary>
    public double Last { get; init; }
    /// <summary>获取累计 Counter 区间增量；Gauge 或不足两个样本时为空。</summary>
    public double? Increase { get; init; }
    /// <summary>获取累计值回退或启动时间改变次数，用于识别进程重启。</summary>
    public int CounterResets { get; init; }
    /// <summary>获取去重后的序列样本总数。</summary>
    public int Samples { get; init; }
}
