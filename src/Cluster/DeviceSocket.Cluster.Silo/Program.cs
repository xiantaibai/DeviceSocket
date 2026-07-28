using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Silo.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        // Windows 普通用户默认不能写系统事件日志，独立 Silo 统一使用控制台日志避免启动失败。
        logging.ClearProviders();
        logging.AddSimpleConsole(options => options.SingleLine = true);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<IGatewayCommandInvoker, GatewayCommandInvoker>();
    })
    .UseOrleans(silo =>
    {
        // 本机开发阶段使用单 Silo 与内存存储，正式部署时在此替换集群发现和持久化提供程序。
        silo.UseLocalhostClustering()
            .AddMemoryGrainStorage("DeviceStore")
            .AddMemoryGrainStorage("GatewayStore")
            .AddMemoryGrainStorage("CommandStore")
            .UseInMemoryReminderService();
    })
    .Build();

await host.RunAsync();
