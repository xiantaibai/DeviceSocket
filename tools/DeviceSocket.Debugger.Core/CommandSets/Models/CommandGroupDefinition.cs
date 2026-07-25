namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述指令树中的一个业务分组。
/// </summary>
public sealed class CommandGroupDefinition
{
    /// <summary>获取或设置分组名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置分组内的指令列表。</summary>
    public List<CommandDefinition> Commands { get; set; } = [];
}
