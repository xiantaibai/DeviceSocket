using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 使用独立 HttpClient 连接池和有界工作队列执行 Gather 读表 API 压测，不占用 TCP 会话接收循环。
/// </summary>
internal sealed class GatherCommandWorkloadRunner : IDisposable
{
    private readonly LoadTestOptions _options;
    private readonly IReadOnlyList<VirtualDeviceDescriptor> _devices;
    private readonly IReadOnlyList<GatherDeviceMeterMapping> _mappings;
    private readonly LoadTestMetrics _metrics;
    private readonly IDeviceOnlineRegistry? _onlineRegistry;
    private readonly HttpClient _httpClient;
    private readonly List<CommandPhaseResult> _results = [];

    /// <summary>
    /// 获取已经完成的 HTTP 指令阶段结果；测试结束后用于写入节点报告。
    /// </summary>
    internal IReadOnlyList<CommandPhaseResult> Results => _results;

    /// <summary>
    /// 创建 HTTP 指令运行器，并在任何 TCP 连接建立前校验当前分片的设备映射覆盖率。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="devices">当前分片负责的虚拟设备。</param>
    /// <param name="metrics">共享节点指标。</param>
    /// <param name="onlineRegistry">目标来源为 onlineRegistry 时使用的本节点设备在线注册表。</param>
    /// <exception cref="InvalidDataException">映射不能覆盖任一已配置指令阶段。</exception>
    /// <exception cref="InvalidOperationException">配置要求鉴权头但环境变量未提供密钥。</exception>
    internal GatherCommandWorkloadRunner(
        LoadTestOptions options,
        IReadOnlyList<VirtualDeviceDescriptor> devices,
        LoadTestMetrics metrics,
        IDeviceOnlineRegistry? onlineRegistry = null)
    {
        _options = options;
        _devices = devices;
        _metrics = metrics;
        _onlineRegistry = onlineRegistry;
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
    /// 按配置顺序执行全部 HTTP 指令阶段；combined 角色会在每个阶段开始前独立判断在线门槛，门槛失败默认记录跳过并继续。
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

        foreach (var phase in _options.CommandWorkload.Phases)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var gate = waitForOnline
                ? await WaitForStableOnlineAsync(phase, cancellationToken)
                : OnlineGateResult.NotRequired;
            var targetSelection = SelectPhaseTargets(phase);
            if (_options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry)
            {
                _metrics.ApiTargetsObserved(
                    targetSelection.PlannedTargetCount,
                    targetSelection.Targets.Count);
            }
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
                        null);
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
                        null);
                    throw new TimeoutException(gate.FailureReason);
                }

                Console.WriteLine($"[HTTP阶段强制执行] {phase.Name}：{gate.FailureReason}");
            }

            var before = _metrics.Snapshot(DateTimeOffset.UtcNow);
            try
            {
                await RunPhaseAsync(phase, targetSelection, cancellationToken);
                if (phase.PauseAfterSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(phase.PauseAfterSeconds), cancellationToken);
                AddResult(
                    phase,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    CommandPhaseStatus.Completed,
                    gate.Satisfied ? null : gate.FailureReason,
                    gate,
                    targetSelection,
                    before,
                    _metrics.Snapshot(DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                    _metrics.Snapshot(DateTimeOffset.UtcNow));
                throw;
            }
            catch (Exception exception)
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
                    _metrics.Snapshot(DateTimeOffset.UtcNow));
                throw;
            }
        }
    }

    /// <summary>
    /// 释放 HTTP 连接池；不会影响独立的 TCP 虚拟设备连接。
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// 执行单个限速阶段；生产者负责全局速率分摊，固定数量工作线程负责有界并发。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段。</param>
    /// <param name="targetSelection">在线门槛结束后一次性选出的本阶段目标快照。</param>
    /// <param name="cancellationToken">取消阶段的令牌。</param>
    /// <returns>当前阶段全部本地请求完成的异步任务。</returns>
    private async Task RunPhaseAsync(
        CommandPhaseOptions phase,
        PhaseTargetSelection targetSelection,
        CancellationToken cancellationToken)
    {
        var useOnlineRegistry = _options.CommandWorkload.TargetDeviceSource ==
            CommandTargetSource.OnlineRegistry;
        var missingTargetCount = targetSelection.PlannedTargetCount - targetSelection.Targets.Count;
        if (useOnlineRegistry && missingTargetCount > 0)
        {
            // 在线门槛允许按比例放行或强制执行，因此把快照时缺少的目标换算为计划请求缺口，避免 API 成功率掩盖覆盖不足。
            _metrics.ApiRequestSkippedOffline(
                checked((long)missingTargetCount * phase.RequestsPerDevice));
        }
        if (targetSelection.Targets.Count == 0)
            return;

        var localConcurrency = ResolveLocalConcurrency(phase);
        var localRate = phase.RequestsPerSecond / (double)_options.Distributed.ShardCount;
        var plannedRequests = checked(targetSelection.PlannedTargetCount * phase.RequestsPerDevice);
        var selectedRequests = checked(targetSelection.Targets.Count * phase.RequestsPerDevice);
        Console.WriteLine(
            $"[HTTP阶段开始] {phase.Name}，计划目标 {targetSelection.PlannedTargetCount:N0}，" +
            $"已选在线目标 {targetSelection.Targets.Count:N0}，计划/实际候选请求 {plannedRequests:N0}/{selectedRequests:N0}，" +
            $"全局速率 {phase.RequestsPerSecond:N0}/s，全局并发 {phase.MaxConcurrency:N0}，show_result={phase.ShowResult}");

        var channel = Channel.CreateBounded<GatherDeviceMeterMapping>(new BoundedChannelOptions(localConcurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = localConcurrency == 1
        });
        var workers = Enumerable.Range(0, localConcurrency)
            .Select(_ => ConsumeRequestsAsync(channel.Reader, phase, cancellationToken))
            .ToArray();
        var producer = ProduceRequestsAsync(
            channel.Writer,
            targetSelection.Targets,
            phase.RequestsPerDevice,
            localRate,
            useOnlineRegistry
                ? IsTargetEligible
                : _ => true,
            cancellationToken);
        await Task.WhenAll(workers.Prepend(producer));
        var snapshot = _metrics.Snapshot(DateTimeOffset.UtcNow);
        Console.WriteLine(
            $"[HTTP阶段结束] {phase.Name}，累计成功/失败/超时 " +
            $"{snapshot.ApiRequestsSucceeded:N0}/{snapshot.ApiRequestsFailed:N0}/{snapshot.ApiRequestTimeouts:N0}");
    }

    /// <summary>
    /// 按单设备连续写入 requestsPerDevice 条请求的顺序生产，使同一集中器在前一命令完成前形成真实排队压力。
    /// </summary>
    /// <param name="writer">有界请求通道写入端。</param>
    /// <param name="candidates">当前分片候选设备映射。</param>
    /// <param name="requestsPerDevice">每个设备请求次数。</param>
    /// <param name="requestsPerSecond">当前分片分摊后的每秒速率。</param>
    /// <param name="isEligible">判断候选设备当前是否允许发送；onlineRegistry 模式会拒绝已离线设备。</param>
    /// <param name="cancellationToken">取消生产的令牌。</param>
    /// <returns>全部请求写入通道的异步任务。</returns>
    private async Task ProduceRequestsAsync(
        ChannelWriter<GatherDeviceMeterMapping> writer,
        IReadOnlyList<GatherDeviceMeterMapping> candidates,
        int requestsPerDevice,
        double requestsPerSecond,
        Func<GatherDeviceMeterMapping, bool> isEligible,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            long produced = 0;
            foreach (var target in candidates)
            {
                for (var repetition = 0; repetition < requestsPerDevice; repetition++)
                {
                    var due = TimeSpan.FromSeconds(produced / requestsPerSecond);
                    var delay = due - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken);
                    produced++;
                    if (!isEligible(target))
                    {
                        _metrics.ApiRequestSkippedOffline();
                        continue;
                    }

                    await writer.WriteAsync(target, cancellationToken);
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
    /// 持续消费映射并发送读表请求，工作线程数量即本地最大在途 HTTP 请求数。
    /// </summary>
    /// <param name="reader">有界请求通道读取端。</param>
    /// <param name="phase">当前阶段请求参数。</param>
    /// <param name="cancellationToken">取消消费的令牌。</param>
    /// <returns>通道完成且全部请求处理结束的异步任务。</returns>
    private async Task ConsumeRequestsAsync(
        ChannelReader<GatherDeviceMeterMapping> reader,
        CommandPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        await foreach (var mapping in reader.ReadAllAsync(cancellationToken))
        {
            // 设备可能在进入有界队列后、真正取得 HTTP 并发槽之前掉线，因此发送前必须再次核对在线状态。
            if (!IsTargetEligible(mapping))
            {
                _metrics.ApiRequestSkippedOffline();
                continue;
            }

            await SendReadingRequestAsync(mapping, phase.ShowResult, cancellationToken);
        }
    }

    /// <summary>
    /// 判断目标在准备进入 HTTP 队列或即将发送时是否仍符合当前目标来源要求。
    /// </summary>
    /// <param name="mapping">待发送的真实电表与虚拟集中器映射。</param>
    /// <returns>固定序号来源始终返回 true；在线注册表来源仅在设备仍在线时返回 true。</returns>
    private bool IsTargetEligible(GatherDeviceMeterMapping mapping) =>
        _options.CommandWorkload.TargetDeviceSource != CommandTargetSource.OnlineRegistry ||
        _onlineRegistry?.IsOnline(mapping.Device.GlobalOrdinal) == true;

    /// <summary>
    /// 调用一次老读表 API，并同时检查 HTTP 状态码和响应 JSON 的 success 业务字段。
    /// </summary>
    /// <param name="mapping">目标真实电表与虚拟集中器映射。</param>
    /// <param name="showResult">是否要求 API 同步等待设备业务结果。</param>
    /// <param name="cancellationToken">取消整轮压测的令牌。</param>
    /// <returns>请求完成的异步任务。</returns>
    private async Task SendReadingRequestAsync(
        GatherDeviceMeterMapping mapping,
        bool showResult,
        CancellationToken cancellationToken)
    {
        var body = new ReadingRequest(
            showResult,
            new ReadingRequestData(
                mapping.MeterId,
                _options.CommandWorkload.OperatorUserId,
                _options.CommandWorkload.OperationSource,
                _options.CommandWorkload.ReadingModel,
                _options.CommandWorkload.ReadingCode));
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.CommandWorkload.EndpointPath)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandWorkload.RequestTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        _metrics.ApiRequestStarted();
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var result = EvaluateResponse(response.StatusCode, payload);
            _metrics.ApiRequestCompleted(result.Succeeded, stopwatch.Elapsed, result.ErrorCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _metrics.ApiRequestTimedOut(stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            var errorCode = exception.HttpRequestError == HttpRequestError.NameResolutionError
                ? "API_DNS_ERROR"
                : "API_NETWORK_ERROR";
            _metrics.ApiRequestCompleted(false, stopwatch.Elapsed, errorCode);
        }
    }

    /// <summary>
    /// 解析老 API 的 BaseDto 响应；只有 2xx 且 JSON success=true 才计为业务成功。
    /// </summary>
    /// <param name="statusCode">HTTP 响应状态码。</param>
    /// <param name="payload">完整响应体字节。</param>
    /// <returns>业务成功标志和稳定错误码。</returns>
    private static ApiEvaluation EvaluateResponse(HttpStatusCode statusCode, byte[] payload)
    {
        if ((int)statusCode is < 200 or >= 300)
            return new ApiEvaluation(false, $"API_HTTP_{(int)statusCode}");
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new ApiEvaluation(false, "API_INVALID_RESPONSE");
            }
            if (success.GetBoolean())
                return new ApiEvaluation(true, null);
            var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                ? value
                : 0;
            return new ApiEvaluation(false, $"API_BUSINESS_{code}");
        }
        catch (JsonException)
        {
            return new ApiEvaluation(false, "API_INVALID_JSON");
        }
    }

    /// <summary>
    /// 等待当前 HTTP 阶段要求的设备达到在线门槛并连续稳定指定时长；超时返回失败结果而不直接中断整轮测试。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段及其可选在线门槛覆盖值。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>包含是否满足、要求在线数、峰值在线数和等待耗时的门槛结果。</returns>
    private async Task<OnlineGateResult> WaitForStableOnlineAsync(
        CommandPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        if (_options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry)
            return await WaitForOnlineRegistryAsync(phase, cancellationToken);

        var globalRequired = phase.MinimumOnlineCount ??
            (int)Math.Ceiling(_options.Devices.Count * _options.CommandWorkload.RequiredOnlineRate);
        var required = _devices.Count(item => item.GlobalOrdinal < globalRequired);
        if (required == 0)
            return OnlineGateResult.NotRequired;

        var stableSeconds = phase.OnlineStableSeconds ?? _options.CommandWorkload.OnlineStableSeconds;
        var waitTimeoutSeconds = phase.OnlineWaitTimeoutSeconds ??
            _options.CommandWorkload.OnlineWaitTimeoutSeconds;
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitTimeoutSeconds);
        DateTimeOffset? stableSince = null;
        var maximumObservedOnline = 0;
        Console.WriteLine(
            $"[HTTP等待] 阶段 {phase.Name} 等待本地设备稳定在线 {required:N0}/{_devices.Count:N0}，" +
            $"连续 {stableSeconds:N0} 秒，最长等待 {waitTimeoutSeconds:N0} 秒。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = DateTimeOffset.UtcNow;
            var online = _metrics.Snapshot(now).ProtocolOnline;
            maximumObservedOnline = Math.Max(maximumObservedOnline, checked((int)online));
            stableSince = online >= required ? stableSince ?? now : null;
            if (stableSince.HasValue &&
                now - stableSince.Value >= TimeSpan.FromSeconds(stableSeconds))
            {
                stopwatch.Stop();
                Console.WriteLine(
                    $"[HTTP等待完成] 阶段 {phase.Name} 本地在线 {online:N0}/{_devices.Count:N0}，开始指令压测。");
                return new OnlineGateResult(
                    IsRequired: true,
                    Satisfied: true,
                    RequiredOnlineCount: required,
                    MaximumObservedOnlineCount: maximumObservedOnline,
                    WaitDuration: stopwatch.Elapsed,
                    FailureReason: null);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        stopwatch.Stop();
        var reason =
            $"等待设备稳定在线超过 {waitTimeoutSeconds} 秒；要求本地在线 {required:N0}/{_devices.Count:N0} " +
            $"并连续稳定 {stableSeconds} 秒，实际峰值 {maximumObservedOnline:N0}。";
        return new OnlineGateResult(
            IsRequired: true,
            Satisfied: false,
            RequiredOnlineCount: required,
            MaximumObservedOnlineCount: maximumObservedOnline,
            WaitDuration: stopwatch.Elapsed,
            FailureReason: reason);
    }

    /// <summary>
    /// 在 onlineRegistry 来源下等待本节点任意在线设备达到当前阶段的目标配额并连续稳定。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段及其在线门槛覆盖值。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>按本节点目标配额计算的在线门槛结果。</returns>
    private async Task<OnlineGateResult> WaitForOnlineRegistryAsync(
        CommandPhaseOptions phase,
        CancellationToken cancellationToken)
    {
        var registry = _onlineRegistry ??
            throw new InvalidOperationException("onlineRegistry 目标来源需要设备在线注册表。");
        var globalTarget = ResolveGlobalTarget(phase);
        var localTargetCount = _mappings.Count(item => item.Device.GlobalOrdinal < globalTarget);
        var globalRequired = phase.MinimumOnlineCount ??
            (int)Math.Ceiling(globalTarget * _options.CommandWorkload.RequiredOnlineRate);
        var required = _mappings.Count(item => item.Device.GlobalOrdinal < globalRequired);
        if (localTargetCount == 0 || required == 0)
            return OnlineGateResult.NotRequired;

        var stableSeconds = phase.OnlineStableSeconds ?? _options.CommandWorkload.OnlineStableSeconds;
        var waitTimeoutSeconds = phase.OnlineWaitTimeoutSeconds ??
            _options.CommandWorkload.OnlineWaitTimeoutSeconds;
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitTimeoutSeconds);
        DateTimeOffset? stableSince = null;
        var maximumObservedOnline = 0;
        Console.WriteLine(
            $"[HTTP等待] 阶段 {phase.Name} 等待可选在线设备 {required:N0}/{localTargetCount:N0}，" +
            $"连续 {stableSeconds:N0} 秒，最长等待 {waitTimeoutSeconds:N0} 秒。");
        while (DateTimeOffset.UtcNow < deadline)
        {
            var onlineOrdinals = registry.GetOnlineOrdinalsSnapshot().ToHashSet();
            // onlineRegistry 可以用任意当前在线设备补足阶段配额，不再局限于 GlobalOrdinal 小于 targetDeviceCount 的固定前缀。
            var online = Math.Min(
                localTargetCount,
                _mappings.Count(item => onlineOrdinals.Contains(item.Device.GlobalOrdinal)));
            maximumObservedOnline = Math.Max(maximumObservedOnline, online);
            stableSince = online >= required ? stableSince ?? DateTimeOffset.UtcNow : null;
            if (stableSince.HasValue &&
                DateTimeOffset.UtcNow - stableSince.Value >= TimeSpan.FromSeconds(stableSeconds))
            {
                stopwatch.Stop();
                Console.WriteLine(
                    $"[HTTP等待完成] 阶段 {phase.Name} 可选在线设备 {online:N0}/{localTargetCount:N0}，开始指令压测。");
                return new OnlineGateResult(
                    IsRequired: true,
                    Satisfied: true,
                    RequiredOnlineCount: required,
                    MaximumObservedOnlineCount: maximumObservedOnline,
                    WaitDuration: stopwatch.Elapsed,
                    FailureReason: null);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        stopwatch.Stop();
        var reason =
            $"等待可选在线设备超过 {waitTimeoutSeconds} 秒；要求本地在线配额 {required:N0}/{localTargetCount:N0} " +
            $"并连续稳定 {stableSeconds} 秒，实际峰值 {maximumObservedOnline:N0}。";
        return new OnlineGateResult(
            IsRequired: true,
            Satisfied: false,
            RequiredOnlineCount: required,
            MaximumObservedOnlineCount: maximumObservedOnline,
            WaitDuration: stopwatch.Elapsed,
            FailureReason: reason);
    }

    /// <summary>
    /// 将完成、跳过或失败阶段统一转换为报告结果，并按执行前后快照计算当前阶段的指标增量。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段配置。</param>
    /// <param name="startedAtUtc">开始等待在线门槛的 UTC 时间。</param>
    /// <param name="endedAtUtc">阶段结束、跳过或失败的 UTC 时间。</param>
    /// <param name="status">阶段最终状态。</param>
    /// <param name="statusReason">跳过、失败或强制执行说明。</param>
    /// <param name="gate">在线门槛观测结果。</param>
    /// <param name="targetSelection">当前阶段计划目标数和实际选出的设备快照。</param>
    /// <param name="before">实际执行请求前的指标；未执行请求时为空。</param>
    /// <param name="after">实际执行结束后的指标；未执行请求时为空。</param>
    private void AddResult(
        CommandPhaseOptions phase,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CommandPhaseStatus status,
        string? statusReason,
        OnlineGateResult gate,
        PhaseTargetSelection targetSelection,
        MetricSnapshot? before,
        MetricSnapshot? after)
    {
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
            ApiRequestsStarted = Delta(after?.ApiRequestsStarted, before?.ApiRequestsStarted),
            ApiRequestsSucceeded = Delta(after?.ApiRequestsSucceeded, before?.ApiRequestsSucceeded),
            ApiRequestsFailed = Delta(after?.ApiRequestsFailed, before?.ApiRequestsFailed),
            ApiRequestTimeouts = Delta(after?.ApiRequestTimeouts, before?.ApiRequestTimeouts),
            ApiTargetsRequired = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
                ? targetSelection.PlannedTargetCount
                : 0,
            ApiTargetsOnline = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
                ? targetSelection.Targets.Count
                : 0,
            ApiRequestsSkippedOffline = Delta(
                after?.ApiRequestsSkippedOffline,
                before?.ApiRequestsSkippedOffline),
            GatherReadingCommandsReceived = Delta(
                after?.GatherReadingCommandsReceived,
                before?.GatherReadingCommandsReceived),
            GatherReadingCommandsResponded = Delta(
                after?.GatherReadingCommandsResponded,
                before?.GatherReadingCommandsResponded)
        });
    }

    /// <summary>
    /// 计算可空累计计数器的非负阶段增量；阶段未实际执行时返回零。
    /// </summary>
    /// <param name="after">阶段结束累计值。</param>
    /// <param name="before">阶段开始累计值。</param>
    /// <returns>不小于零的阶段增量。</returns>
    private static long Delta(long? after, long? before) =>
        after.HasValue && before.HasValue ? Math.Max(0, after.Value - before.Value) : 0;

    /// <summary>
    /// 验证当前分片映射覆盖可能被阶段选中的全部设备序号，避免连接建立后才发现 meter_id 缺失。
    /// </summary>
    /// <exception cref="InvalidDataException">至少一个阶段缺少本地设备映射。</exception>
    private void ValidateMappingCoverage()
    {
        var mappedOrdinals = _mappings.Select(item => item.Device.GlobalOrdinal).ToHashSet();
        var phases = _options.CommandWorkload.Phases;
        var maximumGlobalTarget = _options.CommandWorkload.TargetDeviceSource == CommandTargetSource.OnlineRegistry
            ? _options.Devices.Count
            : phases.Select(ResolveGlobalTarget).DefaultIfEmpty(0).Max();
        var missing = _devices
            .Where(item => item.GlobalOrdinal < maximumGlobalTarget && !mappedOrdinals.Contains(item.GlobalOrdinal))
            .Take(5)
            .Select(item => item.DeviceId)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"HTTP 指令目标的设备映射不完整，示例缺失：{string.Join(", ", missing)}。");
        }
    }

    /// <summary>
    /// 从环境变量读取鉴权值并设置默认请求头，密钥本身不会进入配置对象或报告。
    /// </summary>
    /// <exception cref="InvalidOperationException">同时配置请求头和环境变量名但环境变量不存在。</exception>
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

    /// <summary>
    /// 把全局最大并发按分片数向上取整分摊，保证每个有请求的节点至少拥有一个工作线程。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段。</param>
    /// <returns>当前分片的工作线程数。</returns>
    private int ResolveLocalConcurrency(CommandPhaseOptions phase) =>
        Math.Max(1, (int)Math.Ceiling(phase.MaxConcurrency / (double)_options.Distributed.ShardCount));

    /// <summary>
    /// 计算阶段使用的全局设备序号上界，零值表示全部配置设备。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段。</param>
    /// <returns>需要参与阶段的全局设备数量。</returns>
    private int ResolveGlobalTarget(CommandPhaseOptions phase) =>
        phase.TargetDeviceCount == 0 ? _options.Devices.Count : phase.TargetDeviceCount;

    /// <summary>
    /// 在在线门槛结束后为当前阶段生成一次目标快照；在线来源可用任意当前在线设备补足本分片配额。
    /// </summary>
    /// <param name="phase">当前 HTTP 指令阶段。</param>
    /// <returns>本分片计划目标数以及按全局序号稳定排序的实际目标。</returns>
    private PhaseTargetSelection SelectPhaseTargets(CommandPhaseOptions phase)
    {
        var globalTarget = ResolveGlobalTarget(phase);
        var fixedTargets = _mappings
            .Where(item => item.Device.GlobalOrdinal < globalTarget)
            .OrderBy(item => item.Device.GlobalOrdinal)
            .ToArray();
        if (_options.CommandWorkload.TargetDeviceSource != CommandTargetSource.OnlineRegistry)
            return new PhaseTargetSelection(fixedTargets.Length, fixedTargets);

        var registry = _onlineRegistry ??
            throw new InvalidOperationException("onlineRegistry 目标来源需要设备在线注册表。");
        var onlineOrdinals = registry.GetOnlineOrdinalsSnapshot().ToHashSet();
        var onlineTargets = _mappings
            .Where(item => onlineOrdinals.Contains(item.Device.GlobalOrdinal))
            .OrderBy(item => item.Device.GlobalOrdinal)
            .Take(fixedTargets.Length)
            .ToArray();
        return new PhaseTargetSelection(fixedTargets.Length, onlineTargets);
    }

    /// <summary>
    /// 表示一次 API 响应的业务判定结果。
    /// </summary>
    /// <param name="Succeeded">是否同时满足 HTTP 与业务成功。</param>
    /// <param name="ErrorCode">失败时的稳定聚合错误码。</param>
    private sealed record ApiEvaluation(bool Succeeded, string? ErrorCode);

    /// <summary>
    /// 表示一个 HTTP 指令阶段的计划目标配额和一次性选出的实际目标快照。
    /// </summary>
    /// <param name="PlannedTargetCount">根据全局目标和分片规则换算出的本节点计划目标数。</param>
    /// <param name="Targets">固定来源的序号前缀，或在线来源在阶段开始时选出的在线设备。</param>
    private sealed record PhaseTargetSelection(
        int PlannedTargetCount,
        IReadOnlyList<GatherDeviceMeterMapping> Targets);

    /// <summary>
    /// 表示单个 HTTP 阶段开始前的在线门槛观测结果。
    /// </summary>
    /// <param name="IsRequired">当前阶段是否启用了在线门槛。</param>
    /// <param name="Satisfied">等待结束时是否满足在线数和连续稳定时长。</param>
    /// <param name="RequiredOnlineCount">当前分片要求的本地在线设备数。</param>
    /// <param name="MaximumObservedOnlineCount">等待期间观测到的最大本地在线设备数。</param>
    /// <param name="WaitDuration">在线门槛实际等待耗时。</param>
    /// <param name="FailureReason">门槛未满足时的中文诊断原因。</param>
    private sealed record OnlineGateResult(
        bool IsRequired,
        bool Satisfied,
        int RequiredOnlineCount,
        int MaximumObservedOnlineCount,
        TimeSpan WaitDuration,
        string? FailureReason)
    {
        /// <summary>
        /// 获取未启用在线门槛时使用的成功结果；apiOnly 或阶段最低在线数为零时不会等待。
        /// </summary>
        internal static OnlineGateResult NotRequired { get; } = new(
            IsRequired: false,
            Satisfied: true,
            RequiredOnlineCount: 0,
            MaximumObservedOnlineCount: 0,
            WaitDuration: TimeSpan.Zero,
            FailureReason: null);
    }

    /// <summary>
    /// 表示老接口要求的顶层 show_result 和 data 请求体。
    /// </summary>
    /// <param name="ShowResult">是否同步等待业务结果。</param>
    /// <param name="Data">读表参数。</param>
    private sealed record ReadingRequest(
        [property: JsonPropertyName("show_result")] bool ShowResult,
        [property: JsonPropertyName("data")] ReadingRequestData Data);

    /// <summary>
    /// 表示老 api/ammeter/reading 接口的 ReadingDto 字段。
    /// </summary>
    /// <param name="MeterId">真实电表主键。</param>
    /// <param name="OperatorUserId">操作人编号。</param>
    /// <param name="OperationSource">操作来源枚举值。</param>
    /// <param name="ReadingModel">抄读模式。</param>
    /// <param name="ReadingCode">读表代码。</param>
    private sealed record ReadingRequestData(
        [property: JsonPropertyName("meter_id")] int MeterId,
        [property: JsonPropertyName("oper_uid")] int OperatorUserId,
        [property: JsonPropertyName("oper_source")] int OperationSource,
        [property: JsonPropertyName("reading_model")] int ReadingModel,
        [property: JsonPropertyName("readingCode")] int ReadingCode);
}
