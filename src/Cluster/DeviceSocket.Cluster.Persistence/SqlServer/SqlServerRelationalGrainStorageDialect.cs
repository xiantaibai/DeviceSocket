using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.Storage;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DeviceSocket.Cluster.Persistence.SqlServer;

/// <summary>
/// SQL Server 的应用 Grain 独立表、乐观并发和 TOP 查询方言。
/// </summary>
public sealed class SqlServerRelationalGrainStorageDialect : IRelationalGrainStorageDialect
{
    private static readonly string[] DeviceIndexColumns =
        ["DeviceId", "SchedulerShard", "HasPendingWork", "NextWakeAt", "ScheduleVersion"];
    private static readonly string[] CommandIndexColumns =
        ["MsgId", "DeviceId", "Status", "Priority", "DeviceSequence", "NotBefore", "ExpiresAt", "NextActionAt", "EnqueueRecoveryAt", "SchedulerShard", "TerminalAt", "PayloadCompactedAt"];
    private static readonly string[] GatewayIndexColumns = ["GatewayNodeId", "LeaseExpiresAt"];
    private static readonly string[] SchedulerIndexColumns = ["ShardId", "LastRecoveryAt"];

    /// <summary>
    /// SQL Server 错误 2601 和 2627 分别表示唯一索引或主键重复。
    /// </summary>
    /// <param name="exception">数据库异常。</param>
    /// <returns>属于首次写入并发竞争时返回 true。</returns>
    public bool IsUniqueConstraintViolation(DbException exception) =>
        exception is SqlException { Number: 2601 or 2627 };

