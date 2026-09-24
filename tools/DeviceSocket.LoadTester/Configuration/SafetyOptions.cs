namespace DeviceSocket.LoadTester.Configuration;

/// <summary>配置 <c>safety</c> 下高负载、故障注入和目标地址保护，避免误向非测试环境发起破坏性压测。</summary>
public sealed class SafetyOptions
{
    /// <summary>
    /// 获取或设置 <c>safety.environment</c>；默认 <c>test</c>，作为报告中的环境标签，不参与凭据或网络地址推断，空值无效。
    /// </summary>
    public string Environment { get; set; } = "test";

    /// <summary>
    /// 获取或设置 <c>safety.highLoadThreshold</c>；单位设备数，默认 10000，超过该值时必须显式开启 <see cref="AllowHighLoad"/>，必须大于零。
    /// </summary>
    public int HighLoadThreshold { get; set; } = 10_000;

    /// <summary>
    /// 获取或设置 <c>safety.allowHighLoad</c>；默认 false，表示拒绝超过 <see cref="HighLoadThreshold"/> 的设备规模，防止误运行三万或五万场景。
    /// </summary>
    public bool AllowHighLoad { get; set; }

    /// <summary>
    /// 获取或设置 <c>safety.allowFaultInjection</c>；默认 false，表示存在断连、丢包、延迟、校验和破坏、重复帧或拆包注入时拒绝启动。
    /// </summary>
    public bool AllowFaultInjection { get; set; }

    /// <summary>
    /// 获取或设置 <c>safety.allowedTargets</c>；默认空集合表示兼容既有场景而不启用白名单；非空时 TCP 的 <c>target.host</c> 与已启用 HTTP 指令负载的 <c>commandWorkload.baseUrl</c> 主机都必须与其中一项完全匹配，不解析 DNS 别名。
    /// </summary>
    public List<string> AllowedTargets { get; set; } = [];
}
