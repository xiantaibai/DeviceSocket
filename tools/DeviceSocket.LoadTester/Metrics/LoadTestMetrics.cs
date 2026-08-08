using System.Collections.Concurrent;

namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 以原子计数维护负载节点的热路径指标，并跟踪目标在线数下降后的恢复窗口。
/// </summary>
public sealed class LoadTestMetrics
{
    private readonly ConcurrentDictionary<string, long> _errors = new(StringComparer.Ordinal);
    private readonly object _recoveryLock = new();
    private readonly MergeableHistogram _connectionLatency = new();
    private readonly MergeableHistogram _loginLatency = new();
    private readonly MergeableHistogram _heartbeatLatency = new();
    private readonly MergeableHistogram _connectionScheduleLag = new();
    private long _expectedOnline;
    private long _connecting;
    private long _tcpConnected;
    private long _protocolOnline;
    private long _backoffWaiting;
    private long _connectionAttempts;
    private long _connectionSucceeded;
    private long _connectionFailed;
    private long _connectionTimeouts;
    private long _loginAttempts;
    private long _loginSucceeded;
    private long _loginFailed;
    private long _loginTimeouts;
    private long _heartbeatsSent;
    private long _heartbeatsConfirmed;
    private long _heartbeatTimeouts;
    private long _reportsSent;
    private long _framesReceived;
    private long _invalidFrames;
    private long _downstreamFrames;
    private long _remoteClosed;
    private long _writeTimeouts;
    private long _reconnectsScheduled;
    private long _permanentlyStopped;
    private long _bytesSent;
    private long _bytesReceived;
    private long _forcedDisconnects;
    private long _injectedOutboundDrops;
    private long _injectedInboundDrops;
    private long _injectedCorruptFrames;
    private long _injectedDuplicateFrames;
    private long _injectedFragmentedWrites;
    private long _downstreamResponsesSent;
    private bool _baselineReached;
    private DateTimeOffset? _outageStartedAt;
    private long _outageCount;
    private long? _lastRecoveryMilliseconds;

    /// <summary>
    /// 设置当前阶段预期在线设备数；阶段缩容时先更新目标，避免把正常下线误判为故障。
    /// </summary>
    public void SetExpectedOnline(int count, DateTimeOffset now)
    {
        var previous = Interlocked.Exchange(ref _expectedOnline, count);
        lock (_recoveryLock)
        {
            // 故障和恢复阶段通常保持同一目标值，此时必须延续停机窗口，不能在阶段切换时清零。
            if (previous == count)
                return;
            _baselineReached = count > 0 && Interlocked.Read(ref _protocolOnline) >= count;
            _outageStartedAt = null;
        }
    }

    /// <summary>记录一次进入 TCP 建连阶段。</summary>
    public void ConnectionStarted() { Interlocked.Increment(ref _connectionAttempts); Interlocked.Increment(ref _connecting); }

    /// <summary>记录 TCP 成功建立并保存建连延迟。</summary>
    public void ConnectionOpened(TimeSpan latency)
    {
        DecrementNonNegative(ref _connecting);
        Interlocked.Increment(ref _tcpConnected);
        Interlocked.Increment(ref _connectionSucceeded);
        _connectionLatency.Record(latency);
    }

    /// <summary>记录尚未建连成功的失败，并按是否超时分类。</summary>
    public void ConnectionFailed(bool timeout, string errorCode)
    {
        DecrementNonNegative(ref _connecting);
        Interlocked.Increment(ref _connectionFailed);
        if (timeout)
            Interlocked.Increment(ref _connectionTimeouts);
        RecordError(errorCode);
    }

    /// <summary>场景停止发生在建连途中时，只清理当前连接仪表而不计入失败。</summary>
    public void ConnectionCancelled() => DecrementNonNegative(ref _connecting);

    /// <summary>记录协议登录或地址绑定尝试。</summary>
    public void LoginStarted() => Interlocked.Increment(ref _loginAttempts);

    /// <summary>记录协议登录或地址绑定成功及其延迟。</summary>
    public void LoginCompleted(TimeSpan latency, DateTimeOffset now)
    {
        Interlocked.Increment(ref _loginSucceeded);
        var online = Interlocked.Increment(ref _protocolOnline);
        _loginLatency.Record(latency);
        lock (_recoveryLock)
        {
            var expected = Interlocked.Read(ref _expectedOnline);
            if (expected <= 0 || online < expected)
                return;
            if (!_baselineReached)
                _baselineReached = true;
            if (_outageStartedAt is not { } outageStartedAt)
                return;
            _lastRecoveryMilliseconds = Math.Max(0, (long)(now - outageStartedAt).TotalMilliseconds);
            _outageStartedAt = null;
        }
    }

