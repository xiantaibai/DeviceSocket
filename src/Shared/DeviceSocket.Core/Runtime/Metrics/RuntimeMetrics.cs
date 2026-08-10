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
    private long _openedConnections;
    private long _closedConnections;
    private long _peakTcpConnections;
    private long _heartbeatsReceived;
    private long _heartbeatConfirmationsSent;

    public long TcpConnections => Interlocked.Read(ref _tcpConnections);
    public long QGdw3761Connections => Interlocked.Read(ref _qGdw3761Connections);
    public long Dlt645Connections => Interlocked.Read(ref _dlt645Connections);
    public long ReceivedMessages => Interlocked.Read(ref _receivedMessages);
    public long SentMessages => Interlocked.Read(ref _sentMessages);
    public long SendFailures => Interlocked.Read(ref _sendFailures);
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);
    public long OpenedConnections => Interlocked.Read(ref _openedConnections);
    public long ClosedConnections => Interlocked.Read(ref _closedConnections);
    public long PeakTcpConnections => Interlocked.Read(ref _peakTcpConnections);
    public long HeartbeatsReceived => Interlocked.Read(ref _heartbeatsReceived);
    public long HeartbeatConfirmationsSent => Interlocked.Read(ref _heartbeatConfirmationsSent);

    /// <summary>
    /// 在 TCP Session 建立后增加总连接数和对应协议连接数。
    /// </summary>
    public void ConnectionOpened(DeviceProtocol protocol)
    {
        var current = Interlocked.Increment(ref _tcpConnections);
        Interlocked.Increment(ref _openedConnections);
        UpdateMaximum(ref _peakTcpConnections, current);
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
        Interlocked.Increment(ref _closedConnections);
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

    /// <summary>记录一条已成功解析的设备心跳。</summary>
    public void HeartbeatReceived() => Interlocked.Increment(ref _heartbeatsReceived);

    /// <summary>记录一条已经成功写入 Socket 的心跳确认。</summary>
    public void HeartbeatConfirmationSent() => Interlocked.Increment(ref _heartbeatConfirmationsSent);

    /// <summary>
    /// 原子递减计数并保证结果不小于零，容忍关闭回调重复到达。
    /// </summary>
    /// <param name="value">需要递减的运行时计数。</param>
    private static void DecrementNonNegative(ref long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current <= 0 || Interlocked.CompareExchange(ref value, current - 1, current) == current)
                return;
        }
    }

    /// <summary>
    /// 通过比较交换更新历史最大值，避免连接热路径引入互斥锁。
    /// </summary>
    /// <param name="target">保存历史最大值的字段。</param>
    /// <param name="candidate">本次观测值。</param>
    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
