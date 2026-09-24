using System.Globalization;
using System.Net;
using System.Text;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 使用简洁的分层布局生成验收汇报和排查分析 HTML；验收版只保留结论与数据，问题分析仅出现在排查版。
/// </summary>
internal static class DetailedReportHtmlWriter
{
    /// <summary>
    /// 构建包含测试背景、交互式报表切换和受众专属内容的自包含 HTML5 文本。
    /// </summary>
    /// <param name="analysis">统一离线分析模型。</param>
    /// <param name="charts">已生成的 SVG 图表。</param>
    /// <param name="diagnostic">为 true 时生成含问题分析和排查明细的诊断版。</param>
    /// <returns>可直接离线打开的完整 HTML。</returns>
    internal static string Build(
        DetailedReportAnalysis analysis,
        DetailedReportCharts charts,
        bool diagnostic)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        builder.Append("<title>").Append(Html(analysis.Title)).AppendLine("</title>");
        builder.AppendLine(Styles);
        builder.AppendLine("</head><body><main class=\"report-shell\">");
        AppendTopBar(builder, analysis, diagnostic);
        AppendNavigation(builder, diagnostic);
        builder.AppendLine("<div class=\"report-content\">");
        AppendOverview(builder, analysis);
        AppendMetrics(builder, analysis);
        AppendReportViews(builder, analysis, charts, diagnostic);
        if (diagnostic)
        {
            AppendIssues(builder, analysis.Findings);
            AppendDiagnosticDetails(builder, analysis);
        }
        else
        {
            AppendAcceptanceSupportingData(builder, analysis);
        }

        AppendWarnings(builder, analysis.DataWarnings);
        AppendFinalDecision(builder, analysis, diagnostic);
        builder.AppendLine("</div></main>");
        builder.AppendLine(Script);
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// 输出产品化顶部栏和报告版本标识。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    /// <param name="diagnostic">是否为排查版。</param>
    private static void AppendTopBar(StringBuilder builder, DetailedReportAnalysis analysis, bool diagnostic)
    {
        builder.AppendLine("<header class=\"topbar\"><div class=\"brand\"><span class=\"brand-mark\" aria-hidden=\"true\">↗</span><span><strong>")
            .Append(Html(analysis.Background.ServiceName))
            .Append("</strong><small>负载测试报告</small></span></div><span class=\"edition\">")
            .Append(diagnostic ? "排查分析版" : "验收汇报版")
            .AppendLine("</span></header>");
    }

    /// <summary>
    /// 输出页面章节导航；验收版不出现问题分析入口。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="diagnostic">是否为排查版。</param>
    private static void AppendNavigation(StringBuilder builder, bool diagnostic)
    {
        builder.AppendLine("<nav class=\"section-nav\" aria-label=\"报告章节\"><button type=\"button\" aria-pressed=\"true\" data-section=\"overview\">结论与规则</button><button type=\"button\" aria-pressed=\"false\" data-section=\"metrics\">核心指标</button><button type=\"button\" aria-pressed=\"false\" data-section=\"views\">报表视图</button>");
        if (diagnostic)
            builder.AppendLine("<button type=\"button\" aria-pressed=\"false\" data-section=\"issues\">问题分析</button><button type=\"button\" aria-pressed=\"false\" data-section=\"details\">排查明细</button>");
        builder.AppendLine("</nav>");
    }

