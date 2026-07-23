using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Api;
using DeviceSocket.Host.Networking.Dlt645;
using DeviceSocket.Host.Networking.Processing;
using DeviceSocket.Host.Networking.QGdw3761;
using DeviceSocket.Infrastructure.Memory.Connections;
using DeviceSocket.Infrastructure.Memory.Messages;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["DeviceSocket:HttpUrl"] ?? "http://0.0.0.0:5080");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
});

builder.Services.AddProblemDetails();
builder.Services.AddDeviceSwagger();
builder.Services.AddSingleton<IOnlineDeviceRegistry, MemoryOnlineDeviceRegistry>();
builder.Services.AddSingleton<IRecentStore<ReceivedDeviceMessage>>(_ => new MemoryRecentStore<ReceivedDeviceMessage>(10_000));
builder.Services.AddSingleton<IRecentStore<DeviceErrorEvent>>(_ => new MemoryRecentStore<DeviceErrorEvent>(1_000));
builder.Services.AddSingleton<RuntimeMetrics>();
builder.Services.AddSingleton<DeviceMessageProcessor>();
builder.Services.AddHostedService<QGdw3761ServerWorker>();
builder.Services.AddHostedService<Dlt645ServerWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDeviceSwagger();
app.MapDeviceApi();
app.Run();
