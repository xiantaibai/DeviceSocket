using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 按设备 FIFO 关联 HTTP 期望命令与下行帧，并以报文指纹识别重试；全部集合均受计划请求数或样本上限约束。
/// </summary>
internal sealed class GatherCommandTraceRegistry
{
    private readonly DownstreamDiagnosticsOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<long, MutableTrace> _traces = [];
    private readonly Dictionary<string, DeviceTraceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sampleCounts = new(StringComparer.Ordinal);
    private readonly List<DownstreamDiagnosticSample> _samples = [];
    private readonly Dictionary<DownstreamClassificationKind, long> _classifications = [];
    private long _requestId;
    private long _unmatchedDownstreamFrames;
    private long _duplicateDownstreamFrames;

    /// <summary>
    /// 使用诊断配置创建关联器。
    /// </summary>
    /// <param name="options">样本数量、原始 Hex 长度和关联超时配置。</param>
    internal GatherCommandTraceRegistry(DownstreamDiagnosticsOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 在真正发送 HTTP 请求前登记期望命令，确保极快下行不会早于关联状态建立。
    /// </summary>
    /// <param name="phaseName">HTTP 阶段名称。</param>
    /// <param name="requestSequence">节点内请求序号。</param>
    /// <param name="mapping">真实电表与模拟设备映射。</param>
    /// <param name="showResult">是否同步等待业务结果。</param>
    /// <param name="now">登记时刻。</param>
    /// <param name="expectedCommands">预检确认的一次请求下行命令数，默认 1；HTTP 与 TCP 之间仍属于顺序推断关联。</param>
    /// <returns>节点内唯一关联标识。</returns>
    internal long Register(
        string phaseName,
        long requestSequence,
        GatherDeviceMeterMapping mapping,
        bool showResult,
        DateTimeOffset now,
        int expectedCommands = 1)
    {
        if (expectedCommands is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(expectedCommands), "一次请求的预期子命令必须在 1～1024 之间。");
        var id = Interlocked.Increment(ref _requestId);
        lock (_sync)
        {
            var trace = new MutableTrace(
                id,
                phaseName,
                requestSequence,
                mapping.MeterId,
                mapping.Device.DeviceId,
                showResult,
                now,
                expectedCommands);
            _traces.Add(id, trace);
            GetDeviceState(trace.DeviceId).Pending.Enqueue(id);
        }
        return id;
    }

    /// <summary>
    /// 保存 HTTP 完成状态；明确预校验拒绝不会继续占用设备 FIFO 的下一条下行关联位置。
    /// </summary>
    /// <param name="requestId">登记时返回的关联标识。</param>
    /// <param name="completedAtUtc">HTTP 完成时刻。</param>
    /// <param name="httpStatusCode">可空 HTTP 状态码。</param>
    /// <param name="succeeded">HTTP 和业务结果是否成功。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="prevalidationRejected">是否明确未进入命令队列。</param>
    internal void RecordHttpCompleted(
        long requestId,
        DateTimeOffset completedAtUtc,
        int? httpStatusCode,
        bool succeeded,
        string? errorCode,
        bool prevalidationRejected)
    {
        lock (_sync)
        {
            if (!_traces.TryGetValue(requestId, out var trace))
                return;
            trace.HttpCompletedAtUtc = completedAtUtc;
            trace.HttpStatusCode = httpStatusCode;
            trace.ApiSucceeded = succeeded;
            trace.ApiErrorCode = errorCode;
            trace.PrevalidationRejected = prevalidationRejected;
        }
    }

    /// <summary>新连接建立时清除该设备旧会话指纹，避免旧 Session 的序号复用污染新连接去重。</summary>
    /// <param name="deviceId">规范化设备编号。</param>
    internal void ResetDeviceSession(string deviceId)
    {
        lock (_sync)
            if (_devices.TryGetValue(deviceId, out var state))
                state.RecentFingerprints.Clear();
    }

    /// <summary>
    /// 把设备收到的原始下行与同设备最早的有效期望命令关联，并识别相同报文指纹的重试。
    /// </summary>
    /// <param name="device">收到报文的虚拟设备。</param>
    /// <param name="classification">协议适配器生成的结构化分类。</param>
    /// <param name="now">收到报文的 UTC 时间。</param>
    /// <returns>供响应写出阶段回填的关联结果。</returns>
    internal DownstreamTraceMatch RecordDownstream(
        VirtualDeviceDescriptor device,
        DownstreamFrameClassification classification,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            _classifications[classification.Kind] = _classifications.GetValueOrDefault(classification.Kind) + 1;
            if (!classification.IsRecognizedCommand && !classification.IsUnsupportedCommand)
            {
                // 确认帧、地址不匹配帧和非法帧不能证明某个 HTTP 命令已交付，
                // 只进入分类与样本，不得消费该设备的期望命令 FIFO。
                _unmatchedDownstreamFrames++;
                CaptureSample(classification, device.DeviceId, null, now, duplicate: false);
                return new DownstreamTraceMatch(null, false, null);
            }
            var state = GetDeviceState(device.DeviceId);
            if (state.RecentFingerprints.TryGetValue(classification.Fingerprint, out var recent) &&
                now - recent.ObservedAtUtc <= TimeSpan.FromSeconds(_options.CorrelationTimeoutSeconds) &&
                _traces.TryGetValue(recent.RequestId, out var repeatedTrace))
            {
                repeatedTrace.DuplicateDownstreamFrames++;
                _duplicateDownstreamFrames++;
                CaptureSample(classification, device.DeviceId, repeatedTrace, now, duplicate: true);
                return new DownstreamTraceMatch(repeatedTrace.RequestId, true, repeatedTrace.PhaseName, classification.Fingerprint);
            }

            MutableTrace? trace = null;
            while (state.Pending.Count > 0)
            {
                var candidateId = state.Pending.Peek();
                if (!_traces.TryGetValue(candidateId, out var candidate) ||
                    candidate.PrevalidationRejected ||
                    now - candidate.RegisteredAtUtc > TimeSpan.FromSeconds(_options.CorrelationTimeoutSeconds) ||
                    candidate.ObservedCommands >= candidate.ExpectedCommands)
                {
                    state.Pending.Dequeue();
                    continue;
                }

                trace = candidate;
                break;
            }

            if (trace is null)
            {
                _unmatchedDownstreamFrames++;
                CaptureSample(classification, device.DeviceId, null, now, duplicate: false);
                return new DownstreamTraceMatch(null, false, null);
            }

            trace.FirstDownstreamAtUtc ??= now;
            trace.ObservedCommands++;
            if (trace.ObservedCommands >= trace.ExpectedCommands)
                state.Pending.Dequeue();
            trace.DownstreamKind = classification.Kind;
            trace.AssociationConfidence = CommandAssociationConfidence.Ordered;
            trace.DownstreamFingerprint = classification.Fingerprint;
            trace.Recognized |= classification.IsRecognizedCommand;
            trace.Unsupported |= classification.IsUnsupportedCommand;
            if (classification.IsRecognizedCommand)
                trace.RecognizedCommands++;
            if (classification.IsUnsupportedCommand)
                trace.UnsupportedCommands++;
            // 只保留关联窗口内的指纹，并为每设备设置硬上限，长时间循环发压不能无限积累历史帧。
            foreach (var stale in state.RecentFingerprints.Where(pair => now - pair.Value.ObservedAtUtc > TimeSpan.FromSeconds(_options.CorrelationTimeoutSeconds)).Select(pair => pair.Key).ToArray())
                state.RecentFingerprints.Remove(stale);
            if (state.RecentFingerprints.Count >= 1024)
                state.RecentFingerprints.Remove(state.RecentFingerprints.MinBy(pair => pair.Value.ObservedAtUtc).Key);
            state.RecentFingerprints[classification.Fingerprint] = new RecentFingerprint(trace.RequestId, now);
            CaptureSample(classification, device.DeviceId, trace, now, duplicate: false);
            return new DownstreamTraceMatch(trace.RequestId, false, trace.PhaseName, classification.Fingerprint);
        }
    }