    /// <summary>记录协议登录失败，并按是否超时分类。</summary>
    public void LoginFailed(bool timeout, string errorCode)
    {
        Interlocked.Increment(ref _loginFailed);
        if (timeout)
            Interlocked.Increment(ref _loginTimeouts);
        RecordError(errorCode);
    }

    /// <summary>记录一个已连接会话结束，并在基线已建立时启动恢复计时。</summary>
    public void ConnectionClosed(bool wasOnline, DateTimeOffset now)
    {
        DecrementNonNegative(ref _tcpConnected);
        if (!wasOnline)
            return;
        var online = DecrementNonNegative(ref _protocolOnline);
        lock (_recoveryLock)
        {
            var expected = Interlocked.Read(ref _expectedOnline);
            if (_baselineReached && expected > 0 && online < expected && _outageStartedAt is null)
            {
                _outageStartedAt = now;
                _outageCount++;
            }
        }
    }

    /// <summary>记录一次心跳写入。</summary>
    public void HeartbeatSent(int bytes) { Interlocked.Increment(ref _heartbeatsSent); Interlocked.Add(ref _bytesSent, bytes); }

    /// <summary>记录收到匹配心跳确认及其往返延迟。</summary>
    public void HeartbeatConfirmed(TimeSpan latency) { Interlocked.Increment(ref _heartbeatsConfirmed); _heartbeatLatency.Record(latency); }

    /// <summary>记录心跳确认超时。</summary>
    public void HeartbeatTimedOut() { Interlocked.Increment(ref _heartbeatTimeouts); RecordError("HEARTBEAT_TIMEOUT"); }

    /// <summary>记录周期上报写入。</summary>
    public void ReportSent(int bytes) { Interlocked.Increment(ref _reportsSent); Interlocked.Add(ref _bytesSent, bytes); }

    /// <summary>记录登录或退出报文写入字节数。</summary>
    public void BytesSent(int bytes) => Interlocked.Add(ref _bytesSent, bytes);

    /// <summary>记录网络读取和解析出的协议帧。</summary>
    public void BytesReceived(int bytes) => Interlocked.Add(ref _bytesReceived, bytes);

    /// <summary>记录一个已解析协议帧及其合法性。</summary>
    public void FrameReceived(bool valid)
    {
        Interlocked.Increment(ref _framesReceived);
        if (!valid)
            Interlocked.Increment(ref _invalidFrames);
    }

    /// <summary>记录未被当前登录或心跳请求消费的服务端下行帧。</summary>
    public void DownstreamFrame() => Interlocked.Increment(ref _downstreamFrames);

    /// <summary>记录远端正常关闭 TCP 流。</summary>
    public void RemoteClosed() { Interlocked.Increment(ref _remoteClosed); RecordError("REMOTE_CLOSED"); }

    /// <summary>记录 Socket 写入超时。</summary>
    public void WriteTimedOut() { Interlocked.Increment(ref _writeTimeouts); RecordError("WRITE_TIMEOUT"); }

    /// <summary>记录设备进入或离开重连退避状态。</summary>
    public void SetBackoff(bool active)
    {
        if (active)
            Interlocked.Increment(ref _backoffWaiting);
        else
            DecrementNonNegative(ref _backoffWaiting);
    }

    /// <summary>记录一次计划中的自动重连。</summary>
    public void ReconnectScheduled() => Interlocked.Increment(ref _reconnectsScheduled);

    /// <summary>记录设备达到最大重试次数后停止。</summary>
    public void PermanentlyStopped() => Interlocked.Increment(ref _permanentlyStopped);

    /// <summary>记录连接调度票据的实际延迟。</summary>
    public void ConnectionScheduleLag(TimeSpan latency) => _connectionScheduleLag.Record(latency);

    /// <summary>按稳定错误码累计运行时异常，报告不保存逐设备异常明细。</summary>
    public void RecordError(string errorCode) => _errors.AddOrUpdate(errorCode, 1, static (_, value) => value + 1);

    /// <summary>记录场景编排主动中断一个设备连接。</summary>
    public void ForcedDisconnect() => Interlocked.Increment(ref _forcedDisconnects);

    /// <summary>记录故障注入主动丢弃一个上行逻辑报文。</summary>
    public void InjectedOutboundDrop() => Interlocked.Increment(ref _injectedOutboundDrops);

    /// <summary>记录故障注入主动丢弃一个已解析下行帧。</summary>
    public void InjectedInboundDrop() => Interlocked.Increment(ref _injectedInboundDrops);

