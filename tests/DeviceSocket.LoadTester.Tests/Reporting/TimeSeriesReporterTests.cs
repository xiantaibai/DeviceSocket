using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证周期报告器在最终报告尚未生成时也会保存可解析的原子检查点。
/// </summary>
public sealed class TimeSeriesReporterTests
{
    /// <summary>
    /// 验证报告器启动后的首次采样会同时生成 JSONL 和包含运行身份、累计指标的检查点文件。
    /// </summary>
    [Fact]
    public async Task FirstSample_WritesRecoverableCheckpoint()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"device-load-checkpoint-{Guid.NewGuid():N}");
        var options = TestOptionsFactory.Create();
        options.Reporting.SampleIntervalSeconds = 1;
        options.Reporting.CheckpointIntervalSeconds = 1;
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(10, DateTimeOffset.UtcNow);
        var reporter = new TimeSeriesReporter(metrics, options, localDeviceCount: 10, outputDirectory);

        try
        {
            reporter.Start();
            var checkpointPath = Path.Combine(outputDirectory, "run-checkpoint.json");
            for (var attempt = 0; attempt < 100 && !File.Exists(checkpointPath); attempt++)
                await Task.Delay(50);

            Assert.True(File.Exists(checkpointPath), "首次采样后应生成恢复检查点。");
            await using var stream = File.OpenRead(checkpointPath);
            var checkpoint = await JsonSerializer.DeserializeAsync<RunCheckpoint>(
                stream,
                LoadTestConfigurationLoader.CreateSerializerOptions());

            Assert.NotNull(checkpoint);
            Assert.Equal("unit-test", checkpoint.RunId);
            Assert.Equal(10, checkpoint.LocalDeviceCount);
            Assert.Equal(10, checkpoint.Metrics.ExpectedOnline);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "node-timeseries.jsonl")));
        }
        finally
        {
            await reporter.DisposeAsync();
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
