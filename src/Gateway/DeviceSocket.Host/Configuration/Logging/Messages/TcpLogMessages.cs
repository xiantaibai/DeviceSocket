using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Networking.Common;
using SuperSocket.Connection;

namespace DeviceSocket.Host.Configuration.Logging.Messages;

/// <summary>
/// 提供 TCP 服务、Session 生命周期、接入保护和超时清理的编译时源生成日志方法。
/// </summary>
internal static partial class TcpLogMessages
{
    /// <summary>记录 Q/GDW 376.1 TCP 服务已经开始监听。</summary>
    [LoggerMessage(1001, LogLevel.Information, "Q/GDW 376.1 server listening on TCP {Ip}:{Port}, backlog {BackLog}", EventName = "QGdw3761ServerStarted")]
    internal static partial void QGdw3761ServerStarted(ILogger logger, string ip, int port, int backLog);

    /// <summary>记录 DL/T 645 TCP 服务已经开始监听。</summary>
    [LoggerMessage(1002, LogLevel.Information, "DL/T 645-2007 server listening on TCP {Ip}:{Port}, backlog {BackLog}", EventName = "Dlt645ServerStarted")]
    internal static partial void Dlt645ServerStarted(ILogger logger, string ip, int port, int backLog);

    /// <summary>记录 SessionId 冲突导致无法纳入连接看门狗的连接。</summary>
    [LoggerMessage(1003, LogLevel.Warning, "TCP session registration conflict; closing untracked session: {SessionId} {Protocol} {RemoteEndPoint}", EventName = "TcpSessionRegistrationConflict")]
    internal static partial void SessionRegistrationConflict(
        ILogger logger,
        string sessionId,
        DeviceProtocol protocol,
        string? remoteEndPoint);

    /// <summary>记录 TCP Session 打开调试事件。</summary>
    [LoggerMessage(1004, LogLevel.Debug, "TCP session opened: {SessionId}, {Protocol}, {RemoteEndPoint}", EventName = "TcpSessionOpened")]
    internal static partial void SessionOpened(
        ILogger logger,
        string sessionId,
        DeviceProtocol protocol,
        string? remoteEndPoint);

    /// <summary>记录 TCP Session 关闭调试事件。</summary>
    [LoggerMessage(1005, LogLevel.Debug, "TCP session closed: {SessionId}, {Protocol}, {Reason}", EventName = "TcpSessionClosed")]
    internal static partial void SessionClosed(
        ILogger logger,
        string sessionId,
        DeviceProtocol protocol,
        CloseReason reason);

    /// <summary>记录被连接容量或速率保护拒绝的 TCP Session。</summary>
    [LoggerMessage(1006, LogLevel.Warning, "TCP connection rejected by middleware: {Reason}, {SessionId}, {RemoteEndPoint}, {ConnectionCount}/{MaxConnections}, rejected total {RejectedConnections}", EventName = "TcpConnectionRejected")]
    internal static partial void ConnectionRejected(
        ILogger logger,
        DeviceConnectionRejectionReason reason,
        string sessionId,
        string? remoteEndPoint,
        int connectionCount,
        int maxConnections,
        long rejectedConnections);

    /// <summary>记录当前平台不支持完整 TCP KeepAlive 参数。</summary>
    [LoggerMessage(1007, LogLevel.Warning, "当前平台不支持完整 TCP KeepAlive 参数，将继续使用应用层 Session 超时清理。", EventName = "TcpKeepAliveConfigurationUnsupported")]
    internal static partial void KeepAliveConfigurationUnsupported(ILogger logger, Exception exception);

    /// <summary>记录主动关闭设备 Session 的底层异常。</summary>
    [LoggerMessage(1008, LogLevel.Warning, "主动关闭设备 Session 失败: {SessionId} {Reason}", EventName = "TcpSessionCloseFailed")]
    internal static partial void SessionCloseFailed(
        ILogger logger,
        string sessionId,
        DeviceSessionCloseReason reason,
        Exception exception);

    /// <summary>记录一轮关闭了超时设备 Session 的扫描结果。</summary>
    [LoggerMessage(1009, LogLevel.Information, "本轮主动清理 {ClosedSessions} 条超时设备 TCP Session，扫描 {ScannedSessions} 条，候选 {Candidates} 条，耗时 {ElapsedMilliseconds} ms。", EventName = "TcpExpiredSessionsClosed")]
    internal static partial void ExpiredSessionsClosed(
        ILogger logger,
        int closedSessions,
        int scannedSessions,
        int candidates,
        long elapsedMilliseconds);
}
