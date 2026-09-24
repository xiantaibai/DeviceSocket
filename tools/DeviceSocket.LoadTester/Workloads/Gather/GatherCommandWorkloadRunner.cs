using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 使用独立 HttpClient 连接池和有界工作队列执行 GatherTcpCenter 或 GatherV4 指令 API 压测，
/// 并按阶段跟踪排空、熔断和逐请求失败明细。
/// </summary>
internal sealed class GatherCommandWorkloadRunner : IDisposable
{
    private readonly LoadTestOptions _options;
    private readonly IReadOnlyList<VirtualDeviceDescriptor> _devices;
    private readonly IReadOnlyList<GatherDeviceMeterMapping> _mappings;
    private readonly LoadTestMetrics _metrics;
    private readonly IDeviceOnlineRegistry? _onlineRegistry;
    private readonly GatherCommandTraceRegistry? _commandTraceRegistry;
    private readonly ICommandWorkloadAdapter _commandAdapter;
    private readonly HttpClient _httpClient;
    private readonly List<CommandPhaseResult> _results = [];
    private readonly ConcurrentQueue<HttpRequestErrorDetail> _errorDetails = new();
    private long _requestSequence;

    /// <summary>获取已经完成、跳过或失败的 HTTP 指令阶段结果。</summary>
    internal IReadOnlyList<CommandPhaseResult> Results => _results;

    /// <summary>获取当前节点产生的逐请求 HTTP 失败明细。</summary>
    internal IReadOnlyCollection<HttpRequestErrorDetail> ErrorDetails => _errorDetails.ToArray();

    /// <summary>获取逐 HTTP 请求与设备下行、应答关联后的追踪明细。</summary>
    internal IReadOnlyList<CommandTraceDetail> CommandTraces => _commandTraceRegistry?.GetTraces() ?? [];

    /// <summary>获取按报文指纹限量保存的未知或不支持下行样本。</summary>
    internal IReadOnlyList<DownstreamDiagnosticSample> DownstreamSamples => _commandTraceRegistry?.GetSamples() ?? [];

    /// <summary>获取下行分类、重复帧和未关联帧的节点级汇总；未启用诊断时为空。</summary>
    internal DownstreamDiagnosticSummary? DownstreamSummary => _commandTraceRegistry?.GetSummary();

    /// <summary>
    /// 创建 HTTP 指令运行器，并在任何 TCP 连接建立前校验当前分片的设备映射覆盖率。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="devices">当前分片负责的虚拟设备。</param>
    /// <param name="metrics">共享节点指标。</param>
    /// <param name="onlineRegistry">目标来源为 onlineRegistry 时使用的本节点设备在线注册表。</param>
    /// <param name="commandTraceRegistry">可选 HTTP 请求与设备下行关联器。</param>
    /// <exception cref="InvalidDataException">映射不能覆盖任一已配置指令阶段。</exception>
    /// <exception cref="InvalidOperationException">配置要求鉴权头但环境变量未提供密钥。</exception>
    internal GatherCommandWorkloadRunner(
        LoadTestOptions options,
        IReadOnlyList<VirtualDeviceDescriptor> devices,
        LoadTestMetrics metrics,
        IDeviceOnlineRegistry? onlineRegistry = null,
        GatherCommandTraceRegistry? commandTraceRegistry = null)
    {
        _options = options;
        _devices = devices;
        _metrics = metrics;
        _onlineRegistry = onlineRegistry;
        _commandTraceRegistry = commandTraceRegistry;
        _commandAdapter = CommandWorkloadAdapterFactory.Create(options);
        _mappings = GatherDeviceMeterMappingLoader.Load(options, devices);
        ValidateMappingCoverage();

        var maximumLocalConcurrency = options.CommandWorkload.Phases
            .Select(ResolveLocalConcurrency)
            .DefaultIfEmpty(1)
            .Max();
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maximumLocalConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(options.CommandWorkload.BaseUrl, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        ConfigureAuthorizationHeader();
    }

    /// <summary>
    /// 按配置顺序执行全部 HTTP 指令阶段；combined 角色先等待全局或阶段在线门槛，阶段失败仍保留可落盘结果。
    /// </summary>
    /// <param name="waitForOnline">true 时等待当前分片设备达到稳定在线门槛；apiOnly 角色传 false。</param>
    /// <param name="cancellationToken">取消整轮压测的令牌。</param>
    /// <returns>全部指令阶段完成的异步任务。</returns>
    internal async Task RunAsync(bool waitForOnline, CancellationToken cancellationToken)
    {
        if (_options.CommandWorkload.StartDelaySeconds > 0)
        {
            Console.WriteLine($"[HTTP等待] 额外等待 {_options.CommandWorkload.StartDelaySeconds} 秒后开始指令阶段。");
            await Task.Delay(TimeSpan.FromSeconds(_options.CommandWorkload.StartDelaySeconds), cancellationToken);
        }

        var skipNextPhase = false;
        foreach (var phase in _options.CommandWorkload.Phases)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var targetSelection = SelectPhaseTargets(phase);
            if (skipNextPhase)
            {
                skipNextPhase = false;
                const string reason = "上一阶段业务排空超时，按 drainFailureAction=skipNextPhase 跳过本阶段。";
                Console.WriteLine($"[HTTP阶段跳过] {phase.Name}：{reason}");
                AddResult(
                    phase,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    CommandPhaseStatus.Skipped,
                    reason,
                    OnlineGateResult.NotRequired,
                    targetSelection,
                    null,
                    null,
                    null,
                    DrainResult.NotRequired,
                    acceptancePassed: false);
                continue;
            }

            var gate = waitForOnline
                ? await WaitForStableOnlineAsync(phase, cancellationToken)
                : OnlineGateResult.NotRequired;
            targetSelection = SelectPhaseTargets(phase);
            if (_options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry)
                _metrics.ApiTargetsObserved(targetSelection.PlannedTargetCount, targetSelection.Targets.Count);

            if (gate.IsRequired && !gate.Satisfied)
            {
                if (phase.OnlineGateFailureAction == OnlineGateFailureAction.SkipPhase)
                {
                    Console.WriteLine($"[HTTP阶段跳过] {phase.Name}：{gate.FailureReason}");
                    AddResult(
                        phase,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        CommandPhaseStatus.Skipped,
                        gate.FailureReason,
                        gate,
                        targetSelection,
                        null,
                        null,
                        null,
                        DrainResult.NotRequired,
                        acceptancePassed: false);
                    continue;
                }

                if (phase.OnlineGateFailureAction == OnlineGateFailureAction.AbortRun)
                {
                    AddResult(
                        phase,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        CommandPhaseStatus.Failed,
                        gate.FailureReason,
                        gate,
                        targetSelection,
                        null,
                        null,
                        null,
                        DrainResult.NotRequired,
                        acceptancePassed: false);
                    throw new TimeoutException(gate.FailureReason);
                }

                Console.WriteLine($"[HTTP阶段强制执行] {phase.Name}：{gate.FailureReason}");
            }

            var before = _metrics.Snapshot(DateTimeOffset.UtcNow);
            var tracker = new PhaseExecutionTracker(phase);
            var resultWritten = false;
            try
            {
                await RunPhaseAsync(phase, targetSelection, tracker, cancellationToken);
                var drain = await WaitForDrainAsync(phase, before, tracker, cancellationToken);
                var after = _metrics.Snapshot(DateTimeOffset.UtcNow);
                var phaseAcceptance = EvaluatePhaseAcceptance(phase, tracker, drain);
                var failed = tracker.CircuitBreakerTripped || !drain.Satisfied || !phaseAcceptance.Passed;
                var reason = JoinReasons(
                    gate.Satisfied ? null : gate.FailureReason,
                    tracker.CircuitBreakerReason,
                    drain.FailureReason,
                    phaseAcceptance.FailureReason);
                AddResult(
                    phase,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    failed ? CommandPhaseStatus.Failed : CommandPhaseStatus.Completed,
                    reason,
                    gate,
                    targetSelection,
                    before,
                    after,
                    tracker,
                    drain,
                    phaseAcceptance.Passed && !tracker.CircuitBreakerTripped);
                resultWritten = true;

                if (!drain.Satisfied)
                {
                    skipNextPhase = phase.DrainFailureAction == CommandDrainFailureAction.SkipNextPhase;
                    if (phase.DrainFailureAction == CommandDrainFailureAction.AbortRun)
                        throw new TimeoutException(drain.FailureReason);
                }
                if (tracker.CircuitBreakerTripped &&
                    phase.CircuitBreakerAction == CommandCircuitBreakerAction.AbortRun)
                {
                    throw new InvalidOperationException(tracker.CircuitBreakerReason);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!resultWritten)
                {
                    AddResult(
                        phase,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        CommandPhaseStatus.Failed,
                        "测试被取消，当前 HTTP 指令阶段未完成。",
                        gate,
                        targetSelection,
                        before,
                        _metrics.Snapshot(DateTimeOffset.UtcNow),
                        tracker,
                        DrainResult.NotRequired,
                        acceptancePassed: false);
                }
                throw;
            }
            catch (Exception exception)
            {
                if (!resultWritten)
                {
                    AddResult(
                        phase,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        CommandPhaseStatus.Failed,
                        $"{exception.GetType().Name}: {exception.Message}",
                        gate,
                        targetSelection,
                        before,
                        _metrics.Snapshot(DateTimeOffset.UtcNow),
                        tracker,
                        DrainResult.NotRequired,
                        acceptancePassed: false);
                }
                throw;
            }

            // 固定暂停只用于给服务端恢复，不再纳入当前阶段业务增量和阶段耗时。
            if (phase.PauseAfterSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(phase.PauseAfterSeconds), cancellationToken);
        }
    }

