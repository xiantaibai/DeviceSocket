using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Runtime;

internal sealed class LoadTestRunner(
    LoadTestOptions options,
    IReadOnlyList<VirtualDeviceDescriptor> devices,
    ILoadTestProtocol protocol,
    LoadTestMetrics metrics,
    ConnectionPacer pacer,
    SemaphoreSlim connectionGate,
    FaultInjectionController faultController)
{
    private readonly Dictionary<int, DeviceHandle> _handles = [];

    internal async Task<ScenarioExecutionResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var phaseResults = new List<PhaseResult>();
        var measurement = metrics.Snapshot(startedAt);
        var cancelled = false;

        foreach (var phase in GetPhases())
        {
            var phaseStartedAt = DateTimeOffset.UtcNow;
            var desired = GetLocalTargetCount(phase.TargetDevicePercentage);
            metrics.SetExpectedOnline(desired, phaseStartedAt);
            pacer.UpdateRate(ResolveConnectionRate(phase));
            faultController.Apply(phase.Faults);
            Console.WriteLine(
                $"[阶段开始] {phase.Name} ({phase.Kind})，持续 {phase.DurationSeconds} 秒，" +
                $"本地目标设备 {desired:N0}/{devices.Count:N0}");
            await ResizeAsync(desired);
            using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var disconnectTask = RunDisconnectWaveAsync(phase.Faults, phaseCancellation.Token);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(phase.DurationSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                phaseCancellation.Cancel();
                try
                {
                    await disconnectTask;
                }
                catch (OperationCanceledException) when (phaseCancellation.IsCancellationRequested)
                {
                }
            }

            var phaseEndedAt = DateTimeOffset.UtcNow;
            var snapshot = metrics.Snapshot(phaseEndedAt);
            phaseResults.Add(new PhaseResult(
                phase.Name,
                phase.Kind,
                phaseStartedAt,
                phaseEndedAt,
                desired,
                snapshot));
            Console.WriteLine(
                $"[阶段结束] {phase.Name}，在线 {snapshot.ProtocolOnline:N0}/{snapshot.ExpectedOnline:N0}，" +
                $"累计重连 {snapshot.ReconnectsScheduled:N0}");
            if (phase.Kind != ScenarioPhaseKind.Cooldown)
                measurement = snapshot;
            if (cancelled)
                break;
        }

        faultController.Reset();
        await StopAllAsync();
        return new ScenarioExecutionResult(
            startedAt,
            DateTimeOffset.UtcNow,
            measurement,
            phaseResults,
            cancelled);
    }

    private IReadOnlyList<ScenarioPhaseOptions> GetPhases()
    {
        if (options.Scenario.Phases.Count > 0)
            return options.Scenario.Phases;
        return
        [
            new ScenarioPhaseOptions
            {
                Name = options.Scenario.Name,
                Kind = ScenarioPhaseKind.Steady,
                DurationSeconds = options.Scenario.DurationSeconds,
                TargetDevicePercentage = 100
            }
        ];
    }

    private int ResolveConnectionRate(ScenarioPhaseOptions phase)
    {
        if (phase.ConnectionsPerSecond.HasValue)
            return phase.ConnectionsPerSecond.Value;
        return options.Connection.StartMode == ConnectionStartMode.Burst
            ? 0
            : options.Connection.ConnectionsPerSecond;
    }

    private int GetLocalTargetCount(double percentage)
    {
        var globalTarget = (int)Math.Ceiling(options.Devices.Count * percentage / 100d);
        return devices.Count(device => device.GlobalOrdinal < globalTarget);
    }

    private async Task ResizeAsync(int desired)
    {
        var desiredDevices = devices.Take(desired).Select(item => item.GlobalOrdinal).ToHashSet();
        var stopping = _handles
            .Where(item => !desiredDevices.Contains(item.Key))
            .Select(item => item.Value)
            .ToArray();
        foreach (var handle in stopping)
            handle.Cancellation.Cancel();
        if (stopping.Length > 0)
        {
            await Task.WhenAll(stopping.Select(item => item.Task));
            foreach (var handle in stopping)
            {
                _handles.Remove(handle.Descriptor.GlobalOrdinal);
                handle.Cancellation.Dispose();
            }
        }

        foreach (var descriptor in devices.Take(desired))
        {
            if (_handles.ContainsKey(descriptor.GlobalOrdinal))
                continue;
            var cancellation = new CancellationTokenSource();
            var session = new VirtualDeviceSession(
                descriptor,
                options,
                protocol,
                metrics,
                pacer,
                connectionGate,
                faultController);
            var task = session.RunAsync(cancellation.Token);
            _handles.Add(descriptor.GlobalOrdinal, new DeviceHandle(descriptor, session, cancellation, task));
        }
    }

    private async Task StopAllAsync()
    {
        metrics.SetExpectedOnline(0, DateTimeOffset.UtcNow);
        foreach (var handle in _handles.Values)
            handle.Cancellation.Cancel();
        await Task.WhenAll(_handles.Values.Select(item => item.Task));
        foreach (var handle in _handles.Values)
            handle.Cancellation.Dispose();
        _handles.Clear();
    }

    private async Task RunDisconnectWaveAsync(
        FaultInjectionOptions faults,
        CancellationToken cancellationToken)
    {
        if (faults.DisconnectPercentage <= 0 || _handles.Count == 0)
            return;

        var count = Math.Clamp(
            (int)Math.Ceiling(_handles.Count * faults.DisconnectPercentage / 100d),
            1,
            _handles.Count);
        var targets = _handles.Values
            .OrderBy(item => item.Descriptor.GlobalOrdinal)
            .Take(count)
            .ToArray();
        var batchSize = faults.DisconnectBatchSize <= 0
            ? targets.Length
            : Math.Min(faults.DisconnectBatchSize, targets.Length);
        for (var offset = 0; offset < targets.Length; offset += batchSize)
        {
            foreach (var handle in targets.Skip(offset).Take(batchSize))
                handle.Session.RequestDisconnect();
            if (offset + batchSize < targets.Length && faults.DisconnectIntervalMilliseconds > 0)
                await Task.Delay(faults.DisconnectIntervalMilliseconds, cancellationToken);
        }
    }

    private sealed record DeviceHandle(
        VirtualDeviceDescriptor Descriptor,
        VirtualDeviceSession Session,
        CancellationTokenSource Cancellation,
        Task Task);
}
