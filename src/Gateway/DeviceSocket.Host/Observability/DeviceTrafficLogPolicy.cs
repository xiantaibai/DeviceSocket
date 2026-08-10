using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Configuration.Logging;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Observability;

/// <summary>
/// 在通讯报文生成 RawHex 前决定是否记录，并支持配置热更新的心跳设备白名单和确定性采样。
/// </summary>
public sealed class DeviceTrafficLogPolicy : IDisposable
{
    private const uint FnvOffsetBasis = 2_166_136_261;
    private const uint FnvPrime = 16_777_619;
    private const uint SamplingBuckets = 10_000;
    private readonly DeviceTrafficLoggingMetrics _metrics;
    private readonly IDisposable? _changeRegistration;
    private PolicyState _state;

    /// <summary>
    /// 从可热更新日志配置创建策略，设备列表只在配置变化时构造成哈希集合。
    /// </summary>
    /// <param name="options">日志配置监视器。</param>
    /// <param name="metrics">心跳过滤观测指标。</param>
    public DeviceTrafficLogPolicy(
        IOptionsMonitor<DeviceLoggingOptions> options,
        DeviceTrafficLoggingMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        _metrics = metrics;
        _state = PolicyState.Create(options.CurrentValue);
        _changeRegistration = options.OnChange(value =>
            Volatile.Write(ref _state, PolicyState.Create(value)));
    }

    /// <summary>
    /// 判断一条合法上行帧是否进入最近消息和通讯日志；非心跳始终记录。
    /// </summary>
    /// <param name="trafficKind">协议帧业务类型。</param>
    /// <param name="deviceId">协议解析出的设备编号。</param>
    /// <returns>需要生成 RawHex 并提交日志时返回 true。</returns>
    public bool ShouldRecord(ProtocolTrafficKind trafficKind, string? deviceId)
    {
        if (trafficKind != ProtocolTrafficKind.Heartbeat)
            return true;

        var state = Volatile.Read(ref _state);
        if (deviceId is not null && state.EnabledDeviceIds.Contains(deviceId))
        {
            _metrics.HeartbeatEvaluated(HeartbeatLogDecision.DeviceEnabled);
            return true;
        }

        if (deviceId is not null && IsSampled(deviceId, state.SampleRate))
        {
            _metrics.HeartbeatEvaluated(HeartbeatLogDecision.Sampled);
            return true;
        }

        _metrics.HeartbeatEvaluated(HeartbeatLogDecision.Suppressed);
        return false;
    }

    /// <summary>释放配置变更订阅。</summary>
    public void Dispose() => _changeRegistration?.Dispose();

    /// <summary>
    /// 使用稳定设备哈希判断是否命中采样，同一设备跨进程重启保持一致结果。
    /// </summary>
    /// <param name="deviceId">设备唯一标识。</param>
    /// <param name="sampleRate">零到一之间的设备采样比例。</param>
    /// <returns>设备命中稳定采样桶时返回 true。</returns>
    private static bool IsSampled(string deviceId, double sampleRate)
    {
        if (sampleRate <= 0)
            return false;
        if (sampleRate >= 1)
            return true;

        // 使用稳定 FNV-1a 哈希选择设备，保证同一设备在进程重启后仍得到相同采样结果，
        // 同时避免为每条心跳创建随机数状态或临时字符串。
        var hash = FnvOffsetBasis;
        foreach (var character in deviceId)
        {
            hash ^= char.ToUpperInvariant(character);
            hash *= FnvPrime;
        }

        var threshold = (uint)Math.Round(sampleRate * SamplingBuckets, MidpointRounding.AwayFromZero);
        return hash % SamplingBuckets < threshold;
    }

    /// <summary>保存一次配置更新后不可变的心跳策略快照。</summary>
    private sealed record PolicyState(double SampleRate, HashSet<string> EnabledDeviceIds)
    {
        /// <summary>
        /// 从强类型配置创建已校验、去重且适合热路径读取的策略快照。
        /// </summary>
        /// <param name="options">当前日志配置。</param>
        /// <returns>新的不可变策略快照。</returns>
        public static PolicyState Create(DeviceLoggingOptions options) => new(
            Math.Clamp(options.HeartbeatSampleRate, 0, 1),
            new HashSet<string>(
                (options.HeartbeatEnabledDeviceIds ?? []).Where(static value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase));
    }
}
