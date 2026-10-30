using System.Globalization;
using System.Text.Json;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>离线读取 Collector OTLP JSON/JSONL，仅接纳命令及资源低基数白名单，按实例、维度和时间去重，不读取 Redis。</summary>
internal static class GatherV4ServerEvidenceReader
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "gather.tcp.response.matched", "gather.tcp.response.unmatched", "gather.tcp.command.inflight",
        "gather.tcp.command.popped", "gather.tcp.command.send_accepted", "gather.tcp.command.send_failed",
        "gather.tcp.command.restored", "gather.tcp.command.mark_expired", "gather.tcp.command.mark_sessions",
        "gather.tcp.pipeline.pending", "gather.tcp.pipeline.rejected", "gather.tcp.redis.operations",
        "gather.tcp.bound.connections", "gather.tcp.socket.connections", "gather.tcp.process.cpu.utilization",
        "gather.tcp.process.working_set.bytes", "gather.tcp.process.private_memory.bytes", "gather.tcp.process.thread.count"
    };

    /// <summary>按有效压测窗口读取 OTLP 累计 Sum 与 Gauge；缺失、错误格式、Delta Sum 或无时间重叠明确报告。</summary>
    /// <param name="path">Collector file exporter JSONL 或单个标准 OTLP JSON 文件。</param>
    /// <param name="start">压测开始时间。</param>
    /// <param name="end">所有分片进入冷却前的截止时间。</param>
    /// <param name="warnings">接收数据质量告警。</param>
    /// <param name="cancellationToken">取消文件读取令牌。</param>
    /// <returns>有数据的白名单指标，缺失指标不生成伪造的零值。</returns>
    internal static async Task<IReadOnlyList<GatherV4ServerMetricEvidence>> ReadAsync(
        string path, DateTimeOffset start, DateTimeOffset end, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var series = new Dictionary<string, MetricSeries>(StringComparer.Ordinal);
        var invalid = 0;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            ReadDocument(document.RootElement, start, end, series, warnings);
        }
        else
        {
            using var reader = File.OpenText(path);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    ReadDocument(document.RootElement, start, end, series, warnings);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
                { invalid++; }
            }
        }
        if (invalid > 0) warnings.Add($"GatherV4 Collector 指标跳过 {invalid} 条截断或非法 JSON 行。");
        if (series.Count == 0) warnings.Add("GatherV4 OTLP 指标没有有效时间重叠或不含支持的命令指标，服务端任务完成不可评估。");
        return series.Values.Where(item => item.Points.Count > 0).GroupBy(item => item.Name).Select(group =>
        {
            var points = group.SelectMany(item => item.Points.Select(pair => (pair.Key, pair.Value)))
                .GroupBy(item => item.Key).OrderBy(item => item.Key)
                .Select(item => item.Sum(point => point.Value.Value)).ToArray();
            var increments = group.Select(ComputeIncrease).ToArray();
            return new GatherV4ServerMetricEvidence
            {
                Name = group.Key,
                Maximum = points.Max(),
                Last = group.Sum(item => item.Points.Last().Value.Value),
                Samples = group.Sum(item => item.Points.Count),
                Increase = group.All(item => item.Counter && item.Points.Count >= 2) ? increments.Sum(item => item.Increase) : null,
                CounterResets = increments.Sum(item => item.Resets)
            };
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>读取标准 OTLP resourceMetrics/scopemetrics/metrics，忽略无关资源和不受支持的高基数属性。</summary>
    /// <param name="root">一个 ExportMetricsServiceRequest 对象。</param>
    /// <param name="start">有效起点。</param>
    /// <param name="end">有效终点。</param>
    /// <param name="series">按服务实例及固定维度去重的序列。</param>
    /// <param name="warnings">质量告警。</param>
    private static void ReadDocument(JsonElement root, DateTimeOffset start, DateTimeOffset end,
        Dictionary<string, MetricSeries> series, ICollection<string> warnings)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resourceMetrics", out var resources)) return;
        foreach (var resource in resources.EnumerateArray())
        {
            var resourceAttributes = resource.TryGetProperty("resource", out var data) ? ReadAttributes(data) : new Dictionary<string, string>();
            var instance = resourceAttributes.GetValueOrDefault("service.instance.id", "unknown");
            var service = resourceAttributes.GetValueOrDefault("service.name", "unknown");
            if (!resource.TryGetProperty("scopeMetrics", out var scopes)) continue;
            foreach (var scope in scopes.EnumerateArray())
                foreach (var metric in scope.GetProperty("metrics").EnumerateArray())
                {
                    var name = metric.GetProperty("name").GetString()!;
                    if (!AllowedNames.Contains(name)) continue;
                    var counter = metric.TryGetProperty("sum", out var values);
                    if (!counter && !metric.TryGetProperty("gauge", out values)) continue;
                    if (counter && values.TryGetProperty("aggregationTemporality", out var temporality) &&
                        temporality.ToString() is not ("2" or "AGGREGATION_TEMPORALITY_CUMULATIVE"))
                    {
                        const string warning = "GatherV4 Counter 仅支持累计 temporality，已跳过 Delta Sum；请配置 Collector 导出 cumulative。";
                        if (!warnings.Contains(warning)) warnings.Add(warning);
                        continue;
                    }
                    foreach (var point in values.GetProperty("dataPoints").EnumerateArray())
                    {
                        if (!point.TryGetProperty("timeUnixNano", out var timestamp) ||
                            !decimal.TryParse(timestamp.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var nano)) continue;
                        var time = DateTimeOffset.FromUnixTimeMilliseconds((long)(nano / 1_000_000));
                        if (time < start || time > end) continue;
                        var attributes = ReadAttributes(point);
                        var operation = attributes.GetValueOrDefault("operation", "");
                        if (name == "gather.tcp.redis.operations" &&
                            (operation != "processing_complete" || attributes.GetValueOrDefault("result", "success") != "success")) continue;
                        var effectiveName = name == "gather.tcp.redis.operations" ? name + ".processing_complete" : name;
                        var labels = string.Join('|', attributes.Where(pair => pair.Key is "pipeline" or "queue" or "queue_type" or "operation")
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
                        var key = $"{service}|{instance}|{effectiveName}|{labels}";
                        if (!series.TryGetValue(key, out var target))
                        {
                            if (series.Count >= 4096) throw new InvalidDataException("GatherV4 指标序列超过 4096，拒绝无界高基数输入。");
                            target = new MetricSeries(effectiveName, counter);
                            series.Add(key, target);
                        }
                        var raw = point.TryGetProperty("asDouble", out var number) ? number : point.GetProperty("asInt");
                        if (!double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) continue;
                        if (target.Points.Count >= 100_000) throw new InvalidDataException("单条 GatherV4 指标超过十万采样点，请缩短导出时间范围。");
                        var startTime = point.TryGetProperty("startTimeUnixNano", out var since) ? since.ToString() : "";
                        target.Points[time.UtcTicks] = new MetricPoint(value, startTime);
                    }
                }
        }
    }

    /// <summary>读取 OTLP KeyValue 字符串/整型值，属性仅用于稳定分组，不写出资源凭据。</summary>
    /// <param name="element">包含 attributes 的 OTLP 对象。</param>
    /// <returns>属性键值。</returns>
    private static Dictionary<string, string> ReadAttributes(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty("attributes", out var attributes)) return result;
        foreach (var attribute in attributes.EnumerateArray())
        {
            var key = attribute.GetProperty("key").GetString()!;
            var value = attribute.GetProperty("value");
            if (value.TryGetProperty("stringValue", out var text)) result[key] = text.GetString() ?? "";
            else if (value.TryGetProperty("intValue", out text)) result[key] = text.ToString();
        }
        return result;
    }

    /// <summary>按单序列累计差值计数，进程重启时从新累计值重新计入，不产生负增量。</summary>
    /// <param name="series">按时间排序、已去重的指标序列。</param>
    /// <returns>区间增量及重启次数。</returns>
    private static (double Increase, int Resets) ComputeIncrease(MetricSeries series)
    {
        double increment = 0;
        var resets = 0;
        MetricPoint? previous = null;
        foreach (var point in series.Points.Values)
        {
            if (previous is not null && series.Counter)
            {
                var reset = point.Value < previous.Value || point.StartTime != previous.StartTime;
                increment += reset ? point.Value : point.Value - previous.Value;
                if (reset) resets++;
            }
            previous = point;
        }
        return (increment, resets);
    }

    /// <summary>保存一个固定指标维度的有界时间序列。</summary>
    /// <param name="name">公开指标名称。</param>
    /// <param name="counter">是否累计 Counter。</param>
    private sealed class MetricSeries(string name, bool counter)
    {
        /// <summary>获取指标名。</summary>
        internal string Name { get; } = name;
        /// <summary>获取是否累计计数。</summary>
        internal bool Counter { get; } = counter;
        /// <summary>获取去重的 UTC 时间点。</summary>
        internal SortedDictionary<long, MetricPoint> Points { get; } = [];
    }

    /// <summary>保存数值及累计起点，识别同实例 ID 下的服务重启。</summary>
    /// <param name="Value">数值。</param>
    /// <param name="StartTime">OTLP 累计起点。</param>
    private sealed record MetricPoint(double Value, string StartTime);
}
