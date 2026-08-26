using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 将统一离线分析模型写为标准 JSON、阶段 CSV、独立 SVG，以及面向验收或排障人员的 Markdown 和自包含 HTML。
/// </summary>
internal static class DetailedReportWriter
{
    /// <summary>
    /// 按受众参数创建输出目录并写入全部报告产物；源结果目录只读且不会被覆盖。
    /// </summary>
    /// <param name="analysis">当前运行分析模型。</param>
    /// <param name="audience">需要生成的报告受众。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="cancellationToken">取消文件写入的令牌。</param>
    /// <returns>报告文件写入完成的异步任务。</returns>
    internal static async Task WriteAsync(
        DetailedReportAnalysis analysis,
        DetailedReportAudience audience,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(outputDirectory);
        var chartDirectory = Path.Combine(root, "charts");
        Directory.CreateDirectory(chartDirectory);
        var charts = DetailedReportChartBuilder.Build(analysis);
        await WriteTextAsync(Path.Combine(chartDirectory, "online-timeline.svg"), charts.OnlineTimeline, cancellationToken);
        await WriteTextAsync(Path.Combine(chartDirectory, "business-online-gap.svg"), charts.BusinessOnlineGap, cancellationToken);
        await WriteTextAsync(Path.Combine(chartDirectory, "connection-errors.svg"), charts.ConnectionErrors, cancellationToken);
        await WriteTextAsync(Path.Combine(chartDirectory, "api-phases.svg"), charts.ApiPhases, cancellationToken);
        await WriteTextAsync(Path.Combine(chartDirectory, "command-funnel.svg"), charts.CommandFunnel, cancellationToken);
        await WriteAnalysisJsonAsync(analysis, Path.Combine(root, "analysis.json"), cancellationToken);
        await WriteTextAsync(Path.Combine(root, "phase-summary.csv"), BuildPhaseCsv(analysis), cancellationToken);

        if (audience is DetailedReportAudience.Acceptance or DetailedReportAudience.Both)
        {
            await WriteTextAsync(Path.Combine(root, "acceptance-report.md"), BuildAcceptanceMarkdown(analysis), cancellationToken);
            await WriteTextAsync(Path.Combine(root, "acceptance-report.html"), DetailedReportHtmlWriter.Build(analysis, charts, false), cancellationToken);
        }
        if (audience is DetailedReportAudience.Diagnostic or DetailedReportAudience.Both)
        {
            await WriteTextAsync(Path.Combine(root, "diagnostic-report.md"), BuildDiagnosticMarkdown(analysis), cancellationToken);
            await WriteTextAsync(Path.Combine(root, "diagnostic-report.html"), DetailedReportHtmlWriter.Build(analysis, charts, true), cancellationToken);
        }
    }

    /// <summary>
    /// 使用稳定 camelCase 和字符串枚举格式写入可供外部工具二次消费的分析 JSON。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <param name="path">目标 JSON 路径。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    /// <returns>写入完成的异步任务。</returns>
    private static async Task WriteAnalysisJsonAsync(
        DetailedReportAnalysis analysis,
        string path,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await WriteTextAsync(path, JsonSerializer.Serialize(analysis, options), cancellationToken);
    }

