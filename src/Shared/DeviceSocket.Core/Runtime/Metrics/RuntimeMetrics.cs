using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Metrics;

/// <summary>
/// 使用无锁原子计数维护连接、收发和错误指标。
/// </summary>
public sealed class RuntimeMetrics
{
    private long _tcpConnections;
    private long _qGdw3761Connections;
    private long _dlt645Connections;
    private long _receivedMessages;
    private long _sentMessages;
    private long _sendFailures;
    private long _protocolErrors;

    public long TcpConnections => Interlocked.Read(ref _tcpConnections);
    public long QGdw3761Connections => Interlocked.Read(ref _qGdw3761Connections);
    public long Dlt645Connections => Interlocked.Read(ref _dlt645Connections);
    public long ReceivedMessages => Interlocked.Read(ref _receivedMessages);
    public long SentMessages => Interlocked.Read(ref _sentMessages);
    public long SendFailures => Interlocked.Read(ref _sendFailures);
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);

    /// <summary>
    /// 在 TCP Session 建立后增加总连接数和对应协议连接数。
    /// </summary>
    public void ConnectionOpened(DeviceProtocol protocol)
    {
        Interlocked.Increment(ref _tcpConnections);
        if (protocol == DeviceProtocol.QGdw3761)
            Interlocked.Increment(ref _qGdw3761Connections);
        else
            Interlocked.Increment(ref _dlt645Connections);
    }

    /// <summary>
    /// 在 TCP Session 关闭后安全减少连接计数，计数不会降为负数。
    /// </summary>
    public void ConnectionClosed(DeviceProtocol protocol)
    {
        DecrementNonNegative(ref _tcpConnections);
        if (protocol == DeviceProtocol.QGdw3761)
            DecrementNonNegative(ref _qGdw3761Connections);
        else
            DecrementNonNegative(ref _dlt645Connections);
    }

    /// <summary>记录一条已成功解析的上行消息。</summary>
    public void MessageReceived() => Interlocked.Increment(ref _receivedMessages);

    /// <summary>记录一条已写入连接的下行消息。</summary>
    public void MessageSent() => Interlocked.Increment(ref _sentMessages);

    /// <summary>记录一次下行发送失败。</summary>
    public void SendFailed() => Interlocked.Increment(ref _sendFailures);

    /// <summary>记录一条被协议校验拒绝的消息。</summary>
    public void ProtocolError() => Interlocked.Increment(ref _protocolErrors);

    private static void DecrementNonNegative(ref long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current <= 0 || Interlocked.CompareExchange(ref value, current - 1, current) == current)
                return;
        }
    }
}
