using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>由单个会话接收循环独占的近期命令观察器，最多保留 64 个指纹，断线重建以隔离 Session 代次。</summary>
internal sealed class GatherV4SessionCommandObserver
{
    private readonly Dictionary<string, Observation> _recent = new(StringComparer.Ordinal);
    private readonly GatherV4CommandMetrics _metrics;
    private readonly TimeSpan _window;
    private DateTimeOffset? _lastConfirmed;
    private string? _lastConfirmedFingerprint;

    /// <summary>创建仅在设备收到业务命令后分配的有界观察器。</summary>
    /// <param name="metrics">当前节点共享聚合指标。</param>
    /// <param name="seconds">疑似重发观察窗口，单位秒。</param>
    internal GatherV4SessionCommandObserver(GatherV4CommandMetrics metrics, int seconds)
    { _metrics = metrics; _window = TimeSpan.FromSeconds(seconds); }

    /// <summary>记录业务下行并识别近期同帧重复；相同帧无法区分新业务任务，报告使用“疑似”口径。</summary>
    /// <param name="fingerprint">包含地址、A3/PSEQ、AFN/PN/FN 及数据的完整报文指纹。</param>
    /// <param name="now">接收时间。</param>
    internal void Received(string fingerprint, DateTimeOffset now)
    {
        foreach (var key in _recent.Where(pair => now - pair.Value.ReceivedAt > _window).Select(pair => pair.Key).ToArray())
            _recent.Remove(key);
        var repeated = _recent.TryGetValue(fingerprint, out var previous);
        _metrics.Received(repeated, previous?.Confirmed == true);
        if (!repeated)
        {
            if (_recent.Count >= 64)
                _recent.Remove(_recent.MinBy(pair => pair.Value.ReceivedAt).Key);
            _recent[fingerprint] = new Observation(now, false);
            if (_lastConfirmed is { } confirmed && fingerprint != _lastConfirmedFingerprint)
            {
                _metrics.Following(now - confirmed);
                _lastConfirmed = null;
            }
        }
    }

    /// <summary>记录已成功写出的正确确认；重复确认不会重置该条命令的首次到达时间。</summary>
    /// <param name="fingerprint">被回复命令指纹。</param>
    /// <param name="receivedAt">本次下行到达时间。</param>
    /// <param name="now">写出完成时间。</param>
    internal void Confirmed(string fingerprint, DateTimeOffset receivedAt, DateTimeOffset now)
    {
        _metrics.Confirmed(now - receivedAt);
        if (_recent.TryGetValue(fingerprint, out var previous))
            _recent[fingerprint] = previous with { Confirmed = true };
        _lastConfirmed = now;
        _lastConfirmedFingerprint = fingerprint;
    }

    /// <summary>保存一个近期报文的到达时间与正确确认标志。</summary>
    /// <param name="ReceivedAt">首次到达时间。</param>
    /// <param name="Confirmed">是否已正确写出 F1。</param>
    private sealed record Observation(DateTimeOffset ReceivedAt, bool Confirmed);
}
