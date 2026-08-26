using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证客户端在线数、重连次数和 Gateway 观测值的自动验收规则。
/// </summary>
public sealed class AcceptanceEvaluatorTests
{
    /// <summary>
    /// 验证容量场景可以按服务端认证设备峰值通过，而不依赖客户端瞬时绑定判断。
    /// </summary>
    [Fact]
    public void GatewayCapacityThreshold_UsesObservedMaximum()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumObservedAuthenticatedDevices = 490,
            MaximumObservedAuthenticatedDevices = 500,
            MinimumReconnects = 1
        };
        var metrics = new MetricSnapshot { ProtocolOnline = 700, ReconnectsScheduled = 20 };
        var gateway = new GatewayObservationSummary(10, 0, 10, 10, 510, 510, 500, 500, null);

        var result = AcceptanceEvaluator.Evaluate(metrics, options, 1000, gateway);

        Assert.True(result.Passed);
        Assert.Equal(4, result.Checks.Count);
    }

    /// <summary>
    /// 验证只设置容量上限时，启动前状态 API 不可用也不能被误判为零设备并通过。
    /// </summary>
    [Fact]
    public void MissingGatewayBaseline_FailsMaximumOnlyRule()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MaximumObservedAuthenticatedDevices = 500
        };
        var metrics = new MetricSnapshot();
        var gateway = new GatewayObservationSummary(1, 1, null, null, 0, 0, 0, 0, null);

        var result = AcceptanceEvaluator.Evaluate(metrics, options, 0, gateway);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "gatewayObservationAvailable" && !item.Passed);
    }

    /// <summary>
    /// 验证 apiOnly 角色忽略默认 TCP 在线门槛，并按 API 成功率和 P95 延迟验收。
    /// </summary>
    [Fact]
    public void ApiOnly_UsesOnlyApiThresholds()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = 0.99,
            MaximumProtocolErrorRate = 0,
            MinimumApiSuccessRate = 0.95,
            MaximumApiP95Milliseconds = 1_000
        };
        var histogram = new MergeableHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(500));
        var metrics = new MetricSnapshot
        {
            ApiRequestsStarted = 100,
            ApiRequestsSucceeded = 98,
            ApiRequestsFailed = 2,
            ApiRequestLatency = histogram.Snapshot()
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Checks.Count);
    }

    /// <summary>
    /// 验证 API 目标覆盖率把离线跳过请求计入分母，避免少量已发送请求全部成功时误判整轮通过。
    /// </summary>
    [Fact]
    public void ApiTargetCoverageRate_IncludesOfflineSkippedRequests()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumApiSuccessRate = 0.95,
            MinimumApiTargetCoverageRate = 0.95
        };
        var metrics = new MetricSnapshot
        {
            ApiRequestsStarted = 90,
            ApiRequestsSucceeded = 90,
            ApiRequestsSkippedOffline = 10
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly);

        Assert.False(result.Passed);
        Assert.Contains(
            result.Checks,
            item => item.Name == "apiSuccessRate" && item.Passed && item.Actual == 1);
        Assert.Contains(
            result.Checks,
            item => item.Name == "apiTargetCoverageRate" && !item.Passed && item.Actual == 0.9);
    }

    /// <summary>
    /// 验证多分片节点使用全局在线数和重连数门槛时会换算本地配额，避免每台机器都被要求完成全局数量。
    /// </summary>
    [Fact]
    public void GlobalCountThresholds_AreDistributedForNodeAcceptance()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumFinalOnlineCount = 9_800,
            MinimumReconnects = 9_800
        };
        var metrics = new MetricSnapshot
        {
            ProtocolOnline = 2_450,
            ReconnectsScheduled = 2_450
        };

        var result = AcceptanceEvaluator.Evaluate(
            metrics,
            options,
            expectedOnline: 2_500,
            shardIndex: 3,
            shardCount: 4);

        Assert.True(result.Passed);
        Assert.Contains(
            result.Checks,
            item => item.Name == "finalOnlineCount" && item.Threshold == 2_450);
        Assert.Contains(
            result.Checks,
            item => item.Name == "reconnects" && item.Threshold == 2_450);
    }

    /// <summary>
    /// 验证重启验收使用最严重恢复事件，并能同时拒绝二次掉线和过量级联重连。
    /// </summary>
    [Fact]
    public void RestartStabilityRules_UseWorstAndSecondaryOutages()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MaximumRecoverySeconds = 60,
            MaximumReconnectsPerDevice = 2,
            MaximumSecondaryOutages = 0,
            MinimumStableOnlineSeconds = 120
        };
        var metrics = new MetricSnapshot
        {
            ExpectedOnline = 10_000,
            ProtocolOnline = 10_000,
            ReconnectsScheduled = 30_000,
            WorstRecoveryMilliseconds = 90_000,
            CurrentStableOnlineMilliseconds = 30_000,
            Outages =
            [
                CreateOutage(1, expectedRestart: true, 90_000),
                CreateOutage(2, expectedRestart: false, 1_000)
            ]
        };

        var result = AcceptanceEvaluator.Evaluate(metrics, options, expectedOnline: 10_000);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "worstRecoverySeconds" && item.Actual == 90 && !item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "secondaryOutages" && item.Actual == 1 && !item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "reconnectsPerDevice" && item.Actual == 3 && !item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "stableOnlineSeconds" && item.Actual == 30 && !item.Passed);
    }

    /// <summary>
    /// 验证计划重启场景的连接和登录成功率只统计重启前正常阶段，不受预期重连风暴失败污染。
    /// </summary>
    [Fact]
    public void PreRestartPhaseRates_ExcludeExpectedRestartTraffic()
    {
        var options = new AcceptanceOptions
        {
            UsePreRestartPhaseRates = true,
            MinimumConnectionSuccessRate = 0.95,
            MinimumLoginSuccessRate = 0.95,
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null
        };
        var metrics = new MetricSnapshot
        {
            ConnectionAttempts = 30_000,
            ConnectionSucceeded = 10_000,
            LoginAttempts = 10_000,
            LoginSucceeded = 10_000
        };
        var now = DateTimeOffset.UtcNow;
        var phases = new[]
        {
            CreatePhase("ramp", ScenarioPhaseKind.Ramp, now, expectedRestart: false, attempts: 10_000, connected: 9_900, logins: 9_900, loggedIn: 9_801),
            CreatePhase("restart", ScenarioPhaseKind.Fault, now.AddMinutes(1), expectedRestart: true, attempts: 20_000, connected: 100, logins: 100, loggedIn: 100)
        };

        var result = AcceptanceEvaluator.Evaluate(metrics, options, 10_000, phases: phases);

        Assert.True(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "connectionSuccessRate" && item.Actual == 0.99);
        Assert.Contains(result.Checks, item => item.Name == "loginSuccessRate" && item.Actual == 0.99);
    }

    /// <summary>
    /// 验证异步 API 接受率、同步结果率、设备交付率和未观测数量使用 HTTP 阶段独立口径验收。
    /// </summary>
    [Fact]
    public void CommandPhases_AreAcceptedBySeparateEndToEndRules()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumAsyncApiAcceptanceRate = 0.95,
            MinimumSyncResultSuccessRate = 0.75,
            MinimumCommandDeliveryRate = 0.95,
            MaximumUnobservedCommands = 1
        };
        var now = DateTimeOffset.UtcNow;
        var commandPhases = new[]
        {
            CreateCommandPhase("async", showResult: false, started: 10, succeeded: 9, estimatedEnqueued: 10, received: 8),
            CreateCommandPhase("sync", showResult: true, started: 10, succeeded: 8, estimatedEnqueued: 10, received: 10)
        };

        var result = AcceptanceEvaluator.Evaluate(
            new MetricSnapshot { TimestampUtc = now },
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly,
            commandPhases: commandPhases);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "asyncApiAcceptanceRate" && item.Actual == 0.9 && !item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "syncResultSuccessRate" && item.Actual == 0.8 && item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "commandDeliveryRate" && item.Actual == 0.9 && !item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "unobservedCommands" && item.Actual == 2 && !item.Passed);
    }

    /// <summary>
    /// 验证原始交付通过但协议识别率不足时只让识别规则失败，并且未执行的同步阶段不会产生零样本假失败。
    /// </summary>
    [Fact]
    public void CommandPhases_SeparateRawDeliveryFromRecognition_AndSkipEmptyModes()
    {
        var options = new AcceptanceOptions
        {
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MinimumSyncResultSuccessRate = 0.99,
            MinimumCommandDeliveryRate = 0.95,
            MinimumCommandRecognitionRate = 0.95
        };
        var phase = CreateCommandPhase(
            "async",
            showResult: false,
            started: 100,
            succeeded: 100,
            estimatedEnqueued: 100,
            received: 80) with
        {
            RawDownstreamCommandsObserved = 100,
            ProtocolRecognizedCommands = 80
        };

        var result = AcceptanceEvaluator.Evaluate(
            new MetricSnapshot { TimestampUtc = DateTimeOffset.UtcNow },
            options,
            expectedOnline: 0,
            role: LoadTestRole.ApiOnly,
            commandPhases: [phase]);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, item => item.Name == "commandDeliveryRate" && item.Actual == 1 && item.Passed);
        Assert.Contains(result.Checks, item => item.Name == "commandRecognitionRate" && item.Actual == 0.8 && !item.Passed);
        Assert.DoesNotContain(result.Checks, item => item.Name == "syncResultSuccessRate");
    }

    /// <summary>创建端到端 HTTP 验收测试使用的最小阶段结果。</summary>
    /// <param name="name">阶段名称。</param>
    /// <param name="showResult">是否同步结果阶段。</param>
    /// <param name="started">发起数。</param>
    /// <param name="succeeded">业务成功数。</param>
    /// <param name="estimatedEnqueued">估算入队数。</param>
    /// <param name="received">设备侧观测数。</param>
    /// <returns>包含独立异步或同步成功字段的阶段结果。</returns>
    private static CommandPhaseResult CreateCommandPhase(
        string name,
        bool showResult,
        long started,
        long succeeded,
        long estimatedEnqueued,
        long received) => new()
        {
            Name = name,
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow,
            LocalDeviceCount = 10,
            PlannedRequests = 10,
            ShowResult = showResult,
            ApiRequestsStarted = started,
            ApiRequestsSucceeded = succeeded,
            ApiRequestsFailed = started - succeeded,
            ApiRequestTimeouts = 0,
            ApiEstimatedEnqueued = estimatedEnqueued,
            ApiBusinessAccepted = showResult ? 0 : succeeded,
            SyncResultsSucceeded = showResult ? succeeded : 0,
            GatherReadingCommandsReceived = received,
            GatherReadingCommandsResponded = received,
            RawDownstreamCommandsObserved = received,
            ProtocolRecognizedCommands = received,
            GatherReadingCommandsUnobserved = Math.Max(0, estimatedEnqueued - received)
        };

    /// <summary>
    /// 创建验收测试使用的完整掉线事件快照。
    /// </summary>
    /// <param name="sequence">事件编号。</param>
    /// <param name="expectedRestart">是否计划重启事件。</param>
    /// <param name="durationMilliseconds">恢复耗时毫秒数。</param>
    /// <returns>用于验收的掉线事件。</returns>
    private static OutageEventSnapshot CreateOutage(long sequence, bool expectedRestart, long durationMilliseconds) => new()
    {
        Sequence = sequence,
        PhaseName = "restart",
        PhaseKind = ScenarioPhaseKind.Fault,
        ExpectedServerRestart = expectedRestart,
        StartedAtUtc = DateTimeOffset.UtcNow,
        RecoveredAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(durationMilliseconds),
        DurationMilliseconds = durationMilliseconds,
        ExpectedOnline = 10_000,
        MinimumProtocolOnline = 0,
        MaximumOfflineDevices = 10_000,
        ConnectionAttempts = 10_000,
        ConnectionFailures = 0,
        LoginFailures = 0,
        HeartbeatTimeouts = 0,
        ReconnectsScheduled = 10_000,
        AddressAlreadyInUse = 0
    };

    /// <summary>
    /// 创建包含连接和登录增量的阶段结果。
    /// </summary>
    /// <param name="name">阶段名称。</param>
    /// <param name="kind">阶段类型。</param>
    /// <param name="startedAtUtc">阶段开始时间。</param>
    /// <param name="expectedRestart">是否计划重启阶段。</param>
    /// <param name="attempts">连接尝试增量。</param>
    /// <param name="connected">连接成功增量。</param>
    /// <param name="logins">登录尝试增量。</param>
    /// <param name="loggedIn">登录成功增量。</param>
    /// <returns>用于阶段范围验收的结果。</returns>
    private static PhaseResult CreatePhase(
        string name,
        ScenarioPhaseKind kind,
        DateTimeOffset startedAtUtc,
        bool expectedRestart,
        long attempts,
        long connected,
        long logins,
        long loggedIn) => new(
            name,
            kind,
            startedAtUtc,
            startedAtUtc.AddSeconds(30),
            10_000,
            new MetricSnapshot(),
            Delta: new PhaseMetricDelta
            {
                ConnectionAttempts = attempts,
                ConnectionSucceeded = connected,
                ConnectionFailed = attempts - connected,
                LoginAttempts = logins,
                LoginSucceeded = loggedIn,
                LoginFailed = logins - loggedIn,
                HeartbeatsSent = 0,
                HeartbeatsConfirmed = 0,
                HeartbeatTimeouts = 0,
                ReconnectsScheduled = 0,
                OutageCount = 0,
                Errors = new Dictionary<string, long>()
            },
            ExpectedServerRestart: expectedRestart);
}
