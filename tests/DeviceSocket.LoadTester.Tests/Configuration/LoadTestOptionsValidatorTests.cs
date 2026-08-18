using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using System.Text.Json;

namespace DeviceSocket.LoadTester.Tests.Configuration;

/// <summary>
/// 验证负载配置在地址空间、分片和阶段参数上的失败语义。
/// </summary>
public sealed class LoadTestOptionsValidatorTests
{
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
    /// 验证老服务万人场景使用已准备的260810001～260820000连续设备号和 Gather 兼容配置。
    /// </summary>
    [Fact]
    public async Task GatherTenThousandScenario_UsesPreparedDeviceRange()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", "intranet-local-5000-10000.json");
        var options = await LoadTestConfigurationLoader.LoadAsync(
            path,
            new RunCommandOverrides(),
            CancellationToken.None);
        var devices = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(ServerCompatibilityProfile.GatherTcpCenter, options.Target.CompatibilityProfile);
        Assert.Equal(10_000, devices.Count);
        Assert.Equal("3761:2608-10001", devices[0].DeviceId);
        Assert.Equal("3761:2608-20000", devices[^1].DeviceId);
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

        Assert.NotEmpty(paths);
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
    /// 验证 HTTP 阶段在线门槛不能超过全局设备数，且阶段等待上限设置后必须大于零。
    /// </summary>
    [Fact]
    public void CommandPhaseOnlineGate_InvalidValuesAreRejected()
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
                    Name = "invalid-gate",
                    TargetDeviceCount = 10,
                    RequestsPerDevice = 1,
                    RequestsPerSecond = 10,
                    MaxConcurrency = 5,
                    MinimumOnlineCount = 11,
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
}
