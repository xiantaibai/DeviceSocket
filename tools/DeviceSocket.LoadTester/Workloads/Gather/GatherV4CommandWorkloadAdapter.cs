using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>生成 GatherV4 人工任务请求，逐项核对本表有效结果，防止部分返回被误算完整成功。</summary>
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
    public CommandApiEvaluation EvaluateResponse(HttpStatusCode statusCode, byte[] payload, GatherDeviceMeterMapping mapping)
    {
        if ((int)statusCode is < 200 or >= 300)
            return new CommandApiEvaluation(false, $"API_HTTP_{(int)statusCode}", null, false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !CommandApiJson.TryGetBoolean(root, "status", out var succeeded))
                return new CommandApiEvaluation(false, "API_INVALID_RESPONSE", CommandApiJson.ReadMessage(root), false);
            // 服务端超时后仍可能存在已入队任务，失败结果一律不冒充确定的入队前拒绝。
            if (!succeeded)
                return new CommandApiEvaluation(false, "API_GATHERV4_BUSINESS_FAILURE", CommandApiJson.ReadMessage(root), false);
            return _options.ValidateResultData
                ? ValidateResults(root, mapping)
                : new CommandApiEvaluation(true, null, CommandApiJson.ReadMessage(root), false);
        }
        catch (JsonException)
        {
            return new CommandApiEvaluation(false, "API_INVALID_JSON", null, false);
        }
    }

    /// <summary>核对有效表号与采集项覆盖；F1 只证明确认链路，不把其中的零读数当作真实采集值。</summary>
    /// <param name="root">HTTP JSON 对象。</param>
    /// <param name="mapping">本次请求的真实电表映射。</param>
    /// <returns>空数据、错误电表、无效数据或缺项时返回独立稳定错误码。</returns>
    private CommandApiEvaluation ValidateResults(JsonElement root, GatherDeviceMeterMapping mapping)
    {
        if (!TryReadProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return new CommandApiEvaluation(false, "API_GATHERV4_EMPTY_RESULT", "Status 成功但没有设备结果。", false);

        var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryReadProperty(item, "meterId", out var meter) || meter.ValueKind != JsonValueKind.Number || !meter.TryGetInt32(out var meterId) || meterId != mapping.MeterId)
                return new CommandApiEvaluation(false, "API_GATHERV4_RESULT_DEVICE_MISMATCH", "结果电表与本次请求不一致。", false);
            if (!CommandApiJson.TryGetBoolean(item, "validData", out var valid) || !valid)
                return new CommandApiEvaluation(false, "API_GATHERV4_INVALID_RESULT", "设备返回无效或否认结果。", false);
            if (TryReadProperty(item, "dataType", out var type) && type.ValueKind == JsonValueKind.String)
                returned.Add(type.GetString()!);
        }
        var expected = _options.ExpectedResultItemCodes.Count > 0 ? _options.ExpectedResultItemCodes : _options.ItemCodes;
        var missing = expected.Where(code => !returned.Contains(code)).ToArray();
        return missing.Length > 0
            ? new CommandApiEvaluation(false, "API_GATHERV4_PARTIAL_RESULT", $"结果缺少采集项：{string.Join(',', missing)}。", false)
            : new CommandApiEvaluation(true, null, "已核验本表有效结果及配置的全部返回项；未证明真实示值或落库。", false);
    }

    /// <summary>读取大小写不敏感的结果属性，兼容新旧服务的 JSON 命名。</summary>
    /// <param name="root">JSON 对象。</param>
    /// <param name="name">属性名。</param>
    /// <param name="value">找到的属性值。</param>
    /// <returns>属性存在时返回 true。</returns>
    private static bool TryReadProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
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
