using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Debugger.Core.Protocols;

/// <summary>
/// 在 JSON 协议名称与核心层协议枚举之间进行受控转换。
/// </summary>
public static class ProtocolResolver
{
    /// <summary>
    /// 把指令集协议名称转换为核心枚举。
    /// </summary>
    /// <param name="name">JSON 中的协议名称。</param>
    /// <returns>核心协议枚举。</returns>
    public static DeviceProtocol Parse(string name) =>
        name.ToLowerInvariant() switch
        {
            "qgdw3761" => DeviceProtocol.QGdw3761,
            "dlt6452007" => DeviceProtocol.Dlt6452007,
            _ => throw new ArgumentOutOfRangeException(nameof(name), $"不支持协议“{name}”。")
        };

    /// <summary>
    /// 获取协议的中文显示名称。
    /// </summary>
    /// <param name="protocol">核心协议枚举。</param>
    /// <returns>界面显示名称。</returns>
    public static string GetDisplayName(DeviceProtocol protocol) =>
        protocol == DeviceProtocol.QGdw3761 ? "Q/GDW 376.1" : "DL/T 645-2007";
}
