using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Dlt645.Processing;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Host.Networking.QGdw3761.Commands;
using DeviceSocket.Host.Networking.QGdw3761.Processing;
using DeviceSocket.Host.Networking.QGdw3761.Responses;

namespace DeviceSocket.Host.Networking.Processing;

/// <summary>
/// 集中注册设备协议消息管道、分发器和自动发现的指令 Handler。
/// </summary>
public static class DeviceProtocolProcessingExtensions
{
    /// <summary>
    /// 注册当前 Gateway 支持的全部协议处理能力；Handler 实现由程序集扫描自动加入。
    /// </summary>
    /// <param name="configuration">用于绑定协议专属强类型配置的应用配置根。</param>
    public static IServiceCollection AddDeviceProtocolProcessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<DeviceConnectionCoordinator>();
        services.AddSingleton<DeviceSessionLifecycleHandler>();
        services.AddSingleton<QGdw3761LinkConfirmationSender>();
        services.AddOptions<QGdw3761CommandCorrelationOptions>()
            .Bind(configuration.GetSection(QGdw3761CommandCorrelationOptions.SectionName))
            .Validate(options => options.MaxPendingCommands > 0, "376.1 MaxPendingCommands 必须大于零。")
            .Validate(options => options.CleanupIntervalSeconds > 0, "376.1 CleanupIntervalSeconds 必须大于零。")
            .ValidateOnStart();
        services.AddSingleton<QGdw3761CommandCorrelationRegistry>();
        services.AddSingleton<IDeviceSessionResourceCleaner>(services =>
            services.GetRequiredService<QGdw3761CommandCorrelationRegistry>());
        services.AddHostedService<QGdw3761CommandCorrelationCleanupWorker>();

        services.AddSingleton<
            IProtocolInstructionResolver<QGdw3761Frame, QGdw3761InstructionKey>,
            QGdw3761InstructionResolver>();
        services.AddSingleton<ProtocolInstructionDispatcher<QGdw3761Frame, QGdw3761InstructionKey>>();
        services.AddSingleton<IProtocolMessageDispatcher<QGdw3761Frame>, QGdw3761MessageDispatcher>();
        services.AddSingleton<ProtocolMessagePipeline<QGdw3761Frame>>();

        services.AddSingleton<
            IProtocolInstructionResolver<Dlt645Frame, Dlt645InstructionKey>,
            Dlt645InstructionResolver>();
        services.AddSingleton<ProtocolInstructionDispatcher<Dlt645Frame, Dlt645InstructionKey>>();
        services.AddSingleton<IProtocolMessageDispatcher<Dlt645Frame>, Dlt645MessageDispatcher>();
        services.AddSingleton<ProtocolMessagePipeline<Dlt645Frame>>();

        services.AddProtocolInstructionHandlers(typeof(DeviceProtocolProcessingExtensions).Assembly);
        return services;
    }
}
