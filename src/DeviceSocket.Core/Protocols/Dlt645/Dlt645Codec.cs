using System.Buffers.Binary;
using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Protocols.Dlt645;

/// <summary>
/// 按 DL/T 645-2007 完成帧边界识别、地址解析、数据域变换和帧编码。
/// </summary>
public static class Dlt645Codec
{
    public const int MaximumDataLength = 200;
    public const int MaximumPreambleLength = 4;
    private const int FrameOverheadLength = 12;

    /// <summary>
    /// 从缓冲区读取一条 645 帧，支持文档规定的 0-4 个 FEH 前导字节。
    /// </summary>
    /// <param name="input">当前连接尚未消费的字节。</param>
    public static FrameReadResult<Dlt645Frame> Read(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
            return FrameReadResult<Dlt645Frame>.NeedMoreData();

        var baseIndex = 0;
        while (baseIndex < input.Length && input[baseIndex] == 0xFE && baseIndex < MaximumPreambleLength)
            baseIndex++;

        if (baseIndex == input.Length)
            return FrameReadResult<Dlt645Frame>.NeedMoreData();

        if (input[baseIndex] != 0x68)
        {
            var nextStart = input.IndexOf((byte)0x68);
            var consumed = nextStart > 0 ? nextStart : 1;
            return Invalid(ProtocolErrorCode.GarbageBeforeFrame, "645 帧起始符前存在无效字节或 FEH 前导超过 4 个。", input[..consumed].ToArray(), consumed);
        }

        if (input.Length < baseIndex + 10)
            return FrameReadResult<Dlt645Frame>.NeedMoreData();

        if (input[baseIndex + 7] != 0x68)
            return Invalid(ProtocolErrorCode.InvalidSecondStart, "地址域后的帧起始符不是 68H。", input[..Math.Min(input.Length, baseIndex + 8)].ToArray(), baseIndex + 1);

        var dataLength = input[baseIndex + 9];
        if (dataLength > MaximumDataLength)
            return Invalid(ProtocolErrorCode.FrameTooLarge, "645 数据域长度超过文档规定的 200 字节上限。", input[..(baseIndex + 10)].ToArray(), baseIndex + 1);

        var totalLength = baseIndex + FrameOverheadLength + dataLength;
        if (input.Length < totalLength)
            return FrameReadResult<Dlt645Frame>.NeedMoreData();

        var raw = input[..totalLength].ToArray();
        if (raw[^1] != 0x16)
            return Invalid(ProtocolErrorCode.InvalidEnd, "帧结束符不是 16H。", raw, totalLength);

        var checksumLength = 10 + dataLength;
        // CS 从第一个 68H 累加到数据域末尾，不包含 FEH 前导、CS 和 16H。
        var expectedChecksum = CalculateChecksum(raw.AsSpan(baseIndex, checksumLength));
        if (raw[^2] != expectedChecksum)
            return Invalid(ProtocolErrorCode.InvalidChecksum, $"校验和错误，收到 {raw[^2]:X2}，计算值 {expectedChecksum:X2}。", raw, totalLength);

        var addressBytes = raw.AsSpan(baseIndex + 1, 6);
        if (!IsValidAddress(addressBytes))
            return Invalid(ProtocolErrorCode.InvalidAddress, "645 地址域包含非 BCD 且非 AAH 通配的字节。", raw, totalLength);

        var address = DecodeAddress(addressBytes);
        var control = raw[baseIndex + 8];
        var functionCode = (byte)(control & 0x1F);
        var encodedData = raw.AsSpan(baseIndex + 10, dataLength).ToArray();
        // 645 数据域在线路上传输时统一加 33H，交给业务层前必须逐字节还原。
        var data = encodedData.Select(value => unchecked((byte)(value - 0x33))).ToArray();
        uint? dataIdentifier = data.Length >= 4 && functionCode is 0x11 or 0x12 or 0x14
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : null;

        var frame = new Dlt645Frame
        {
            IsValid = true,
            Raw = raw,
            DeviceId = $"645:{address}",
            PreambleLength = baseIndex,
            Address = address,
            Control = control,
            IsResponse = (control & 0x80) != 0,
            IsAbnormalResponse = (control & 0x40) != 0,
            HasMoreFrames = (control & 0x20) != 0,
            FunctionCode = functionCode,
            EncodedData = encodedData,
            Data = data,
            DataIdentifier = dataIdentifier
        };

        return FrameReadResult<Dlt645Frame>.Ready(frame, totalLength);
    }

