using System.Collections.Concurrent;
using DeviceSocket.Core.Runtime.Connections;

namespace DeviceSocket.Infrastructure.Memory.Connections;

/// <summary>
/// 使用并发字典保存本节点在线设备与 Session 的双向索引。
/// </summary>
public sealed class MemoryOnlineDeviceRegistry : IOnlineDeviceRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _byDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _bySession = new(StringComparer.Ordinal);

    public int Count => _byDevice.Count;

    /// <summary>
    /// 原子注册设备；相同设备重复登录时由新连接替换旧连接。
    /// </summary>
    public DeviceRegistration Register(string deviceId, IDeviceConnection connection, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(connection);

        var current = new Entry(deviceId, connection, now);
        IDeviceConnection? replaced = null;

        // 使用 CAS 风格的 TryUpdate 循环，避免并发登录时覆盖更新丢失。
        while (true)
        {
            if (_byDevice.TryGetValue(deviceId, out var existing))
            {
                if (string.Equals(existing.Connection.SessionId, connection.SessionId, StringComparison.Ordinal))
                {
                    existing.Touch(now);
                    // 再次确认字典中的引用未被并发登录替换，避免把已经失效的旧 Session 当作当前连接返回。
                    if (_byDevice.TryGetValue(deviceId, out var latest) && ReferenceEquals(existing, latest))
                        return new DeviceRegistration(existing.ToSnapshot(), null, false);

                    continue;
                }

                if (!_byDevice.TryUpdate(deviceId, current, existing))
                    continue;

                if (!string.Equals(existing.Connection.SessionId, connection.SessionId, StringComparison.Ordinal))
                {
                    _bySession.TryRemove(existing.Connection.SessionId, out _);
                    replaced = existing.Connection;
                }

                break;
            }

            if (_byDevice.TryAdd(deviceId, current))
                break;
        }

        _bySession[connection.SessionId] = deviceId;
        return new DeviceRegistration(current.ToSnapshot(), replaced, true);
    }

    /// <summary>
    /// 仅刷新当前有效 Session，旧连接迟到的心跳不会抢占新连接。
    /// </summary>
    public bool Touch(string deviceId, string sessionId, DateTimeOffset now)
    {
        if (!_byDevice.TryGetValue(deviceId, out var entry) ||
            !string.Equals(entry.Connection.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        entry.Touch(now);
        return true;
    }

    /// <summary>
    /// 根据设备编号取得当前可用于下发的连接。
    /// </summary>
    public bool TryGetConnection(string deviceId, out IDeviceConnection? connection)
    {
        if (_byDevice.TryGetValue(deviceId, out var entry))
        {
            connection = entry.Connection;
            return true;
        }

        connection = null;
        return false;
    }

    /// <summary>
    /// 获取设备当前连接的只读快照，供 RPC 路由校验使用。
    /// </summary>
    public bool TryGetSnapshot(string deviceId, out DeviceConnectionSnapshot? snapshot)
    {
        if (_byDevice.TryGetValue(deviceId, out var entry))
        {
            snapshot = entry.ToSnapshot();
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>
    /// 条件更新会话世代号，旧登录请求迟到时不能覆盖新连接的世代号。
    /// </summary>
    public bool TryUpdateSessionEpoch(string deviceId, string sessionId, long sessionEpoch)
    {
        if (!_byDevice.TryGetValue(deviceId, out var entry) ||
            !string.Equals(entry.Connection.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        entry.UpdateSessionEpoch(sessionEpoch);
        return true;
    }

    /// <summary>
    /// 条件删除指定 Session；只有它仍是设备当前连接时才删除设备索引。
    /// </summary>
    public bool RemoveSession(string sessionId) => RemoveSession(sessionId, out _);

    /// <summary>
    /// 条件删除指定 Session，并返回用于通知集群路由失效的设备快照。
    /// </summary>
    public bool RemoveSession(string sessionId, out DeviceConnectionSnapshot? removed)
    {
        removed = null;
        if (!_bySession.TryRemove(sessionId, out var deviceId) ||
            !_byDevice.TryGetValue(deviceId, out var entry) ||
            !string.Equals(entry.Connection.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        // ICollection.Remove 会同时比较键和值，可避免检查后被新登录替换造成的竞态误删。
        var success = ((ICollection<KeyValuePair<string, Entry>>)_byDevice)
            .Remove(new KeyValuePair<string, Entry>(deviceId, entry));
        if (success)
            removed = entry.ToSnapshot();
        return success;
    }

    /// <summary>
    /// 返回有限数量的在线设备快照，避免管理查询遍历并序列化全部连接。
    /// </summary>
    public IReadOnlyList<DeviceConnectionSnapshot> GetSnapshot(int take = 100)
    {
        take = Math.Clamp(take, 1, 1000);
        return _byDevice.Values
            .OrderByDescending(entry => entry.LastSeenTicks)
            .Take(take)
            .Select(entry => entry.ToSnapshot())
            .ToArray();
    }

    private sealed class Entry(string deviceId, IDeviceConnection connection, DateTimeOffset connectedAt)
    {
        private long _lastSeenTicks = connectedAt.UtcTicks;
        private long _sessionEpoch;

        public string DeviceId { get; } = deviceId;
        public IDeviceConnection Connection { get; } = connection;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
        public long LastSeenTicks => Interlocked.Read(ref _lastSeenTicks);
        public long SessionEpoch => Interlocked.Read(ref _sessionEpoch);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

        public void UpdateSessionEpoch(long sessionEpoch) => Interlocked.Exchange(ref _sessionEpoch, sessionEpoch);

        public DeviceConnectionSnapshot ToSnapshot() => new(
            DeviceId,
            Connection.SessionId,
            Connection.Protocol,
            Connection.RemoteEndPoint,
            ConnectedAt,
            new DateTimeOffset(LastSeenTicks, TimeSpan.Zero),
            SessionEpoch);
    }
}
