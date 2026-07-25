using System.Buffers.Binary;
using System.Globalization;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.Encoding;

/// <summary>
/// 根据 JSON 字段布局安全拼装报文，并在第二阶段计算长度、复制字段和校验和。
/// </summary>
public sealed class DeclarativeLayoutEncoder
{
    private sealed record FieldRange(int Offset, int Length);

    /// <summary>
    /// 根据声明式字段顺序构建原始字节。
    /// </summary>
    /// <param name="layout">JSON 中的字段布局。</param>
    /// <param name="values">已经合并固定参数后的有效输入。</param>
    /// <returns>完整原始报文。</returns>
    public byte[] Build(
        CommandLayoutDefinition layout,
        IReadOnlyDictionary<string, string> values)
    {
        var bytes = new List<byte>();
        var ranges = new Dictionary<string, FieldRange>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layout.Fields)
        {
            if (ranges.ContainsKey(field.Name))
                throw new FormatException($"布局字段“{field.Name}”重复。");

            var offset = bytes.Count;
            byte[] fieldBytes;
            switch (field.Source.ToLowerInvariant())
            {
                case "fixed":
                    fieldBytes = ParseHex(field.Value, field.Name);
                    break;
                case "input":
                    fieldBytes = EncodeInput(field, values);
                    break;
                case "computed":
                case "copy":
                    if (field.Size is < 1 or > 8)
                        throw new FormatException($"字段“{field.Name}”预留长度必须是 1-8。");
                    // 先为前置长度等字段预留位置，所有字段边界确定后再统一回填。
                    fieldBytes = new byte[field.Size];
                    break;
                default:
                    throw new FormatException($"字段“{field.Name}”使用了未知来源“{field.Source}”。");
            }

            bytes.AddRange(fieldBytes);
            ranges[field.Name] = new FieldRange(offset, fieldBytes.Length);
        }

        var result = bytes.ToArray();
        foreach (var field in layout.Fields)
        {
            if (field.Source.Equals("computed", StringComparison.OrdinalIgnoreCase))
                FillComputedField(field, result, ranges);
            else if (field.Source.Equals("copy", StringComparison.OrdinalIgnoreCase))
                FillCopiedField(field, result, ranges);
        }

        return result;
    }

    private static byte[] EncodeInput(
        CommandLayoutFieldDefinition field,
        IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(field.InputKey, out var value))
            throw new FormatException($"字段“{field.Name}”缺少输入参数“{field.InputKey}”。");

        return field.Encoding.ToLowerInvariant() switch
        {
            "hex" => ParseHex(value, field.Name),
            "hexadd33" => ParseHex(value, field.Name)
                .Select(item => unchecked((byte)(item + 0x33)))
                .ToArray(),
            "bcdreverse" => EncodeReversedBcd(value, field.Name),
            "byte" => [ParseByte(value, field.Name)],
            _ => throw new FormatException($"字段“{field.Name}”使用了未知编码“{field.Encoding}”。")
        };
    }

    private static void FillComputedField(
        CommandLayoutFieldDefinition field,
        byte[] result,
        IReadOnlyDictionary<string, FieldRange> ranges)
    {
        var target = GetRange(ranges, field.Name);
        ulong value = field.Algorithm.ToLowerInvariant() switch
        {
            "bytelength" => (ulong)GetCoveredLength(field, ranges),
            "qgdw3761length" => checked(((ulong)GetCoveredLength(field, ranges) << 2) | 0b10UL),
            "sum8" => CalculateSum8(result, GetCoveredRange(field, ranges)),
            _ => throw new FormatException($"字段“{field.Name}”使用了未知计算算法“{field.Algorithm}”。")
        };
        WriteUnsigned(value, result.AsSpan(target.Offset, target.Length), field.Endian, field.Name);
    }

    private static void FillCopiedField(
        CommandLayoutFieldDefinition field,
        byte[] result,
        IReadOnlyDictionary<string, FieldRange> ranges)
    {
        var target = GetRange(ranges, field.Name);
        var source = GetRange(ranges, field.CopyField);
        if (target.Length != source.Length)
            throw new FormatException($"复制字段“{field.Name}”与来源“{field.CopyField}”长度不一致。");
        result.AsSpan(source.Offset, source.Length).CopyTo(result.AsSpan(target.Offset, target.Length));
    }

    private static int GetCoveredLength(
        CommandLayoutFieldDefinition field,
        IReadOnlyDictionary<string, FieldRange> ranges)
    {
        var range = GetCoveredRange(field, ranges);
        return range.Length;
    }

    private static FieldRange GetCoveredRange(
        CommandLayoutFieldDefinition field,
        IReadOnlyDictionary<string, FieldRange> ranges)
    {
        var from = GetRange(ranges, field.FromField);
        var to = string.IsNullOrWhiteSpace(field.ToField) ? from : GetRange(ranges, field.ToField);
        var end = to.Offset + to.Length;
        if (end < from.Offset)
            throw new FormatException($"字段“{field.Name}”的计算范围顺序无效。");
        return new FieldRange(from.Offset, end - from.Offset);
    }

    private static FieldRange GetRange(
        IReadOnlyDictionary<string, FieldRange> ranges,
        string fieldName)
    {
        if (!ranges.TryGetValue(fieldName, out var range))
            throw new FormatException($"布局引用了不存在的字段“{fieldName}”。");
        return range;
    }

    private static byte CalculateSum8(byte[] bytes, FieldRange range)
    {
        var sum = 0;
        foreach (var value in bytes.AsSpan(range.Offset, range.Length))
            sum += value;
        return unchecked((byte)sum);
    }

    private static void WriteUnsigned(
        ulong value,
        Span<byte> destination,
        string endian,
        string fieldName)
    {
        var maximum = destination.Length == 8
            ? ulong.MaxValue
            : (1UL << (destination.Length * 8)) - 1;
        if (value > maximum)
            throw new OverflowException($"字段“{fieldName}”的计算结果 {value} 超出 {destination.Length} 字节范围。");

        Span<byte> storage = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(storage, value);
        if (endian.Equals("little", StringComparison.OrdinalIgnoreCase))
        {
            storage[..destination.Length].CopyTo(destination);
            return;
        }
        if (!endian.Equals("big", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"字段“{fieldName}”的字节序必须是 little 或 big。");
        for (var index = 0; index < destination.Length; index++)
            destination[index] = storage[destination.Length - index - 1];
    }

    private static byte[] EncodeReversedBcd(string value, string fieldName)
    {
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
            throw new FormatException($"字段“{fieldName}”必须包含偶数个 BCD/AA 字符。");
        var bytes = ParseHex(normalized, fieldName);
        foreach (var item in bytes)
        {
            if (item != 0xAA && ((item >> 4) > 9 || (item & 0x0F) > 9))
                throw new FormatException($"字段“{fieldName}”包含非 BCD 且非 AAH 的地址字节。");
        }

        Array.Reverse(bytes);
        return bytes;
    }

    private static byte ParseByte(string value, string fieldName)
    {
        var normalized = value.Trim();
        var isHex = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith('H') ||
                    normalized.Any(character => character is >= 'A' and <= 'F' or >= 'a' and <= 'f');
        normalized = normalized.TrimEnd('H', 'h');
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        if (byte.TryParse(normalized, style, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException($"字段“{fieldName}”的单字节值“{value}”无效。");
    }

    private static byte[] ParseHex(string value, string fieldName)
    {
        if (HexCodec.TryParse(value, out var bytes, out var error))
            return bytes;
        throw new FormatException($"字段“{fieldName}”：{error}");
    }
}
