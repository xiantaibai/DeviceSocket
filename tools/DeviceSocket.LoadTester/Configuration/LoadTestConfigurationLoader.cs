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

        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var options = await JsonSerializer.DeserializeAsync<LoadTestOptions>(
            stream,
            SerializerOptions,
            cancellationToken) ?? throw new ConfigurationValidationException(["配置文件内容不能为空。"]);

        ApplyDefaultsAndOverrides(options, overrides);
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

    private static void ApplyDefaultsAndOverrides(LoadTestOptions options, RunCommandOverrides overrides)
    {
        options.Target ??= new TargetOptions();
        options.Devices ??= new DeviceOptions();
        options.Connection ??= new ConnectionOptions();
        options.Heartbeat ??= new HeartbeatOptions();
        options.Workload ??= new WorkloadOptions();
        options.Reconnect ??= new ReconnectOptions();
        options.Scenario ??= new ScenarioOptions();
        options.Scenario.Phases ??= [];
        foreach (var phase in options.Scenario.Phases)
            phase.Faults ??= new FaultInjectionOptions();
        options.Reporting ??= new ReportingOptions();
        options.ServerObservation ??= new ServerObservationOptions();
        options.Acceptance ??= new AcceptanceOptions();
        options.Distributed ??= new DistributedOptions();
        options.Target.Port = options.Target.Port == 0
            ? options.Target.Protocol == LoadProtocol.QGdw3761 ? 5001 : 5002
            : options.Target.Port;

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