    /// <summary>
    /// 返回 SQL Server 幂等建表和索引语句；标准 Orleans Membership/Reminder 表仍使用官方 ADO.NET 脚本。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>按顺序执行的 DDL 语句。</returns>
    public IReadOnlyList<string> GetSchemaStatements(RelationalGrainStorageOptions options)
    {
        var schema = Quote(options.Schema);
        var device = Qualified(options.Schema, options.Tables.Device);
        var command = Qualified(options.Schema, options.Tables.Command);
        var gateway = Qualified(options.Schema, options.Tables.Gateway);
        var scheduler = Qualified(options.Schema, options.Tables.Scheduler);
        return
        [
            $"IF SCHEMA_ID(N'{EscapeLiteral(options.Schema)}') IS NULL EXEC(N'CREATE SCHEMA {schema}')",
            $"""
            IF OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Device)}', N'U') IS NULL
            BEGIN
                CREATE TABLE {device} (
                    [GrainId] nvarchar(512) NOT NULL CONSTRAINT [PK_{options.Tables.Device}] PRIMARY KEY,
                    [ETag] bigint NOT NULL,
                    [SchemaVersion] int NOT NULL,
                    [State] varbinary(max) NOT NULL,
                    [DeviceId] nvarchar(256) NOT NULL,
                    [SchedulerShard] int NOT NULL,
                    [HasPendingWork] bit NOT NULL,
                    [NextWakeAt] datetimeoffset(7) NULL,
                    [ScheduleVersion] bigint NOT NULL,
                    [UpdatedAt] datetimeoffset(7) NOT NULL
                );
                CREATE UNIQUE INDEX [UX_{options.Tables.Device}_DeviceId] ON {device} ([DeviceId]);
                CREATE INDEX [IX_{options.Tables.Device}_Due] ON {device}
                    ([SchedulerShard], [HasPendingWork], [NextWakeAt]) INCLUDE ([DeviceId], [ScheduleVersion]);
            END
            """,
            $"""
            IF OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Command)}', N'U') IS NULL
            BEGIN
                CREATE TABLE {command} (
                    [GrainId] nvarchar(512) NOT NULL CONSTRAINT [PK_{options.Tables.Command}] PRIMARY KEY,
                    [ETag] bigint NOT NULL,
                    [SchemaVersion] int NOT NULL,
                    [State] varbinary(max) NOT NULL,
                    [MsgId] nvarchar(128) NOT NULL,
                    [DeviceId] nvarchar(256) NOT NULL,
                    [Status] int NOT NULL,
                    [Priority] int NOT NULL,
                    [DeviceSequence] bigint NOT NULL,
                    [NotBefore] datetimeoffset(7) NOT NULL,
                    [ExpiresAt] datetimeoffset(7) NOT NULL,
                    [NextActionAt] datetimeoffset(7) NULL,
                    [EnqueueRecoveryAt] datetimeoffset(7) NULL,
                    [SchedulerShard] int NOT NULL,
                    [TerminalAt] datetimeoffset(7) NULL,
                    [PayloadCompactedAt] datetimeoffset(7) NULL,
                    [UpdatedAt] datetimeoffset(7) NOT NULL
                );
                CREATE UNIQUE INDEX [UX_{options.Tables.Command}_MsgId] ON {command} ([MsgId]);
                CREATE INDEX [IX_{options.Tables.Command}_Queue] ON {command}
                    ([DeviceId], [Status], [NotBefore], [Priority] DESC, [DeviceSequence]) INCLUDE ([MsgId], [ExpiresAt]);
                CREATE INDEX [IX_{options.Tables.Command}_Recovery] ON {command}
                    ([SchedulerShard], [Status], [EnqueueRecoveryAt]) INCLUDE ([MsgId], [DeviceId], [DeviceSequence], [NotBefore]);
                CREATE INDEX [IX_{options.Tables.Command}_Purge] ON {command}
                    ([SchedulerShard], [TerminalAt]) INCLUDE ([MsgId]) WHERE [TerminalAt] IS NOT NULL;
                CREATE INDEX [IX_{options.Tables.Command}_Compaction] ON {command}
                    ([SchedulerShard], [PayloadCompactedAt], [TerminalAt]) INCLUDE ([MsgId]) WHERE [TerminalAt] IS NOT NULL;
            END
            """,
            $"""
            IF COL_LENGTH(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Command)}', N'TerminalAt') IS NULL
                ALTER TABLE {command} ADD [TerminalAt] datetimeoffset(7) NULL;
            IF COL_LENGTH(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Command)}', N'PayloadCompactedAt') IS NULL
                ALTER TABLE {command} ADD [PayloadCompactedAt] datetimeoffset(7) NULL;

            WHILE 1 = 1
            BEGIN
                UPDATE TOP (1000) {command}
                    SET [TerminalAt] = [UpdatedAt]
                    WHERE [TerminalAt] IS NULL AND [Status] IN (3, 4, 6, 7, 8);
                IF @@ROWCOUNT = 0 BREAK;
            END

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Command)}') AND [name] = N'IX_{EscapeLiteral(options.Tables.Command)}_Purge')
                CREATE INDEX [IX_{options.Tables.Command}_Purge] ON {command}
                    ([SchedulerShard], [TerminalAt]) INCLUDE ([MsgId]) WHERE [TerminalAt] IS NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Command)}') AND [name] = N'IX_{EscapeLiteral(options.Tables.Command)}_Compaction')
                CREATE INDEX [IX_{options.Tables.Command}_Compaction] ON {command}
                    ([SchedulerShard], [PayloadCompactedAt], [TerminalAt]) INCLUDE ([MsgId]) WHERE [TerminalAt] IS NOT NULL;
            """,
            $"""
            IF OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Gateway)}', N'U') IS NULL
            BEGIN
                CREATE TABLE {gateway} (
                    [GrainId] nvarchar(512) NOT NULL CONSTRAINT [PK_{options.Tables.Gateway}] PRIMARY KEY,
                    [ETag] bigint NOT NULL,
                    [SchemaVersion] int NOT NULL,
                    [State] varbinary(max) NOT NULL,
                    [GatewayNodeId] nvarchar(256) NOT NULL,
                    [LeaseExpiresAt] datetimeoffset(7) NULL,
                    [UpdatedAt] datetimeoffset(7) NOT NULL
                );
                CREATE UNIQUE INDEX [UX_{options.Tables.Gateway}_GatewayNodeId] ON {gateway} ([GatewayNodeId]);
            END
            """,
            $"""
            IF OBJECT_ID(N'{EscapeLiteral(options.Schema)}.{EscapeLiteral(options.Tables.Scheduler)}', N'U') IS NULL
            BEGIN
                CREATE TABLE {scheduler} (
                    [GrainId] nvarchar(512) NOT NULL CONSTRAINT [PK_{options.Tables.Scheduler}] PRIMARY KEY,
                    [ETag] bigint NOT NULL,
                    [SchemaVersion] int NOT NULL,
                    [State] varbinary(max) NOT NULL,
                    [ShardId] int NOT NULL,
                    [LastRecoveryAt] datetimeoffset(7) NULL,
                    [UpdatedAt] datetimeoffset(7) NOT NULL
                );
                CREATE UNIQUE INDEX [UX_{options.Tables.Scheduler}_ShardId] ON {scheduler} ([ShardId]);
            END
            """
        ];
    }

