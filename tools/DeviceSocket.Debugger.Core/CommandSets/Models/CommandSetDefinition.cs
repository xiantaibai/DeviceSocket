namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述一个可以由调试工具加载的协议指令集文件。
/// </summary>
public sealed class CommandSetDefinition
{
    /// <summary>获取或设置指令集格式版本。</summary>
    public int Version { get; set; } = 1;

    /// <summary>获取或设置指令集显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置协议标识，当前支持 QGdw3761 和 Dlt6452007。</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>获取或设置默认 TCP 端口。</summary>
    public int DefaultPort { get; set; }

    /// <summary>获取或设置指令分组。</summary>
    public List<CommandGroupDefinition> Groups { get; set; } = [];
}
