using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Tests.Runtime;

/// <summary>
/// 验证设备在线注册表按全局序号维护当前节点的协议在线集合。
/// </summary>
public sealed class DeviceOnlineRegistryTests
{
    /// <summary>
    /// 验证注册、重复注册、注销和查询均只针对稳定的全局设备序号。
    /// </summary>
    [Fact]
    public void RegisterAndUnregister_UpdateOnlineSet()
    {
        var registry = new DeviceOnlineRegistry();

        Assert.False(registry.IsOnline(1));
        Assert.Equal(0, registry.Count);

        registry.RegisterOnline(1);
        registry.RegisterOnline(1);
        registry.RegisterOnline(2);

        Assert.True(registry.IsOnline(1));
        Assert.True(registry.IsOnline(2));
        Assert.Equal(2, registry.Count);

        registry.UnregisterOnline(1);

        Assert.False(registry.IsOnline(1));
        Assert.True(registry.IsOnline(2));
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// 验证在线序号快照按稳定顺序返回独立副本，后续在线状态变化不会反向修改旧快照。
    /// </summary>
    [Fact]
    public void GetOnlineOrdinalsSnapshot_ReturnsSortedIndependentCopy()
    {
        var registry = new DeviceOnlineRegistry();
        registry.RegisterOnline(9);
        registry.RegisterOnline(2);

        var snapshot = registry.GetOnlineOrdinalsSnapshot();
        registry.UnregisterOnline(2);
        registry.RegisterOnline(5);

        Assert.Equal([2, 9], snapshot);
        Assert.Equal([5, 9], registry.GetOnlineOrdinalsSnapshot());
    }
}
