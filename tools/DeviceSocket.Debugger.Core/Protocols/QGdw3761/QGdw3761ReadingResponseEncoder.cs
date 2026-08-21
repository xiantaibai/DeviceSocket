using System.Buffers.Binary;
using System.Globalization;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 按 Gather 老 CmdCenter 的 A15/A14 字段顺序编码 AFN=0CH/F129 设备抄读业务响应。
/// </summary>
public static class QGdw3761ReadingResponseEncoder
{
    private const int FixedOverheadLength = 8;

    /// <summary>
    /// 根据调试界面维护的地址、测量点和读数参数生成可解析的 376.1 上行抄读回复。
    /// </summary>
    /// <param name="administrativeDivision">终端行政区划码，必须是 4 位十进制数字。</param>
    /// <param name="terminalAddress">终端地址，范围 0-65535。</param>
    /// <param name="masterStationAddress">主站地址（MSA），范围 0-127。</param>
    /// <param name="groupAddress">A3 的群地址标志；true 时写入 1，否则写入 0。</param>
    /// <param name="sequence">回复 RSEQ，仅使用低 4 位。</param>
    /// <param name="measurementPoint">测量点 PN，0 表示终端自身，1-2040 按协议位图编码。</param>
    /// <param name="rateCount">返回的费率示值数量，范围 0-8。</param>
    /// <param name="totalReadingKwh">总电能示值，单位 kWh，范围 0-999999.9999。</param>
    /// <param name="readingTime">写入 A15 的终端抄表时间。</param>
    /// <param name="businessError">true 时使用 EEH 填充 A14，模拟老解析器不能形成有效示值的业务失败。</param>
    /// <returns>可直接写入 TCP Socket 的完整 376.1 上行响应报文。</returns>
    /// <exception cref="ArgumentException">任一地址、序号或读数参数超出协议范围时抛出。</exception>
    public static byte[] Create(
        string administrativeDivision,
        ushort terminalAddress,
        byte masterStationAddress,
        bool groupAddress,
        byte sequence,
        int measurementPoint,
        int rateCount,
        decimal totalReadingKwh,
        DateTimeOffset readingTime,
        bool businessError)
    {
        ValidateArguments(
            administrativeDivision,
            masterStationAddress,
            sequence,
            measurementPoint,
            rateCount,
            totalReadingKwh);

        var address = CreateAddress(
            administrativeDivision,
            terminalAddress,
            masterStationAddress,
            groupAddress);
        var point = EncodePoint(measurementPoint);
        var application = new List<byte>(12 + rateCount * 5)
        {
            0x0C,
            (byte)(0x60 | (sequence & 0x0F)),
            point.Da1,
            point.Da2,
            0x01,
            0x10
        };

        // Gather 的 A15 按“分、时、日、月、年”低字段在前传输，与常见年月日顺序相反。
        application.Add(ToBcd(readingTime.Minute));
        application.Add(ToBcd(readingTime.Hour));
        application.Add(ToBcd(readingTime.Day));
        application.Add(ToBcd(readingTime.Month));
        application.Add(ToBcd(readingTime.Year % 100));
        application.Add(checked((byte)rateCount));

        if (businessError)
        {
            application.AddRange([0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);
            for (var rate = 0; rate < rateCount; rate++)
                application.AddRange([0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);
        }
        else
        {
            application.AddRange(EncodeA14(totalReadingKwh));
            for (var rate = 0; rate < rateCount; rate++)
            {
                // 与 LoadTester 保持一致：各费率示值按总示值均分，并增加 0.0001 kWh 使每路数据可区分。
                var rateReading = totalReadingKwh / Math.Max(1, rateCount) + rate * 0.0001m;
                application.AddRange(EncodeA14(rateReading));
            }
        }

        // 老虚拟集中器使用 C=88H 表示终端从动响应；地址五字节必须完整回显，尤其 A3 中的 MSA 用于命令关联。
        return BuildFrame(0x88, address, application);
    }

    private static void ValidateArguments(
        string administrativeDivision,
        byte masterStationAddress,
        byte sequence,
        int measurementPoint,
        int rateCount,
        decimal totalReadingKwh)
    {
        if (administrativeDivision.Length != 4 ||
            administrativeDivision.Any(character => !char.IsDigit(character)))
        {
            throw new ArgumentException("行政区划码必须为 4 位十进制数字。", nameof(administrativeDivision));
        }

        if (masterStationAddress > 127)
            throw new ArgumentOutOfRangeException(nameof(masterStationAddress), "主站地址必须在 0-127 之间。");
        if (sequence > 15)
            throw new ArgumentOutOfRangeException(nameof(sequence), "帧序号必须在 0-15 之间。");
        if (measurementPoint is < 0 or > 2040)
            throw new ArgumentOutOfRangeException(nameof(measurementPoint), "测量点必须在 0-2040 之间。");
        if (rateCount is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(rateCount), "费率示值数量必须在 0-8 之间。");
        if (totalReadingKwh is < 0m or > 999_999.9999m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalReadingKwh),
                "总电能示值必须在 0-999999.9999 kWh 之间。");
        }
    }

