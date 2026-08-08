using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

internal static class MetricSnapshotAggregator
{
    internal static MetricSnapshot Merge(IEnumerable<MetricSnapshot> snapshots)
    {
        var source = snapshots.ToArray();
        if (source.Length == 0)
            throw new ArgumentException("至少需要一个指标快照。", nameof(snapshots));

        var errors = source
            .SelectMany(item => item.Errors)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.Ordinal);
        return new MetricSnapshot
        {
            TimestampUtc = source.Max(item => item.TimestampUtc),
            ExpectedOnline = source.Sum(item => item.ExpectedOnline),
            Connecting = source.Sum(item => item.Connecting),
            TcpConnected = source.Sum(item => item.TcpConnected),
            ProtocolOnline = source.Sum(item => item.ProtocolOnline),
            BackoffWaiting = source.Sum(item => item.BackoffWaiting),
            ConnectionAttempts = source.Sum(item => item.ConnectionAttempts),
            ConnectionSucceeded = source.Sum(item => item.ConnectionSucceeded),
            ConnectionFailed = source.Sum(item => item.ConnectionFailed),
            ConnectionTimeouts = source.Sum(item => item.ConnectionTimeouts),
            LoginAttempts = source.Sum(item => item.LoginAttempts),
            LoginSucceeded = source.Sum(item => item.LoginSucceeded),
            LoginFailed = source.Sum(item => item.LoginFailed),
            LoginTimeouts = source.Sum(item => item.LoginTimeouts),
            HeartbeatsSent = source.Sum(item => item.HeartbeatsSent),
            HeartbeatsConfirmed = source.Sum(item => item.HeartbeatsConfirmed),
            HeartbeatTimeouts = source.Sum(item => item.HeartbeatTimeouts),
            ReportsSent = source.Sum(item => item.ReportsSent),
            FramesReceived = source.Sum(item => item.FramesReceived),
            InvalidFrames = source.Sum(item => item.InvalidFrames),
            DownstreamFrames = source.Sum(item => item.DownstreamFrames),
            RemoteClosed = source.Sum(item => item.RemoteClosed),
            WriteTimeouts = source.Sum(item => item.WriteTimeouts),
            ReconnectsScheduled = source.Sum(item => item.ReconnectsScheduled),
            PermanentlyStopped = source.Sum(item => item.PermanentlyStopped),
            BytesSent = source.Sum(item => item.BytesSent),
            BytesReceived = source.Sum(item => item.BytesReceived),
            ForcedDisconnects = source.Sum(item => item.ForcedDisconnects),
            InjectedOutboundDrops = source.Sum(item => item.InjectedOutboundDrops),
            InjectedInboundDrops = source.Sum(item => item.InjectedInboundDrops),
            InjectedCorruptFrames = source.Sum(item => item.InjectedCorruptFrames),
            InjectedDuplicateFrames = source.Sum(item => item.InjectedDuplicateFrames),
            InjectedFragmentedWrites = source.Sum(item => item.InjectedFragmentedWrites),
            DownstreamResponsesSent = source.Sum(item => item.DownstreamResponsesSent),
            OutageCount = source.Sum(item => item.OutageCount),
            OutageActive = source.Any(item => item.OutageActive),
            LastRecoveryMilliseconds = source.Where(item => item.LastRecoveryMilliseconds.HasValue)
                .Select(item => item.LastRecoveryMilliseconds)
                .DefaultIfEmpty(null)
                .Max(),
            Errors = errors,
            ConnectionLatency = MergeableHistogram.Merge(source.Select(item => item.ConnectionLatency)),
            LoginLatency = MergeableHistogram.Merge(source.Select(item => item.LoginLatency)),
            HeartbeatLatency = MergeableHistogram.Merge(source.Select(item => item.HeartbeatLatency)),
            ConnectionScheduleLag = MergeableHistogram.Merge(source.Select(item => item.ConnectionScheduleLag))
        };
    }
}
