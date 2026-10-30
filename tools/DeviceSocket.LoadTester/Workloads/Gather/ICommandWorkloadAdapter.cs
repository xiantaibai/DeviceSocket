using System.Net;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 隔离 Gather.TcpCenter 与 GatherV4 的 HTTP 请求体和业务响应格式，使限速、并发、在线门槛、排空及报告逻辑保持共用。
/// </summary>
internal interface ICommandWorkloadAdapter
{
    /// <summary>创建一次命令请求的 UTF-8 JSON。</summary>
    /// <param name="mapping">当前目标设备与真实电表主键。</param>
    /// <param name="phase">当前命令负载阶段。</param>
    /// <param name="now">生成动态业务时间使用的当前时刻。</param>
    /// <returns>不包含凭据的 UTF-8 JSON 请求体。</returns>
    byte[] CreateRequestPayload(
        GatherDeviceMeterMapping mapping,
        CommandPhaseOptions phase,
        DateTimeOffset now);

    /// <summary>同时按 HTTP 状态码和服务端 JSON 语义判定业务结果。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="payload">完整响应体。</param>
    /// <param name="mapping">本次请求的设备及电表映射，用于防止其他电表结果误算成功。</param>
    /// <returns>统一后的成功状态、错误码和消息。</returns>
    CommandApiEvaluation EvaluateResponse(HttpStatusCode statusCode, byte[] payload, GatherDeviceMeterMapping mapping);
}
