namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 校验地址空间、时间参数、分片参数和验收门槛，避免无效配置在压测中途才失败。
/// </summary>
public static class LoadTestOptionsValidator
{
    private const long MaximumDlt645Address = 999_999_999_999;

    /// <summary>
    /// 校验完整配置；发现错误时一次性返回全部可定位的问题。
    /// </summary>
    /// <param name="options">待校验的完整负载配置。</param>
    /// <returns>全部配置错误；空集合表示配置有效。</returns>
    public static IReadOnlyList<string> Validate(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (!Enum.IsDefined(options.Role))
            errors.Add("role: 仅支持 devicesOnly、apiOnly 或 combined。");

        if (string.IsNullOrWhiteSpace(options.Target.Host))
            errors.Add("target.host: 服务器地址不能为空。");
        if (options.Target.Port is <= 0 or > 65535)
            errors.Add("target.port: 端口必须在 1-65535 之间。");
        if (!Enum.IsDefined(options.Target.Protocol))
            errors.Add("target.protocol: 通信协议不受支持。");
        if (!Enum.IsDefined(options.Target.CompatibilityProfile))
            errors.Add("target.compatibilityProfile: 服务端兼容配置不受支持。");
        if (options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherTcpCenter &&
            options.Target.Protocol != LoadProtocol.QGdw3761)
        {
            errors.Add("target.compatibilityProfile: gatherTcpCenter 当前只支持 Q/GDW 376.1 协议。");
        }
        if (options.Devices.Count <= 0)
            errors.Add("devices.count: 设备总数必须大于 0。");
        if (options.Devices.DuplicateIdentityPercentage is < 0 or > 100)
            errors.Add("devices.duplicateIdentityPercentage: 必须在 0-100 之间。");
        if (!Enum.IsDefined(options.Devices.GenerationMode))
            errors.Add("devices.generationMode: 仅支持 sequential、random 或 csv。");

        var duplicateCount = (int)Math.Floor(
            options.Devices.Count * options.Devices.DuplicateIdentityPercentage / 100d);
        var uniqueIdentityCount = Math.Max(1, options.Devices.Count - duplicateCount);
        var requiredAddressOffset = uniqueIdentityCount - 1L;
        if (options.Devices.GenerationMode == DeviceAddressGenerationMode.Csv)
        {
            ValidateCsvDeviceSource(options, errors);
        }
        else if (options.Target.Protocol == LoadProtocol.QGdw3761)
        {
            if (options.Devices.AddressStart <= 0)
                errors.Add("devices.addressStart: 起始地址必须大于 0。");
            if (string.IsNullOrEmpty(options.Devices.AdministrativeDivision) ||
                options.Devices.AdministrativeDivision.Length != 4 ||
                !options.Devices.AdministrativeDivision.All(char.IsDigit))
            {
                errors.Add("devices.administrativeDivision: 376.1 行政区划必须是 4 位十进制数字。");
            }

            if (options.Devices.GenerationMode == DeviceAddressGenerationMode.Random)
            {
                ValidateRandomQGdwAddressSpace(options.Devices, uniqueIdentityCount, errors);
            }
            else if (options.Devices.AddressStart > ushort.MaxValue)
            {
                errors.Add("devices.addressStart: 376.1 起始终端地址不能超过 65535。");
            }
            else if (!options.Devices.SpanAdministrativeDivisions &&
                     requiredAddressOffset > ushort.MaxValue - options.Devices.AddressStart)
            {
                errors.Add("devices: 376.1 终端地址范围不能超过 65535；需要跨区划压测时启用 spanAdministrativeDivisions。");
            }
            else if (options.Devices.SpanAdministrativeDivisions &&
                     int.TryParse(options.Devices.AdministrativeDivision, out var startingDivision))
            {
                // 每个行政区划使用终端地址 1～65535；跨区划时要确保四位 BCD 区划码不会超过 9999。
                var lastLinearAddress = options.Devices.AddressStart - 1 + requiredAddressOffset;
                var requiredDivisionOffset = lastLinearAddress / ushort.MaxValue;
                if (requiredDivisionOffset > 9_999 - startingDivision)
                    errors.Add("devices: 376.1 跨行政区划后的区划码不能超过 9999。");
            }
        }
        else if (options.Devices.GenerationMode == DeviceAddressGenerationMode.Random)
        {
            errors.Add("devices.generationMode: random 当前只支持 Q/GDW 376.1 协议。");
        }
        else if (options.Devices.AddressStart <= 0)
        {
            errors.Add("devices.addressStart: 起始地址必须大于 0。");
        }
        else if (options.Devices.AddressStart > MaximumDlt645Address ||
                 requiredAddressOffset > MaximumDlt645Address - options.Devices.AddressStart)
        {
            errors.Add("devices: 645 表地址范围不能超过 12 位十进制地址。");
        }

        if (options.Connection.StartMode == ConnectionStartMode.Ramp && options.Connection.ConnectionsPerSecond <= 0)
            errors.Add("connection.connectionsPerSecond: Ramp 模式下必须大于 0。");
        if (options.Connection.MaxConcurrentConnecting <= 0)
            errors.Add("connection.maxConcurrentConnecting: 必须大于 0。");
        ValidatePositiveSeconds(options.Connection.ConnectTimeoutSeconds, "connection.connectTimeoutSeconds", errors);
        ValidatePositiveSeconds(options.Connection.LoginTimeoutSeconds, "connection.loginTimeoutSeconds", errors);
        ValidatePositiveSeconds(options.Connection.WriteTimeoutSeconds, "connection.writeTimeoutSeconds", errors);

        if (options.Heartbeat.Enabled)
        {
            ValidatePositiveSeconds(options.Heartbeat.IntervalSeconds, "heartbeat.intervalSeconds", errors);
            ValidatePositiveSeconds(options.Heartbeat.ResponseTimeoutSeconds, "heartbeat.responseTimeoutSeconds", errors);
            if (options.Heartbeat.JitterSeconds < 0 || options.Heartbeat.JitterSeconds >= options.Heartbeat.IntervalSeconds)
                errors.Add("heartbeat.jitterSeconds: 必须大于等于 0 且小于心跳间隔。");
        }

        if (options.Workload.ReportsEnabled)
        {
            ValidatePositiveSeconds(options.Workload.ReportIntervalSeconds, "workload.reportIntervalSeconds", errors);
            if (options.Workload.JitterSeconds < 0 || options.Workload.JitterSeconds >= options.Workload.ReportIntervalSeconds)
                errors.Add("workload.jitterSeconds: 必须大于等于 0 且小于上报间隔。");
        }
        ValidateGatherReadingResponse(options, errors);
        ValidateCommandWorkload(options, errors);

        if (options.Reconnect.InitialDelayMilliseconds < 0)
            errors.Add("reconnect.initialDelayMilliseconds: 不能小于 0。");
        if (options.Reconnect.MaximumDelaySeconds < 0)
            errors.Add("reconnect.maximumDelaySeconds: 不能小于 0。");
        if (options.Reconnect.Multiplier < 1)
            errors.Add("reconnect.multiplier: 不能小于 1。");
        if (options.Reconnect.JitterRatio is < 0 or > 1)
            errors.Add("reconnect.jitterRatio: 必须在 0-1 之间。");
        if (options.Reconnect.MaximumAttempts < 0)
            errors.Add("reconnect.maximumAttempts: 不能小于 0，0 表示不限制。");

        if (string.IsNullOrWhiteSpace(options.Scenario.Name))
            errors.Add("scenario.name: 场景名称不能为空。");
        if (options.Scenario.Phases.Count == 0)
            ValidatePositiveSeconds(options.Scenario.DurationSeconds, "scenario.durationSeconds", errors);
        foreach (var phase in options.Scenario.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name))
                errors.Add("scenario.phases.name: 阶段名称不能为空。");
            ValidatePositiveSeconds(phase.DurationSeconds, $"scenario.phases[{phase.Name}].durationSeconds", errors);
            if (phase.TargetDevicePercentage is < 0 or > 100)
                errors.Add($"scenario.phases[{phase.Name}].targetDevicePercentage: 必须在 0-100 之间。");
            if (phase.ConnectionsPerSecond is <= 0)
                errors.Add($"scenario.phases[{phase.Name}].connectionsPerSecond: 设置后必须大于 0。");
            if (phase.ManualAction.Enabled)
            {
                if (string.IsNullOrWhiteSpace(phase.ManualAction.Message))
                    errors.Add($"scenario.phases[{phase.Name}].manualAction.message: 启用人工操作时不能为空。");
                if (phase.ManualAction.PreparationSeconds < 0)
                    errors.Add($"scenario.phases[{phase.Name}].manualAction.preparationSeconds: 不能小于 0。");
                if (phase.ManualAction.OperationSeconds <= 0)
                    errors.Add($"scenario.phases[{phase.Name}].manualAction.operationSeconds: 启用人工操作时必须大于 0。");
            }
            if (phase.ExpectedServerRestart && !phase.ManualAction.Enabled)
                errors.Add($"scenario.phases[{phase.Name}]: expectedServerRestart=true 时必须启用 manualAction，确保计划重启具有明确时间标记。");
            ValidateFaults(phase, errors);
        }

        if (options.Reporting.SampleIntervalSeconds <= 0)
            errors.Add("reporting.sampleIntervalSeconds: 必须大于 0。");
        if (options.Reporting.CheckpointIntervalSeconds <= 0)
            errors.Add("reporting.checkpointIntervalSeconds: 必须大于 0。");
        else if (options.Reporting.CheckpointIntervalSeconds < options.Reporting.SampleIntervalSeconds)
            errors.Add("reporting.checkpointIntervalSeconds: 不能小于 reporting.sampleIntervalSeconds。");
        if (string.IsNullOrWhiteSpace(options.Reporting.OutputDirectory))
            errors.Add("reporting.outputDirectory: 输出目录不能为空。");
        if (options.Reporting.DownstreamDiagnostics is null)
        {
            errors.Add("reporting.downstreamDiagnostics: 不能为空。");
        }
        else
        {
            if (options.Reporting.DownstreamDiagnostics.MaxSamplesPerFingerprint is < 0 or > 100)
                errors.Add("reporting.downstreamDiagnostics.maxSamplesPerFingerprint: 必须在 0-100 之间。");
            if (options.Reporting.DownstreamDiagnostics.MaxTotalSamples is < 0 or > 10_000)
                errors.Add("reporting.downstreamDiagnostics.maxTotalSamples: 必须在 0-10000 之间。");
            if (options.Reporting.DownstreamDiagnostics.RawHexMaxBytes is < 0 or > 4096)
                errors.Add("reporting.downstreamDiagnostics.rawHexMaxBytes: 必须在 0-4096 之间。");
            ValidatePositiveSeconds(
                options.Reporting.DownstreamDiagnostics.CorrelationTimeoutSeconds,
                "reporting.downstreamDiagnostics.correlationTimeoutSeconds",
                errors);
        }
        if (options.ServerObservation.Enabled)
        {
            if (!Uri.TryCreate(options.ServerObservation.StatusUrl, UriKind.Absolute, out var statusUri) ||
                statusUri.Scheme is not ("http" or "https"))
            {
                errors.Add("serverObservation.statusUrl: 必须是绝对 HTTP/HTTPS 地址。");
            }
            if (options.ServerObservation.TimeoutSeconds <= 0)
                errors.Add("serverObservation.timeoutSeconds: 必须大于 0。");
        }
        if (string.IsNullOrWhiteSpace(options.Distributed.RunId))
            errors.Add("distributed.runId: 多节点必须显式设置，同一轮测试的所有节点使用相同值。");
        else if (options.Distributed.RunId.Length > 100)
            errors.Add("distributed.runId: 长度不能超过 100 个字符。");
        if (options.Distributed.ShardCount <= 0)
            errors.Add("distributed.shardCount: 必须大于 0。");
        if (options.Distributed.ShardCount > options.Devices.Count)
            errors.Add("distributed.shardCount: 不能大于设备总数，否则会产生空分片。");
        if (options.Distributed.ShardIndex < 0 || options.Distributed.ShardIndex >= options.Distributed.ShardCount)
            errors.Add("distributed.shardIndex: 必须在 0 到 shardCount-1 之间。");

        ValidateRate(options.Acceptance.MinimumConnectionSuccessRate, "acceptance.minimumConnectionSuccessRate", errors);
        ValidateRate(options.Acceptance.MinimumLoginSuccessRate, "acceptance.minimumLoginSuccessRate", errors);
        ValidateRate(options.Acceptance.MinimumFinalOnlineRate, "acceptance.minimumFinalOnlineRate", errors);
        ValidateRate(options.Acceptance.MaximumProtocolErrorRate, "acceptance.maximumProtocolErrorRate", errors);
        ValidateRate(options.Acceptance.MinimumApiSuccessRate, "acceptance.minimumApiSuccessRate", errors);
        ValidateRate(
            options.Acceptance.MinimumApiTargetCoverageRate,
            "acceptance.minimumApiTargetCoverageRate",
            errors);
        ValidateRate(options.Acceptance.MinimumGatherReadingResponseRate, "acceptance.minimumGatherReadingResponseRate", errors);
        ValidateRate(
            options.Acceptance.MinimumAsyncApiAcceptanceRate,
            "acceptance.minimumAsyncApiAcceptanceRate",
            errors);
        ValidateRate(
            options.Acceptance.MinimumSyncResultSuccessRate,
            "acceptance.minimumSyncResultSuccessRate",
            errors);
        ValidateRate(
            options.Acceptance.MinimumCommandDeliveryRate,
            "acceptance.minimumCommandDeliveryRate",
            errors);
        ValidateRate(
            options.Acceptance.MinimumCommandRecognitionRate,
            "acceptance.minimumCommandRecognitionRate",
            errors);
        if (options.Acceptance.MaximumApiP95Milliseconds is <= 0)
            errors.Add("acceptance.maximumApiP95Milliseconds: 设置后必须大于 0。");
        if (options.Acceptance.MaximumUnobservedCommands is < 0)
            errors.Add("acceptance.maximumUnobservedCommands: 不能小于 0。");
        if (options.Acceptance.MaximumRecoverySeconds is <= 0)
            errors.Add("acceptance.maximumRecoverySeconds: 设置后必须大于 0。");
        if (options.Acceptance.MaximumReconnects is < 0)
            errors.Add("acceptance.maximumReconnects: 不能小于 0。");
        if (options.Acceptance.MinimumReconnects.HasValue &&
            options.Acceptance.MaximumReconnects.HasValue &&
            options.Acceptance.MinimumReconnects > options.Acceptance.MaximumReconnects)
        {
            errors.Add("acceptance: 最小重连次数不能大于最大重连次数。");
        }
        if (options.Acceptance.MaximumReconnectsPerDevice is < 0)
            errors.Add("acceptance.maximumReconnectsPerDevice: 不能小于 0。");
        if (options.Acceptance.MaximumSecondaryOutages is < 0)
            errors.Add("acceptance.maximumSecondaryOutages: 不能小于 0。");
        if (options.Acceptance.MinimumStableOnlineSeconds is < 0)
            errors.Add("acceptance.minimumStableOnlineSeconds: 不能小于 0。");
        if (options.Acceptance.MinimumFinalOnlineCount is < 0)
            errors.Add("acceptance.minimumFinalOnlineCount: 不能小于 0。");
        if (options.Acceptance.MaximumFinalOnlineCount is < 0)
            errors.Add("acceptance.maximumFinalOnlineCount: 不能小于 0。");
        if (options.Acceptance.MinimumFinalOnlineCount.HasValue &&
            options.Acceptance.MaximumFinalOnlineCount.HasValue &&
            options.Acceptance.MinimumFinalOnlineCount > options.Acceptance.MaximumFinalOnlineCount)
        {
            errors.Add("acceptance: 最小最终在线数不能大于最大最终在线数。");
        }
        if (options.Acceptance.MinimumReconnects is < 0)
            errors.Add("acceptance.minimumReconnects: 不能小于 0。");
        if (options.Acceptance.MinimumObservedAuthenticatedDevices is < 0)
            errors.Add("acceptance.minimumObservedAuthenticatedDevices: 不能小于 0。");
        if (options.Acceptance.MaximumObservedAuthenticatedDevices is < 0)
            errors.Add("acceptance.maximumObservedAuthenticatedDevices: 不能小于 0。");
        if (options.Acceptance.MinimumObservedAuthenticatedDevices.HasValue &&
            options.Acceptance.MaximumObservedAuthenticatedDevices.HasValue &&
            options.Acceptance.MinimumObservedAuthenticatedDevices > options.Acceptance.MaximumObservedAuthenticatedDevices)
        {
            errors.Add("acceptance: 服务端最小认证设备数不能大于最大认证设备数。");
        }
        if (!options.ServerObservation.Enabled &&
            (options.Acceptance.MinimumObservedAuthenticatedDevices.HasValue ||
             options.Acceptance.MaximumObservedAuthenticatedDevices.HasValue))
        {
            errors.Add("acceptance: 配置服务端认证设备数门槛时必须启用 serverObservation。");
        }

        return errors;
    }

    /// <summary>
    /// 校验 Gather 读表业务应答的适用协议、影响比例、延迟和模拟示值范围。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateGatherReadingResponse(LoadTestOptions options, ICollection<string> errors)
    {
        var response = options.Workload.GatherReadingResponse;
        if (!Enum.IsDefined(response.Mode))
            errors.Add("workload.gatherReadingResponse.mode: 应答模式不受支持。");
        if (response.AffectedDevicePercentage is < 0 or > 100)
            errors.Add("workload.gatherReadingResponse.affectedDevicePercentage: 必须在 0-100 之间。");
        if (response.DelayMilliseconds < 0)
            errors.Add("workload.gatherReadingResponse.delayMilliseconds: 不能小于 0。");
        if (response.RateCount is < 0 or > 8)
            errors.Add("workload.gatherReadingResponse.rateCount: 必须在 0-8 之间。");
        if (response.BaseReadingKwh is < 0 or > 999_999.9999m)
            errors.Add("workload.gatherReadingResponse.baseReadingKwh: 必须在 0-999999.9999 kWh 之间。");
        if (response.DeviceStepKwh < 0)
            errors.Add("workload.gatherReadingResponse.deviceStepKwh: 不能小于 0。");
        if (response.Enabled &&
            options.Target.CompatibilityProfile != ServerCompatibilityProfile.GatherTcpCenter &&
            response.Mode != GatherReadingResponseMode.Normal)
        {
            errors.Add("workload.gatherReadingResponse: 非 normal 模式只适用于 gatherTcpCenter 兼容配置。");
        }
    }

    /// <summary>
    /// 校验 HTTP 指令驱动的角色组合、地址、设备映射、在线门槛和各阶段限流参数。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateCommandWorkload(LoadTestOptions options, ICollection<string> errors)
    {
        var command = options.CommandWorkload;
        var shouldRunApi = options.Role is LoadTestRole.ApiOnly or LoadTestRole.Combined;
        if (shouldRunApi && !command.Enabled)
            errors.Add("commandWorkload.enabled: apiOnly 或 combined 角色必须启用 HTTP 指令负载。");
        if (!command.Enabled)
            return;
        if (!Enum.IsDefined(command.TargetDeviceSource))
            errors.Add("commandWorkload.targetDeviceSource: 仅支持 fixedOrdinal 或 onlineRegistry。");
        if (command.TargetDeviceSource == CommandTargetSource.OnlineRegistry &&
            options.Role != LoadTestRole.Combined)
        {
            errors.Add("commandWorkload.targetDeviceSource: onlineRegistry 只支持 combined 角色。");
        }
        if (options.Target.CompatibilityProfile != ServerCompatibilityProfile.GatherTcpCenter ||
            options.Target.Protocol != LoadProtocol.QGdw3761)
        {
            errors.Add("commandWorkload: 当前只支持 Q/GDW 376.1 的 gatherTcpCenter 老服务。");
        }
        if (!Uri.TryCreate(command.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            errors.Add("commandWorkload.baseUrl: 必须是绝对 HTTP/HTTPS 地址。");
        }
        if (string.IsNullOrWhiteSpace(command.EndpointPath))
            errors.Add("commandWorkload.endpointPath: 不能为空。");

        var effectiveMappingFile = !string.IsNullOrWhiteSpace(command.DeviceMappingFile)
            ? command.DeviceMappingFile
            : options.Devices.GenerationMode == DeviceAddressGenerationMode.Csv
                ? options.Devices.DeviceMappingFile
                : null;
        var hasMappingFile = !string.IsNullOrWhiteSpace(effectiveMappingFile);
        var hasMeterIdStart = command.MeterIdStart.HasValue;
        if (hasMappingFile == hasMeterIdStart)
            errors.Add("commandWorkload: 有效设备映射文件与 meterIdStart 必须且只能设置一个；CSV 设备模式会默认复用 devices.deviceMappingFile。");
        if (hasMappingFile && !File.Exists(effectiveMappingFile))
            errors.Add($"commandWorkload.deviceMappingFile: 文件不存在：{effectiveMappingFile}");
        if (command.MeterIdStart is <= 0)
            errors.Add("commandWorkload.meterIdStart: 设置后必须大于 0。");
        ValidatePositiveSeconds(command.RequestTimeoutSeconds, "commandWorkload.requestTimeoutSeconds", errors);
        ValidateRate(command.RequiredOnlineRate, "commandWorkload.requiredOnlineRate", errors);
        if (command.RequiredOnlineRate <= 0)
            errors.Add("commandWorkload.requiredOnlineRate: 必须大于 0。");
        if (command.MinimumOnlineCount is < 0 || command.MinimumOnlineCount > options.Devices.Count)
            errors.Add("commandWorkload.minimumOnlineCount: 必须在 0 到 devices.count 之间，空值表示按阶段目标在线率计算。");
        if (command.OnlineStableSeconds < 0)
            errors.Add("commandWorkload.onlineStableSeconds: 不能小于 0。");
        ValidatePositiveSeconds(command.OnlineWaitTimeoutSeconds, "commandWorkload.onlineWaitTimeoutSeconds", errors);
        if (command.StartDelaySeconds < 0)
            errors.Add("commandWorkload.startDelaySeconds: 不能小于 0。");
        if (command.Phases.Count == 0)
            errors.Add("commandWorkload.phases: 启用 HTTP 指令负载时至少需要一个阶段。");
        foreach (var phase in command.Phases)
        {
            var path = $"commandWorkload.phases[{phase.Name}]";
            if (string.IsNullOrWhiteSpace(phase.Name))
                errors.Add("commandWorkload.phases.name: 阶段名称不能为空。");
            if (phase.TargetDeviceCount < 0 || phase.TargetDeviceCount > options.Devices.Count)
                errors.Add($"{path}.targetDeviceCount: 必须在 0 到 devices.count 之间，0 表示全部设备。");
            if (phase.RequestsPerDevice <= 0)
                errors.Add($"{path}.requestsPerDevice: 必须大于 0。");
            if (phase.RequestsPerSecond <= 0)
                errors.Add($"{path}.requestsPerSecond: 必须大于 0。");
            if (phase.MaxConcurrency <= 0)
                errors.Add($"{path}.maxConcurrency: 必须大于 0。");
            if (phase.PauseAfterSeconds < 0)
                errors.Add($"{path}.pauseAfterSeconds: 不能小于 0。");
            if (phase.MinimumOnlineCount is < 0 || phase.MinimumOnlineCount > options.Devices.Count)
            {
                errors.Add(
                    $"{path}.minimumOnlineCount: 必须在 0 到 devices.count 之间，" +
                    "空值表示继承全局最低在线数或按阶段目标在线率计算。");
            }
            if (phase.OnlineStableSeconds is < 0)
                errors.Add($"{path}.onlineStableSeconds: 不能小于 0，空值表示继承全局稳定时长。");
            if (phase.OnlineWaitTimeoutSeconds is <= 0)
                errors.Add($"{path}.onlineWaitTimeoutSeconds: 设置后必须大于 0，空值表示继承全局等待上限。");
            if (!Enum.IsDefined(phase.OnlineGateFailureAction))
                errors.Add($"{path}.onlineGateFailureAction: 配置值不受支持。");
            ValidateRate(phase.MinimumApiSuccessRate, $"{path}.minimumApiSuccessRate", errors);
            if (phase.MaximumP95Milliseconds is <= 0)
                errors.Add($"{path}.maximumP95Milliseconds: 设置后必须大于 0。");
            if (!Enum.IsDefined(phase.DrainFailureAction))
                errors.Add($"{path}.drainFailureAction: 配置值不受支持。");
            if (phase.DrainEnabled)
            {
                ValidateRate(phase.MinimumRawDeliveryRate, $"{path}.minimumRawDeliveryRate", errors);
                ValidateRate(phase.MinimumDeliveryRate, $"{path}.minimumDeliveryRate", errors);
                ValidateRate(phase.MinimumResponseRate, $"{path}.minimumResponseRate", errors);
                ValidatePositiveSeconds(phase.DrainTimeoutSeconds, $"{path}.drainTimeoutSeconds", errors);
                if (phase.QuietSeconds < 0)
                    errors.Add($"{path}.quietSeconds: 不能小于 0。");
            }
            if (!Enum.IsDefined(phase.CircuitBreakerAction))
                errors.Add($"{path}.circuitBreakerAction: 配置值不受支持。");
            if (phase.CircuitBreakerEnabled)
            {
                if (phase.MaximumDurationSeconds is <= 0)
                    errors.Add($"{path}.maximumDurationSeconds: 设置后必须大于 0。");
                if (phase.MaximumConsecutiveFailures < 0)
                    errors.Add($"{path}.maximumConsecutiveFailures: 不能小于 0，零表示不启用该条件。");
                ValidateRate(phase.MaximumTimeoutRate, $"{path}.maximumTimeoutRate", errors);
                if (phase.CircuitBreakerMinimumRequests <= 0)
                    errors.Add($"{path}.circuitBreakerMinimumRequests: 必须大于 0。");
                if (!phase.MaximumDurationSeconds.HasValue &&
                    phase.MaximumConsecutiveFailures == 0 &&
                    !phase.MaximumTimeoutRate.HasValue)
                {
                    errors.Add($"{path}: 启用 circuitBreakerEnabled 时至少配置一个熔断条件。");
                }
            }
        }
    }

    /// <summary>
    /// 校验 CSV 真实设备模式的协议、文件和身份复用约束；文件内容在设备分配前统一解析并校验。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateCsvDeviceSource(LoadTestOptions options, ICollection<string> errors)
    {
        if (options.Target.Protocol != LoadProtocol.QGdw3761)
            errors.Add("devices.generationMode: csv 当前只支持 Q/GDW 376.1 协议。");
        if (string.IsNullOrWhiteSpace(options.Devices.DeviceMappingFile))
        {
            errors.Add("devices.deviceMappingFile: csv 模式必须设置 meterId,deviceId 映射文件。");
        }
        else if (!File.Exists(options.Devices.DeviceMappingFile))
        {
            errors.Add($"devices.deviceMappingFile: 文件不存在：{options.Devices.DeviceMappingFile}");
        }
        if (options.Devices.DuplicateIdentityPercentage != 0)
            errors.Add("devices.duplicateIdentityPercentage: csv 模式已显式指定每条设备身份，必须为 0。");
    }

    /// <summary>
    /// 校验376.1随机地址模式的固定区划、闭区间边界和无重复地址容量。
    /// </summary>
    /// <param name="devices">设备编号配置。</param>
    /// <param name="uniqueIdentityCount">扣除重复登录连接后需要生成的唯一设备身份数量。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateRandomQGdwAddressSpace(
        DeviceOptions devices,
        int uniqueIdentityCount,
        ICollection<string> errors)
    {
        if (devices.SpanAdministrativeDivisions)
            errors.Add("devices.spanAdministrativeDivisions: random 模式必须固定行政区划，不能跨区划。");
        if (devices.AddressStart > ushort.MaxValue)
            errors.Add("devices.addressStart: 376.1 随机地址下界不能超过 65535。");
        if (!devices.AddressEnd.HasValue)
        {
            errors.Add("devices.addressEnd: random 模式必须设置随机终端地址上界。");
            return;
        }

        if (devices.AddressEnd <= 0 || devices.AddressEnd > ushort.MaxValue)
            errors.Add("devices.addressEnd: 376.1 随机地址上界必须在 1-65535 之间。");
        if (devices.AddressEnd < devices.AddressStart)
        {
            errors.Add("devices.addressEnd: 随机地址上界不能小于 addressStart。");
            return;
        }

        var addressCapacity = devices.AddressEnd.Value - devices.AddressStart + 1;
        if (addressCapacity < uniqueIdentityCount)
            errors.Add("devices: 随机地址区间容量不能小于需要生成的唯一设备数量。");
    }

    /// <summary>
    /// 校验完整配置，在存在任意错误时抛出包含全部原因的异常。
    /// </summary>
    /// <param name="options">待校验的完整负载配置。</param>
    /// <exception cref="ConfigurationValidationException">一个或多个配置项无效。</exception>
    public static void ValidateAndThrow(LoadTestOptions options)
    {
        var errors = Validate(options);
        if (errors.Count > 0)
            throw new ConfigurationValidationException(errors);
    }

    /// <summary>
    /// 校验以秒为单位且必须大于零的配置值。
    /// </summary>
    /// <param name="value">配置值。</param>
    /// <param name="path">用于错误定位的配置路径。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidatePositiveSeconds(int value, string path, ICollection<string> errors)
    {
        if (value <= 0)
            errors.Add($"{path}: 必须大于 0。");
    }

    /// <summary>
    /// 校验可选比例位于零到一之间。
    /// </summary>
    /// <param name="value">待校验比例。</param>
    /// <param name="path">用于错误定位的配置路径。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateRate(double? value, string path, ICollection<string> errors)
    {
        if (value is < 0 or > 1)
            errors.Add($"{path}: 必须在 0-1 之间。");
    }

    /// <summary>
    /// 校验单个阶段的网络故障注入参数。
    /// </summary>
    /// <param name="phase">场景阶段配置。</param>
    /// <param name="errors">接收校验错误的集合。</param>
    private static void ValidateFaults(ScenarioPhaseOptions phase, ICollection<string> errors)
    {
        var path = $"scenario.phases[{phase.Name}].faults";
        if (phase.Faults.NetworkLatencyMilliseconds < 0)
            errors.Add($"{path}.networkLatencyMilliseconds: 不能小于 0。");
        ValidateRate(phase.Faults.OutboundPacketLossRate, $"{path}.outboundPacketLossRate", errors);
        ValidateRate(phase.Faults.InboundPacketLossRate, $"{path}.inboundPacketLossRate", errors);
        ValidateRate(phase.Faults.CorruptChecksumRate, $"{path}.corruptChecksumRate", errors);
        ValidateRate(phase.Faults.DuplicateFrameRate, $"{path}.duplicateFrameRate", errors);
        if (phase.Faults.DisconnectPercentage is < 0 or > 100)
            errors.Add($"{path}.disconnectPercentage: 必须在 0-100 之间。");
        if (phase.Faults.FragmentSizeBytes < 0)
            errors.Add($"{path}.fragmentSizeBytes: 不能小于 0。");
        if (phase.Faults.FragmentDelayMilliseconds < 0)
            errors.Add($"{path}.fragmentDelayMilliseconds: 不能小于 0。");
        if (phase.Faults.DisconnectBatchSize < 0)
            errors.Add($"{path}.disconnectBatchSize: 不能小于 0。");
        if (phase.Faults.DisconnectIntervalMilliseconds < 0)
            errors.Add($"{path}.disconnectIntervalMilliseconds: 不能小于 0。");
    }
}
