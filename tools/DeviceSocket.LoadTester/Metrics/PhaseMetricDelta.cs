namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示单个 TCP 场景阶段内新增的关键累计指标，避免报告端对阶段结束累计值进行不可靠的手工相减。
/// </summary>
public sealed record PhaseMetricDelta
{
    /// <summary>获取阶段内新增的连接尝试次数。</summary>
    public required long ConnectionAttempts { get; init; }

    /// <summary>获取阶段内新增的连接成功次数。</summary>
    public required long ConnectionSucceeded { get; init; }

    /// <summary>获取阶段内新增的连接失败次数。</summary>
    public required long ConnectionFailed { get; init; }

    /// <summary>获取阶段内新增的登录尝试次数。</summary>
    public required long LoginAttempts { get; init; }

    /// <summary>获取阶段内新增的登录成功次数。</summary>
    public required long LoginSucceeded { get; init; }

    /// <summary>获取阶段内新增的登录失败次数。</summary>
    public required long LoginFailed { get; init; }

    /// <summary>获取阶段内新增的心跳发送次数。</summary>
    public required long HeartbeatsSent { get; init; }

    /// <summary>获取阶段内新增的心跳确认次数。</summary>
    public required long HeartbeatsConfirmed { get; init; }

    /// <summary>获取阶段内新增的心跳超时次数。</summary>
    public required long HeartbeatTimeouts { get; init; }

    /// <summary>获取阶段内新增的自动重连次数。</summary>
    public required long ReconnectsScheduled { get; init; }

    /// <summary>获取阶段内新增的掉线事件数。</summary>
    public required long OutageCount { get; init; }

    /// <summary>获取阶段内各稳定错误码的新增次数。</summary>
    public required IReadOnlyDictionary<string, long> Errors { get; init; }

    /// <summary>
    /// 根据阶段起止快照计算非负增量；计数异常回退时按零处理，避免报告出现负数。
    /// </summary>
    /// <param name="started">阶段开始前的累计快照。</param>
    /// <param name="ended">阶段结束时的累计快照。</param>
    /// <returns>仅包含当前阶段新增值的指标。</returns>
    public static PhaseMetricDelta Between(MetricSnapshot started, MetricSnapshot ended)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(ended);
        var errorKeys = started.Errors.Keys.Concat(ended.Errors.Keys).Distinct(StringComparer.Ordinal);
        var errors = errorKeys.ToDictionary(
            key => key,
            key => Delta(started.Errors.GetValueOrDefault(key), ended.Errors.GetValueOrDefault(key)),
            StringComparer.Ordinal);

        return new PhaseMetricDelta
        {
            ConnectionAttempts = Delta(started.ConnectionAttempts, ended.ConnectionAttempts),
            ConnectionSucceeded = Delta(started.ConnectionSucceeded, ended.ConnectionSucceeded),
            ConnectionFailed = Delta(started.ConnectionFailed, ended.ConnectionFailed),
            LoginAttempts = Delta(started.LoginAttempts, ended.LoginAttempts),
            LoginSucceeded = Delta(started.LoginSucceeded, ended.LoginSucceeded),
            LoginFailed = Delta(started.LoginFailed, ended.LoginFailed),
            HeartbeatsSent = Delta(started.HeartbeatsSent, ended.HeartbeatsSent),
            HeartbeatsConfirmed = Delta(started.HeartbeatsConfirmed, ended.HeartbeatsConfirmed),
            HeartbeatTimeouts = Delta(started.HeartbeatTimeouts, ended.HeartbeatTimeouts),
            ReconnectsScheduled = Delta(started.ReconnectsScheduled, ended.ReconnectsScheduled),
            OutageCount = Delta(started.OutageCount, ended.OutageCount),
            Errors = errors
        };
    }

    /// <summary>
    /// 计算单个累计计数器的非负差值。
    /// </summary>
    /// <param name="started">阶段开始值。</param>
    /// <param name="ended">阶段结束值。</param>
    /// <returns>结束值不小于开始值时返回差值，否则返回零。</returns>
    private static long Delta(long started, long ended) => Math.Max(0, ended - started);
}
