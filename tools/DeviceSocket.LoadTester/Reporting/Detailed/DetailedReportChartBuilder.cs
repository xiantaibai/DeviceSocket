using System.Globalization;
using System.Security;
using System.Text;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 表示详细报告使用的全部 SVG 图表；字符串既可独立落盘，也可内嵌到自包含 HTML。
/// </summary>
internal sealed record DetailedReportCharts
{
    /// <summary>获取连接与在线时间线 SVG。</summary>
    internal required string OnlineTimeline { get; init; }

    /// <summary>获取登录业务积压 SVG。</summary>
    internal required string BusinessOnlineGap { get; init; }

    /// <summary>获取客户端连接错误 SVG。</summary>
    internal required string ConnectionErrors { get; init; }

    /// <summary>获取 HTTP 阶段成功/失败 SVG。</summary>
    internal required string ApiPhases { get; init; }

    /// <summary>获取命令端到端漏斗 SVG。</summary>
    internal required string CommandFunnel { get; init; }
}

/// <summary>
/// 使用无外部依赖的 SVG 生成在线趋势、业务积压、连接错误、HTTP 阶段和命令漏斗图表。
/// </summary>
internal static class DetailedReportChartBuilder
{
    /// <summary>
    /// 表示折线图中的单条命名序列及其 UTC 数据点。
    /// </summary>
    /// <param name="Name">图例名称。</param>
    /// <param name="Color">SVG 颜色。</param>
    /// <param name="Points">按时间排列的数据点。</param>
    private sealed record LineSeries(string Name, string Color, IReadOnlyList<(DateTimeOffset Time, double Value)> Points);

    /// <summary>
    /// 表示柱状图中的单个命名数值。
    /// </summary>
    /// <param name="Name">横轴和图例名称。</param>
    /// <param name="Value">柱体数值。</param>
    /// <param name="Color">SVG 颜色。</param>
    private sealed record BarItem(string Name, double Value, string Color);

    private const int Width = 1100;
    private const int Height = 380;
    private const int Left = 78;
    private const int Right = 24;
    private const int Top = 54;
    private const int Bottom = 60;

