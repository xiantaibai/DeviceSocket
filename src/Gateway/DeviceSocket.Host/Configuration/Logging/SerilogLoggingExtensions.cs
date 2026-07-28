using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// Gateway 的 Serilog 程序日志和 Command 日志分流配置。
/// </summary>
public static class SerilogLoggingExtensions
{
    private const string ConfigurationPath = "Configuration/Logging/serilog.json";
    private const string CommandLogType = "Command";

    /// <summary>
    /// 加载独立日志配置，并将程序日志与 Command 日志分别写入独立 Sink。
    /// </summary>
    public static WebApplicationBuilder AddDeviceSerilog(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile(ConfigurationPath, optional: false, reloadOnChange: true);
        builder.Services.Configure<DeviceLoggingOptions>(
            builder.Configuration.GetSection(DeviceLoggingOptions.SectionName));
        builder.Services.AddSingleton<CommandLogAsyncMonitor>();
        builder.Services.AddHostedService<CommandLogHealthWorker>();

        builder.Services.AddSerilog((services, configuration) =>
        {
            var options = services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceLoggingOptions>>()
                .Value;
            var monitor = services.GetRequiredService<CommandLogAsyncMonitor>();
            var applicationPath = ResolvePath(builder.Environment.ContentRootPath, options.ApplicationPath);
            var commandPath = ResolvePath(builder.Environment.ContentRootPath, options.CommandPath);
            EnsureParentDirectory(applicationPath);
            EnsureParentDirectory(commandPath);

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
                    .Filter.ByExcluding(IsCommandLog)
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
                            blockWhenFull: false)))
                .WriteTo.Logger(command => command
                    .Filter.ByIncludingOnly(IsCommandLog)
                    .WriteTo.Async(file => file.File(
                        new CompactJsonFormatter(),
                        commandPath,
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: fileSizeLimit,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: retainedFileCount,
                        retainedFileTimeLimit: retainedTime,
                        flushToDiskInterval: flushInterval),
                        bufferSize: Math.Max(options.CommandBufferSize, 1_000),
                        blockWhenFull: false,
                        monitor: monitor));
        });

        return builder;
    }

    private static bool IsCommandLog(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("LogType", out var value) &&
        value is ScalarValue { Value: CommandLogType };

    private static bool IsMicrosoftLog(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) &&
        value is ScalarValue { Value: string sourceContext } &&
        sourceContext.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static string ResolvePath(string contentRootPath, string configuredPath) =>
        Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, contentRootPath);

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
