using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.CommandSets;

/// <summary>
/// 按协议和参数键保存用户在调试界面中修改过的输入值，避免切换指令后退回 JSON 默认值。
/// </summary>
public sealed class CommandInputValueMemory
{
    private readonly Dictionary<DeviceProtocol, Dictionary<string, string>> _values = [];

    /// <summary>
    /// 取得指定输入项应显示的值；用户曾在同一协议下修改该参数时优先返回修改值，否则返回指令定义默认值。
    /// </summary>
    /// <param name="protocol">当前指令所属协议，用于隔离不同协议的同名参数。</param>
    /// <param name="definition">当前指令输入项定义，提供参数键和 JSON 默认值。</param>
    /// <returns>用户记忆值或输入项默认值。</returns>
    public string Resolve(DeviceProtocol protocol, CommandInputDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _values.TryGetValue(protocol, out var protocolValues) &&
               protocolValues.TryGetValue(definition.Key, out var rememberedValue)
            ? rememberedValue
            : definition.Default;
    }

    /// <summary>
    /// 记录用户修改后的输入值；参数键按不区分大小写比较，同一协议后续功能会复用最新值。
    /// </summary>
    /// <param name="protocol">发生修改的指令所属协议。</param>
    /// <param name="key">输入参数键，例如 administrativeDivision 或 terminalAddress。</param>
    /// <param name="value">用户最新输入值，允许空字符串以表达用户主动清空。</param>
    public void Remember(DeviceProtocol protocol, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!_values.TryGetValue(protocol, out var protocolValues))
        {
            protocolValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _values.Add(protocol, protocolValues);
        }

        protocolValues[key] = value;
    }
}