    /// <summary>
    /// 记录模拟设备已为关联命令成功写出至少一条业务响应。
    /// </summary>
    /// <param name="requestId">下行关联标识；无法关联时为空。</param>
    /// <param name="now">成功写出时刻。</param>
    /// <param name="fingerprint">对应下行指纹；重复回复不得增加唯一子命令应答数，旧调用方可省略。</param>
    internal void RecordResponse(long? requestId, DateTimeOffset now, string? fingerprint = null)
    {
        if (!requestId.HasValue)
            return;
        lock (_sync)
        {
            if (_traces.TryGetValue(requestId.Value, out var trace))
            {
                if (trace.ResponseFingerprints.Add(fingerprint ?? trace.DownstreamFingerprint ?? "legacy"))
                    trace.RespondedCommands++;
                trace.ResponseSent = trace.RespondedCommands >= trace.ExpectedCommands;
                trace.ResponseSentAtUtc ??= now;
            }
        }
    }

    /// <summary>
    /// 获取指定阶段用于排空和验收的唯一命令计数，不把重试帧重复计入交付率。
    /// </summary>
    /// <param name="phaseName">阶段名称。</param>
    /// <returns>阶段关联快照。</returns>
    internal CommandPhaseTraceSnapshot GetPhaseSnapshot(string phaseName)
    {
        lock (_sync)
        {
            var source = _traces.Values.Where(item => string.Equals(item.PhaseName, phaseName, StringComparison.Ordinal)).ToArray();
            var estimated = source.Where(item => !item.PrevalidationRejected).Sum(item => (long)item.ExpectedCommands);
            var delivered = source.Sum(item => (long)item.ObservedCommands);
            var recognized = source.Sum(item => (long)item.RecognizedCommands);
            var unsupported = source.Sum(item => (long)item.UnsupportedCommands);
            return new CommandPhaseTraceSnapshot(
                estimated,
                delivered,
                recognized,
                source.Sum(item => (long)item.RespondedCommands),
                unsupported,
                source.Sum(item => (long)item.DuplicateDownstreamFrames));
        }
    }

