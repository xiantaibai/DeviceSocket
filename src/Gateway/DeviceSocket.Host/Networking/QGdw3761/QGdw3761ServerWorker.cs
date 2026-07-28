using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using Microsoft.Extensions.Hosting;
using Serilog;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Host;

namespace DeviceSocket.Host.Networking.QGdw3761;

/// <summary>
/// 在 ASP.NET Core 进程内托管 Q/GDW 376.1 SuperSocket TCP 服务。
/// </summary>
public sealed class QGdw3761ServerWorker(
    ProtocolMessagePipeline<QGdw3761Frame> pipeline,
    DeviceSessionLifecycleHandler sessionLifecycle,
    DeviceConnectionLimiter connectionLimiter,
    IConfiguration configuration,
    Serilog.ILogger serilogLogger,
    ILogger<QGdw3761ServerWorker> logger) : BackgroundService
{
    private IHost? _server;

    /// <summary>
    /// 创建并启动 376.1 子宿主，直到外层应用请求停止。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = configuration.GetValue<int?>("DeviceSocket:QGdw3761Port") ?? 5001;
        var backlog = configuration.GetValue<int?>("DeviceSocket:BackLog") ?? 8192;

        _server = SuperSocketHostBuilder.Create<QGdw3761Frame, QGdw3761PipelineFilter>()
            .UseMiddleware<DeviceConnectionLimitMiddleware>()
            .UsePackageHandler(pipeline.HandleAsync)
            .UseSessionHandler(
                session => sessionLifecycle.SessionOpenedAsync(session, DeviceProtocol.QGdw3761),
                (session, closeEvent) => sessionLifecycle.SessionClosedAsync(session, DeviceProtocol.QGdw3761, closeEvent.Reason.ToString()))
            .ConfigureSuperSocket(options =>
            {
                options.Name = "QGdw3761Server";
                options.MaxPackageLength = QGdw3761Codec.MaximumUserDataLength + 8;
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
        logger.LogInformation("Q/GDW 376.1 server listening on TCP {Port}", port);

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
