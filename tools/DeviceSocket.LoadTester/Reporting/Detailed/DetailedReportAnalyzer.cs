using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 读取 LoadTester 节点报告、客户端 JSONL 和 Gather.TcpCenter JSONL，生成可供多种受众模板复用的统一分析模型。
/// </summary>
internal static class DetailedReportAnalyzer
{
    /// <summary>
    /// 表示一个 LoadTester 分片的节点摘要及其按秒时间序列；<paramref name="Key"/> 用于多分片聚合时稳定区分节点。
    /// </summary>
    /// <param name="Key">节点或分片的稳定键。</param>
    /// <param name="Report">节点最终摘要。</param>
    /// <param name="Entries">节点时间序列，缺失文件时为空集合。</param>
    private sealed record NodeSeries(string Key, NodeReport Report, IReadOnlyList<TimeSeriesEntry> Entries);

    /// <summary>
    /// 表示从一行 TcpCenter JSONL 中读取的趋势指标和累计计数原始样本。
    /// </summary>
    /// <param name="Timeline">可直接展示的连接、队列和 ACK 趋势指标。</param>
    /// <param name="WorkerCount">业务管线固定 Worker 数量。</param>
    /// <param name="MaximumQueueDepth">服务进程启动以来记录的最大队列深度。</param>
    /// <param name="PipelineFailedTotal">业务管线累计失败数。</param>
    /// <param name="PipelineRejectedTotal">业务管线累计拒绝数。</param>
    /// <param name="AckedButRejectedTotal">已 ACK 但后续未进入业务管线的累计数量。</param>
    /// <param name="StaleBusinessTotal">慢业务遇到旧会话并丢弃的累计数量。</param>
    /// <param name="HeartbeatCoalescedTotal">被合并的重复心跳累计数量。</param>
    private sealed record ServerMetricSample(
        TcpCenterTimelinePoint Timeline,
        int WorkerCount,
        long MaximumQueueDepth,
        long PipelineFailedTotal,
        long PipelineRejectedTotal,
        long AckedButRejectedTotal,
        long StaleBusinessTotal,
        long HeartbeatCoalescedTotal);