    /// <summary>释放 HTTP 连接池；不会影响独立的 TCP 虚拟设备连接。</summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// 执行单个限速阶段；生产者负责速率，固定数量工作线程负责有界并发，保护器只停止生产新请求。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段。</param>
    /// <param name="targetSelection">在线门槛结束后一次性选出的本阶段目标快照。</param>
    /// <param name="tracker">当前阶段独立指标跟踪器。</param>
    /// <param name="cancellationToken">取消阶段的令牌。</param>
    /// <returns>当前阶段已经进入本地队列的请求全部完成的异步任务。</returns>
    private async Task RunPhaseAsync(
        CommandPhaseOptions phase,
        PhaseTargetSelection targetSelection,
        PhaseExecutionTracker tracker,
        CancellationToken cancellationToken)
    {
        var useOnlineRegistry = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry;
        var missingTargetCount = targetSelection.PlannedTargetCount - targetSelection.Targets.Count;
        if (useOnlineRegistry && missingTargetCount > 0)
        {
            _metrics.ApiRequestSkippedOffline(checked((long)missingTargetCount * phase.RequestsPerDevice));
        }
        if (targetSelection.Targets.Count == 0)
            return;

        var localConcurrency = ResolveLocalConcurrency(phase);
        var localRate = phase.RequestsPerSecond / (double)_options.Distributed.ShardCount;
        var plannedRequests = checked(targetSelection.PlannedTargetCount * phase.RequestsPerDevice);
        var selectedRequests = checked(targetSelection.Targets.Count * phase.RequestsPerDevice);
        Console.WriteLine(
            $"[HTTP阶段开始] {phase.Name}，计划目标 {targetSelection.PlannedTargetCount:N0}，" +
            $"已选在线目标 {targetSelection.Targets.Count:N0}，计划/候选请求 {plannedRequests:N0}/{selectedRequests:N0}，" +
            $"全局速率 {phase.RequestsPerSecond:N0}/s，全局并发 {phase.MaxConcurrency:N0}，show_result={phase.ShowResult}");

        var channel = Channel.CreateBounded<RequestWorkItem>(new BoundedChannelOptions(localConcurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = localConcurrency == 1
        });
        var workers = Enumerable.Range(0, localConcurrency)
            .Select(_ => ConsumeRequestsAsync(channel.Reader, phase, tracker, cancellationToken))
            .ToArray();
        var producer = ProduceRequestsAsync(
            channel.Writer,
            targetSelection.Targets,
            phase,
            localRate,
            useOnlineRegistry ? IsTargetEligible : _ => true,
            tracker,
            cancellationToken);
        await Task.WhenAll(workers.Prepend(producer));
        Console.WriteLine(
            $"[HTTP阶段发送结束] {phase.Name}，本阶段成功/失败/超时 " +
            $"{tracker.Succeeded:N0}/{tracker.Failed:N0}/{tracker.Timeouts:N0}，估算入队 {tracker.EstimatedEnqueued:N0}");
    }

