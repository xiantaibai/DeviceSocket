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
        if (options.Target.CompatibilityProfile is ServerCompatibilityProfile.GatherTcpCenter or ServerCompatibilityProfile.GatherV4 &&
            options.Target.Protocol != LoadProtocol.QGdw3761)
        {
            errors.Add("target.compatibilityProfile: gatherTcpCenter 和 gatherV4 当前只支持 Q/GDW 376.1 协议。");
        }
        if (options.Devices.Count <= 0)
            errors.Add("devices.count: 设备总数必须大于 0。");
        ValidateSafety(options, errors);
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
        ValidateGatherV4Response(options, errors);
        ValidateGatherV4Report(options, errors);
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
            ValidateRate(phase.MinimumOnlineRate, $"scenario.phases[{phase.Name}].minimumOnlineRate", errors);
            if (phase.WaitForOnline && phase.MinimumOnlineRate <= 0)
                errors.Add($"scenario.phases[{phase.Name}].minimumOnlineRate: 开启在线屏障时必须大于 0。");
            if (phase.OnlineStableSeconds < 0)
                errors.Add($"scenario.phases[{phase.Name}].onlineStableSeconds: 不能小于 0。");
            if (phase.WaitForOnline && phase.OnlineWaitTimeoutSeconds <= 0)
                errors.Add($"scenario.phases[{phase.Name}].onlineWaitTimeoutSeconds: 开启在线屏障时必须大于 0。");
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
        if (options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherV4 &&
            options.Acceptance.MinimumGatherReadingResponseRate.HasValue)
        {
            errors.Add(
                "acceptance.minimumGatherReadingResponseRate: 属于旧 GatherTcpCenter 专用累计口径；" +
                "GatherV4 请使用 minimumCommandDeliveryRate、minimumCommandRecognitionRate 和阶段 minimumResponseRate。");
        }
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

    /// <summary>校验高负载授权、故障注入授权、环境标签和可选目标白名单。</summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收全部配置错误的集合。</param>
    private static void ValidateSafety(LoadTestOptions options, ICollection<string> errors)
    {
        var safety = options.Safety;
        if (string.IsNullOrWhiteSpace(safety.Environment))
            errors.Add("safety.environment: 不能为空。");
        if (safety.HighLoadThreshold <= 0)
            errors.Add("safety.highLoadThreshold: 必须大于 0。");
        else if (options.Devices.Count > safety.HighLoadThreshold && !safety.AllowHighLoad)
            errors.Add($"safety.allowHighLoad: devices.count={options.Devices.Count} 超过保护阈值 {safety.HighLoadThreshold}，必须显式设置为 true。");

        if (safety.AllowedTargets.Any(string.IsNullOrWhiteSpace))
            errors.Add("safety.allowedTargets: 不能包含空白地址。");
        if (safety.AllowedTargets.Count > 0 &&
            !safety.AllowedTargets.Contains(options.Target.Host, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"safety.allowedTargets: target.host={options.Target.Host} 不在目标白名单中。");
        }
        if (safety.AllowedTargets.Count > 0 &&
            options.CommandWorkload.Enabled &&
            Uri.TryCreate(options.CommandWorkload.BaseUrl, UriKind.Absolute, out var commandBaseUri) &&
            !safety.AllowedTargets.Contains(commandBaseUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            // combined 场景会同时向 TCP 与 HTTP 两个地址产生压力，因此两者都必须经过同一份显式白名单审核。
            errors.Add($"safety.allowedTargets: commandWorkload.baseUrl 主机 {commandBaseUri.Host} 不在目标白名单中。");
        }

        var hasProtocolResponseFault =
            options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherTcpCenter &&
            options.Workload.GatherReadingResponse.Enabled &&
            options.Workload.GatherReadingResponse.Mode != GatherReadingResponseMode.Normal ||
            options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherV4 &&
            (!options.Workload.GatherV4Response.Enabled ||
             options.Workload.GatherV4Response.Mode != GatherV4ResponseMode.Normal);
        var hasFaultInjection = options.Scenario.Phases.Any(HasFaultInjection) || hasProtocolResponseFault;
        if (hasFaultInjection && !safety.AllowFaultInjection)
            errors.Add("safety.allowFaultInjection: 场景包含网络或断连故障注入，必须显式设置为 true。");
    }

    /// <summary>判断阶段是否包含会改变正常网络行为的故障注入。</summary>
    /// <param name="phase">待检查场景阶段。</param>
    /// <returns>任意故障参数非零时返回 true。</returns>
    private static bool HasFaultInjection(ScenarioPhaseOptions phase)
    {
        var faults = phase.Faults;
        return faults.NetworkLatencyMilliseconds > 0 ||
               faults.OutboundPacketLossRate > 0 ||
               faults.InboundPacketLossRate > 0 ||
               faults.CorruptChecksumRate > 0 ||
               faults.DuplicateFrameRate > 0 ||
               faults.DisconnectPercentage > 0 ||
               faults.FragmentSizeBytes > 0;
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

    /// <summary>校验 GatherV4 命令应答模式的枚举、影响比例、延迟和适用兼容配置。</summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收全部配置错误的集合。</param>
    private static void ValidateGatherV4Response(LoadTestOptions options, ICollection<string> errors)
    {
        var response = options.Workload.GatherV4Response;
        if (response.MaximumPendingResponsesPerDevice is < 1 or > 1024)
            errors.Add("workload.gatherV4Response.maximumPendingResponsesPerDevice: 必须在 1～1024 之间。");
        if (response.RepeatObservationSeconds is < 1 or > 3600)
            errors.Add("workload.gatherV4Response.repeatObservationSeconds: 必须在 1～3600 秒之间。");
        if (!Enum.IsDefined(response.Mode))
            errors.Add("workload.gatherV4Response.mode: 应答模式不受支持。");
        if (response.AffectedDevicePercentage is < 0 or > 100)
            errors.Add("workload.gatherV4Response.affectedDevicePercentage: 必须在 0-100 之间。");
        if (response.DelayMilliseconds < 0)
            errors.Add("workload.gatherV4Response.delayMilliseconds: 不能小于 0。");
        if (options.Target.CompatibilityProfile != ServerCompatibilityProfile.GatherV4 &&
            (!response.Enabled || response.Mode != GatherV4ResponseMode.Normal))
        {
            errors.Add("workload.gatherV4Response: 关闭应答或非 normal 模式只适用于 gatherV4 兼容配置。");
        }
    }

    /// <summary>校验 GatherV4 真实主动上报模板的十六进制、帧完整性、方向和服务端4096字节上限。</summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收全部配置错误的集合。</param>
    private static void ValidateGatherV4Report(LoadTestOptions options, ICollection<string> errors)
    {
        var report = options.Workload.GatherV4Report;
        if (!string.IsNullOrWhiteSpace(report.FrameHexTemplate) &&
            options.Target.CompatibilityProfile != ServerCompatibilityProfile.GatherV4)
        {
            errors.Add("workload.gatherV4Report.frameHexTemplate: 只适用于 gatherV4 兼容配置。");
        }
        if (!options.Workload.ReportsEnabled ||
            options.Target.CompatibilityProfile != ServerCompatibilityProfile.GatherV4)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(report.FrameHexTemplate))
        {
            errors.Add("workload.gatherV4Report.frameHexTemplate: GatherV4 启用周期上报时必须提供真实完整帧模板。");
            return;
        }

        try
        {
            var compact = string.Concat(report.FrameHexTemplate.Where(character =>
                !char.IsWhiteSpace(character) && character is not ',' and not '-'));
            var bytes = Convert.FromHexString(compact);
            if (bytes.Length > 4_096)
                errors.Add("workload.gatherV4Report.frameHexTemplate: 完整帧不能超过 GatherV4 服务端 4096 字节上限。");
            var parsed = DeviceSocket.Core.Protocols.QGdw3761.QGdw3761Codec.Read(bytes).Package;
            if (parsed is not DeviceSocket.Core.Protocols.QGdw3761.QGdw3761Frame frame ||
                !frame.IsValid ||
                frame.Raw.Length != bytes.Length)
            {
                errors.Add("workload.gatherV4Report.frameHexTemplate: 必须是校验和正确且不包含尾随字节的完整376.1帧。");
            }
            else if (!frame.IsUpstream || !frame.IsPrimaryMessage)
            {
                errors.Add("workload.gatherV4Report.frameHexTemplate: 必须是终端主动上行帧（DIR=1、PRM=1）。");
            }
            else if (frame.Afn == 0x02)
            {
                errors.Add("workload.gatherV4Report.frameHexTemplate: AFN=02H 属于登录/心跳链路帧，不能冒充周期业务上报模板。");
            }
        }
        catch (FormatException)
        {
            errors.Add("workload.gatherV4Report.frameHexTemplate: 不是有效的偶数字节十六进制字符串。");
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
        if (options.Target.CompatibilityProfile is not (ServerCompatibilityProfile.GatherTcpCenter or ServerCompatibilityProfile.GatherV4) ||
            options.Target.Protocol != LoadProtocol.QGdw3761)
        {
            errors.Add("commandWorkload: 当前只支持 Q/GDW 376.1 的 gatherTcpCenter 或 gatherV4 服务。");
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
        if (options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherV4)
            ValidateGatherV4Command(options, errors);
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
            if (phase.PostConfirmationObservationSeconds is < 0 or > 3600)
                errors.Add($"{path}.postConfirmationObservationSeconds: 必须在 0～3600 秒之间。");
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

        if (options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherV4 &&
            command.Phases.Any(phase => phase.DrainEnabled) &&
            options.Reporting.DownstreamDiagnostics?.Enabled != true)
        {
            errors.Add(
                "reporting.downstreamDiagnostics.enabled: GatherV4 启用业务排空时必须开启命令追踪，" +
                "否则无法把 HTTP 请求、TCP 下行和设备应答关联起来。");
        }
    }

    /// <summary>校验 GatherV4 IssueCmd 的固定入口、同步语义、采集服务、表类型、采集项和动态时间格式。</summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="errors">接收全部配置错误的集合。</param>
    private static void ValidateGatherV4Command(
        LoadTestOptions options,
        ICollection<string> errors)
    {
        var command = options.CommandWorkload;
        var gatherV4 = command.GatherV4;
        if (gatherV4.ExpectedCommandsPerRequest is < 1 or > 1024)
            errors.Add("commandWorkload.gatherV4.expectedCommandsPerRequest: 必须在 1～1024 之间，并与预检拆分命令数一致。");
        if (gatherV4.ExpectedResultItemCodes is null || gatherV4.ExpectedResultItemCodes.Any(string.IsNullOrWhiteSpace))
            errors.Add("commandWorkload.gatherV4.expectedResultItemCodes: 返回项编码不能为空。");
        var normalizedEndpoint = command.EndpointPath.Trim().TrimEnd('/');
        if (!string.Equals(normalizedEndpoint, "/Handmould/IssueCmd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedEndpoint, "/Handmould/IssueCmdSync", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "commandWorkload.endpointPath: gatherV4 仅支持 /Handmould/IssueCmd 或 /Handmould/IssueCmdSync，" +
                "不能沿用老服务 /api/ammeter/reading。");
        }
        if (command.RequestTimeoutSeconds <= 60)
        {
            errors.Add(
                "commandWorkload.requestTimeoutSeconds: GatherV4 同步接口内部可等待设备结果约 60 秒，" +
                "客户端超时必须大于 60 秒才能区分服务端结果与客户端提前取消。");
        }
        if (command.Phases.Any(phase => !phase.ShowResult))
        {
            errors.Add(
                "commandWorkload.phases[].showResult: GatherV4 IssueCmd 属于同步结果接口，所有阶段必须设置为 true，" +
                "不能按旧服务异步受理口径统计。");
        }
        if (string.IsNullOrWhiteSpace(gatherV4.GatherCode))
            errors.Add("commandWorkload.gatherV4.gatherCode: GatherV4 指令负载必须填写与服务端一致的采集服务编码。");
        if (gatherV4.MeterType <= 0)
            errors.Add("commandWorkload.gatherV4.meterType: 必须大于 0 并与 GatherV4 MeterTypeEnum 一致。");
        if (gatherV4.ItemCodes.Count == 0 || gatherV4.ItemCodes.Any(string.IsNullOrWhiteSpace))
            errors.Add("commandWorkload.gatherV4.itemCodes: 至少填写一个非空的 GatherV4 采集项编码。");
        if (gatherV4.Point < 0)
            errors.Add("commandWorkload.gatherV4.point: 不能小于 0。");
        if (string.IsNullOrWhiteSpace(gatherV4.DataTimeFormat))
        {
            errors.Add("commandWorkload.gatherV4.dataTimeFormat: 不能为空。");
        }
        else if (string.IsNullOrWhiteSpace(gatherV4.DataTime))
        {
            try
            {
                _ = DateTimeOffset.Now.ToString(
                    gatherV4.DataTimeFormat,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                errors.Add("commandWorkload.gatherV4.dataTimeFormat: 不是有效的 .NET 日期时间格式。");
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
