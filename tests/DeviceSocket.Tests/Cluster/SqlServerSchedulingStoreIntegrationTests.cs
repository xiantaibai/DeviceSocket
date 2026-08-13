using System.Data;
using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.SqlServer;
using DeviceSocket.Cluster.Persistence.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 在 Windows SQL Server LocalDB 中验证真实 DDL、四张独立表及调度索引查询。
/// </summary>
public sealed class SqlServerSchedulingStoreIntegrationTests
{
    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=30";

    /// <summary>
    /// 创建隔离测试数据库，执行自动建表并验证优先级、未来命令、设备到期和孤儿入队查询。
    /// </summary>
    [Fact]
    public async Task SqlServerSchemaAndSchedulingQueries_WorkAgainstLocalDb()
    {
        // 常规单元测试不隐式启动用户级数据库；验收环境显式设置该变量后执行真实 LocalDB 建库与清理。
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("DEVICESOCKET_SQLSERVER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var databaseName = $"DeviceSocketOrleansTests_{Environment.ProcessId}_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(MasterConnectionString, databaseName);
        try
        {
            var connectionString =
                $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True;Connect Timeout=30";
            var options = Options.Create(new RelationalGrainStorageOptions
            {
                ConnectionString = connectionString,
                AutoCreateSchema = true
            });
            var connectionFactory = new SqlServerConnectionFactory(options);
            var dialect = new SqlServerRelationalGrainStorageDialect();
            var initializer = new RelationalSchemaInitializer(connectionFactory, dialect, options);
            await initializer.EnsureInitializedAsync();

            await AssertFourTablesAndIndexesAsync(connectionString);
            var now = DateTimeOffset.UtcNow;
            await SeedDeviceAsync(connectionString, "device-sql", shardId: 7, now);
            await SeedCommandAsync(connectionString, "command-low", "device-sql", ReliableMessageStatus.Queued, 1, 1, now.AddSeconds(-2), now.AddMinutes(5), null, 7);
            await SeedCommandAsync(connectionString, "command-high", "device-sql", ReliableMessageStatus.Queued, 100, 2, now.AddSeconds(-1), now.AddMinutes(5), null, 7);
            await SeedCommandAsync(connectionString, "command-future", "device-sql", ReliableMessageStatus.Queued, 200, 3, now.AddMinutes(1), now.AddMinutes(5), null, 7);
            await SeedCommandAsync(connectionString, "command-orphan", "device-orphan", ReliableMessageStatus.EnqueuePending, 0, 1, now, now.AddMinutes(5), now.AddSeconds(-1), 7);
            await SeedCommandAsync(connectionString, "command-purge", "device-old", ReliableMessageStatus.Failed, 0, 1, now.AddDays(-32), now.AddDays(-31), null, 7, now.AddDays(-31));
            await SeedCommandAsync(connectionString, "command-compact", "device-warm", ReliableMessageStatus.Confirmed, 0, 1, now.AddDays(-9), now.AddDays(-8), null, 7, now.AddDays(-8));

            var store = new SqlServerCommandSchedulingStore(connectionFactory, dialect, initializer, options);
            var next = await store.FindNextCommandAsync("device-sql", now);
            Assert.NotNull(next);
            Assert.Equal("command-high", next.MsgId);

            await MarkCommandCompletedWithoutConfirmationAsync(connectionString, "command-high");
            next = await store.FindNextCommandAsync("device-sql", now);
            Assert.NotNull(next);
            Assert.Equal("command-low", next.MsgId);

            var dueDevices = await store.ReadDueDevicesAsync(7, now.AddSeconds(5), 100);
            var due = Assert.Single(dueDevices);
            Assert.Equal("device-sql", due.DeviceId);
            Assert.Equal(12, due.ScheduleVersion);

            var recoveries = await store.ReadPendingEnqueuesAsync(7, now, 100);
            var recovery = Assert.Single(recoveries);
            Assert.Equal("command-orphan", recovery.MsgId);
            Assert.Equal("device-orphan", recovery.DeviceId);

            var purgeCandidates = await store.ReadCommandPurgeCandidatesAsync(7, now.AddDays(-30), 100);
            Assert.Equal(["command-purge"], purgeCandidates);
            var compactionCandidates = await store.ReadPayloadCompactionCandidatesAsync(
                7,
                now.AddDays(-7),
                now.AddDays(-30),
                100);
            Assert.Equal(["command-compact"], compactionCandidates);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(MasterConnectionString, databaseName);
        }
    }

    /// <summary>
    /// 显式容量验收模式下写入一百万条 DeviceGrain 状态，验证固定分片到期索引能在有限时间内返回有界批次。
    /// </summary>
    [Fact]
    public async Task MillionDeviceStateTable_SupportsBoundedShardDueScan()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("DEVICESOCKET_MILLION_SQLSERVER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var databaseName = $"DeviceSocketMillionTests_{Environment.ProcessId}_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(MasterConnectionString, databaseName);
        try
        {
            var connectionString =
                $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True;Connect Timeout=60";
            var options = Options.Create(new RelationalGrainStorageOptions
            {
                ConnectionString = connectionString,
                AutoCreateSchema = true,
                CommandTimeoutSeconds = 120
            });
            var connectionFactory = new SqlServerConnectionFactory(options);
            var dialect = new SqlServerRelationalGrainStorageDialect();
            var initializer = new RelationalSchemaInitializer(connectionFactory, dialect, options);
            await initializer.EnsureInitializedAsync();
            var now = DateTimeOffset.UtcNow;
            await SeedOneMillionDevicesAsync(connectionString, now);

            var count = await CountDevicesAsync(connectionString);
            Assert.Equal(1_000_000, count);
            var store = new SqlServerCommandSchedulingStore(connectionFactory, dialect, initializer, options);
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var due = await store.ReadDueDevicesAsync(42, now.AddMinutes(2), 2048);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

            Assert.InRange(due.Count, 900, 1100);
            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"百万设备分片扫描耗时 {elapsed}，超过 10 秒验收线。");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(MasterConnectionString, databaseName);
        }
    }