    /// <summary>获取全部逐请求追踪快照，按请求序号排序。</summary>
    /// <returns>与调用方状态隔离的不可变追踪明细数组。</returns>
    internal IReadOnlyList<CommandTraceDetail> GetTraces()
    {
        lock (_sync)
            return _traces.Values.OrderBy(item => item.RequestSequence).Select(CreateTraceDetail).ToArray();
    }

    /// <summary>获取按指纹和节点总量双重限量保存的未知报文样本。</summary>
    /// <returns>与内部集合隔离的未知报文样本数组。</returns>
    internal IReadOnlyList<DownstreamDiagnosticSample> GetSamples()
    {
        lock (_sync)
            return _samples.ToArray();
    }

    /// <summary>获取本节点下行分类和关联汇总。</summary>
    /// <returns>包含注册、交付、识别、应答、未关联与重复计数的汇总。</returns>
    internal DownstreamDiagnosticSummary GetSummary()
    {
        lock (_sync)
        {
            var source = _traces.Values.ToArray();
            return new DownstreamDiagnosticSummary
            {
                RegisteredCommands = source.Sum(item => (long)item.ExpectedCommands),
                CommandsWithDownstream = source.Sum(item => (long)item.ObservedCommands),
                RecognizedCommands = source.Sum(item => (long)item.RecognizedCommands),
                UnsupportedCommands = source.Sum(item => (long)item.UnsupportedCommands),
                RespondedCommands = source.Sum(item => (long)item.RespondedCommands),
                UnmatchedDownstreamFrames = _unmatchedDownstreamFrames,
                DuplicateDownstreamFrames = _duplicateDownstreamFrames,
                Classifications = new Dictionary<DownstreamClassificationKind, long>(_classifications)
            };
        }
    }

