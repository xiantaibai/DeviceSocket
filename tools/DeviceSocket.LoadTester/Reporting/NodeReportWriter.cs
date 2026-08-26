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
            Path.Combine(outputDirectory, "http-errors.csv"),
            BuildHttpErrorsCsv(report.HttpRequestErrors),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "http-errors.jsonl"),
            BuildHttpErrorsJsonLines(report.HttpRequestErrors, serializerOptions),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "command-traces.jsonl"),
            BuildJsonLines(report.CommandTraces, serializerOptions),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "phase-targets.csv"),
            BuildPhaseTargetsCsv(report.CommandTraces),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "downstream-unknown-samples.jsonl"),
            BuildJsonLines(report.DownstreamDiagnosticSamples, serializerOptions),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "downstream-classification.csv"),
            BuildDownstreamClassificationCsv(report.DownstreamDiagnostics),
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
    /// 生成可直接由 Excel 打开的逐请求 HTTP 失败明细 CSV；字段值使用标准双引号转义。
    /// </summary>
    /// <param name="errors">按请求完成时间收集的失败明细。</param>
    /// <returns>包含表头的 UTF-8 CSV 文本。</returns>
    private static string BuildHttpErrorsCsv(IEnumerable<HttpRequestErrorDetail> errors)
    {
        var builder = new StringBuilder(
            "timestampUtc,phase,meterId,deviceId,showResult,requestSequence,httpStatusCode,errorCode,errorMessage,elapsedMilliseconds,timedOut,prevalidationRejected,shardIndex\n");
        foreach (var error in errors.OrderBy(item => item.TimestampUtc).ThenBy(item => item.RequestSequence))
        {
            builder.Append(Csv(error.TimestampUtc.ToString("O"))).Append(',')
                .Append(Csv(error.PhaseName)).Append(',')
                .Append(error.MeterId).Append(',')
                .Append(Csv(error.DeviceId)).Append(',')
                .Append(error.ShowResult).Append(',')
                .Append(error.RequestSequence).Append(',')
                .Append(error.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Csv(error.ErrorCode)).Append(',')
                .Append(Csv(error.ErrorMessage)).Append(',')
                .Append(error.ElapsedMilliseconds).Append(',')
                .Append(error.TimedOut).Append(',')
                .Append(error.PrevalidationRejected).Append(',')
                .Append(error.ShardIndex).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 生成逐请求 HTTP 失败 JSON Lines，便于使用脚本按阶段、设备和错误码流式分析。
    /// </summary>
    /// <param name="errors">失败明细。</param>
    /// <param name="serializerOptions">与节点报告一致的 JSON 配置。</param>
    /// <returns>每行一个完整 JSON 对象的文本。</returns>
    private static string BuildHttpErrorsJsonLines(
        IEnumerable<HttpRequestErrorDetail> errors,
        JsonSerializerOptions serializerOptions)
    {
        var jsonLinesOptions = new JsonSerializerOptions(serializerOptions) { WriteIndented = false };
        var builder = new StringBuilder();
        foreach (var error in errors.OrderBy(item => item.TimestampUtc).ThenBy(item => item.RequestSequence))
            builder.AppendLine(JsonSerializer.Serialize(error, jsonLinesOptions));
        return builder.ToString();
    }

    /// <summary>生成通用 JSON Lines 文本，支持逐请求关联和未知报文样本流式分析。</summary>
    /// <typeparam name="T">单行报告模型类型。</typeparam>
    /// <param name="items">待序列化记录。</param>
    /// <param name="serializerOptions">与节点摘要一致的 JSON 配置。</param>
    /// <returns>每行一个 JSON 对象的文本；没有记录时为空字符串。</returns>
    private static string BuildJsonLines<T>(
        IEnumerable<T> items,
        JsonSerializerOptions serializerOptions)
    {
        var jsonLinesOptions = new JsonSerializerOptions(serializerOptions) { WriteIndented = false };
        var builder = new StringBuilder();
        foreach (var item in items)
            builder.AppendLine(JsonSerializer.Serialize(item, jsonLinesOptions));
        return builder.ToString();
    }

    /// <summary>生成每个 HTTP 阶段实际请求目标及端到端结果 CSV，便于按电表或设备定位缺口。</summary>
    /// <param name="traces">逐请求关联明细。</param>
    /// <returns>包含固定表头的 UTF-8 CSV 文本。</returns>
    private static string BuildPhaseTargetsCsv(IEnumerable<CommandTraceDetail> traces)
    {
        var builder = new StringBuilder(
            "phase,requestSequence,meterId,deviceId,showResult,httpStatusCode,apiSucceeded,prevalidationRejected," +
            "downstreamKind,associationConfidence,duplicateDownstreamFrames,responseSent,outcome," +
            "registeredAtUtc,httpCompletedAtUtc,firstDownstreamAtUtc,responseSentAtUtc\n");
        foreach (var trace in traces.OrderBy(item => item.RequestSequence))
        {
            builder.Append(Csv(trace.PhaseName)).Append(',')
                .Append(trace.RequestSequence).Append(',')
                .Append(trace.MeterId).Append(',')
                .Append(Csv(trace.DeviceId)).Append(',')
                .Append(trace.ShowResult).Append(',')
                .Append(trace.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(trace.ApiSucceeded?.ToString() ?? string.Empty).Append(',')
                .Append(trace.PrevalidationRejected).Append(',')
                .Append(Csv(trace.DownstreamKind?.ToString())).Append(',')
                .Append(Csv(trace.AssociationConfidence.ToString())).Append(',')
                .Append(trace.DuplicateDownstreamFrames).Append(',')
                .Append(trace.ResponseSent).Append(',')
                .Append(Csv(trace.Outcome.ToString())).Append(',')
                .Append(Csv(trace.RegisteredAtUtc.ToString("O"))).Append(',')
                .Append(Csv(trace.HttpCompletedAtUtc?.ToString("O"))).Append(',')
                .Append(Csv(trace.FirstDownstreamAtUtc?.ToString("O"))).Append(',')
                .Append(Csv(trace.ResponseSentAtUtc?.ToString("O"))).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>生成节点级下行分类计数 CSV，并附加未关联帧和重复帧诊断计数。</summary>
    /// <param name="summary">可选下行诊断汇总。</param>
    /// <returns>包含固定表头的 UTF-8 CSV 文本；未启用诊断时只包含表头。</returns>
    private static string BuildDownstreamClassificationCsv(DownstreamDiagnosticSummary? summary)
    {
        var builder = new StringBuilder("classification,count\n");
        if (summary is null)
            return builder.ToString();
        foreach (var item in summary.Classifications.OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
            builder.Append(Csv(item.Key.ToString())).Append(',').Append(item.Value).Append('\n');
        builder.Append(Csv("UnmatchedDownstreamFrames")).Append(',').Append(summary.UnmatchedDownstreamFrames).Append('\n');
        builder.Append(Csv("DuplicateDownstreamFrames")).Append(',').Append(summary.DuplicateDownstreamFrames).Append('\n');
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
        var commandPhases = report.CommandPhases.Where(item => item.Status != CommandPhaseStatus.Skipped).ToArray();
        var estimatedEnqueued = commandPhases.Sum(item => item.ApiEstimatedEnqueued);
        var rawObservedCommands = commandPhases.Sum(item => item.RawDownstreamCommandsObserved);
        var recognizedCommands = commandPhases.Sum(item => item.ProtocolRecognizedCommands);
        var unsupportedCommands = commandPhases.Sum(item => item.UnsupportedCommands);
        var duplicateCommands = commandPhases.Sum(item => item.DuplicateDownstreamFrames);
        var unobservedCommands = commandPhases.Sum(item => item.GatherReadingCommandsUnobserved);
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
        if (commandPhases.Length > 0)
        {
            builder.AppendLine(
                $"| HTTP 估算入队/原始下行/协议识别/不支持/未观测 | " +
                $"{estimatedEnqueued:N0}/{rawObservedCommands:N0}/{recognizedCommands:N0}/{unsupportedCommands:N0}/{unobservedCommands:N0} |");
            builder.AppendLine($"| HTTP 原始下行交付率 | {Rate(Math.Min(rawObservedCommands, estimatedEnqueued), estimatedEnqueued):P2} |");
            builder.AppendLine($"| HTTP 协议识别率 | {Rate(recognizedCommands, rawObservedCommands):P2} |");
            builder.AppendLine($"| HTTP 重复或重试下行帧 | {duplicateCommands:N0} |");
        }
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
        builder.AppendLine($"| 最严重恢复耗时/当前连续稳定 | {FormatRecovery(metrics)}/{metrics.CurrentStableOnlineMilliseconds / 1000d:F1} s |");
        if (report.Phases.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## TCP 阶段");
            builder.AppendLine();
            builder.AppendLine("| 阶段 | 类型 | 目标在线 | 最低/最高/结束在线 | 连接成功/失败 | 登录成功/失败 | 重连/心跳超时 | 人工操作窗口 |");
            builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var phase in report.Phases)
            {
                var delta = phase.Delta;
                var actionWindow = phase.ActionWindowStartedAtUtc.HasValue && phase.ActionWindowEndedAtUtc.HasValue
                    ? $"{phase.ActionWindowStartedAtUtc:HH:mm:ss}–{phase.ActionWindowEndedAtUtc:HH:mm:ss} UTC"
                    : "-";
                builder.AppendLine(
                    $"| {EscapeMarkdownCell(phase.Name)} | {phase.Kind} | {phase.ExpectedOnline:N0} | " +
                    $"{phase.MinimumProtocolOnline:N0}/{phase.MaximumProtocolOnline:N0}/{phase.Metrics.ProtocolOnline:N0} | " +
                    $"{delta?.ConnectionSucceeded ?? 0:N0}/{delta?.ConnectionFailed ?? 0:N0} | " +
                    $"{delta?.LoginSucceeded ?? 0:N0}/{delta?.LoginFailed ?? 0:N0} | " +
                    $"{delta?.ReconnectsScheduled ?? 0:N0}/{delta?.HeartbeatTimeouts ?? 0:N0} | {actionWindow} |");
            }
        }
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
        if (report.CommandPhases.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## HTTP 指令阶段");
            builder.AppendLine();
            builder.AppendLine("| 阶段 | 状态 | show_result | API 发起/成功/失败/超时 | 预校验拒绝/估算入队 | 原始下行/识别/不支持/应答/未观测 | 原始交付率/识别率/应答率 | 重复帧 | 排空 | P95 | 在线门槛峰值/要求 | 耗时 | 说明 |");
            builder.AppendLine("| --- | --- | :---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |");
            foreach (var phase in report.CommandPhases)
            {
                var onlineGate = phase.OnlineGateSatisfied.HasValue
                    ? $"{phase.MaximumObservedOnlineCount:N0}/{phase.RequiredOnlineCount:N0}"
                    : "不适用";
                builder.AppendLine(
                    $"| {EscapeMarkdownCell(phase.Name)} | {FormatCommandPhaseStatus(phase.Status)} | {phase.ShowResult} | " +
                    $"{phase.ApiRequestsStarted:N0}/{phase.ApiRequestsSucceeded:N0}/{phase.ApiRequestsFailed:N0}/{phase.ApiRequestTimeouts:N0} | " +
                    $"{phase.ApiPrevalidationRejected:N0}/{phase.ApiEstimatedEnqueued:N0} | " +
                    $"{phase.RawDownstreamCommandsObserved:N0}/{phase.ProtocolRecognizedCommands:N0}/{phase.UnsupportedCommands:N0}/" +
                    $"{phase.GatherReadingCommandsResponded:N0}/{phase.GatherReadingCommandsUnobserved:N0} | " +
                    $"{phase.RawDeliveryRate:P2}/{phase.ProtocolRecognitionRate:P2}/{phase.GatherReadingResponseRate:P2} | " +
                    $"{phase.DuplicateDownstreamFrames:N0} | " +
                    $"{FormatDrain(phase)} | {phase.ApiRequestLatency.Percentile(0.95):N0} ms | {onlineGate} | " +
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
    /// 将可空毫秒值转换为秒；未恢复事件显示“未恢复”。
    /// </summary>
    /// <param name="milliseconds">可空毫秒值。</param>
    /// <returns>带秒单位的报告文本。</returns>
    private static string FormatOptionalMilliseconds(long? milliseconds) =>
        milliseconds.HasValue ? $"{milliseconds.Value / 1000d:F1} s" : "未恢复";

    /// <summary>区分“没有掉线事件”和“发生掉线但尚未恢复”，避免正常场景显示未恢复。</summary>
    /// <param name="metrics">最终指标快照。</param>
    /// <returns>无掉线、未恢复或恢复耗时。</returns>
    private static string FormatRecovery(DeviceSocket.LoadTester.Metrics.MetricSnapshot metrics)
    {
        if (metrics.Outages.Count == 0 && !metrics.OutageActive)
            return "无掉线";
        return FormatOptionalMilliseconds(metrics.WorstRecoveryMilliseconds);
    }

    /// <summary>把阶段排空三态转换为中文报告文本。</summary>
    /// <param name="phase">HTTP 指令阶段结果。</param>
    /// <returns>未启用、通过或超时及等待时长。</returns>
    private static string FormatDrain(CommandPhaseResult phase) => phase.DrainSatisfied switch
    {
        true => $"通过/{phase.DrainWaitMilliseconds / 1000d:F1} s",
        false => $"超时/{phase.DrainWaitMilliseconds / 1000d:F1} s",
        null => "未启用"
    };

    /// <summary>计算安全比例，分母不大于零时返回零。</summary>
    /// <param name="numerator">分子。</param>
    /// <param name="denominator">分母。</param>
    /// <returns>双精度比例。</returns>
    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    /// <summary>按 RFC 4180 规则转义单个 CSV 字段。</summary>
    /// <param name="value">可空字段值。</param>
    /// <returns>始终使用双引号包围的安全字段。</returns>
    private static string Csv(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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
