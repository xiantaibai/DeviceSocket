using DeviceSocket.Host.Observability;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// Gateway 的 Serilog 程序日志、设备上行日志和设备下发日志分流配置。
/// </summary>
public static class SerilogLoggingExtensions
{
    private const string ConfigurationPath = "Configuration/Logging/serilog.json";
    /// <summary>
    /// 加载独立日志配置，并将程序、设备上行和设备下发日志分别写入独立异步 Sink。
    /// </summary>
    /// <param name="builder">当前 Gateway WebApplication 构建器。</param>
    /// <returns>用于继续链式配置的原构建器。</returns>
    public static WebApplicationBuilder AddDeviceSerilog(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile(ConfigurationPath, optional: false, reloadOnChange: true);
        builder.Services
            .AddOptions<DeviceLoggingOptions>()
            .Bind(builder.Configuration.GetSection(DeviceLoggingOptions.SectionName))
            .Validate(static options => !string.IsNullOrWhiteSpace(options.ApplicationPath), "ApplicationPath 不能为空。")
            .Validate(static options => !string.IsNullOrWhiteSpace(options.UpstreamPath), "UpstreamPath 不能为空。")
            .Validate(static options => !string.IsNullOrWhiteSpace(options.DownstreamPath), "DownstreamPath 不能为空。")
            .Validate(static options => options.FileSizeLimitBytes > 0, "FileSizeLimitBytes 必须大于零。")
            .Validate(static options => options.RetainedFileCount > 0, "RetainedFileCount 必须大于零。")
            .Validate(static options => options.RetainedDays > 0, "RetainedDays 必须大于零。")
            .Validate(static options => options.ApplicationBufferSize > 0, "ApplicationBufferSize 必须大于零。")
            .Validate(static options => options.UpstreamBufferSize > 0, "UpstreamBufferSize 必须大于零。")
            .Validate(static options => options.DownstreamBufferSize > 0, "DownstreamBufferSize 必须大于零。")
            .Validate(static options => options.FlushIntervalSeconds > 0, "FlushIntervalSeconds 必须大于零。")
            .Validate(static options => options.MonitorIntervalSeconds > 0, "MonitorIntervalSeconds 必须大于零。")
            .Validate(
                static options => options.HeartbeatSampleRate is >= 0 and <= 1,
                "HeartbeatSampleRate 必须位于 0 到 1 之间。")
            .Validate(
                static options => options.HeartbeatEnabledDeviceIds is not null &&
                                  options.HeartbeatEnabledDeviceIds.All(static value => !string.IsNullOrWhiteSpace(value)),
                "HeartbeatEnabledDeviceIds 不能包含空设备编号。")
            .ValidateOnStart();
        builder.Services.AddSingleton<LogBufferMonitorRegistry>();
        builder.Services.AddHostedService<LogBufferHealthWorker>();
        builder.Services.AddSingleton<DeviceTrafficLoggingMetrics>();
        builder.Services.AddSingleton<DeviceTrafficLogPolicy>();

        builder.Services.AddSerilog((services, configuration) =>
        {
            var options = services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceLoggingOptions>>()
                .Value;
            var monitors = services.GetRequiredService<LogBufferMonitorRegistry>();
            var applicationPath = ResolvePath(builder.Environment.ContentRootPath, options.ApplicationPath);
            var upstreamPath = ResolvePath(builder.Environment.ContentRootPath, options.UpstreamPath);
            var downstreamPath = ResolvePath(builder.Environment.ContentRootPath, options.DownstreamPath);
            EnsureParentDirectory(applicationPath);
            EnsureParentDirectory(upstreamPath);
            EnsureParentDirectory(downstreamPath);

            var fileSizeLimit = Math.Max(options.FileSizeLimitBytes, 1_048_576);
            var retainedFileCount = Math.Max(options.RetainedFileCount, 1);
            var retainedTime = TimeSpan.FromDays(Math.Max(options.RetainedDays, 1));
            var flushInterval = TimeSpan.FromSeconds(Math.Max(options.FlushIntervalSeconds, 1));

            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", builder.Environment.ApplicationName)
                .Enrich.WithProperty("Host", Environment.MachineName)
                .Enrich.WithProperty("LocalHost", Environment.MachineName)
                .WriteTo.Logger(application => application
                    .Filter.ByExcluding(IsDeviceTrafficLog)
                    .WriteTo.Console(
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Logger(applicationFile => applicationFile
                        .Filter.ByExcluding(IsMicrosoftLog)
                        .WriteTo.Async(file => file.File(
                            new SysLogJsonFormatter(),
                            applicationPath,
                            rollingInterval: RollingInterval.Day,
                            fileSizeLimitBytes: fileSizeLimit,
                            rollOnFileSizeLimit: true,
                            retainedFileCountLimit: retainedFileCount,
                            retainedFileTimeLimit: retainedTime,
                            flushToDiskInterval: flushInterval),
                            bufferSize: Math.Max(options.ApplicationBufferSize, 1_000),
                            blockWhenFull: false,
                            monitor: monitors.GetOrCreate(LogBufferMonitorRegistry.ApplicationLogType))))
                .WriteTo.Logger(upstream => upstream
                    .Filter.ByIncludingOnly(IsUpstreamTrafficLog)
                    .Enrich.WithProperty("LogType", DeviceTrafficLogCategories.UpstreamLogType)
                    .WriteTo.Async(file => file.File(
                        new CompactJsonFormatter(),
                        upstreamPath,
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: fileSizeLimit,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: retainedFileCount,
                        retainedFileTimeLimit: retainedTime,
                        flushToDiskInterval: flushInterval),
                        bufferSize: Math.Max(options.UpstreamBufferSize, 1_000),
                        blockWhenFull: false,
                        monitor: monitors.GetOrCreate(DeviceTrafficLogCategories.UpstreamLogType)))
                .WriteTo.Logger(downstream => downstream
                    .Filter.ByIncludingOnly(IsDownstreamTrafficLog)
                    .Enrich.WithProperty("LogType", DeviceTrafficLogCategories.DownstreamLogType)
                    .WriteTo.Async(file => file.File(
                        new CompactJsonFormatter(),
                        downstreamPath,
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: fileSizeLimit,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: retainedFileCount,
                        retainedFileTimeLimit: retainedTime,
                        flushToDiskInterval: flushInterval),
                        bufferSize: Math.Max(options.DownstreamBufferSize, 1_000),
                        blockWhenFull: false,
                        monitor: monitors.GetOrCreate(DeviceTrafficLogCategories.DownstreamLogType)));
        });

        return builder;
    }

    /// <summary>判断事件是否属于任一设备通信日志流。</summary>
    private static bool IsDeviceTrafficLog(LogEvent logEvent) =>
        IsUpstreamTrafficLog(logEvent) || IsDownstreamTrafficLog(logEvent);

    /// <summary>判断事件是否属于设备上行日志类别。</summary>
    private static bool IsUpstreamTrafficLog(LogEvent logEvent) =>
        HasSourceContext(logEvent, DeviceTrafficLogCategories.UpstreamCategory);

    /// <summary>判断事件是否属于设备下发日志类别。</summary>
    private static bool IsDownstreamTrafficLog(LogEvent logEvent) =>
        HasSourceContext(logEvent, DeviceTrafficLogCategories.DownstreamCategory);

    /// <summary>
    /// 精确匹配 SourceContext，防止名称前缀相似的普通程序日志误入通信日志文件。
    /// </summary>
    /// <param name="logEvent">待分类的 Serilog 事件。</param>
    /// <param name="expected">期望的完整 SourceContext。</param>
    /// <returns>SourceContext 完全一致时返回 true。</returns>
    private static bool HasSourceContext(LogEvent logEvent, string expected) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) &&
        value is ScalarValue { Value: string sourceContext } &&
        string.Equals(sourceContext, expected, StringComparison.Ordinal);

    /// <summary>判断事件是否来自 Microsoft 框架类别。</summary>
    private static bool IsMicrosoftLog(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) &&
        value is ScalarValue { Value: string sourceContext } &&
        sourceContext.StartsWith("Microsoft.", StringComparison.Ordinal);

    /// <summary>
    /// 把日志配置路径解析为绝对路径，相对路径以 Gateway 内容根目录为基准。
    /// </summary>
    /// <param name="contentRootPath">Gateway 内容根目录。</param>
    /// <param name="configuredPath">配置中的绝对或相对路径。</param>
    /// <returns>规范化后的绝对路径。</returns>
    private static string ResolvePath(string contentRootPath, string configuredPath) =>
        Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, contentRootPath);

    /// <summary>
    /// 在 Serilog 初始化前创建日志父目录，使路径或权限错误能够在启动阶段暴露。
    /// </summary>
    /// <param name="path">日志文件滚动路径。</param>
    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
