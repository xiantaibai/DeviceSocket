namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 表示通过 <c>report generate --background</c> 导入的测试背景 JSON 根对象；所有字段只用于报告追溯和展示，不影响压测结果计算。
/// </summary>
internal sealed class DetailedReportBackgroundOptions
{
    /// <summary>获取或设置 <c>report</c>；默认空对象，保存标题、环境和测试人员，节为 null 时按空对象处理。</summary>
    public DetailedReportIdentityOptions Report { get; set; } = new();

    /// <summary>获取或设置 <c>scenario</c>；默认空对象，补充测试场景名称和范围说明，节为 null 时按空对象处理。</summary>
    public DetailedReportScenarioOptions Scenario { get; set; } = new();

    /// <summary>获取或设置 <c>server</c>；默认空对象，记录被测服务器名称、系统、CPU、内存和网络规格，节为 null 时按空对象处理。</summary>
    public DetailedReportServerOptions Server { get; set; } = new();

    /// <summary>获取或设置 <c>service</c>；默认空对象，记录服务版本、代码版本和关键运行参数，节为 null 时按空对象处理。</summary>
    public DetailedReportServiceOptions Service { get; set; } = new();
}

/// <summary>
/// 表示测试背景 JSON 的 <c>report</c> 节，用于补充报告身份信息。
/// </summary>
internal sealed class DetailedReportIdentityOptions
{
    /// <summary>获取或设置 <c>report.title</c>；默认 null 表示根据 RunId 自动生成，命令行 <c>--title</c> 的优先级更高。</summary>
    public string? Title { get; set; }

    /// <summary>获取或设置 <c>report.environment</c>；默认 null 表示未填写，命令行 <c>--environment</c> 的优先级更高。</summary>
    public string? Environment { get; set; }

    /// <summary>获取或设置 <c>report.tester</c>；默认 null 表示未填写，命令行 <c>--tester</c> 的优先级更高。</summary>
    public string? Tester { get; set; }
}

/// <summary>
/// 表示测试背景 JSON 的 <c>scenario</c> 节，用于解释本轮测试目的和覆盖范围。
/// </summary>
internal sealed class DetailedReportScenarioOptions
{
    /// <summary>获取或设置 <c>scenario.name</c>；默认 null 表示使用节点摘要中的 <c>scenario.name</c>。</summary>
    public string? Name { get; set; }

    /// <summary>获取或设置 <c>scenario.description</c>；默认 null 表示根据目标设备数、协议、运行角色和阶段数量自动生成摘要。</summary>
    public string? Description { get; set; }
}

/// <summary>
/// 表示测试背景 JSON 的 <c>server</c> 节，用于记录结果文件本身无法提供的服务器基础规格。
/// </summary>
internal sealed class DetailedReportServerOptions
{
    /// <summary>获取或设置 <c>server.name</c>；默认 null 表示使用场景目标主机，建议填写资产名或测试节点名。</summary>
    public string? Name { get; set; }

    /// <summary>获取或设置 <c>server.operatingSystem</c>；默认 null 表示未填写，例如 Windows Server 2022 或 Ubuntu 24.04。</summary>
    public string? OperatingSystem { get; set; }

    /// <summary>获取或设置 <c>server.cpu</c>；默认 null 表示未填写，值中应同时包含核心数和处理器型号。</summary>
    public string? Cpu { get; set; }

    /// <summary>获取或设置 <c>server.memory</c>；默认 null 表示未填写，值中应包含容量和必要的内存规格。</summary>
    public string? Memory { get; set; }

    /// <summary>获取或设置 <c>server.network</c>；默认 null 表示未填写，例如 1 Gbps 公司内网。</summary>
    public string? Network { get; set; }
}

/// <summary>
/// 表示测试背景 JSON 的 <c>service</c> 节，用于追溯被测程序版本和运行参数。
/// </summary>
internal sealed class DetailedReportServiceOptions
{
    /// <summary>获取或设置 <c>service.name</c>；默认 null 表示未填写，例如 Gather.TcpCenter。</summary>
    public string? Name { get; set; }

    /// <summary>获取或设置 <c>service.version</c>；默认 null 表示未填写，命令行 <c>--service-version</c> 的优先级更高。</summary>
    public string? Version { get; set; }

    /// <summary>获取或设置 <c>service.codeBranch</c>；默认 null 表示未填写，应记录生成被测程序的 Git 分支。</summary>
    public string? CodeBranch { get; set; }

    /// <summary>获取或设置 <c>service.codeCommit</c>；默认 null 表示未填写，建议使用完整或不少于七位的 Git Commit。</summary>
    public string? CodeCommit { get; set; }

    /// <summary>获取或设置 <c>service.parameters</c>；默认空列表，每项显示一个与本轮结论相关的服务运行参数，最多 50 项。</summary>
    public List<DetailedReportParameterOptions> Parameters { get; set; } = [];
}

/// <summary>
/// 表示测试背景 JSON 的单个 <c>service.parameters[]</c> 名值参数。
/// </summary>
internal sealed class DetailedReportParameterOptions
{
    /// <summary>获取或设置 <c>service.parameters[].name</c>；无默认值，必须是非空且在当前列表中唯一的参数名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>获取或设置 <c>service.parameters[].value</c>；无默认值，必须包含单位或完整可读值，不能是空字符串。</summary>
    public string Value { get; set; } = string.Empty;
}
