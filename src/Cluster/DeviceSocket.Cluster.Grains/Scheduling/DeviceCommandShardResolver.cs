using System.Text;

namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 使用稳定的 UTF-8 哈希把设备均匀映射到固定数量的调度分片。
/// </summary>
public static class DeviceCommandShardResolver
{
    /// <summary>
    /// 计算设备调度分片；分片数必须是 2 的幂，确保使用位掩码稳定映射。
    /// </summary>
    /// <param name="deviceId">非空设备编号。</param>
    /// <param name="shardCount">大于零且为 2 的幂的分片数。</param>
    /// <returns>范围为零到分片数减一的分片编号。</returns>
    /// <exception cref="ArgumentException">设备编号为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">分片数不是大于零的 2 的幂。</exception>
    public static int Resolve(string deviceId, int shardCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (shardCount <= 0 || (shardCount & (shardCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(shardCount), "调度分片数必须是大于零的 2 的幂。");

        var bytes = Encoding.UTF8.GetBytes(deviceId.Trim());
        // FNV-1a 只依赖明确的 UTF-8 字节，避免 string.GetHashCode 在进程重启后随机化导致设备换分片。
        uint hash = 2166136261;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 16777619;
        }

        return (int)(hash & (uint)(shardCount - 1));
    }
}
