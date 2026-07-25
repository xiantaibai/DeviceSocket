namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述一条可以生成并发送的调试指令。
/// </summary>
public sealed class CommandDefinition
{
    /// <summary>获取或设置指令唯一标识。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>获取或设置指令显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置指令用途说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>获取或设置生成模式，当前支持 codec 和 raw。</summary>
    public string Mode { get; set; } = "codec";

    /// <summary>获取或设置安全白名单中的编码器名称。</summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>获取或设置 raw 模式使用的十六进制报文模板。</summary>
    public string? RawHex { get; set; }

    /// <summary>获取或设置 layout 模式使用的声明式字段布局。</summary>
    public CommandLayoutDefinition? Layout { get; set; }

    /// <summary>获取或设置不在界面显示、由指令文件固定提供的编码参数。</summary>
    public Dictionary<string, string> FixedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>获取或设置发送前由界面收集的参数定义。</summary>
    public List<CommandInputDefinition> Inputs { get; set; } = [];

    /// <summary>获取或设置响应解析配置。</summary>
    public CommandResponseDefinition Response { get; set; } = new();
}
