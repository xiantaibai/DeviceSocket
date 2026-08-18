using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示周期覆盖保存的单节点恢复检查点；当进程被强制结束而无法写最终报告时，可据此读取最后一次累计指标和资源状态。
/// </summary>
public sealed record RunCheckpoint
{
    /// <summary>获取检查点结构版本，当前固定为 1。</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>获取本轮分布式测试运行编号。</summary>
    public required string RunId { get; init; }
    /// <summary>获取写入检查点的负载机名称。</summary>
    public required string NodeName { get; init; }
    /// <summary>获取当前分片从零开始的编号。</summary>
    public required int ShardIndex { get; init; }
    /// <summary>获取整轮测试配置的分片总数。</summary>
    public required int ShardCount { get; init; }
    /// <summary>获取当前分片分配的虚拟设备数量。</summary>
    public required int LocalDeviceCount { get; init; }
    /// <summary>获取检查点采样的 UTC 时间。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>获取检查点时刻的累计连接、协议、HTTP 和错误指标。</summary>
    public required MetricSnapshot Metrics { get; init; }
    /// <summary>获取检查点时刻负载机进程的 CPU、内存、线程和句柄资源。</summary>
    public required ResourceSnapshot Resources { get; init; }
    /// <summary>获取检查点时刻可选服务端状态接口的最新采样；未启用服务端观测时为空。</summary>
    public GatewayStatusSnapshot? Gateway { get; init; }
}
