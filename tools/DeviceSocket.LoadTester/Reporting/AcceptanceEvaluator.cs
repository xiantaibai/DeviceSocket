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
    public static AcceptanceResult Evaluate(
        MetricSnapshot metrics,
        AcceptanceOptions options,
        int expectedOnline,
        GatewayObservationSummary? gatewayObservation = null)
    {
        var checks = new List<AcceptanceCheck>();
        AddMinimum(checks, "connectionSuccessRate", Rate(metrics.ConnectionSucceeded, metrics.ConnectionAttempts), options.MinimumConnectionSuccessRate);
        AddMinimum(checks, "loginSuccessRate", Rate(metrics.LoginSucceeded, metrics.LoginAttempts), options.MinimumLoginSuccessRate);
        AddMinimum(checks, "finalOnlineRate", Rate(metrics.ProtocolOnline, expectedOnline), options.MinimumFinalOnlineRate);
        AddMaximum(checks, "protocolErrorRate", Rate(metrics.InvalidFrames, metrics.FramesReceived), options.MaximumProtocolErrorRate);
        AddMinimum(checks, "finalOnlineCount", metrics.ProtocolOnline, options.MinimumFinalOnlineCount);
        AddMaximum(checks, "finalOnlineCount", metrics.ProtocolOnline, options.MaximumFinalOnlineCount);
        AddMinimum(checks, "reconnects", metrics.ReconnectsScheduled, options.MinimumReconnects);
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
        if (options.MaximumRecoverySeconds is { } maximumRecovery)
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
