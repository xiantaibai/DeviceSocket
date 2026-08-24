using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 从 JSON 文件加载负载测试配置，并应用当前节点的命令行覆盖参数。
/// </summary>
public static class LoadTestConfigurationLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// 加载配置、应用分片覆盖并完成全部边界校验。
    /// </summary>
    /// <param name="path">JSON 场景配置文件路径。</param>
    /// <param name="overrides">只影响当前执行节点的命令行覆盖。</param>
    /// <returns>可直接执行的完整配置。</returns>
    public static async Task<LoadTestOptions> LoadAsync(
        string path,
        RunCommandOverrides overrides,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置文件路径不能为空。", nameof(path));

        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        var options = await JsonSerializer.DeserializeAsync<LoadTestOptions>(
            stream,
            SerializerOptions,
            cancellationToken) ?? throw new ConfigurationValidationException(["配置文件内容不能为空。"]);

        ApplyDefaultsAndOverrides(options, overrides, Path.GetDirectoryName(fullPath)!);
        LoadTestOptionsValidator.ValidateAndThrow(options);
        return options;
    }

    /// <summary>
    /// 返回与配置文件和报告文件共享的 JSON 序列化规则。
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 补齐旧配置缺省对象、解析配置文件相对路径并应用当前节点命令行覆盖。
    /// </summary>
    /// <param name="options">从 JSON 反序列化得到的配置。</param>
    /// <param name="overrides">当前节点命令行覆盖。</param>
    /// <param name="configurationDirectory">配置文件所在的绝对目录。</param>
    private static void ApplyDefaultsAndOverrides(
        LoadTestOptions options,
        RunCommandOverrides overrides,
        string configurationDirectory)
    {
        options.Target ??= new TargetOptions();
        options.Devices ??= new DeviceOptions();
        options.Connection ??= new ConnectionOptions();
        options.Heartbeat ??= new HeartbeatOptions();
        options.Workload ??= new WorkloadOptions();
        options.Workload.GatherReadingResponse ??= new GatherReadingResponseOptions();
        options.CommandWorkload ??= new CommandWorkloadOptions();
        options.CommandWorkload.Phases ??= [];
        options.Reconnect ??= new ReconnectOptions();
        options.Scenario ??= new ScenarioOptions();
        options.Scenario.Phases ??= [];
        foreach (var phase in options.Scenario.Phases)
        {
            phase.ManualAction ??= new ManualActionOptions();
            phase.Faults ??= new FaultInjectionOptions();
        }
        options.Reporting ??= new ReportingOptions();
        options.ServerObservation ??= new ServerObservationOptions();
        options.Acceptance ??= new AcceptanceOptions();
        options.Distributed ??= new DistributedOptions();
        options.Target.Port = options.Target.Port == 0
            ? options.Target.Protocol == LoadProtocol.QGdw3761 ? 5001 : 5002
            : options.Target.Port;

        if (!string.IsNullOrWhiteSpace(options.Devices.DeviceMappingFile) &&
            !Path.IsPathRooted(options.Devices.DeviceMappingFile))
        {
            // 真实设备身份 CSV 跟随场景发布；按场景目录解析才能在本地和多台 Linux/Windows 负载机上得到同一文件。
            options.Devices.DeviceMappingFile = Path.GetFullPath(
                Path.Combine(configurationDirectory, options.Devices.DeviceMappingFile));
        }

        if (!string.IsNullOrWhiteSpace(options.CommandWorkload.DeviceMappingFile) &&
            !Path.IsPathRooted(options.CommandWorkload.DeviceMappingFile))
        {
            // 多机发布时场景文件和 CSV 通常放在同一目录，相对该目录解析可避免受启动工作目录影响。
            options.CommandWorkload.DeviceMappingFile = Path.GetFullPath(
                Path.Combine(configurationDirectory, options.CommandWorkload.DeviceMappingFile));
        }

        if (!string.IsNullOrWhiteSpace(overrides.RunId))
            options.Distributed.RunId = overrides.RunId;
        if (overrides.ShardIndex.HasValue)
            options.Distributed.ShardIndex = overrides.ShardIndex.Value;
        if (overrides.ShardCount.HasValue)
            options.Distributed.ShardCount = overrides.ShardCount.Value;
        if (overrides.StartAtUtc.HasValue)
            options.Distributed.StartAtUtc = overrides.StartAtUtc.Value.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(overrides.OutputDirectory))
            options.Reporting.OutputDirectory = overrides.OutputDirectory;

        if (string.IsNullOrWhiteSpace(options.Distributed.RunId) && options.Distributed.ShardCount == 1)
            options.Distributed.RunId = $"load-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..42];
    }
}