    /// <summary>
    /// 以 UTF-8 无 BOM 写入文本，保证 Windows 和 Linux 部署后中文与 SVG 均可稳定打开。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">完整文件内容。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    /// <returns>写入完成的异步任务。</returns>
    private static Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);

    /// <summary>
    /// 生成面向负责人和验收人员的精简 Markdown，优先展示结论、门槛、趋势和改造前后差异。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <returns>验收版 Markdown。</returns>
    private static string BuildAcceptanceMarkdown(DetailedReportAnalysis analysis)
    {
        var builder = new StringBuilder();
        AppendMarkdownHeader(builder, analysis, "验收汇报版");
        builder.AppendLine("## 验收结论").AppendLine();
        builder.AppendLine($"> **{(analysis.AcceptancePassed ? "通过" : "未通过")}**：{BuildExecutiveConclusion(analysis)}").AppendLine();
        AppendChecksMarkdown(builder, analysis.AcceptanceChecks);
        builder.AppendLine("## 核心趋势").AppendLine();
        builder.AppendLine("![连接与在线时间线](charts/online-timeline.svg)").AppendLine();
        builder.AppendLine("![业务在线差额](charts/business-online-gap.svg)").AppendLine();
        AppendKeyMetricsMarkdown(builder, analysis);
        AppendComparisonsMarkdown(builder, analysis.Comparisons);
        AppendWarningsMarkdown(builder, analysis.DataWarnings);
        return builder.ToString();
    }

    /// <summary>
    /// 生成面向开发和运维的完整 Markdown，保留所有图表、问题证据、阶段数据和资源峰值。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <returns>排障版 Markdown。</returns>
    private static string BuildDiagnosticMarkdown(DetailedReportAnalysis analysis)
    {
        var builder = new StringBuilder();
        AppendMarkdownHeader(builder, analysis, "排查分析版");
        builder.AppendLine("## 自动诊断").AppendLine();
        AppendFindingsMarkdown(builder, analysis.Findings);
        builder.AppendLine("## 连接与业务在线").AppendLine();
        builder.AppendLine("![连接与在线时间线](charts/online-timeline.svg)").AppendLine();
        builder.AppendLine("![登录慢路径与业务积压](charts/business-online-gap.svg)").AppendLine();
        builder.AppendLine("## 负载机连接错误").AppendLine();
        builder.AppendLine("![连接错误趋势](charts/connection-errors.svg)").AppendLine();
        builder.AppendLine("## HTTP 与设备命令链路").AppendLine();
        builder.AppendLine("![HTTP阶段结果](charts/api-phases.svg)").AppendLine();
        builder.AppendLine("![命令数量漏斗](charts/command-funnel.svg)").AppendLine();
        AppendKeyMetricsMarkdown(builder, analysis);
        AppendCommandPhasesMarkdown(builder, analysis.CommandPhases);
        AppendErrorsMarkdown(builder, analysis);
        builder.AppendLine("## 验收规则复核").AppendLine();
        AppendChecksMarkdown(builder, analysis.AcceptanceChecks);
        AppendComparisonsMarkdown(builder, analysis.Comparisons);
        AppendWarningsMarkdown(builder, analysis.DataWarnings);
        return builder.ToString();
    }

    /// <summary>
    /// 写入 Markdown 标题和可追溯测试元数据。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    /// <param name="edition">报告版本名称。</param>
    private static void AppendMarkdownHeader(StringBuilder builder, DetailedReportAnalysis analysis, string edition)
    {
        builder.AppendLine($"# {analysis.Title} — {edition}").AppendLine();
        builder.AppendLine($"- RunId：`{analysis.RunId}`");
        builder.AppendLine($"- 环境：{analysis.Environment}");
        builder.AppendLine($"- 测试场景：{analysis.Background.ScenarioName}");
        builder.AppendLine($"- 场景说明：{analysis.Background.ScenarioDescription}");
        builder.AppendLine($"- 服务器：{analysis.Background.ServerName}（{JoinMarkdownMetadata(analysis.Background.OperatingSystem, analysis.Background.Cpu, analysis.Background.Memory, analysis.Background.Network)}）");
        builder.AppendLine($"- 服务版本：{analysis.Background.ServiceName} · {analysis.ServiceVersion}");
        builder.AppendLine($"- 代码版本：{analysis.Background.CodeBranch} · {analysis.Background.CodeCommit}");
        builder.AppendLine($"- 关键参数：{FormatMarkdownParameters(analysis.Background.ServiceParameters)}");
        builder.AppendLine($"- 测试人员：{analysis.Tester}");
        builder.AppendLine($"- 时间：{analysis.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 至 {analysis.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 报告生成：{analysis.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 负载节点/设备目标：{analysis.NodeCount:N0} / {analysis.TargetDeviceCount:N0}").AppendLine();
    }

    /// <summary>
    /// 合并 Markdown 标题区域的服务器规格，忽略未填写字段但在全部缺失时保留明确占位。
    /// </summary>
    /// <param name="values">操作系统、CPU、内存和网络等规格值。</param>
    /// <returns>使用中点连接的规格文本。</returns>
    private static string JoinMarkdownMetadata(params string[] values)
    {
        var available = values.Where(item => item != "未填写").ToArray();
        return available.Length == 0 ? "未填写" : string.Join(" · ", available);
    }

    /// <summary>
    /// 将背景 JSON 中的关键参数格式化为紧凑 Markdown 文本。
    /// </summary>
    /// <param name="parameters">关键服务参数。</param>
    /// <returns>分号分隔的参数名和值；空列表返回未填写。</returns>
    private static string FormatMarkdownParameters(IReadOnlyList<DetailedReportParameter> parameters) =>
        parameters.Count == 0
            ? "未填写"
            : string.Join("；", parameters.Select(item => $"{item.Name}：{item.Value}"));

    /// <summary>
    /// 输出验收项 Markdown 表格。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="checks">三态验收项。</param>
    private static void AppendChecksMarkdown(StringBuilder builder, IReadOnlyList<DetailedAcceptanceCheck> checks)
    {
        builder.AppendLine("| 验收项 | 结果 | 实际值 | 门槛 | 依据 |");
        builder.AppendLine("| --- | :---: | ---: | ---: | --- |");
        foreach (var check in checks)
            builder.AppendLine($"| {Md(check.Name)} | {FormatStatus(check.Status)} | {Md(check.Actual)} | {Md(check.Expected)} | {Md(check.Evidence)} |");
        builder.AppendLine();
    }

    /// <summary>
    /// 输出连接、登录、服务端 Pipeline 和负载机资源核心指标。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendKeyMetricsMarkdown(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        var metrics = analysis.ClientMetrics;
        builder.AppendLine("## 核心指标").AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| 建连尝试/成功/失败 | {metrics.ConnectionAttempts:N0}/{metrics.ConnectionSucceeded:N0}/{metrics.ConnectionFailed:N0} |");
        builder.AppendLine($"| 登录尝试/成功/失败 | {metrics.LoginAttempts:N0}/{metrics.LoginSucceeded:N0}/{metrics.LoginFailed:N0} |");
        builder.AppendLine($"| 心跳发送/确认/超时 | {metrics.HeartbeatsSent:N0}/{metrics.HeartbeatsConfirmed:N0}/{metrics.HeartbeatTimeouts:N0} |");
        builder.AppendLine($"| 重连/掉线事件/非预期事件 | {metrics.ReconnectsScheduled:N0}/{metrics.OutageCount:N0}/{CountSecondaryOutages(analysis):N0} |");
        builder.AppendLine($"| 最严重恢复/当前连续稳定 | {FormatRecovery(metrics)}/{metrics.CurrentStableOnlineMilliseconds / 1000d:F1} 秒 |");
        builder.AppendLine($"| HTTP发起/成功/失败/超时 | {metrics.ApiRequestsStarted:N0}/{metrics.ApiRequestsSucceeded:N0}/{metrics.ApiRequestsFailed:N0}/{metrics.ApiRequestTimeouts:N0} |");
        builder.AppendLine($"| 设备命令收到/应答（旧累计口径） | {metrics.GatherReadingCommandsReceived:N0}/{metrics.GatherReadingCommandsResponded:N0} |");
        if (analysis.CommandPhases.Count > 0)
        {
            builder.AppendLine(
                $"| 原始下行/协议识别/不支持/应答/重复帧 | " +
                $"{analysis.CommandPhases.Sum(item => item.RawDownstreamCommands):N0}/" +
                $"{analysis.CommandPhases.Sum(item => item.RecognizedCommands):N0}/" +
                $"{analysis.CommandPhases.Sum(item => item.UnsupportedCommands):N0}/" +
                $"{analysis.CommandPhases.Sum(item => item.CommandsResponded):N0}/" +
                $"{analysis.CommandPhases.Sum(item => item.DuplicateDownstreamFrames):N0} |");
        }
        builder.AppendLine($"| 连接 P50/P95/P99 | {metrics.ConnectionLatency.Percentile(0.5)}/{metrics.ConnectionLatency.Percentile(0.95)}/{metrics.ConnectionLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 登录 P50/P95/P99 | {metrics.LoginLatency.Percentile(0.5)}/{metrics.LoginLatency.Percentile(0.95)}/{metrics.LoginLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| HTTP P50/P95/P99 | {metrics.ApiRequestLatency.Percentile(0.5)}/{metrics.ApiRequestLatency.Percentile(0.95)}/{metrics.ApiRequestLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 负载机CPU/工作集峰值 | {analysis.LoadGeneratorResources.MaximumCpuPercent:F2}%/{Bytes(analysis.LoadGeneratorResources.MaximumWorkingSetBytes)} |");
        if (analysis.Server is { } server)
        {
            builder.AppendLine($"| 服务物理/绑定/业务在线峰值 | {server.MaximumSocketConnections:N0}/{server.MaximumBoundDeviceConnections:N0}/{server.MaximumBusinessOnlineDevices:N0} |");
            builder.AppendLine($"| 业务在线追平 | {(server.BusinessCatchupSeconds.HasValue ? $"{server.BusinessCatchupSeconds:F1} 秒" : "未追平")} |");
            builder.AppendLine($"| Pipeline队列深度/年龄峰值 | {server.MaximumQueueDepth:N0}/{server.MaximumQueueAgeMilliseconds / 1000d:F1} 秒 |");
            builder.AppendLine($"| Pipeline失败/拒绝/ACK后拒绝 | {server.PipelineFailed:N0}/{server.PipelineRejected:N0}/{server.AckedButRejected:N0} |");
            builder.AppendLine($"| StaleBusiness/HeartbeatCoalesced | {server.StaleBusiness:N0}/{server.HeartbeatCoalesced:N0} |");
            builder.AppendLine($"| ACK组帧发送/发送完成最大耗时 | {server.MaximumAckElapsedMilliseconds:N0}/{server.MaximumAckSendCompletedElapsedMilliseconds:N0} ms |");
            builder.AppendLine($"| ACK待完成峰值/失败/中止 | {server.MaximumPendingConfirmSend:N0}/{server.AckFailed:N0}/{server.AckSendAborted:N0} |");
            builder.AppendLine($"| 登录业务就绪最大耗时 | {server.MaximumLoginBusinessReadyElapsedMilliseconds / 1000d:F1} 秒 |");
            builder.AppendLine($"| 待建命令线路/命令管线深度峰值 | {server.MaximumPendingCommandLine:N0}/{server.MaximumCommandLineQueueDepth:N0} |");
            builder.AppendLine($"| 命令线路压力保护次数/最长暂停 | {server.CommandLineStartPauseCount:N0}/{server.MaximumCommandLineStartPauseElapsedMilliseconds / 1000d:F1} 秒 |");
            builder.AppendLine($"| 连接关闭/错误/接收/其他 | {server.ConnectionClosed:N0}/{server.ConnectionCloseWithError:N0}/{server.ConnectionCloseReceive:N0}/{server.ConnectionCloseOther:N0} |");
        }
        if (analysis.CooldownServer is { } cooldown)
        {
            builder.AppendLine(
                $"| 冷却窗口连接关闭/ACK中止/命令暂停 | " +
                $"{cooldown.ConnectionClosed:N0}/{cooldown.AckSendAborted:N0}/{cooldown.CommandLineStartPauseCount:N0}（不参与验收） |");
        }
        builder.AppendLine();
    }

    /// <summary>
    /// 统计非计划重启中断；旧报告没有事件明细时按总事件减去一次计划重启近似计算。
    /// </summary>
    /// <param name="analysis">包含客户端事件和场景计划重启语义的分析模型。</param>
    /// <returns>非预期中断事件数。</returns>
    private static long CountSecondaryOutages(DetailedReportAnalysis analysis) =>
        analysis.ClientMetrics.Outages.Count > 0
            ? analysis.ClientMetrics.Outages.Count(item => !item.ExpectedServerRestart)
            : analysis.ExpectedServerRestart
                ? Math.Max(0, analysis.ClientMetrics.OutageCount - 1)
                : analysis.ClientMetrics.OutageCount;

    /// <summary>
    /// 将可空恢复毫秒值格式化为秒，缺少完整恢复事件时返回“未恢复”。
    /// </summary>
    /// <param name="milliseconds">恢复耗时毫秒数。</param>
    /// <returns>适合报告展示的恢复时间。</returns>
    private static string FormatRecovery(MetricSnapshot metrics)
    {
        if (metrics.OutageCount == 0 && !metrics.OutageActive)
            return "无掉线";
        return metrics.WorstRecoveryMilliseconds.HasValue
            ? $"{metrics.WorstRecoveryMilliseconds.Value / 1000d:F1} 秒"
            : "未恢复";
    }

    /// <summary>
    /// 输出自动诊断问题的分级证据列表。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="findings">待输出的问题集合。</param>
    private static void AppendFindingsMarkdown(StringBuilder builder, IEnumerable<DiagnosticFinding> findings)
    {
        var source = findings.ToArray();
        if (source.Length == 0)
        {
            builder.AppendLine("未触发自动诊断规则。").AppendLine();
            return;
        }
        foreach (var finding in source)
        {
            builder.AppendLine($"### [{finding.Severity}] {finding.Title}").AppendLine();
            builder.AppendLine($"- 证据：{finding.Evidence}");
            builder.AppendLine($"- 影响：{finding.Impact}");
            builder.AppendLine($"- 建议：{finding.Recommendation}");
            builder.AppendLine($"- 置信度：{FormatConfidence(finding.Confidence)}").AppendLine();
        }
    }

    /// <summary>
    /// 输出 HTTP 指令阶段 Markdown 表格。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="phases">合并阶段摘要。</param>
    private static void AppendCommandPhasesMarkdown(StringBuilder builder, IReadOnlyList<DetailedCommandPhaseSummary> phases)
    {
        if (phases.Count == 0)
            return;
        builder.AppendLine("## HTTP 阶段明细").AppendLine();
        builder.AppendLine("| 阶段 | 同步结果 | 计划/发起 | 成功/失败/超时 | 预校验/估算入队 | 原始下行/识别/不支持/应答/未观测 | 原始交付率/识别率/应答率 | 重复帧 | 排空 | P95 | 熔断 | 耗时 |");
        builder.AppendLine("| --- | :---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | :---: | ---: |");
        foreach (var phase in phases)
        {
            builder.AppendLine($"| {Md(phase.Name)} | {(phase.ShowResult ? "是" : "否")} | {phase.PlannedRequests:N0}/{phase.StartedRequests:N0} | {phase.SucceededRequests:N0}/{phase.FailedRequests:N0}/{phase.TimeoutRequests:N0} | {phase.PrevalidationRejected:N0}/{phase.EstimatedEnqueued:N0} | {phase.RawDownstreamCommands:N0}/{phase.RecognizedCommands:N0}/{phase.UnsupportedCommands:N0}/{phase.CommandsResponded:N0}/{phase.CommandsUnobserved:N0} | {phase.RawDeliveryRate:P2}/{phase.RecognitionRate:P2}/{phase.ResponseRate:P2} | {phase.DuplicateDownstreamFrames:N0} | {FormatDrain(phase)} | {phase.P95Milliseconds:N0} ms | {(phase.CircuitBreakerTripped ? "是" : "否")} | {(phase.EndedAtUtc - phase.StartedAtUtc).TotalSeconds:F1} 秒 |");
        }
        builder.AppendLine();
    }

    /// <summary>将详细报告阶段排空三态转换为简短文本。</summary>
    /// <param name="phase">合并后的 HTTP 阶段。</param>
    /// <returns>未启用、通过或超时以及最长等待秒数。</returns>
    private static string FormatDrain(DetailedCommandPhaseSummary phase) => phase.DrainSatisfied switch
    {
        true => $"通过/{phase.MaximumDrainWaitMilliseconds / 1000d:F1}秒",
        false => $"超时/{phase.MaximumDrainWaitMilliseconds / 1000d:F1}秒",
        null => "未启用"
    };

    /// <summary>
    /// 输出客户端错误码降序表。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendErrorsMarkdown(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        builder.AppendLine("## 客户端错误分布").AppendLine();
        builder.AppendLine("| 错误码 | 次数 |");
        builder.AppendLine("| --- | ---: |");
        foreach (var error in analysis.ClientMetrics.Errors.OrderByDescending(item => item.Value))
            builder.AppendLine($"| `{Md(error.Key)}` | {error.Value:N0} |");
        builder.AppendLine();
    }

    /// <summary>
    /// 输出可选基线对比表格。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="comparisons">改造前后对比项。</param>
    private static void AppendComparisonsMarkdown(StringBuilder builder, IReadOnlyList<ReportComparisonItem> comparisons)
    {
        if (comparisons.Count == 0)
            return;
        builder.AppendLine("## 基线对比").AppendLine();
        builder.AppendLine("| 指标 | 基线 | 当前 | 变化 |");
        builder.AppendLine("| --- | ---: | ---: | --- |");
        foreach (var item in comparisons)
            builder.AppendLine($"| {Md(item.Name)} | {Md(item.Baseline)} | {Md(item.Current)} | {Md(item.Change)} |");
        builder.AppendLine();
    }

    /// <summary>
    /// 输出数据质量警告，提醒读者区分服务异常与采集缺失。
    /// </summary>
    /// <param name="builder">Markdown 构建器。</param>
    /// <param name="warnings">数据质量警告。</param>
    private static void AppendWarningsMarkdown(StringBuilder builder, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return;
        builder.AppendLine("## 数据质量说明").AppendLine();
        foreach (var warning in warnings)
            builder.AppendLine($"- {warning}");
        builder.AppendLine();
    }

    /// <summary>
    /// 根据失败项和最高优先级问题生成一段适合报告首页的结论。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <returns>不包含未经标注推断的简短结论。</returns>
    private static string BuildExecutiveConclusion(DetailedReportAnalysis analysis)
    {
        var failed = analysis.AcceptanceChecks
            .Where(item => item.Status != DetailedAcceptanceStatus.Passed)
            .Select(item => item.Name)
            .ToArray();
        if (failed.Length == 0)
            return $"{analysis.TargetDeviceCount:N0} 台目标下的全部可用验收项均满足门槛。";
        var names = string.Join("、", failed.Take(3));
        var remaining = failed.Length > 3 ? $"等 {failed.Length:N0} 项" : string.Empty;
        return $"未满足或无法评估：{names}{remaining}。";
    }

    /// <summary>
    /// 把 HTTP 阶段摘要写成可由 Excel 直接打开的 UTF-8 CSV 文本。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <returns>阶段 CSV。</returns>
    private static string BuildPhaseCsv(DetailedReportAnalysis analysis)
    {
        var builder = new StringBuilder("name,showResult,planned,started,succeeded,failed,timeouts,skippedOffline,estimatedEnqueued,rawDownstream,recognized,unsupported,commandsResponded,unobserved,duplicateFrames,rawDeliveryRate,recognitionRate,responseRate,durationSeconds\n");
        foreach (var phase in analysis.CommandPhases)
        {
            builder.Append(Csv(phase.Name)).Append(',').Append(phase.ShowResult).Append(',')
                .Append(phase.PlannedRequests).Append(',').Append(phase.StartedRequests).Append(',')
                .Append(phase.SucceededRequests).Append(',').Append(phase.FailedRequests).Append(',')
                .Append(phase.TimeoutRequests).Append(',').Append(phase.SkippedOfflineRequests).Append(',')
                .Append(phase.EstimatedEnqueued).Append(',').Append(phase.RawDownstreamCommands).Append(',')
                .Append(phase.RecognizedCommands).Append(',').Append(phase.UnsupportedCommands).Append(',')
                .Append(phase.CommandsResponded).Append(',').Append(phase.CommandsUnobserved).Append(',')
                .Append(phase.DuplicateDownstreamFrames).Append(',')
                .Append(phase.RawDeliveryRate.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(phase.RecognitionRate.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(phase.ResponseRate.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append((phase.EndedAtUtc - phase.StartedAtUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 转义 CSV 字段中的引号、逗号和换行。
    /// </summary>
    /// <param name="value">原始字段。</param>
    /// <returns>安全 CSV 字段。</returns>
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    /// <summary>
    /// 将验收状态转换为中文报告文本。
    /// </summary>
    /// <param name="status">三态验收状态。</param>
    /// <returns>中文状态。</returns>
    private static string FormatStatus(DetailedAcceptanceStatus status) => status switch
    {
        DetailedAcceptanceStatus.Passed => "通过",
        DetailedAcceptanceStatus.Failed => "失败",
        _ => "不可评估"
    };

    /// <summary>
    /// 将诊断置信度转换为中文，明确事实和推断的边界。
    /// </summary>
    /// <param name="confidence">诊断置信度。</param>
    /// <returns>中文置信度。</returns>
    private static string FormatConfidence(DiagnosticConfidence confidence) => confidence switch
    {
        DiagnosticConfidence.Fact => "指标直接证明",
        DiagnosticConfidence.HighProbability => "高概率推断",
        _ => "待验证假设"
    };

    /// <summary>
    /// 将字节数格式化为 MiB。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>保留两位小数的 MiB 文本。</returns>
    private static string Bytes(long bytes) => $"{bytes / 1024d / 1024d:F2} MiB";

    /// <summary>
    /// 转义 Markdown 表格中的竖线和换行。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>安全 Markdown 单元格。</returns>
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

}
