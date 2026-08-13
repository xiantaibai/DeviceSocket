using System.Net;
using System.Data.Common;
using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.Hosting;
using DeviceSocket.Cluster.Silo.Commands;
using DeviceSocket.Cluster.Silo.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Orleans.Configuration;
using Orleans.Hosting;

namespace DeviceSocket.Cluster.Silo.Configuration;

/// <summary>
/// 集中维护 Silo 的强类型选项、SQL Server 集群、Reminder 和应用 Grain 存储注册。
/// </summary>
public static class OrleansSiloConfiguration
{
    private const string SqlClientInvariant = "Microsoft.Data.SqlClient";

    /// <summary>
    /// 注册 Silo 级服务和全部强类型选项验证。
    /// </summary>
    /// <param name="services">宿主服务集合。</param>
    /// <param name="configuration">宿主配置。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddDeviceSocketSiloServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IGatewayCommandInvoker, GatewayCommandInvoker>();
        services
            .AddOptions<OrleansClusterRuntimeOptions>()
            .Bind(configuration.GetSection(OrleansClusterRuntimeOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OrleansClusterRuntimeOptions>, OrleansClusterRuntimeOptionsValidator>());
        services
            .AddOptions<DeviceCommandSchedulingOptions>()
            .Bind(configuration.GetSection(DeviceCommandSchedulingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<DeviceCommandSchedulingOptions>, DeviceCommandSchedulingOptionsValidator>());
        services.AddHostedService<DeviceCommandSchedulerBootstrapService>();
        return services;
    }

    /// <summary>
    /// 使用 SQL Server 成员表、持久化 Reminder 和每种 Grain 类型独立状态表配置 Orleans。
    /// </summary>
    /// <param name="silo">Orleans Silo 构建器。</param>
    /// <param name="configuration">宿主配置。</param>
    /// <returns>原 Silo 构建器。</returns>
    public static ISiloBuilder ConfigureDeviceSocketSilo(
        this ISiloBuilder silo,
        IConfiguration configuration)
    {
        RegisterSqlClientFactory();
        var cluster = configuration
            .GetSection(OrleansClusterRuntimeOptions.SectionName)
            .Get<OrleansClusterRuntimeOptions>() ?? new OrleansClusterRuntimeOptions();
        var persistence = configuration
            .GetSection(RelationalGrainStorageOptions.SectionName)
            .Get<RelationalGrainStorageOptions>() ?? new RelationalGrainStorageOptions();

        silo.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = cluster.ClusterId;
                options.ServiceId = cluster.ServiceId;
            })
            .ConfigureEndpoints(
                IPAddress.Parse(cluster.AdvertisedIPAddress),
                cluster.SiloPort,
                cluster.GatewayPort,
                listenOnAnyHostAddress: true)
            .UseAdoNetClustering(options =>
            {
                options.Invariant = SqlClientInvariant;
                options.ConnectionString = persistence.ConnectionString;
            })
            .UseAdoNetReminderService(options =>
            {
                options.Invariant = SqlClientInvariant;
                options.ConnectionString = persistence.ConnectionString;
            })
            .AddDeviceSocketSqlServerPersistence(configuration);
        return silo;
    }

    /// <summary>
    /// 显式注册 Microsoft.Data.SqlClient 工厂，确保 Orleans ADO.NET Membership 和 Reminder 能按 invariant 创建连接。
    /// </summary>
    private static void RegisterSqlClientFactory()
    {
        DbProviderFactories.RegisterFactory(SqlClientInvariant, SqlClientFactory.Instance);
    }
}