    /// <summary>
    /// 根据统一分析模型生成全部图表。
    /// </summary>
    /// <param name="analysis">当前运行分析模型。</param>
    /// <returns>可落盘和内嵌的 SVG 集合。</returns>
    internal static DetailedReportCharts Build(DetailedReportAnalysis analysis)
    {
        var onlineSeries = new List<LineSeries>
        {
            new("客户端 TCP", "#2563eb", analysis.ClientTimeline.Select(item => (item.TimestampUtc, (double)item.TcpConnected)).ToArray()),
            new("协议在线", "#059669", analysis.ClientTimeline.Select(item => (item.TimestampUtc, (double)item.ProtocolOnline)).ToArray())
        };
        if (analysis.ServerTimeline.Count > 0)
        {
            onlineSeries.Add(new("服务物理连接", "#7c3aed", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.SocketConnections)).ToArray()));
            onlineSeries.Add(new("业务在线", "#dc2626", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.BusinessOnlineDevices)).ToArray()));
        }

        var gapSeries = analysis.ServerTimeline.Count == 0
            ? []
            : new[]
            {
                new LineSeries("绑定未建业务线路", "#dc2626", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.BoundWithoutBusinessLine)).ToArray()),
                new LineSeries("待处理登录", "#ea580c", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.PendingLogin)).ToArray()),
                new LineSeries("待处理心跳", "#ca8a04", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.PendingHeartbeat)).ToArray()),
                new LineSeries("接收管线", "#7c3aed", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.QueueDepth)).ToArray()),
                new LineSeries("待建命令线路", "#0891b2", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.PendingCommandLine)).ToArray()),
                new LineSeries("命令线路管线", "#4f46e5", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.CommandLineQueueDepth)).ToArray()),
                new LineSeries("待完成 ACK", "#be123c", analysis.ServerTimeline.Select(item => (item.TimestampUtc, (double)item.PendingConfirmSend)).ToArray())
            };
        var errorSeries = new[]
        {
            new LineSeries("累计连接失败", "#dc2626", analysis.ClientTimeline.Select(item => (item.TimestampUtc, (double)item.ConnectionFailed)).ToArray()),
            new LineSeries("AddressAlreadyInUse", "#7c3aed", analysis.ClientTimeline.Select(item => (item.TimestampUtc, (double)item.AddressAlreadyInUse)).ToArray())
        };
        var phaseBars = analysis.CommandPhases.SelectMany(item => new[]
        {
            new BarItem($"{item.Name}-成功", item.SucceededRequests, "#059669"),
            new BarItem($"{item.Name}-失败", item.FailedRequests, "#dc2626")
        }).ToArray();
        var metrics = analysis.ClientMetrics;
        var funnelBars = new[]
        {
            new BarItem("计划目标", metrics.ApiTargetsRequired, "#64748b"),
            new BarItem("HTTP发起", metrics.ApiRequestsStarted, "#2563eb"),
            new BarItem("API成功", metrics.ApiRequestsSucceeded, "#7c3aed"),
            new BarItem("设备收到", metrics.GatherReadingCommandsReceived, "#ea580c"),
            new BarItem("设备应答", metrics.GatherReadingCommandsResponded, "#059669")
        };
        return new DetailedReportCharts
        {
            OnlineTimeline = BuildLineChart("连接与在线时间线", onlineSeries, analysis.StartedAtUtc, analysis.EndedAtUtc),
            BusinessOnlineGap = BuildLineChart("登录慢路径与业务积压", gapSeries, analysis.StartedAtUtc, analysis.EndedAtUtc),
            ConnectionErrors = BuildLineChart("客户端连接失败累计趋势", errorSeries, analysis.StartedAtUtc, analysis.EndedAtUtc),
            ApiPhases = BuildBarChart("HTTP 指令阶段结果", phaseBars),
            CommandFunnel = BuildBarChart("命令链路数量漏斗", funnelBars)
        };
    }

    /// <summary>
    /// 生成带坐标轴、网格、图例和时间刻度的多序列折线 SVG。
    /// </summary>
    /// <param name="title">图表标题。</param>
    /// <param name="series">折线序列。</param>
    /// <param name="startedAtUtc">横轴开始时间。</param>
    /// <param name="endedAtUtc">横轴结束时间。</param>
    /// <returns>完整 SVG 文本；没有数据时输出明确占位图。</returns>
    private static string BuildLineChart(
        string title,
        IReadOnlyList<LineSeries> series,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc)
    {
        var available = series.Where(item => item.Points.Count > 0).ToArray();
        if (available.Length == 0)
            return BuildEmptyChart(title, "没有可用于绘图的数据");
        var maximum = Math.Max(1, available.SelectMany(item => item.Points).Max(item => item.Value));
        var spanMilliseconds = Math.Max(1, (endedAtUtc - startedAtUtc).TotalMilliseconds);
        var plotWidth = Width - Left - Right;
        var plotHeight = Height - Top - Bottom;
        var builder = BeginSvg(title);
        AppendGrid(builder, maximum, startedAtUtc, endedAtUtc);
        foreach (var item in available)
        {
            var points = Downsample(item.Points, 900)
                .Select(point =>
                {
                    var x = Left + Math.Clamp((point.Time - startedAtUtc).TotalMilliseconds / spanMilliseconds, 0, 1) * plotWidth;
                    var y = Top + plotHeight - Math.Clamp(point.Value / maximum, 0, 1) * plotHeight;
                    return $"{x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}";
                });
            builder.Append("<polyline fill=\"none\" stroke=\"").Append(item.Color)
                .Append("\" stroke-width=\"2.2\" points=\"").Append(string.Join(' ', points)).AppendLine("\"/>");
        }
        AppendLegend(builder, available.Select(item => (item.Name, item.Color)).ToArray());
        return EndSvg(builder);
    }

    /// <summary>
    /// 按最大点数等距抽样长时间序列，保留首尾点并控制 HTML 和 SVG 体积。
    /// </summary>
    /// <param name="points">原始有序点。</param>
    /// <param name="maximumPoints">最大输出点数。</param>
    /// <returns>抽样后的点集合。</returns>
    private static IReadOnlyList<(DateTimeOffset Time, double Value)> Downsample(
        IReadOnlyList<(DateTimeOffset Time, double Value)> points,
        int maximumPoints)
    {
        if (points.Count <= maximumPoints)
            return points;
        var result = new List<(DateTimeOffset Time, double Value)>(maximumPoints);
        var step = (points.Count - 1d) / (maximumPoints - 1);
        for (var index = 0; index < maximumPoints; index++)
            result.Add(points[(int)Math.Round(index * step)]);
        return result;
    }

    /// <summary>
    /// 生成适合阶段结果和命令漏斗的纵向条形 SVG。
    /// </summary>
    /// <param name="title">图表标题。</param>
    /// <param name="items">条形项目。</param>
    /// <returns>完整 SVG 文本。</returns>
    private static string BuildBarChart(string title, IReadOnlyList<BarItem> items)
    {
        if (items.Count == 0)
            return BuildEmptyChart(title, "本轮没有 HTTP 指令阶段");
        var visible = items.Count <= 20 ? items : items.Take(20).ToArray();
        var maximum = Math.Max(1, visible.Max(item => item.Value));
        var plotWidth = Width - Left - Right;
        var plotHeight = Height - Top - Bottom;
        var slot = plotWidth / visible.Count;
        var barWidth = Math.Max(6, slot * 0.62);
        var builder = BeginSvg(title);
        AppendGrid(builder, maximum, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false);
        for (var index = 0; index < visible.Count; index++)
        {
            var item = visible[index];
            var height = item.Value / maximum * plotHeight;
            var x = Left + index * slot + (slot - barWidth) / 2;
            var y = Top + plotHeight - height;
            builder.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y))
                .Append("\" width=\"").Append(F(barWidth)).Append("\" height=\"").Append(F(height))
                .Append("\" rx=\"3\" fill=\"").Append(item.Color).AppendLine("\"/>");
            builder.Append("<text x=\"").Append(F(x + barWidth / 2)).Append("\" y=\"").Append(F(Math.Max(Top + 12, y - 5)))
                .Append("\" text-anchor=\"middle\" class=\"value\">").Append(item.Value.ToString("N0", CultureInfo.InvariantCulture)).AppendLine("</text>");
            builder.Append("<text x=\"").Append(F(x + barWidth / 2)).Append("\" y=\"").Append(Height - 37)
                .Append("\" transform=\"rotate(25 ").Append(F(x + barWidth / 2)).Append(' ').Append(Height - 37)
                .Append(")\" class=\"axis\">").Append(Xml(item.Name.Length > 24 ? item.Name[..24] : item.Name)).AppendLine("</text>");
        }
        return EndSvg(builder);
    }

    /// <summary>
    /// 创建 SVG 头部、统一字体和标题。
    /// </summary>
    /// <param name="title">图表标题。</param>
    /// <returns>已写入头部的字符串构建器。</returns>
    private static StringBuilder BeginSvg(string title)
    {
        var builder = new StringBuilder();
        builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).AppendLine("\" role=\"img\">");
        builder.AppendLine("<style>.title{font:600 20px 'Segoe UI','Microsoft YaHei',sans-serif;fill:#0f172a}.axis{font:12px 'Segoe UI','Microsoft YaHei',sans-serif;fill:#475569}.value{font:11px 'Segoe UI','Microsoft YaHei',sans-serif;fill:#334155}.legend{font:12px 'Segoe UI','Microsoft YaHei',sans-serif;fill:#334155}.grid{stroke:#e2e8f0;stroke-width:1}</style>");
        builder.AppendLine("<rect width=\"100%\" height=\"100%\" rx=\"12\" fill=\"#ffffff\"/>");
        builder.Append("<text x=\"").Append(Left).Append("\" y=\"30\" class=\"title\">").Append(Xml(title)).AppendLine("</text>");
        return builder;
    }

    /// <summary>
    /// 追加纵横网格、数值刻度和可选时间刻度。
    /// </summary>
    /// <param name="builder">SVG 构建器。</param>
    /// <param name="maximum">纵轴最大值。</param>
    /// <param name="startedAtUtc">开始时间。</param>
    /// <param name="endedAtUtc">结束时间。</param>
    /// <param name="includeTimeAxis">是否显示时间横轴。</param>
    private static void AppendGrid(
        StringBuilder builder,
        double maximum,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        bool includeTimeAxis = true)
    {
        var plotWidth = Width - Left - Right;
        var plotHeight = Height - Top - Bottom;
        for (var index = 0; index <= 4; index++)
        {
            var y = Top + plotHeight * index / 4d;
            var value = maximum * (4 - index) / 4d;
            builder.Append("<line x1=\"").Append(Left).Append("\" y1=\"").Append(F(y)).Append("\" x2=\"").Append(Width - Right).Append("\" y2=\"").Append(F(y)).AppendLine("\" class=\"grid\"/>");
            builder.Append("<text x=\"").Append(Left - 10).Append("\" y=\"").Append(F(y + 4)).Append("\" text-anchor=\"end\" class=\"axis\">").Append(FormatAxisValue(value)).AppendLine("</text>");
        }
        if (!includeTimeAxis)
            return;
        for (var index = 0; index <= 4; index++)
        {
            var x = Left + plotWidth * index / 4d;
            var time = startedAtUtc + TimeSpan.FromTicks((endedAtUtc - startedAtUtc).Ticks * index / 4);
            builder.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(Height - 18)
                .Append("\" text-anchor=\"middle\" class=\"axis\">").Append(time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine("</text>");
        }
    }

    /// <summary>
    /// 在标题右侧追加颜色图例。
    /// </summary>
    /// <param name="builder">SVG 构建器。</param>
    /// <param name="items">图例名称和颜色。</param>
    private static void AppendLegend(StringBuilder builder, IReadOnlyList<(string Name, string Color)> items)
    {
        var x = Width - Right;
        foreach (var item in items.Reverse())
        {
            var textWidth = item.Name.Length * 13 + 28;
            x -= textWidth;
            builder.Append("<line x1=\"").Append(x).Append("\" y1=\"27\" x2=\"").Append(x + 18).Append("\" y2=\"27\" stroke=\"").Append(item.Color).AppendLine("\" stroke-width=\"3\"/>");
            builder.Append("<text x=\"").Append(x + 23).Append("\" y=\"31\" class=\"legend\">").Append(Xml(item.Name)).AppendLine("</text>");
        }
    }

    /// <summary>
    /// 生成无数据占位 SVG，确保 HTML 和 Markdown 链接始终有效。
    /// </summary>
    /// <param name="title">图表标题。</param>
    /// <param name="message">占位说明。</param>
    /// <returns>占位 SVG。</returns>
    private static string BuildEmptyChart(string title, string message)
    {
        var builder = BeginSvg(title);
        builder.Append("<text x=\"").Append(Width / 2).Append("\" y=\"").Append(Height / 2)
            .Append("\" text-anchor=\"middle\" class=\"axis\">").Append(Xml(message)).AppendLine("</text>");
        return EndSvg(builder);
    }

    /// <summary>
    /// 关闭 SVG 根元素并返回文本。
    /// </summary>
    /// <param name="builder">SVG 构建器。</param>
    /// <returns>完整 SVG。</returns>
    private static string EndSvg(StringBuilder builder)
    {
        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    /// <summary>
    /// 根据量级紧凑格式化纵轴数值。
    /// </summary>
    /// <param name="value">原始数值。</param>
    /// <returns>带 k/M 后缀的轴标签。</returns>
    private static string FormatAxisValue(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:F1}M",
        >= 1_000 => $"{value / 1_000:F1}k",
        _ => value.ToString("F0", CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// 使用固定小数位和不变区域格式化 SVG 坐标。
    /// </summary>
    /// <param name="value">坐标值。</param>
    /// <returns>SVG 数值文本。</returns>
    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// 转义 SVG 文本节点中的 XML 特殊字符。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>安全 XML 文本。</returns>
    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