    /// <summary>
    /// 表示一次运行完成输入发现和流式读取后的归一化数据及质量警告。
    /// </summary>
    /// <param name="Nodes">全部 LoadTester 分片数据。</param>
    /// <param name="ServerSamples">服务端指标原始样本；未提供或无法识别时为空。</param>
    /// <param name="ServerMetricsPath">实际采用的服务端指标路径；未找到时为空。</param>
    /// <param name="Warnings">读取过程中的数据缺失、坏行和自动识别警告。</param>
    private sealed record RunInputs(
        IReadOnlyList<NodeSeries> Nodes,
        IReadOnlyList<ServerMetricSample> ServerSamples,
        string? ServerMetricsPath,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// 分析当前结果和可选基线，并把关键差异写回当前分析模型。
    /// </summary>
    /// <param name="options">离线报告输入、阈值、时区和元数据参数。</param>
    /// <param name="cancellationToken">取消大文件流式读取的令牌。</param>
    /// <returns>包含当前事实、诊断、验收和可选基线对比的完整分析。</returns>
    internal static async Task<DetailedReportAnalysis> AnalyzeAsync(
        DetailedReportGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var timeZone = ResolveTimeZone(options.ServerTimeZoneId);
        var background = await DetailedReportBackgroundLoader.LoadAsync(
            options.BackgroundPath,
            cancellationToken);
        var current = await AnalyzeRunAsync(
            options.InputDirectory,
            options.ServerMetricsPath,
            options,
            background,
            timeZone,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(options.BaselineInputDirectory))
            return current;

        var baseline = await AnalyzeRunAsync(
            options.BaselineInputDirectory,
            options.BaselineServerMetricsPath,
            options with { Title = "基线分析", BackgroundPath = null },
            new DetailedReportBackgroundOptions(),
            timeZone,
            cancellationToken);
        return current with { Comparisons = BuildComparisons(baseline, current) };
    }

    /// <summary>
    /// 解析系统时区标识；<c>Local</c> 使用报告机本地时区，其他值交由系统时区数据库解析。
    /// </summary>
    /// <param name="timeZoneId">命令行提供的时区标识。</param>
    /// <returns>用于解释 TcpCenter 无偏移时间戳的时区。</returns>
    /// <exception cref="ArgumentException">系统不存在指定时区时抛出。</exception>
    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.Equals(timeZoneId, "Local", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException($"找不到服务端时区：{timeZoneId}", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException($"服务端时区数据无效：{timeZoneId}", exception);
        }
    }

    /// <summary>
    /// 分析一套当前或基线输入，不在该步骤创建任何输出文件。
    /// </summary>
    /// <param name="inputDirectory">LoadTester 结果目录。</param>
    /// <param name="serverMetricsPath">可选 TcpCenter 指标路径。</param>
    /// <param name="options">阈值和报告元数据。</param>
    /// <param name="background">当前运行采用的测试背景配置；基线分析使用空配置。</param>
    /// <param name="timeZone">解释服务端无时区时间戳的时区。</param>
    /// <param name="cancellationToken">取消文件读取的令牌。</param>
    /// <returns>单套运行的分析模型。</returns>
    private static async Task<DetailedReportAnalysis> AnalyzeRunAsync(
        string inputDirectory,
        string? serverMetricsPath,
        DetailedReportGenerationOptions options,
        DetailedReportBackgroundOptions background,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var inputs = await LoadInputsAsync(inputDirectory, serverMetricsPath, timeZone, cancellationToken);
        var reports = inputs.Nodes.Select(item => item.Report).ToArray();
        ValidateReports(reports);

        var startedAt = reports.Min(item => item.StartedAtUtc);
        var endedAt = reports.Max(item => item.EndedAtUtc);
        var clientTimeline = MergeClientTimeline(inputs.Nodes);
        var target = options.TargetDeviceCount ?? InferTargetDeviceCount(reports, clientTimeline);
        var clientMetrics = MetricSnapshotAggregator.Merge(reports.Select(item => item.Metrics));
        var serverWindow = SelectServerWindow(inputs.ServerSamples, startedAt, endedAt);
        var serverSummary = BuildServerSummary(serverWindow, inputs.ServerMetricsPath, target);
        var commandPhases = MergeCommandPhases(reports);
        var resources = BuildResourceSummary(inputs.Nodes);
        var warnings = inputs.Warnings.ToList();
        if (inputs.ServerMetricsPath is null)
            warnings.Add("未找到 TcpCenter 服务指标文件，业务在线、Pipeline 和服务进程稳定性无法验收。");
        else if (serverWindow.Count == 0)
            warnings.Add("TcpCenter 指标时间与 LoadTester 测试时间不重叠，请检查 --server-time-zone 参数和两台机器时钟。");

        var checks = BuildAcceptanceChecks(
            reports,
            clientTimeline,
            serverSummary,
            target,
            options);
        var findings = BuildFindings(clientMetrics, commandPhases, serverSummary, target, options);
        var title = FirstNonBlank(options.Title, background.Report.Title) ?? $"{reports[0].RunId} 负载测试详细报告";
        var environment = NormalizeMetadata(FirstNonBlank(options.Environment, background.Report.Environment));
        var serviceVersion = NormalizeMetadata(FirstNonBlank(options.ServiceVersion, background.Service.Version));
        var tester = NormalizeMetadata(FirstNonBlank(options.Tester, background.Report.Tester));
        var effectiveBackground = BuildBackground(
            options.BackgroundPath,
            background,
            reports[0],
            target,
            commandPhases.Count,
            serviceVersion);
        return new DetailedReportAnalysis
        {
            RunId = reports[0].RunId,
            Title = title,
            Environment = environment,
            ServiceVersion = serviceVersion,
            Tester = tester,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Background = effectiveBackground,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            NodeCount = reports.Length,
            DeviceCount = reports.Sum(item => item.LocalDeviceCount),
            TargetDeviceCount = target,
            ClientMetrics = clientMetrics,
            LoadGeneratorResources = resources,
            ClientTimeline = clientTimeline,
            ServerTimeline = serverWindow.Select(item => item.Timeline).ToArray(),
            Server = serverSummary,
            CommandPhases = commandPhases,
            AcceptanceChecks = checks,
            Findings = findings,
            DataWarnings = warnings,
            Comparisons = []
        };
    }

    /// <summary>
    /// 按“命令行、背景 JSON”顺序选取第一个非空文本，不负责填充最终占位值。
    /// </summary>
    /// <param name="values">按优先级从高到低排列的候选文本。</param>
    /// <returns>首个去除首尾空白后的文本；全部为空时返回 null。</returns>
    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// 把背景 JSON 与节点摘要中的场景、目标主机和协议事实合并为可直接展示的测试背景。
    /// </summary>
    /// <param name="backgroundPath">背景 JSON 输入路径。</param>
    /// <param name="options">已校验的背景 JSON 配置。</param>
    /// <param name="report">用于自动推断场景和目标服务的首个节点摘要。</param>
    /// <param name="targetDeviceCount">报告采用的目标设备数。</param>
    /// <param name="commandPhaseCount">合并后的 HTTP 指令阶段数量。</param>
    /// <param name="serviceVersion">命令行和背景 JSON 合并后的服务版本。</param>
    /// <returns>不存在空字符串且可序列化到 <c>analysis.json</c> 的背景模型。</returns>
    private static DetailedReportBackground BuildBackground(
        string? backgroundPath,
        DetailedReportBackgroundOptions options,
        NodeReport report,
        int targetDeviceCount,
        int commandPhaseCount,
        string serviceVersion)
    {
        var scenarioName = FirstNonBlank(options.Scenario.Name, report.Options.Scenario.Name);
        var scenarioDescription = FirstNonBlank(options.Scenario.Description) ??
            $"{targetDeviceCount:N0} 台 {report.Options.Target.Protocol} 虚拟设备，运行角色 {report.Options.Role}，" +
            $"TCP 阶段 {report.Options.Scenario.Phases.Count:N0} 个，HTTP 阶段 {commandPhaseCount:N0} 个。";
        var defaultServiceName = report.Options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherTcpCenter
            ? "Gather.TcpCenter"
            : "DeviceSocket Gateway";
        return new DetailedReportBackground
        {
            Source = string.IsNullOrWhiteSpace(backgroundPath) ? "自动推断" : Path.GetFullPath(backgroundPath),
            ScenarioName = NormalizeMetadata(scenarioName),
            ScenarioDescription = NormalizeMetadata(scenarioDescription),
            ServerName = NormalizeMetadata(FirstNonBlank(options.Server.Name, report.Options.Target.Host)),
            OperatingSystem = NormalizeMetadata(options.Server.OperatingSystem),
            Cpu = NormalizeMetadata(options.Server.Cpu),
            Memory = NormalizeMetadata(options.Server.Memory),
            Network = NormalizeMetadata(options.Server.Network),
            ServiceName = NormalizeMetadata(FirstNonBlank(options.Service.Name, defaultServiceName)),
            ServiceVersion = serviceVersion,
            CodeBranch = NormalizeMetadata(options.Service.CodeBranch),
            CodeCommit = NormalizeMetadata(options.Service.CodeCommit),
            ServiceParameters = options.Service.Parameters.Select(item => new DetailedReportParameter
            {
                Name = item.Name.Trim(),
                Value = item.Value.Trim()
            }).ToArray()
        };
    }

    /// <summary>
    /// 将空白报告元数据转换为明确占位文本，避免汇报模板出现空表格单元格。
    /// </summary>
    /// <param name="value">命令行提供的可选元数据。</param>
    /// <returns>原值或“未填写”。</returns>
    private static string NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未填写" : value.Trim();

    /// <summary>
    /// 发现并流式读取节点报告、节点时间序列和可选服务端指标。
    /// </summary>
    /// <param name="inputDirectory">单分片目录或运行根目录。</param>
    /// <param name="serverMetricsPath">显式服务端指标文件；为空时自动识别。</param>
    /// <param name="timeZone">服务端时间戳时区。</param>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    /// <returns>所有输入数据及数据质量警告。</returns>
    private static async Task<RunInputs> LoadInputsAsync(
        string inputDirectory,
        string? serverMetricsPath,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"LoadTester 结果目录不存在：{root}");
        var summaryPaths = Directory.GetFiles(root, "node-summary.json", SearchOption.AllDirectories)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (summaryPaths.Length == 0)
            throw new FileNotFoundException($"目录中没有找到 node-summary.json：{root}");

        var serializerOptions = CreateReadOptions();
        var warnings = new List<string>();
        var nodes = new List<NodeSeries>();
        foreach (var summaryPath in summaryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var summaryStream = File.OpenRead(summaryPath);
            var report = await JsonSerializer.DeserializeAsync<NodeReport>(
                summaryStream,
                serializerOptions,
                cancellationToken) ?? throw new JsonException($"节点报告为空：{summaryPath}");
            var seriesPath = Path.Combine(Path.GetDirectoryName(summaryPath)!, "node-timeseries.jsonl");
            var entries = await ReadClientSeriesAsync(seriesPath, serializerOptions, warnings, cancellationToken);
            nodes.Add(new NodeSeries($"{report.ShardIndex:D3}:{report.NodeName}", report, entries));
        }

        var resolvedServerPath = ResolveServerMetricsPath(root, serverMetricsPath, warnings);
        var serverSamples = resolvedServerPath is null
            ? []
            : await ReadServerMetricsAsync(resolvedServerPath, timeZone, warnings, cancellationToken);
        return new RunInputs(nodes, serverSamples, resolvedServerPath, warnings);
    }

