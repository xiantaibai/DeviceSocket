namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 按虚拟设备全局序号维护当前节点已进入协议在线状态的设备集合。
/// </summary>
public interface IDeviceOnlineRegistry
{
    /// <summary>
    /// 获取当前节点注册为协议在线的设备数量。
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 在虚拟设备完成协议登录并进入在线状态时注册。
    /// </summary>
    /// <param name="globalOrdinal">虚拟设备的稳定全局序号，必须非负。</param>
    void RegisterOnline(int globalOrdinal);

    /// <summary>
    /// 在在线设备断开、停止或开始重连时注销。
    /// </summary>
    /// <param name="globalOrdinal">虚拟设备的稳定全局序号，必须非负。</param>
    void UnregisterOnline(int globalOrdinal);

    /// <summary>
    /// 判断指定全局序号的虚拟设备当前是否注册为在线。
    /// </summary>
    /// <param name="globalOrdinal">待检查的全局设备序号。</param>
    /// <returns>注册表包含该序号时返回 true。</returns>
    bool IsOnline(int globalOrdinal);

    /// <summary>
    /// 获取调用时刻的在线设备全局序号副本，供一个 HTTP 阶段按同一份在线状态稳定选择目标。
    /// </summary>
    /// <returns>按全局序号升序排列的独立只读快照；后续注册或注销不会修改已返回集合。</returns>
    IReadOnlyList<int> GetOnlineOrdinalsSnapshot();
}
