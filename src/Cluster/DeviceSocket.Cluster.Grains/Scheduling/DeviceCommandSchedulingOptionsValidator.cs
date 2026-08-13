using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 在 Silo 启动时验证百万设备调度参数，避免错误分片或零周期进入运行态。
/// </summary>
public sealed class DeviceCommandSchedulingOptionsValidator : IValidateOptions<DeviceCommandSchedulingOptions>
{
    /// <summary>
    /// 验证分片、批量、超时和重试参数是否满足运行约束。
    /// </summary>
    /// <param name="name">命名选项名称，本配置不区分名称。</param>
    /// <param name="options">待验证的调度配置。</param>
    /// <returns>成功结果或包含具体配置路径的失败结果。</returns>
    public ValidateOptionsResult Validate(string? name, DeviceCommandSchedulingOptions options)
    {
        var failures = new List<string>();
        if (options.ShardCount <= 0 || (options.ShardCount & (options.ShardCount - 1)) != 0)
            failures.Add("Orleans:CommandScheduling:ShardCount 必须是大于零的 2 的幂。");
        if (options.RecoveryHorizon <= TimeSpan.Zero)
            failures.Add("Orleans:CommandScheduling:RecoveryHorizon 必须大于零。");
        if (options.RecoveryPeriod < TimeSpan.FromMinutes(1))
            failures.Add("Orleans:CommandScheduling:RecoveryPeriod 不得小于 1 分钟。");
        if (options.BatchSize <= 0 || options.MaxBatchesPerTick <= 0 || options.DevicePumpLimit <= 0)
            failures.Add("调度批量和单设备泵送上限必须大于零。");
        if (options.MaxSameDeviceBatchSize <= 0)
            failures.Add("Orleans:CommandScheduling:MaxSameDeviceBatchSize 必须大于零。");
        if (options.DefaultCommandTtl <= TimeSpan.Zero || options.DefaultResponseTimeout <= TimeSpan.Zero)
            failures.Add("默认命令 TTL 和回复超时必须大于零。");
        if (options.DefaultMaxAttempts <= 0)
            failures.Add("Orleans:CommandScheduling:DefaultMaxAttempts 必须大于零。");
        if (options.EnqueueRecoveryDelay <= TimeSpan.Zero || options.RoutePersistenceInterval < TimeSpan.Zero)
            failures.Add("入队恢复延迟必须大于零，路由持久化间隔不得小于零。");
        if (options.UncertainRetryDelay <= TimeSpan.Zero)
            failures.Add("Orleans:CommandScheduling:UncertainRetryDelay 必须大于零。");
        if (options.RetryDelayMilliseconds is not { Length: > 0 } || options.RetryDelayMilliseconds.Any(value => value <= 0))
            failures.Add("Orleans:CommandScheduling:RetryDelayMilliseconds 至少包含一个正整数。");
        if (options.PayloadRetentionDays <= 0)
            failures.Add("Orleans:CommandScheduling:PayloadRetentionDays 必须大于零。");
        if (options.CommandRetentionDays < options.PayloadRetentionDays)
            failures.Add("Orleans:CommandScheduling:CommandRetentionDays 必须不小于 PayloadRetentionDays。");
        if (options.CleanupScanIntervalMinutes <= 0)
            failures.Add("Orleans:CommandScheduling:CleanupScanIntervalMinutes 必须大于零。");
        if (options.CleanupBatchSize <= 0 || options.CleanupMaxBatchesPerRun <= 0)
            failures.Add("历史清理批量和每轮批次数必须大于零。");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
