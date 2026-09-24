using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>生成 Gather.TcpCenter 老业务读表请求，并按 BaseDto 的 success/code 语义判定结果。</summary>
internal sealed class GatherTcpCenterCommandWorkloadAdapter : ICommandWorkloadAdapter
{
    private readonly CommandWorkloadOptions _options;

    /// <summary>使用老业务字段配置创建适配器。</summary>
    /// <param name="options">HTTP 指令负载配置。</param>
    internal GatherTcpCenterCommandWorkloadAdapter(CommandWorkloadOptions options) => _options = options;

    /// <inheritdoc />
    public byte[] CreateRequestPayload(
        GatherDeviceMeterMapping mapping,
        CommandPhaseOptions phase,
        DateTimeOffset now)
    {
        _ = now;
        return JsonSerializer.SerializeToUtf8Bytes(new ReadingRequest(
            phase.ShowResult,
            new ReadingRequestData(
                mapping.MeterId,
                _options.OperatorUserId,
                _options.OperationSource,
                _options.ReadingModel,
                _options.ReadingCode)));
    }

    /// <inheritdoc />
    public CommandApiEvaluation EvaluateResponse(HttpStatusCode statusCode, byte[] payload)
    {
        if ((int)statusCode is < 200 or >= 300)
            return new CommandApiEvaluation(false, $"API_HTTP_{(int)statusCode}", null, false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new CommandApiEvaluation(false, "API_INVALID_RESPONSE", CommandApiJson.ReadMessage(root), false);
            }
            if (success.GetBoolean())
                return new CommandApiEvaluation(true, null, CommandApiJson.ReadMessage(root), false);
            var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                ? value
                : 0;
            // 老服务 405/407 在命令进入 Redis/CmdCenter 之前就拒绝，必须从估算入队数中排除。
            return new CommandApiEvaluation(false, $"API_BUSINESS_{code}", CommandApiJson.ReadMessage(root), code is 405 or 407);
        }
        catch (JsonException)
        {
            return new CommandApiEvaluation(false, "API_INVALID_JSON", null, false);
        }
    }

    /// <summary>表示老接口要求的顶层 show_result 和 data 请求体。</summary>
    /// <param name="ShowResult">是否同步等待业务结果。</param>
    /// <param name="Data">读表参数。</param>
    private sealed record ReadingRequest(
        [property: JsonPropertyName("show_result")] bool ShowResult,
        [property: JsonPropertyName("data")] ReadingRequestData Data);

    /// <summary>表示老接口读表 data 对象。</summary>
    /// <param name="MeterId">真实电表主键。</param>
    /// <param name="OperatorUserId">操作用户标识。</param>
    /// <param name="OperationSource">操作来源。</param>
    /// <param name="ReadingModel">抄读模型。</param>
    /// <param name="ReadingCode">抄读项编码。</param>
    private sealed record ReadingRequestData(
        [property: JsonPropertyName("meter_id")] int MeterId,
        [property: JsonPropertyName("oper_uid")] int OperatorUserId,
        [property: JsonPropertyName("oper_source")] int OperationSource,
        [property: JsonPropertyName("readingModel")] int ReadingModel,
        [property: JsonPropertyName("readingCode")] int ReadingCode);
}