    private static byte[] CreateAddress(
        string administrativeDivision,
        ushort terminalAddress,
        byte masterStationAddress,
        bool groupAddress)
    {
        var address = new byte[5];
        address[0] = ToBcd((administrativeDivision[2] - '0') * 10 + (administrativeDivision[3] - '0'));
        address[1] = ToBcd((administrativeDivision[0] - '0') * 10 + (administrativeDivision[1] - '0'));
        BinaryPrimitives.WriteUInt16LittleEndian(address.AsSpan(2, 2), terminalAddress);
        address[4] = (byte)((masterStationAddress << 1) | (groupAddress ? 1 : 0));
        return address;
    }

    private static (byte Da1, byte Da2) EncodePoint(int measurementPoint)
    {
        if (measurementPoint == 0)
            return (0x00, 0x00);

        // PN=0 固定表示终端自身；非零 PN 使用 DA2 作为组号、DA1 作为组内位图。
        var group = (measurementPoint - 1) / 8 + 1;
        var bit = (measurementPoint - 1) % 8;
        return ((byte)(1 << bit), checked((byte)group));
    }

    /// <summary>
    /// 把非负电能示值编码为 Gather 附录 A14 的五字节低位 BCD 在前格式。
    /// </summary>
    /// <param name="value">单位 kWh、最多四位小数的示值。</param>
    /// <returns>按万分位到十万位排列的五个 BCD 字节。</returns>
    private static byte[] EncodeA14(decimal value)
    {
        var bounded = Math.Clamp(
            decimal.Round(value, 4, MidpointRounding.AwayFromZero),
            0m,
            999_999.9999m);
        var digits = bounded
            .ToString("000000.0000", CultureInfo.InvariantCulture)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        var result = new byte[5];
        for (var index = 0; index < result.Length; index++)
        {
            var pairOffset = digits.Length - (index + 1) * 2;
            result[index] = (byte)(((digits[pairOffset] - '0') << 4) | (digits[pairOffset + 1] - '0'));
        }

        return result;
    }

    /// <summary>
    /// 按 376.1 双长度字段、用户区校验和和 16H 结束符组装完整帧。
    /// </summary>
    /// <param name="control">终端上行控制域。</param>
    /// <param name="address">完整五字节地址域。</param>
    /// <param name="application">AFN 起始的应用层数据。</param>
    /// <returns>完整且校验和正确的 376.1 帧。</returns>
    private static byte[] BuildFrame(
        byte control,
        ReadOnlySpan<byte> address,
        IReadOnlyCollection<byte> application)
    {
        if (address.Length != 5)
            throw new ArgumentException("376.1 地址域必须为 5 字节。", nameof(address));

        var userDataLength = 1 + address.Length + application.Count;
        var lengthField = checked((ushort)((userDataLength << 2) | 0b10));
        var frame = new byte[userDataLength + FixedOverheadLength];
        frame[0] = 0x68;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), lengthField);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), lengthField);
        frame[5] = 0x68;
        frame[6] = control;
        address.CopyTo(frame.AsSpan(7, 5));
        application.ToArray().CopyTo(frame, 12);
        frame[^2] = QGdw3761Codec.CalculateChecksum(frame.AsSpan(6, userDataLength));
        frame[^1] = 0x16;
        return frame;
    }

    /// <summary>
    /// 把 0～99 的十进制值编码为单字节 BCD（Binary-Coded Decimal）。
    /// </summary>
    /// <param name="value">待编码十进制值。</param>
    /// <returns>高四位十位、低四位个位的 BCD 字节。</returns>
    private static byte ToBcd(int value) => checked((byte)(((value / 10) << 4) | value % 10));
}
