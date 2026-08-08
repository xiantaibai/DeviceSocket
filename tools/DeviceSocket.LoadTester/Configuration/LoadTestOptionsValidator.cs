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
    public static IReadOnlyList<string> Validate(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Target.Host))
            errors.Add("target.host: 服务器地址不能为空。");
        if (options.Target.Port is <= 0 or > 65535)
            errors.Add("target.port: 端口必须在 1-65535 之间。");
        if (options.Devices.Count <= 0)
            errors.Add("devices.count: 设备总数必须大于 0。");
        if (options.Devices.AddressStart <= 0)
            errors.Add("devices.addressStart: 起始地址必须大于 0。");
        if (options.Devices.DuplicateIdentityPercentage is < 0 or > 100)
            errors.Add("devices.duplicateIdentityPercentage: 必须在 0-100 之间。");

        var duplicateCount = (int)Math.Floor(
            options.Devices.Count * options.Devices.DuplicateIdentityPercentage / 100d);
        var uniqueIdentityCount = Math.Max(1, options.Devices.Count - duplicateCount);
        var requiredAddressOffset = uniqueIdentityCount - 1L;
        if (options.Target.Protocol == LoadProtocol.QGdw3761)
        {
            if (string.IsNullOrEmpty(options.Devices.AdministrativeDivision) ||
                options.Devices.AdministrativeDivision.Length != 4 ||
                !options.Devices.AdministrativeDivision.All(char.IsDigit))
            {
                errors.Add("devices.administrativeDivision: 376.1 行政区划必须是 4 位十进制数字。");
            }

            if (options.Devices.AddressStart > ushort.MaxValue ||
                requiredAddressOffset > ushort.MaxValue - options.Devices.AddressStart)
            {
                errors.Add("devices: 376.1 终端地址范围不能超过 65535。");
            }
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
            ValidateFaults(phase, errors);
        }

        if (options.Reporting.SampleIntervalSeconds <= 0)
            errors.Add("reporting.sampleIntervalSeconds: 必须大于 0。");
        if (string.IsNullOrWhiteSpace(options.Reporting.OutputDirectory))
            errors.Add("reporting.outputDirectory: 输出目录不能为空。");
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
        if (options.Acceptance.MaximumRecoverySeconds is <= 0)
            errors.Add("acceptance.maximumRecoverySeconds: 设置后必须大于 0。");
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
    /// 校验完整配置，在存在任意错误时抛出包含全部原因的异常。
    /// </summary>
    public static void ValidateAndThrow(LoadTestOptions options)
    {
        var errors = Validate(options);
        if (errors.Count > 0)
            throw new ConfigurationValidationException(errors);
    }

    private static void ValidatePositiveSeconds(int value, string path, ICollection<string> errors)
    {
        if (value <= 0)
            errors.Add($"{path}: 必须大于 0。");
    }

    private static void ValidateRate(double? value, string path, ICollection<string> errors)
    {
        if (value is < 0 or > 1)
            errors.Add($"{path}: 必须在 0-1 之间。");
    }

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
