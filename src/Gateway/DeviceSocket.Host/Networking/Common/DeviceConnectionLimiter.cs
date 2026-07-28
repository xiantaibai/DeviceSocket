using System.Collections.Concurrent;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 使用原子计数和 Session 集合限制当前 Gateway 的设备 TCP 连接总量。
/// </summary>
public sealed class DeviceConnectionLimiter
{
    private const string MaxConnectionsKey = "DeviceSocket:ConnectionLimit:MaxConnections";
    private readonly ConcurrentDictionary<string, byte> _admittedSessions = new(StringComparer.Ordinal);
    private int _connectionCount;
    private long _rejectedConnections;

    /// <summary>
    /// 从应用配置读取 Gateway 总连接上限。
    /// </summary>
    /// <param name="configuration">Gateway 应用配置。</param>
    public DeviceConnectionLimiter(IConfiguration configuration)
        : this(configuration.GetValue<int?>(MaxConnectionsKey) ?? 70_000)
    {
    }

    /// <summary>
    /// 使用指定连接上限创建配额，主要用于不依赖宿主配置的并发测试。
    /// </summary>
    /// <param name="maxConnections">允许同时接入的最大设备连接数。</param>
    public DeviceConnectionLimiter(int maxConnections)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections), "最大设备连接数必须大于 0。");

        MaxConnections = maxConnections;
    }

    /// <summary>允许同时接入的最大设备连接数。</summary>
    public int MaxConnections { get; }

    /// <summary>当前已占用配额的设备连接数。</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>进程启动以来因超过上限而被拒绝的累计连接数。</summary>
    public long RejectedConnections => Interlocked.Read(ref _rejectedConnections);

    /// <summary>
    /// 尝试为新 Session 原子预留一个连接配额，达到上限时返回 false。
    /// </summary>
    /// <param name="sessionId">SuperSocket Session 唯一标识。</param>
    /// <param name="connectionCount">操作完成后的已接入连接数。</param>
    /// <param name="rejectedConnections">操作完成后的累计拒绝数。</param>
    /// <returns>成功预留或该 Session 已预留时返回 true；容量不足时返回 false。</returns>
    public bool TryAcquire(string sessionId, out int connectionCount, out long rejectedConnections)
    {
        if (_admittedSessions.ContainsKey(sessionId))
        {
            connectionCount = ConnectionCount;
            rejectedConnections = RejectedConnections;
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _connectionCount);
            if (current >= MaxConnections)
            {
                connectionCount = current;
                rejectedConnections = Interlocked.Increment(ref _rejectedConnections);
                return false;
            }

            if (Interlocked.CompareExchange(ref _connectionCount, current + 1, current) != current)
                continue;

            if (_admittedSessions.TryAdd(sessionId, 0))
            {
                connectionCount = current + 1;
                rejectedConnections = RejectedConnections;
                return true;
            }

            // 极端情况下同一 Session 被并发通知两次，撤销重复占用，但仍视为已接入。
            Interlocked.Decrement(ref _connectionCount);
            connectionCount = ConnectionCount;
            rejectedConnections = RejectedConnections;
            return true;
        }
    }

    /// <summary>
    /// 释放已接入 Session 的连接配额；拒绝连接或重复关闭不会减少计数。
    /// </summary>
    /// <param name="sessionId">SuperSocket Session 唯一标识。</param>
    /// <param name="connectionCount">释放后的已接入连接数。</param>
    /// <returns>本次是否实际释放了连接配额。</returns>
    public bool Release(string sessionId, out int connectionCount)
    {
        if (!_admittedSessions.TryRemove(sessionId, out _))
        {
            connectionCount = ConnectionCount;
            return false;
        }

        connectionCount = Interlocked.Decrement(ref _connectionCount);
        return true;
    }
}
