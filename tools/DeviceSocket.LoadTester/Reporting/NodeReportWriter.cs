using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 把单个负载分片的明细、错误统计和验收结果写入报告目录。
/// </summary>
internal static class NodeReportWriter
{
    /// <summary>
    /// 将节点 JSON、错误 CSV 和 Markdown 报告写入指定目录。
    /// </summary>
    /// <param name="report">单节点执行报告。</param>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="cancellationToken">取消写入操作的令牌。</param>
    internal static async Task WriteAsync(NodeReport report, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var serializerOptions = LoadTestConfigurationLoader.CreateSerializerOptions();
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "node-summary.json"),
            JsonSerializer.Serialize(report, serializerOptions),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "errors.csv"),
            BuildErrorsCsv(report),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "report.md"),
            BuildMarkdown(report),
            new UTF8Encoding(false),
            cancellationToken);
    }

    /// <summary>
    /// 按出现次数降序生成错误码 CSV。
    /// </summary>
    /// <param name="report">单节点执行报告。</param>
    /// <returns>错误码及数量 CSV 文本。</returns>
    private static string BuildErrorsCsv(NodeReport report)
    {
        var builder = new StringBuilder("errorCode,count\n");
        foreach (var error in report.Metrics.Errors.OrderByDescending(item => item.Value))
            builder.Append(error.Key.Replace(',', '_')).Append(',').Append(error.Value).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// 生成包含客户端指标和 Gateway 观测值的节点 Markdown 报告。
    /// </summary>
    /// <param name="report">单节点执行报告。</param>
    /// <returns>完整 Markdown 文本。</returns>
    private static string BuildMarkdown(NodeReport report)
    {
        var metrics = report.Metrics;
        var builder = new StringBuilder();
        builder.AppendLine($"# DeviceSocket 负载测试报告：{report.RunId}");
        builder.AppendLine();
        builder.AppendLine($"- 节点：{report.NodeName}，分片 {report.ShardIndex + 1}/{report.ShardCount}");
        builder.AppendLine($"- 设备：{report.LocalDeviceCount:N0}");
        builder.AppendLine($"- 时间：{report.StartedAtUtc:O} 至 {report.EndedAtUtc:O}");
        builder.AppendLine($"- 运行状态：{FormatRunStatus(report.RunStatus)}");
        builder.AppendLine($"- 验收结果：{(report.Acceptance.Passed ? "通过" : "失败")}");
        if (!string.IsNullOrWhiteSpace(report.FailureReason))
            builder.AppendLine($"- 终止原因：{EscapeMarkdownCell(report.FailureReason)}");
        builder.AppendLine();
        builder.AppendLine("## 核心指标");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| 最终协议在线 | {metrics.ProtocolOnline:N0}/{metrics.ExpectedOnline:N0} |");
        builder.AppendLine($"| 连接尝试/成功/失败 | {metrics.ConnectionAttempts:N0}/{metrics.ConnectionSucceeded:N0}/{metrics.ConnectionFailed:N0} |");
        builder.AppendLine($"| 登录尝试/成功/失败 | {metrics.LoginAttempts:N0}/{metrics.LoginSucceeded:N0}/{metrics.LoginFailed:N0} |");
        builder.AppendLine($"| 心跳发送/确认/超时 | {metrics.HeartbeatsSent:N0}/{metrics.HeartbeatsConfirmed:N0}/{metrics.HeartbeatTimeouts:N0} |");
        builder.AppendLine($"| 重连次数 | {metrics.ReconnectsScheduled:N0} |");
        builder.AppendLine($"| 主动断线注入 | {metrics.ForcedDisconnects:N0} |");
        builder.AppendLine($"| 出站/入站丢包注入 | {metrics.InjectedOutboundDrops:N0}/{metrics.InjectedInboundDrops:N0} |");
        builder.AppendLine($"| 错误校验和/粘包/半包注入 | {metrics.InjectedCorruptFrames:N0}/{metrics.InjectedDuplicateFrames:N0}/{metrics.InjectedFragmentedWrites:N0} |");
        builder.AppendLine($"| 下发应答 | {metrics.DownstreamResponsesSent:N0} |");
        builder.AppendLine($"| HTTP API 发起/成功/失败/超时 | {metrics.ApiRequestsStarted:N0}/{metrics.ApiRequestsSucceeded:N0}/{metrics.ApiRequestsFailed:N0}/{metrics.ApiRequestTimeouts:N0} |");
        builder.AppendLine($"| HTTP 目标设备累计要求/在线/离线跳过 | {metrics.ApiTargetsRequired:N0}/{metrics.ApiTargetsOnline:N0}/{metrics.ApiRequestsSkippedOffline:N0} |");
        builder.AppendLine($"| Gather 读表命令收到/已应答/丢弃 | {metrics.GatherReadingCommandsReceived:N0}/{metrics.GatherReadingCommandsResponded:N0}/{metrics.GatherReadingResponsesDropped:N0} |");
        builder.AppendLine($"| Gather 读表响应帧/业务失败/重复帧 | {metrics.GatherReadingResponsesSent:N0}/{metrics.GatherReadingBusinessFailuresSent:N0}/{metrics.GatherReadingDuplicateResponsesSent:N0} |");
        if (report.GatewayObservation is { } gateway)
        {
            builder.AppendLine($"| Gateway 测试前基线 TCP/认证设备 | {gateway.BaselineTcpConnections?.ToString("N0") ?? "不可用"}/{gateway.BaselineAuthenticatedDevices?.ToString("N0") ?? "不可用"} |");
            builder.AppendLine($"| Gateway 本轮峰值增量 TCP/认证设备 | {gateway.MaximumAdditionalTcpConnections:N0}/{gateway.MaximumAdditionalAuthenticatedDevices:N0} |");
            builder.AppendLine($"| Gateway 峰值 TCP/认证设备 | {gateway.MaximumTcpConnections:N0}/{gateway.MaximumAuthenticatedDevices:N0} |");
            builder.AppendLine($"| Gateway CPU/工作集/托管堆峰值 | {gateway.MaximumProcessCpuPercent:F2}%/{FormatBytes(gateway.MaximumProcessWorkingSetBytes)}/{FormatBytes(gateway.MaximumManagedHeapBytes)} |");
            builder.AppendLine($"| Gateway 线程池最大排队 | {gateway.MaximumThreadPoolPendingWorkItems:N0} |");
            builder.AppendLine($"| Gateway 发送积压消息/字节历史峰值 | {gateway.MaximumPeakPendingSendMessages:N0}/{FormatBytes(gateway.MaximumPeakPendingSendBytes)} |");
            builder.AppendLine($"| Gateway Session 扫描最大耗时 | {gateway.MaximumSessionScanDurationMilliseconds:N0} ms |");
            builder.AppendLine($"| Gateway 新增程序/上行/下发日志丢弃 | {gateway.AdditionalApplicationLogDropped:N0}/{gateway.AdditionalUpstreamLogDropped:N0}/{gateway.AdditionalDownstreamLogDropped:N0} |");
            builder.AppendLine($"| Gateway 默认过滤心跳日志 | {gateway.AdditionalHeartbeatLogSuppressed:N0} |");
            builder.AppendLine($"| Gateway 不可用采样 | {gateway.UnavailableSamples:N0}/{gateway.Samples:N0} |");
        }
        builder.AppendLine($"| 收/发字节 | {metrics.BytesReceived:N0}/{metrics.BytesSent:N0} |");
        builder.AppendLine($"| 登录延迟 P50/P95/P99 | {metrics.LoginLatency.Percentile(0.50)}/{metrics.LoginLatency.Percentile(0.95)}/{metrics.LoginLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 建连延迟 P50/P95/P99 | {metrics.ConnectionLatency.Percentile(0.50)}/{metrics.ConnectionLatency.Percentile(0.95)}/{metrics.ConnectionLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| HTTP 端到端延迟 P50/P95/P99 | {metrics.ApiRequestLatency.Percentile(0.50)}/{metrics.ApiRequestLatency.Percentile(0.95)}/{metrics.ApiRequestLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| Gather 读表本地响应延迟 P50/P95/P99 | {metrics.GatherReadingResponseLatency.Percentile(0.50)}/{metrics.GatherReadingResponseLatency.Percentile(0.95)}/{metrics.GatherReadingResponseLatency.Percentile(0.99)} ms |");
        if (report.CommandPhases.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## HTTP 指令阶段");
            builder.AppendLine();
            builder.AppendLine("| 阶段 | 状态 | 在线门槛峰值/要求 | 等待 | 本地设备 | 计划请求 | show_result | 目标在线/要求 | 离线跳过 | API 成功/失败/超时 | 读表收到/应答 | 耗时 | 说明 |");
            builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | :---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var phase in report.CommandPhases)
            {
                var onlineGate = phase.OnlineGateSatisfied.HasValue
                    ? $"{phase.MaximumObservedOnlineCount:N0}/{phase.RequiredOnlineCount:N0}"
                    : "不适用";
                builder.AppendLine(
                    $"| {EscapeMarkdownCell(phase.Name)} | {FormatCommandPhaseStatus(phase.Status)} | {onlineGate} | " +
                    $"{phase.OnlineGateWaitMilliseconds / 1000d:F1} s | {phase.LocalDeviceCount:N0} | {phase.PlannedRequests:N0} | {phase.ShowResult} | " +
                    $"{phase.ApiTargetsOnline:N0}/{phase.ApiTargetsRequired:N0} | {phase.ApiRequestsSkippedOffline:N0} | " +
                    $"{phase.ApiRequestsSucceeded:N0}/{phase.ApiRequestsFailed:N0}/{phase.ApiRequestTimeouts:N0} | " +
                    $"{phase.GatherReadingCommandsReceived:N0}/{phase.GatherReadingCommandsResponded:N0} | " +
                    $"{(phase.EndedAtUtc - phase.StartedAtUtc).TotalSeconds:F1} s | " +
                    $"{EscapeMarkdownCell(phase.StatusReason ?? "-")} |");
            }
        }
        builder.AppendLine();
        builder.AppendLine("## 验收规则");
        builder.AppendLine();
        foreach (var check in report.Acceptance.Checks)
            builder.AppendLine($"- {(check.Passed ? "通过" : "失败")} {check.Name}：{check.Actual:F4} {check.Comparator} {check.Threshold:F4}");
        if (report.PreflightWarnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 运行前警告");
            builder.AppendLine();
            foreach (var warning in report.PreflightWarnings)
                builder.AppendLine($"- {warning}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把字节数格式化为便于压测报告阅读的 MiB 文本。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>保留两位小数的 MiB 文本。</returns>
    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:F2} MiB";

    /// <summary>
    /// 将节点运行状态转换为适合中文报告阅读的稳定文本。
    /// </summary>
    /// <param name="status">节点运行状态枚举。</param>
    /// <returns>中文运行状态。</returns>
    private static string FormatRunStatus(LoadTestRunStatus status) => status switch
    {
        LoadTestRunStatus.Passed => "完成并通过",
        LoadTestRunStatus.Failed => "完成但验收失败",
        LoadTestRunStatus.CompletedWithSkippedPhases => "完成，但存在跳过阶段",
        LoadTestRunStatus.Cancelled => "已取消，已生成部分报告",
        LoadTestRunStatus.Aborted => "异常中止，已生成部分报告",
        _ => status.ToString()
    };

    /// <summary>
    /// 将 HTTP 指令阶段状态转换为中文报告文本。
    /// </summary>
    /// <param name="status">HTTP 指令阶段状态。</param>
    /// <returns>中文阶段状态。</returns>
    private static string FormatCommandPhaseStatus(CommandPhaseStatus status) => status switch
    {
        CommandPhaseStatus.Completed => "已完成",
        CommandPhaseStatus.Skipped => "已跳过",
        CommandPhaseStatus.Failed => "失败",
        _ => status.ToString()
    };

    /// <summary>
    /// 转义 Markdown 表格单元格中的换行和竖线，避免异常消息破坏报告表格结构。
    /// </summary>
    /// <param name="value">原始单元格文本。</param>
    /// <returns>可安全写入单行 Markdown 表格的文本。</returns>
    private static string EscapeMarkdownCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