    /// <summary>
    /// 在 LocalDB master 中创建名称由当前测试生成的隔离数据库。
    /// </summary>
    /// <param name="masterConnectionString">master 连接字符串。</param>
    /// <param name="databaseName">只含安全字符的随机测试库名。</param>
    private static async Task CreateDatabaseAsync(string masterConnectionString, string databaseName)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 强制关闭测试库连接后删除本次随机数据库，确保不会影响其他数据库。
    /// </summary>
    /// <param name="masterConnectionString">master 连接字符串。</param>
    /// <param name="databaseName">本测试创建的精确数据库名。</param>
    private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 查询 SQL Server 系统目录，验证四张类型表和队列、恢复、清理索引真实存在。
    /// </summary>
    /// <param name="connectionString">隔离测试库连接字符串。</param>
    private static async Task AssertFourTablesAndIndexesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "WHERE s.name = N'orleans_app' AND t.name IN (N'OrleansDeviceState', N'OrleansDeviceCommandState', N'OrleansGatewayNodeState', N'OrleansDeviceCommandSchedulerState'); " +
            "SELECT COUNT(*) FROM sys.indexes WHERE name IN (N'IX_OrleansDeviceState_Due', N'IX_OrleansDeviceCommandState_Queue', N'IX_OrleansDeviceCommandState_Recovery', N'IX_OrleansDeviceCommandState_Purge', N'IX_OrleansDeviceCommandState_Compaction');";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt32(0));
    }

    /// <summary>
    /// 使用 SQL Server 集合运算批量生成一百万条轻量 DeviceGrain 状态，避免客户端逐行往返影响验收结果。
    /// </summary>
    /// <param name="connectionString">隔离容量测试库连接字符串。</param>
    /// <param name="now">所有状态使用的统一更新时间。</param>
    private static async Task SeedOneMillionDevicesAsync(string connectionString, DateTimeOffset now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText =
            "WITH E1(N) AS (SELECT N FROM (VALUES(0),(0),(0),(0),(0),(0),(0),(0),(0),(0)) V(N)), " +
            "E2(N) AS (SELECT 0 FROM E1 A CROSS JOIN E1 B), " +
            "E4(N) AS (SELECT 0 FROM E2 A CROSS JOIN E2 B), " +
            "E6(N) AS (SELECT 0 FROM E4 A CROSS JOIN E2 B), " +
            "Numbers(N) AS (SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 FROM E6) " +
            "INSERT INTO [orleans_app].[OrleansDeviceState] " +
            "([GrainId], [ETag], [SchemaVersion], [State], [DeviceId], [SchedulerShard], [HasPendingWork], [NextWakeAt], [ScheduleVersion], [UpdatedAt]) " +
            "SELECT CONCAT(N'device/', N), 1, 1, 0x00, CONCAT(N'device-', N), N % 1024, 1, DATEADD(second, CONVERT(int, N % 120) - 60, @Now), 1, @Now FROM Numbers";
        command.Parameters.AddWithValue("@Now", now);
        Assert.Equal(1_000_000, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// 读取容量测试表的精确设备行数。
    /// </summary>
    /// <param name="connectionString">隔离容量测试库连接字符串。</param>
    /// <returns>DeviceGrain 状态行数。</returns>
    private static async Task<long> CountDevicesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM [orleans_app].[OrleansDeviceState]";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 插入一条具有到期调度游标的 DeviceGrain 索引记录。
    /// </summary>
    /// <param name="connectionString">测试库连接字符串。</param>
    /// <param name="deviceId">设备编号。</param>
    /// <param name="shardId">调度分片。</param>
    /// <param name="now">测试统一时间。</param>
    private static async Task SeedDeviceAsync(
        string connectionString,
        string deviceId,
        int shardId,
        DateTimeOffset now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO [orleans_app].[OrleansDeviceState] " +
            "([GrainId], [ETag], [SchemaVersion], [State], [DeviceId], [SchedulerShard], [HasPendingWork], [NextWakeAt], [ScheduleVersion], [UpdatedAt]) " +
            "VALUES (@GrainId, 1, 1, 0x00, @DeviceId, @ShardId, 1, @NextWakeAt, 12, @Now)";
        command.Parameters.AddWithValue("@GrainId", $"device/{deviceId}");
        command.Parameters.AddWithValue("@DeviceId", deviceId);
        command.Parameters.AddWithValue("@ShardId", shardId);
        command.Parameters.AddWithValue("@NextWakeAt", now.AddSeconds(-1));
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 插入一条只供结构化索引查询使用的 CommandGrain 记录。
    /// </summary>
    /// <param name="connectionString">测试库连接字符串。</param>
    /// <param name="msgId">命令编号。</param>
    /// <param name="deviceId">设备编号。</param>
    /// <param name="status">命令状态。</param>
    /// <param name="priority">优先级。</param>
    /// <param name="sequence">设备内顺序号。</param>
    /// <param name="notBefore">最早发送时间。</param>
    /// <param name="expiresAt">命令到期时间。</param>
    /// <param name="enqueueRecoveryAt">入队恢复时间。</param>
    /// <param name="shardId">调度分片。</param>
    /// <param name="terminalAt">终态时间；非终态命令为空。</param>
    /// <param name="payloadCompactedAt">Payload 压缩时间；未压缩时为空。</param>
    private static async Task SeedCommandAsync(
        string connectionString,
        string msgId,
        string deviceId,
        ReliableMessageStatus status,
        int priority,
        long sequence,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        DateTimeOffset? enqueueRecoveryAt,
        int shardId,
        DateTimeOffset? terminalAt = null,
        DateTimeOffset? payloadCompactedAt = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO [orleans_app].[OrleansDeviceCommandState] " +
            "([GrainId], [ETag], [SchemaVersion], [State], [MsgId], [DeviceId], [Status], [Priority], [DeviceSequence], [NotBefore], [ExpiresAt], [NextActionAt], [EnqueueRecoveryAt], [SchedulerShard], [TerminalAt], [PayloadCompactedAt], [UpdatedAt]) " +
            "VALUES (@GrainId, 1, 2, 0x00, @MsgId, @DeviceId, @Status, @Priority, @Sequence, @NotBefore, @ExpiresAt, @NotBefore, @EnqueueRecoveryAt, @ShardId, @TerminalAt, @PayloadCompactedAt, @UpdatedAt)";
        command.Parameters.AddWithValue("@GrainId", $"command/{msgId}");
        command.Parameters.AddWithValue("@MsgId", msgId);
        command.Parameters.AddWithValue("@DeviceId", deviceId);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@Priority", priority);
        command.Parameters.AddWithValue("@Sequence", sequence);
        command.Parameters.AddWithValue("@NotBefore", notBefore);
        command.Parameters.AddWithValue("@ExpiresAt", expiresAt);
        command.Parameters.AddWithValue("@EnqueueRecoveryAt", enqueueRecoveryAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ShardId", shardId);
        command.Parameters.AddWithValue("@TerminalAt", terminalAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@PayloadCompactedAt", payloadCompactedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@UpdatedAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 把指定测试命令改为 CompletedWithoutConfirmation，验证下一次队首选择会跳过终态。
    /// </summary>
    /// <param name="connectionString">测试库连接字符串。</param>
    /// <param name="msgId">命令编号。</param>
    private static async Task MarkCommandCompletedWithoutConfirmationAsync(string connectionString, string msgId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE [orleans_app].[OrleansDeviceCommandState] SET [Status] = @Status WHERE [MsgId] = @MsgId";
        command.Parameters.AddWithValue("@Status", (int)ReliableMessageStatus.CompletedWithoutConfirmation);
        command.Parameters.AddWithValue("@MsgId", msgId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