    /// <summary>
    /// 按指纹上限保存未知、非法或地址异常样本；已支持的标准 F129 不保存原始 Hex，避免正常流量放大报告。
    /// </summary>
    /// <param name="classification">协议分类和原始报文。</param>
    /// <param name="deviceId">收到报文的设备编号。</param>
    /// <param name="trace">可空关联命令。</param>
    /// <param name="now">收到报文的 UTC 时间。</param>
    /// <param name="duplicate">是否为重复或重试帧。</param>
    private void CaptureSample(
        DownstreamFrameClassification classification,
        string deviceId,
        MutableTrace? trace,
        DateTimeOffset now,
        bool duplicate)
    {
        if (!_options.CaptureUnknownFrames ||
            _options.MaxSamplesPerFingerprint == 0 ||
            _options.MaxTotalSamples == 0 ||
            _samples.Count >= _options.MaxTotalSamples ||
            classification.IsRecognizedCommand)
        {
            return;
        }
        var count = _sampleCounts.GetValueOrDefault(classification.Fingerprint);
        if (count >= _options.MaxSamplesPerFingerprint)
            return;
        _sampleCounts[classification.Fingerprint] = count + 1;
        var rawCount = Math.Min(_options.RawHexMaxBytes, classification.Raw.Length);
        _samples.Add(new DownstreamDiagnosticSample
        {
            TimestampUtc = now,
            PhaseName = trace?.PhaseName,
            RequestSequence = trace?.RequestSequence,
            MeterId = trace?.MeterId,
            DeviceId = deviceId,
            Kind = classification.Kind,
            AddressMatched = classification.AddressMatched,
            ControlHex = classification.Control.HasValue ? $"{classification.Control.Value:X2}" : null,
            AfnHex = classification.Afn.HasValue ? $"{classification.Afn.Value:X2}" : null,
            Points = classification.Points,
            Functions = classification.Functions,
            Sequence = classification.Sequence,
            RawLength = classification.RawLength,
            RawHex = rawCount == 0 ? null : Convert.ToHexString(classification.Raw.AsSpan(0, rawCount)),
            Fingerprint = classification.Fingerprint,
            Reason = classification.Reason,
            Duplicate = duplicate
        });
    }

    /// <summary>把内部可变状态转换为报告不可变记录。</summary>
    /// <param name="trace">单条命令内部状态。</param>
    /// <returns>可安全写入报告的不可变追踪明细。</returns>
    private static CommandTraceDetail CreateTraceDetail(MutableTrace trace)
    {
        var outcome = trace.PrevalidationRejected
            ? CommandTraceOutcome.PrevalidationRejected
            : !trace.FirstDownstreamAtUtc.HasValue
                ? trace.ApiSucceeded == false
                    ? CommandTraceOutcome.HttpFailedWithoutDownstream
                    : CommandTraceOutcome.NoDownstreamObserved
                : trace.ApiSucceeded == false
                    ? CommandTraceOutcome.HttpFailedAfterDownstream
                    : trace.Unsupported
                        ? CommandTraceOutcome.UnsupportedDownstream
                        : trace.Recognized && !trace.ResponseSent
                            ? CommandTraceOutcome.RecognizedWithoutResponse
                            : trace.ResponseSent
                                ? CommandTraceOutcome.Responded
                                : CommandTraceOutcome.Pending;
        return new CommandTraceDetail
        {
            RequestId = trace.RequestId,
            PhaseName = trace.PhaseName,
            RequestSequence = trace.RequestSequence,
            MeterId = trace.MeterId,
            DeviceId = trace.DeviceId,
            ShowResult = trace.ShowResult,
            RegisteredAtUtc = trace.RegisteredAtUtc,
            HttpCompletedAtUtc = trace.HttpCompletedAtUtc,
            HttpStatusCode = trace.HttpStatusCode,
            ApiSucceeded = trace.ApiSucceeded,
            ApiErrorCode = trace.ApiErrorCode,
            PrevalidationRejected = trace.PrevalidationRejected,
            FirstDownstreamAtUtc = trace.FirstDownstreamAtUtc,
            DownstreamKind = trace.DownstreamKind,
            AssociationConfidence = trace.AssociationConfidence,
            DownstreamFingerprint = trace.DownstreamFingerprint,
            DuplicateDownstreamFrames = trace.DuplicateDownstreamFrames,
            ResponseSentAtUtc = trace.ResponseSentAtUtc,
            ResponseSent = trace.ResponseSent,
            ExpectedDownstreamCommands = trace.ExpectedCommands,
            ObservedDownstreamCommands = trace.ObservedCommands,
            CorrectlyRespondedCommands = trace.RespondedCommands,
            Outcome = outcome
        };
    }

