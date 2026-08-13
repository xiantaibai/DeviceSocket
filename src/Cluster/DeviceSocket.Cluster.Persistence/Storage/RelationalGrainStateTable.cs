namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 应用 Grain 类型与独立状态表的内部映射。
/// </summary>
public enum RelationalGrainStateTable
{
    /// <summary>设备状态表。</summary>
    Device = 0,

    /// <summary>可靠命令状态表。</summary>
    Command = 1,

    /// <summary>Gateway 节点状态表。</summary>
    Gateway = 2,

    /// <summary>固定调度分片状态表。</summary>
    Scheduler = 3
}
