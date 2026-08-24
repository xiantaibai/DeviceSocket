using System.Collections.Concurrent;
using DeviceSocket.LoadTester.Configuration;

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
    private readonly MergeableHistogram _apiRequestLatency = new();
    private readonly MergeableHistogram _gatherReadingResponseLatency = new();
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
    private long _apiRequestsStarted;
    private long _apiRequestsSucceeded;
    private long _apiRequestsFailed;
    private long _apiRequestTimeouts;
    private long _apiTargetsRequired;
    private long _apiTargetsOnline;
    private long _apiRequestsSkippedOffline;
    private long _gatherReadingCommandsReceived;
    private long _gatherReadingCommandsResponded;
    private long _gatherReadingResponsesSent;
    private long _gatherReadingResponsesDropped;
    private long _gatherReadingBusinessFailuresSent;
    private long _gatherReadingDuplicateResponsesSent;
    private bool _baselineReached;
    private readonly List<OutageEventSnapshot> _completedOutages = [];
    private ActiveOutage? _activeOutage;
    private long _outageCount;
    private long? _lastRecoveryMilliseconds;
    private long? _worstRecoveryMilliseconds;
    private DateTimeOffset? _stableOnlineStartedAt;
    private long _maximumStableOnlineMilliseconds;
    private string _currentPhaseName = string.Empty;
    private ScenarioPhaseKind _currentPhaseKind = ScenarioPhaseKind.Steady;
    private bool _currentPhaseExpectedServerRestart;
    private bool _expectedRestartOutageConsumed;
    private long _phaseMinimumProtocolOnline;
    private long _phaseMaximumProtocolOnline;

    /// <summary>
    /// 切换当前场景阶段并重置阶段在线数范围；正在进行的掉线事件仍保留其开始阶段，避免 fault 到 recovery 边界切断恢复窗口。
    /// </summary>
    /// <param name="name">用于报告定位的阶段名称。</param>
    /// <param name="kind">阶段业务类型。</param>
    /// <param name="expectedServerRestart">该阶段是否预期服务端进程发生计划重启。</param>
    public void SetCurrentPhase(string name, ScenarioPhaseKind kind, bool expectedServerRestart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_recoveryLock)
        {
            _currentPhaseName = name;
            _currentPhaseKind = kind;
            _currentPhaseExpectedServerRestart = expectedServerRestart;
            _expectedRestartOutageConsumed = false;
            var online = Interlocked.Read(ref _protocolOnline);
            _phaseMinimumProtocolOnline = online;
            _phaseMaximumProtocolOnline = online;
        }
    }

    /// <summary>
    /// 获取当前阶段实际观测到的最低和最高协议在线数。
    /// </summary>
    /// <returns>以 <c>Minimum</c> 和 <c>Maximum</c> 命名的在线数范围。</returns>
    public (long Minimum, long Maximum) GetCurrentPhaseOnlineRange()
    {
        lock (_recoveryLock)
            return (_phaseMinimumProtocolOnline, _phaseMaximumProtocolOnline);
    }

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
            UpdateMaximumStableOnline(now);
            _baselineReached = count > 0 && Interlocked.Read(ref _protocolOnline) >= count;
            _activeOutage = null;
            _stableOnlineStartedAt = _baselineReached ? now : null;
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
            _phaseMinimumProtocolOnline = Math.Min(_phaseMinimumProtocolOnline, online);
            _phaseMaximumProtocolOnline = Math.Max(_phaseMaximumProtocolOnline, online);
            if (expected <= 0 || online < expected)
                return;
            if (!_baselineReached)
                _baselineReached = true;
            if (_activeOutage is { } activeOutage)
            {
                var recovered = CreateOutageSnapshot(activeOutage, now, now);
                _completedOutages.Add(recovered);
                _lastRecoveryMilliseconds = recovered.DurationMilliseconds;
                _worstRecoveryMilliseconds = Math.Max(
                    _worstRecoveryMilliseconds ?? 0,
                    recovered.DurationMilliseconds);
                _activeOutage = null;
            }
            _stableOnlineStartedAt ??= now;
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
            _phaseMinimumProtocolOnline = Math.Min(_phaseMinimumProtocolOnline, online);
            _phaseMaximumProtocolOnline = Math.Max(_phaseMaximumProtocolOnline, online);
            if (_baselineReached && expected > 0 && online < expected)
            {
                if (_activeOutage is null)
                {
                    UpdateMaximumStableOnline(now);
                    _stableOnlineStartedAt = null;
                    _outageCount++;
                    var expectedRestartOutage = _currentPhaseExpectedServerRestart &&
                                                !_expectedRestartOutageConsumed;
                    if (expectedRestartOutage)
                        _expectedRestartOutageConsumed = true;
                    _activeOutage = new ActiveOutage(
                        _outageCount,
                        _currentPhaseName,
                        _currentPhaseKind,
                        expectedRestartOutage,
                        now,
                        expected,
                        online,
                        Interlocked.Read(ref _connectionAttempts),
                        Interlocked.Read(ref _connectionFailed),
                        Interlocked.Read(ref _loginFailed),
                        Interlocked.Read(ref _heartbeatTimeouts),
                        Interlocked.Read(ref _reconnectsScheduled),
                        _errors.GetValueOrDefault("SOCKET_AddressAlreadyInUse"));
                }
                else
                {
                    _activeOutage.MinimumProtocolOnline = Math.Min(
                        _activeOutage.MinimumProtocolOnline,
                        online);
                }
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

    /// <summary>记录 HTTP 指令驱动开始发送一个 API 请求。</summary>
    public void ApiRequestStarted() => Interlocked.Increment(ref _apiRequestsStarted);

    /// <summary>
    /// 记录 HTTP 请求完成、端到端延迟以及可聚合的稳定失败码。
    /// </summary>
    /// <param name="succeeded">HTTP 和业务 success 字段是否同时成功。</param>
    /// <param name="latency">从开始发送到读取完整响应体的耗时。</param>
    /// <param name="errorCode">失败时使用的稳定错误码；成功时为空。</param>
    public void ApiRequestCompleted(bool succeeded, TimeSpan latency, string? errorCode)
    {
        _apiRequestLatency.Record(latency);
        if (succeeded)
        {
            Interlocked.Increment(ref _apiRequestsSucceeded);
            return;
        }

        Interlocked.Increment(ref _apiRequestsFailed);
        RecordError(string.IsNullOrWhiteSpace(errorCode) ? "API_FAILED" : errorCode);
    }

    /// <summary>
    /// 记录 HTTP 请求超过客户端超时；超时同时计入 API 失败总数和延迟分布。
    /// </summary>
    /// <param name="latency">请求取消前的实际等待时长。</param>
    public void ApiRequestTimedOut(TimeSpan latency)
    {
        Interlocked.Increment(ref _apiRequestTimeouts);
        ApiRequestCompleted(false, latency, "API_TIMEOUT");
    }

    /// <summary>
    /// 记录一个 HTTP 指令阶段的目标设备数及阶段开始时其中在线设备数；onlineRegistry 之外的来源会写入零值。
    /// </summary>
    /// <param name="required">本分片计划覆盖的目标设备数。</param>
    /// <param name="online">本分片目标设备中当前在线的数量。</param>
    public void ApiTargetsObserved(int required, int online)
    {
        Interlocked.Add(ref _apiTargetsRequired, required);
        Interlocked.Add(ref _apiTargetsOnline, online);
    }

    /// <summary>
    /// 记录因阶段目标不足或发送前设备已经离线而主动跳过的 HTTP 读表请求数。
    /// </summary>
    /// <param name="count">本次跳过的计划请求数，默认一条；必须大于等于零。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 小于零。</exception>
    public void ApiRequestSkippedOffline(long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Interlocked.Add(ref _apiRequestsSkippedOffline, count);
    }

    /// <summary>记录虚拟设备收到一条 Gather AFN=0CH/F129 读表命令。</summary>
    public void GatherReadingCommandReceived() => Interlocked.Increment(ref _gatherReadingCommandsReceived);

    /// <summary>
    /// 记录一条 Gather 读表命令至少成功写出一次业务响应及其本地处理延迟。
    /// </summary>
    /// <param name="latency">从解析下行命令到首次业务响应写入完成的耗时。</param>
    public void GatherReadingCommandResponded(TimeSpan latency)
    {
        Interlocked.Increment(ref _gatherReadingCommandsResponded);
        _gatherReadingResponseLatency.Record(latency);
    }

    /// <summary>
    /// 记录单条 Gather 读表业务响应写入以及特殊响应类型。
    /// </summary>
    /// <param name="businessFailure">是否携带模拟业务失败数据。</param>
    /// <param name="duplicate">是否为同一命令的第二条重复响应。</param>
    public void GatherReadingResponseSent(bool businessFailure, bool duplicate)
    {
        Interlocked.Increment(ref _gatherReadingResponsesSent);
        if (businessFailure)
            Interlocked.Increment(ref _gatherReadingBusinessFailuresSent);
        if (duplicate)
            Interlocked.Increment(ref _gatherReadingDuplicateResponsesSent);
    }

    /// <summary>记录响应模型有意丢弃一条 Gather 读表命令。</summary>
    public void GatherReadingResponseDropped() => Interlocked.Increment(ref _gatherReadingResponsesDropped);

    /// <summary>创建当前所有原子指标和恢复状态的一致近似快照。</summary>
    /// <param name="now">快照对应的 UTC 时间。</param>
    /// <returns>可序列化、可跨节点合并的指标快照。</returns>
    public MetricSnapshot Snapshot(DateTimeOffset now)
    {
        bool outageActive;
        long outageCount;
        long? lastRecovery;
        long? worstRecovery;
        long currentStableOnline;
        long maximumStableOnline;
        string currentPhaseName;
        IReadOnlyList<OutageEventSnapshot> outages;
        lock (_recoveryLock)
        {
            outageActive = _activeOutage is not null;
            outageCount = _outageCount;
            lastRecovery = _lastRecoveryMilliseconds;
            worstRecovery = _worstRecoveryMilliseconds;
            currentStableOnline = _stableOnlineStartedAt is { } stableStartedAt
                ? Math.Max(0, (long)(now - stableStartedAt).TotalMilliseconds)
                : 0;
            maximumStableOnline = Math.Max(_maximumStableOnlineMilliseconds, currentStableOnline);
            currentPhaseName = _currentPhaseName;
            var eventSnapshots = new List<OutageEventSnapshot>(_completedOutages);
            if (_activeOutage is { } activeOutage)
                eventSnapshots.Add(CreateOutageSnapshot(activeOutage, now, recoveredAt: null));
            outages = eventSnapshots;
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
            ApiRequestsStarted = Interlocked.Read(ref _apiRequestsStarted),
            ApiRequestsSucceeded = Interlocked.Read(ref _apiRequestsSucceeded),
            ApiRequestsFailed = Interlocked.Read(ref _apiRequestsFailed),
            ApiRequestTimeouts = Interlocked.Read(ref _apiRequestTimeouts),
            ApiTargetsRequired = Interlocked.Read(ref _apiTargetsRequired),
            ApiTargetsOnline = Interlocked.Read(ref _apiTargetsOnline),
            ApiRequestsSkippedOffline = Interlocked.Read(ref _apiRequestsSkippedOffline),
            GatherReadingCommandsReceived = Interlocked.Read(ref _gatherReadingCommandsReceived),
            GatherReadingCommandsResponded = Interlocked.Read(ref _gatherReadingCommandsResponded),
            GatherReadingResponsesSent = Interlocked.Read(ref _gatherReadingResponsesSent),
            GatherReadingResponsesDropped = Interlocked.Read(ref _gatherReadingResponsesDropped),
            GatherReadingBusinessFailuresSent = Interlocked.Read(ref _gatherReadingBusinessFailuresSent),
            GatherReadingDuplicateResponsesSent = Interlocked.Read(ref _gatherReadingDuplicateResponsesSent),
            OutageCount = outageCount,
            OutageActive = outageActive,
            LastRecoveryMilliseconds = lastRecovery,
            Outages = outages,
            WorstRecoveryMilliseconds = worstRecovery,
            CurrentStableOnlineMilliseconds = currentStableOnline,
            MaximumStableOnlineMilliseconds = maximumStableOnline,
            CurrentPhaseName = currentPhaseName,
            Errors = new Dictionary<string, long>(_errors, StringComparer.Ordinal),
            ConnectionLatency = _connectionLatency.Snapshot(),
            LoginLatency = _loginLatency.Snapshot(),
            HeartbeatLatency = _heartbeatLatency.Snapshot(),
            ConnectionScheduleLag = _connectionScheduleLag.Snapshot(),
            ApiRequestLatency = _apiRequestLatency.Snapshot(),
            GatherReadingResponseLatency = _gatherReadingResponseLatency.Snapshot()
        };
    }

    /// <summary>
    /// 把当前完整在线区间计入历史最大稳定时长。
    /// </summary>
    /// <param name="now">稳定区间结束或目标切换的 UTC 时间。</param>
    private void UpdateMaximumStableOnline(DateTimeOffset now)
    {
        if (_stableOnlineStartedAt is not { } stableStartedAt)
            return;
        _maximumStableOnlineMilliseconds = Math.Max(
            _maximumStableOnlineMilliseconds,
            Math.Max(0, (long)(now - stableStartedAt).TotalMilliseconds));
    }

    /// <summary>
    /// 根据掉线开始基线和当前累计计数创建可序列化事件快照。
    /// </summary>
    /// <param name="active">正在进行的掉线事件。</param>
    /// <param name="now">用于未恢复事件计算当前持续时间的快照时间。</param>
    /// <param name="recoveredAt">实际恢复时间；为空表示事件仍在进行。</param>
    /// <returns>包含持续时间、最低在线和错误增量的不可变事件。</returns>
    private OutageEventSnapshot CreateOutageSnapshot(
        ActiveOutage active,
        DateTimeOffset now,
        DateTimeOffset? recoveredAt)
    {
        var endedAt = recoveredAt ?? now;
        return new OutageEventSnapshot
        {
            Sequence = active.Sequence,
            PhaseName = active.PhaseName,
            PhaseKind = active.PhaseKind,
            ExpectedServerRestart = active.ExpectedServerRestart,
            StartedAtUtc = active.StartedAtUtc,
            RecoveredAtUtc = recoveredAt,
            DurationMilliseconds = Math.Max(0, (long)(endedAt - active.StartedAtUtc).TotalMilliseconds),
            ExpectedOnline = active.ExpectedOnline,
            MinimumProtocolOnline = active.MinimumProtocolOnline,
            MaximumOfflineDevices = Math.Max(0, active.ExpectedOnline - active.MinimumProtocolOnline),
            ConnectionAttempts = Delta(active.ConnectionAttempts, Interlocked.Read(ref _connectionAttempts)),
            ConnectionFailures = Delta(active.ConnectionFailures, Interlocked.Read(ref _connectionFailed)),
            LoginFailures = Delta(active.LoginFailures, Interlocked.Read(ref _loginFailed)),
            HeartbeatTimeouts = Delta(active.HeartbeatTimeouts, Interlocked.Read(ref _heartbeatTimeouts)),
            ReconnectsScheduled = Delta(active.ReconnectsScheduled, Interlocked.Read(ref _reconnectsScheduled)),
            AddressAlreadyInUse = Delta(
                active.AddressAlreadyInUse,
                _errors.GetValueOrDefault("SOCKET_AddressAlreadyInUse"))
        };
    }

    /// <summary>
    /// 计算累计计数的非负增量。
    /// </summary>
    /// <param name="started">事件开始值。</param>
    /// <param name="ended">当前或事件结束值。</param>
    /// <returns>非负差值。</returns>
    private static long Delta(long started, long ended) => Math.Max(0, ended - started);

    /// <summary>
    /// 以原子方式递减瞬时仪表但不允许低于零，容忍取消和关闭回调的竞争顺序。
    /// </summary>
    /// <param name="value">要递减的原子长整数。</param>
    /// <returns>成功递减后的值；原值已为零时返回零。</returns>
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

    /// <summary>
    /// 保存掉线事件开始时的阶段身份、最低在线数和累计计数基线，恢复时转换为不可变快照。
    /// </summary>
    private sealed class ActiveOutage(
        long sequence,
        string phaseName,
        ScenarioPhaseKind phaseKind,
        bool expectedServerRestart,
        DateTimeOffset startedAtUtc,
        long expectedOnline,
        long minimumProtocolOnline,
        long connectionAttempts,
        long connectionFailures,
        long loginFailures,
        long heartbeatTimeouts,
        long reconnectsScheduled,
        long addressAlreadyInUse)
    {
        /// <summary>获取事件编号。</summary>
        internal long Sequence { get; } = sequence;

        /// <summary>获取事件开始阶段名称。</summary>
        internal string PhaseName { get; } = phaseName;

        /// <summary>获取事件开始阶段类型。</summary>
        internal ScenarioPhaseKind PhaseKind { get; } = phaseKind;

        /// <summary>获取事件是否属于计划重启阶段。</summary>
        internal bool ExpectedServerRestart { get; } = expectedServerRestart;

        /// <summary>获取事件开始时间。</summary>
        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

        /// <summary>获取事件目标在线数。</summary>
        internal long ExpectedOnline { get; } = expectedOnline;

        /// <summary>获取或设置事件期间最低在线数。</summary>
        internal long MinimumProtocolOnline { get; set; } = minimumProtocolOnline;

        /// <summary>获取事件开始时连接尝试累计值。</summary>
        internal long ConnectionAttempts { get; } = connectionAttempts;

        /// <summary>获取事件开始时连接失败累计值。</summary>
        internal long ConnectionFailures { get; } = connectionFailures;

        /// <summary>获取事件开始时登录失败累计值。</summary>
        internal long LoginFailures { get; } = loginFailures;

        /// <summary>获取事件开始时心跳超时累计值。</summary>
        internal long HeartbeatTimeouts { get; } = heartbeatTimeouts;

        /// <summary>获取事件开始时重连累计值。</summary>
        internal long ReconnectsScheduled { get; } = reconnectsScheduled;

        /// <summary>获取事件开始时端口占用错误累计值。</summary>
        internal long AddressAlreadyInUse { get; } = addressAlreadyInUse;
    }
}
