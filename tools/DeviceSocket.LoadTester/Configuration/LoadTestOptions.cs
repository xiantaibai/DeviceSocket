namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示一轮可独立执行和跨节点复现的完整负载测试配置。
/// </summary>
public sealed class LoadTestOptions
{
    /// <summary>
    /// 获取或设置 <c>role</c>；默认 devicesOnly，控制当前进程是否建立 TCP 会话、调用 HTTP API 或同时执行两者。
    /// </summary>
    public LoadTestRole Role { get; set; } = LoadTestRole.DevicesOnly;

    /// <summary>
    /// 获取或设置 <c>target</c>；定义 TCP 目标地址、基础协议和服务端兼容配置。
    /// </summary>
    public TargetOptions Target { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>devices</c>；定义所有分片共享的虚拟设备身份空间。
    /// </summary>
    public DeviceOptions Devices { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>connection</c>；定义建连速率、并发和超时行为。
    /// </summary>
    public ConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>heartbeat</c>；定义设备上线后的协议心跳周期和确认超时。
    /// </summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>workload</c>；定义设备周期上报和 Gather 读表业务应答行为。
    /// </summary>
    public WorkloadOptions Workload { get; set; } = new();
    /// <summary>
    /// 获取或设置 <c>commandWorkload</c>；定义独立于 TCP 接收循环的 Gather 读表 HTTP 指令压测。
    /// </summary>
    public CommandWorkloadOptions CommandWorkload { get; set; } = new();
    /// <summary>
    /// 获取或设置 <c>reconnect</c>；定义连接断开后的自动重连和退避策略。
    /// </summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>scenario</c>；定义 TCP 设备规模随时间变化及故障注入阶段。
    /// </summary>
    public ScenarioOptions Scenario { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>reporting</c>；定义采样周期、控制台输出和报告目录。
    /// </summary>
    public ReportingOptions Reporting { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>serverObservation</c>；定义可选服务端状态接口采样。
    /// </summary>
    public ServerObservationOptions ServerObservation { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>acceptance</c>；定义测试结束时的自动验收门槛。
    /// </summary>
    public AcceptanceOptions Acceptance { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>distributed</c>；定义跨负载节点运行编号、分片和同步启动时刻。
    /// </summary>
    public DistributedOptions Distributed { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>safety</c>；定义高负载、故障注入和目标地址白名单保护，不改变正常压测的协议语义。
    /// </summary>
    public SafetyOptions Safety { get; set; } = new();
}
