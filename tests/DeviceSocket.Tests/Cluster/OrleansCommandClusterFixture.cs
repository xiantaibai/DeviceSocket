using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 为可靠命令闭环测试启动单 Silo Orleans 测试集群，并注入可观测 Gateway 与内存命令索引。
/// </summary>
public sealed class OrleansCommandClusterFixture : IAsyncLifetime
{
    /// <summary>测试期间共享的 Orleans 集群。</summary>
    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// 构建并部署包含四个命名内存存储和持久化 Reminder 模拟的测试集群。
    /// </summary>
    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<CommandSiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    /// <summary>
    /// 停止测试集群并释放 Silo 进程资源。
    /// </summary>
    public async Task DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.StopAllSilosAsync();
    }

    /// <summary>
    /// 测试 Silo 配置器，使用内存状态模拟 SQL 表，并保留与生产相同的 Grain 状态机和调度逻辑。
    /// </summary>
    public sealed class CommandSiloConfigurator : ISiloConfigurator
    {
        /// <summary>
        /// 注册四种 Grain 命名存储、内存 Reminder、调度参数和测试替身。
        /// </summary>
        /// <param name="siloBuilder">测试 Silo 构建器。</param>
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("DeviceStore")
                .AddMemoryGrainStorage("CommandStore")
                .AddMemoryGrainStorage("GatewayStore")
                .AddMemoryGrainStorage("SchedulerStore")
                .UseInMemoryReminderService();
            siloBuilder.Services.Configure<DeviceCommandSchedulingOptions>(options =>
            {
                options.ShardCount = 16;
                options.RecoveryHorizon = TimeSpan.FromSeconds(5);
                options.RecoveryPeriod = TimeSpan.FromMinutes(1);
                options.BatchSize = 64;
                options.MaxBatchesPerTick = 4;
                options.DevicePumpLimit = 16;
                options.DefaultResponseTimeout = TimeSpan.FromMilliseconds(80);
                options.DefaultMaxAttempts = 3;
                options.UncertainRetryDelay = TimeSpan.FromMilliseconds(30);
                options.RetryDelayMilliseconds = [20, 30, 40];
                options.EnqueueRecoveryDelay = TimeSpan.FromMilliseconds(50);
                options.RoutePersistenceInterval = TimeSpan.FromSeconds(30);
            });
            siloBuilder.Services.AddSingleton<InMemoryCommandSchedulingStore>();
            siloBuilder.Services.AddSingleton<ICommandSchedulingStore>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryCommandSchedulingStore>());
            siloBuilder.Services.AddSingleton<TestGatewayCommandInvoker>();
            siloBuilder.Services.AddSingleton<IGatewayCommandInvoker>(serviceProvider =>
                serviceProvider.GetRequiredService<TestGatewayCommandInvoker>());
        }
    }
}
