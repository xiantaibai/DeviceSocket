using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>生成 GatherV4 Handmould/IssueCmd 请求，并按 ResultMsg 的 Status/Msg 语义判定同步结果。</summary>
internal sealed class GatherV4CommandWorkloadAdapter : ICommandWorkloadAdapter
{
    private readonly GatherV4CommandOptions _options;

    /// <summary>使用 GatherV4 专属业务参数创建适配器。</summary>
    /// <param name="options">GatherV4 IssueCmd 参数。</param>
    internal GatherV4CommandWorkloadAdapter(GatherV4CommandOptions options) => _options = options;

    /// <inheritdoc />
    public byte[] CreateRequestPayload(
        GatherDeviceMeterMapping mapping,
        CommandPhaseOptions phase,
        DateTimeOffset now)
    {
        _ = phase;
        var dataTime = string.IsNullOrWhiteSpace(_options.DataTime)
            ? now.LocalDateTime.ToString(_options.DataTimeFormat, CultureInfo.InvariantCulture)
            : _options.DataTime;
        var request = new GatherV4IssueCommandRequest(
            mapping.Device.AdministrativeDivision,
            mapping.Device.TerminalAddress.ToString("D5", CultureInfo.InvariantCulture),
            mapping.MeterId,
            _options.GatherCode,
            _options.MeterType,
            _options.ItemCodes.ToArray(),
            _options.Density,
            _options.Point,
            dataTime!);
        return JsonSerializer.SerializeToUtf8Bytes(request);
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
            if (!CommandApiJson.TryGetBoolean(root, "status", out var succeeded))
                return new CommandApiEvaluation(false, "API_INVALID_RESPONSE", CommandApiJson.ReadMessage(root), false);
            return succeeded
                ? new CommandApiEvaluation(true, null, CommandApiJson.ReadMessage(root), false)
                // IssueCmd 在命令已经入队后仍可能因等待设备结果超时返回 Status=false，不能误标成“入队前拒绝”。
                : new CommandApiEvaluation(false, "API_GATHERV4_BUSINESS_FAILURE", CommandApiJson.ReadMessage(root), false);
        }
        catch (JsonException)
        {
            return new CommandApiEvaluation(false, "API_INVALID_JSON", null, false);
        }
    }

    /// <summary>表示 GatherV4 HandmouldRequest 的最小稳定字段集合。</summary>
    /// <param name="AreaCode">设备编号前四位行政区划码。</param>
    /// <param name="Address">设备编号后五位终端地址。</param>
    /// <param name="AmmeterId">测试库中真实电表主键。</param>
    /// <param name="GatherCode">与通讯服务一致的采集服务编码。</param>
    /// <param name="MeterType">GatherV4 表计类型。</param>
    /// <param name="ItemCodes">要读取的采集项。</param>
    /// <param name="Density">冻结密度。</param>
    /// <param name="Point">数据点数。</param>
    /// <param name="DataTime">业务数据时间。</param>
    private sealed record GatherV4IssueCommandRequest(
        [property: JsonPropertyName("areaCode")] string AreaCode,
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("ammeterId")] int AmmeterId,
        [property: JsonPropertyName("gatherCode")] string GatherCode,
        [property: JsonPropertyName("meterType")] int MeterType,
        [property: JsonPropertyName("itemCodes")] string[] ItemCodes,
        [property: JsonPropertyName("density")] int Density,
        [property: JsonPropertyName("point")] int Point,
        [property: JsonPropertyName("dataTime")] string DataTime);
}
