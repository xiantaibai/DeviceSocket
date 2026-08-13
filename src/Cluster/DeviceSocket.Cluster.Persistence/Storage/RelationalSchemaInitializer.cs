using DeviceSocket.Cluster.Persistence.Configuration;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 使用数据库方言幂等创建应用 Grain 独立表，并在进程内只执行一次。
/// </summary>
public sealed class RelationalSchemaInitializer(
    IRelationalConnectionFactory connectionFactory,
    IRelationalGrainStorageDialect dialect,
    IOptions<RelationalGrainStorageOptions> options)
    : IRelationalSchemaInitializer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// 首次调用时顺序执行方言 DDL；后续调用无数据库往返。
    /// </summary>
    /// <param name="cancellationToken">取消信号。</param>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            if (options.Value.AutoCreateSchema)
            {
                await using var connection = connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                foreach (var statement in dialect.GetSchemaStatements(options.Value))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.CommandTimeout = options.Value.CommandTimeoutSeconds;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
