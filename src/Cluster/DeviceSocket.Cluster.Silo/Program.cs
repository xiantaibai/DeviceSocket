using DeviceSocket.Cluster.Silo.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        // Windows 普通用户默认不能写系统事件日志，独立 Silo 使用控制台日志避免启动失败。
        logging.ClearProviders();
        logging.AddSimpleConsole(options => options.SingleLine = true);
    })
    .ConfigureServices((context, services) =>
        services.AddDeviceSocketSiloServices(context.Configuration))
    .UseOrleans((context, silo) =>
        silo.ConfigureDeviceSocketSilo(context.Configuration))
    .Build();

await host.RunAsync();
