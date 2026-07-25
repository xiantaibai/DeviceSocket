namespace DeviceSocket.Debugger.Core.CommandSets.Models;

/// <summary>
/// 描述指令发送后采用的响应解析和等待策略。
/// </summary>
public sealed class CommandResponseDefinition
{
    /// <summary>获取或设置响应解码器；为空时使用指令集协议的默认解码器。</summary>
    public string Decoder { get; set; } = string.Empty;

    /// <summary>获取或设置建议等待响应的毫秒数。</summary>
    public int TimeoutMs { get; set; } = 5000;
}
