using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.SqlServer;
using DeviceSocket.Cluster.Persistence.Storage;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 验证 SQL Server 首期实现遵守每种 Grain 类型独立表、结构化索引和可替换方言边界。
/// </summary>
public sealed class RelationalPersistenceContractTests
{
    /// <summary>
    /// 验证建表脚本包含四张独立应用 Grain 表及调度关键索引，不把所有状态堆进通用表。
    /// </summary>
    [Fact]
    public void SqlServerDialect_CreatesOneTablePerGrainType()
    {
        var options = CreateOptions();
        var dialect = new SqlServerRelationalGrainStorageDialect();
        var sql = string.Join(Environment.NewLine, dialect.GetSchemaStatements(options));

        Assert.Contains("[OrleansDeviceState]", sql, StringComparison.Ordinal);
        Assert.Contains("[OrleansDeviceCommandState]", sql, StringComparison.Ordinal);
        Assert.Contains("[OrleansGatewayNodeState]", sql, StringComparison.Ordinal);
        Assert.Contains("[OrleansDeviceCommandSchedulerState]", sql, StringComparison.Ordinal);
        Assert.Contains("[SchedulerShard], [HasPendingWork], [NextWakeAt]", sql, StringComparison.Ordinal);
        Assert.Contains("[DeviceId], [Status], [NotBefore], [Priority] DESC, [DeviceSequence]", sql, StringComparison.Ordinal);
        Assert.Contains("[TerminalAt]", sql, StringComparison.Ordinal);
        Assert.Contains("[PayloadCompactedAt]", sql, StringComparison.Ordinal);
        Assert.Contains("_Purge]", sql, StringComparison.Ordinal);
        Assert.Contains("_Compaction]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OrleansStorage", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证四种命名存储的写入语句只引用各自配置表，并通过 ETag 条件实现乐观并发。
    /// </summary>
    [Fact]
    public void SqlServerDialect_MapsEachStoreAndUsesOptimisticConcurrency()
    {
        var options = CreateOptions();
        var dialect = new SqlServerRelationalGrainStorageDialect();

        Assert.Contains("[OrleansDeviceState]", dialect.GetInsertStateSql(RelationalGrainStateTable.Device, options), StringComparison.Ordinal);
        Assert.Contains("[OrleansDeviceCommandState]", dialect.GetInsertStateSql(RelationalGrainStateTable.Command, options), StringComparison.Ordinal);
        Assert.Contains("[OrleansGatewayNodeState]", dialect.GetInsertStateSql(RelationalGrainStateTable.Gateway, options), StringComparison.Ordinal);
        Assert.Contains("[OrleansDeviceCommandSchedulerState]", dialect.GetInsertStateSql(RelationalGrainStateTable.Scheduler, options), StringComparison.Ordinal);
        var update = dialect.GetUpdateStateSql(RelationalGrainStateTable.Command, options);
        Assert.Contains("[ETag] = [ETag] + 1", update, StringComparison.Ordinal);
        Assert.Contains("[ETag] = @ETag", update, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证重复表名、危险标识符和空连接字符串均在 Silo 启动前被拒绝。
    /// </summary>
    [Fact]
    public void PersistenceOptionsValidator_RejectsUnsafeOrSharedTables()
    {
        var options = CreateOptions();
        options.ConnectionString = string.Empty;
        options.Schema = "bad-schema";
        options.Tables.Command = options.Tables.Device;
        var result = new RelationalGrainStorageOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ConnectionString", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Schema", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("不同", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证百万设备默认调度参数采用固定 1024 分片并通过全部边界校验。
    /// </summary>
    [Fact]
    public void SchedulingOptions_DefaultsAreValidForMillionDeviceBaseline()
    {
        var options = new DeviceCommandSchedulingOptions();
        var result = new DeviceCommandSchedulingOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(1024, options.ShardCount);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RecoveryPeriod);
        Assert.True(options.BatchSize * options.MaxBatchesPerTick >= 1024);
        Assert.Equal(7, options.PayloadRetentionDays);
        Assert.Equal(30, options.CommandRetentionDays);
        Assert.Equal(15, options.CleanupScanIntervalMinutes);
        Assert.Equal(200, options.CleanupBatchSize);
        Assert.Equal(5, options.CleanupMaxBatchesPerRun);
    }

    /// <summary>
    /// 验证总保留期短于 Payload 热数据期以及零清理周期都会在启动校验时被拒绝。
    /// </summary>
    [Fact]
    public void SchedulingOptionsValidator_RejectsInvalidCleanupRetention()
    {
        var options = new DeviceCommandSchedulingOptions
        {
            PayloadRetentionDays = 7,
            CommandRetentionDays = 6,
            CleanupScanIntervalMinutes = 0
        };

        var result = new DeviceCommandSchedulingOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("CommandRetentionDays", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("CleanupScanIntervalMinutes", StringComparison.Ordinal));
    }

    /// <summary>
    /// 创建具有四个不同表名的有效 SQL Server 测试配置。
    /// </summary>
    /// <returns>关系型存储配置。</returns>
    private static RelationalGrainStorageOptions CreateOptions() => new()
    {
        ConnectionString = "Server=localhost;Database=test;Integrated Security=True",
        Schema = "orleans_app"
    };
}
