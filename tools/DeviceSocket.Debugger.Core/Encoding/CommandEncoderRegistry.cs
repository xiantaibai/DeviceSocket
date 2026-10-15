using System.Globalization;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Core.CommandSets.Models;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Encoding;

/// <summary>
/// 使用安全白名单编码器把 JSON 指令和用户参数转换为协议原始报文。
/// </summary>
public sealed class CommandEncoderRegistry
{
    private const string QGdw3761LinkEncoder = "qgdw3761.link";
    private const string QGdw3761GatherReadingResponseEncoder = "qgdw3761.gatherReadingResponse";
    private const string QGdw3761TerminalConfirmationEncoder = "qgdw3761.terminalConfirmation";
    private const string Dlt645FrameEncoder = "dlt645.frame";
    private readonly DeclarativeLayoutEncoder _layoutEncoder = new();

    /// <summary>
    /// 按指令模式生成原始报文；任何参数错误都会转换为可展示的异常信息。
    /// </summary>
    /// <param name="command">JSON 中定义的指令。</param>
    /// <param name="values">由界面收集的参数键值。</param>
    /// <param name="serverProfile">当前 376.1 服务端兼容类型；非 376.1 指令忽略该参数。</param>
    /// <returns>生成的报文和摘要。</returns>
    public CommandBuildResult Build(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> values,
        QGdw3761ServerCompatibilityProfile serverProfile =
            QGdw3761ServerCompatibilityProfile.Standard)
    {
        ValidateRequiredInputs(command, values);
        var effectiveValues = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var fixedValue in command.FixedValues)
            effectiveValues[fixedValue.Key] = fixedValue.Value;

        if (command.Mode.Equals("layout", StringComparison.OrdinalIgnoreCase))
        {
            var layout = command.Layout ??
                         throw new InvalidOperationException("layout 模式缺少字段布局。");
            var bytes = _layoutEncoder.Build(layout, effectiveValues);
            return new CommandBuildResult(bytes, $"声明式布局生成，共 {bytes.Length} 字节");
        }
        if (command.Mode.Equals("raw", StringComparison.OrdinalIgnoreCase))
            return BuildRaw(command, effectiveValues);

        var encoder = command.Encoder.Trim();
        if (encoder.Equals(QGdw3761LinkEncoder, StringComparison.OrdinalIgnoreCase))
            return BuildQGdw3761Link(effectiveValues, serverProfile);
        if (encoder.Equals(QGdw3761GatherReadingResponseEncoder, StringComparison.OrdinalIgnoreCase))
            return BuildQGdw3761GatherReadingResponse(effectiveValues);
        if (encoder.Equals(QGdw3761TerminalConfirmationEncoder, StringComparison.OrdinalIgnoreCase))
            return BuildQGdw3761TerminalConfirmation(effectiveValues, serverProfile);
        if (encoder.Equals(Dlt645FrameEncoder, StringComparison.OrdinalIgnoreCase))
            return BuildDlt645Frame(effectiveValues);

