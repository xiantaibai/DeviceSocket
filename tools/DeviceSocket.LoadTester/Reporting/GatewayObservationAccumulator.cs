namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 在线程安全的采样窗口内累计 Gateway 容量、资源、发送和日志健康峰值。
/// </summary>
internal sealed class GatewayObservationAccumulator
{
    private readonly object _lock = new();
    private long _samples;
    private long _unavailableSamples;
    private long? _baselineTcpConnections;
    private long? _baselineAuthenticatedDevices;
    private long? _baselineApplicationLogDropped;
    private long? _baselineUpstreamLogDropped;
    private long? _baselineDownstreamLogDropped;
    private long? _baselineHeartbeatLogSuppressed;
    private long _maximumTcpConnections;
    private long _maximumAuthenticatedDevices;
    private long _maximumAdditionalTcpConnections;
    private long _maximumAdditionalAuthenticatedDevices;
    private double _maximumProcessCpuPercent;
    private long _maximumProcessWorkingSetBytes;
    private long _maximumManagedHeapBytes;
    private long _maximumThreadPoolPendingWorkItems;
    private long _maximumPeakPendingSendMessages;
    private long _maximumPeakPendingSendBytes;
    private long _maximumSessionScanDurationMilliseconds;
    private long _additionalApplicationLogDropped;
    private long _additionalUpstreamLogDropped;
    private long _additionalDownstreamLogDropped;
    private long _additionalHeartbeatLogSuppressed;
    private GatewayStatusSnapshot? _last;

    /// <summary>
    /// 保存负载开始前的连接和累计计数基线，并把本次状态计入采样统计。
    /// </summary>
    /// <param name="snapshot">负载开始前的 Gateway 状态。</param>
    internal void SetBaseline(GatewayStatusSnapshot snapshot)
    {
        lock (_lock)
        {
            if (snapshot.Available)
            {
                _baselineTcpConnections = snapshot.TcpConnections;
                _baselineAuthenticatedDevices = snapshot.AuthenticatedDevices;
                _baselineApplicationLogDropped = snapshot.ApplicationLogDropped;
                _baselineUpstreamLogDropped = snapshot.UpstreamLogDropped;
                _baselineDownstreamLogDropped = snapshot.DownstreamLogDropped;
                _baselineHeartbeatLogSuppressed = snapshot.HeartbeatLogSuppressed;
            }

            RecordCore(snapshot);
        }
    }

    /// <summary>
    /// 记录一次测试期间的 Gateway 状态采样。
    /// </summary>
    /// <param name="snapshot">本次 Gateway 状态。</param>
    internal void Record(GatewayStatusSnapshot snapshot)
    {
        lock (_lock)
            RecordCore(snapshot);
    }

    /// <summary>
    /// 生成当前累计状态的不可变汇总快照。
    /// </summary>
    /// <returns>用于节点报告的 Gateway 观测汇总。</returns>
    internal GatewayObservationSummary Snapshot()
    {
        lock (_lock)
        {
            return new GatewayObservationSummary(
                _samples,
                _unavailableSamples,
                _baselineTcpConnections,
                _baselineAuthenticatedDevices,
                _maximumTcpConnections,
                _maximumAuthenticatedDevices,
                _maximumAdditionalTcpConnections,
                _maximumAdditionalAuthenticatedDevices,
                _last)
            {
                MaximumProcessCpuPercent = _maximumProcessCpuPercent,
                MaximumProcessWorkingSetBytes = _maximumProcessWorkingSetBytes,
                MaximumManagedHeapBytes = _maximumManagedHeapBytes,
                MaximumThreadPoolPendingWorkItems = _maximumThreadPoolPendingWorkItems,
                MaximumPeakPendingSendMessages = _maximumPeakPendingSendMessages,
                MaximumPeakPendingSendBytes = _maximumPeakPendingSendBytes,
                MaximumSessionScanDurationMilliseconds = _maximumSessionScanDurationMilliseconds,
                AdditionalApplicationLogDropped = _additionalApplicationLogDropped,
                AdditionalUpstreamLogDropped = _additionalUpstreamLogDropped,
                AdditionalDownstreamLogDropped = _additionalDownstreamLogDropped,
                AdditionalHeartbeatLogSuppressed = _additionalHeartbeatLogSuppressed
            };
        }
    }

    /// <summary>
    /// 在调用方持有采样锁时更新峰值和相对基线增量。
    /// </summary>
    /// <param name="snapshot">本次 Gateway 状态。</param>
    private void RecordCore(GatewayStatusSnapshot snapshot)
    {
        _samples++;
        if (!snapshot.Available)
            _unavailableSamples++;
        _maximumTcpConnections = Math.Max(_maximumTcpConnections, snapshot.TcpConnections);
        _maximumAuthenticatedDevices = Math.Max(_maximumAuthenticatedDevices, snapshot.AuthenticatedDevices);
        _maximumProcessCpuPercent = Math.Max(_maximumProcessCpuPercent, snapshot.ProcessCpuPercent);
        _maximumProcessWorkingSetBytes = Math.Max(_maximumProcessWorkingSetBytes, snapshot.ProcessWorkingSetBytes);
        _maximumManagedHeapBytes = Math.Max(_maximumManagedHeapBytes, snapshot.ManagedHeapBytes);
        _maximumThreadPoolPendingWorkItems = Math.Max(_maximumThreadPoolPendingWorkItems, snapshot.ThreadPoolPendingWorkItems);
        _maximumPeakPendingSendMessages = Math.Max(_maximumPeakPendingSendMessages, snapshot.PeakPendingSendMessages);
        _maximumPeakPendingSendBytes = Math.Max(_maximumPeakPendingSendBytes, snapshot.PeakPendingSendBytes);
        _maximumSessionScanDurationMilliseconds = Math.Max(
            _maximumSessionScanDurationMilliseconds,
            snapshot.MaximumSessionScanDurationMilliseconds);

        // 容量验收只统计本轮测试相对启动前的增量，避免线上已有设备污染重复登录和容量上限结论。
        if (snapshot.Available &&
            _baselineTcpConnections.HasValue &&
            _baselineAuthenticatedDevices.HasValue)
        {
            _maximumAdditionalTcpConnections = Math.Max(
                _maximumAdditionalTcpConnections,
                Math.Max(0, snapshot.TcpConnections - _baselineTcpConnections.Value));
            _maximumAdditionalAuthenticatedDevices = Math.Max(
                _maximumAdditionalAuthenticatedDevices,
                Math.Max(0, snapshot.AuthenticatedDevices - _baselineAuthenticatedDevices.Value));
            _additionalApplicationLogDropped = Math.Max(
                _additionalApplicationLogDropped,
                Math.Max(0, snapshot.ApplicationLogDropped - _baselineApplicationLogDropped.GetValueOrDefault()));
            _additionalUpstreamLogDropped = Math.Max(
                _additionalUpstreamLogDropped,
                Math.Max(0, snapshot.UpstreamLogDropped - _baselineUpstreamLogDropped.GetValueOrDefault()));
            _additionalDownstreamLogDropped = Math.Max(
                _additionalDownstreamLogDropped,
                Math.Max(0, snapshot.DownstreamLogDropped - _baselineDownstreamLogDropped.GetValueOrDefault()));
            _additionalHeartbeatLogSuppressed = Math.Max(
                _additionalHeartbeatLogSuppressed,
                Math.Max(0, snapshot.HeartbeatLogSuppressed - _baselineHeartbeatLogSuppressed.GetValueOrDefault()));
        }

        _last = snapshot;
    }
}
