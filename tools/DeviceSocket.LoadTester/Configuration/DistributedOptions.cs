namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示跨负载机共享的运行编号、设备分片和计划启动时间。
/// </summary>
public sealed class DistributedOptions
{
    public string RunId { get; set; } = string.Empty;
    public int ShardIndex { get; set; }
    public int ShardCount { get; set; } = 1;
    public DateTimeOffset? StartAtUtc { get; set; }
}
