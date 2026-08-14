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
using System.Net;

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
builder.Services.AddSingleton<GatewayProcessMetrics>();
builder.Services.AddOptions<DeviceTcpListenersOptions>()
    .Bind(builder.Configuration.GetSection(DeviceTcpListenersOptions.SectionName))
    .Validate(
        options => IsValidDeviceTcpListener(options.QGdw3761),
        "DeviceSocket:Listeners:QGdw3761 必须配置有效的 IP、端口和 BackLog。")
    .Validate(
        options => IsValidDeviceTcpListener(options.Dlt645),
        "DeviceSocket:Listeners:Dlt645 必须配置有效的 IP、端口和 BackLog。")
    .ValidateOnStart();
builder.Services.AddOptions<DeviceConnectionOptions>()
    .Bind(builder.Configuration.GetSection(DeviceConnectionOptions.SectionName))
    .Validate(options => options.MaxConnections > 0, "MaxConnections 必须大于 0。")
    .Validate(
        options => options.MaxUnauthenticatedConnections > 0 &&
                   options.MaxUnauthenticatedConnections <= options.MaxConnections,
        "MaxUnauthenticatedConnections 必须大于 0 且不能超过 MaxConnections。")
    .Validate(options => options.MaxConnectionsPerIp >= 0, "MaxConnectionsPerIp 不能小于 0。")
    .Validate(options => options.MaxConnectionsPerSecond >= 0, "MaxConnectionsPerSecond 不能小于 0。")
    .Validate(options => options.LoginTimeoutSeconds > 0, "LoginTimeoutSeconds 必须大于 0。")
    .Validate(options => options.IdleTimeoutSeconds > 0, "IdleTimeoutSeconds 必须大于 0。")
    .Validate(options => options.IdleScanIntervalSeconds > 0, "IdleScanIntervalSeconds 必须大于 0。")
    .Validate(options => options.MaxConcurrentSessionCloses > 0, "MaxConcurrentSessionCloses 必须大于 0。")
    .Validate(options => options.MaxConsecutiveProtocolErrors > 0, "MaxConsecutiveProtocolErrors 必须大于 0。")
    .Validate(options => options.SendTimeoutSeconds > 0, "SendTimeoutSeconds 必须大于 0。")
    .Validate(options => options.MaxPendingSendMessagesPerSession > 0, "MaxPendingSendMessagesPerSession 必须大于 0。")
    .Validate(options => options.MaxPendingSendBytesPerSession > 0, "MaxPendingSendBytesPerSession 必须大于 0。")
    .Validate(
        options => options.MaxPendingSendBytes >= options.MaxPendingSendBytesPerSession,
        "MaxPendingSendBytes 不能小于单 Session 的发送字节上限。")
    .Validate(options => options.ReceiveBufferSize > 0, "ReceiveBufferSize 必须大于 0。")
    .Validate(options => options.SendBufferSize > 0, "SendBufferSize 必须大于 0。")
    .Validate(options => options.PackageHandlingTimeoutSeconds > 0, "PackageHandlingTimeoutSeconds 必须大于 0。")
    .Validate(
        options => !options.TcpKeepAliveEnabled ||
                   options.TcpKeepAliveTimeSeconds > 0 &&
                   options.TcpKeepAliveIntervalSeconds > 0 &&
                   options.TcpKeepAliveRetryCount > 0,
        "启用 TCP KeepAlive 时，时间、间隔和重试次数必须大于 0。")
    .ValidateOnStart();
builder.Services.AddSingleton<DeviceConnectionLimiter>();
builder.Services.AddSingleton<DeviceTcpSessionManager>();
// BackgroundService 不需要业务代码手动调用，外层 ASP.NET Core Generic Host 会自动启动 ExecuteAsync。
builder.Services.AddHostedService<DeviceTcpSessionCleanupWorker>();
builder.Services.AddSingleton<IDeviceTrafficSink, DeviceTrafficLogSink>();
builder.Services.AddDeviceProtocolProcessing(builder.Configuration);
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
    builder.Services.AddSingleton<IDeviceCommandReplyPublisher, OrleansDeviceCommandReplyPublisher>();
    builder.Services.AddHostedService<GatewayNodeLeaseWorker>();
}
else
{
    builder.Services.AddSingleton<IDeviceRoutePublisher, NullDeviceRoutePublisher>();
    builder.Services.AddSingleton<IDeviceCommandReplyPublisher, NullDeviceCommandReplyPublisher>();
}

builder.Services.AddHostedService<QGdw3761ServerWorker>();
builder.Services.AddHostedService<Dlt645ServerWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDeviceSwagger();
app.MapDeviceApi();
app.MapMagicOnionService();
app.Run();

// SuperSocket 只识别 Any、IPv6Any 或具体 IP；提前校验可避免子宿主启动后才暴露监听配置错误。
static bool IsValidDeviceTcpListener(DeviceTcpListenerOptions? options)
{
    if (options is null || options.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort || options.BackLog <= 0)
    {
        return false;
    }

    return string.Equals(options.Ip, "Any", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(options.Ip, "IPv6Any", StringComparison.OrdinalIgnoreCase) ||
           IPAddress.TryParse(options.Ip, out _);
}
