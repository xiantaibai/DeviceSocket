using System.Globalization;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.Encoding;

/// <summary>
/// 使用安全白名单编码器把 JSON 指令和用户参数转换为协议原始报文。
/// </summary>
public sealed class CommandEncoderRegistry
{
    private const string QGdw3761LinkEncoder = "qgdw3761.link";
    private const string Dlt645FrameEncoder = "dlt645.frame";
    private readonly DeclarativeLayoutEncoder _layoutEncoder = new();

    /// <summary>
    /// 按指令模式生成原始报文；任何参数错误都会转换为可展示的异常信息。
    /// </summary>
    /// <param name="command">JSON 中定义的指令。</param>
    /// <param name="values">由界面收集的参数键值。</param>
    /// <returns>生成的报文和摘要。</returns>
    public CommandBuildResult Build(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> values)
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

        return command.Encoder.ToLowerInvariant() switch
        {
            QGdw3761LinkEncoder => BuildQGdw3761Link(effectiveValues),
            Dlt645FrameEncoder => BuildDlt645Frame(effectiveValues),
            _ => throw new InvalidOperationException(
                $"编码器“{command.Encoder}”不在安全白名单中，当前支持 {QGdw3761LinkEncoder}、{Dlt645FrameEncoder}。")
        };
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

    private static CommandBuildResult BuildQGdw3761Link(IReadOnlyDictionary<string, string> values)
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
            heartbeatTime);
        return new CommandBuildResult(
            frame,
            $"376.1 {kind}，设备 3761:{division}-{terminalAddress:D5}，SEQ={sequence}");
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
