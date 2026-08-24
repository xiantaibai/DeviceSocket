using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示一次从完整协议在线跌落到重新达到阶段目标的连续掉线事件，用于区分计划重启、二次风暴和零星抖动。
/// </summary>
public sealed record OutageEventSnapshot
{
    /// <summary>获取节点进程内递增的掉线事件编号，从 1 开始。</summary>
    public required long Sequence { get; init; }

    /// <summary>获取掉线发生时的场景阶段名称；未进入显式阶段时为空字符串。</summary>
    public required string PhaseName { get; init; }

    /// <summary>获取掉线发生时的场景阶段类型。</summary>
    public required ScenarioPhaseKind PhaseKind { get; init; }

    /// <summary>获取当前阶段是否声明为计划中的服务端重启；true 不代表重启一定成功执行。</summary>
    public required bool ExpectedServerRestart { get; init; }

    /// <summary>获取首次从目标在线数跌落的 UTC 时间。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>获取重新达到目标在线数的 UTC 时间；仍处于掉线状态时为空。</summary>
    public DateTimeOffset? RecoveredAtUtc { get; init; }

    /// <summary>获取截至恢复或快照时刻的持续时间，单位毫秒。</summary>
    public required long DurationMilliseconds { get; init; }

    /// <summary>获取掉线开始时要求保持的协议在线设备数。</summary>
    public required long ExpectedOnline { get; init; }

    /// <summary>获取本次事件期间观测到的最低协议在线设备数。</summary>
    public required long MinimumProtocolOnline { get; init; }

    /// <summary>获取相对目标在线数的最大离线设备数。</summary>
    public required long MaximumOfflineDevices { get; init; }

    /// <summary>获取本次事件期间新增的 TCP 连接尝试次数。</summary>
    public required long ConnectionAttempts { get; init; }

    /// <summary>获取本次事件期间新增的 TCP 连接失败次数。</summary>
    public required long ConnectionFailures { get; init; }

    /// <summary>获取本次事件期间新增的协议登录失败次数。</summary>
    public required long LoginFailures { get; init; }

    /// <summary>获取本次事件期间新增的心跳确认超时次数。</summary>
    public required long HeartbeatTimeouts { get; init; }

    /// <summary>获取本次事件期间新增的自动重连调度次数。</summary>
    public required long ReconnectsScheduled { get; init; }

    /// <summary>获取本次事件期间新增的本地源端口占用错误数，用于识别恢复过程中的负载机瓶颈。</summary>
    public required long AddressAlreadyInUse { get; init; }
}
