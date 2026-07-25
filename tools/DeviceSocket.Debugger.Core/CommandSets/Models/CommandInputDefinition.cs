namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述一条指令的动态输入参数。
/// </summary>
public sealed class CommandInputDefinition
{
    /// <summary>获取或设置参数键名。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>获取或设置界面标签。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>获取或设置输入类型，支持 text、number、hex、select 和 datetime。</summary>
    public string Type { get; set; } = "text";

    /// <summary>获取或设置默认值。</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>获取或设置参数是否必填。</summary>
    public bool Required { get; set; } = true;

    /// <summary>获取或设置参数说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>获取或设置 select 类型的候选值。</summary>
    public List<string> Options { get; set; } = [];
}
