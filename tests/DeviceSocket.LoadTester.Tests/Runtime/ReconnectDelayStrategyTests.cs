using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Tests.Runtime;

/// <summary>
/// 验证自动重连的固定、指数和风暴等待规则。
/// </summary>
public sealed class ReconnectDelayStrategyTests
{
    /// <summary>
    /// 验证指数退避在达到配置上限后不再增长。
    /// </summary>
    [Fact]
    public void ExponentialDelay_IsCapped()
    {
        var options = new ReconnectOptions
        {
            Mode = ReconnectMode.ExponentialJitter,
            InitialDelayMilliseconds = 500,
            MaximumDelaySeconds = 2,
            Multiplier = 2,
            JitterRatio = 0
        };
        var strategy = new ReconnectDelayStrategy(options, new Random(1));

        Assert.Equal(TimeSpan.FromMilliseconds(500), strategy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1), strategy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), strategy.GetDelay(10));
    }

    /// <summary>
    /// 验证正向随机抖动也不能突破显式配置的最大重连间隔。
    /// </summary>
    [Fact]
    public void PositiveJitter_DoesNotExceedHardCap()
    {
        var options = new ReconnectOptions
        {
            Mode = ReconnectMode.ExponentialJitter,
            InitialDelayMilliseconds = 2_000,
            MaximumDelaySeconds = 2,
            Multiplier = 2,
            JitterRatio = 1
        };
        var strategy = new ReconnectDelayStrategy(options, new Random(2));

        for (var index = 0; index < 100; index++)
            Assert.InRange(strategy.GetDelay(10), TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 验证连接风暴使用立即重连时不引入隐式延迟。
    /// </summary>
    [Fact]
    public void ImmediateMode_HasNoDelay()
    {
        var options = new ReconnectOptions { Mode = ReconnectMode.Immediate };
        var strategy = new ReconnectDelayStrategy(options, new Random(1));

        Assert.Equal(TimeSpan.Zero, strategy.GetDelay(100));
    }
}
