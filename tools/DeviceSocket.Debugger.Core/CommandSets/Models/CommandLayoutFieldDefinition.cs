namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述声明式报文中的一个固定、输入、计算或复制字段。
/// </summary>
public sealed class CommandLayoutFieldDefinition
{
    /// <summary>获取或设置字段唯一名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置字段来源，支持 fixed、input、computed 和 copy。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>获取或设置 fixed 字段的十六进制内容。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>获取或设置 input 字段对应的参数键名。</summary>
    public string InputKey { get; set; } = string.Empty;

    /// <summary>获取或设置输入编码，支持 hex、hexAdd33、bcdReverse 和 byte。</summary>
    public string Encoding { get; set; } = "hex";

    /// <summary>获取或设置 computed 字段使用的安全算法。</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>获取或设置计算范围的起始字段。</summary>
    public string FromField { get; set; } = string.Empty;

    /// <summary>获取或设置计算范围的结束字段。</summary>
    public string ToField { get; set; } = string.Empty;

    /// <summary>获取或设置 copy 字段复制的来源字段。</summary>
    public string CopyField { get; set; } = string.Empty;

    /// <summary>获取或设置计算或复制字段预留的字节数。</summary>
    public int Size { get; set; } = 1;

    /// <summary>获取或设置多字节计算结果的字节序，支持 little 和 big。</summary>
    public string Endian { get; set; } = "little";
}
