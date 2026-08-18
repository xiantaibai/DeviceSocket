namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 表示套接字读取返回零字节，即服务端正常或异常关闭了当前 TCP 连接；设备运行循环会把它归类为远端断开并按重连策略恢复。
/// </summary>
internal sealed class RemoteClosedException() : IOException("服务端已经关闭 TCP 连接。");
