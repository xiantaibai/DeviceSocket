using System.Data;
using System.Data.Common;
using System.Globalization;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Grains.Devices;
using DeviceSocket.Cluster.Grains.Gateways;
using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Cluster.Persistence.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Storage;

namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 把一种应用 Grain 状态写入其独立关系表，并使用 ETag 防止旧激活覆盖新状态。
/// </summary>
public sealed class RelationalGrainStorage(
    RelationalGrainStateTable table,
    IRelationalConnectionFactory connectionFactory,
    IRelationalGrainStorageDialect dialect,
    IRelationalSchemaInitializer schemaInitializer,
    IGrainStorageSerializer serializer,
    IOptions<RelationalGrainStorageOptions> options)
    : IGrainStorage
{
    private const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 按 GrainId 读取 ETag 和 Orleans 二进制状态；记录不存在时保留 Orleans 创建的默认状态。
    /// </summary>
    /// <typeparam name="T">具体 Grain 状态类型。</typeparam>
    /// <param name="stateName">Grain 类声明的状态名称。</param>
    /// <param name="grainId">Orleans 完整 GrainId。</param>
    /// <param name="grainState">需要填充的状态容器。</param>
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await schemaInitializer.EnsureInitializedAsync();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.GetReadStateSql(table, options.Value);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@GrainId", DbType.String, grainId.ToString());

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
        {
            grainState.RecordExists = false;
            grainState.ETag = null;
            return;
        }

        var etag = reader.GetInt64(0);
        var payload = (byte[])reader.GetValue(1);
        grainState.State = serializer.Deserialize<T>(new BinaryData(payload));
        grainState.ETag = etag.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
    }

    /// <summary>
    /// 首次状态使用 INSERT，已有状态使用带旧 ETag 条件的 UPDATE，并同步维护结构化调度索引列。
    /// </summary>
    /// <typeparam name="T">具体 Grain 状态类型。</typeparam>
    /// <param name="stateName">Grain 类声明的状态名称。</param>
    /// <param name="grainId">Orleans 完整 GrainId。</param>
    /// <param name="grainState">待持久化状态和当前 ETag。</param>
    /// <exception cref="InconsistentStateException">ETag 已被其他激活修改或首次插入发生主键竞争。</exception>
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await schemaInitializer.EnsureInitializedAsync();
        var existing = grainState.RecordExists || !string.IsNullOrWhiteSpace(grainState.ETag);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = existing
            ? dialect.GetUpdateStateSql(table, options.Value)
            : dialect.GetInsertStateSql(table, options.Value);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@GrainId", DbType.String, grainId.ToString());
        AddParameter(command, "@SchemaVersion", DbType.Int32, CurrentSchemaVersion);
        AddParameter(command, "@State", DbType.Binary, serializer.Serialize(grainState.State).ToArray());
        AddParameter(command, "@UpdatedAt", DbType.DateTimeOffset, DateTimeOffset.UtcNow);
        if (existing)
            AddParameter(command, "@ETag", DbType.Int64, ParseEtag(grainState.ETag));
        AddIndexParameters(command, grainId, grainState.State);

        try
        {
            var result = await command.ExecuteScalarAsync();
            if (result is null || result is DBNull)
                throw CreateConcurrencyException(grainId, grainState.ETag, null);

            grainState.ETag = Convert.ToInt64(result, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            grainState.RecordExists = true;
        }
        catch (InconsistentStateException)
        {
            throw;
        }
        catch (DbException exception) when (!existing && dialect.IsUniqueConstraintViolation(exception))
        {
            throw new InconsistentStateException(
                $"首次写入 Grain 状态时检测到并发记录: {grainId}",
                storedEtag: "unknown",
                currentEtag: grainState.ETag ?? "null",
                exception);
        }
    }

    /// <summary>
    /// 使用 GrainId 和旧 ETag 删除状态；并发不一致时拒绝静默删除新状态。
    /// </summary>
    /// <typeparam name="T">具体 Grain 状态类型。</typeparam>
    /// <param name="stateName">Grain 类声明的状态名称。</param>
    /// <param name="grainId">Orleans 完整 GrainId。</param>
    /// <param name="grainState">最后读取的状态和 ETag。</param>
    /// <exception cref="InconsistentStateException">记录存在但 ETag 已改变。</exception>
    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (!grainState.RecordExists && string.IsNullOrWhiteSpace(grainState.ETag))
            return;

        await schemaInitializer.EnsureInitializedAsync();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.GetDeleteStateSql(table, options.Value);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        AddParameter(command, "@GrainId", DbType.String, grainId.ToString());
        AddParameter(command, "@ETag", DbType.Int64, ParseEtag(grainState.ETag));
        var affected = await command.ExecuteNonQueryAsync();
        if (affected == 0)
            throw CreateConcurrencyException(grainId, grainState.ETag, null);

        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    /// <summary>
    /// 根据当前独立表类型提取调度查询所需列，完整状态仍由 Orleans 二进制序列化保存。
    /// </summary>
    /// <typeparam name="T">具体 Grain 状态类型。</typeparam>
    /// <param name="command">待增加参数的数据库命令。</param>
    /// <param name="grainId">Orleans 完整 GrainId。</param>
    /// <param name="value">当前 Grain 状态。</param>
    /// <exception cref="InvalidOperationException">命名存储与实际状态类型不匹配。</exception>
    private void AddIndexParameters<T>(DbCommand command, GrainId grainId, T value)
    {
        switch (table, value)
        {
            case (RelationalGrainStateTable.Device, DeviceGrainState device):
                AddParameter(command, "@DeviceId", DbType.String, device.DeviceId);
                AddParameter(command, "@SchedulerShard", DbType.Int32, device.SchedulerShard);
                AddParameter(command, "@HasPendingWork", DbType.Boolean, device.HasPendingWork);
                AddParameter(command, "@NextWakeAt", DbType.DateTimeOffset, device.NextWakeAt);
                AddParameter(command, "@ScheduleVersion", DbType.Int64, device.ScheduleVersion);
                break;
            case (RelationalGrainStateTable.Command, DeviceCommandGrainState commandState):
                var snapshot = commandState.Snapshot;
                AddParameter(command, "@MsgId", DbType.String, snapshot.MsgId);
                AddParameter(command, "@DeviceId", DbType.String, snapshot.DeviceId);
                AddParameter(command, "@Status", DbType.Int32, (int)snapshot.Status);
                AddParameter(command, "@Priority", DbType.Int32, snapshot.Priority);
                AddParameter(command, "@DeviceSequence", DbType.Int64, snapshot.DeviceSequence);
                AddParameter(command, "@NotBefore", DbType.DateTimeOffset, snapshot.NotBefore);
                AddParameter(command, "@ExpiresAt", DbType.DateTimeOffset, snapshot.ExpiresAt);
                AddParameter(command, "@NextActionAt", DbType.DateTimeOffset, snapshot.NextRetryAt);
                AddParameter(command, "@EnqueueRecoveryAt", DbType.DateTimeOffset, snapshot.EnqueueRecoveryAt);
                AddParameter(command, "@SchedulerShard", DbType.Int32, snapshot.SchedulerShard);
                AddParameter(command, "@TerminalAt", DbType.DateTimeOffset, snapshot.TerminalAt);
                AddParameter(command, "@PayloadCompactedAt", DbType.DateTimeOffset, snapshot.PayloadCompactedAt);
                break;
            case (RelationalGrainStateTable.Gateway, GatewayNodeState gateway):
                AddParameter(command, "@GatewayNodeId", DbType.String, gateway.Snapshot.GatewayNodeId);
                AddParameter(command, "@LeaseExpiresAt", DbType.DateTimeOffset, gateway.Snapshot.LeaseExpiresAt == default ? null : gateway.Snapshot.LeaseExpiresAt);
                break;
            case (RelationalGrainStateTable.Scheduler, DeviceCommandSchedulerGrainState scheduler):
                AddParameter(command, "@ShardId", DbType.Int32, checked((int)grainId.GetIntegerKey()));
                AddParameter(command, "@LastRecoveryAt", DbType.DateTimeOffset, scheduler.LastRecoveryAt);
                break;
            default:
                throw new InvalidOperationException($"存储 {table} 不能写入状态类型 {typeof(T).FullName}。");
        }
    }

    /// <summary>
    /// 创建数据库参数并统一把空值转换为 DBNull。
    /// </summary>
    /// <param name="command">数据库命令。</param>
    /// <param name="name">参数名。</param>
    /// <param name="type">通用 ADO.NET 类型。</param>
    /// <param name="value">参数值。</param>
    private static void AddParameter(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        if (type == DbType.Binary)
            parameter.Size = -1;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// 把 Orleans 字符串 ETag 解析为 SQL bigint。
    /// </summary>
    /// <param name="etag">当前状态 ETag。</param>
    /// <returns>数值 ETag。</returns>
    /// <exception cref="InconsistentStateException">ETag 为空或不是有效整数。</exception>
    private static long ParseEtag(string? etag)
    {
        if (!long.TryParse(etag, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new InconsistentStateException($"关系型 Grain 状态 ETag 无效: {etag ?? "null"}");
        return value;
    }

    /// <summary>
    /// 创建包含当前 ETag 的 Orleans 乐观并发异常。
    /// </summary>
    /// <param name="grainId">发生冲突的 GrainId。</param>
    /// <param name="currentEtag">当前激活持有的 ETag。</param>
    /// <param name="storedEtag">数据库实际 ETag；未知时为空。</param>
    /// <returns>Orleans 可识别的状态不一致异常。</returns>
    private static InconsistentStateException CreateConcurrencyException(
        GrainId grainId,
        string? currentEtag,
        string? storedEtag) => new(
            $"Grain 状态乐观并发冲突: {grainId}",
            storedEtag ?? "unknown",
            currentEtag ?? "null");
}
