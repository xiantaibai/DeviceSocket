namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>保存不同 Gather 业务 API 对同一 HTTP 响应的统一业务判定结果。</summary>
/// <param name="Succeeded">是否同时满足 HTTP 和业务成功。</param>
/// <param name="ErrorCode">便于汇总的稳定错误码。</param>
/// <param name="ErrorMessage">服务端返回的简短业务消息。</param>
/// <param name="PrevalidationRejected">是否明确在命令进入 Redis 前被业务校验拒绝。</param>
internal sealed record CommandApiEvaluation(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    bool PrevalidationRejected);
