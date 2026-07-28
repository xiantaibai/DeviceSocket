using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Api;
using DeviceSocket.Host.Configuration.Logging;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Dlt645;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Processing;
using DeviceSocket.Host.Networking.QGdw3761;
using DeviceSocket.Host.Observability;
using DeviceSocket.Host.Rpc;
using DeviceSocket.Infrastructure.Memory.Connections;
using DeviceSocket.Infrastructure.Memory.Messages;
using MagicOnion.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddDeviceSerilog();
var httpPort = builder.Configuration.GetValue<int?>("DeviceSocket:HttpPort") ?? 5080;
var rpcPort = builder.Configuration.GetValue<int?>("Gateway:RpcPort") ?? 7001;
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort, listen => listen.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(rpcPort, listen => listen.Protocols = HttpProtocols.Http2);
});
builder.Services.AddProblemDetails();
builder.Services.AddDeviceSwagger();
builder.Services.AddMagicOnion();
builder.Services.AddSingleton<IOnlineDeviceRegistry, MemoryOnlineDeviceRegistry>();
builder.Services.AddSingleton<IRecentStore<ReceivedDeviceMessage>>(_ => new MemoryRecentStore<ReceivedDeviceMessage>(10_000));
builder.Services.AddSingleton<IRecentStore<DeviceErrorEvent>>(_ => new MemoryRecentStore<DeviceErrorEvent>(1_000));
builder.Services.AddSingleton<RuntimeMetrics>();
builder.Services.AddSingleton<DeviceConnectionLimiter>();
builder.Services.AddSingleton<IDeviceTrafficSink, SerilogDeviceTrafficSink>();
builder.Services.AddDeviceProtocolProcessing();
builder.Services.Configure<GatewayNodeOptions>(builder.Configuration.GetSection(GatewayNodeOptions.SectionName));
builder.Services.AddSingleton<GatewayCommandDeduplicator>();

var orleansEnabled = builder.Configuration.GetValue<bool?>("Orleans:Enabled") ?? true;
if (orleansEnabled)
{
    var gatewayPort = builder.Configuration.GetValue<int?>("Orleans:GatewayPort") ?? 30000;
    builder.Host.UseOrleansClient(client => client.UseLocalhostClustering(gatewayPort));
    builder.Services.AddSingleton<OrleansDeviceRoutePublisher>();
    builder.Services.AddSingleton<IDeviceRoutePublisher>(services => services.GetRequiredService<OrleansDeviceRoutePublisher>());
    builder.Services.AddHostedService(services => services.GetRequiredService<OrleansDeviceRoutePublisher>());
    builder.Services.AddHostedService<GatewayNodeLeaseWorker>();
}
else
{
    builder.Services.AddSingleton<IDeviceRoutePublisher, NullDeviceRoutePublisher>();
}

builder.Services.AddHostedService<QGdw3761ServerWorker>();
builder.Services.AddHostedService<Dlt645ServerWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDeviceSwagger();
app.MapDeviceApi();
app.MapMagicOnionService();
app.Run();
