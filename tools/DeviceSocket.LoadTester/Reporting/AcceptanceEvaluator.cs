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
    /// <returns>全部已启用规则的逐项结果和总判定。</returns>
    public static AcceptanceResult Evaluate(
        MetricSnapshot metrics,
        AcceptanceOptions options,
        int expectedOnline,
        GatewayObservationSummary? gatewayObservation = null,
        LoadTestRole role = LoadTestRole.DevicesOnly)
    {
        var checks = new List<AcceptanceCheck>();
        if (role != LoadTestRole.ApiOnly)
        {
            AddMinimum(checks, "connectionSuccessRate", Rate(metrics.ConnectionSucceeded, metrics.ConnectionAttempts), options.MinimumConnectionSuccessRate);
            AddMinimum(checks, "loginSuccessRate", Rate(metrics.LoginSucceeded, metrics.LoginAttempts), options.MinimumLoginSuccessRate);
            AddMinimum(checks, "finalOnlineRate", Rate(metrics.ProtocolOnline, expectedOnline), options.MinimumFinalOnlineRate);
            AddMaximum(checks, "protocolErrorRate", Rate(metrics.InvalidFrames, metrics.FramesReceived), options.MaximumProtocolErrorRate);
            AddMinimum(checks, "finalOnlineCount", metrics.ProtocolOnline, options.MinimumFinalOnlineCount);
            AddMaximum(checks, "finalOnlineCount", metrics.ProtocolOnline, options.MaximumFinalOnlineCount);
            AddMinimum(checks, "reconnects", metrics.ReconnectsScheduled, options.MinimumReconnects);
            AddMinimum(
                checks,
                "gatherReadingResponseRate",
                Rate(metrics.GatherReadingCommandsResponded, metrics.GatherReadingCommandsReceived),
                options.MinimumGatherReadingResponseRate);
        }
        if (role != LoadTestRole.DevicesOnly)
        {
            AddMinimum(checks, "apiSuccessRate", Rate(metrics.ApiRequestsSucceeded, metrics.ApiRequestsStarted), options.MinimumApiSuccessRate);
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
            var actual = metrics.LastRecoveryMilliseconds.HasValue
                ? metrics.LastRecoveryMilliseconds.Value / 1000d
                : metrics.OutageActive ? maximumRecovery + 1 : 0;
            checks.Add(new AcceptanceCheck("recoverySeconds", actual, "<=", maximumRecovery, actual <= maximumRecovery));
        }

        return new AcceptanceResult(checks.All(item => item.Passed), checks);
    }

    private static double Rate(long numerator, long denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;

    private static void AddMinimum(
        ICollection<AcceptanceCheck> checks,
        string name,
        double actual,
        double? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, ">=", threshold.Value, actual >= threshold.Value));
    }

    private static void AddMaximum(
        ICollection<AcceptanceCheck> checks,
        string name,
        double actual,
        double? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, "<=", threshold.Value, actual <= threshold.Value));
    }

    private static void AddMinimum(
        ICollection<AcceptanceCheck> checks,
        string name,
        long actual,
        long? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, ">=", threshold.Value, actual >= threshold.Value));
    }

    private static void AddMaximum(
        ICollection<AcceptanceCheck> checks,
        string name,
        long actual,
        long? threshold)
    {
        if (threshold.HasValue)
            checks.Add(new AcceptanceCheck(name, actual, "<=", threshold.Value, actual <= threshold.Value));
    }
}
