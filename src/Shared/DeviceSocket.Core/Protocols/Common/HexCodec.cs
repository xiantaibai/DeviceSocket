using System.Globalization;

namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 提供协议报文与十六进制文本之间的安全转换。
/// </summary>
public static class HexCodec
{
    /// <summary>
    /// 尝试解析允许空格、短横线或冒号分隔的十六进制文本。
    /// </summary>
    /// <param name="text">待解析的十六进制文本。</param>
    /// <param name="bytes">解析成功后的字节数组。</param>
    /// <param name="error">解析失败时的中文错误信息。</param>
    /// <returns>文本合法时返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? text, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hex 报文不能为空。";
            return false;
        }

        var normalized = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            error = "Hex 报文必须包含偶数个十六进制字符。";
            return false;
        }

        var nonSeparators = text.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not ':').ToArray();
        if (nonSeparators.Any(c => !Uri.IsHexDigit(c)))
        {
            error = "Hex 报文包含非法字符。";
            return false;
        }

        bytes = new byte[normalized.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(normalized.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out bytes[i]))
            {
                bytes = [];
                error = "Hex 报文解析失败。";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 将字节序列转换为大写十六进制文本。
    /// </summary>
    /// <param name="bytes">待转换的字节序列。</param>
    /// <param name="separated">是否使用空格分隔每个字节。</param>
    public static string ToHex(ReadOnlySpan<byte> bytes, bool separated = false) =>
        separated ? string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2"))) : Convert.ToHexString(bytes);
}
