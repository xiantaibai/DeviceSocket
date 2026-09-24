namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>commandWorkload</c> 下调用 Gather.TcpCenter 或 GatherV4 业务服务读表 API 的独立 HTTP 负载。
/// </summary>
public sealed class CommandWorkloadOptions
{
    /// <summary>
    /// 获取或设置 <c>commandWorkload.targetDeviceSource</c>；默认 <c>fixedOrdinal</c>，
    /// 表示保持当前按 GlobalOrdinal 固定截取 HTTP 目标设备的行为；
    /// <c>onlineRegistry</c> 表示从本进程 TCP 会话的全部在线设备中按阶段数量选择目标，只允许与 combined 角色组合。
    /// </summary>
    public CommandTargetSource TargetDeviceSource { get; set; } = CommandTargetSource.FixedOrdinal;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.enabled</c>；默认 false，启用后非 devicesOnly 角色会执行 HTTP 指令阶段。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.baseUrl</c>；默认空值无效，必须是老 Gather.Control.Api 的绝对 HTTP/HTTPS 根地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.endpointPath</c>；默认 <c>/api/ammeter/reading</c>，表示读表 POST 接口的相对路径。
    /// </summary>
    public string EndpointPath { get; set; } = "/api/ammeter/reading";

    /// <summary>
    /// 获取或设置 <c>commandWorkload.authorizationHeaderName</c>；默认 <c>gather-authorize-key</c>，空值表示不发送鉴权头。
    /// </summary>
    public string AuthorizationHeaderName { get; set; } = "gather-authorize-key";

    /// <summary>
    /// 获取或设置 <c>commandWorkload.authorizationKeyEnvironmentVariable</c>；默认 <c>GATHER_AUTHORIZE_KEY</c>，运行时从该环境变量读取密钥，避免密钥进入配置和报告，空值表示不读取。
    /// </summary>
    public string AuthorizationKeyEnvironmentVariable { get; set; } = "GATHER_AUTHORIZE_KEY";

    /// <summary>
    /// 获取或设置 <c>commandWorkload.deviceMappingFile</c>；默认空，CSV 每行格式为 <c>meterId,deviceId</c>；设备使用 CSV 模式时空值表示复用 <c>devices.deviceMappingFile</c>，否则与 <see cref="MeterIdStart"/> 二选一；仅 <c>qGdw3761 + gatherTcpCenter</c> 兼容配置允许真实特殊终端地址 <c>00000</c>。
    /// </summary>
    public string? DeviceMappingFile { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.meterIdStart</c>；默认空，设置后按全局设备序号递增生成 meter_id，与 <see cref="DeviceMappingFile"/> 二选一，零值无效。
    /// </summary>
    public int? MeterIdStart { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.operatorUserId</c>；默认 0，作为请求体 oper_uid，仅用于压测审计标识。
    /// </summary>
    public int OperatorUserId { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.operationSource</c>；默认 6，对应老项目 <c>OperationSource.Test</c>。
    /// </summary>
    public int OperationSource { get; set; } = 6;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.readingModel</c>；默认 0，表示普通 Q/GDW 376.1 抄读。
    /// </summary>
    public int ReadingModel { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.readingCode</c>；默认 0，透传给老读表接口的 readingCode。
    /// </summary>
    public int ReadingCode { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4</c>；仅当 <c>target.compatibilityProfile=gatherV4</c> 时生效，
    /// 定义 IssueCmd 请求中的采集服务编码、表类型、采集项和数据时间。
    /// </summary>
    public GatherV4CommandOptions GatherV4 { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>commandWorkload.requestTimeoutSeconds</c>；单位秒，默认 70，应大于老 API 同步等待设备结果的 60 秒上限。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 70;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.requiredOnlineRate</c>；默认 0.98，combined 角色达到该在线率后才允许开始 HTTP 压测；
    /// <c>targetDeviceSource=onlineRegistry</c> 时在线率按阶段计划目标数计算，可由本节点任意在线设备满足。
    /// </summary>
    public double RequiredOnlineRate { get; set; } = 0.98;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.minimumOnlineCount</c>；单位为全部分片合计在线设备数，默认空表示各阶段按
    /// <see cref="RequiredOnlineRate"/> 与阶段目标数计算，零表示全局不要求在线。
    /// 设置为 10000 可保证小流量探测阶段也必须等一万台设备稳定在线后才启动。
    /// </summary>
    public int? MinimumOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.onlineStableSeconds</c>；单位秒，默认 30，在线率必须连续满足该时长才开始下发，零表示命中后立即开始。
    /// </summary>
    public int OnlineStableSeconds { get; set; } = 30;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.onlineWaitTimeoutSeconds</c>；单位秒，默认 600，作为未单独配置阶段的在线等待上限，零值无效。
    /// 超时后的行为由阶段的 <see cref="CommandPhaseOptions.OnlineGateFailureAction"/> 决定，默认跳过阶段而不是中断报告生成。
    /// </summary>
    public int OnlineWaitTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.startDelaySeconds</c>；单位秒，默认 0；apiOnly 分离部署时可用于等待另一台负载机完成设备上线，零表示立即执行阶段。
    /// </summary>
    public int StartDelaySeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases</c>；按列表顺序执行 HTTP 指令阶段，默认空集合且启用时无效。
    /// </summary>
    public List<CommandPhaseOptions> Phases { get; set; } = [];
}
