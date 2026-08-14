using DeviceSocket.LoadTester.Configuration;
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
}
