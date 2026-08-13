using Orleans;
using Orleans.Concurrency;

namespace DeviceSocket.Cluster.Contracts.Scheduling;

/// <summary>
/// 按固定分片管理设备命令的延迟、回复超时和重试唤醒，分片数量不随设备数量增长。
/// </summary>
public interface IDeviceCommandSchedulerGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// 确保分片已注册恢复 Reminder 并装载近期任务；重复调用保持幂等。
    /// </summary>
    /// <returns>当前分片运行快照。</returns>
    Task<DeviceCommandSchedulerSnapshot> EnsureStartedAsync();

    /// <summary>
    /// 接收某台设备最新的下一动作时间；同设备较旧版本会被丢弃。
    /// </summary>
    /// <param name="schedule">设备编号、调度版本和到期时间。</param>
    [OneWay]
    Task ScheduleAsync(DeviceCommandSchedule schedule);

    /// <summary>
    /// 立即执行一次持久化恢复扫描，主要供运维诊断和自动化测试使用。
    /// </summary>
    /// <returns>扫描后的分片运行快照。</returns>
    Task<DeviceCommandSchedulerSnapshot> RecoverAsync();

    /// <summary>
    /// 查询分片当前内存任务数和最近恢复时间，不返回具体设备列表。
    /// </summary>
    /// <returns>当前分片运行快照。</returns>
    Task<DeviceCommandSchedulerSnapshot> GetSnapshotAsync();
}
