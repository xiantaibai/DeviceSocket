namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 百万设备命令调度的强类型配置，对应配置节 <c>Orleans:CommandScheduling</c>。
/// </summary>
public sealed class DeviceCommandSchedulingOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Orleans:CommandScheduling";

    /// <summary>固定调度分片数量，默认 1024；必须是 2 的幂，零值非法。</summary>
    public int ShardCount { get; set; } = 1024;

    /// <summary>单次从持久化存储预读的未来时间窗口，默认 2 分钟；过小会增加漏调度恢复延迟。</summary>
    public TimeSpan RecoveryHorizon { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>每个分片持久化恢复 Reminder 的周期，默认 1 分钟；不得低于 Orleans Reminder 的有效粒度。</summary>
    public TimeSpan RecoveryPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>每个分片单次扫描或唤醒的最大设备数，默认 256；零值非法。</summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>每个分片一轮最多连续处理的批次数，默认 8；限制集中到期任务对 Grain 调度器的独占时间。</summary>
    public int MaxBatchesPerTick { get; set; } = 8;

    /// <summary>同一设备一次激活内最多连续完成的命令数，默认 16；零值非法。</summary>
    public int DevicePumpLimit { get; set; } = 16;

    /// <summary>同一设备一次批量受理的最大命令数，默认 100；零值非法。</summary>
    public int MaxSameDeviceBatchSize { get; set; } = 100;

    /// <summary>可靠命令默认存活时间，默认 5 分钟；请求未指定到期时间时使用。</summary>
    public TimeSpan DefaultCommandTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>设备回复默认超时时间，默认 10 秒；请求未指定时使用。</summary>
    public TimeSpan DefaultResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>可靠命令默认最大尝试次数，默认 5 次；请求传零值时使用。</summary>
    public int DefaultMaxAttempts { get; set; } = 5;

    /// <summary>命令创建后允许设备游标接管的时间，默认 30 秒；超过后由恢复扫描修复。</summary>
    public TimeSpan EnqueueRecoveryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>设备路由心跳持久化最小间隔，默认 30 秒；零值表示每次 Touch 都写入数据库。</summary>
    public TimeSpan RoutePersistenceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>无法判断 Gateway 是否已写入时的重试间隔，默认 5 秒。</summary>
    public TimeSpan UncertainRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>第一次、第二次、后续失败的重试延迟，单位为毫秒；默认 1 秒、3 秒、10 秒、30 秒。</summary>
    public int[] RetryDelayMilliseconds { get; set; } = [1000, 3000, 10000, 30000];

    /// <summary>终态命令完整报文的热数据保留天数，默认 7 天；达到期限后只清空 Payload，不删除状态。</summary>
    public int PayloadRetentionDays { get; set; } = 7;

    /// <summary>终态 CommandGrain 状态的总保留天数，默认 30 天；必须不小于 PayloadRetentionDays，达到期限后物理删除。</summary>
    public int CommandRetentionDays { get; set; } = 30;

    /// <summary>每个调度分片检查历史清理任务的间隔分钟数，默认 15 分钟；首次执行会按分片编号打散。</summary>
    public int CleanupScanIntervalMinutes { get; set; } = 15;

    /// <summary>单次历史清理 SQL 查询最多返回的命令数，默认 200；零值非法。</summary>
    public int CleanupBatchSize { get; set; } = 200;

    /// <summary>每个分片每轮对压缩和删除分别处理的最大批次数，默认 5；用于限制清理任务占用时间。</summary>
    public int CleanupMaxBatchesPerRun { get; set; } = 5;
}
