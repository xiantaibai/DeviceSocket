using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using Microsoft.Extensions.Hosting;
using Serilog;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Host;

namespace DeviceSocket.Host.Networking.Dlt645;

/// <summary>
/// 在 ASP.NET Core 进程内托管 DL/T 645-2007 SuperSocket TCP 服务。
/// </summary>
public sealed class Dlt645ServerWorker(
    ProtocolMessagePipeline<Dlt645Frame> pipeline,
    DeviceSessionLifecycleHandler sessionLifecycle,
    DeviceConnectionLimiter connectionLimiter,
    IConfiguration configuration,
    Serilog.ILogger serilogLogger,
    ILogger<Dlt645ServerWorker> logger) : BackgroundService
{
    private IHost? _server;

    /// <summary>
    /// 创建并启动 645 子宿主，直到外层应用请求停止。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = configuration.GetValue<int?>("DeviceSocket:Dlt645Port") ?? 5002;
        var backlog = configuration.GetValue<int?>("DeviceSocket:BackLog") ?? 8192;

        _server = SuperSocketHostBuilder.Create<Dlt645Frame, Dlt645PipelineFilter>()
            .UseMiddleware<DeviceConnectionLimitMiddleware>()
            .UsePackageHandler(pipeline.HandleAsync)
            .UseSessionHandler(
                session => sessionLifecycle.SessionOpenedAsync(session, DeviceProtocol.Dlt6452007),
                (session, closeEvent) => sessionLifecycle.SessionClosedAsync(session, DeviceProtocol.Dlt6452007, closeEvent.Reason.ToString()))
            .ConfigureSuperSocket(options =>
            {
                options.Name = "Dlt645Server";
                options.MaxPackageLength = Dlt645Codec.MaximumDataLength + 16;
                options.Listeners =
                [
                    new ListenOptions
                    {
                        Ip = "Any",
                        Port = port,
                        BackLog = backlog,
                        NoDelay = true
                    }
                ];
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(connectionLimiter);
                services.AddSingleton<IHostLifetime, NestedHostLifetime>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                // SuperSocket 会创建独立的子宿主，显式复用主宿主 Serilog，避免绕过统一日志分流和级别配置。
                logging.AddSerilog(serilogLogger, dispose: false);
            })
            .Build();

        await _server.StartAsync(stoppingToken);
        logger.LogInformation("DL/T 645-2007 server listening on TCP {Port}", port);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 释放 SuperSocket 子宿主及后台服务资源。
    /// </summary>
    public override void Dispose()
    {
        _server?.Dispose();
        base.Dispose();
    }
}