    /// <summary>
    /// 按单设备连续写入请求；熔断后停止产生新请求，但保证有界队列内请求正常收尾。
    /// </summary>
    /// <param name="writer">有界请求通道写入端。</param>
    /// <param name="candidates">当前分片候选设备映射。</param>
    /// <param name="phase">当前阶段配置。</param>
    /// <param name="requestsPerSecond">当前分片分摊后的每秒速率。</param>
    /// <param name="isEligible">判断目标当前是否允许发送。</param>
    /// <param name="tracker">阶段独立指标跟踪器。</param>
    /// <param name="cancellationToken">取消生产的令牌。</param>
    /// <returns>生产结束并关闭通道的异步任务。</returns>
    private async Task ProduceRequestsAsync(
        ChannelWriter<RequestWorkItem> writer,
        IReadOnlyList<GatherDeviceMeterMapping> candidates,
        CommandPhaseOptions phase,
        double requestsPerSecond,
        Func<GatherDeviceMeterMapping, bool> isEligible,
        PhaseExecutionTracker tracker,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            long produced = 0;
            foreach (var target in candidates)
            {
                for (var repetition = 0; repetition < phase.RequestsPerDevice; repetition++)
                {
                    if (tracker.ShouldStop || tracker.TryTripDuration(stopwatch.Elapsed))
                        return;
                    var due = TimeSpan.FromSeconds(produced / requestsPerSecond);
                    var delay = due - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken);
                    if (tracker.ShouldStop || tracker.TryTripDuration(stopwatch.Elapsed))
                        return;
                    produced++;
                    if (!isEligible(target))
                    {
                        _metrics.ApiRequestSkippedOffline();
                        continue;
                    }

                    var sequence = Interlocked.Increment(ref _requestSequence);
                    await writer.WriteAsync(new RequestWorkItem(sequence, target), cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    /// <summary>
    /// 持续消费有界队列并发送服务端指令请求；设备在排队期间离线时会被发送前检查拦截。
    /// </summary>
    /// <param name="reader">有界请求通道读取端。</param>
    /// <param name="phase">当前阶段配置。</param>
    /// <param name="tracker">阶段独立指标跟踪器。</param>
    /// <param name="cancellationToken">取消消费的令牌。</param>
    /// <returns>通道完成且全部请求处理结束的异步任务。</returns>
    private async Task ConsumeRequestsAsync(
        ChannelReader<RequestWorkItem> reader,
        CommandPhaseOptions phase,
        PhaseExecutionTracker tracker,
        CancellationToken cancellationToken)
    {
        await foreach (var request in reader.ReadAllAsync(cancellationToken))
        {
            if (!IsTargetEligible(request.Mapping))
            {
                _metrics.ApiRequestSkippedOffline();
                continue;
            }

            await SendReadingRequestAsync(request, phase, tracker, cancellationToken);
        }
    }

    /// <summary>判断固定来源或在线注册表来源的目标在发送前是否仍有效。</summary>
    /// <param name="mapping">待发送的真实电表与虚拟集中器映射。</param>
    /// <returns>固定来源始终为 true；在线来源仅在设备仍在线时为 true。</returns>
    private bool IsTargetEligible(GatherDeviceMeterMapping mapping) =>
        _options.CommandWorkload.TargetDeviceSource != CommandTargetSource.OnlineRegistry ||
        _onlineRegistry?.IsOnline(mapping.Device.GlobalOrdinal) == true;

    /// <summary>
    /// 调用一次当前兼容配置对应的指令 API，同时记录全局指标、阶段指标和失败请求追踪字段。
    /// </summary>
    /// <param name="workItem">带节点内序号的请求目标。</param>
    /// <param name="phase">当前阶段配置。</param>
    /// <param name="tracker">阶段独立指标跟踪器。</param>
    /// <param name="cancellationToken">取消整轮压测的令牌。</param>
    /// <returns>请求完成的异步任务。</returns>
    private async Task SendReadingRequestAsync(
        RequestWorkItem workItem,
        CommandPhaseOptions phase,
        PhaseExecutionTracker tracker,
        CancellationToken cancellationToken)
    {
        var mapping = workItem.Mapping;
        // 在 HTTP 真正发出前登记期望命令，避免服务端极快完成下发时设备帧先于关联状态到达。
        var traceId = _commandTraceRegistry?.Register(
            phase.Name,
            workItem.Sequence,
            mapping,
            phase.ShowResult,
            DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.CommandWorkload.EndpointPath)
        {
            Content = new ByteArrayContent(_commandAdapter.CreateRequestPayload(mapping, phase, DateTimeOffset.Now))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandWorkload.RequestTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        tracker.RecordStarted();
        _metrics.ApiRequestStarted();
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var evaluation = _commandAdapter.EvaluateResponse(response.StatusCode, payload);
            _metrics.ApiRequestCompleted(evaluation.Succeeded, stopwatch.Elapsed, evaluation.ErrorCode);
            tracker.RecordCompleted(
                evaluation.Succeeded,
                timedOut: false,
                evaluation.PrevalidationRejected,
                evaluation.ErrorCode,
                stopwatch.Elapsed);
            if (traceId.HasValue)
            {
                _commandTraceRegistry?.RecordHttpCompleted(
                    traceId.Value,
                    DateTimeOffset.UtcNow,
                    (int)response.StatusCode,
                    evaluation.Succeeded,
                    evaluation.ErrorCode,
                    evaluation.PrevalidationRejected);
            }
            if (!evaluation.Succeeded)
            {
                AddErrorDetail(
                    workItem,
                    phase,
                    (int)response.StatusCode,
                    evaluation.ErrorCode ?? "API_UNKNOWN_ERROR",
                    evaluation.ErrorMessage,
                    stopwatch.Elapsed,
                    timedOut: false,
                    evaluation.PrevalidationRejected);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _metrics.ApiRequestTimedOut(stopwatch.Elapsed);
            tracker.RecordCompleted(false, timedOut: true, prevalidationRejected: false, "API_TIMEOUT", stopwatch.Elapsed);
            if (traceId.HasValue)
            {
                _commandTraceRegistry?.RecordHttpCompleted(
                    traceId.Value,
                    DateTimeOffset.UtcNow,
                    null,
                    succeeded: false,
                    "API_TIMEOUT",
                    prevalidationRejected: false);
            }
            AddErrorDetail(workItem, phase, null, "API_TIMEOUT", "请求超过单次超时上限。", stopwatch.Elapsed, true, false);
        }
        catch (HttpRequestException exception)
        {
            var errorCode = exception.HttpRequestError == HttpRequestError.NameResolutionError
                ? "API_DNS_ERROR"
                : "API_NETWORK_ERROR";
            _metrics.ApiRequestCompleted(false, stopwatch.Elapsed, errorCode);
            tracker.RecordCompleted(false, timedOut: false, prevalidationRejected: false, errorCode, stopwatch.Elapsed);
            if (traceId.HasValue)
            {
                _commandTraceRegistry?.RecordHttpCompleted(
                    traceId.Value,
                    DateTimeOffset.UtcNow,
                    null,
                    succeeded: false,
                    errorCode,
                    prevalidationRejected: false);
            }
            AddErrorDetail(workItem, phase, null, errorCode, exception.Message, stopwatch.Elapsed, false, false);
        }
    }

    /// <summary>把一次失败转换为稳定、长度受控的报告明细。</summary>
    /// <param name="workItem">失败请求。</param>
    /// <param name="phase">所属阶段。</param>
    /// <param name="httpStatusCode">可空 HTTP 状态码。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="errorMessage">原始简短错误信息。</param>
    /// <param name="elapsed">请求耗时。</param>
    /// <param name="timedOut">是否请求超时。</param>
    /// <param name="prevalidationRejected">是否明确预校验拒绝。</param>
    private void AddErrorDetail(
        RequestWorkItem workItem,
        CommandPhaseOptions phase,
        int? httpStatusCode,
        string errorCode,
        string? errorMessage,
        TimeSpan elapsed,
        bool timedOut,
        bool prevalidationRejected)
    {
        _errorDetails.Enqueue(new HttpRequestErrorDetail
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            PhaseName = phase.Name,
            MeterId = workItem.Mapping.MeterId,
            DeviceId = workItem.Mapping.Device.DeviceId,
            ShowResult = phase.ShowResult,
            RequestSequence = workItem.Sequence,
            HttpStatusCode = httpStatusCode,
            ErrorCode = errorCode,
            ErrorMessage = NormalizeErrorMessage(errorMessage),
            ElapsedMilliseconds = Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds)),
            TimedOut = timedOut,
            PrevalidationRejected = prevalidationRejected,
            ShardIndex = _options.Distributed.ShardIndex
        });
    }

    /// <summary>清理错误消息中的换行并限制长度，避免服务端异常页无限放大报告。</summary>
    /// <param name="message">原始错误消息。</param>
    /// <returns>最多 500 个字符的单行消息；空白值返回 null。</returns>
    private static string? NormalizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    /// <summary>等待设备侧命令收到和应答指标达到门槛并连续静默。</summary>
    /// <param name="phase">当前阶段排空配置。</param>
    /// <param name="before">阶段请求开始前累计指标。</param>
    /// <param name="tracker">阶段 HTTP 独立指标。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>排空是否完成、等待耗时和失败原因。</returns>
    private async Task<DrainResult> WaitForDrainAsync(
        CommandPhaseOptions phase,
        MetricSnapshot before,
        PhaseExecutionTracker tracker,
        CancellationToken cancellationToken)
    {
        if (!phase.DrainEnabled)
            return DrainResult.NotRequired;
        if (tracker.EstimatedEnqueued == 0)
            return new DrainResult(true, true, TimeSpan.Zero, null);

        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(phase.DrainTimeoutSeconds);
        var lastRawDelivered = -1L;
        var lastRecognized = -1L;
        var lastResponded = -1L;
        var lastUnsupported = -1L;
        var lastDuplicates = -1L;
        var lastChangedAt = DateTimeOffset.UtcNow;
        var requiredRawDeliveryRate = _commandTraceRegistry is null
            ? Math.Max(phase.MinimumRawDeliveryRate, phase.MinimumDeliveryRate)
            : phase.MinimumRawDeliveryRate;
        Console.WriteLine(
            $"[HTTP排空] {phase.Name} 等待估算入队 {tracker.EstimatedEnqueued:N0} 条追平，" +
            $"原始下行率≥{requiredRawDeliveryRate:P1}，协议识别率≥{phase.MinimumDeliveryRate:P1}，" +
            $"应答率≥{phase.MinimumResponseRate:P1}，静默 {phase.QuietSeconds:N0} 秒。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = DateTimeOffset.UtcNow;
            var counters = ReadDrainCounters(phase.Name, before, now);
            if (counters.RawDelivered != lastRawDelivered ||
                counters.Recognized != lastRecognized ||
                counters.Responded != lastResponded ||
                counters.Unsupported != lastUnsupported ||
                counters.DuplicateFrames != lastDuplicates)
            {
                lastRawDelivered = counters.RawDelivered;
                lastRecognized = counters.Recognized;
                lastResponded = counters.Responded;
                lastUnsupported = counters.Unsupported;
                lastDuplicates = counters.DuplicateFrames;
                lastChangedAt = now;
            }

            var rawDeliveryRate = Rate(
                Math.Min(counters.RawDelivered, tracker.EstimatedEnqueued),
                tracker.EstimatedEnqueued);
            var responseRate = Rate(counters.Responded, counters.Recognized);
            // 全部报文均为未知格式时不能生成业务响应；达到原始下行门槛并静默后结束排空，
            // 再由协议识别率验收明确失败，避免无意义地等满整个超时时间。
            var responseSatisfied = counters.Recognized == 0
                ? counters.RawDelivered > 0
                : responseRate >= phase.MinimumResponseRate;
            var thresholdsReached = rawDeliveryRate >= requiredRawDeliveryRate && responseSatisfied;
            if (thresholdsReached && now - lastChangedAt >= TimeSpan.FromSeconds(phase.QuietSeconds))
            {
                stopwatch.Stop();
                Console.WriteLine(
                    $"[HTTP排空完成] {phase.Name}，原始下行/识别/不支持/应答/重复 " +
                    $"{counters.RawDelivered:N0}/{counters.Recognized:N0}/{counters.Unsupported:N0}/" +
                    $"{counters.Responded:N0}/{counters.DuplicateFrames:N0}，" +
                    $"原始下行/识别/应答率 {rawDeliveryRate:P2}/" +
                    $"{Rate(counters.Recognized, counters.RawDelivered):P2}/{responseRate:P2}，" +
                    $"等待 {stopwatch.Elapsed.TotalSeconds:F1} 秒。");
                return new DrainResult(true, true, stopwatch.Elapsed, null);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        stopwatch.Stop();
        var finalCounters = ReadDrainCounters(phase.Name, before, DateTimeOffset.UtcNow);
        var reason =
            $"业务排空超过 {phase.DrainTimeoutSeconds} 秒；估算入队/原始下行/协议识别/不支持/设备应答 " +
            $"{tracker.EstimatedEnqueued:N0}/{finalCounters.RawDelivered:N0}/{finalCounters.Recognized:N0}/" +
            $"{finalCounters.Unsupported:N0}/{finalCounters.Responded:N0}。";
        Console.WriteLine($"[HTTP排空超时] {phase.Name}：{reason}");
        return new DrainResult(true, false, stopwatch.Elapsed, reason);
    }

    /// <summary>计算阶段级 HTTP 成功率、P95 和排空门槛验收。</summary>
    /// <param name="phase">当前阶段规则。</param>
    /// <param name="tracker">阶段 HTTP 指标。</param>
    /// <param name="drain">阶段排空结果。</param>
    /// <returns>是否通过以及失败原因。</returns>
    private PhaseAcceptanceResult EvaluatePhaseAcceptance(
        CommandPhaseOptions phase,
        PhaseExecutionTracker tracker,
        DrainResult drain)
    {
        var failures = new List<string>();
        var apiSuccessRate = Rate(tracker.Succeeded, tracker.Started);
        if (phase.MinimumApiSuccessRate.HasValue && apiSuccessRate < phase.MinimumApiSuccessRate.Value)
        {
            failures.Add(
                $"阶段 HTTP {(phase.ShowResult ? "同步结果成功率" : "异步接受率")} {apiSuccessRate:P2} " +
                $"低于 {phase.MinimumApiSuccessRate.Value:P2}");
        }
        var p95 = tracker.Latency.Percentile(0.95);
        if (phase.MaximumP95Milliseconds.HasValue && p95 > phase.MaximumP95Milliseconds.Value)
            failures.Add($"阶段 HTTP P95 {p95:N0} ms 超过 {phase.MaximumP95Milliseconds.Value:N0} ms");
        if (phase.DrainEnabled && tracker.EstimatedEnqueued > 0)
        {
            var trace = _commandTraceRegistry?.GetPhaseSnapshot(phase.Name);
            if (trace is not null)
            {
                var recognitionRate = Rate(trace.Recognized, tracker.EstimatedEnqueued);
                if (recognitionRate < phase.MinimumDeliveryRate)
                {
                    failures.Add(
                        $"阶段协议识别率 {recognitionRate:P2} 低于 {phase.MinimumDeliveryRate:P2}，" +
                        $"收到但不支持 {trace.Unsupported:N0} 条");
                }
            }
        }
        if (drain.IsRequired && !drain.Satisfied)
            failures.Add("阶段业务排空未达到配置门槛");
        return new PhaseAcceptanceResult(failures.Count == 0, failures.Count == 0 ? null : string.Join("；", failures) + "。");
    }

    /// <summary>等待当前 HTTP 阶段要求的设备达到在线门槛并连续稳定指定时长。</summary>
    /// <param name="phase">当前阶段及在线门槛覆盖值。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>在线数、峰值和等待耗时。</returns>
    private async Task<OnlineGateResult> WaitForStableOnlineAsync(
        CommandPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        if (_options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry)
            return await WaitForOnlineRegistryAsync(phase, cancellationToken);

        var globalRequired = phase.MinimumOnlineCount ??
            _options.CommandWorkload.MinimumOnlineCount ??
            (int)Math.Ceiling(_options.Devices.Count * _options.CommandWorkload.RequiredOnlineRate);
        var required = _devices.Count(item => item.GlobalOrdinal < globalRequired);
        return await WaitForOnlineCountAsync(
            phase,
            required,
            _devices.Count,
            () => checked((int)_metrics.Snapshot(DateTimeOffset.UtcNow).ProtocolOnline),
            "本地设备",
            cancellationToken);
    }

    /// <summary>在 onlineRegistry 来源下等待本节点任意在线设备满足全局或阶段门槛。</summary>
    /// <param name="phase">当前阶段及在线门槛覆盖值。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>在线数、峰值和等待耗时。</returns>
    private Task<OnlineGateResult> WaitForOnlineRegistryAsync(
        CommandPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        var registry = _onlineRegistry ?? throw new InvalidOperationException("onlineRegistry 目标来源需要设备在线注册表。");
        var explicitGlobalMinimum = phase.MinimumOnlineCount ?? _options.CommandWorkload.MinimumOnlineCount;
        var globalTarget = ResolveGlobalTarget(phase);
        var globalRequired = explicitGlobalMinimum ??
            (int)Math.Ceiling(globalTarget * _options.CommandWorkload.RequiredOnlineRate);
        var required = _mappings.Count(item => item.Device.GlobalOrdinal < globalRequired);
        var localCapacity = explicitGlobalMinimum.HasValue
            ? _mappings.Count
            : _mappings.Count(item => item.Device.GlobalOrdinal < globalTarget);
        return WaitForOnlineCountAsync(
            phase,
            required,
            localCapacity,
            () => Math.Min(localCapacity, registry.GetOnlineOrdinalsSnapshot().Count),
            "可选在线设备",
            cancellationToken);
    }

    /// <summary>执行通用在线稳定等待循环，供固定目标和在线注册表目标复用。</summary>
    /// <param name="phase">当前阶段。</param>
    /// <param name="required">当前分片要求在线数。</param>
    /// <param name="capacity">当前分片可用容量。</param>
    /// <param name="readOnlineCount">读取当前在线数的函数。</param>
    /// <param name="label">控制台展示的在线来源名称。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>门槛观测结果。</returns>
    private async Task<OnlineGateResult> WaitForOnlineCountAsync(
        CommandPhaseOptions phase,
        int required,
        int capacity,
        Func<int> readOnlineCount,
        string label,
        CancellationToken cancellationToken)
    {
        if (required == 0)
            return OnlineGateResult.NotRequired;
        var stableSeconds = phase.OnlineStableSeconds ?? _options.CommandWorkload.OnlineStableSeconds;
        var waitTimeoutSeconds = phase.OnlineWaitTimeoutSeconds ?? _options.CommandWorkload.OnlineWaitTimeoutSeconds;
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitTimeoutSeconds);
        DateTimeOffset? stableSince = null;
        var maximumObserved = 0;
        Console.WriteLine(
            $"[HTTP等待] 阶段 {phase.Name} 等待{label} {required:N0}/{capacity:N0}，" +
            $"连续 {stableSeconds:N0} 秒，最长等待 {waitTimeoutSeconds:N0} 秒。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = DateTimeOffset.UtcNow;
            var online = readOnlineCount();
            maximumObserved = Math.Max(maximumObserved, online);
            stableSince = online >= required ? stableSince ?? now : null;
            if (stableSince.HasValue && now - stableSince.Value >= TimeSpan.FromSeconds(stableSeconds))
            {
                stopwatch.Stop();
                Console.WriteLine($"[HTTP等待完成] 阶段 {phase.Name} {label} {online:N0}/{capacity:N0}，开始指令压测。");
                return new OnlineGateResult(true, true, required, maximumObserved, stopwatch.Elapsed, null);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        stopwatch.Stop();
        var reason =
            $"等待设备稳定在线超过 {waitTimeoutSeconds} 秒；要求{label} {required:N0}/{capacity:N0} " +
            $"并连续稳定 {stableSeconds} 秒，实际峰值 {maximumObserved:N0}。";
        return new OnlineGateResult(true, false, required, maximumObserved, stopwatch.Elapsed, reason);
    }

    /// <summary>将阶段配置、在线门槛、独立 HTTP 指标和设备侧增量转换为报告结果。</summary>
    /// <param name="phase">当前阶段配置。</param>
    /// <param name="startedAtUtc">开始等待在线门槛的 UTC 时间。</param>
    /// <param name="endedAtUtc">排空结束、跳过或失败的 UTC 时间。</param>
    /// <param name="status">阶段最终状态。</param>
    /// <param name="statusReason">跳过、失败或风险执行说明。</param>
    /// <param name="gate">在线门槛观测。</param>
    /// <param name="targetSelection">阶段目标快照。</param>
    /// <param name="before">请求前累计指标。</param>
    /// <param name="after">排空后累计指标。</param>
    /// <param name="tracker">阶段 HTTP 独立指标。</param>
    /// <param name="drain">排空结果。</param>
    /// <param name="acceptancePassed">阶段级规则是否通过。</param>
    private void AddResult(
        CommandPhaseOptions phase,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CommandPhaseStatus status,
        string? statusReason,
        OnlineGateResult gate,
        PhaseTargetSelection targetSelection,
        MetricSnapshot? before,
        MetricSnapshot? after,
        PhaseExecutionTracker? tracker,
        DrainResult drain,
        bool acceptancePassed)
    {
        var received = Delta(after?.GatherReadingCommandsReceived, before?.GatherReadingCommandsReceived);
        var responded = Delta(after?.GatherReadingCommandsResponded, before?.GatherReadingCommandsResponded);
        var estimatedEnqueued = tracker?.EstimatedEnqueued ?? 0;
        var trace = _commandTraceRegistry?.GetPhaseSnapshot(phase.Name);
        var rawDelivered = trace?.RawDelivered ?? received;
        var recognized = trace?.Recognized ?? received;
        var correlatedResponded = trace?.Responded ?? responded;
        var unsupported = trace?.Unsupported ?? 0;
        var duplicateFrames = trace?.DuplicateFrames ?? 0;
        _results.Add(new CommandPhaseResult
        {
            Name = phase.Name,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = endedAtUtc,
            LocalDeviceCount = targetSelection.PlannedTargetCount,
            PlannedRequests = checked(targetSelection.PlannedTargetCount * phase.RequestsPerDevice),
            ShowResult = phase.ShowResult,
            Status = status,
            StatusReason = statusReason,
            RequiredOnlineCount = gate.RequiredOnlineCount,
            MaximumObservedOnlineCount = gate.MaximumObservedOnlineCount,
            OnlineGateWaitMilliseconds = (long)gate.WaitDuration.TotalMilliseconds,
            OnlineGateSatisfied = gate.IsRequired ? gate.Satisfied : null,
            ApiRequestsStarted = tracker?.Started ?? 0,
            ApiRequestsSucceeded = tracker?.Succeeded ?? 0,
            ApiRequestsFailed = tracker?.Failed ?? 0,
            ApiRequestTimeouts = tracker?.Timeouts ?? 0,
            ApiPrevalidationRejected = tracker?.PrevalidationRejected ?? 0,
            ApiEstimatedEnqueued = estimatedEnqueued,
            ApiBusinessAccepted = phase.ShowResult ? 0 : tracker?.Succeeded ?? 0,
            SyncResultsSucceeded = phase.ShowResult ? tracker?.Succeeded ?? 0 : 0,
            ApiRequestLatency = tracker?.Latency ?? new MergeableHistogram().Snapshot(),
            Errors = tracker?.Errors ?? new Dictionary<string, long>(),
            ApiTargetsRequired = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
                ? targetSelection.PlannedTargetCount
                : 0,
            ApiTargetsOnline = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
                ? targetSelection.Targets.Count
                : 0,
            ApiRequestsSkippedOffline = Delta(after?.ApiRequestsSkippedOffline, before?.ApiRequestsSkippedOffline),
            GatherReadingCommandsReceived = received,
            GatherReadingCommandsResponded = responded,
            RawDownstreamCommandsObserved = rawDelivered,
            ProtocolRecognizedCommands = recognized,
            UnsupportedCommands = unsupported,
            DuplicateDownstreamFrames = duplicateFrames,
            GatherReadingCommandsUnobserved = Math.Max(0, estimatedEnqueued - rawDelivered),
            RawDeliveryRate = Rate(Math.Min(rawDelivered, estimatedEnqueued), estimatedEnqueued),
            ProtocolRecognitionRate = Rate(recognized, rawDelivered),
            GatherReadingDeliveryRate = Rate(Math.Min(recognized, estimatedEnqueued), estimatedEnqueued),
            GatherReadingResponseRate = Rate(correlatedResponded, recognized),
            DrainEnabled = phase.DrainEnabled,
            DrainSatisfied = drain.IsRequired ? drain.Satisfied : null,
            DrainWaitMilliseconds = (long)drain.WaitDuration.TotalMilliseconds,
            CircuitBreakerTripped = tracker?.CircuitBreakerTripped ?? false,
            CircuitBreakerReason = tracker?.CircuitBreakerReason,
            MaximumObservedConsecutiveFailures = tracker?.MaximumObservedConsecutiveFailures ?? 0,
            AcceptancePassed = acceptancePassed
        });
    }

    /// <summary>
    /// 读取排空窗口的唯一原始下行、协议识别、应答和重试计数；关闭关联诊断时退回旧累计指标口径。
    /// </summary>
    /// <param name="phaseName">HTTP 阶段名称。</param>
    /// <param name="before">阶段开始前累计指标。</param>
    /// <param name="now">本次读取时刻。</param>
    /// <returns>用于排空进度判断的计数快照。</returns>
    private DrainCounters ReadDrainCounters(string phaseName, MetricSnapshot before, DateTimeOffset now)
    {
        if (_commandTraceRegistry is not null)
        {
            var trace = _commandTraceRegistry.GetPhaseSnapshot(phaseName);
            return new DrainCounters(
                trace.RawDelivered,
                trace.Recognized,
                trace.Responded,
                trace.Unsupported,
                trace.DuplicateFrames);
        }

        var snapshot = _metrics.Snapshot(now);
        var received = Delta(snapshot.GatherReadingCommandsReceived, before.GatherReadingCommandsReceived);
        return new DrainCounters(
            received,
            received,
            Delta(snapshot.GatherReadingCommandsResponded, before.GatherReadingCommandsResponded),
            0,
            0);
    }

    /// <summary>验证当前分片映射覆盖可能被阶段选中的全部设备序号。</summary>
    /// <exception cref="InvalidDataException">至少一个阶段缺少本地设备映射。</exception>
    private void ValidateMappingCoverage()
    {
        var mappedOrdinals = _mappings.Select(item => item.Device.GlobalOrdinal).ToHashSet();
        var maximumGlobalTarget = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
            ? _options.Devices.Count
            : _options.CommandWorkload.Phases.Select(ResolveGlobalTarget).DefaultIfEmpty(0).Max();
        var missing = _devices
            .Where(item => item.GlobalOrdinal < maximumGlobalTarget && !mappedOrdinals.Contains(item.GlobalOrdinal))
            .Take(5)
            .Select(item => item.DeviceId)
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"HTTP 指令目标的设备映射不完整，示例缺失：{string.Join(", ", missing)}。");
    }

    /// <summary>从环境变量读取鉴权值并设置默认请求头，密钥本身不会进入配置或报告。</summary>
    /// <exception cref="InvalidOperationException">配置了请求头和环境变量名但环境变量不存在。</exception>
    private void ConfigureAuthorizationHeader()
    {
        var headerName = _options.CommandWorkload.AuthorizationHeaderName;
        var variableName = _options.CommandWorkload.AuthorizationKeyEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(variableName))
            return;
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"未设置 HTTP 鉴权环境变量 {variableName}。");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(headerName, value);
    }

    /// <summary>把全局最大并发按分片数向上取整分摊。</summary>
    /// <param name="phase">当前阶段。</param>
    /// <returns>本分片工作线程数。</returns>
    private int ResolveLocalConcurrency(CommandPhaseOptions phase) =>
        Math.Max(1, (int)Math.Ceiling(phase.MaxConcurrency / (double)_options.Distributed.ShardCount));

    /// <summary>计算阶段使用的全局设备序号上界，零表示全部设备。</summary>
    /// <param name="phase">当前阶段。</param>
    /// <returns>全局目标设备数量。</returns>
    private int ResolveGlobalTarget(CommandPhaseOptions phase) =>
        phase.TargetDeviceCount == 0 ? _options.Devices.Count : phase.TargetDeviceCount;

    /// <summary>在在线门槛结束后生成阶段目标快照。</summary>
    /// <param name="phase">当前阶段。</param>
    /// <returns>本分片计划目标数和实际目标。</returns>
    private PhaseTargetSelection SelectPhaseTargets(CommandPhaseOptions phase)
    {
        var globalTarget = ResolveGlobalTarget(phase);
        var fixedTargets = _mappings
            .Where(item => item.Device.GlobalOrdinal < globalTarget)
            .OrderBy(item => item.Device.GlobalOrdinal)
            .ToArray();
        if (_options.CommandWorkload.TargetDeviceSource != CommandTargetSource.OnlineRegistry)
            return new PhaseTargetSelection(fixedTargets.Length, fixedTargets);

        var registry = _onlineRegistry ?? throw new InvalidOperationException("onlineRegistry 目标来源需要设备在线注册表。");
        var onlineOrdinals = registry.GetOnlineOrdinalsSnapshot().ToHashSet();
        var onlineTargets = _mappings
            .Where(item => onlineOrdinals.Contains(item.Device.GlobalOrdinal))
            .OrderBy(item => item.Device.GlobalOrdinal)
            .Take(fixedTargets.Length)
            .ToArray();
        return new PhaseTargetSelection(fixedTargets.Length, onlineTargets);
    }

    /// <summary>计算两个累计指标的非负增量；任一值为空时返回零。</summary>
    /// <param name="after">结束累计值。</param>
    /// <param name="before">开始累计值。</param>
    /// <returns>非负增量。</returns>
    private static long Delta(long? after, long? before) =>
        after.HasValue && before.HasValue ? Math.Max(0, after.Value - before.Value) : 0;

    /// <summary>计算安全比例，分母不大于零时返回零。</summary>
    /// <param name="numerator">分子。</param>
    /// <param name="denominator">分母。</param>
    /// <returns>双精度比例。</returns>
    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    /// <summary>连接多个非空原因，避免阶段报告覆盖较早的门槛告警。</summary>
    /// <param name="reasons">可空原因列表。</param>
    /// <returns>使用中文分号连接的原因；全部为空时返回 null。</returns>
    private static string? JoinReasons(params string?[] reasons)
    {
        var available = reasons.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        return available.Length == 0 ? null : string.Join("；", available);
    }

    /// <summary>表示一个带节点内序号的 HTTP 请求工作项。</summary>
    /// <param name="Sequence">节点内单调请求序号。</param>
    /// <param name="Mapping">真实电表与模拟终端映射。</param>
    private sealed record RequestWorkItem(long Sequence, GatherDeviceMeterMapping Mapping);

    /// <summary>表示一个阶段的计划目标和实际目标快照。</summary>
    /// <param name="PlannedTargetCount">本节点计划目标数。</param>
    /// <param name="Targets">实际目标。</param>
    private sealed record PhaseTargetSelection(int PlannedTargetCount, IReadOnlyList<GatherDeviceMeterMapping> Targets);

    /// <summary>表示在线门槛观测结果。</summary>
    /// <param name="IsRequired">是否启用门槛。</param>
    /// <param name="Satisfied">是否满足。</param>
    /// <param name="RequiredOnlineCount">本节点要求在线数。</param>
    /// <param name="MaximumObservedOnlineCount">等待期峰值在线数。</param>
    /// <param name="WaitDuration">实际等待耗时。</param>
    /// <param name="FailureReason">失败原因。</param>
    private sealed record OnlineGateResult(
        bool IsRequired,
        bool Satisfied,
        int RequiredOnlineCount,
        int MaximumObservedOnlineCount,
        TimeSpan WaitDuration,
        string? FailureReason)
    {
        /// <summary>获取未启用在线门槛时使用的成功结果。</summary>
        internal static OnlineGateResult NotRequired { get; } = new(false, true, 0, 0, TimeSpan.Zero, null);
    }

    /// <summary>表示请求发送后的业务排空结果。</summary>
    /// <param name="IsRequired">是否启用排空。</param>
    /// <param name="Satisfied">是否满足。</param>
    /// <param name="WaitDuration">实际等待耗时。</param>
    /// <param name="FailureReason">失败原因。</param>
    private sealed record DrainResult(bool IsRequired, bool Satisfied, TimeSpan WaitDuration, string? FailureReason)
    {
        /// <summary>获取未启用排空时使用的成功结果。</summary>
        internal static DrainResult NotRequired { get; } = new(false, true, TimeSpan.Zero, null);
    }

    /// <summary>表示一次排空轮询使用的唯一命令计数。</summary>
    /// <param name="RawDelivered">已关联原始下行的命令数。</param>
    /// <param name="Recognized">协议已识别命令数。</param>
    /// <param name="Responded">已成功写出业务响应的命令数。</param>
    /// <param name="Unsupported">收到但当前不支持的命令数。</param>
    /// <param name="DuplicateFrames">重复或重试下行帧数。</param>
    private sealed record DrainCounters(
        long RawDelivered,
        long Recognized,
        long Responded,
        long Unsupported,
        long DuplicateFrames);

    /// <summary>表示阶段级规则计算结果。</summary>
    /// <param name="Passed">是否全部通过。</param>
    /// <param name="FailureReason">失败原因。</param>
    private sealed record PhaseAcceptanceResult(bool Passed, string? FailureReason);

    /// <summary>线程安全地维护一个 HTTP 阶段的请求计数、延迟、错误和保护状态。</summary>
    private sealed class PhaseExecutionTracker
    {
        private readonly object _sync = new();
        private readonly CommandPhaseOptions _phase;
        private readonly MergeableHistogram _latency = new();
        private readonly Dictionary<string, long> _errors = new(StringComparer.Ordinal);
        private long _started;
        private long _succeeded;
        private long _failed;
        private long _timeouts;
        private long _prevalidationRejected;
        private int _consecutiveFailures;
        private int _maximumConsecutiveFailures;
        private bool _circuitBreakerTripped;
        private string? _circuitBreakerReason;

        /// <summary>创建绑定阶段熔断条件的独立指标跟踪器。</summary>
        /// <param name="phase">当前阶段配置。</param>
        internal PhaseExecutionTracker(CommandPhaseOptions phase) => _phase = phase;

        /// <summary>获取发起请求数。</summary>
        internal long Started { get { lock (_sync) return _started; } }
        /// <summary>获取业务成功数。</summary>
        internal long Succeeded { get { lock (_sync) return _succeeded; } }
        /// <summary>获取失败数，包含超时。</summary>
        internal long Failed { get { lock (_sync) return _failed; } }
        /// <summary>获取超时数。</summary>
        internal long Timeouts { get { lock (_sync) return _timeouts; } }
        /// <summary>获取明确预校验拒绝数。</summary>
        internal long PrevalidationRejected { get { lock (_sync) return _prevalidationRejected; } }
        /// <summary>获取估算已入队数。</summary>
        internal long EstimatedEnqueued { get { lock (_sync) return Math.Max(0, _started - _prevalidationRejected); } }
        /// <summary>获取阶段延迟直方图快照。</summary>
        internal HistogramSnapshot Latency => _latency.Snapshot();
        /// <summary>获取阶段错误聚合快照。</summary>
        internal IReadOnlyDictionary<string, long> Errors { get { lock (_sync) return new Dictionary<string, long>(_errors); } }
        /// <summary>获取是否触发保护。</summary>
        internal bool CircuitBreakerTripped { get { lock (_sync) return _circuitBreakerTripped; } }
        /// <summary>获取保护触发原因。</summary>
        internal string? CircuitBreakerReason { get { lock (_sync) return _circuitBreakerReason; } }
        /// <summary>获取最大连续失败数。</summary>
        internal int MaximumObservedConsecutiveFailures { get { lock (_sync) return _maximumConsecutiveFailures; } }
        /// <summary>获取生产者是否应停止创建请求。</summary>
        internal bool ShouldStop => CircuitBreakerTripped;

        /// <summary>记录一个请求已发起。</summary>
        internal void RecordStarted()
        {
            lock (_sync)
                _started++;
        }

        /// <summary>记录请求结果并在启用时检查连续失败和超时率。</summary>
        /// <param name="succeeded">是否成功。</param>
        /// <param name="timedOut">是否超时。</param>
        /// <param name="prevalidationRejected">是否明确预校验拒绝。</param>
        /// <param name="errorCode">失败错误码。</param>
        /// <param name="elapsed">请求耗时。</param>
        internal void RecordCompleted(
            bool succeeded,
            bool timedOut,
            bool prevalidationRejected,
            string? errorCode,
            TimeSpan elapsed)
        {
            _latency.Record(elapsed);
            lock (_sync)
            {
                if (succeeded)
                {
                    _succeeded++;
                    _consecutiveFailures = 0;
                }
                else
                {
                    _failed++;
                    _consecutiveFailures++;
                    _maximumConsecutiveFailures = Math.Max(_maximumConsecutiveFailures, _consecutiveFailures);
                    if (!string.IsNullOrWhiteSpace(errorCode))
                        _errors[errorCode] = _errors.GetValueOrDefault(errorCode) + 1;
                }
                if (timedOut)
                    _timeouts++;
                if (prevalidationRejected)
                    _prevalidationRejected++;

                if (!_phase.CircuitBreakerEnabled || _circuitBreakerTripped)
                    return;
                if (_phase.MaximumConsecutiveFailures > 0 &&
                    _consecutiveFailures >= _phase.MaximumConsecutiveFailures)
                {
                    TripCore($"连续失败达到 {_consecutiveFailures:N0} 次保护阈值。");
                    return;
                }
                if (_phase.MaximumTimeoutRate.HasValue &&
                    _started >= _phase.CircuitBreakerMinimumRequests &&
                    _timeouts / (double)_started > _phase.MaximumTimeoutRate.Value)
                {
                    TripCore(
                        $"超时率 {_timeouts / (double)_started:P2} 超过 {_phase.MaximumTimeoutRate.Value:P2} 保护阈值。");
                }
            }
        }

        /// <summary>检查最大阶段时长并在超过时触发保护。</summary>
        /// <param name="elapsed">生产者已运行时长。</param>
        /// <returns>检查后是否已触发保护。</returns>
        internal bool TryTripDuration(TimeSpan elapsed)
        {
            lock (_sync)
            {
                if (!_phase.CircuitBreakerEnabled ||
                    !_phase.MaximumDurationSeconds.HasValue ||
                    _circuitBreakerTripped ||
                    elapsed < TimeSpan.FromSeconds(_phase.MaximumDurationSeconds.Value))
                {
                    return _circuitBreakerTripped;
                }
                TripCore($"阶段请求生产时长超过 {_phase.MaximumDurationSeconds.Value:N0} 秒保护阈值。");
                return true;
            }
        }

        /// <summary>在已经持有同步锁时保存首个熔断原因。</summary>
        /// <param name="reason">中文触发原因。</param>
        private void TripCore(string reason)
        {
            _circuitBreakerTripped = true;
            _circuitBreakerReason = reason;
        }
    }

}
