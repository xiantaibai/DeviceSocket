namespace DeviceSocket.LoadTester.Runtime;

internal sealed class RemoteClosedException() : IOException("服务端已经关闭 TCP 连接。");