    /// <summary>
    /// 输出首页结论、测试背景、规则汇总和可折叠验收规则。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendOverview(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        var failed = analysis.AcceptanceChecks.Count(item => item.Status == DetailedAcceptanceStatus.Failed);
        var unavailable = analysis.AcceptanceChecks.Count(item => item.Status == DetailedAcceptanceStatus.Unavailable);
        var passed = analysis.AcceptanceChecks.Count - failed - unavailable;
        builder.AppendLine("<section class=\"report-section\" id=\"overview\"><p class=\"eyebrow\">Acceptance summary</p><h1>")
            .Append(Html(BuildHeroTitle(analysis, failed + unavailable)))
            .Append("</h1><p class=\"lead\">")
            .Append(Html(BuildExecutiveConclusion(analysis)))
            .AppendLine("</p>");
        AppendBackground(builder, analysis);
        builder.AppendLine("<div class=\"status-layout\"><div class=\"conclusion-surface\"><span class=\"status ")
            .Append(analysis.AcceptancePassed ? "status-pass" : "status-fail")
            .Append("\">")
            .Append(analysis.AcceptancePassed ? "验收通过" : "验收未通过")
            .Append("</span><strong>")
            .Append(analysis.AcceptancePassed ? "全部规则满足门槛" : $"{failed + unavailable:N0} 项规则未满足")
            .Append("</strong><p>")
            .Append(analysis.AcceptancePassed
                ? "容量、稳定性和性能指标均有数据证明通过。"
                : "报告明确区分失败与不可评估；缺失数据不会被计为通过。")
            .AppendLine("</p></div><dl class=\"rule-counts\">")
            .Append("<div><dt>通过</dt><dd>").Append(passed.ToString("N0", CultureInfo.InvariantCulture)).AppendLine("</dd></div>")
            .Append("<div><dt>失败</dt><dd>").Append(failed.ToString("N0", CultureInfo.InvariantCulture)).AppendLine("</dd></div>")
            .Append("<div><dt>不可评估</dt><dd>").Append(unavailable.ToString("N0", CultureInfo.InvariantCulture)).AppendLine("</dd></div></dl></div>");
        AppendRules(builder, analysis.AcceptanceChecks);
        builder.AppendLine("</section>");
    }

    /// <summary>
    /// 输出背景 JSON 与节点摘要合并后的服务器、代码、场景和时间信息。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendBackground(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        var background = analysis.Background;
        builder.AppendLine("<section class=\"background-surface\" aria-label=\"测试背景\"><div class=\"surface-head\"><strong>测试背景</strong><span>报告生成：")
            .Append(analysis.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
            .AppendLine("</span></div><dl class=\"background-grid\">");
        AppendBackgroundItem(builder, "测试场景", background.ScenarioName, background.ScenarioDescription);
        AppendBackgroundItem(builder, "运行标识", analysis.RunId, $"测试窗口：{analysis.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}–{analysis.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        AppendBackgroundItem(builder, "环境与人员", analysis.Environment, $"测试人员：{analysis.Tester}");
        AppendBackgroundItem(builder, "服务器与规格", background.ServerName, JoinMetadata(background.OperatingSystem, background.Cpu, background.Memory, background.Network));
        AppendBackgroundItem(builder, "服务版本", $"{background.ServiceName} · {background.ServiceVersion}", $"目标设备：{analysis.TargetDeviceCount:N0}，负载节点：{analysis.NodeCount:N0}");
        AppendBackgroundItem(builder, "代码版本", background.CodeBranch, $"Commit：{background.CodeCommit}");
        AppendBackgroundItem(builder, "关键运行参数", BuildParameterHeadline(background.ServiceParameters), BuildParameterDetails(background.ServiceParameters));
        AppendBackgroundItem(builder, "背景配置", Path.GetFileName(background.Source), background.Source);
        builder.AppendLine("</dl></section>");
    }

    /// <summary>
    /// 输出一个测试背景字段，次要说明允许展示“未填写”以保留审计缺口。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="value">主要值。</param>
    /// <param name="detail">补充说明。</param>
    private static void AppendBackgroundItem(StringBuilder builder, string name, string value, string detail)
    {
        builder.Append("<div class=\"background-item\"><dt>").Append(Html(name)).Append("</dt><dd class=\"")
            .Append(value == "未填写" ? "missing" : string.Empty)
            .Append("\">").Append(Html(value)).Append("<small>").Append(Html(detail)).AppendLine("</small></dd></div>");
    }

    /// <summary>
    /// 输出未通过规则和折叠的已通过规则，减少验收首页的重复视觉噪声。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="checks">全部三态验收项。</param>
    private static void AppendRules(StringBuilder builder, IReadOnlyList<DetailedAcceptanceCheck> checks)
    {
        var attention = checks.Where(item => item.Status != DetailedAcceptanceStatus.Passed).ToArray();
        var passed = checks.Where(item => item.Status == DetailedAcceptanceStatus.Passed).ToArray();
        builder.AppendLine("<div class=\"rules-surface\" aria-label=\"验收规则\">");
        foreach (var check in attention)
            AppendRuleRow(builder, check);
        if (passed.Length > 0)
        {
            builder.Append("<details><summary>查看 ").Append(passed.Length.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" 项已通过规则</summary>");
            foreach (var check in passed)
                AppendRuleRow(builder, check);
            builder.AppendLine("</details>");
        }
        builder.AppendLine("</div>");
    }

    /// <summary>
    /// 输出一条验收规则的名称、门槛、实际值和状态。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="check">单项验收结果。</param>
    private static void AppendRuleRow(StringBuilder builder, DetailedAcceptanceCheck check)
    {
        var css = check.Status switch
        {
            DetailedAcceptanceStatus.Passed => "result-pass",
            DetailedAcceptanceStatus.Failed => "result-fail",
            _ => "result-unavailable"
        };
        builder.Append("<div class=\"rule-row\"><div><strong>").Append(Html(check.Name)).Append("</strong><small>")
            .Append(Html(check.Expected)).Append(" · ").Append(Html(check.Evidence)).Append("</small></div><span class=\"rule-value\">")
            .Append(Html(check.Actual)).Append("</span><span class=\"rule-result ").Append(css).Append("\">")
            .Append(Html(FormatStatus(check.Status))).AppendLine("</span></div>");
    }

    /// <summary>
    /// 输出六项高价值指标，避免把所有原始计数平铺成难以汇报的仪表盘。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendMetrics(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        var metrics = analysis.ClientMetrics;
        var protocolPeak = analysis.ClientTimeline.DefaultIfEmpty().Max(item => item?.ProtocolOnline ?? 0);
        var httpRate = metrics.ApiRequestsStarted == 0 ? (double?)null : metrics.ApiRequestsSucceeded * 100d / metrics.ApiRequestsStarted;
        builder.AppendLine("<section class=\"report-section\" id=\"metrics\"><p class=\"eyebrow\">Key metrics</p><h2>核心指标</h2><div class=\"metric-surface\">");
        AppendMetric(builder, "协议在线峰值", protocolPeak.ToString("N0", CultureInfo.InvariantCulture), $"目标 {analysis.TargetDeviceCount:N0}", protocolPeak >= analysis.TargetDeviceCount);
        AppendMetric(builder, "业务在线峰值", analysis.Server?.MaximumBusinessOnlineDevices.ToString("N0", CultureInfo.InvariantCulture) ?? "不可评估", $"目标 {analysis.TargetDeviceCount:N0}", analysis.Server?.MaximumBusinessOnlineDevices >= analysis.TargetDeviceCount);
        AppendMetric(builder, "业务追平耗时", analysis.Server?.BusinessCatchupSeconds is { } catchup ? $"{catchup:F1} 秒" : "未追平", "物理连接达到目标后", analysis.AcceptanceChecks.FirstOrDefault(item => item.Name == "业务在线追平时间")?.Status == DetailedAcceptanceStatus.Passed);
        AppendMetric(builder, "队列年龄峰值", analysis.Server is { } server ? $"{server.MaximumQueueAgeMilliseconds / 1000d:F1} 秒" : "不可评估", "Pipeline 最大等待", analysis.AcceptanceChecks.FirstOrDefault(item => item.Name == "业务管线最大排队年龄")?.Status == DetailedAcceptanceStatus.Passed);
        AppendMetric(builder, "ACK 最大耗时", analysis.Server is { } ackServer ? $"{ackServer.MaximumAckElapsedMilliseconds:N0} ms" : "不可评估", "组帧至 Socket Send 返回", analysis.AcceptanceChecks.FirstOrDefault(item => item.Name == "ACK 最大耗时")?.Status == DetailedAcceptanceStatus.Passed);
        AppendMetric(builder, "HTTP 成功率", httpRate.HasValue ? $"{httpRate:F2}%" : "无请求", $"{metrics.ApiRequestsSucceeded:N0} / {metrics.ApiRequestsStarted:N0}", httpRate is null or >= 99d);
        var rawCommands = analysis.CommandPhases.Sum(item => item.RawDownstreamCommands);
        var recognizedCommands = analysis.CommandPhases.Sum(item => item.RecognizedCommands);
        var estimatedCommands = analysis.CommandPhases.Sum(item => item.EstimatedEnqueued);
        AppendMetric(
            builder,
            "原始下行交付率",
            estimatedCommands > 0
                ? $"{Rate(rawCommands, estimatedCommands) * 100:F2}%"
                : "无命令",
            $"{rawCommands:N0} 条原始下行",
            estimatedCommands == 0 || Rate(rawCommands, estimatedCommands) >= 0.95);
        AppendMetric(
            builder,
            "协议识别率",
            rawCommands > 0 ? $"{Rate(recognizedCommands, rawCommands) * 100:F2}%" : "无下行",
            $"{recognizedCommands:N0} 条已识别",
            rawCommands == 0 || Rate(recognizedCommands, rawCommands) >= 0.95);
        AppendMetric(builder, "最严重恢复耗时", FormatRecovery(metrics), "按完整中断事件取最大值", (metrics.OutageCount == 0 || metrics.WorstRecoveryMilliseconds.HasValue) && !metrics.OutageActive);
        var secondaryOutages = metrics.Outages.Count > 0
            ? metrics.Outages.Count(item => !item.ExpectedServerRestart)
            : analysis.ExpectedServerRestart
                ? Math.Max(0, metrics.OutageCount - 1)
                : metrics.OutageCount;
        AppendMetric(builder, "非预期二次掉线", secondaryOutages.ToString("N0", CultureInfo.InvariantCulture), $"总中断 {metrics.OutageCount:N0}", secondaryOutages == 0);
        AppendMetric(builder, "ACK 发送完成峰值", analysis.Server is { } completedServer ? $"{completedServer.MaximumAckSendCompletedElapsedMilliseconds:N0} ms" : "不可评估", "Socket Send 完成回调", analysis.AcceptanceChecks.FirstOrDefault(item => item.Name == "ACK 发送完成最大耗时")?.Status == DetailedAcceptanceStatus.Passed);
        builder.AppendLine("</div></section>");
    }

    /// <summary>
    /// 输出一个核心指标单元；状态色只提示是否满足当前规则，不替代文字值。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="name">指标名称。</param>
    /// <param name="value">主要数值。</param>
    /// <param name="context">口径或分母说明。</param>
    /// <param name="passed">是否满足当前门槛。</param>
    private static void AppendMetric(StringBuilder builder, string name, string value, string context, bool passed)
    {
        builder.Append("<div class=\"metric-item\"><span>").Append(Html(name)).Append("</span><strong>")
            .Append(Html(value)).Append("</strong><small class=\"").Append(passed ? string.Empty : "negative")
            .Append("\">").Append(Html(context)).AppendLine("</small></div>");
    }

    /// <summary>
    /// 输出可切换的在线、登录积压、指令链路和诊断图表，首屏只显示在线趋势。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    /// <param name="charts">SVG 图表集合。</param>
    /// <param name="diagnostic">是否增加连接错误和 HTTP 阶段视图。</param>
    private static void AppendReportViews(
        StringBuilder builder,
        DetailedReportAnalysis analysis,
        DetailedReportCharts charts,
        bool diagnostic)
    {
        builder.AppendLine("<section class=\"report-section\" id=\"views\"><p class=\"eyebrow\">Report views</p><h2>报表视图</h2><div class=\"chart-surface\"><div class=\"surface-head chart-head\"><strong>关键趋势</strong><div class=\"chart-switch\" role=\"group\" aria-label=\"切换报表\">");
        AppendChartButton(builder, "chart-online", "在线趋势", true);
        AppendChartButton(builder, "chart-business", "业务追平", false);
        if (analysis.ClientMetrics.ApiRequestsStarted > 0 || analysis.ClientMetrics.GatherReadingCommandsReceived > 0)
            AppendChartButton(builder, "chart-command", "指令链路", false);
        if (diagnostic)
        {
            AppendChartButton(builder, "chart-errors", "连接错误", false);
            AppendChartButton(builder, "chart-api", "HTTP 阶段", false);
        }
        builder.AppendLine("</div></div>");
        AppendChartPanel(builder, "chart-online", charts.OnlineTimeline, false);
        AppendChartPanel(builder, "chart-business", charts.BusinessOnlineGap, true);
        if (analysis.ClientMetrics.ApiRequestsStarted > 0 || analysis.ClientMetrics.GatherReadingCommandsReceived > 0)
            AppendChartPanel(builder, "chart-command", charts.CommandFunnel, true);
        if (diagnostic)
        {
            AppendChartPanel(builder, "chart-errors", charts.ConnectionErrors, true);
            AppendChartPanel(builder, "chart-api", charts.ApiPhases, true);
        }
        builder.AppendLine("</div></section>");
    }

    /// <summary>
    /// 输出一个原生报表切换按钮。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="target">目标图表面板 ID。</param>
    /// <param name="label">按钮文本。</param>
    /// <param name="selected">是否为初始选中视图。</param>
    private static void AppendChartButton(StringBuilder builder, string target, string label, bool selected) =>
        builder.Append("<button type=\"button\" aria-pressed=\"").Append(selected ? "true" : "false")
            .Append("\" data-chart=\"").Append(target).Append("\">").Append(Html(label)).AppendLine("</button>");

    /// <summary>
    /// 输出一个图表面板，非首屏图表使用 hidden 由本地脚本切换。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="id">面板 ID。</param>
    /// <param name="svg">完整 SVG。</param>
    /// <param name="hidden">是否初始隐藏。</param>
    private static void AppendChartPanel(StringBuilder builder, string id, string svg, bool hidden)
    {
        builder.Append("<div class=\"chart-panel\" id=\"").Append(id).Append('"');
        if (hidden)
            builder.Append(" hidden");
        builder.Append('>').Append(svg).AppendLine("</div>");
    }

    /// <summary>
    /// 仅在排查版输出完整问题证据、影响、建议和置信度。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="findings">自动诊断问题。</param>
    private static void AppendIssues(StringBuilder builder, IReadOnlyList<DiagnosticFinding> findings)
    {
        builder.AppendLine("<section class=\"report-section\" id=\"issues\"><p class=\"eyebrow\">Issue analysis</p><h2>问题分析</h2><div class=\"issues-surface\">");
        if (findings.Count == 0)
            builder.AppendLine("<div class=\"empty\">未触发自动诊断规则。</div>");
        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            builder.Append("<article class=\"issue\"><span class=\"issue-index\">").Append((index + 1).ToString("00", CultureInfo.InvariantCulture))
                .Append("</span><div><h3>[").Append(finding.Severity).Append("] ").Append(Html(finding.Title))
                .Append("</h3><p><b>证据</b>").Append(Html(finding.Evidence)).Append("</p><p><b>影响</b>")
                .Append(Html(finding.Impact)).Append("</p><p><b>建议</b>").Append(Html(finding.Recommendation))
                .Append("</p><small>置信度：").Append(Html(FormatConfidence(finding.Confidence))).AppendLine("</small></div></article>");
        }
        builder.AppendLine("</div></section>");
    }

    /// <summary>
    /// 输出排查版的完整原始指标、HTTP 阶段、基线对比和错误分布表格。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendDiagnosticDetails(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        builder.AppendLine("<section class=\"report-section\" id=\"details\"><p class=\"eyebrow\">Diagnostic details</p><h2>排查明细</h2>");
        AppendFullMetricsTable(builder, analysis);
        if (analysis.CommandPhases.Count > 0)
            AppendCommandPhasesTable(builder, analysis.CommandPhases);
        if (analysis.ClientMetrics.Errors.Count > 0)
            AppendErrorsTable(builder, analysis);
        if (analysis.Comparisons.Count > 0)
            AppendComparisonTable(builder, analysis.Comparisons);
        builder.AppendLine("</section>");
    }

    /// <summary>
    /// 验收版仅输出基线对比等数据支撑，不输出问题、建议和排查措辞。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendAcceptanceSupportingData(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        if (analysis.Comparisons.Count == 0)
            return;
        builder.AppendLine("<section class=\"report-section\"><p class=\"eyebrow\">Baseline</p><h2>基线对比</h2>");
        AppendComparisonTable(builder, analysis.Comparisons, includeHeading: false);
        builder.AppendLine("</section>");
    }

    /// <summary>
    /// 输出全部连接、登录、HTTP、负载机和服务端关键指标表。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendFullMetricsTable(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        var metrics = analysis.ClientMetrics;
        builder.AppendLine("<h3 class=\"table-title\">完整指标</h3><div class=\"table-wrap\"><table><thead><tr><th>指标</th><th>数值</th></tr></thead><tbody>");
        AppendTableRow(builder, "连接尝试 / 成功 / 失败", $"{metrics.ConnectionAttempts:N0} / {metrics.ConnectionSucceeded:N0} / {metrics.ConnectionFailed:N0}");
        AppendTableRow(builder, "登录尝试 / 成功 / 失败", $"{metrics.LoginAttempts:N0} / {metrics.LoginSucceeded:N0} / {metrics.LoginFailed:N0}");
        AppendTableRow(builder, "心跳发送 / 确认 / 超时", $"{metrics.HeartbeatsSent:N0} / {metrics.HeartbeatsConfirmed:N0} / {metrics.HeartbeatTimeouts:N0}");
        AppendTableRow(builder, "重连 / 掉线事件 / 最严重恢复", $"{metrics.ReconnectsScheduled:N0} / {metrics.OutageCount:N0} / {FormatRecovery(metrics)}");
        AppendTableRow(builder, "HTTP 发起 / 成功 / 失败 / 超时", $"{metrics.ApiRequestsStarted:N0} / {metrics.ApiRequestsSucceeded:N0} / {metrics.ApiRequestsFailed:N0} / {metrics.ApiRequestTimeouts:N0}");
        AppendTableRow(builder, "设备命令收到 / 应答（旧累计口径）", $"{metrics.GatherReadingCommandsReceived:N0} / {metrics.GatherReadingCommandsResponded:N0}");
        if (analysis.CommandPhases.Count > 0)
        {
            AppendTableRow(
                builder,
                "原始下行 / 协议识别 / 不支持 / 应答 / 重复帧",
                $"{analysis.CommandPhases.Sum(item => item.RawDownstreamCommands):N0} / " +
                $"{analysis.CommandPhases.Sum(item => item.RecognizedCommands):N0} / " +
                $"{analysis.CommandPhases.Sum(item => item.UnsupportedCommands):N0} / " +
                $"{analysis.CommandPhases.Sum(item => item.CommandsResponded):N0} / " +
                $"{analysis.CommandPhases.Sum(item => item.DuplicateDownstreamFrames):N0}");
        }
        AppendTableRow(builder, "负载机 CPU / 工作集峰值", $"{analysis.LoadGeneratorResources.MaximumCpuPercent:F2}% / {Bytes(analysis.LoadGeneratorResources.MaximumWorkingSetBytes)}");
        AppendTableRow(builder, "负载机进程线程 / 句柄峰值", $"{analysis.LoadGeneratorResources.MaximumProcessThreadCount:N0} / {analysis.LoadGeneratorResources.MaximumProcessHandleCount:N0}");
        if (analysis.Server is { } server)
        {
            AppendTableRow(builder, "物理 / 绑定 / 业务在线峰值", $"{server.MaximumSocketConnections:N0} / {server.MaximumBoundDeviceConnections:N0} / {server.MaximumBusinessOnlineDevices:N0}");
            AppendTableRow(builder, "Pipeline 队列深度 / 年龄峰值", $"{server.MaximumQueueDepth:N0} / {server.MaximumQueueAgeMilliseconds / 1000d:F1} 秒");
            AppendTableRow(builder, "Pipeline 失败 / 拒绝 / ACK 后拒绝", $"{server.PipelineFailed:N0} / {server.PipelineRejected:N0} / {server.AckedButRejected:N0}");
            AppendTableRow(builder, "ACK 组帧发送 / 发送完成峰值", $"{server.MaximumAckElapsedMilliseconds:N0} / {server.MaximumAckSendCompletedElapsedMilliseconds:N0} ms");
            AppendTableRow(builder, "待完成 ACK / 失败 / 中止", $"{server.MaximumPendingConfirmSend:N0} / {server.AckFailed:N0} / {server.AckSendAborted:N0}");
            AppendTableRow(builder, "登录业务就绪最大耗时", $"{server.MaximumLoginBusinessReadyElapsedMilliseconds / 1000d:F1} 秒");
            AppendTableRow(builder, "待建命令线路 / 命令管线深度峰值", $"{server.MaximumPendingCommandLine:N0} / {server.MaximumCommandLineQueueDepth:N0}");
            AppendTableRow(builder, "命令线路压力保护次数 / 最长暂停", $"{server.CommandLineStartPauseCount:N0} / {server.MaximumCommandLineStartPauseElapsedMilliseconds / 1000d:F1} 秒");
            AppendTableRow(builder, "连接关闭 / 错误 / 接收 / 其他", $"{server.ConnectionClosed:N0} / {server.ConnectionCloseWithError:N0} / {server.ConnectionCloseReceive:N0} / {server.ConnectionCloseOther:N0}");
        }
        if (analysis.CooldownServer is { } cooldown)
        {
            AppendTableRow(
                builder,
                "冷却窗口连接关闭 / ACK 中止 / 命令暂停",
                $"{cooldown.ConnectionClosed:N0} / {cooldown.AckSendAborted:N0} / {cooldown.CommandLineStartPauseCount:N0}（不参与验收）");
        }
        builder.AppendLine("</tbody></table></div>");
    }

    /// <summary>
    /// 输出 HTTP 指令阶段表格。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="phases">合并后的阶段摘要。</param>
    private static void AppendCommandPhasesTable(StringBuilder builder, IReadOnlyList<DetailedCommandPhaseSummary> phases)
    {
        builder.AppendLine("<h3 class=\"table-title\">HTTP 阶段明细</h3><div class=\"table-wrap\"><table><thead><tr><th>阶段</th><th>同步结果</th><th>计划 / 发起</th><th>成功 / 失败 / 超时</th><th>预校验 / 估算入队</th><th>原始下行 / 识别 / 不支持 / 应答 / 未观测</th><th>原始交付率 / 识别率 / 应答率</th><th>重复帧</th><th>排空</th><th>P95</th><th>熔断</th><th>耗时</th></tr></thead><tbody>");
        foreach (var phase in phases)
        {
            builder.Append("<tr><td>").Append(Html(phase.Name)).Append("</td><td>").Append(phase.ShowResult ? "是" : "否")
                .Append("</td><td>").Append($"{phase.PlannedRequests:N0} / {phase.StartedRequests:N0}")
                .Append("</td><td>").Append($"{phase.SucceededRequests:N0} / {phase.FailedRequests:N0} / {phase.TimeoutRequests:N0}")
                .Append("</td><td>").Append($"{phase.PrevalidationRejected:N0} / {phase.EstimatedEnqueued:N0}")
                .Append("</td><td>").Append($"{phase.RawDownstreamCommands:N0} / {phase.RecognizedCommands:N0} / {phase.UnsupportedCommands:N0} / {phase.CommandsResponded:N0} / {phase.CommandsUnobserved:N0}")
                .Append("</td><td>").Append($"{phase.RawDeliveryRate:P2} / {phase.RecognitionRate:P2} / {phase.ResponseRate:P2}")
                .Append("</td><td>").Append($"{phase.DuplicateDownstreamFrames:N0}")
                .Append("</td><td>").Append(FormatDrain(phase))
                .Append("</td><td>").Append($"{phase.P95Milliseconds:N0} ms")
                .Append("</td><td>").Append(phase.CircuitBreakerTripped ? "是" : "否")
                .Append("</td><td>").Append((phase.EndedAtUtc - phase.StartedAtUtc).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" 秒</td></tr>");
        }
        builder.AppendLine("</tbody></table></div>");
    }

    /// <summary>将详细 HTML 阶段排空三态转换为简短文本。</summary>
    /// <param name="phase">合并后的 HTTP 阶段。</param>
    /// <returns>未启用、通过或超时以及最长等待秒数。</returns>
    private static string FormatDrain(DetailedCommandPhaseSummary phase) => phase.DrainSatisfied switch
    {
        true => $"通过 / {phase.MaximumDrainWaitMilliseconds / 1000d:F1} 秒",
        false => $"超时 / {phase.MaximumDrainWaitMilliseconds / 1000d:F1} 秒",
        null => "未启用"
    };

    /// <summary>安全计算计数比例，分母不大于零时返回零。</summary>
    /// <param name="numerator">分子计数。</param>
    /// <param name="denominator">分母计数。</param>
    /// <returns>双精度比例。</returns>
    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    /// <summary>区分没有掉线事件和发生掉线但尚未恢复。</summary>
    /// <param name="metrics">客户端最终指标。</param>
    /// <returns>无掉线、未恢复或最严重恢复耗时。</returns>
    private static string FormatRecovery(MetricSnapshot metrics)
    {
        if (metrics.OutageCount == 0 && !metrics.OutageActive)
            return "无掉线";
        return metrics.WorstRecoveryMilliseconds.HasValue
            ? $"{metrics.WorstRecoveryMilliseconds.Value / 1000d:F1} 秒"
            : "未恢复";
    }

    /// <summary>
    /// 输出客户端错误码次数表格。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    private static void AppendErrorsTable(StringBuilder builder, DetailedReportAnalysis analysis)
    {
        builder.AppendLine("<h3 class=\"table-title\">客户端错误分布</h3><div class=\"table-wrap\"><table><thead><tr><th>错误码</th><th>次数</th></tr></thead><tbody>");
        foreach (var error in analysis.ClientMetrics.Errors.OrderByDescending(item => item.Value))
            AppendTableRow(builder, error.Key, error.Value.ToString("N0", CultureInfo.InvariantCulture));
        builder.AppendLine("</tbody></table></div>");
    }

    /// <summary>
    /// 输出改造前后基线对比表格。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="items">比较项。</param>
    /// <param name="includeHeading">是否输出三级标题。</param>
    private static void AppendComparisonTable(
        StringBuilder builder,
        IReadOnlyList<ReportComparisonItem> items,
        bool includeHeading = true)
    {
        if (includeHeading)
            builder.AppendLine("<h3 class=\"table-title\">基线对比</h3>");
        builder.AppendLine("<div class=\"table-wrap\"><table><thead><tr><th>指标</th><th>基线</th><th>当前</th><th>变化</th></tr></thead><tbody>");
        foreach (var item in items)
            builder.Append("<tr><td>").Append(Html(item.Name)).Append("</td><td>").Append(Html(item.Baseline)).Append("</td><td>").Append(Html(item.Current)).Append("</td><td>").Append(Html(item.Change)).AppendLine("</td></tr>");
        builder.AppendLine("</tbody></table></div>");
    }

    /// <summary>
    /// 输出两列表格的一行并对外部文本进行编码。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="name">指标或错误名称。</param>
    /// <param name="value">对应值。</param>
    private static void AppendTableRow(StringBuilder builder, string name, string value) =>
        builder.Append("<tr><td>").Append(Html(name)).Append("</td><td>").Append(Html(value)).AppendLine("</td></tr>");

    /// <summary>
    /// 输出数据质量警告；该区域陈述采集事实，不在验收版引入问题诊断建议。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="warnings">数据质量警告。</param>
    private static void AppendWarnings(StringBuilder builder, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return;
        builder.AppendLine("<section class=\"report-section\"><p class=\"eyebrow\">Data quality</p><h2>数据质量说明</h2><div class=\"warnings\">");
        foreach (var warning in warnings)
            builder.Append("<p>").Append(Html(warning)).AppendLine("</p>");
        builder.AppendLine("</div></section>");
    }

    /// <summary>
    /// 输出报告结尾结论；验收版不附带问题分析或排查建议。
    /// </summary>
    /// <param name="builder">HTML 构建器。</param>
    /// <param name="analysis">分析模型。</param>
    /// <param name="diagnostic">是否为排查版。</param>
    private static void AppendFinalDecision(StringBuilder builder, DetailedReportAnalysis analysis, bool diagnostic)
    {
        builder.Append("<footer class=\"final-decision\"><strong>")
            .Append(diagnostic ? "分析结论：" : "验收结论：")
            .Append("</strong>").Append(Html(BuildExecutiveConclusion(analysis))).AppendLine("</footer>");
    }

    /// <summary>
    /// 根据容量规则和未通过项数量生成首页大标题。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <param name="attentionCount">失败与不可评估项总数。</param>
    /// <returns>不包含诊断推断的容量和验收结论。</returns>
    private static string BuildHeroTitle(DetailedReportAnalysis analysis, int attentionCount)
    {
        if (analysis.AcceptancePassed)
            return $"{analysis.TargetDeviceCount:N0} 台目标下，全部验收规则满足门槛。";
        var capacityPassed = analysis.AcceptanceChecks
            .Where(item => item.Name.Contains("容量", StringComparison.Ordinal))
            .All(item => item.Status == DetailedAcceptanceStatus.Passed);
        return capacityPassed
            ? $"{analysis.TargetDeviceCount:N0} 台连接达标，{attentionCount:N0} 项规则未达标。"
            : $"{analysis.TargetDeviceCount:N0} 台目标未完成，{attentionCount:N0} 项规则需要关注。";
    }

    /// <summary>
    /// 根据失败或不可评估项生成简短、可审计的执行结论。
    /// </summary>
    /// <param name="analysis">分析模型。</param>
    /// <returns>最多列举三个项目并保留总数的结论。</returns>
    private static string BuildExecutiveConclusion(DetailedReportAnalysis analysis)
    {
        var attention = analysis.AcceptanceChecks
            .Where(item => item.Status != DetailedAcceptanceStatus.Passed)
            .Select(item => item.Name)
            .ToArray();
        if (attention.Length == 0)
            return $"{analysis.TargetDeviceCount:N0} 台目标下的全部可用验收项均满足门槛。";
        var names = string.Join("、", attention.Take(3));
        var remaining = attention.Length > 3 ? $"等 {attention.Length:N0} 项" : string.Empty;
        return $"未满足或无法评估：{names}{remaining}。";
    }

    /// <summary>
    /// 合并多个服务器规格字段，所有字段未填写时仍保留一个明确占位值。
    /// </summary>
    /// <param name="values">服务器系统、CPU、内存和网络等候选值。</param>
    /// <returns>使用中点分隔的非空规格文本。</returns>
    private static string JoinMetadata(params string[] values)
    {
        var available = values.Where(item => item != "未填写").ToArray();
        return available.Length == 0 ? "未填写" : string.Join(" · ", available);
    }

    /// <summary>
    /// 为关键参数背景项生成主文本，优先显示参数数量和前两项名称。
    /// </summary>
    /// <param name="parameters">背景 JSON 中的关键服务参数。</param>
    /// <returns>参数摘要。</returns>
    private static string BuildParameterHeadline(IReadOnlyList<DetailedReportParameter> parameters) =>
        parameters.Count == 0
            ? "未填写"
            : $"{parameters.Count:N0} 项 · {string.Join("、", parameters.Take(2).Select(item => item.Name))}";

    /// <summary>
    /// 为关键参数背景项生成全部名值明细。
    /// </summary>
    /// <param name="parameters">背景 JSON 中的关键服务参数。</param>
    /// <returns>分号分隔的参数名值文本。</returns>
    private static string BuildParameterDetails(IReadOnlyList<DetailedReportParameter> parameters) =>
        parameters.Count == 0
            ? "请通过 --background 导入与本轮结论相关的服务参数"
            : string.Join("；", parameters.Select(item => $"{item.Name}：{item.Value}"));

    /// <summary>
    /// 将验收状态转换为中文展示文本。
    /// </summary>
    /// <param name="status">三态验收状态。</param>
    /// <returns>通过、失败或不可评估。</returns>
    private static string FormatStatus(DetailedAcceptanceStatus status) => status switch
    {
        DetailedAcceptanceStatus.Passed => "通过",
        DetailedAcceptanceStatus.Failed => "失败",
        _ => "不可评估"
    };

    /// <summary>
    /// 将诊断置信度转换为中文证据等级。
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
    /// 对来自场景、背景 JSON 和指标文件的所有文本执行 HTML 编码。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>安全 HTML 文本。</returns>
    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private const string Styles = """
        <style>
        :root{color-scheme:light;font-family:-apple-system,BlinkMacSystemFont,"SF Pro Display","Segoe UI","Microsoft YaHei",sans-serif;color:#1d1d1f;background:#f5f5f7}*{box-sizing:border-box}body{margin:0;background:#f5f5f7}.report-shell{max-width:1180px;margin:26px auto 60px;overflow:hidden;background:#f5f5f7;border:1px solid #e5e5e7;border-radius:24px;box-shadow:0 18px 55px rgba(0,0,0,.09)}.topbar{display:flex;align-items:center;justify-content:space-between;gap:20px;padding:20px 28px;background:rgba(255,255,255,.92);border-bottom:1px solid #e5e5e7}.brand{display:flex;align-items:center;gap:12px;min-width:0}.brand-mark{display:grid;place-items:center;width:34px;height:34px;flex:0 0 auto;color:#fff;background:#1d1d1f;border-radius:10px}.brand strong,.brand small{display:block}.brand strong{font-weight:500}.brand small{margin-top:2px;color:#6e6e73;font-size:12px}.edition{padding:7px 11px;color:#3a3a3c;background:#ededf0;border-radius:999px;font-size:12px;font-weight:500}.section-nav{display:flex;gap:4px;padding:10px 20px;overflow-x:auto;background:rgba(255,255,255,.74);border-bottom:1px solid #e5e5e7}.section-nav button,.chart-switch button{border:0;cursor:pointer;color:#6e6e73;background:transparent;border-radius:9px;white-space:nowrap}.section-nav button{padding:8px 12px}.section-nav button:hover,.section-nav button[aria-pressed=true]{color:#1d1d1f;background:#fff}.report-content{padding:40px 28px 52px}.report-section{margin:0 0 54px;scroll-margin-top:16px}.eyebrow{margin:0 0 8px;color:#6e6e73;font-size:12px;font-weight:500;letter-spacing:.09em;text-transform:uppercase}h1,h2,h3,p{margin-top:0}h1{max-width:820px;margin-bottom:16px;font-size:clamp(32px,5vw,54px);font-weight:500;letter-spacing:-.045em;line-height:1.08}h2{margin-bottom:22px;font-size:25px;font-weight:500;letter-spacing:-.025em}h3{font-size:16px;font-weight:500}.lead{max-width:790px;margin-bottom:0;color:#6e6e73;line-height:1.65}.background-surface,.conclusion-surface,.rule-counts,.rules-surface,.metric-surface,.chart-surface,.issues-surface,.table-wrap{background:#fff;border:1px solid #e5e5e7;border-radius:20px}.background-surface{margin-top:30px;overflow:hidden}.surface-head{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:17px 20px;border-bottom:1px solid #ededf0}.surface-head strong{font-weight:500}.surface-head span{color:#6e6e73;font-size:12px}.background-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));margin:0}.background-item{min-width:0;padding:17px 20px;border-right:1px solid #ededf0;border-bottom:1px solid #ededf0}.background-item:nth-child(2n){border-right:0}.background-item:nth-last-child(-n+2){border-bottom:0}.background-item dt{margin-bottom:6px;color:#6e6e73;font-size:12px}.background-item dd{margin:0;overflow-wrap:anywhere;font-size:14px;font-weight:500}.background-item small{display:block;margin-top:5px;color:#6e6e73;font-weight:400;line-height:1.5}.background-item .missing{color:#a15c00}.status-layout{display:grid;grid-template-columns:minmax(0,1.35fr) minmax(240px,.65fr);gap:18px;margin-top:20px}.conclusion-surface{padding:26px}.status{display:inline-flex;align-items:center;gap:7px;margin-bottom:18px;font-size:13px;font-weight:500}.status:before{content:"";width:8px;height:8px;background:currentColor;border-radius:50%}.status-pass,.result-pass{color:#18794e}.status-fail,.result-fail{color:#b42318}.result-unavailable{color:#a15c00}.conclusion-surface>strong{display:block;margin-bottom:12px;font-size:27px;font-weight:500;letter-spacing:-.03em}.conclusion-surface p{margin-bottom:0;color:#515154;line-height:1.55}.rule-counts{margin:0;padding:18px 22px}.rule-counts div{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:10px 0;border-bottom:1px solid #ededf0}.rule-counts div:last-child{border-bottom:0}.rule-counts dt{color:#6e6e73;font-size:13px}.rule-counts dd{margin:0;font-weight:500;font-variant-numeric:tabular-nums}.rules-surface{margin-top:18px;overflow:hidden}.rule-row{display:grid;grid-template-columns:minmax(230px,1.25fr) minmax(110px,.65fr) minmax(90px,.4fr);gap:16px;align-items:center;padding:16px 20px;border-bottom:1px solid #ededf0}.rule-row:last-child{border-bottom:0}.rule-row strong,.rule-row small{display:block}.rule-row strong{font-weight:500}.rule-row small{margin-top:4px;color:#6e6e73;font-size:12px;line-height:1.45}.rule-value{font-variant-numeric:tabular-nums}.rule-result{justify-self:end;font-size:13px;font-weight:500}details{border-top:1px solid #ededf0}summary{cursor:pointer;padding:16px 20px;color:#06c;font-weight:500}.metric-surface{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));overflow:hidden}.metric-item{min-height:130px;padding:22px;border-right:1px solid #ededf0;border-bottom:1px solid #ededf0}.metric-item:nth-child(3n){border-right:0}.metric-item:nth-last-child(-n+3){border-bottom:0}.metric-item span{display:block;margin-bottom:18px;color:#6e6e73;font-size:12px}.metric-item strong{display:block;font-size:25px;font-weight:500;letter-spacing:-.03em;font-variant-numeric:tabular-nums}.metric-item small{display:block;margin-top:7px;color:#6e6e73}.metric-item small.negative{color:#b42318}.chart-surface{overflow:hidden}.chart-head{align-items:center}.chart-switch{display:inline-flex;gap:2px;padding:3px;background:#ededf0;border-radius:10px;overflow-x:auto}.chart-switch button{padding:7px 10px;font-size:12px}.chart-switch button[aria-pressed=true]{color:#1d1d1f;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.12)}.chart-panel{padding:16px}.chart-panel[hidden]{display:none}.chart-panel svg{display:block;width:100%;height:auto}.issues-surface{overflow:hidden}.issue{display:grid;grid-template-columns:44px minmax(0,1fr);gap:16px;padding:22px;border-bottom:1px solid #ededf0}.issue:last-child{border-bottom:0}.issue-index{display:grid;place-items:center;width:34px;height:34px;color:#b42318;background:#fff1f0;border-radius:50%;font-size:12px;font-weight:500}.issue h3{margin-bottom:10px}.issue p{margin-bottom:8px;color:#515154;line-height:1.55}.issue p b{display:inline-block;min-width:48px;color:#6e6e73;font-size:12px;font-weight:500}.issue small{color:#6e6e73}.empty{padding:24px;color:#6e6e73}.table-title{margin:28px 0 12px}.table-wrap{overflow:auto;border-radius:16px}table{width:100%;border-collapse:collapse}th,td{padding:11px 14px;border-bottom:1px solid #ededf0;text-align:left;font-size:13px}th{color:#6e6e73;background:#fbfbfd;font-weight:500}tr:last-child td{border-bottom:0}.warnings{padding:4px 18px;background:#fff;border:1px solid #e5e5e7;border-radius:16px}.warnings p{margin:0;padding:14px 0;color:#6e6e73;border-bottom:1px solid #ededf0}.warnings p:last-child{border-bottom:0}.final-decision{padding:19px 21px;color:#515154;background:#e9e9ed;border-radius:16px;line-height:1.55}.final-decision strong{color:#1d1d1f;font-weight:500}@media(max-width:700px){.report-shell{margin:0;border:0;border-radius:0}.topbar{padding:16px 18px}.report-content{padding:32px 18px 42px}.status-layout{grid-template-columns:1fr}.background-grid{grid-template-columns:1fr}.background-item{border-right:0;border-bottom:1px solid #ededf0!important}.background-item:last-child{border-bottom:0!important}.metric-surface{grid-template-columns:repeat(2,minmax(0,1fr))}.metric-item:nth-child(3n){border-right:1px solid #ededf0}.metric-item:nth-child(2n){border-right:0}.metric-item:nth-last-child(-n+3){border-bottom:1px solid #ededf0}.metric-item:nth-last-child(-n+2){border-bottom:0}.chart-head{align-items:flex-start;flex-direction:column}.chart-switch{width:100%}.rule-row{grid-template-columns:1fr auto}.rule-value{grid-column:1/-1;color:#6e6e73}}@media(max-width:430px){.edition{display:none}.metric-surface{grid-template-columns:1fr}.metric-item{border-right:0;border-bottom:1px solid #ededf0!important}.metric-item:last-child{border-bottom:0!important}.issue{grid-template-columns:1fr}}
        </style>
        """;

    private const string Script = """
        <script>
        (()=>{const root=document.querySelector('.report-shell');const sections=root.querySelectorAll('[data-section]');sections.forEach(button=>button.addEventListener('click',()=>{sections.forEach(item=>item.setAttribute('aria-pressed',String(item===button)));document.getElementById(button.dataset.section).scrollIntoView({behavior:'smooth',block:'start'});}));const charts=root.querySelectorAll('[data-chart]');charts.forEach(button=>button.addEventListener('click',()=>{charts.forEach(item=>item.setAttribute('aria-pressed',String(item===button)));root.querySelectorAll('.chart-panel').forEach(panel=>{panel.hidden=panel.id!==button.dataset.chart;});}));})();
        </script>
        """;
}
