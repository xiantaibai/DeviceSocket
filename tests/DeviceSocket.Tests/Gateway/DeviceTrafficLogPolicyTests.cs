using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Configuration.Logging;
using DeviceSocket.Host.Observability;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证心跳默认过滤、设备白名单和配置热更新日志策略。
/// </summary>
public sealed class DeviceTrafficLogPolicyTests
{
    /// <summary>默认配置应跳过心跳，但普通业务报文始终记录。</summary>
    [Fact]
    public void ShouldRecord_DefaultConfigurationSuppressesOnlyHeartbeat()
    {
        var monitor = new MutableOptionsMonitor<DeviceLoggingOptions>(new DeviceLoggingOptions());
        var metrics = new DeviceTrafficLoggingMetrics();
        using var policy = new DeviceTrafficLogPolicy(monitor, metrics);

        Assert.False(policy.ShouldRecord(ProtocolTrafficKind.Heartbeat, "device-001"));
        Assert.True(policy.ShouldRecord(ProtocolTrafficKind.Business, "device-001"));
        Assert.Equal(1, metrics.HeartbeatSuppressed);
    }

    /// <summary>配置热更新加入设备编号后，应立即记录该设备心跳而无需重建策略。</summary>
    [Fact]
    public void ShouldRecord_ConfigurationReloadEnablesSpecifiedDevice()
    {
        var monitor = new MutableOptionsMonitor<DeviceLoggingOptions>(new DeviceLoggingOptions());
        var metrics = new DeviceTrafficLoggingMetrics();
        using var policy = new DeviceTrafficLogPolicy(monitor, metrics);

        monitor.Set(new DeviceLoggingOptions
        {
            HeartbeatEnabledDeviceIds = ["device-001"]
        });

        Assert.True(policy.ShouldRecord(ProtocolTrafficKind.Heartbeat, "DEVICE-001"));
        Assert.False(policy.ShouldRecord(ProtocolTrafficKind.Heartbeat, "device-002"));
        Assert.Equal(1, metrics.HeartbeatRecordedByDevice);
        Assert.Equal(1, metrics.HeartbeatSuppressed);
    }

    /// <summary>采样比例为一时应记录全部设备心跳，并计入采样命中而不是设备白名单命中。</summary>
    [Fact]
    public void ShouldRecord_FullSamplingRecordsHeartbeat()
    {
        var monitor = new MutableOptionsMonitor<DeviceLoggingOptions>(new DeviceLoggingOptions
        {
            HeartbeatSampleRate = 1
        });
        var metrics = new DeviceTrafficLoggingMetrics();
        using var policy = new DeviceTrafficLogPolicy(monitor, metrics);

        Assert.True(policy.ShouldRecord(ProtocolTrafficKind.Heartbeat, "device-001"));
        Assert.Equal(1, metrics.HeartbeatRecordedBySampling);
        Assert.Equal(0, metrics.HeartbeatRecordedByDevice);
        Assert.Equal(0, metrics.HeartbeatSuppressed);
    }

    /// <summary>为单元测试提供可主动触发变更通知的强类型配置监视器。</summary>
    private sealed class MutableOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        private TOptions _value = value;
        private Action<TOptions, string?>? _listener;

        /// <inheritdoc />
        public TOptions CurrentValue => _value;

        /// <inheritdoc />
        public TOptions Get(string? name) => _value;

        /// <inheritdoc />
        public IDisposable OnChange(Action<TOptions, string?> listener)
        {
            _listener = listener;
            return new ChangeSubscription(() => _listener = null);
        }

        /// <summary>替换当前配置并触发变更订阅。</summary>
        /// <param name="newValue">新的配置值。</param>
        public void Set(TOptions newValue)
        {
            _value = newValue;
            _listener?.Invoke(newValue, Options.DefaultName);
        }

        /// <summary>释放测试配置变更回调。</summary>
        private sealed class ChangeSubscription(Action dispose) : IDisposable
        {
            /// <inheritdoc />
            public void Dispose() => dispose();
        }
    }
}
