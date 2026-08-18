namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示被测 Gateway 的协议和网络终结点。
/// </summary>
public sealed class TargetOptions
{
    /// <summary>
    /// 获取或设置 <c>target.host</c>；默认 <c>127.0.0.1</c>，表示被测服务端的主机名或 IP，空值无效。
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// 获取或设置 <c>target.port</c>；单位为 TCP 端口，默认 0 表示由配置加载器按协议补全，最终有效范围为 1～65535。
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// 获取或设置 <c>target.protocol</c>；默认 Q/GDW 376.1，决定虚拟终端使用的帧格式和地址模型。
    /// </summary>
    public LoadProtocol Protocol { get; set; } = LoadProtocol.QGdw3761;

    /// <summary>
    /// 获取或设置 <c>target.compatibilityProfile</c>；默认 <see cref="ServerCompatibilityProfile.DeviceSocket"/>，仅改变同一协议面向不同服务端实现时的确认和关联规则。
    /// </summary>
    public ServerCompatibilityProfile CompatibilityProfile { get; set; } = ServerCompatibilityProfile.DeviceSocket;
}
