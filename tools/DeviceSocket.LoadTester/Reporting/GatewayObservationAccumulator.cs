namespace DeviceSocket.LoadTester.Reporting;

internal sealed class GatewayObservationAccumulator
{
    private readonly object _lock = new();
    private long _samples;
    private long _unavailableSamples;
    private long? _baselineTcpConnections;
    private long? _baselineAuthenticatedDevices;
    private long _maximumTcpConnections;
    private long _maximumAuthenticatedDevices;
    private long _maximumAdditionalTcpConnections;
    private long _maximumAdditionalAuthenticatedDevices;
    private GatewayStatusSnapshot? _last;

    internal void SetBaseline(GatewayStatusSnapshot snapshot)
    {
        lock (_lock)
        {
            if (snapshot.Available)
            {
                _baselineTcpConnections = snapshot.TcpConnections;
                _baselineAuthenticatedDevices = snapshot.AuthenticatedDevices;
            }

            RecordCore(snapshot);
        }
    }

    internal void Record(GatewayStatusSnapshot snapshot)
    {
        lock (_lock)
            RecordCore(snapshot);
    }

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
                _last);
        }
    }

    private void RecordCore(GatewayStatusSnapshot snapshot)
    {
        _samples++;
        if (!snapshot.Available)
            _unavailableSamples++;
        _maximumTcpConnections = Math.Max(_maximumTcpConnections, snapshot.TcpConnections);
        _maximumAuthenticatedDevices = Math.Max(_maximumAuthenticatedDevices, snapshot.AuthenticatedDevices);

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
        }

        _last = snapshot;
    }
}