    /// <summary>记录一次校验和损坏注入。</summary>
    public void InjectedCorruptFrame() => Interlocked.Increment(ref _injectedCorruptFrames);

    /// <summary>记录一次把同一完整帧粘连发送两次的注入。</summary>
    public void InjectedDuplicateFrame() => Interlocked.Increment(ref _injectedDuplicateFrames);

    /// <summary>记录一次把完整报文拆分成多个 Socket 写入的注入。</summary>
    public void InjectedFragmentedWrite() => Interlocked.Increment(ref _injectedFragmentedWrites);

    /// <summary>记录虚拟设备对服务端下发报文写入协议应答。</summary>
    public void DownstreamResponseSent(int bytes)
    {
        Interlocked.Increment(ref _downstreamResponsesSent);
        Interlocked.Add(ref _bytesSent, bytes);
    }

    /// <summary>创建当前所有原子指标和恢复状态的一致近似快照。</summary>
    public MetricSnapshot Snapshot(DateTimeOffset now)
    {
        bool outageActive;
        long outageCount;
        long? lastRecovery;
        lock (_recoveryLock)
        {
            outageActive = _outageStartedAt is not null;
            outageCount = _outageCount;
            lastRecovery = _lastRecoveryMilliseconds;
        }

        return new MetricSnapshot
        {
            TimestampUtc = now,
            ExpectedOnline = Interlocked.Read(ref _expectedOnline),
            Connecting = Interlocked.Read(ref _connecting),
            TcpConnected = Interlocked.Read(ref _tcpConnected),
            ProtocolOnline = Interlocked.Read(ref _protocolOnline),
            BackoffWaiting = Interlocked.Read(ref _backoffWaiting),
            ConnectionAttempts = Interlocked.Read(ref _connectionAttempts),
            ConnectionSucceeded = Interlocked.Read(ref _connectionSucceeded),
            ConnectionFailed = Interlocked.Read(ref _connectionFailed),
            ConnectionTimeouts = Interlocked.Read(ref _connectionTimeouts),
            LoginAttempts = Interlocked.Read(ref _loginAttempts),
            LoginSucceeded = Interlocked.Read(ref _loginSucceeded),
            LoginFailed = Interlocked.Read(ref _loginFailed),
            LoginTimeouts = Interlocked.Read(ref _loginTimeouts),
            HeartbeatsSent = Interlocked.Read(ref _heartbeatsSent),
            HeartbeatsConfirmed = Interlocked.Read(ref _heartbeatsConfirmed),
            HeartbeatTimeouts = Interlocked.Read(ref _heartbeatTimeouts),
            ReportsSent = Interlocked.Read(ref _reportsSent),
            FramesReceived = Interlocked.Read(ref _framesReceived),
            InvalidFrames = Interlocked.Read(ref _invalidFrames),
            DownstreamFrames = Interlocked.Read(ref _downstreamFrames),
            RemoteClosed = Interlocked.Read(ref _remoteClosed),
            WriteTimeouts = Interlocked.Read(ref _writeTimeouts),
            ReconnectsScheduled = Interlocked.Read(ref _reconnectsScheduled),
            PermanentlyStopped = Interlocked.Read(ref _permanentlyStopped),
            BytesSent = Interlocked.Read(ref _bytesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            ForcedDisconnects = Interlocked.Read(ref _forcedDisconnects),
            InjectedOutboundDrops = Interlocked.Read(ref _injectedOutboundDrops),
            InjectedInboundDrops = Interlocked.Read(ref _injectedInboundDrops),
            InjectedCorruptFrames = Interlocked.Read(ref _injectedCorruptFrames),
            InjectedDuplicateFrames = Interlocked.Read(ref _injectedDuplicateFrames),
            InjectedFragmentedWrites = Interlocked.Read(ref _injectedFragmentedWrites),
            DownstreamResponsesSent = Interlocked.Read(ref _downstreamResponsesSent),
            OutageCount = outageCount,
            OutageActive = outageActive,
            LastRecoveryMilliseconds = lastRecovery,
            Errors = new Dictionary<string, long>(_errors, StringComparer.Ordinal),
            ConnectionLatency = _connectionLatency.Snapshot(),
            LoginLatency = _loginLatency.Snapshot(),
            HeartbeatLatency = _heartbeatLatency.Snapshot(),
            ConnectionScheduleLag = _connectionScheduleLag.Snapshot()
        };
    }

    private static long DecrementNonNegative(ref long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current <= 0)
                return 0;
            if (Interlocked.CompareExchange(ref value, current - 1, current) == current)
                return current - 1;
        }
    }
}
