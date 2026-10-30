using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Runtime;
using System.Text.Json;

namespace DeviceSocket.LoadTester.Tests.Configuration;

/// <summary>
/// 验证负载配置在地址空间、分片和阶段参数上的失败语义。
/// </summary>
public sealed class LoadTestOptionsValidatorTests
{
    /// <summary>新增人工命令场景使用已配置的 0D_82，必须全量覆盖、包含同设备五次排队，并可切为纯设备待命。</summary>
    /// <param name="count">全局设备数量。</param>
    [Theory]
    [InlineData(10000)]
    [InlineData(50000)]
    public async Task ManualCommandScenarios_CoverAllDevicesAndSupportPassiveOverride(int count)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", $"gatherv4-manual-command-{count}.json");
        var combined = await LoadTestConfigurationLoader.LoadAsync(path, new RunCommandOverrides());
        Assert.True(combined.CommandWorkload.GatherV4.ValidateResultData);
        Assert.Equal(new[] { "0D_82" }, combined.CommandWorkload.GatherV4.ItemCodes);
        Assert.Empty(combined.CommandWorkload.GatherV4.ExpectedResultItemCodes);
        Assert.Contains(combined.CommandWorkload.Phases, phase => phase.TargetDeviceCount == count && phase.RequestsPerDevice == 1);
        Assert.Contains(combined.CommandWorkload.Phases, phase => phase.RequestsPerDevice == 5);
        Assert.All(combined.CommandWorkload.Phases, phase => Assert.Equal(30, phase.PostConfirmationObservationSeconds));
        var passive = await LoadTestConfigurationLoader.LoadAsync(path, new RunCommandOverrides(Role: LoadTestRole.DevicesOnly));
        Assert.False(passive.CommandWorkload.Enabled);
        Assert.True(passive.Workload.GatherV4Response.Enabled);
        Assert.Equal(LoadTestRole.DevicesOnly, passive.Role);
    }
    /// <summary>
    /// 验证默认测试配置可以通过完整校验。
    /// </summary>
    [Fact]
    public void ValidOptions_HaveNoErrors()
    {
        var errors = LoadTestOptionsValidator.Validate(TestOptionsFactory.Create());

        Assert.Empty(errors);
    }

    /// <summary>
    /// 验证 Gather.TcpCenter 兼容配置只允许与 Q/GDW 376.1 协议组合。
    /// </summary>
    [Fact]
    public void GatherCompatibilityProfile_RejectsDlt645()
    {
        var options = TestOptionsFactory.Create(LoadProtocol.Dlt6452007);
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("gatherTcpCenter", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证老服务万人纯连接场景使用数据库真实 CSV 映射和 Gather 兼容配置。
    /// </summary>
    [Fact]
    public async Task GatherTenThousandScenario_UsesPreparedDeviceMapping()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Scenarios",
            "gather-tcpcenter-connections-10000.json");
        var options = await LoadTestConfigurationLoader.LoadAsync(
            path,
            new RunCommandOverrides(),
            CancellationToken.None);
        var devices = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(ServerCompatibilityProfile.GatherTcpCenter, options.Target.CompatibilityProfile);
        Assert.Equal(DeviceAddressGenerationMode.Csv, options.Devices.GenerationMode);
        Assert.Equal(10_000, devices.Count);
        Assert.Equal(10_000, devices.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(0, 10_000), devices.Select(item => item.GlobalOrdinal));
    }

    /// <summary>
    /// 验证376.1设备范围超过16位终端地址时会在执行前被拒绝。
    /// </summary>
    [Fact]
    public void QGdwAddressOverflow_IsRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.AddressStart = 65_530;
        options.Devices.Count = 10;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("65535", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证随机模式必须提供合法地址上界，且区间必须容纳全部唯一设备。
    /// </summary>
    [Fact]
    public void QGdwRandomAddressRange_IsValidated()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.GenerationMode = DeviceAddressGenerationMode.Random;
        options.Devices.Count = 10;
        options.Devices.AddressStart = 100;

        var missingEndErrors = LoadTestOptionsValidator.Validate(options);
        options.Devices.AddressEnd = 105;
        var insufficientRangeErrors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(missingEndErrors, item => item.Contains("addressEnd", StringComparison.Ordinal));
        Assert.Contains(insufficientRangeErrors, item => item.Contains("区间容量", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证随机模式固定行政区划，并拒绝当前未实现的645随机地址。
    /// </summary>
    [Fact]
    public void RandomMode_RejectsSpanningDivisionAndDlt645()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.GenerationMode = DeviceAddressGenerationMode.Random;
        options.Devices.AddressEnd = 100;
        options.Devices.SpanAdministrativeDivisions = true;

        var spanningErrors = LoadTestOptionsValidator.Validate(options);
        options.Target.Protocol = LoadProtocol.Dlt6452007;
        options.Devices.SpanAdministrativeDivisions = false;
        var dltErrors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(spanningErrors, item => item.Contains("不能跨区划", StringComparison.Ordinal));
        Assert.Contains(dltErrors, item => item.Contains("只支持 Q/GDW 376.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证 CSV 设备模式必须提供存在的文件、仅支持376.1，且不能再叠加重复身份比例。
    /// </summary>
    [Fact]
    public void CsvMode_ValidatesFileProtocolAndDuplicateIdentity()
    {
        var options = TestOptionsFactory.Create(LoadProtocol.Dlt6452007);
        options.Devices.GenerationMode = DeviceAddressGenerationMode.Csv;
        options.Devices.DeviceMappingFile = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv");
        options.Devices.DuplicateIdentityPercentage = 1;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("文件不存在", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("只支持 Q/GDW 376.1", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("必须为 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证极大起始地址不会因 long 加法回绕而绕过645地址上限校验。
    /// </summary>
    [Fact]
    public void Dlt645LongOverflow_IsRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Target.Protocol = LoadProtocol.Dlt6452007;
        options.Devices.AddressStart = long.MaxValue;
        options.Devices.Count = 2;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("12 位", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证分片数大于设备数会产生空分片并被明确拒绝。
    /// </summary>
    [Fact]
    public void EmptyShardConfiguration_IsRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 2;
        options.Distributed.ShardCount = 3;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("空分片", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证仓库交付的全部示例场景都能被实际 JSON 加载器解析并通过校验。
    /// </summary>
    [Fact]
    public async Task ShippedScenarios_AreValid()
    {
        var scenarioDirectory = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        var paths = Directory.GetFiles(scenarioDirectory, "*.json");
        var expectedNames = new[]
        {
            "gather-tcpcenter-connections-10000.json",
            "gather-tcpcenter-http-reading-10000.json",
            "gather-tcpcenter-http-reading-50000.json",
            "gather-tcpcenter-reconnect-storm-10000.json",
            "gather-tcpcenter-reconnect-storm-50000.json",
            "gather-tcpcenter-soak-10000.json",
            "gatherv4-connections-10000.json",
            "gatherv4-connections-50000.json",
            "gatherv4-http-reading-10000.json",
            "gatherv4-manual-command-10000.json",
            "gatherv4-manual-command-50000.json",
            "gatherv4-reconnect-storm-10000.json",
            "gatherv4-reconnect-storm-50000.json"
        };

        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            paths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var path in paths)
        {
            var options = await LoadTestConfigurationLoader.LoadAsync(
                path,
                new RunCommandOverrides(),
                CancellationToken.None);
            Assert.Empty(LoadTestOptionsValidator.Validate(options));
        }
    }

    /// <summary>
    /// 验证标准 HTTP 场景等待一万台稳定在线、覆盖全部设备，并使用可逐级排空的基准请求速率。
    /// </summary>
    [Fact]
    public async Task StandardGatherHttpScenario_CoversAllOnlineDevicesAndUsesNormalRequestRate()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Scenarios",
            "gather-tcpcenter-http-reading-10000.json");

        var options = await LoadTestConfigurationLoader.LoadAsync(
            path,
            new RunCommandOverrides(),
            CancellationToken.None);
        var allDevicesPhase = Assert.Single(
            options.CommandWorkload.Phases,
            phase => phase.Name == "all-devices-async-once");

        Assert.Equal(10_000, options.Devices.Count);
        Assert.Equal(LoadTestRole.Combined, options.Role);
        Assert.Equal(CommandTargetSource.OnlineRegistry, options.CommandWorkload.TargetDeviceSource);
        Assert.Equal(0, allDevicesPhase.TargetDeviceCount);
        Assert.Equal(1, allDevicesPhase.RequestsPerDevice);
        Assert.Equal(10_000, options.CommandWorkload.MinimumOnlineCount);
        Assert.Equal(60, options.CommandWorkload.OnlineStableSeconds);
        Assert.Equal(50, allDevicesPhase.RequestsPerSecond);
        Assert.Equal(100, allDevicesPhase.MaxConcurrency);
        Assert.False(allDevicesPhase.ShowResult);
        Assert.True(allDevicesPhase.DrainEnabled);
        Assert.Equal(0.98, allDevicesPhase.MinimumRawDeliveryRate);
        Assert.Equal(0.98, allDevicesPhase.MinimumDeliveryRate);
        Assert.Equal(CommandDrainFailureAction.AbortRun, allDevicesPhase.DrainFailureAction);
        Assert.True(allDevicesPhase.CircuitBreakerEnabled);
    }

    /// <summary>
    /// 验证两个五万台正式场景均使用四分片、完整真实 CSV 和已经按五万规模校正的关键门槛。
    /// </summary>
    [Fact]
    public async Task FiftyThousandGatherScenarios_UseFourShardsAndScaledThresholds()
    {
        var scenarioDirectory = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        var reconnectOptions = await LoadTestConfigurationLoader.LoadAsync(
            Path.Combine(scenarioDirectory, "gather-tcpcenter-reconnect-storm-50000.json"),
            new RunCommandOverrides(),
            CancellationToken.None);
        var httpOptions = await LoadTestConfigurationLoader.LoadAsync(
            Path.Combine(scenarioDirectory, "gather-tcpcenter-http-reading-50000.json"),
            new RunCommandOverrides(),
            CancellationToken.None);
        var allDevicesPhase = Assert.Single(
            httpOptions.CommandWorkload.Phases,
            phase => phase.Name == "all-devices-async-once-50000");

        Assert.Equal(50_000, reconnectOptions.Devices.Count);
        Assert.Equal(4, reconnectOptions.Distributed.ShardCount);
        Assert.Equal("gather-tcpcenter-reconnect-storm-50000", reconnectOptions.Scenario.Name);
        Assert.Equal(49_000, reconnectOptions.Acceptance.MinimumReconnects);
        Assert.Contains(reconnectOptions.Scenario.Phases, phase => phase.Name == "ramp-50000");
        Assert.Contains(reconnectOptions.Scenario.Phases, phase => phase.Name == "recovery-50000");
        Assert.Equal(20, reconnectOptions.Scenario.Phases.Single(
            phase => phase.Name == "restart-tcpcenter-now").ManualAction.PreparationSeconds);
        Assert.Equal(60, reconnectOptions.Scenario.Phases.Single(
            phase => phase.Name == "restart-tcpcenter-now").ManualAction.OperationSeconds);

        Assert.Equal(50_000, httpOptions.Devices.Count);
        Assert.Equal(4, httpOptions.Distributed.ShardCount);
        Assert.Equal(LoadTestRole.Combined, httpOptions.Role);
        Assert.Equal(CommandTargetSource.OnlineRegistry, httpOptions.CommandWorkload.TargetDeviceSource);
        Assert.Equal(50_000, httpOptions.CommandWorkload.MinimumOnlineCount);
        Assert.Equal(0, allDevicesPhase.TargetDeviceCount);
        Assert.Equal(1, allDevicesPhase.RequestsPerDevice);
        Assert.Equal(50, allDevicesPhase.RequestsPerSecond);
        Assert.Equal(100, allDevicesPhase.MaxConcurrency);
        Assert.Equal(1_500, allDevicesPhase.MaximumDurationSeconds);
        Assert.Equal(1_800, allDevicesPhase.DrainTimeoutSeconds);
        Assert.Equal(2_400, httpOptions.Reporting.DownstreamDiagnostics.CorrelationTimeoutSeconds);
    }

    /// <summary>
    /// 验证 GatherV4 五万台常态和重连风暴场景使用四分片、同一真实设备全集及按五万规模设置的在线门槛。
    /// </summary>
    [Fact]
    public async Task FiftyThousandGatherV4Scenarios_UseFourShardsAndScaledThresholds()
    {
        var scenarioDirectory = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        var connectionOptions = await LoadTestConfigurationLoader.LoadAsync(
            Path.Combine(scenarioDirectory, "gatherv4-connections-50000.json"),
            new RunCommandOverrides(),
            CancellationToken.None);
        var reconnectOptions = await LoadTestConfigurationLoader.LoadAsync(
            Path.Combine(scenarioDirectory, "gatherv4-reconnect-storm-50000.json"),
            new RunCommandOverrides(),
            CancellationToken.None);

        Assert.Equal(50_000, connectionOptions.Devices.Count);
        Assert.Equal(4, connectionOptions.Distributed.ShardCount);
        Assert.Equal(ServerCompatibilityProfile.GatherV4, connectionOptions.Target.CompatibilityProfile);
        Assert.Equal("gatherv4-device-50000.csv", Path.GetFileName(connectionOptions.Devices.DeviceMappingFile));
        var steadyPhase = Assert.Single(
            connectionOptions.Scenario.Phases,
            phase => phase.Name == "steady-50000");
        Assert.Equal(3_600, steadyPhase.DurationSeconds);
        Assert.True(steadyPhase.WaitForOnline);
        Assert.Equal(0.99, steadyPhase.MinimumOnlineRate);

        Assert.Equal(50_000, reconnectOptions.Devices.Count);
        Assert.Equal(4, reconnectOptions.Distributed.ShardCount);
        Assert.Equal(ServerCompatibilityProfile.GatherV4, reconnectOptions.Target.CompatibilityProfile);
        Assert.Equal("gatherv4-device-50000.csv", Path.GetFileName(reconnectOptions.Devices.DeviceMappingFile));
        Assert.Equal(49_000, reconnectOptions.Acceptance.MinimumReconnects);
        Assert.Equal(300, reconnectOptions.Acceptance.MaximumRecoverySeconds);
        Assert.Contains(reconnectOptions.Scenario.Phases, phase => phase.Name == "ramp-50000");
        Assert.Contains(reconnectOptions.Scenario.Phases, phase => phase.Name == "recovery-50000");
        var restartPhase = Assert.Single(
            reconnectOptions.Scenario.Phases,
            phase => phase.Name == "restart-gatherv4-now");
        Assert.Equal(20, restartPhase.ManualAction.PreparationSeconds);
        Assert.Equal(60, restartPhase.ManualAction.OperationSeconds);
    }

    /// <summary>
    /// 验证四台负载机读取同一份完整 CSV 时，每个真实设备只会分配给一个分片且全局没有遗漏。
    /// </summary>
    /// <param name="scenarioFileName">需要验证的正式场景文件名。</param>
    /// <param name="globalDeviceCount">四个分片合计的预期设备数。</param>
    /// <param name="localDeviceCount">每个分片的预期固定设备数。</param>
    [Theory]
    [InlineData("gather-tcpcenter-connections-10000.json", 10_000, 2_500)]
    [InlineData("gather-tcpcenter-reconnect-storm-50000.json", 50_000, 12_500)]
    public async Task GatherCsvScenario_FourShardsPartitionAllDevicesWithoutDuplicates(
        string scenarioFileName,
        int globalDeviceCount,
        int localDeviceCount)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Scenarios",
            scenarioFileName);
        var ordinals = new HashSet<int>();

        for (var shardIndex = 0; shardIndex < 4; shardIndex++)
        {
            var options = await LoadTestConfigurationLoader.LoadAsync(
                path,
                new RunCommandOverrides(ShardIndex: shardIndex, ShardCount: 4),
                CancellationToken.None);
            var devices = DeviceAddressAllocator.Allocate(options);

            Assert.Equal(localDeviceCount, devices.Count);
            Assert.All(devices, device => Assert.True(ordinals.Add(device.GlobalOrdinal)));
        }

        Assert.Equal(globalDeviceCount, ordinals.Count);
        Assert.Equal(Enumerable.Range(0, globalDeviceCount), ordinals.Order());
    }

    /// <summary>
    /// 验证 TCP 建连速率是所有分片的全局预算，并支持用小数精确分摊低速配置。
    /// </summary>
    /// <param name="globalRate">场景配置的所有分片合计连接数/秒。</param>
    /// <param name="shardCount">共同运行的分片总数。</param>
    /// <param name="expectedLocalRate">单个分片应得到的连接数/秒。</param>
    [Theory]
    [InlineData(300, 4, 75)]
    [InlineData(1, 4, 0.25)]
    public void GlobalConnectionRate_IsDistributedAcrossShards(
        int globalRate,
        int shardCount,
        double expectedLocalRate)
    {
        var actual = LoadTestRunner.ResolveLocalConnectionRate(globalRate, shardCount);

        Assert.Equal(expectedLocalRate, actual, 6);
    }

    /// <summary>
    /// 验证故障注入概率和断线比例越界时会在运行前被拒绝。
    /// </summary>
    [Fact]
    public void InvalidFaultRates_AreRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Scenario.Phases.Add(new ScenarioPhaseOptions
        {
            Name = "fault",
            DurationSeconds = 1,
            Faults = new FaultInjectionOptions
            {
                OutboundPacketLossRate = 1.1,
                DisconnectPercentage = 101
            }
        });

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("outboundPacketLossRate", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("disconnectPercentage", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证拼错的 JSON 配置字段会被拒绝，避免静默回退到默认压测参数。
    /// </summary>
    [Fact]
    public void UnknownJsonProperty_IsRejected()
    {
        const string json = """
            {
              "target": {
                "host": "127.0.0.1",
                "connectTimeoutSecond": 3
              }
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<LoadTestOptions>(
                json,
                LoadTestConfigurationLoader.CreateSerializerOptions()));
    }

    /// <summary>
    /// 验证运行编号不能无限扩展输出路径长度。
    /// </summary>
    [Fact]
    public void ExcessiveRunId_IsRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Distributed.RunId = new string('x', 101);

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("100", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证 combined 角色必须显式启用 HTTP 指令负载，避免用户误以为已同时压测 API。
    /// </summary>
    [Fact]
    public void CombinedRole_RequiresCommandWorkload()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("commandWorkload.enabled", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证使用顺序 meter_id、合法 API 地址和指令阶段时 combined 老服务配置可以通过校验。
    /// </summary>
    [Fact]
    public void CombinedGatherCommandWorkload_IsValid()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:8080",
            MeterIdStart = 1_000,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "functional",
                    TargetDeviceCount = 10,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 10,
                    MaxConcurrency = 5
                }
            ]
        };

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Empty(errors);
    }

    /// <summary>
    /// 验证在线注册表目标来源只允许 combined 角色使用，apiOnly 必须保持固定序号读取。
    /// </summary>
    [Fact]
    public void OnlineRegistryTargetSource_RequiresCombinedRole()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:8080",
            MeterIdStart = 1_000,
            TargetDeviceSource = CommandTargetSource.OnlineRegistry,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "online-only",
                    TargetDeviceCount = 1,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 10,
                    MaxConcurrency = 1
                }
            ]
        };

        var apiOnlyErrors = LoadTestOptionsValidator.Validate(options);
        options.Role = LoadTestRole.Combined;
        var combinedErrors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(
            apiOnlyErrors,
            error => error.Contains("onlineRegistry", StringComparison.Ordinal) &&
                     error.Contains("combined", StringComparison.Ordinal));
        Assert.Empty(combinedErrors);
    }

    /// <summary>
    /// 验证 HTTP 阶段在线门槛不能超过全局设备数，且阶段等待上限设置后必须大于零。
    /// </summary>
    [Fact]
    public void CommandPhaseOnlineGate_InvalidValuesAreRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Devices.Count = 20;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:8080",
            MeterIdStart = 1_000,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "invalid-gate",
                    TargetDeviceCount = 10,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 10,
                    MaxConcurrency = 5,
                    MinimumOnlineCount = 21,
                    OnlineStableSeconds = -1,
                    OnlineWaitTimeoutSeconds = 0
                }
            ]
        };

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("minimumOnlineCount", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("onlineStableSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("onlineWaitTimeoutSeconds", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证恢复检查点周期不能短于基础采样周期，否则检查点配置会造成无法兑现的写入频率。
    /// </summary>
    [Fact]
    public void ReportingCheckpointInterval_ShorterThanSampleIntervalIsRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Reporting.SampleIntervalSeconds = 5;
        options.Reporting.CheckpointIntervalSeconds = 4;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("checkpointIntervalSeconds", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证下行诊断样本上限、Hex 长度和关联窗口越界时会在压测启动前被拒绝。
    /// </summary>
    [Fact]
    public void DownstreamDiagnostics_InvalidBoundsAreRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Reporting.DownstreamDiagnostics = new DownstreamDiagnosticsOptions
        {
            MaxSamplesPerFingerprint = 101,
            MaxTotalSamples = 10_001,
            RawHexMaxBytes = 4097,
            CorrelationTimeoutSeconds = 0
        };

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("maxSamplesPerFingerprint", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("maxTotalSamples", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("rawHexMaxBytes", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("correlationTimeoutSeconds", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证计划服务端重启必须配置明确的人工操作窗口，避免报告无法判断哪次进程变化属于预期行为。
    /// </summary>
    [Fact]
    public void ExpectedServerRestart_RequiresValidManualActionWindow()
    {
        var options = TestOptionsFactory.Create();
        options.Scenario.Phases =
        [
            new ScenarioPhaseOptions
            {
                Name = "restart",
                DurationSeconds = 60,
                ExpectedServerRestart = true
            }
        ];

        var missingWindowErrors = LoadTestOptionsValidator.Validate(options);
        options.Scenario.Phases[0].ManualAction = new ManualActionOptions
        {
            Enabled = true,
            Message = "请重启服务",
            PreparationSeconds = 5,
            OperationSeconds = 30
        };
        var validErrors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(missingWindowErrors, item => item.Contains("manualAction", StringComparison.Ordinal));
        Assert.Empty(validErrors);
    }

    /// <summary>
    /// 验证 GatherV4 不允许误用旧服务入口、异步成功口径和旧版读表累计验收指标。
    /// </summary>
    [Fact]
    public void GatherV4CommandWorkload_RejectsLegacySemantics()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:5000",
            EndpointPath = "/api/ammeter/reading",
            RequestTimeoutSeconds = 60,
            MeterIdStart = 1_000,
            GatherV4 = new GatherV4CommandOptions
            {
                GatherCode = "v4-test",
                ItemCodes = ["10_1"]
            },
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "wrong-async",
                    TargetDeviceCount = 1,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 1,
                    MaxConcurrency = 1,
                    ShowResult = false
                }
            ]
        };
        options.Acceptance.MinimumGatherReadingResponseRate = 0.99;

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("/Handmould/IssueCmd", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("大于 60 秒", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("所有阶段必须设置为 true", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("旧 GatherTcpCenter 专用", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证超过容量阈值或启用断连故障时，场景必须显式声明相应安全授权。
    /// </summary>
    [Fact]
    public void HighLoadAndFaultInjection_RequireExplicitSafetyFlags()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 10_001;
        options.Devices.SpanAdministrativeDivisions = true;
        options.Scenario.Phases =
        [
            new ScenarioPhaseOptions
            {
                Name = "disconnect",
                DurationSeconds = 1,
                Faults = new FaultInjectionOptions { DisconnectPercentage = 10 }
            }
        ];

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("safety.allowHighLoad", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("safety.allowFaultInjection", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证 combined 场景启用目标白名单后，TCP 服务和 HTTP 指令 API 的主机都必须显式获准。
    /// </summary>
    [Fact]
    public void AllowedTargets_RequiresBothTcpAndCommandApiHosts()
    {
        var options = TestOptionsFactory.Create();
        options.Role = LoadTestRole.Combined;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Target.Host = "192.168.1.10";
        options.CommandWorkload = new CommandWorkloadOptions
        {
            Enabled = true,
            BaseUrl = "http://192.168.1.20:8080",
            MeterIdStart = 1_000,
            Phases =
            [
                new CommandPhaseOptions
                {
                    Name = "whitelist",
                    TargetDeviceCount = 1,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 1,
                    MaxConcurrency = 1
                }
            ]
        };
        options.Safety.AllowedTargets = ["192.168.1.10"];

        var missingApiHostErrors = LoadTestOptionsValidator.Validate(options);
        options.Safety.AllowedTargets.Add("192.168.1.20");
        var allowedErrors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(
            missingApiHostErrors,
            item => item.Contains("commandWorkload.baseUrl", StringComparison.Ordinal));
        Assert.Empty(allowedErrors);
    }

    /// <summary>
    /// 验证 TCP 阶段在线屏障拒绝零在线率、负稳定时间和零等待上限，避免运行后形成永久或无意义等待。
    /// </summary>
    [Fact]
    public void ScenarioOnlineBarrier_InvalidBoundsAreRejected()
    {
        var options = TestOptionsFactory.Create();
        options.Scenario.Phases =
        [
            new ScenarioPhaseOptions
            {
                Name = "invalid-barrier",
                DurationSeconds = 1,
                WaitForOnline = true,
                MinimumOnlineRate = 0,
                OnlineStableSeconds = -1,
                OnlineWaitTimeoutSeconds = 0
            }
        ];

        var errors = LoadTestOptionsValidator.Validate(options);

        Assert.Contains(errors, item => item.Contains("minimumOnlineRate", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("onlineStableSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("onlineWaitTimeoutSeconds", StringComparison.Ordinal));
    }
}