    /// <summary>获取或创建单设备有界关联状态；设备数量上限等于当前分片设备数。</summary>
    /// <param name="deviceId">协议设备编号。</param>
    /// <returns>该设备独占的待关联 FIFO 和近期指纹状态。</returns>
    private DeviceTraceState GetDeviceState(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var state))
            return state;
        state = new DeviceTraceState();
        _devices.Add(deviceId, state);
        return state;
    }

    /// <summary>保存单设备待关联 FIFO 和近期报文指纹。</summary>
    private sealed class DeviceTraceState
    {
        internal Queue<long> Pending { get; } = new();
        internal Dictionary<string, RecentFingerprint> RecentFingerprints { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>保存近期同指纹下行对应的命令和时间。</summary>
    private sealed record RecentFingerprint(long RequestId, DateTimeOffset ObservedAtUtc);

    /// <summary>保存单条命令的内部可变关联状态。</summary>
    private sealed class MutableTrace(
        long requestId,
        string phaseName,
        long requestSequence,
        int meterId,
        string deviceId,
        bool showResult,
        DateTimeOffset registeredAtUtc,
        int expectedCommands)
    {
        internal long RequestId { get; } = requestId;
        internal string PhaseName { get; } = phaseName;
        internal long RequestSequence { get; } = requestSequence;
        internal int MeterId { get; } = meterId;
        internal string DeviceId { get; } = deviceId;
        internal bool ShowResult { get; } = showResult;
        internal DateTimeOffset RegisteredAtUtc { get; } = registeredAtUtc;
        /// <summary>获取预检确认的子命令数量；不根据采集项个数猜测。</summary>
        internal int ExpectedCommands { get; } = expectedCommands;
        /// <summary>获取本请求已关联的不同下行命令数量。</summary>
        internal int ObservedCommands { get; set; }
        /// <summary>获取已识别子命令数量。</summary>
        internal int RecognizedCommands { get; set; }
        /// <summary>获取不支持的子命令数量。</summary>
        internal int UnsupportedCommands { get; set; }
        /// <summary>获取正确回复的不同子命令数量。</summary>
        internal int RespondedCommands { get; set; }
        /// <summary>获取用于回复去重的子命令指纹集合，受预期命令数量限制。</summary>
        internal HashSet<string> ResponseFingerprints { get; } = new(StringComparer.Ordinal);
        internal DateTimeOffset? HttpCompletedAtUtc { get; set; }
        internal int? HttpStatusCode { get; set; }
        internal bool? ApiSucceeded { get; set; }
        internal string? ApiErrorCode { get; set; }
        internal bool PrevalidationRejected { get; set; }
        internal DateTimeOffset? FirstDownstreamAtUtc { get; set; }
        internal DownstreamClassificationKind? DownstreamKind { get; set; }
        internal CommandAssociationConfidence AssociationConfidence { get; set; }
        internal string? DownstreamFingerprint { get; set; }
        internal int DuplicateDownstreamFrames { get; set; }
        internal bool Recognized { get; set; }
        internal bool Unsupported { get; set; }
        internal DateTimeOffset? ResponseSentAtUtc { get; set; }
        internal bool ResponseSent { get; set; }
    }
}

/// <summary>
/// 表示一条设备下行与期望命令的关联结果。
/// </summary>
/// <param name="RequestId">可空请求标识。</param>
/// <param name="Duplicate">是否为已关联命令的重复或重试下行。</param>
/// <param name="PhaseName">可空阶段名称。</param>
/// <param name="Fingerprint">可空子命令指纹，供应答时去重；不代表服务端业务 CommandId。</param>
internal sealed record DownstreamTraceMatch(long? RequestId, bool Duplicate, string? PhaseName, string? Fingerprint = null);

/// <summary>
/// 表示单个 HTTP 阶段的唯一命令交付、识别和响应计数。
/// </summary>
/// <param name="EstimatedEnqueued">排除预校验拒绝后的估算入队请求数。</param>
/// <param name="RawDelivered">至少关联到一帧原始下行的唯一命令数。</param>
/// <param name="Recognized">被识别为当前支持格式的唯一命令数。</param>
/// <param name="Responded">成功写出业务响应的唯一命令数。</param>
/// <param name="Unsupported">收到但当前不支持的唯一命令数。</param>
/// <param name="DuplicateFrames">重复或重试下行帧数。</param>
internal sealed record CommandPhaseTraceSnapshot(
    long EstimatedEnqueued,
    long RawDelivered,
    long Recognized,
    long Responded,
    long Unsupported,
    long DuplicateFrames)
{
    /// <summary>获取空阶段快照。</summary>
    internal static CommandPhaseTraceSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
