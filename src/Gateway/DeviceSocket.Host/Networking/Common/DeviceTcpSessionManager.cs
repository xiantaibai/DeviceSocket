using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Configuration.Logging.Messages;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 集中跟踪全部设备 TCP Session，并提供认证状态、合法活动、超时回收和有界超时发送能力。
/// </summary>
/// <param name="connectionLimiter">负责同步认证状态的 Gateway 连接配额管理器。</param>
/// <param name="options">已经完成启动校验的 TCP 连接配置。</param>
/// <param name="logger">记录主动关闭异常的程序日志器。</param>
public sealed class DeviceTcpSessionManager(
    DeviceConnectionLimiter connectionLimiter,
    IOptions<DeviceConnectionOptions> options,
    ILogger<DeviceTcpSessionManager> logger)
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly DeviceConnectionOptions _options = options.Value;
    private long _authenticatedSessions;
    private long _pendingSendMessages;
    private long _pendingSendBytes;
    private long _peakPendingSendMessages;
    private long _peakPendingSendBytes;
    private long _loginTimeoutClosures;
    private long _idleTimeoutClosures;
    private long _protocolErrorClosures;
    private long _duplicateLoginClosures;
    private long _logoutClosures;
    private long _sendTimeoutClosures;
    private long _sendBackpressureClosures;
    private long _staleSessionClosures;
    private long _sessionScanCount;
    private long _lastSessionScanAtTicks;
    private long _lastSessionScanDurationMilliseconds;
    private long _maximumSessionScanDurationMilliseconds;
    private int _lastSessionScanSessions;
    private int _lastSessionScanCandidates;
    private int _lastSessionScanClosed;

    /// <summary>当前由管理器跟踪的全部已接入 TCP Session 数量。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>当前已经完成协议登录或设备绑定的 Session 数量。</summary>
    public long AuthenticatedSessionCount => Interlocked.Read(ref _authenticatedSessions);

    /// <summary>当前尚未完成协议登录或设备绑定的 Session 数量。</summary>
    public long UnauthenticatedSessionCount => Math.Max(0, SessionCount - AuthenticatedSessionCount);

    /// <summary>当前正在等待或写入 Socket 的消息数量。</summary>
    public long PendingSendMessages => Interlocked.Read(ref _pendingSendMessages);

    /// <summary>当前正在等待或写入 Socket 的消息字节总数。</summary>
    public long PendingSendBytes => Interlocked.Read(ref _pendingSendBytes);

    /// <summary>进程启动以来同时等待或写入 Socket 的峰值消息数量。</summary>
    public long PeakPendingSendMessages => Interlocked.Read(ref _peakPendingSendMessages);

    /// <summary>进程启动以来同时等待或写入 Socket 的峰值字节数量。</summary>
    public long PeakPendingSendBytes => Interlocked.Read(ref _peakPendingSendBytes);

    /// <summary>进程启动以来因登录超时主动关闭的连接数。</summary>
    public long LoginTimeoutClosures => Interlocked.Read(ref _loginTimeoutClosures);

    /// <summary>进程启动以来因合法报文空闲超时主动关闭的连接数。</summary>
    public long IdleTimeoutClosures => Interlocked.Read(ref _idleTimeoutClosures);

    /// <summary>进程启动以来因连续非法报文主动关闭的连接数。</summary>
    public long ProtocolErrorClosures => Interlocked.Read(ref _protocolErrorClosures);

    /// <summary>进程启动以来因重复登录替换主动关闭的旧连接数。</summary>
    public long DuplicateLoginClosures => Interlocked.Read(ref _duplicateLoginClosures);

    /// <summary>进程启动以来处理退出登录后主动关闭的连接数。</summary>
    public long LogoutClosures => Interlocked.Read(ref _logoutClosures);

    /// <summary>进程启动以来因下发超时主动关闭的连接数。</summary>
    public long SendTimeoutClosures => Interlocked.Read(ref _sendTimeoutClosures);

    /// <summary>进程启动以来因发送背压上限主动关闭的连接数。</summary>
    public long SendBackpressureClosures => Interlocked.Read(ref _sendBackpressureClosures);

    /// <summary>进程启动以来因未登录或旧 Session 继续通信而主动关闭的连接数。</summary>
    public long StaleSessionClosures => Interlocked.Read(ref _staleSessionClosures);

    /// <summary>进程启动以来完成的协议层 Session 扫描次数。</summary>
    public long SessionScanCount => Interlocked.Read(ref _sessionScanCount);

    /// <summary>最近一轮协议层 Session 扫描的 UTC 时间；尚未扫描时为空。</summary>
    public DateTimeOffset? LastSessionScanAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSessionScanAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>最近一轮协议层 Session 扫描耗时毫秒数。</summary>
    public long LastSessionScanDurationMilliseconds => Interlocked.Read(ref _lastSessionScanDurationMilliseconds);

    /// <summary>进程启动以来协议层 Session 扫描的最大耗时毫秒数。</summary>
    public long MaximumSessionScanDurationMilliseconds => Interlocked.Read(ref _maximumSessionScanDurationMilliseconds);

    /// <summary>最近一轮扫描开始时管理器跟踪的 Session 数量。</summary>
    public int LastSessionScanSessions => Volatile.Read(ref _lastSessionScanSessions);

    /// <summary>最近一轮扫描初步识别出的超时候选数量。</summary>
    public int LastSessionScanCandidates => Volatile.Read(ref _lastSessionScanCandidates);

    /// <summary>最近一轮扫描重新确认后实际发起关闭的 Session 数量。</summary>
    public int LastSessionScanClosed => Volatile.Read(ref _lastSessionScanClosed);

    /// <summary>
    /// 在 Session 打开回调中登记连接；重复打开通知只保留首次状态。
    /// </summary>
    /// <param name="session">可控制的设备 TCP Session。</param>
    /// <param name="protocol">监听端口对应的设备协议。</param>
    /// <param name="connectedAt">Session 建立时间。</param>
    /// <returns>本次是否新增了 Session。</returns>
    public bool RegisterSession(IDeviceSessionControl session, DeviceProtocol protocol, DateTimeOffset connectedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryAdd(session.SessionId, new SessionEntry(session, protocol, connectedAt));
    }

    /// <summary>
    /// 在 Session 关闭回调中移除跟踪状态；认证计数只会释放一次。
    /// </summary>
    /// <param name="sessionId">已经关闭的 Session 编号。</param>
    /// <returns>本次是否实际移除了 Session。</returns>
    public bool UnregisterSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
            return false;

        // 远端断开和官方中间件关闭不会预先经过 CloseSessionAsync；移出字典后立即封闭生命周期，
        // 让已经取得 Entry 引用的迟到登录或心跳也无法重新激活已关闭连接。
        entry.TryBeginClose();
        if (entry.IsAuthenticated)
            Interlocked.Decrement(ref _authenticatedSessions);
        return true;
    }

    /// <summary>
    /// 将仍然存活的 Session 标记为已认证，并同步释放未认证连接配额。
    /// </summary>
    /// <param name="sessionId">完成协议登录或设备绑定的 Session 编号。</param>
    /// <param name="deviceId">当前 Session 绑定的设备编号。</param>
    /// <param name="authenticatedAt">完成认证的时间。</param>
    /// <returns>Session 仍可用且认证状态已生效时返回 true。</returns>
    public bool TryAuthenticate(string sessionId, string deviceId, DateTimeOffset authenticatedAt)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.IsClosing)
            return false;

        var authentication = entry.Authenticate(deviceId, authenticatedAt);
        if (authentication is AuthenticationResult.Conflict or AuthenticationResult.Unavailable)
            return false;
        if (authentication == AuthenticationResult.First)
        {
            Interlocked.Increment(ref _authenticatedSessions);
            connectionLimiter.MarkAuthenticated(sessionId);
        }

        return !entry.IsClosing;
    }

    /// <summary>
    /// 判断 Session 是否仍被跟踪且尚未进入主动关闭流程。
    /// </summary>
    /// <param name="sessionId">需要检查的 Session 编号。</param>
    public bool IsActive(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && !entry.IsClosing;

    /// <summary>
    /// 合法协议帧到达时刷新活动时间并清零连续协议错误计数。
    /// </summary>
    /// <param name="sessionId">收到报文的 Session 编号。</param>
    /// <param name="seenAt">合法帧完成解析的时间。</param>
    public void RecordValidMessage(string sessionId, DateTimeOffset seenAt)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            entry.RecordValidMessage(seenAt);
    }

    /// <summary>
    /// 记录一条由 SuperSocket 官方空闲清理中间件关闭的 Session。
    /// </summary>
    /// <param name="sessionId">被官方中间件判定为空闲的 Session 编号。</param>
    public void RecordSuperSocketIdleTimeout(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry) && !entry.IsClosing)
            Interlocked.Increment(ref _idleTimeoutClosures);
    }

    /// <summary>
    /// 记录一条非法协议帧；达到连续错误阈值时关闭当前连接。
    /// </summary>
    /// <param name="sessionId">收到非法帧的 Session 编号。</param>
    /// <returns>是否因达到阈值而发起了主动关闭。</returns>
    public async ValueTask<bool> RecordProtocolErrorAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) ||
            entry.IncrementProtocolErrors() < _options.MaxConsecutiveProtocolErrors)
        {
            return false;
        }

        return await CloseSessionAsync(sessionId, DeviceSessionCloseReason.ProtocolErrorLimit);
    }

    /// <summary>
    /// 对指定 Session 执行有界且带超时的 Socket 写入；同一连接的写入顺序由 SuperSocket 内部发送锁保证。
    /// </summary>
    /// <param name="sessionId">目标 Session 编号。</param>
    /// <param name="message">已经完成协议编码的二进制帧。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <exception cref="IOException">Session 不存在、正在关闭或达到发送背压上限时抛出。</exception>
    /// <exception cref="TimeoutException">等待 SuperSocket 发送锁或写入 Pipe 超时时抛出。</exception>
    public async ValueTask SendAsync(
        string sessionId,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.IsClosing)
            throw new IOException($"设备 Session {sessionId} 已关闭或不存在。");
        if (!TryReserveSend(entry, message.Length))
        {
            await CloseSessionAsync(sessionId, DeviceSessionCloseReason.SendBackpressure);
            throw new IOException($"设备 Session {sessionId} 的发送队列已达到有界背压上限。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));
        try
        {
            if (entry.IsClosing)
                throw new IOException($"设备 Session {sessionId} 正在关闭。");
            await entry.Session.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseSessionAsync(sessionId, DeviceSessionCloseReason.SendTimeout);
            throw new TimeoutException($"设备 Session {sessionId} 在 {_options.SendTimeoutSeconds} 秒内未完成发送。");
        }
        finally
        {
            ReleaseSend(entry, message.Length);
        }
    }

    /// <summary>
    /// 幂等发起主动关闭；关闭完成后的字典和配额清理由 SessionClosed 回调统一执行。
    /// </summary>
    /// <param name="sessionId">目标 Session 编号。</param>
    /// <param name="reason">Gateway 判定的主动关闭原因。</param>
    /// <returns>本次是否首次发起关闭。</returns>
    public async ValueTask<bool> CloseSessionAsync(string sessionId, DeviceSessionCloseReason reason)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.TryBeginClose())
            return false;

        return await CloseClaimedSessionAsync(entry, reason);
    }

    /// <summary>
    /// 集中扫描登录和合法报文空闲超时的连接，并以有限并发关闭，避免大批断线冲击线程池。
    /// </summary>
    /// <param name="now">本轮扫描使用的统一 UTC 时间。</param>
    /// <param name="cancellationToken">停止扫描和关闭操作的取消令牌。</param>
    /// <returns>本轮重新确认后实际发起关闭的超时 Session 数量。</returns>
    public async Task<int> CloseExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var scanStarted = Stopwatch.GetTimestamp();
        var scannedSessions = _sessions.Count;
        var loginTimeout = TimeSpan.FromSeconds(_options.LoginTimeoutSeconds);
        var idleTimeout = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
        var expired = new List<ExpiredSession>();
        foreach (var entry in _sessions.Values)
        {
            if (entry.IsClosing)
                continue;

            if (entry.IsAuthenticated)
            {
                if (now - entry.LastValidAt >= idleTimeout)
                    expired.Add(new ExpiredSession(entry, DeviceSessionCloseReason.IdleTimeout));
            }
            else if (now - entry.ConnectedAt >= loginTimeout)
            {
                expired.Add(new ExpiredSession(entry, DeviceSessionCloseReason.LoginTimeout));
            }
        }

        var closed = 0;
        try
        {
            await Parallel.ForEachAsync(
                expired,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.MaxConcurrentSessionCloses
                },
                async (item, _) =>
                {
                    var entry = item.Entry;
                    var sessionId = entry.Session.SessionId;
                    if (!_sessions.TryGetValue(sessionId, out var current) ||
                        !ReferenceEquals(current, entry) ||
                        !entry.TryBeginExpiredClose(now, loginTimeout, idleTimeout, item.Reason))
                    {
                        return;
                    }

                    if (await CloseClaimedSessionAsync(entry, item.Reason))
                        Interlocked.Increment(ref closed);
                });
            return closed;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(scanStarted);
            RecordSessionScan(now, scannedSessions, expired.Count, Volatile.Read(ref closed), elapsed);
        }
    }

    /// <summary>
    /// 为一次发送预留单 Session 和 Gateway 全局背压容量。
    /// </summary>
    /// <param name="entry">目标 Session 状态。</param>
    /// <param name="messageBytes">本次发送的字节数。</param>
    /// <returns>两个层级的容量均足够时返回 true。</returns>
    private bool TryReserveSend(SessionEntry entry, int messageBytes)
    {
        if (!entry.TryReserveSend(
                messageBytes,
                _options.MaxPendingSendMessagesPerSession,
                _options.MaxPendingSendBytesPerSession))
        {
            return false;
        }

        var totalBytes = Interlocked.Add(ref _pendingSendBytes, messageBytes);
        if (totalBytes > _options.MaxPendingSendBytes)
        {
            Interlocked.Add(ref _pendingSendBytes, -messageBytes);
            entry.ReleaseSend(messageBytes);
            return false;
        }

        var pendingMessages = Interlocked.Increment(ref _pendingSendMessages);
        UpdateMaximum(ref _peakPendingSendMessages, pendingMessages);
        UpdateMaximum(ref _peakPendingSendBytes, totalBytes);
        return true;
    }

    /// <summary>
    /// 在发送结束或失败后释放单 Session 和 Gateway 全局背压容量。
    /// </summary>
    /// <param name="entry">目标 Session 状态。</param>
    /// <param name="messageBytes">本次发送已经预留的字节数。</param>
    private void ReleaseSend(SessionEntry entry, int messageBytes)
    {
        entry.ReleaseSend(messageBytes);
        Interlocked.Decrement(ref _pendingSendMessages);
        Interlocked.Add(ref _pendingSendBytes, -messageBytes);
    }

    /// <summary>
    /// 对已经成功认领关闭权的 Session 执行底层关闭，并记录对应关闭原因。
    /// </summary>
    /// <param name="entry">已经进入 Closing 状态的 Session。</param>
    /// <param name="reason">Gateway 判定的主动关闭原因。</param>
    /// <returns>底层关闭调用是否成功完成。</returns>
    private async ValueTask<bool> CloseClaimedSessionAsync(SessionEntry entry, DeviceSessionCloseReason reason)
    {
        try
        {
            await entry.Session.CloseAsync(reason);
            RecordClosure(reason);
        }
        catch (Exception exception)
        {
            // CloseAsync 明确失败时允许后续扫描重试，避免永久保留无法再次回收的 Closing Session。
            entry.CancelClose();
            try
            {
                TcpLogMessages.SessionCloseFailed(logger, entry.Session.SessionId, reason, exception);
            }
            catch
            {
                // 日志提供程序异常不能覆盖底层关闭失败语义，Session 仍保持可重试状态。
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按主动关闭原因累加连接治理指标。
    /// </summary>
    /// <param name="reason">已经成功发起的主动关闭原因。</param>
    private void RecordClosure(DeviceSessionCloseReason reason)
    {
        switch (reason)
        {
            case DeviceSessionCloseReason.LoginTimeout:
                Interlocked.Increment(ref _loginTimeoutClosures);
                break;
            case DeviceSessionCloseReason.IdleTimeout:
                Interlocked.Increment(ref _idleTimeoutClosures);
                break;
            case DeviceSessionCloseReason.ProtocolErrorLimit:
                Interlocked.Increment(ref _protocolErrorClosures);
                break;
            case DeviceSessionCloseReason.DuplicateLogin:
                Interlocked.Increment(ref _duplicateLoginClosures);
                break;
            case DeviceSessionCloseReason.Logout:
                Interlocked.Increment(ref _logoutClosures);
                break;
            case DeviceSessionCloseReason.SendTimeout:
                Interlocked.Increment(ref _sendTimeoutClosures);
                break;
            case DeviceSessionCloseReason.SendBackpressure:
                Interlocked.Increment(ref _sendBackpressureClosures);
                break;
            case DeviceSessionCloseReason.StaleSession:
                Interlocked.Increment(ref _staleSessionClosures);
                break;
        }
    }

    /// <summary>
    /// 原子发布一轮 Session 扫描结果，供状态接口和 Linux 长稳压测读取。
    /// </summary>
    /// <param name="scanAt">本轮扫描使用的 UTC 时间。</param>
    /// <param name="scannedSessions">扫描开始时的 Session 数量。</param>
    /// <param name="candidates">初步识别出的超时候选数量。</param>
    /// <param name="closed">重新确认后实际发起关闭的数量。</param>
    /// <param name="elapsed">本轮扫描总耗时。</param>
    private void RecordSessionScan(
        DateTimeOffset scanAt,
        int scannedSessions,
        int candidates,
        int closed,
        TimeSpan elapsed)
    {
        var elapsedMilliseconds = Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds));
        Volatile.Write(ref _lastSessionScanSessions, scannedSessions);
        Volatile.Write(ref _lastSessionScanCandidates, candidates);
        Volatile.Write(ref _lastSessionScanClosed, closed);
        Interlocked.Exchange(ref _lastSessionScanDurationMilliseconds, elapsedMilliseconds);
        UpdateMaximum(ref _maximumSessionScanDurationMilliseconds, elapsedMilliseconds);
        Interlocked.Exchange(ref _lastSessionScanAtTicks, scanAt.UtcTicks);
        Interlocked.Increment(ref _sessionScanCount);
    }

    /// <summary>
    /// 使用无锁比较交换把目标值提升到观察到的最大值。
    /// </summary>
    /// <param name="target">保存历史最大值的字段。</param>
    /// <param name="candidate">本次观察值。</param>
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

    /// <summary>表示一条由本轮集中扫描初步识别出的超时 Session。</summary>
    /// <param name="Entry">扫描时命中的 Session 状态引用，用于避免 SessionId 复用误关新连接。</param>
    /// <param name="Reason">扫描时判定的超时类型。</param>
    private readonly record struct ExpiredSession(SessionEntry Entry, DeviceSessionCloseReason Reason);

    /// <summary>表示 Session 设备身份认证是首次、重复相同身份、身份冲突或 Session 已不可用。</summary>
    private enum AuthenticationResult
    {
        /// <summary>首次完成设备身份绑定。</summary>
        First,

        /// <summary>重复提交与当前 Session 相同的设备身份。</summary>
        Existing,

        /// <summary>同一 Session 尝试切换到另一个设备身份。</summary>
        Conflict,

        /// <summary>Session 已进入关闭流程，不能继续认证。</summary>
        Unavailable
    }

    /// <summary>保存单个 TCP Session 的轻量活动、认证、关闭和发送背压状态。</summary>
    /// <param name="session">可执行发送和关闭的底层 Session。</param>
    /// <param name="protocol">当前监听端口对应的设备协议。</param>
    /// <param name="connectedAt">Session 建立时间。</param>
    private sealed class SessionEntry(
        IDeviceSessionControl session,
        DeviceProtocol protocol,
        DateTimeOffset connectedAt)
    {
        private readonly object _lifecycleLock = new();
        private readonly object _sendReservationLock = new();
        private long _lastValidTicks = connectedAt.UtcTicks;
        private int _authenticated;
        private int _closing;
        private int _consecutiveProtocolErrors;
        private int _pendingSendMessages;
        private int _pendingSendBytes;

        public IDeviceSessionControl Session { get; } = session;
        public DeviceProtocol Protocol { get; } = protocol;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
        public DateTimeOffset LastValidAt => new(Interlocked.Read(ref _lastValidTicks), TimeSpan.Zero);
        public bool IsAuthenticated => Volatile.Read(ref _authenticated) != 0;
        public bool IsClosing => Volatile.Read(ref _closing) != 0;
        public string? DeviceId { get; private set; }
        /// <summary>
        /// 在 Session 尚未关闭时完成首次认证，重复认证只允许使用相同设备编号。
        /// </summary>
        /// <param name="deviceId">准备绑定的设备编号。</param>
        /// <param name="authenticatedAt">完成认证的时间。</param>
        /// <returns>认证状态变化结果；Session 已进入关闭流程时返回不可用。</returns>
        public AuthenticationResult Authenticate(string deviceId, DateTimeOffset authenticatedAt)
        {
            lock (_lifecycleLock)
            {
                if (IsClosing)
                    return AuthenticationResult.Unavailable;
                if (IsAuthenticated)
                {
                    return string.Equals(DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                        ? AuthenticationResult.Existing
                        : AuthenticationResult.Conflict;
                }

                DeviceId = deviceId;
                RecordValidMessage(authenticatedAt);
                Volatile.Write(ref _authenticated, 1);
                return AuthenticationResult.First;
            }
        }

        /// <summary>
        /// 在 Session 尚未关闭时刷新最后合法报文时间并清零连续协议错误数。
        /// </summary>
        /// <param name="seenAt">合法帧完成解析的时间。</param>
        public void RecordValidMessage(DateTimeOffset seenAt)
        {
            lock (_lifecycleLock)
            {
                if (IsClosing)
                    return;

                Interlocked.Exchange(ref _lastValidTicks, seenAt.UtcTicks);
                Interlocked.Exchange(ref _consecutiveProtocolErrors, 0);
            }
        }

        /// <summary>
        /// 累加当前 Session 的连续非法协议帧数量。
        /// </summary>
        /// <returns>累加后的连续错误数。</returns>
        public int IncrementProtocolErrors() => Interlocked.Increment(ref _consecutiveProtocolErrors);

        /// <summary>
        /// 幂等认领当前 Session 的主动关闭权。
        /// </summary>
        /// <returns>本次是否把 Session 首次切换为 Closing 状态。</returns>
        public bool TryBeginClose()
        {
            lock (_lifecycleLock)
            {
                if (IsClosing)
                    return false;

                Volatile.Write(ref _closing, 1);
                return true;
            }
        }

        /// <summary>
        /// 在底层关闭明确失败时撤销 Closing 状态，允许后续治理流程重试。
        /// </summary>
        public void CancelClose()
        {
            lock (_lifecycleLock)
                Volatile.Write(ref _closing, 0);
        }

        /// <summary>
        /// 在生命周期锁内重新确认扫描结果并认领关闭权，避免迟到登录或心跳被旧扫描结果误关。
        /// </summary>
        /// <param name="now">本轮扫描使用的统一 UTC 时间。</param>
        /// <param name="loginTimeout">未认证 Session 的登录期限。</param>
        /// <param name="idleTimeout">已认证 Session 的合法报文空闲期限。</param>
        /// <param name="reason">初次扫描判定的超时类型。</param>
        /// <returns>Session 仍符合相同超时条件且成功进入 Closing 状态时返回 true。</returns>
        public bool TryBeginExpiredClose(
            DateTimeOffset now,
            TimeSpan loginTimeout,
            TimeSpan idleTimeout,
            DeviceSessionCloseReason reason)
        {
            lock (_lifecycleLock)
            {
                if (IsClosing)
                    return false;

                var stillExpired = reason switch
                {
                    DeviceSessionCloseReason.LoginTimeout =>
                        !IsAuthenticated && now - ConnectedAt >= loginTimeout,
                    DeviceSessionCloseReason.IdleTimeout =>
                        IsAuthenticated && now - LastValidAt >= idleTimeout,
                    _ => false
                };
                if (!stillExpired)
                    return false;

                Volatile.Write(ref _closing, 1);
                return true;
            }
        }

        /// <summary>
        /// 尝试为单个 Session 预留一条待发送消息及其字节容量。
        /// </summary>
        /// <param name="messageBytes">本次发送的字节数。</param>
        /// <param name="maximumMessages">单 Session 待发送消息上限。</param>
        /// <param name="maximumBytes">单 Session 待发送字节上限。</param>
        /// <returns>容量足够且预留成功时返回 true。</returns>
        public bool TryReserveSend(int messageBytes, int maximumMessages, int maximumBytes)
        {
            lock (_sendReservationLock)
            {
                if (_pendingSendMessages >= maximumMessages ||
                    _pendingSendBytes > maximumBytes - messageBytes)
                {
                    return false;
                }

                _pendingSendMessages++;
                _pendingSendBytes += messageBytes;
                return true;
            }
        }

        /// <summary>
        /// 释放已经预留的单 Session 待发送消息及字节容量。
        /// </summary>
        /// <param name="messageBytes">本次发送原先预留的字节数。</param>
        public void ReleaseSend(int messageBytes)
        {
            lock (_sendReservationLock)
            {
                _pendingSendMessages--;
                _pendingSendBytes -= messageBytes;
            }
        }
    }
}
