namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述 layout 模式下按顺序组成原始报文的字段集合。
/// </summary>
public sealed class CommandLayoutDefinition
{
    /// <summary>获取或设置报文字段；数组顺序就是最终线路字节顺序。</summary>
    public List<CommandLayoutFieldDefinition> Fields { get; set; } = [];
}
