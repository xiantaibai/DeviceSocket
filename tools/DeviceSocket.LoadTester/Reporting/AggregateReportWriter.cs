using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 把多负载节点合并结果写为 JSON 和便于人工验收的 Markdown 报告。
/// </summary>
internal static class AggregateReportWriter
{
    /// <summary>
    /// 将汇总报告写入指定目录。
    /// </summary>
    /// <param name="report">已经完成验收计算的汇总报告。</param>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="cancellationToken">取消写入操作的令牌。</param>
    internal static async Task WriteAsync(AggregateReport report, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "aggregate-summary.json"),
            JsonSerializer.Serialize(report, LoadTestConfigurationLoader.CreateSerializerOptions()),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "report.md"),
            BuildMarkdown(report),
            new UTF8Encoding(false),
            cancellationToken);
    }

    /// <summary>
    /// 生成包含客户端负载和 Gateway 资源峰值的汇总 Markdown。
    /// </summary>
    /// <param name="report">汇总报告。</param>
    /// <returns>完整 Markdown 文本。</returns>
    private static string BuildMarkdown(AggregateReport report)
    {
        var metrics = report.Metrics;
        var builder = new StringBuilder();
        builder.AppendLine($"# DeviceSocket 多节点负载测试汇总：{report.RunId}");
        builder.AppendLine();
        builder.AppendLine($"- 节点：{report.NodeCount:N0}");
        builder.AppendLine($"- 设备：{report.DeviceCount:N0}");
        builder.AppendLine($"- 时间：{report.StartedAtUtc:O} 至 {report.EndedAtUtc:O}");
        builder.AppendLine($"- 运行状态：{FormatRunStatus(report.RunStatus)}");
        builder.AppendLine($"- 指标验收：{(report.Acceptance.Passed ? "通过" : "失败")}");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 汇总值 |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| 最终协议在线 | {metrics.ProtocolOnline:N0}/{metrics.ExpectedOnline:N0} |");
        builder.AppendLine($"| 连接尝试/成功/失败 | {metrics.ConnectionAttempts:N0}/{metrics.ConnectionSucceeded:N0}/{metrics.ConnectionFailed:N0} |");
        builder.AppendLine($"| 登录尝试/成功/失败 | {metrics.LoginAttempts:N0}/{metrics.LoginSucceeded:N0}/{metrics.LoginFailed:N0} |");
        builder.AppendLine($"| 重连 | {metrics.ReconnectsScheduled:N0} |");
        builder.AppendLine($"| 主动断线 | {metrics.ForcedDisconnects:N0} |");
        builder.AppendLine($"| 注入丢包/校验和/粘包/半包 | {metrics.InjectedOutboundDrops + metrics.InjectedInboundDrops:N0}/{metrics.InjectedCorruptFrames:N0}/{metrics.InjectedDuplicateFrames:N0}/{metrics.InjectedFragmentedWrites:N0} |");
        builder.AppendLine($"| 下发应答 | {metrics.DownstreamResponsesSent:N0} |");
        builder.AppendLine($"| HTTP API 发起/成功/失败/超时 | {metrics.ApiRequestsStarted:N0}/{metrics.ApiRequestsSucceeded:N0}/{metrics.ApiRequestsFailed:N0}/{metrics.ApiRequestTimeouts:N0} |");
        builder.AppendLine($"| HTTP 目标设备累计要求/在线/离线跳过 | {metrics.ApiTargetsRequired:N0}/{metrics.ApiTargetsOnline:N0}/{metrics.ApiRequestsSkippedOffline:N0} |");
        builder.AppendLine($"| 旧 Gather 读表命令收到/已应答/丢弃 | {metrics.GatherReadingCommandsReceived:N0}/{metrics.GatherReadingCommandsResponded:N0}/{metrics.GatherReadingResponsesDropped:N0} |");
        if (metrics.GatherV4Commands.ReceivedFrames > 0)
            builder.AppendLine(GatherV4CommandReportFormatter.MarkdownRows(metrics.GatherV4Commands));
        builder.AppendLine($"| 旧 Gather 读表响应帧/业务失败/重复帧 | {metrics.GatherReadingResponsesSent:N0}/{metrics.GatherReadingBusinessFailuresSent:N0}/{metrics.GatherReadingDuplicateResponsesSent:N0} |");
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
        builder.AppendLine($"| 登录延迟 P50/P95/P99 | {metrics.LoginLatency.Percentile(0.50)}/{metrics.LoginLatency.Percentile(0.95)}/{metrics.LoginLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 建连延迟 P50/P95/P99 | {metrics.ConnectionLatency.Percentile(0.50)}/{metrics.ConnectionLatency.Percentile(0.95)}/{metrics.ConnectionLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| HTTP 端到端延迟 P50/P95/P99 | {metrics.ApiRequestLatency.Percentile(0.50)}/{metrics.ApiRequestLatency.Percentile(0.95)}/{metrics.ApiRequestLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 旧 Gather 读表本地响应延迟 P50/P95/P99 | {metrics.GatherReadingResponseLatency.Percentile(0.50)}/{metrics.GatherReadingResponseLatency.Percentile(0.95)}/{metrics.GatherReadingResponseLatency.Percentile(0.99)} ms |");
        builder.AppendLine($"| 掉线事件/非预期事件 | {metrics.Outages.Count:N0}/{metrics.Outages.Count(item => !item.ExpectedServerRestart):N0} |");
        builder.AppendLine($"| 最严重恢复耗时/当前连续稳定 | {FormatOptionalMilliseconds(metrics.WorstRecoveryMilliseconds)}/{metrics.CurrentStableOnlineMilliseconds / 1000d:F1} s |");
        if (metrics.Outages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 掉线与恢复事件");
            builder.AppendLine();
            builder.AppendLine("| # | 阶段 | 性质 | 开始 | 恢复 | 耗时 | 最低在线 | 最大离线 | 重连 | 端口占用 |");
            builder.AppendLine("| ---: | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var outage in metrics.Outages)
            {
                builder.AppendLine(
                    $"| {outage.Sequence:N0} | {EscapeMarkdownCell(outage.PhaseName)} | " +
                    $"{(outage.ExpectedServerRestart ? "计划重启" : "非预期")} | {outage.StartedAtUtc:HH:mm:ss} UTC | " +
                    $"{(outage.RecoveredAtUtc.HasValue ? $"{outage.RecoveredAtUtc:HH:mm:ss} UTC" : "未恢复")} | " +
                    $"{FormatOptionalMilliseconds(outage.DurationMilliseconds)} | {outage.MinimumProtocolOnline:N0} | " +
                    $"{outage.MaximumOfflineDevices:N0} | {outage.ReconnectsScheduled:N0} | {outage.AddressAlreadyInUse:N0} |");
            }
        }
        builder.AppendLine();
        builder.AppendLine("## 节点运行状态");
        builder.AppendLine();
        builder.AppendLine("| 分片 | 节点 | 状态 | 原因 |");
        builder.AppendLine("| ---: | --- | --- | --- |");
        foreach (var outcome in report.NodeOutcomes)
        {
            builder.AppendLine(
                $"| {outcome.ShardIndex} | {EscapeMarkdownCell(outcome.NodeName)} | {FormatRunStatus(outcome.Status)} | " +
                $"{EscapeMarkdownCell(outcome.Reason ?? "-")} |");
        }
        builder.AppendLine();
        builder.AppendLine("## 验收规则");
        builder.AppendLine();
        foreach (var check in report.Acceptance.Checks)
            builder.AppendLine($"- {(check.Passed ? "通过" : "失败")} {check.Name}：{check.Actual:F4} {check.Comparator} {check.Threshold:F4}");
        return builder.ToString();
    }

    /// <summary>
    /// 把内部运行状态转换为报告中的中文结论。
    /// </summary>
    /// <param name="status">节点或汇总运行状态。</param>
    /// <returns>用于 Markdown 展示的中文文本。</returns>
    private static string FormatRunStatus(LoadTestRunStatus status) => status switch
    {
        LoadTestRunStatus.Passed => "通过",
        LoadTestRunStatus.Failed => "失败",
        LoadTestRunStatus.CompletedWithSkippedPhases => "完成但有阶段跳过",
        LoadTestRunStatus.Cancelled => "已取消",
        LoadTestRunStatus.Aborted => "已终止",
        _ => status.ToString()
    };

    /// <summary>
    /// 转义会破坏 Markdown 表格结构的节点名称或原因文本，并把换行压缩为空格。
    /// </summary>
    /// <param name="value">待写入表格单元格的文本。</param>
    /// <returns>可安全写入 Markdown 表格的文本。</returns>
    private static string EscapeMarkdownCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// 把字节数格式化为便于压测报告阅读的 MiB 文本。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>保留两位小数的 MiB 文本。</returns>
    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:F2} MiB";

    /// <summary>
    /// 将可空毫秒值转换为秒；未恢复事件显示“未恢复”。
    /// </summary>
    /// <param name="milliseconds">可空毫秒值。</param>
    /// <returns>带秒单位的报告文本。</returns>
    private static string FormatOptionalMilliseconds(long? milliseconds) =>
        milliseconds.HasValue ? $"{milliseconds.Value / 1000d:F1} s" : "未恢复";
}