    /// <summary>
    /// 创建兼容旧报告和未来新增字段的只读 JSON 选项；离线分析不应因无关新增字段拒绝历史文件。
    /// </summary>
    /// <returns>节点报告和时间序列反序列化选项。</returns>
    private static JsonSerializerOptions CreateReadOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 流式读取单个分片的客户端时间序列，末行截断时保留此前有效数据并生成警告。
    /// </summary>
    /// <param name="path">节点时间序列文件路径。</param>
    /// <param name="serializerOptions">兼容报告版本的 JSON 选项。</param>
    /// <param name="warnings">追加数据质量警告的集合。</param>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    /// <returns>按文件顺序保留的有效采样点。</returns>
    private static async Task<IReadOnlyList<TimeSeriesEntry>> ReadClientSeriesAsync(
        string path,
        JsonSerializerOptions serializerOptions,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"缺少客户端时间序列：{path}");
            return [];
        }

        var entries = new List<TimeSeriesEntry>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        var invalidLines = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize<TimeSeriesEntry>(line, serializerOptions);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException exception)
            {
                invalidLines++;
                if (invalidLines <= 5)
                    warnings.Add($"客户端时间序列第 {lineNumber} 行无法解析，已跳过：{exception.Message}");
            }
        }
        if (invalidLines > 5)
            warnings.Add($"客户端时间序列共有 {invalidLines:N0} 行无法解析，仅展示前 5 条明细。");

        return entries;
    }

    /// <summary>
    /// 解析显式服务端指标路径，或在结果目录自动寻找包含 TcpCenter 指标特征字段的 JSONL。
    /// </summary>
    /// <param name="root">LoadTester 结果根目录。</param>
    /// <param name="explicitPath">命令行显式路径。</param>
    /// <param name="warnings">追加自动识别歧义警告的集合。</param>
    /// <returns>服务端指标绝对路径；未找到时为空。</returns>
    private static string? ResolveServerMetricsPath(
        string root,
        string? explicitPath,
        ICollection<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"TcpCenter 指标文件不存在：{fullPath}", fullPath);
            return fullPath;
        }

        var candidates = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "node-timeseries.jsonl", StringComparison.OrdinalIgnoreCase))
            .Where(IsTcpCenterMetricsFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length > 1)
            warnings.Add($"自动识别到 {candidates.Length} 个 TcpCenter 指标文件，默认使用：{candidates[0]}");
        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// 读取候选 JSONL 首个非空行并检查稳定特征字段，避免把其他业务 JSONL 误当成 TcpCenter 指标。
    /// </summary>
    /// <param name="path">候选 JSONL 路径。</param>
    /// <returns>同时包含 HandlerName、SocketConnections 和 BusinessOnlineDevices 时返回真。</returns>
    private static bool IsTcpCenterMetricsFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        for (var index = 0; index < 5 && reader.ReadLine() is { } line; index++)
        {
            if (line.Contains("\"HandlerName\"", StringComparison.Ordinal) &&
                line.Contains("\"SocketConnections\"", StringComparison.Ordinal) &&
                line.Contains("\"BusinessOnlineDevices\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 流式读取 TcpCenter JSONL，将无偏移本地时间转换为 UTC，并保留当前分析所需的稳定字段。
    /// </summary>
    /// <param name="path">TcpCenter JSONL 路径。</param>
    /// <param name="timeZone">服务端时间戳所属时区。</param>
    /// <param name="warnings">追加截断行和字段错误警告的集合。</param>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    /// <returns>按时间排序的服务指标样本。</returns>
    private static async Task<IReadOnlyList<ServerMetricSample>> ReadServerMetricsAsync(
        string path,
        TimeZoneInfo timeZone,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var samples = new List<ServerMetricSample>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        var invalidLines = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                samples.Add(ParseServerSample(document.RootElement, timeZone));
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
            {
                invalidLines++;
                if (invalidLines <= 5)
                    warnings.Add($"TcpCenter 指标第 {lineNumber} 行无法解析，已跳过：{exception.Message}");
            }
        }
        if (invalidLines > 5)
            warnings.Add($"TcpCenter 指标共有 {invalidLines:N0} 行无法解析，仅展示前 5 条明细。");

        return samples.OrderBy(item => item.Timeline.TimestampUtc).ToArray();
    }

    /// <summary>
    /// 从一个 TcpCenter JSON 对象提取连接仪表、Pipeline 峰值和累计计数。
    /// </summary>
    /// <param name="root">单行 JSON 根对象。</param>
    /// <param name="timeZone">无偏移时间戳所属时区。</param>
    /// <returns>内部服务指标样本。</returns>
    private static ServerMetricSample ParseServerSample(JsonElement root, TimeZoneInfo timeZone)
    {
        var pipeline = root.TryGetProperty("Pipeline", out var pipelineElement) && pipelineElement.ValueKind == JsonValueKind.Object
            ? pipelineElement
            : default;
        var timeline = new TcpCenterTimelinePoint
        {
            TimestampUtc = ParseServerTimestamp(ReadString(root, "Timestamp"), timeZone),
            ProcessId = ReadInt(root, "ProcessId"),
            SocketConnections = ReadInt(root, "SocketConnections"),
            BoundDeviceConnections = ReadInt(root, "BoundDeviceConnections"),
            BusinessOnlineDevices = ReadInt(root, "BusinessOnlineDevices"),
            BoundWithoutBusinessLine = ReadInt(root, "BoundWithoutBusinessLine"),
            PendingLogin = ReadInt(root, "PendingLogin"),
            PendingHeartbeat = ReadInt(root, "PendingHeartbeat"),
            QueueDepth = ReadInt(pipeline, "QueueDepth"),
            MaxQueueAgeMilliseconds = ReadLong(pipeline, "MaxQueueAgeMs"),
            AckMaxElapsedMilliseconds = ReadLong(root, "AckMaxElapsedMs")
        };
        return new ServerMetricSample(
            timeline,
            ReadInt(pipeline, "WorkerCount"),
            ReadLong(pipeline, "MaxQueueDepth"),
            ReadCounterTotal(pipeline, "Failed"),
            ReadCounterTotal(pipeline, "Rejected"),
            ReadCounterTotal(root, "AckedButRejected"),
            ReadCounterTotal(root, "StaleBusiness"),
            ReadCounterTotal(root, "HeartbeatCoalesced"));
    }

    /// <summary>
    /// 解析 TcpCenter 时间戳；显式带偏移时尊重原偏移，否则按指定服务端时区转换。
    /// </summary>
    /// <param name="value">JSON 中的时间文本。</param>
    /// <param name="timeZone">无偏移时间所属时区。</param>
    /// <returns>归一化 UTC 时间。</returns>
    private static DateTimeOffset ParseServerTimestamp(string value, TimeZoneInfo timeZone)
    {
        if ((value.EndsWith('Z') || HasExplicitOffset(value)) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
        {
            return withOffset.ToUniversalTime();
        }
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
            throw new FormatException($"无法解析服务端时间：{value}");
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
    }

    /// <summary>
    /// 判断 ISO 时间文本在日期部分之后是否包含显式正负偏移。
    /// </summary>
    /// <param name="value">待检查的时间文本。</param>
    /// <returns>包含显式偏移时返回真。</returns>
    private static bool HasExplicitOffset(string value)
    {
        var timeSeparator = value.IndexOf('T');
        if (timeSeparator < 0)
            timeSeparator = value.IndexOf(' ');
        return timeSeparator >= 0 && value.AsSpan(timeSeparator + 1).IndexOfAny('+', '-') >= 0;
    }

    /// <summary>
    /// 从 JSON 对象读取必需字符串字段。
    /// </summary>
    /// <param name="element">JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <returns>非空字符串。</returns>
    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new FormatException($"字段 {name} 为空。")
            : throw new FormatException($"缺少字符串字段 {name}。");

    /// <summary>
    /// 从 JSON 对象读取可选整数，字段缺失时按零处理以兼容旧指标版本。
    /// </summary>
    /// <param name="element">JSON 对象；未定义元素会返回零。</param>
    /// <param name="name">字段名称。</param>
    /// <returns>整数值或零。</returns>
    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>
    /// 从 JSON 对象读取可选长整数，字段缺失时按零处理。
    /// </summary>
    /// <param name="element">JSON 对象；未定义元素会返回零。</param>
    /// <param name="name">字段名称。</param>
    /// <returns>长整数值或零。</returns>
    private static long ReadLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;

    /// <summary>
    /// 兼容 Pipeline 直接累计字段和通用指标 <c>{ Total, Interval }</c> 对象两种结构读取累计值。
    /// </summary>
    /// <param name="element">包含计数器的 JSON 对象。</param>
    /// <param name="name">计数器名称。</param>
    /// <returns>累计计数；字段缺失时为零。</returns>
    private static long ReadCounterTotal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var direct))
            return direct;
        return property.ValueKind == JsonValueKind.Object ? ReadLong(property, "Total") : 0;
    }

    /// <summary>
    /// 校验多分片报告属于同一运行编号且分片编号不重复，避免错误目录产生看似正常的聚合结论。
    /// </summary>
    /// <param name="reports">发现的节点报告。</param>
    private static void ValidateReports(IReadOnlyList<NodeReport> reports)
    {
        var runIds = reports.Select(item => item.RunId).Distinct(StringComparer.Ordinal).ToArray();
        if (runIds.Length != 1)
            throw new InvalidOperationException($"输入目录包含多个 RunId：{string.Join(", ", runIds)}");
        var duplicateShard = reports.GroupBy(item => item.ShardIndex).FirstOrDefault(group => group.Count() > 1);
        if (duplicateShard is not null)
            throw new InvalidOperationException($"分片编号 {duplicateShard.Key} 存在重复 node-summary.json。");
    }

    /// <summary>
    /// 按采样事件时间合并多分片瞬时值；每个事件使用其他节点最近一次样本，避免要求各节点毫秒级对齐。
    /// </summary>
    /// <param name="nodes">节点报告及时间序列。</param>
    /// <returns>按 UTC 时间递增的集群级客户端趋势。</returns>
    private static IReadOnlyList<ClientTimelinePoint> MergeClientTimeline(IReadOnlyList<NodeSeries> nodes)
    {
        var withEntries = nodes.Where(item => item.Entries.Count > 0).ToArray();
        if (withEntries.Length == 0)
            return [];
        var firstCompleteAt = withEntries.Max(item => item.Entries[0].Metrics.TimestampUtc);
        var latest = new Dictionary<string, TimeSeriesEntry>(StringComparer.Ordinal);
        var result = new List<ClientTimelinePoint>();
        var events = withEntries
            .SelectMany(node => node.Entries.Select(entry => (node.Key, Entry: entry)))
            .OrderBy(item => item.Entry.Metrics.TimestampUtc);
        foreach (var current in events)
        {
            latest[current.Key] = current.Entry;
            if (current.Entry.Metrics.TimestampUtc < firstCompleteAt || latest.Count < withEntries.Length)
                continue;
            result.Add(new ClientTimelinePoint
            {
                TimestampUtc = current.Entry.Metrics.TimestampUtc,
                ExpectedOnline = latest.Values.Sum(item => item.Metrics.ExpectedOnline),
                TcpConnected = latest.Values.Sum(item => item.Metrics.TcpConnected),
                ProtocolOnline = latest.Values.Sum(item => item.Metrics.ProtocolOnline),
                ConnectionFailed = latest.Values.Sum(item => item.Metrics.ConnectionFailed),
                AddressAlreadyInUse = latest.Values.Sum(item => item.Metrics.Errors.GetValueOrDefault("SOCKET_AddressAlreadyInUse"))
            });
        }

        return result;
    }

    /// <summary>
    /// 从场景期望在线峰值、测量快照和设备配置依次推断本轮容量目标。
    /// </summary>
    /// <param name="reports">节点报告。</param>
    /// <param name="timeline">聚合客户端时间序列。</param>
    /// <returns>正整数目标设备数。</returns>
    private static int InferTargetDeviceCount(
        IReadOnlyList<NodeReport> reports,
        IReadOnlyList<ClientTimelinePoint> timeline)
    {
        var expected = timeline.Count == 0 ? 0 : timeline.Max(item => item.ExpectedOnline);
        if (expected <= 0)
            expected = reports.Sum(item => item.Metrics.ExpectedOnline);
        if (expected <= 0)
            expected = reports.Max(item => item.Options.Devices.Count);
        return checked((int)Math.Max(1, expected));
    }

    /// <summary>
    /// 截取测试时间窗内的服务端样本，并保留开始前最后一个样本作为累计计数基线。
    /// </summary>
    /// <param name="samples">服务指标完整文件样本。</param>
    /// <param name="startedAtUtc">LoadTester 最早开始时间。</param>
    /// <param name="endedAtUtc">LoadTester 最晚结束时间。</param>
    /// <returns>包含可选基线点的时间窗样本。</returns>
    private static IReadOnlyList<ServerMetricSample> SelectServerWindow(
        IReadOnlyList<ServerMetricSample> samples,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc)
    {
        var within = samples
            .Where(item => item.Timeline.TimestampUtc >= startedAtUtc && item.Timeline.TimestampUtc <= endedAtUtc)
            .ToList();
        var baseline = samples.LastOrDefault(item => item.Timeline.TimestampUtc < startedAtUtc);
        if (baseline is not null)
            within.Insert(0, baseline);
        return within;
    }

    /// <summary>
    /// 汇总服务端峰值、累计计数增量和物理/业务在线目标里程碑。
    /// </summary>
    /// <param name="samples">测试窗口服务样本。</param>
    /// <param name="sourcePath">服务指标源路径。</param>
    /// <param name="target">目标设备数。</param>
    /// <returns>服务端摘要；没有时间窗样本时为空。</returns>
    private static TcpCenterAnalysisSummary? BuildServerSummary(
        IReadOnlyList<ServerMetricSample> samples,
        string? sourcePath,
        int target)
    {
        if (samples.Count == 0 || sourcePath is null)
            return null;
        var physicalAt = samples.FirstOrDefault(item => item.Timeline.SocketConnections >= target)?.Timeline.TimestampUtc;
        var businessAt = physicalAt is null
            ? null
            : samples.FirstOrDefault(item =>
                item.Timeline.TimestampUtc >= physicalAt && item.Timeline.BusinessOnlineDevices >= target)?.Timeline.TimestampUtc;
        return new TcpCenterAnalysisSummary
        {
            SourcePath = sourcePath,
            SampleCount = samples.Count,
            MaximumSocketConnections = samples.Max(item => item.Timeline.SocketConnections),
            MaximumBoundDeviceConnections = samples.Max(item => item.Timeline.BoundDeviceConnections),
            MaximumBusinessOnlineDevices = samples.Max(item => item.Timeline.BusinessOnlineDevices),
            MaximumBoundWithoutBusinessLine = samples.Max(item => item.Timeline.BoundWithoutBusinessLine),
            MaximumPendingLogin = samples.Max(item => item.Timeline.PendingLogin),
            MaximumPendingHeartbeat = samples.Max(item => item.Timeline.PendingHeartbeat),
            MaximumQueueDepth = samples.Max(item => Math.Max(item.MaximumQueueDepth, item.Timeline.QueueDepth)),
            MaximumQueueAgeMilliseconds = samples.Max(item => item.Timeline.MaxQueueAgeMilliseconds),
            MaximumAckElapsedMilliseconds = samples.Max(item => item.Timeline.AckMaxElapsedMilliseconds),
            WorkerCount = samples.Max(item => item.WorkerCount),
            PipelineFailed = CalculateCounterDelta(samples, item => item.PipelineFailedTotal),
            PipelineRejected = CalculateCounterDelta(samples, item => item.PipelineRejectedTotal),
            AckedButRejected = CalculateCounterDelta(samples, item => item.AckedButRejectedTotal),
            StaleBusiness = CalculateCounterDelta(samples, item => item.StaleBusinessTotal),
            HeartbeatCoalesced = CalculateCounterDelta(samples, item => item.HeartbeatCoalescedTotal),
            ProcessRestartCount = samples.Zip(samples.Skip(1)).Count(pair => pair.First.Timeline.ProcessId != pair.Second.Timeline.ProcessId),
            PhysicalTargetReachedAtUtc = physicalAt,
            BusinessTargetReachedAtUtc = businessAt,
            BusinessCatchupSeconds = physicalAt.HasValue && businessAt.HasValue
                ? (businessAt.Value - physicalAt.Value).TotalSeconds
                : null
        };
    }

    /// <summary>
    /// 计算可能跨进程重启的累计计数区间增量；计数回零时从新进程当前值重新累计。
    /// </summary>
    /// <param name="samples">按时间排列且首项可作为基线的服务样本。</param>
    /// <param name="selector">读取累计计数的函数。</param>
    /// <returns>测试窗口累计增量。</returns>
    private static long CalculateCounterDelta(
        IReadOnlyList<ServerMetricSample> samples,
        Func<ServerMetricSample, long> selector)
    {
        long total = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            var previous = selector(samples[index - 1]);
            var current = selector(samples[index]);
            var processChanged = samples[index - 1].Timeline.ProcessId != samples[index].Timeline.ProcessId;
            total += processChanged || current < previous ? current : current - previous;
        }

        return total;
    }

    /// <summary>
    /// 合并多分片同名 HTTP 指令阶段的计划量、请求结果和设备应答。
    /// </summary>
    /// <param name="reports">节点报告。</param>
    /// <returns>按最早开始时间排列的阶段摘要。</returns>
    private static IReadOnlyList<DetailedCommandPhaseSummary> MergeCommandPhases(IReadOnlyList<NodeReport> reports) =>
        reports.SelectMany(item => item.CommandPhases)
            .GroupBy(item => (item.Name, item.ShowResult))
            .Select(group => new DetailedCommandPhaseSummary
            {
                Name = group.Key.Name,
                ShowResult = group.Key.ShowResult,
                PlannedRequests = group.Sum(item => (long)item.PlannedRequests),
                StartedRequests = group.Sum(item => item.ApiRequestsStarted),
                SucceededRequests = group.Sum(item => item.ApiRequestsSucceeded),
                FailedRequests = group.Sum(item => item.ApiRequestsFailed),
                TimeoutRequests = group.Sum(item => item.ApiRequestTimeouts),
                SkippedOfflineRequests = group.Sum(item => item.ApiRequestsSkippedOffline),
                CommandsReceived = group.Sum(item => item.GatherReadingCommandsReceived),
                CommandsResponded = group.Sum(item => item.GatherReadingCommandsResponded),
                StartedAtUtc = group.Min(item => item.StartedAtUtc),
                EndedAtUtc = group.Max(item => item.EndedAtUtc)
            })
            .OrderBy(item => item.StartedAtUtc)
            .ToArray();

    /// <summary>
    /// 汇总所有客户端时间序列的资源峰值；缺少时间序列时使用节点最终资源快照兜底。
    /// </summary>
    /// <param name="nodes">节点报告和时间序列。</param>
    /// <returns>负载机资源峰值。</returns>
    private static LoadGeneratorResourceSummary BuildResourceSummary(IReadOnlyList<NodeSeries> nodes)
    {
        var resources = nodes.SelectMany(item => item.Entries.Select(entry => entry.Resources)).ToArray();
        if (resources.Length == 0)
            resources = nodes.Select(item => item.Report.Resources).ToArray();
        return new LoadGeneratorResourceSummary
        {
            MaximumCpuPercent = resources.Max(item => item.CpuPercent),
            MaximumWorkingSetBytes = resources.Max(item => item.WorkingSetBytes),
            MaximumManagedHeapBytes = resources.Max(item => item.ManagedHeapBytes),
            MaximumThreadPoolThreads = resources.Max(item => item.ThreadPoolThreads),
            MaximumPendingWorkItems = resources.Max(item => item.PendingWorkItems)
        };
    }

    /// <summary>
    /// 建立客户端原始验收、容量、业务追平、Pipeline 和进程稳定性三态验收项。
    /// </summary>
    /// <param name="reports">节点报告。</param>
    /// <param name="clientTimeline">客户端聚合趋势。</param>
    /// <param name="server">可选服务端摘要。</param>
    /// <param name="target">目标设备数。</param>
    /// <param name="options">报告阈值。</param>
    /// <returns>稳定顺序的验收项。</returns>
    private static IReadOnlyList<DetailedAcceptanceCheck> BuildAcceptanceChecks(
        IReadOnlyList<NodeReport> reports,
        IReadOnlyList<ClientTimelinePoint> clientTimeline,
        TcpCenterAnalysisSummary? server,
        int target,
        DetailedReportGenerationOptions options)
    {
        var originalPassed = reports.All(item => item.Acceptance.Passed && item.RunStatus == LoadTestRunStatus.Passed);
        var maxProtocol = clientTimeline.Count == 0
            ? reports.Sum(item => item.Metrics.ProtocolOnline)
            : clientTimeline.Max(item => item.ProtocolOnline);
        var checks = new List<DetailedAcceptanceCheck>
        {
            CreateCheck("LoadTester 原始验收", originalPassed, originalPassed ? "通过" : "失败", "全部分片通过", "沿用场景 JSON 中的原始验收规则和运行状态。"),
            CreateCheck("协议在线容量", maxProtocol >= target, maxProtocol.ToString("N0", CultureInfo.InvariantCulture), target.ToString("N0", CultureInfo.InvariantCulture), "使用客户端时间序列峰值，避免 cooldown 归零干扰。")
        };
        if (server is null)
        {
            checks.AddRange(
            [
                CreateUnavailableCheck("TcpCenter 物理连接容量"),
                CreateUnavailableCheck("TcpCenter 业务在线容量"),
                CreateUnavailableCheck("业务在线追平时间"),
                CreateUnavailableCheck("业务管线最大排队年龄"),
                CreateUnavailableCheck("ACK 最大耗时"),
                CreateUnavailableCheck("Pipeline 失败与拒绝"),
                CreateUnavailableCheck("TcpCenter 进程稳定性")
            ]);
            return checks;
        }

        checks.Add(CreateCheck("TcpCenter 物理连接容量", server.MaximumSocketConnections >= target, $"{server.MaximumSocketConnections:N0}", $">= {target:N0}", "来源：SocketConnections 峰值。"));
        checks.Add(CreateCheck("TcpCenter 业务在线容量", server.MaximumBusinessOnlineDevices >= target, $"{server.MaximumBusinessOnlineDevices:N0}", $">= {target:N0}", "来源：BusinessOnlineDevices 峰值。"));
        checks.Add(server.BusinessCatchupSeconds.HasValue
            ? CreateCheck("业务在线追平时间", server.BusinessCatchupSeconds <= options.MaximumBusinessCatchupSeconds, $"{server.BusinessCatchupSeconds:F1} 秒", $"<= {options.MaximumBusinessCatchupSeconds} 秒", "从 SocketConnections 首次达到目标到 BusinessOnlineDevices 首次达到目标。")
            : new DetailedAcceptanceCheck { Name = "业务在线追平时间", Status = DetailedAcceptanceStatus.Failed, Actual = "未追平", Expected = $"<= {options.MaximumBusinessCatchupSeconds} 秒", Evidence = "物理连接或业务在线未达到目标。" });
        checks.Add(CreateCheck("业务管线最大排队年龄", server.MaximumQueueAgeMilliseconds <= options.MaximumQueueAgeSeconds * 1000L, $"{server.MaximumQueueAgeMilliseconds / 1000d:F1} 秒", $"<= {options.MaximumQueueAgeSeconds} 秒", "来源：Pipeline.MaxQueueAgeMs 周期峰值。"));
        checks.Add(CreateCheck("ACK 最大耗时", server.MaximumAckElapsedMilliseconds <= options.MaximumAckMilliseconds, $"{server.MaximumAckElapsedMilliseconds:N0} ms", $"<= {options.MaximumAckMilliseconds:N0} ms", "来源：AckMaxElapsedMs；该值是组帧到 Socket Send 返回耗时。"));
        checks.Add(CreateCheck("Pipeline 失败与拒绝", server.PipelineFailed == 0 && server.PipelineRejected == 0 && server.AckedButRejected == 0, $"处理失败 {server.PipelineFailed:N0} / 容量拒绝 {server.PipelineRejected:N0} / ACK后拒绝 {server.AckedButRejected:N0}", "全部为 0", "使用测试窗口内累计计数增量。"));
        checks.Add(CreateCheck("TcpCenter 进程稳定性", server.ProcessRestartCount == 0, $"重启 {server.ProcessRestartCount:N0} 次", "0 次", "依据服务指标 ProcessId 在测试窗口内是否变化。"));
        return checks;
    }

    /// <summary>
    /// 创建具有通过或失败状态的验收项。
    /// </summary>
    /// <param name="name">验收项名称。</param>
    /// <param name="passed">是否满足规则。</param>
    /// <param name="actual">实际值文本。</param>
    /// <param name="expected">预期值文本。</param>
    /// <param name="evidence">判断依据。</param>
    /// <returns>三态验收项。</returns>
    private static DetailedAcceptanceCheck CreateCheck(
        string name,
        bool passed,
        string actual,
        string expected,
        string evidence) => new()
        {
            Name = name,
            Status = passed ? DetailedAcceptanceStatus.Passed : DetailedAcceptanceStatus.Failed,
            Actual = actual,
            Expected = expected,
            Evidence = evidence
        };

    /// <summary>
    /// 创建因服务指标缺失而不可评估的验收项。
    /// </summary>
    /// <param name="name">验收项名称。</param>
    /// <returns>不可评估验收项。</returns>
    private static DetailedAcceptanceCheck CreateUnavailableCheck(string name) => new()
    {
        Name = name,
        Status = DetailedAcceptanceStatus.Unavailable,
        Actual = "不可评估",
        Expected = "需要 TcpCenter 指标",
        Evidence = "未提供与测试时间重叠的服务端指标。"
    };

    /// <summary>
    /// 根据当前已经验证的指标关联规则生成自动诊断问题，并明确区分事实和高概率推断。
    /// </summary>
    /// <param name="metrics">客户端合并指标。</param>
    /// <param name="commandPhases">HTTP 阶段摘要。</param>
    /// <param name="server">可选服务端摘要。</param>
    /// <param name="target">目标设备数。</param>
    /// <param name="options">报告阈值。</param>
    /// <returns>按严重程度排序的问题列表。</returns>
    private static IReadOnlyList<DiagnosticFinding> BuildFindings(
        MetricSnapshot metrics,
        IReadOnlyList<DetailedCommandPhaseSummary> commandPhases,
        TcpCenterAnalysisSummary? server,
        int target,
        DetailedReportGenerationOptions options)
    {
        var findings = new List<DiagnosticFinding>();
        var addressInUse = metrics.Errors.GetValueOrDefault("SOCKET_AddressAlreadyInUse");
        if (addressInUse > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.P0,
                Title = "负载机临时端口资源耗尽",
                Evidence = $"SOCKET_AddressAlreadyInUse 累计 {addressInUse:N0} 次，占全部连接尝试的 {Rate(addressInUse, metrics.ConnectionAttempts):P2}。",
                Impact = "连接风暴阶段的失败主要受负载机源端口/TIME_WAIT 约束，不能直接归因于 TcpCenter 拒绝连接。",
                Recommendation = "对本地资源错误增加全局退避，并使用多个负载机或多个源 IP 复测重连风暴。",
                Confidence = DiagnosticConfidence.Fact
            });
        }

        var noBuffer = metrics.Errors.GetValueOrDefault("SOCKET_NoBufferSpaceAvailable");
        if (noBuffer > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.P1,
                Title = "负载机 Socket 缓冲资源出现饱和",
                Evidence = $"SOCKET_NoBufferSpaceAvailable 累计 {noBuffer:N0} 次。",
                Impact = "负载机已经接近本地网络资源上限，相关阶段的服务端容量结论需要降级解释。",
                Recommendation = "降低单机连接重试速率并检查负载机端口、句柄和网络栈资源。",
                Confidence = DiagnosticConfidence.Fact
            });
        }

        var synchronous = commandPhases.Where(item => item.ShowResult).ToArray();
        var syncStarted = synchronous.Sum(item => item.StartedRequests);
        var syncSucceeded = synchronous.Sum(item => item.SucceededRequests);
        if (syncStarted > 0 && Rate(syncSucceeded, syncStarted) < 0.95)
        {
            findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.P0,
                Title = "同步命令结果链路成功率异常",
                Evidence = $"showResult=true 阶段发起 {syncStarted:N0} 次，仅成功 {syncSucceeded:N0} 次，成功率 {Rate(syncSucceeded, syncStarted):P2}。",
                Impact = "即使设备已经收到并应答，业务 API 也可能长时间等待或返回失败，无法通过同步命令验收。",
                Recommendation = "优先检查 Gather.Control.Api 的结果订阅时序、每请求订阅资源和 cmdId 结果匹配。",
                Confidence = DiagnosticConfidence.HighProbability
            });
        }

        if (metrics.ApiRequestsSucceeded > 0 && metrics.GatherReadingCommandsReceived < metrics.ApiRequestsSucceeded * 0.8)
        {
            findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.P1,
                Title = "HTTP 成功与设备实际收到命令存在明显落差",
                Evidence = $"API 业务成功 {metrics.ApiRequestsSucceeded:N0}，模拟设备收到命令 {metrics.GatherReadingCommandsReceived:N0}，粗略覆盖率 {Rate(metrics.GatherReadingCommandsReceived, metrics.ApiRequestsSucceeded):P2}。",
                Impact = "异步 API 成功可能只表示成功入队，不能代表命令已经到达设备。",
                Recommendation = "增加 API 入队、TcpCenter 出队、Socket 发送、设备应答和结果发布的统一 cmdId 端到端指标。",
                Confidence = DiagnosticConfidence.HighProbability
            });
        }

        if (server is not null)
        {
            if (server.MaximumBusinessOnlineDevices < target || server.BusinessCatchupSeconds is null)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P0,
                    Title = "业务在线未达到测试目标",
                    Evidence = $"业务在线峰值 {server.MaximumBusinessOnlineDevices:N0}/{target:N0}，物理连接峰值 {server.MaximumSocketConnections:N0}。",
                    Impact = "协议登录成功不等于服务已经能够给全部设备下发命令。",
                    Recommendation = "检查设备 ID 缓存未命中返回、登录慢业务失败和 ConrLine 初始化。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }
            else if (server.BusinessCatchupSeconds > options.MaximumBusinessCatchupSeconds)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P1,
                    Title = "登录慢路径导致业务在线追平过慢",
                    Evidence = $"物理连接达到 {target:N0} 后，业务在线还需要 {server.BusinessCatchupSeconds:F1} 秒；门槛为 {options.MaximumBusinessCatchupSeconds} 秒。",
                    Impact = "爬升结束后立即下发命令会命中尚未建立业务线路的设备。",
                    Recommendation = "修复缓存未命中返回值，登录期间抑制心跳慢业务，并测量 Redis、HTTP、ConrLine 和 LongTask 分段耗时。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }

            if (server.MaximumQueueAgeMilliseconds > options.MaximumQueueAgeSeconds * 1000L)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P1,
                    Title = "TcpCenter 接收业务管线出现长时间积压",
                    Evidence = $"最大队列深度 {server.MaximumQueueDepth:N0}，最大队列年龄 {server.MaximumQueueAgeMilliseconds / 1000d:F1} 秒，Worker 数 {server.WorkerCount:N0}。",
                    Impact = "登录、心跳和命令应答慢业务会延迟执行，旧连接工作项可能在会话替换后才被处理。",
                    Recommendation = "先减少登录期间重复心跳和同步 I/O，再对 16/24/32 Worker 做阶梯测试。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }

            if (server.MaximumAckElapsedMilliseconds > options.MaximumAckMilliseconds)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P1,
                    Title = "服务端 ACK 最大耗时超过客户端超时门槛",
                    Evidence = $"AckMaxElapsedMs={server.MaximumAckElapsedMilliseconds:N0} ms，报告门槛={options.MaximumAckMilliseconds:N0} ms。",
                    Impact = "设备可能在服务端最终发送成功前已经按超时策略断线重连。",
                    Recommendation = "结合 PerfView ThreadTime 检查 Socket Send、线程调度和接收风暴期间的阻塞。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }

            if (server.PipelineFailed > 0 || server.PipelineRejected > 0 || server.AckedButRejected > 0)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P0,
                    Title = "TcpCenter 业务管线发生失败或拒绝",
                    Evidence = $"PipelineFailed={server.PipelineFailed:N0}，PipelineRejected={server.PipelineRejected:N0}，AckedButRejected={server.AckedButRejected:N0}。",
                    Impact = "已接收的登录、心跳或业务工作可能没有完成慢业务处理。",
                    Recommendation = "检查有界容量、异常日志和过载降级逻辑，禁止仅提高队列容量掩盖消费不足。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }

            if (server.ProcessRestartCount > 0)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.P0,
                    Title = "测试期间 TcpCenter 进程发生重启",
                    Evidence = $"服务指标 ProcessId 发生 {server.ProcessRestartCount:N0} 次变化。",
                    Impact = "本轮不能认定服务持续稳定运行。",
                    Recommendation = "结合 Windows 事件、服务日志和 dump 确认退出原因。",
                    Confidence = DiagnosticConfidence.Fact
                });
            }
        }

        return findings
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 安全计算比例，分母为零时返回零，避免报告出现 NaN 或无穷大。
    /// </summary>
    /// <param name="numerator">分子。</param>
    /// <param name="denominator">分母。</param>
    /// <returns>零到一附近的比例值。</returns>
    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    /// <summary>
    /// 生成基线与当前结果的核心容量、延迟、队列和命令成功率对比。
    /// </summary>
    /// <param name="baseline">改造前或基线分析。</param>
    /// <param name="current">当前分析。</param>
    /// <returns>可直接用于两种报告模板的比较项。</returns>
    private static IReadOnlyList<ReportComparisonItem> BuildComparisons(
        DetailedReportAnalysis baseline,
        DetailedReportAnalysis current)
    {
        var result = new List<ReportComparisonItem>
        {
            CompareRate("连接成功率", Rate(baseline.ClientMetrics.ConnectionSucceeded, baseline.ClientMetrics.ConnectionAttempts), Rate(current.ClientMetrics.ConnectionSucceeded, current.ClientMetrics.ConnectionAttempts), false),
            CompareRate("登录成功率", Rate(baseline.ClientMetrics.LoginSucceeded, baseline.ClientMetrics.LoginAttempts), Rate(current.ClientMetrics.LoginSucceeded, current.ClientMetrics.LoginAttempts), false),
            CompareNumber("协议在线峰值", baseline.ClientTimeline.DefaultIfEmpty().Max(item => item?.ProtocolOnline ?? 0), current.ClientTimeline.DefaultIfEmpty().Max(item => item?.ProtocolOnline ?? 0), false, "N0")
        };
        if (baseline.Server is not null && current.Server is not null)
        {
            result.Add(CompareNumber("业务在线峰值", baseline.Server.MaximumBusinessOnlineDevices, current.Server.MaximumBusinessOnlineDevices, false, "N0"));
            result.Add(CompareNullableNumber("业务在线追平", baseline.Server.BusinessCatchupSeconds, current.Server.BusinessCatchupSeconds, true, "F1", "秒"));
            result.Add(CompareNumber("最大队列年龄", baseline.Server.MaximumQueueAgeMilliseconds / 1000d, current.Server.MaximumQueueAgeMilliseconds / 1000d, true, "F1", "秒"));
            result.Add(CompareNumber("ACK 最大耗时", baseline.Server.MaximumAckElapsedMilliseconds, current.Server.MaximumAckElapsedMilliseconds, true, "N0", "ms"));
        }
        result.Add(CompareRate("HTTP 业务成功率", Rate(baseline.ClientMetrics.ApiRequestsSucceeded, baseline.ClientMetrics.ApiRequestsStarted), Rate(current.ClientMetrics.ApiRequestsSucceeded, current.ClientMetrics.ApiRequestsStarted), false));
        return result;
    }

    /// <summary>
    /// 创建百分比指标对比。
    /// </summary>
    /// <param name="name">指标名称。</param>
    /// <param name="baseline">基线比例。</param>
    /// <param name="current">当前比例。</param>
    /// <param name="lowerIsBetter">数值降低是否表示改善。</param>
    /// <returns>格式化比较项。</returns>
    private static ReportComparisonItem CompareRate(string name, double baseline, double current, bool lowerIsBetter) =>
        CompareNumber(name, baseline * 100, current * 100, lowerIsBetter, "F2", "%");

    /// <summary>
    /// 创建数值指标对比，并根据指标方向生成改善或退化说明。
    /// </summary>
    /// <param name="name">指标名称。</param>
    /// <param name="baseline">基线数值。</param>
    /// <param name="current">当前数值。</param>
    /// <param name="lowerIsBetter">数值降低是否表示改善。</param>
    /// <param name="format">数值格式。</param>
    /// <param name="unit">可选单位。</param>
    /// <returns>格式化比较项。</returns>
    private static ReportComparisonItem CompareNumber(
        string name,
        double baseline,
        double current,
        bool lowerIsBetter,
        string format,
        string unit = "")
    {
        var delta = current - baseline;
        var improved = lowerIsBetter ? delta < 0 : delta > 0;
        var unchanged = Math.Abs(delta) < 0.0001;
        var direction = unchanged ? "持平" : improved ? "改善" : "退化";
        double? percent = Math.Abs(baseline) < 0.0001 ? null : delta / Math.Abs(baseline);
        var change = percent.HasValue
            ? $"{direction} {Math.Abs(percent.Value):P2}"
            : $"{direction}，差值 {delta.ToString(format, CultureInfo.InvariantCulture)}{unit}";
        return new ReportComparisonItem
        {
            Name = name,
            Baseline = $"{baseline.ToString(format, CultureInfo.InvariantCulture)}{unit}",
            Current = $"{current.ToString(format, CultureInfo.InvariantCulture)}{unit}",
            Change = change
        };
    }

    /// <summary>
    /// 创建可能缺失的数值指标对比；任一侧不可用时明确说明而不伪造零值。
    /// </summary>
    /// <param name="name">指标名称。</param>
    /// <param name="baseline">可选基线值。</param>
    /// <param name="current">可选当前值。</param>
    /// <param name="lowerIsBetter">数值降低是否表示改善。</param>
    /// <param name="format">数值格式。</param>
    /// <param name="unit">可选单位。</param>
    /// <returns>比较项。</returns>
    private static ReportComparisonItem CompareNullableNumber(
        string name,
        double? baseline,
        double? current,
        bool lowerIsBetter,
        string format,
        string unit)
    {
        if (baseline.HasValue && current.HasValue)
            return CompareNumber(name, baseline.Value, current.Value, lowerIsBetter, format, unit);
        return new ReportComparisonItem
        {
            Name = name,
            Baseline = baseline.HasValue ? $"{baseline.Value.ToString(format, CultureInfo.InvariantCulture)}{unit}" : "不可用",
            Current = current.HasValue ? $"{current.Value.ToString(format, CultureInfo.InvariantCulture)}{unit}" : "不可用",
            Change = "无法比较"
        };
    }
}
