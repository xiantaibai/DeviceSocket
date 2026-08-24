using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 根据用户显式配置的成功率、错误率和恢复时间门槛判定测试结果。
/// </summary>
public static class AcceptanceEvaluator
{
    /// <summary>
    /// 计算已启用规则；未配置的规则不会隐式影响退出码。
    /// </summary>
    /// <param name="metrics">测试结束前的累计指标和在线仪表。</param>
    /// <param name="options">可选验收门槛。</param>
    /// <param name="expectedOnline">当前角色需要验收的预期在线设备数。</param>
    /// <param name="gatewayObservation">可选服务端状态观测汇总。</param>
    /// <param name="role">当前运行角色；apiOnly 跳过 TCP 规则，devicesOnly 跳过 HTTP 规则。</param>
    /// <param name="shardIndex">当前节点零基分片编号；汇总报告保持默认零。</param>
    /// <param name="shardCount">当前运行的分片总数；汇总报告保持默认一，节点报告用它换算全局数量门槛。</param>
    /// <param name="phases">可选阶段结果；启用重启前阶段成功率时用于排除计划故障和恢复流量。</param>
    /// <returns>全部已启用规则的逐项结果和总判定。</returns>
    public static AcceptanceResult Evaluate(
        MetricSnapshot metrics,
        AcceptanceOptions options,
        int expectedOnline,
        GatewayObservationSummary? gatewayObservation = null,
        LoadTestRole role = LoadTestRole.DevicesOnly,
        int shardIndex = 0,
        int shardCount = 1,
        IReadOnlyCollection<PhaseResult>? phases = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(shardIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shardIndex, shardCount);
        var checks = new List<AcceptanceCheck>();
        if (role != LoadTestRole.ApiOnly)
        {
            var rateCounters = ResolveRateCounters(metrics, options, phases);
            AddMinimum(checks, "connectionSuccessRate", Rate(rateCounters.ConnectionSucceeded, rateCounters.ConnectionAttempts), options.MinimumConnectionSuccessRate);
            AddMinimum(checks, "loginSuccessRate", Rate(rateCounters.LoginSucceeded, rateCounters.LoginAttempts), options.MinimumLoginSuccessRate);
            AddMinimum(checks, "finalOnlineRate", Rate(metrics.ProtocolOnline, expectedOnline), options.MinimumFinalOnlineRate);
            AddMaximum(checks, "protocolErrorRate", Rate(metrics.InvalidFrames, metrics.FramesReceived), options.MaximumProtocolErrorRate);
            // 配置中的数量是全部分片合计值；节点退出码必须先换算本地配额，最终合并报告仍按全局值重新验收。
            AddMinimum(
                checks,
                "finalOnlineCount",
                metrics.ProtocolOnline,
                ResolveLocalCountThreshold(options.MinimumFinalOnlineCount, shardIndex, shardCount));
            AddMaximum(
                checks,
                "finalOnlineCount",
                metrics.ProtocolOnline,
                ResolveLocalCountThreshold(options.MaximumFinalOnlineCount, shardIndex, shardCount));
            AddMinimum(
                checks,
                "reconnects",
                metrics.ReconnectsScheduled,
                ResolveLocalCountThreshold(options.MinimumReconnects, shardIndex, shardCount));
            AddMaximum(
                checks,
                "reconnects",
                metrics.ReconnectsScheduled,
                ResolveLocalCountThreshold(options.MaximumReconnects, shardIndex, shardCount));
            AddMaximum(
                checks,
                "reconnectsPerDevice",
                Rate(metrics.ReconnectsScheduled, expectedOnline),
                options.MaximumReconnectsPerDevice);
            AddMaximum(
                checks,
                "secondaryOutages",
                metrics.Outages.LongCount(item => !item.ExpectedServerRestart),
                options.MaximumSecondaryOutages);
            AddMinimum(
                checks,
                "stableOnlineSeconds",
                metrics.CurrentStableOnlineMilliseconds / 1000d,
                options.MinimumStableOnlineSeconds);
            AddMinimum(
                checks,
                "gatherReadingResponseRate",
                Rate(metrics.GatherReadingCommandsResponded, metrics.GatherReadingCommandsReceived),
                options.MinimumGatherReadingResponseRate);
        }
        if (role != LoadTestRole.DevicesOnly)
        {
            AddMinimum(checks, "apiSuccessRate", Rate(metrics.ApiRequestsSucceeded, metrics.ApiRequestsStarted), options.MinimumApiSuccessRate);
            AddMinimum(
                checks,
                "apiTargetCoverageRate",
                Rate(
                    metrics.ApiRequestsStarted,
                    checked(metrics.ApiRequestsStarted + metrics.ApiRequestsSkippedOffline)),
                options.MinimumApiTargetCoverageRate);
            AddMaximum(
                checks,
                "apiP95Milliseconds",
                metrics.ApiRequestLatency.Percentile(0.95),
                options.MaximumApiP95Milliseconds);
        }
        if (options.MinimumObservedAuthenticatedDevices.HasValue ||
            options.MaximumObservedAuthenticatedDevices.HasValue)
        {
            var observationAvailable = gatewayObservation?.BaselineAuthenticatedDevices.HasValue == true;
            checks.Add(new AcceptanceCheck(
                "gatewayObservationAvailable",
                observationAvailable ? 1 : 0,
                "==",
                1,
                observationAvailable));
            AddMinimum(
                checks,
                "gatewayAdditionalAuthenticatedDevices",
                gatewayObservation?.MaximumAdditionalAuthenticatedDevices ?? 0,
                options.MinimumObservedAuthenticatedDevices);
            AddMaximum(
                checks,
                "gatewayAdditionalAuthenticatedDevices",
                gatewayObservation?.MaximumAdditionalAuthenticatedDevices ?? 0,
                options.MaximumObservedAuthenticatedDevices);
        }
        if (role != LoadTestRole.ApiOnly && options.MaximumRecoverySeconds is { } maximumRecovery)
        {
            // 验收必须使用最严重事件，禁止后续几台设备的短暂恢复覆盖全量掉线事实。
            var actual = metrics.OutageActive
                ? maximumRecovery + 1
                : metrics.WorstRecoveryMilliseconds.HasValue
                    ? metrics.WorstRecoveryMilliseconds.Value / 1000d
                    : 0;
            checks.Add(new AcceptanceCheck("worstRecoverySeconds", actual, "<=", maximumRecovery, actual <= maximumRecovery));
        }

        return new AcceptanceResult(checks.All(item => item.Passed), checks);
    }