    /// <summary>
    /// 使用通信地址、控制码和还原后的数据域创建一条完整 645 帧。
    /// </summary>
    /// <param name="address">按人类阅读顺序书写的 12 位通信地址。</param>
    /// <param name="control">控制码 C。</param>
    /// <param name="data">尚未加 33H 的数据域。</param>
    /// <param name="preambleLength">0-4 个 FEH 唤醒前导字节。</param>
    public static byte[] CreateFrame(string address, byte control, ReadOnlySpan<byte> data, int preambleLength = 0)
    {
        if (data.Length > MaximumDataLength)
            throw new ArgumentOutOfRangeException(nameof(data), "645 数据域不能超过 200 字节。");
        if (preambleLength is < 0 or > MaximumPreambleLength)
            throw new ArgumentOutOfRangeException(nameof(preambleLength), "FEH 前导字节只能为 0-4 个。");

        var addressBytes = EncodeAddress(address);
        var frame = new byte[preambleLength + FrameOverheadLength + data.Length];
        frame.AsSpan(0, preambleLength).Fill(0xFE);
        var start = preambleLength;
        frame[start] = 0x68;
        addressBytes.CopyTo(frame.AsSpan(start + 1, 6));
        frame[start + 7] = 0x68;
        frame[start + 8] = control;
        frame[start + 9] = (byte)data.Length;
        // 数据域在线路上传输前按协议逐字节加 33H；地址字节序已由 EncodeAddress 处理。
        for (var index = 0; index < data.Length; index++)
            frame[start + 10 + index] = unchecked((byte)(data[index] + 0x33));
        frame[^2] = CalculateChecksum(frame.AsSpan(start, 10 + data.Length));
        frame[^1] = 0x16;
        return frame;
    }

    /// <summary>
    /// 计算从第一个 68H 到数据域末尾的模 256 算术和。
    /// </summary>
    public static byte CalculateChecksum(ReadOnlySpan<byte> frameWithoutChecksumAndEnd)
    {
        var sum = 0;
        foreach (var value in frameWithoutChecksumAndEnd)
            sum += value;
        return unchecked((byte)sum);
    }

    private static string DecodeAddress(ReadOnlySpan<byte> bytes) =>
        string.Concat(bytes.ToArray().Reverse().Select(value => value.ToString("X2")));

    private static byte[] EncodeAddress(string address)
    {
        if (address.Length != 12 || address.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("645 通信地址必须为 12 个十六进制字符。", nameof(address));

        var result = new byte[6];
        for (var index = 0; index < result.Length; index++)
            result[index] = Convert.ToByte(address.Substring((5 - index) * 2, 2), 16);

        if (!IsValidAddress(result))
            throw new ArgumentException("645 通信地址每字节必须为 BCD 或 AAH 通配符。", nameof(address));
        return result;
    }

    private static bool IsValidAddress(ReadOnlySpan<byte> address)
    {
        foreach (var value in address)
        {
            if (value != 0xAA && ((value >> 4) > 9 || (value & 0x0F) > 9))
                return false;
        }

        return true;
    }

    private static FrameReadResult<Dlt645Frame> Invalid(ProtocolErrorCode code, string message, byte[] raw, int consumed) =>
        FrameReadResult<Dlt645Frame>.Ready(Dlt645Frame.Invalid(code, message, raw), consumed);
}
