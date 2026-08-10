using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 原子限制 Gateway 总连接、未认证连接、单 IP 连接和接入速率，并跟踪已占用配额的 Session。
/// </summary>
public sealed class DeviceConnectionLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Admission> _admittedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _connectionsByRemoteAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceConnectionOptions _options;
    private int _connectionCount;
    private int _unauthenticatedConnectionCount;
    private long _rejectedConnections;
    private long _capacityRejections;
    private long _unauthenticatedCapacityRejections;
    private long _perIpCapacityRejections;
    private long _rateLimitRejections;
    private long _rateWindowSecond = -1;
    private int _acceptedInRateWindow;

    /// <summary>
    /// 从强类型配置读取 Gateway 的全部 TCP 接入限制。
    /// </summary>
    /// <param name="options">已经完成启动校验的连接配置。</param>
    public DeviceConnectionLimiter(IOptions<DeviceConnectionOptions> options)
        : this(options.Value)
    {
    }

    /// <summary>
    /// 使用指定总连接上限创建配额，其他保护保持关闭，主要用于并发单元测试。
    /// </summary>
    /// <param name="maxConnections">允许同时接入的最大设备连接数。</param>
    public DeviceConnectionLimiter(int maxConnections)
        : this(new DeviceConnectionOptions
        {
            MaxConnections = maxConnections,
            MaxUnauthenticatedConnections = maxConnections,
            MaxConnectionsPerIp = 0,
            MaxConnectionsPerSecond = 0
        })
    {
    }

    /// <summary>
    /// 使用完整连接治理参数创建配额。
    /// </summary>
    /// <param name="options">连接容量和速率配置。</param>
    public DeviceConnectionLimiter(DeviceConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "最大设备连接数必须大于 0。");
        if (options.MaxUnauthenticatedConnections <= 0 ||
            options.MaxUnauthenticatedConnections > options.MaxConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "最大未认证连接数必须大于 0 且不能超过总连接数。");
        }

        _options = options;
    }

    /// <summary>允许同时接入的最大设备连接数。</summary>
    public int MaxConnections => _options.MaxConnections;

    /// <summary>当前是否启用单远端 IP 并发连接限制；关闭时接入路径不生成或保存 IP 统计键。</summary>
    public bool PerIpLimitEnabled => _options.MaxConnectionsPerIp > 0;

    /// <summary>当前已占用配额的设备连接数。</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>当前尚未完成协议登录或设备绑定的连接数。</summary>
    public int UnauthenticatedConnectionCount => Volatile.Read(ref _unauthenticatedConnectionCount);

    /// <summary>进程启动以来因任一接入保护规则被拒绝的累计连接数。</summary>
    public long RejectedConnections => Interlocked.Read(ref _rejectedConnections);

    /// <summary>因达到 Gateway 总连接上限被拒绝的累计连接数。</summary>
    public long CapacityRejections => Interlocked.Read(ref _capacityRejections);

    /// <summary>因达到未认证连接上限被拒绝的累计连接数。</summary>
    public long UnauthenticatedCapacityRejections => Interlocked.Read(ref _unauthenticatedCapacityRejections);

    /// <summary>因达到单 IP 并发连接上限被拒绝的累计连接数。</summary>
    public long PerIpCapacityRejections => Interlocked.Read(ref _perIpCapacityRejections);

    /// <summary>因达到每秒接入速率上限被拒绝的累计连接数。</summary>
    public long RateLimitRejections => Interlocked.Read(ref _rateLimitRejections);

    /// <summary>
    /// 尝试为新 Session 预留连接配额；该兼容重载不参与单 IP 统计。
    /// </summary>
    /// <param name="sessionId">SuperSocket Session 唯一标识。</param>
    /// <param name="connectionCount">操作完成后的已接入连接数。</param>
    /// <param name="rejectedConnections">操作完成后的累计拒绝数。</param>
    /// <returns>成功预留或该 Session 已预留时返回 true。</returns>
    public bool TryAcquire(
        string sessionId,
        out int connectionCount,
        out long rejectedConnections) =>
        TryAcquire(
            sessionId,
            null,
            DateTimeOffset.UtcNow,
            out connectionCount,
            out rejectedConnections,
            out _);

    /// <summary>
    /// 按总量、未认证容量、单 IP 和当前秒接入速率依次检查并原子预留连接配额。
    /// </summary>
    /// <param name="sessionId">SuperSocket Session 唯一标识。</param>
    /// <param name="remoteAddress">已经归一化的远端 IP；为空时跳过单 IP 限制。</param>
    /// <param name="now">用于固定窗口速率统计的当前时间。</param>
    /// <param name="connectionCount">操作完成后的已接入连接数。</param>
    /// <param name="rejectedConnections">操作完成后的累计拒绝数。</param>
    /// <param name="rejectionReason">拒绝原因；成功时为 <see cref="DeviceConnectionRejectionReason.None"/>。</param>
    /// <returns>成功预留或该 Session 已预留时返回 true。</returns>
    public bool TryAcquire(
        string sessionId,
        string? remoteAddress,
        DateTimeOffset now,
        out int connectionCount,
        out long rejectedConnections,
        out DeviceConnectionRejectionReason rejectionReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var trackedRemoteAddress = PerIpLimitEnabled ? remoteAddress : null;
        lock (_gate)
        {
            if (_admittedSessions.ContainsKey(sessionId))
            {
                connectionCount = _connectionCount;
                rejectedConnections = _rejectedConnections;
                rejectionReason = DeviceConnectionRejectionReason.None;
                return true;
            }

            if (_connectionCount >= _options.MaxConnections)
                return Reject(DeviceConnectionRejectionReason.Capacity, out connectionCount, out rejectedConnections, out rejectionReason);
            if (_unauthenticatedConnectionCount >= _options.MaxUnauthenticatedConnections)
                return Reject(DeviceConnectionRejectionReason.UnauthenticatedCapacity, out connectionCount, out rejectedConnections, out rejectionReason);
            if (_options.MaxConnectionsPerIp > 0 &&
                trackedRemoteAddress is not null &&
                _connectionsByRemoteAddress.GetValueOrDefault(trackedRemoteAddress) >= _options.MaxConnectionsPerIp)
            {
                return Reject(DeviceConnectionRejectionReason.PerIpCapacity, out connectionCount, out rejectedConnections, out rejectionReason);
            }

            var currentSecond = now.ToUnixTimeSeconds();
            if (_rateWindowSecond != currentSecond)
            {
                _rateWindowSecond = currentSecond;
                _acceptedInRateWindow = 0;
            }
            if (_options.MaxConnectionsPerSecond > 0 &&
                _acceptedInRateWindow >= _options.MaxConnectionsPerSecond)
            {
                return Reject(DeviceConnectionRejectionReason.RateLimit, out connectionCount, out rejectedConnections, out rejectionReason);
            }

            _admittedSessions.Add(sessionId, new Admission(trackedRemoteAddress));
            _connectionCount++;
            _unauthenticatedConnectionCount++;
            _acceptedInRateWindow++;
            if (trackedRemoteAddress is not null)
            {
                _connectionsByRemoteAddress[trackedRemoteAddress] =
                    _connectionsByRemoteAddress.GetValueOrDefault(trackedRemoteAddress) + 1;
            }

            connectionCount = _connectionCount;
            rejectedConnections = _rejectedConnections;
            rejectionReason = DeviceConnectionRejectionReason.None;
            return true;
        }
    }

    /// <summary>
    /// 将已占用配额的 Session 标记为认证完成，未认证计数只减少一次。
    /// </summary>
    /// <param name="sessionId">完成协议登录或设备绑定的 Session 编号。</param>
    /// <returns>本次是否首次切换为已认证状态。</returns>
    public bool MarkAuthenticated(string sessionId)
    {
        lock (_gate)
        {
            if (!_admittedSessions.TryGetValue(sessionId, out var admission) || admission.IsAuthenticated)
                return false;

            admission.IsAuthenticated = true;
            _unauthenticatedConnectionCount--;
            return true;
        }
    }

    /// <summary>
    /// 释放已接入 Session 的总量、未认证和单 IP 配额；拒绝连接或重复关闭不会改变计数。
    /// </summary>
    /// <param name="sessionId">SuperSocket Session 唯一标识。</param>
    /// <param name="connectionCount">释放后的已接入连接数。</param>
    /// <returns>本次是否实际释放了连接配额。</returns>
    public bool Release(string sessionId, out int connectionCount)
    {
        lock (_gate)
        {
            if (!_admittedSessions.Remove(sessionId, out var admission))
            {
                connectionCount = _connectionCount;
                return false;
            }

            _connectionCount--;
            if (!admission.IsAuthenticated)
                _unauthenticatedConnectionCount--;
            if (admission.RemoteAddress is not null &&
                _connectionsByRemoteAddress.TryGetValue(admission.RemoteAddress, out var addressCount))
            {
                if (addressCount <= 1)
                    _connectionsByRemoteAddress.Remove(admission.RemoteAddress);
                else
                    _connectionsByRemoteAddress[admission.RemoteAddress] = addressCount - 1;
            }

            connectionCount = _connectionCount;
            return true;
        }
    }

    /// <summary>
    /// 记录一次连接拒绝并返回当前容量快照。
    /// </summary>
    /// <param name="reason">本次接入被拒绝的具体规则。</param>
    /// <param name="connectionCount">拒绝发生时的已接入连接数。</param>
    /// <param name="rejectedConnections">更新后的累计拒绝数。</param>
    /// <param name="rejectionReason">返回给调用方的拒绝原因。</param>
    /// <returns>始终返回 false，便于接入检查直接返回。</returns>
    private bool Reject(
        DeviceConnectionRejectionReason reason,
        out int connectionCount,
        out long rejectedConnections,
        out DeviceConnectionRejectionReason rejectionReason)
    {
        rejectedConnections = ++_rejectedConnections;
        connectionCount = _connectionCount;
        rejectionReason = reason;
        switch (reason)
        {
            case DeviceConnectionRejectionReason.Capacity:
                _capacityRejections++;
                break;
            case DeviceConnectionRejectionReason.UnauthenticatedCapacity:
                _unauthenticatedCapacityRejections++;
                break;
            case DeviceConnectionRejectionReason.PerIpCapacity:
                _perIpCapacityRejections++;
                break;
            case DeviceConnectionRejectionReason.RateLimit:
                _rateLimitRejections++;
                break;
        }

        return false;
    }

    /// <summary>保存单个已接入 Session 的远端地址和认证配额状态。</summary>
    /// <param name="remoteAddress">启用单 IP 限制时保存的归一化地址；限制关闭时为空。</param>
    private sealed class Admission(string? remoteAddress)
    {
        public string? RemoteAddress { get; } = remoteAddress;
        public bool IsAuthenticated { get; set; }
    }
}