    /// <summary>
    /// 选择初始登录质量的统计范围；重启场景可只汇总首次计划重启前的正常阶段，其他场景保持整轮累计口径。
    /// </summary>
    /// <param name="metrics">整轮累计指标。</param>
    /// <param name="options">验收配置。</param>
    /// <param name="phases">可选阶段结果。</param>
    /// <returns>用于连接和登录成功率的分子、分母计数器。</returns>
    private static (long ConnectionAttempts, long ConnectionSucceeded, long LoginAttempts, long LoginSucceeded)
        ResolveRateCounters(
            MetricSnapshot metrics,
            AcceptanceOptions options,
            IReadOnlyCollection<PhaseResult>? phases)
    {
        if (!options.UsePreRestartPhaseRates || phases is null || phases.Count == 0)
        {
            return (
                metrics.ConnectionAttempts,
                metrics.ConnectionSucceeded,
                metrics.LoginAttempts,
                metrics.LoginSucceeded);
        }

        var firstRestartAtUtc = phases
            .Where(item => item.ExpectedServerRestart)
            .Select(item => (DateTimeOffset?)item.StartedAtUtc)
            .Min();
        var normalPhases = phases
            .Where(item => !firstRestartAtUtc.HasValue || item.StartedAtUtc < firstRestartAtUtc.Value)
            .Where(item => !item.ExpectedServerRestart)
            .Where(item => item.Kind is ScenarioPhaseKind.Warmup or ScenarioPhaseKind.Ramp or ScenarioPhaseKind.Steady)
            .Where(item => item.Delta is not null)
            .Select(item => item.Delta!)
            .ToArray();
        if (normalPhases.Length == 0)
        {
            return (
                metrics.ConnectionAttempts,
                metrics.ConnectionSucceeded,
                metrics.LoginAttempts,
                metrics.LoginSucceeded);
        }

        return (
            normalPhases.Sum(item => item.ConnectionAttempts),
            normalPhases.Sum(item => item.ConnectionSucceeded),
            normalPhases.Sum(item => item.LoginAttempts),
            normalPhases.Sum(item => item.LoginSucceeded));
    }

    /// <summary>
    /// 安全计算计数比例，分母非正时返回零。
    /// </summary>
    /// <param name="numerator">分子计数。</param>
    /// <param name="denominator">分母计数。</param>
    /// <returns>计算后的双精度比例。</returns>
    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    /// <summary>按可选双精度下限追加验收项。</summary>
    /// <param name="checks">验收项集合。</param><param name="name">规则名称。</param><param name="actual">实际值。</param><param name="threshold">可空下限。</param>
    private static void AddMinimum(
        ICollection<AcceptanceCheck> checks,
        string name,
        double actual,
        double? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, ">=", threshold.Value, actual >= threshold.Value));
    }

    /// <summary>按可选双精度上限追加验收项。</summary>
    /// <param name="checks">验收项集合。</param><param name="name">规则名称。</param><param name="actual">实际值。</param><param name="threshold">可空上限。</param>
    private static void AddMaximum(
        ICollection<AcceptanceCheck> checks,
        string name,
        double actual,
        double? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, "<=", threshold.Value, actual <= threshold.Value));
    }

    /// <summary>按可选整数下限追加验收项。</summary>
    /// <param name="checks">验收项集合。</param><param name="name">规则名称。</param><param name="actual">实际值。</param><param name="threshold">可空下限。</param>
    private static void AddMinimum(
        ICollection<AcceptanceCheck> checks,
        string name,
        long actual,
        long? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, ">=", threshold.Value, actual >= threshold.Value));
    }

    /// <summary>按可选整数上限追加验收项。</summary>
    /// <param name="checks">验收项集合。</param><param name="name">规则名称。</param><param name="actual">实际值。</param><param name="threshold">可空上限。</param>
    private static void AddMaximum(
        ICollection<AcceptanceCheck> checks,
        string name,
        long actual,
        long? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, "<=", threshold.Value, actual <= threshold.Value));
    }

    /// <summary>
    /// 按全局序号取模规则把所有分片合计的数量门槛换算为当前节点配额。
    /// </summary>
    /// <param name="globalThreshold">全局数量门槛；空值表示不启用规则。</param>
    /// <param name="shardIndex">当前节点零基分片编号。</param>
    /// <param name="shardCount">分片总数。</param>
    /// <returns>当前节点应承担的数量；单分片保持原值。</returns>
    private static long? ResolveLocalCountThreshold(
        long? globalThreshold,
        int shardIndex,
        int shardCount)
    {
        if (!globalThreshold.HasValue || shardCount == 1)
            return globalThreshold;
        if (globalThreshold.Value <= shardIndex)
            return 0;

        // 全局序号 shardIndex、shardIndex+shardCount... 中，小于门槛的元素数量就是当前节点配额。
        return (globalThreshold.Value - 1 - shardIndex) / shardCount + 1;
    }
}