    /// <summary>
    /// 生成按 GrainId 读取二进制状态和 ETag 的 SQL Server 查询。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>读取 SQL。</returns>
    public string GetReadStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options) =>
        $"SELECT [ETag], [State] FROM {Qualified(options.Schema, GetTableName(table, options))} WHERE [GrainId] = @GrainId";

    /// <summary>
    /// 生成插入并通过 OUTPUT 返回初始 ETag 的 SQL Server 语句。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>插入 SQL。</returns>
    public string GetInsertStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options)
    {
        var indexColumns = GetIndexColumns(table);
        var columns = string.Join(", ", indexColumns.Select(column => $"[{column}]"));
        var values = string.Join(", ", indexColumns.Select(column => $"@{column}"));
        return $"INSERT INTO {Qualified(options.Schema, GetTableName(table, options))} " +
               $"([GrainId], [ETag], [SchemaVersion], [State], {columns}, [UpdatedAt]) " +
               $"OUTPUT INSERTED.[ETag] VALUES (@GrainId, 1, @SchemaVersion, @State, {values}, @UpdatedAt)";
    }

    /// <summary>
    /// 生成带旧 ETag 条件并返回递增 ETag 的 SQL Server 更新语句。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>更新 SQL。</returns>
    public string GetUpdateStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options)
    {
        var assignments = string.Join(", ", GetIndexColumns(table).Select(column => $"[{column}] = @{column}"));
        return $"UPDATE {Qualified(options.Schema, GetTableName(table, options))} SET " +
               $"[ETag] = [ETag] + 1, [SchemaVersion] = @SchemaVersion, [State] = @State, {assignments}, [UpdatedAt] = @UpdatedAt " +
               "OUTPUT INSERTED.[ETag] WHERE [GrainId] = @GrainId AND [ETag] = @ETag";
    }

    /// <summary>
    /// 生成带 ETag 条件的 SQL Server 删除语句。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>删除 SQL。</returns>
    public string GetDeleteStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options) =>
        $"DELETE FROM {Qualified(options.Schema, GetTableName(table, options))} WHERE [GrainId] = @GrainId AND [ETag] = @ETag";

    /// <summary>
    /// 返回各 Grain 类型表需要同步维护的结构化索引列。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <returns>索引列名。</returns>
    public IReadOnlyList<string> GetIndexColumns(RelationalGrainStateTable table) => table switch
    {
        RelationalGrainStateTable.Device => DeviceIndexColumns,
        RelationalGrainStateTable.Command => CommandIndexColumns,
        RelationalGrainStateTable.Gateway => GatewayIndexColumns,
        RelationalGrainStateTable.Scheduler => SchedulerIndexColumns,
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    /// <summary>
    /// 生成当前可执行最高优先级或最早未来命令的 SQL Server TOP 查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <param name="eligibleOnly">是否限制 NotBefore 已到期。</param>
    /// <returns>命令索引查询 SQL。</returns>
    public string GetNextCommandSql(RelationalGrainStorageOptions options, bool eligibleOnly)
    {
        var table = Qualified(options.Schema, options.Tables.Command);
        var timePredicate = eligibleOnly ? "AND [NotBefore] <= @Now" : "AND [NotBefore] > @Now";
        var ordering = eligibleOnly
            ? "[Priority] DESC, [DeviceSequence] ASC"
            : "[NotBefore] ASC, [Priority] DESC, [DeviceSequence] ASC";
        return $"SELECT TOP (1) [MsgId], [NotBefore], [Priority], [DeviceSequence] FROM {table} " +
               $"WHERE [DeviceId] = @DeviceId AND [Status] = @QueuedStatus AND [ExpiresAt] > @Now {timePredicate} ORDER BY {ordering}";
    }

    /// <summary>
    /// 生成按分片和时间窗口读取设备下一动作的 SQL Server TOP 查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>设备恢复扫描 SQL。</returns>
    public string GetDueDevicesSql(RelationalGrainStorageOptions options) =>
        $"SELECT TOP (@Take) [DeviceId], [ScheduleVersion], [NextWakeAt] FROM {Qualified(options.Schema, options.Tables.Device)} " +
        "WHERE [SchedulerShard] = @ShardId AND [HasPendingWork] = 1 AND [NextWakeAt] IS NOT NULL AND [NextWakeAt] <= @DueThrough " +
        "ORDER BY [NextWakeAt] ASC, [DeviceId] ASC";

    /// <summary>
    /// 生成按分片读取未完成入队命令的 SQL Server TOP 查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>入队恢复扫描 SQL。</returns>
    public string GetPendingEnqueuesSql(RelationalGrainStorageOptions options) =>
        $"SELECT TOP (@Take) [DeviceId], [MsgId], [DeviceSequence], [NotBefore] FROM {Qualified(options.Schema, options.Tables.Command)} " +
        "WHERE [SchedulerShard] = @ShardId AND [Status] = @EnqueuePendingStatus AND [EnqueueRecoveryAt] <= @RecoveryAt " +
        "ORDER BY [EnqueueRecoveryAt] ASC, [MsgId] ASC";

    /// <summary>
    /// 生成按分片读取已达到总保留期限终态命令的 SQL Server TOP 查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>物理删除候选查询 SQL。</returns>
    public string GetCommandPurgeCandidatesSql(RelationalGrainStorageOptions options) =>
        $"SELECT TOP (@Take) [MsgId] FROM {Qualified(options.Schema, options.Tables.Command)} " +
        "WHERE [SchedulerShard] = @ShardId AND [TerminalAt] IS NOT NULL AND [TerminalAt] <= @TerminalThrough " +
        "ORDER BY [TerminalAt] ASC, [MsgId] ASC";

    /// <summary>
    /// 生成按分片读取已过热数据期限、尚未到删除期限且 Payload 未压缩命令的 SQL Server TOP 查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>Payload 压缩候选查询 SQL。</returns>
    public string GetPayloadCompactionCandidatesSql(RelationalGrainStorageOptions options) =>
        $"SELECT TOP (@Take) [MsgId] FROM {Qualified(options.Schema, options.Tables.Command)} " +
        "WHERE [SchedulerShard] = @ShardId AND [PayloadCompactedAt] IS NULL AND [TerminalAt] > @DeleteThrough AND [TerminalAt] <= @CompactThrough " +
        "ORDER BY [TerminalAt] ASC, [MsgId] ASC";

    /// <summary>
    /// 根据 Grain 类型取得已验证的独立表名。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>未加限定符的表名。</returns>
    private static string GetTableName(RelationalGrainStateTable table, RelationalGrainStorageOptions options) => table switch
    {
        RelationalGrainStateTable.Device => options.Tables.Device,
        RelationalGrainStateTable.Command => options.Tables.Command,
        RelationalGrainStateTable.Gateway => options.Tables.Gateway,
        RelationalGrainStateTable.Scheduler => options.Tables.Scheduler,
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    /// <summary>
    /// 使用 SQL Server 方括号引用已验证标识符。
    /// </summary>
    /// <param name="identifier">Schema 或表名。</param>
    /// <returns>已引用标识符。</returns>
    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    /// <summary>
    /// 组合 Schema 和表名为 SQL Server 限定名称。
    /// </summary>
    /// <param name="schema">Schema 名。</param>
    /// <param name="table">表名。</param>
    /// <returns>限定表名。</returns>
    private static string Qualified(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    /// <summary>
    /// 转义只用于 OBJECT_ID 字符串字面量的单引号。
    /// </summary>
    /// <param name="value">字面量内容。</param>
    /// <returns>安全字面量内容。</returns>
    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