        throw new InvalidOperationException(
            $"编码器“{command.Encoder}”不在安全白名单中，当前支持 {QGdw3761LinkEncoder}、" +
            $"{QGdw3761GatherReadingResponseEncoder}、{QGdw3761TerminalConfirmationEncoder}、{Dlt645FrameEncoder}。");
    }

    private static CommandBuildResult BuildRaw(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> values)
    {
        var expanded = command.RawHex ?? string.Empty;
        foreach (var value in values)
        {
            // raw 模板只执行明确的文本占位替换，绝不执行脚本或表达式。
            expanded = expanded.Replace(
                $"{{{{{value.Key}}}}}",
                value.Value,
                StringComparison.OrdinalIgnoreCase);
        }

        if (expanded.Contains("{{", StringComparison.Ordinal))
            throw new FormatException("原始报文模板仍包含未解析的参数占位符。");
        if (!HexCodec.TryParse(expanded, out var bytes, out var error))
            throw new FormatException(error);

        return new CommandBuildResult(bytes, "固定 HEX 指令");
    }

    /// <summary>按当前服务端兼容类型生成登录、退出或心跳链路请求。</summary>
    /// <param name="values">界面输入和指令固定参数。</param>
    /// <param name="serverProfile">决定 GatherV4 是否设置 CON=1 的服务端兼容类型。</param>
    /// <returns>完整链路请求及其显示摘要。</returns>
    private static CommandBuildResult BuildQGdw3761Link(
        IReadOnlyDictionary<string, string> values,
        QGdw3761ServerCompatibilityProfile serverProfile)
    {
        var division = GetValue(values, "administrativeDivision");
        var terminalAddress = ParseUInt16(GetValue(values, "terminalAddress"), "终端地址");
        var kindText = GetValue(values, "linkKind");
        if (!Enum.TryParse<QGdw3761LinkMessageKind>(kindText, true, out var kind) ||
            kind == QGdw3761LinkMessageKind.Other)
            throw new FormatException($"链路消息类型“{kindText}”无效。");

        var sequence = ParseByte(GetValue(values, "sequence", "0"), "帧序号");
        if (sequence > 15)
            throw new FormatException("帧序号必须在 0-15 之间。");

        DateTimeOffset? heartbeatTime = null;
        var heartbeatText = GetValue(values, "heartbeatTime", string.Empty);
        if (!string.IsNullOrWhiteSpace(heartbeatText))
        {
            if (!DateTimeOffset.TryParse(heartbeatText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
                throw new FormatException("心跳时间格式无效，请使用 yyyy-MM-dd HH:mm:ss 或留空使用当前时间。");
            heartbeatTime = parsed;
        }

        var frame = QGdw3761Codec.CreateLinkRequest(
            division,
            terminalAddress,
            kind,
            sequence,
            heartbeatTime,
            confirmationRequested: serverProfile == QGdw3761ServerCompatibilityProfile.GatherV4);
        return new CommandBuildResult(
            frame,
            $"376.1 {kind}，设备 3761:{division}-{terminalAddress:D5}，SEQ={sequence}" +
            (serverProfile == QGdw3761ServerCompatibilityProfile.GatherV4
                ? "，GatherV4 CON=1"
                : string.Empty));
    }

    /// <summary>根据 GatherV4 主站下行帧生成保留 A3、复用 PSEQ 的 AFN=00H/F1 或 F2 任务结果。</summary>
    /// <param name="values">包含下行原始 HEX 和任务结果值的参数集合；0 表示成功，非 0 表示失败。</param>
    /// <param name="serverProfile">当前服务端兼容类型；必须为 GatherV4。</param>
    /// <returns>完整终端确认帧及其关联字段摘要。</returns>
    /// <exception cref="InvalidOperationException">当前未选择 GatherV4 服务类型时抛出。</exception>
    /// <exception cref="FormatException">原始 HEX、帧方向、校验和或错误码无效时抛出。</exception>
    private static CommandBuildResult BuildQGdw3761TerminalConfirmation(
        IReadOnlyDictionary<string, string> values,
        QGdw3761ServerCompatibilityProfile serverProfile)
    {
        if (serverProfile != QGdw3761ServerCompatibilityProfile.GatherV4)
            throw new InvalidOperationException("发送 GatherV4 任务结果前，请把服务类型切换为 GatherV4。");

        var requestHex = GetValue(values, "requestHex");
        if (!HexCodec.TryParse(requestHex, out var requestBytes, out var parseError))
            throw new FormatException($"主站下发报文：{parseError}");

        var readResult = QGdw3761Codec.ReadCompleteFrame(requestBytes);
        if (readResult.Status == FrameReadStatus.NeedMoreData ||
            readResult.Consumed != requestBytes.Length ||
            readResult.Package is not QGdw3761Frame request ||
            !request.IsValid)
        {
            var protocolError = readResult.Package?.Error?.Message ?? "报文长度不完整或包含多余字节。";
            throw new FormatException($"主站下发报文无效：{protocolError}");
        }

        var errorCode = ParseByte(GetValue(values, "errorCode", "0"), "确认错误码");
        byte[] response;
        try
        {
            response = QGdw3761Codec.CreateTerminalAllResult(request, accepted: errorCode == 0);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException(exception.Message, exception);
        }

        var resultFunction = errorCode == 0 ? "F1 全部确认" : "F2 全部否认";
        return new CommandBuildResult(
            response,
            $"GatherV4 AFN=00H/{resultFunction}，设备 {request.DeviceId}，" +
            $"原 AFN={request.Afn:X2}H，A3={request.MasterStationAddress}，RSEQ={request.Sequence}，任务结果值={errorCode}");
    }

    private static CommandBuildResult BuildQGdw3761GatherReadingResponse(
        IReadOnlyDictionary<string, string> values)
    {
        var division = GetValue(values, "administrativeDivision");
        var terminalAddress = ParseUInt16(GetValue(values, "terminalAddress"), "终端地址");
        var masterStationAddress = ParseByte(GetValue(values, "masterStationAddress"), "主站地址");
        if (masterStationAddress > 127)
            throw new FormatException("主站地址必须在 0-127 之间。");

        var groupAddressText = GetValue(values, "groupAddress", "false");
        if (!bool.TryParse(groupAddressText, out var groupAddress))
            throw new FormatException($"群地址标志“{groupAddressText}”无效，应填写 true 或 false。");

        var sequence = ParseByte(GetValue(values, "sequence", "0"), "帧序号");
        if (sequence > 15)
            throw new FormatException("帧序号必须在 0-15 之间。");

        var measurementPoint = ParseInt32(GetValue(values, "measurementPoint", "0"), "测量点");
        if (measurementPoint is < 0 or > 2040)
            throw new FormatException("测量点必须在 0-2040 之间，0 表示终端自身。");

        var rateCount = ParseInt32(GetValue(values, "rateCount", "4"), "费率示值数量");
        if (rateCount is < 0 or > 8)
            throw new FormatException("费率示值数量必须在 0-8 之间。");

        var totalReadingKwh = ParseDecimal(GetValue(values, "totalReadingKwh"), "总电能示值");
        if (totalReadingKwh is < 0m or > 999_999.9999m)
            throw new FormatException("总电能示值必须在 0-999999.9999 kWh 之间。");

        var readingTime = DateTimeOffset.Now;
        var readingTimeText = GetValue(values, "readingTime", string.Empty);
        if (!string.IsNullOrWhiteSpace(readingTimeText))
        {
            if (!DateTimeOffset.TryParse(readingTimeText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out readingTime))
                throw new FormatException("抄表时间格式无效，请使用 yyyy-MM-dd HH:mm:ss 或留空使用当前时间。");
        }

        var businessErrorText = GetValue(values, "businessError", "false");
        if (!bool.TryParse(businessErrorText, out var businessError))
            throw new FormatException($"业务失败标志“{businessErrorText}”无效，应填写 true 或 false。");

        var frame = QGdw3761ReadingResponseEncoder.Create(
            division,
            terminalAddress,
            masterStationAddress,
            groupAddress,
            sequence,
            measurementPoint,
            rateCount,
            totalReadingKwh,
            readingTime,
            businessError);
        return new CommandBuildResult(
            frame,
            $"376.1 AFN=0CH/F129 抄读回复，设备 3761:{division}-{terminalAddress:D5}，" +
            $"PN={measurementPoint}，SEQ={sequence}，费率示值 {rateCount} 个，" +
            $"总示值 {totalReadingKwh:0.0000} kWh{(businessError ? "，业务失败" : string.Empty)}");
    }

    private static CommandBuildResult BuildDlt645Frame(IReadOnlyDictionary<string, string> values)
    {
        var address = GetValue(values, "address");
        var control = ParseByte(GetValue(values, "control"), "控制码");
        var preambleLength = ParseInt32(GetValue(values, "preambleLength", "0"), "前导字节数");
        var dataHex = GetValue(values, "dataHex", string.Empty);
        byte[] data = [];
        if (!string.IsNullOrWhiteSpace(dataHex) && !HexCodec.TryParse(dataHex, out data, out var error))
            throw new FormatException($"数据域：{error}");

        var frame = Dlt645Codec.CreateFrame(address, control, data, preambleLength);
        return new CommandBuildResult(
            frame,
            $"DL/T 645 地址 {address}，控制码 {control:X2}H，还原后数据域 {HexCodec.ToHex(data, true)}");
    }

    private static void ValidateRequiredInputs(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var input in command.Inputs)
        {
            if (input.Required && string.IsNullOrWhiteSpace(GetValue(values, input.Key, string.Empty)))
                throw new FormatException($"参数“{input.Label}”不能为空。");
        }
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string? defaultValue = null)
    {
        if (values.TryGetValue(key, out var value))
            return value.Trim();
        if (defaultValue is not null)
            return defaultValue;
        throw new FormatException($"缺少参数“{key}”。");
    }

    private static byte ParseByte(string value, string displayName)
    {
        if (TryParseUnsigned(value, byte.MaxValue, out var parsed))
            return (byte)parsed;
        throw new FormatException($"{displayName}“{value}”无效，应为 0-255 或 00-FF 的十六进制值。");
    }

    private static ushort ParseUInt16(string value, string displayName)
    {
        if (TryParseUnsigned(value, ushort.MaxValue, out var parsed))
            return (ushort)parsed;
        throw new FormatException($"{displayName}“{value}”无效，应为 0-65535。");
    }

    private static int ParseInt32(string value, string displayName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException($"{displayName}“{value}”无效，应为十进制整数。");
    }

    private static decimal ParseDecimal(string value, string displayName)
    {
        if (decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException($"{displayName}“{value}”无效，应为十进制数值。");
    }

    private static bool TryParseUnsigned(string value, uint maximum, out uint parsed)
    {
        var normalized = value.Trim();
        var isHex = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith('H') ||
                    normalized.Any(character => character is >= 'A' and <= 'F' or >= 'a' and <= 'f');
        normalized = normalized.TrimEnd('H', 'h');
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        return uint.TryParse(normalized, style, CultureInfo.InvariantCulture, out parsed) && parsed <= maximum;
    }
}
