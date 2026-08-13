using System.Data;
using System.Data.Common;
using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.Storage;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace DeviceSocket.Cluster.Persistence.SqlServer;

/// <summary>
/// 从四类 Grain 独立表的结构化索引列读取设备队首、调度窗口和未完成入队命令。
/// </summary>
public sealed class SqlServerCommandSchedulingStore(
    IRelationalConnectionFactory connectionFactory,
    IRelationalGrainStorageDialect dialect,
    IRelationalSchemaInitializer schemaInitializer,
    IOptions<RelationalGrainStorageOptions> options)
    : ICommandSchedulingStore
{
    /// <summary>
    /// SQL Server 的结构化索引列由 RelationalGrainStorage 与二进制状态在同一语句中原子更新，无需二次写入。
    /// </summary>
    /// <param name="snapshot">已经持久化的命令快照。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>已经完成的任务。</returns>
    public Task CommandStateChangedAsync(
        ReliableCommandSnapshot snapshot,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// SQL 索引与 CommandGrain 状态位于同一行，ClearStateAsync 已经原子移除索引，无需二次删除。
    /// </summary>
    /// <param name="msgId">已删除的消息编号。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>已经完成的任务。</returns>
    public Task CommandStateDeletedAsync(
        string msgId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 优先返回当前已到期的最高优先级命令；没有可执行命令时返回最早未来命令。
    /// </summary>
    /// <param name="deviceId">目标设备编号。</param>
    /// <param name="now">选择命令使用的统一时间。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>下一命令索引；没有等待命令时返回空。</returns>
    public async Task<DeviceCommandQueueEntry?> FindNextCommandAsync(
        string deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await schemaInitializer.EnsureInitializedAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var eligible = await ReadNextCommandAsync(connection, deviceId, now, eligibleOnly: true, cancellationToken);
        var result = eligible ?? await ReadNextCommandAsync(connection, deviceId, now, eligibleOnly: false, cancellationToken);
        DeviceCommandRuntimeMetrics.RecordSqlQuery("next_command", Stopwatch.GetElapsedTime(startedAt), result is null ? 0 : 1);
        return result;
    }

    /// <summary>
    /// 按调度分片读取时间窗口内仍有待处理工作的设备。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="dueThrough">扫描窗口截止时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>按到期时间升序的设备调度项。</returns>
    public async Task<IReadOnlyList<PersistedDeviceSchedule>> ReadDueDevicesAsync(
        int shardId,
        DateTimeOffset dueThrough,
        int take,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ArgumentOutOfRangeException.ThrowIfNegative(shardId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await schemaInitializer.EnsureInitializedAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.GetDueDevicesSql(options.Value);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@Take", DbType.Int32, take);
        AddParameter(command, "@ShardId", DbType.Int32, shardId);
        AddParameter(command, "@DueThrough", DbType.DateTimeOffset, dueThrough);

        var result = new List<PersistedDeviceSchedule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PersistedDeviceSchedule
            {
                DeviceId = reader.GetString(0),
                ScheduleVersion = reader.GetInt64(1),
                DueAt = reader.GetFieldValue<DateTimeOffset>(2)
            });
        }

        DeviceCommandRuntimeMetrics.RecordSqlQuery("due_devices", Stopwatch.GetElapsedTime(startedAt), result.Count);
        return result;
    }

    /// <summary>
    /// 读取超过恢复时间仍处于 EnqueuePending 的命令，避免跨 Grain 中断形成永久孤儿。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="recoveryAt">本次恢复时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>待 DeviceGrain 幂等接管的命令索引。</returns>
    public async Task<IReadOnlyList<PendingCommandRecovery>> ReadPendingEnqueuesAsync(
        int shardId,
        DateTimeOffset recoveryAt,
        int take,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ArgumentOutOfRangeException.ThrowIfNegative(shardId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await schemaInitializer.EnsureInitializedAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.GetPendingEnqueuesSql(options.Value);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@Take", DbType.Int32, take);
        AddParameter(command, "@ShardId", DbType.Int32, shardId);
        AddParameter(command, "@RecoveryAt", DbType.DateTimeOffset, recoveryAt);
        AddParameter(command, "@EnqueuePendingStatus", DbType.Int32, (int)ReliableMessageStatus.EnqueuePending);

        var result = new List<PendingCommandRecovery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingCommandRecovery
            {
                DeviceId = reader.GetString(0),
                MsgId = reader.GetString(1),
                DeviceSequence = reader.GetInt64(2),
                DueAt = reader.GetFieldValue<DateTimeOffset>(3)
            });
        }

        DeviceCommandRuntimeMetrics.RecordSqlQuery("pending_enqueues", Stopwatch.GetElapsedTime(startedAt), result.Count);
        return result;
    }

    /// <summary>
    /// 按分片读取达到总保留期限的终态命令 MsgId，不读取二进制状态和 Payload。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="terminalThrough">允许删除的最晚终态时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>物理删除候选 MsgId。</returns>
    public Task<IReadOnlyList<string>> ReadCommandPurgeCandidatesAsync(
        int shardId,
        DateTimeOffset terminalThrough,
        int take,
        CancellationToken cancellationToken = default) => ReadCleanupCandidatesAsync(
            "purge_candidates",
            dialect.GetCommandPurgeCandidatesSql(options.Value),
            shardId,
            take,
            command => AddParameter(command, "@TerminalThrough", DbType.DateTimeOffset, terminalThrough),
            cancellationToken);

    /// <summary>
    /// 按分片读取已过热数据期限但尚未到删除期限、且 Payload 未压缩的终态命令 MsgId。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="compactThrough">允许压缩的最晚终态时间。</param>
    /// <param name="deleteThrough">允许直接删除的最晚终态时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>Payload 压缩候选 MsgId。</returns>
    public Task<IReadOnlyList<string>> ReadPayloadCompactionCandidatesAsync(
        int shardId,
        DateTimeOffset compactThrough,
        DateTimeOffset deleteThrough,
        int take,
        CancellationToken cancellationToken = default) => ReadCleanupCandidatesAsync(
            "compaction_candidates",
            dialect.GetPayloadCompactionCandidatesSql(options.Value),
            shardId,
            take,
            command =>
            {
                AddParameter(command, "@CompactThrough", DbType.DateTimeOffset, compactThrough);
                AddParameter(command, "@DeleteThrough", DbType.DateTimeOffset, deleteThrough);
            },
            cancellationToken);

    /// <summary>
    /// 执行一次设备队首查询并投影轻量索引，避免读取命令 Payload。
    /// </summary>
    /// <param name="connection">已经打开的数据库连接。</param>
    /// <param name="deviceId">目标设备编号。</param>
    /// <param name="now">选择时间。</param>
    /// <param name="eligibleOnly">是否只选择已到期命令。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>命中时返回命令索引，否则返回空。</returns>
    private async Task<DeviceCommandQueueEntry?> ReadNextCommandAsync(
        DbConnection connection,
        string deviceId,
        DateTimeOffset now,
        bool eligibleOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.GetNextCommandSql(options.Value, eligibleOnly);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@DeviceId", DbType.String, deviceId);
        AddParameter(command, "@Now", DbType.DateTimeOffset, now);
        AddParameter(command, "@QueuedStatus", DbType.Int32, (int)ReliableMessageStatus.Queued);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new DeviceCommandQueueEntry
        {
            MsgId = reader.GetString(0),
            DueAt = reader.GetFieldValue<DateTimeOffset>(1),
            Priority = reader.GetInt32(2),
            DeviceSequence = reader.GetInt64(3)
        };
    }

    /// <summary>
    /// 执行一种只投影 MsgId 的清理候选查询，并统一记录低基数 SQL 指标。
    /// </summary>
    /// <param name="operation">有限的指标操作名。</param>
    /// <param name="sql">数据库方言生成的查询。</param>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="addTimeParameters">增加本查询时间边界参数的回调。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>清理候选 MsgId。</returns>
    private async Task<IReadOnlyList<string>> ReadCleanupCandidatesAsync(
        string operation,
        string sql,
        int shardId,
        int take,
        Action<DbCommand> addTimeParameters,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ArgumentOutOfRangeException.ThrowIfNegative(shardId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await schemaInitializer.EnsureInitializedAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@Take", DbType.Int32, take);
        AddParameter(command, "@ShardId", DbType.Int32, shardId);
        addTimeParameters(command);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));

        DeviceCommandRuntimeMetrics.RecordSqlQuery(operation, Stopwatch.GetElapsedTime(startedAt), result.Count);
        return result;
    }

    /// <summary>
    /// 创建 ADO.NET 参数并统一处理数据库类型。
    /// </summary>
    /// <param name="command">数据库命令。</param>
    /// <param name="name">参数名。</param>
    /// <param name="type">通用数据库类型。</param>
    /// <param name="value">参数值。</param>
    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
