namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 从 DeviceGrain 状态表恢复出的版本化调度项。
/// </summary>
public sealed record PersistedDeviceSchedule
{
    /// <summary>设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>设备状态中的调度版本。</summary>
    public required long ScheduleVersion { get; init; }

    /// <summary>下一动作时间。</summary>
    public required DateTimeOffset DueAt { get; init; }
}
