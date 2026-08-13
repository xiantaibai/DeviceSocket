using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.SqlServer;
using DeviceSocket.Cluster.Persistence.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace DeviceSocket.Cluster.Persistence.Hosting;

/// <summary>
/// 为 Silo 注册 SQL Server 独立 Grain 表和可替换调度查询抽象。
/// </summary>
public static class RelationalPersistenceSiloBuilderExtensions
{
    /// <summary>
    /// 绑定并验证关系型存储配置，为四种 Grain 类型分别注册命名存储提供程序。
    /// </summary>
    /// <param name="builder">Orleans Silo 构建器。</param>
    /// <param name="configuration">宿主配置根节点。</param>
    /// <returns>原 Silo 构建器。</returns>
    public static ISiloBuilder AddDeviceSocketSqlServerPersistence(
        this ISiloBuilder builder,
        IConfiguration configuration)
    {
        builder.Services
            .AddOptions<RelationalGrainStorageOptions>()
            .Bind(configuration.GetSection(RelationalGrainStorageOptions.SectionName))
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RelationalGrainStorageOptions>, RelationalGrainStorageOptionsValidator>());
        builder.Services.TryAddSingleton<IRelationalConnectionFactory, SqlServerConnectionFactory>();
        builder.Services.TryAddSingleton<IRelationalGrainStorageDialect, SqlServerRelationalGrainStorageDialect>();
        builder.Services.TryAddSingleton<IRelationalSchemaInitializer, RelationalSchemaInitializer>();
        builder.Services.TryAddSingleton<ICommandSchedulingStore, SqlServerCommandSchedulingStore>();

        AddStore(builder.Services, "DeviceStore", RelationalGrainStateTable.Device);
        AddStore(builder.Services, "CommandStore", RelationalGrainStateTable.Command);
        AddStore(builder.Services, "GatewayStore", RelationalGrainStateTable.Gateway);
        AddStore(builder.Services, "SchedulerStore", RelationalGrainStateTable.Scheduler);
        return builder;
    }

    /// <summary>
    /// 注册一个命名 IGrainStorage，使 PersistentState 的 providerName 精确映射到独立表。
    /// </summary>
    /// <param name="services">Silo 服务集合。</param>
    /// <param name="providerName">PersistentState 使用的提供程序名。</param>
    /// <param name="table">对应 Grain 类型表。</param>
    private static void AddStore(
        IServiceCollection services,
        string providerName,
        RelationalGrainStateTable table)
    {
        services.AddGrainStorage<RelationalGrainStorage>(
            providerName,
            (serviceProvider, _) => new RelationalGrainStorage(
                table,
                serviceProvider.GetRequiredService<IRelationalConnectionFactory>(),
                serviceProvider.GetRequiredService<IRelationalGrainStorageDialect>(),
                serviceProvider.GetRequiredService<IRelationalSchemaInitializer>(),
                serviceProvider.GetRequiredService<IGrainStorageSerializer>(),
                serviceProvider.GetRequiredService<IOptions<RelationalGrainStorageOptions>>()));
    }
}
