using System.Collections.Concurrent;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 使用并发字典保存当前节点的协议在线设备序号，供 HTTP 指令工作负载按设备身份筛选目标。
/// </summary>
public sealed class DeviceOnlineRegistry : IDeviceOnlineRegistry
{
    private readonly ConcurrentDictionary<int, byte> _online = new();

    /// <inheritdoc />
    public int Count => _online.Count;

    /// <inheritdoc />
    public void RegisterOnline(int globalOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalOrdinal);
        // 重复登录场景允许同一全局序号重复进入在线循环，重复注册只保留一个在线条目。
        _online.TryAdd(globalOrdinal, 0);
    }

    /// <inheritdoc />
    public void UnregisterOnline(int globalOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalOrdinal);
        _online.TryRemove(globalOrdinal, out _);
    }

    /// <inheritdoc />
    public bool IsOnline(int globalOrdinal) =>
        globalOrdinal >= 0 && _online.ContainsKey(globalOrdinal);

    /// <inheritdoc />
    public IReadOnlyList<int> GetOnlineOrdinalsSnapshot() =>
        _online.Keys.OrderBy(globalOrdinal => globalOrdinal).ToArray();
}
