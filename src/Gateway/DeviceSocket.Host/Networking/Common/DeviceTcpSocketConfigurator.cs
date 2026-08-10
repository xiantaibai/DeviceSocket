using System.Net.Sockets;
using DeviceSocket.Host.Configuration.Logging.Messages;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 为 SuperSocket 接受的设备 Socket 设置跨平台 TCP KeepAlive 参数。
/// </summary>
public static class DeviceTcpSocketConfigurator
{
    private static int _configurationFailureReported;

    /// <summary>
    /// 启用并调整 TCP KeepAlive；平台不支持细粒度参数时保留应用层空闲清理并只告警一次。
    /// </summary>
    /// <param name="socket">刚接受的设备 TCP Socket。</param>
    /// <param name="options">设备连接配置。</param>
    /// <param name="logger">用于记录平台兼容性告警的程序日志器。</param>
    public static void Configure(Socket socket, DeviceConnectionOptions options, ILogger logger)
    {
        if (!options.TcpKeepAliveEnabled)
            return;

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, options.TcpKeepAliveTimeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, options.TcpKeepAliveIntervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, options.TcpKeepAliveRetryCount);
        }
        catch (Exception exception) when (exception is SocketException or PlatformNotSupportedException)
        {
            // 某些旧版操作系统只能启用系统默认 KeepAlive；应用层合法报文空闲超时仍会负责最终回收。
            if (Interlocked.Exchange(ref _configurationFailureReported, 1) == 0)
                TcpLogMessages.KeepAliveConfigurationUnsupported(logger, exception);
        }
    }
}
