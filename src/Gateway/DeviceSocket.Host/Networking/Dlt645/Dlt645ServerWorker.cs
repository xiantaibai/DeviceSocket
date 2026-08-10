using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Host;

namespace DeviceSocket.Host.Networking.Dlt645;

/// <summary>
/// 在 ASP.NET Core 进程内托管 DL/T 645-2007 SuperSocket TCP 服务。
/// </summary>
/// <param name="pipeline">645 上行报文公共处理管道。</param>
/// <param name="sessionLifecycle">设备 Session 建立和关闭生命周期处理器。</param>
/// <param name="connectionLimiter">两个协议服务共用的进程级连接配额。</param>
/// <param name="connectionOptions">已经完成启动校验的 TCP 连接配置。</param>
/// <param name="listenerOptions">已经完成启动校验的协议监听配置。</param>
/// <param name="serilogLogger">供 SuperSocket 子宿主复用的 Serilog 日志器。</param>
/// <param name="logger">记录服务启动和 Socket 配置告警的程序日志器。</param>
public sealed class Dlt645ServerWorker(
    ProtocolMessagePipeline<Dlt645Frame> pipeline,
    DeviceSessionLifecycleHandler sessionLifecycle,
    DeviceConnectionLimiter connectionLimiter,
    IOptions<DeviceConnectionOptions> connectionOptions,
    IOptions<DeviceTcpListenersOptions> listenerOptions,
    Serilog.ILogger serilogLogger,
    ILogger<Dlt645ServerWorker> logger) : BackgroundService
{
    private IHost? _server;

    /// <summary>
    /// 创建并启动 645 子宿主，直到外层应用请求停止。
    /// </summary>
    /// <param name="stoppingToken">外层宿主停止时触发的取消令牌。</param>
    /// <returns>子宿主停止后完成的后台任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = listenerOptions.Value.Dlt645;
        var tcpOptions = connectionOptions.Value;

        // SuperSocket 官方空闲清理中间件依赖进程内 Session 容器；二者必须同时注册到当前子宿主。
        _server = SuperSocketHostBuilder.Create<Dlt645Frame, Dlt645PipelineFilter>()
            .UseMiddleware<DeviceConnectionLimitMiddleware>()
            .UseInProcSessionContainer()
            .UseClearIdleSession()
            .UsePackageHandler(pipeline.HandleAsync)
            .UseSessionHandler(
                session => sessionLifecycle.SessionOpenedAsync(session, DeviceProtocol.Dlt6452007),
                (session, closeEvent) => sessionLifecycle.SessionClosedAsync(session, DeviceProtocol.Dlt6452007, closeEvent.Reason))
            .ConfigureSocketOptions(socket => DeviceTcpSocketConfigurator.Configure(socket, tcpOptions, logger))
            .ConfigureSuperSocket(options =>
            {
                options.Name = "Dlt645Server";
                options.MaxPackageLength = Dlt645Codec.MaximumDataLength + 16;
                options.ReceiveBufferSize = tcpOptions.ReceiveBufferSize;
                options.SendBufferSize = tcpOptions.SendBufferSize;
                // SuperSocket 的 SendTimeout 使用毫秒并作用于框架可能使用的同步 Socket 路径；
                // 当前异步下发的确定性超时由 DeviceTcpSessionManager 传入同一时长的取消令牌保证。
                options.SendTimeout = tcpOptions.SendTimeoutSeconds * 1000;
                options.PackageHandlingTimeOut = tcpOptions.PackageHandlingTimeoutSeconds;
                options.ClearIdleSessionInterval = tcpOptions.IdleScanIntervalSeconds;
                options.IdleSessionTimeOut = tcpOptions.IdleTimeoutSeconds;
                options.Listeners =
                [
                    new ListenOptions
                    {
                        Ip = listener.Ip,
                        Port = listener.Port,
                        // BackLog 仅控制已完成握手但尚未被 Accept 的连接队列，不代表在线 Session 上限。
                        BackLog = listener.BackLog,
                        NoDelay = listener.NoDelay
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
        TcpLogMessages.Dlt645ServerStarted(logger, listener.Ip, listener.Port, listener.BackLog);

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
