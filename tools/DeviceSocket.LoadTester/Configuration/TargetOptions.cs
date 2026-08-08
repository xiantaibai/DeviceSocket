namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示被测 Gateway 的协议和网络终结点。
/// </summary>
public sealed class TargetOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public LoadProtocol Protocol { get; set; } = LoadProtocol.QGdw3761;
}
