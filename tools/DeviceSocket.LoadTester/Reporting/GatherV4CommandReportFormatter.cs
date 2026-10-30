using System.Text;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>为节点、合并及详细报告统一展示 GatherV4 人工下发观测，始终区分 F1 写出和业务任务完成。</summary>
internal static class GatherV4CommandReportFormatter
{
    /// <summary>生成可嵌入既有指标表的 Markdown 行。</summary>
    /// <param name="value">当前节点或合并事实。</param>
    /// <returns>不包含结论推断的设备侧指标。</returns>
    internal static string MarkdownRows(GatherV4CommandSummary value) =>
        $"| GatherV4 命令帧/观察去重命令 | {value.ReceivedFrames:N0}/{value.ObservedCommands:N0} |\n" +
        $"| GatherV4 正确 F1/故障应答/故意不回复 | {value.CorrectConfirmationsSent:N0}/{value.FaultResponsesSent:N0}/{value.IntentionalDrops:N0} |\n" +
        $"| GatherV4 疑似重发/正确确认后再次到达 | {value.SuspectedRepeatedFrames:N0}/{value.RepeatedAfterConfirmation:N0} |\n" +
        $"| GatherV4 后续命令/等待 P95 | {value.FollowingCommands:N0}/{value.FollowingCommandLatency.Percentile(0.95):N0} ms |\n" +
        $"| GatherV4 应答 P50/P95/P99 | {value.ConfirmationLatency.Percentile(0.5):N0}/{value.ConfirmationLatency.Percentile(0.95):N0}/{value.ConfirmationLatency.Percentile(0.99):N0} ms |\n" +
        $"| GatherV4 回复写失败/队满拒绝/取消/待回复 | {value.WriteFailures:N0}/{value.QueueRejected:N0}/{value.CancelledResponses:N0}/{value.PendingResponses:N0} |";

    /// <summary>生成只含数值和固定标题的 HTML 表，供两种受众共用。</summary>
    /// <param name="value">当前运行设备侧事实。</param>
    /// <returns>可嵌入报表视图的固定 HTML。</returns>
    internal static string Html(GatherV4CommandSummary value)
    {
        var builder = new StringBuilder("<h3 class=\"table-title\">GatherV4 人工下发与自动确认</h3><p>F1 已写出不等于服务端任务已完成；同帧重复仅作疑似重发，需核对 A3/PSEQ 复用与业务任务标识。</p><div class=\"table-wrap\"><table><thead><tr><th>指标</th><th>数值</th></tr></thead><tbody>");
        foreach (var row in MarkdownRows(value).Split('\n'))
        {
            var cells = row.Split('|');
            builder.Append("<tr><td>").Append(cells[1].Trim()).Append("</td><td>").Append(cells[2].Trim()).Append("</td></tr>");
        }
        return builder.Append("</tbody></table></div>").ToString();
    }
}
