namespace DeviceSocket.Core.Runtime.Connections;

/// <summary>
/// 在线设备目录抽象，可替换为带 Redis 路由元数据的实现。
/// </summary>
public interface IOnlineDeviceRegistry
{
    int Count { get; }

    /// <summary>
    /// 注册或替换设备当前连接。
    /// </summary>
    DeviceRegistration Register(string deviceId, IDeviceConnection connection, DateTimeOffset now);

    /// <summary>
    /// 仅在 SessionId 仍匹配当前连接时刷新最后活跃时间。
    /// </summary>
    bool Touch(string deviceId, string sessionId, DateTimeOffset now);

    /// <summary>
    /// 根据设备编号取得本节点实际可写的连接。
    /// </summary>
    bool TryGetConnection(string deviceId, out IDeviceConnection? connection);

    /// <summary>
    /// 按 SessionId 条件删除连接，防止旧连接关闭时误删新连接。
    /// </summary>
    bool RemoveSession(string sessionId);

    /// <summary>
    /// 获取按最后活跃时间倒序排列的有限设备快照。
    /// </summary>
    IReadOnlyList<DeviceConnectionSnapshot> GetSnapshot(int take = 100);
}
