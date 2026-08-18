using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 根据固定或指数抖动配置计算单台虚拟设备的下一次重连等待时间，并保证结果不超过配置硬上限。
/// </summary>
/// <param name="options">重连模式、初始延迟、倍数、抖动和最大延迟配置。</param>
/// <param name="random">设备独立随机源，用于稳定复现指数退避抖动。</param>
internal sealed class ReconnectDelayStrategy(ReconnectOptions options, Random random)
{
    internal TimeSpan GetDelay(int attempt)
    {
        if (options.Mode == ReconnectMode.Immediate)
            return TimeSpan.Zero;

        var initial = Math.Max(0, options.InitialDelayMilliseconds);
        var milliseconds = options.Mode == ReconnectMode.Fixed
            ? initial
            : initial * Math.Pow(options.Multiplier, Math.Max(0, attempt - 1));
        var maximumMilliseconds = Math.Max(0, options.MaximumDelaySeconds) * 1000d;
        milliseconds = Math.Min(milliseconds, maximumMilliseconds);
        if (options.Mode == ReconnectMode.ExponentialJitter && milliseconds > 0 && options.JitterRatio > 0)
        {
            var factor = 1 + ((random.NextDouble() * 2 - 1) * options.JitterRatio);
            // 抖动用于打散重连，但 MaximumDelaySeconds 是保护服务端和负载机的硬上限。
            milliseconds = Math.Min(milliseconds * factor, maximumMilliseconds);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }
}
