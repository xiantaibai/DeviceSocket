using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证调试器在切换协议功能时保留用户修改值，并隔离不同协议的输入记忆。
/// </summary>
public sealed class CommandInputValueMemoryTests
{
    /// <summary>
    /// 验证同一协议下切换指令时，区域码和终端地址使用用户最新修改值而不是新指令默认值。
    /// </summary>
    [Fact]
    public void Resolve_SameProtocolAndKey_PrefersRememberedValue()
    {
        var memory = new CommandInputValueMemory();
        memory.Remember(DeviceProtocol.QGdw3761, "administrativeDivision", "3301");
        memory.Remember(DeviceProtocol.QGdw3761, "terminalAddress", "1234");

        var division = memory.Resolve(
            DeviceProtocol.QGdw3761,
            CreateDefinition("administrativeDivision", "0000"));
        var address = memory.Resolve(
            DeviceProtocol.QGdw3761,
            CreateDefinition("terminalAddress", "0"));

        Assert.Equal("3301", division);
        Assert.Equal("1234", address);
    }

    /// <summary>
    /// 验证不同协议即使出现同名参数也不会相互覆盖，未修改协议继续使用自身默认值。
    /// </summary>
    [Fact]
    public void Resolve_DifferentProtocol_DoesNotReuseRememberedValue()
    {
        var memory = new CommandInputValueMemory();
        memory.Remember(DeviceProtocol.QGdw3761, "address", "3761-value");

        var result = memory.Resolve(
            DeviceProtocol.Dlt6452007,
            CreateDefinition("address", "123456789012"));

        Assert.Equal("123456789012", result);
    }

    /// <summary>
    /// 验证参数记忆可以导出为快照并在新实例中恢复，用于退出时持久化和下次启动回填。
    /// </summary>
    [Fact]
    public void SnapshotAndRestore_RoundTripsProtocolValues()
    {
        var source = new CommandInputValueMemory();
        source.Remember(DeviceProtocol.QGdw3761, "administrativeDivision", "3210");
        source.Remember(DeviceProtocol.QGdw3761, "terminalAddress", "7340");

        var target = new CommandInputValueMemory();
        target.Restore(
            DeviceProtocol.QGdw3761,
            source.Snapshot()[DeviceProtocol.QGdw3761]);

        Assert.Equal(
            "3210",
            target.Resolve(
                DeviceProtocol.QGdw3761,
                CreateDefinition("administrativeDivision", "0000")));
        Assert.Equal(
            "7340",
            target.Resolve(
                DeviceProtocol.QGdw3761,
                CreateDefinition("terminalAddress", "0")));
    }

    /// <summary>
    /// 创建测试使用的最小输入项定义。
    /// </summary>
    /// <param name="key">输入参数键。</param>
    /// <param name="defaultValue">JSON 指令定义提供的默认值。</param>
    /// <returns>可传入输入值记忆组件的定义。</returns>
    private static CommandInputDefinition CreateDefinition(string key, string defaultValue) => new()
    {
        Key = key,
        Default = defaultValue
    };
}
