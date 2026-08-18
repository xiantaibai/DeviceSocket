using System.Buffers.Binary;
using System.Globalization;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>
/// 按 Gather 老 CmdCenter 的 A15/A14 字段顺序编码 AFN=0CH/F129 读表业务响应。
/// </summary>
internal static class GatherTcpCenterReadingResponseEncoder
{
    private const int FixedOverheadLength = 8;

    /// <summary>
    /// 判断下行帧是否为发往当前虚拟集中器的普通读表命令。
    /// </summary>
    /// <param name="frame">已完成校验的376.1下行帧。</param>
    /// <param name="device">当前 TCP 会话的虚拟设备。</param>
    /// <returns>帧合法、地址匹配且为 AFN=0CH/F129 时返回 true。</returns>
    internal static bool IsReadingRequest(QGdw3761Frame frame, VirtualDeviceDescriptor device) =>
        frame.IsValid &&
        !frame.IsUpstream &&
        frame.IsPrimaryMessage &&
        frame.Afn == 0x0C &&
        frame.Functions.Count == 1 &&
        frame.Functions[0] == 129 &&
        string.Equals(frame.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 创建保留请求地址、MSA（主站地址）、PN（测量点）和 PSEQ 的终端读表业务响应。
    /// </summary>
    /// <param name="request">Gather 老服务下发的 AFN=0CH/F129 命令。</param>
    /// <param name="device">生成稳定模拟示值的虚拟设备。</param>
    /// <param name="options">读表业务响应配置。</param>
    /// <param name="now">写入 A15 终端抄表时间的当前时间。</param>
    /// <param name="businessError">true 时写入老解析器不能形成有效示值的 EE 数据。</param>
    /// <returns>可直接写入 TCP Socket 的完整376.1上行响应。</returns>
    /// <exception cref="ArgumentException">请求不是当前设备的合法普通读表命令。</exception>
    internal static byte[] Create(
        QGdw3761Frame request,
        VirtualDeviceDescriptor device,
        GatherReadingResponseOptions options,
        DateTimeOffset now,
        bool businessError)
    {
        if (!IsReadingRequest(request, device))
            throw new ArgumentException("只能为当前设备的合法 AFN=0CH/F129 下行命令创建读表响应。", nameof(request));

        var application = new List<byte>(12 + options.RateCount * 5)
        {
            0x0C,
            (byte)(0x60 | request.Sequence),
            request.Da1,
            request.Da2,
            0x01,
            0x10
        };
        // Gather 的 A15 按“分、时、日、月、年”低字段在前传输，与常见年月日顺序相反。
        application.Add(ToBcd(now.Minute));
        application.Add(ToBcd(now.Hour));
        application.Add(ToBcd(now.Day));
        application.Add(ToBcd(now.Month));
        application.Add(ToBcd(now.Year % 100));
        application.Add(checked((byte)options.RateCount));

        if (businessError)
        {
            application.AddRange([0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);
            for (var rate = 0; rate < options.RateCount; rate++)
                application.AddRange([0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);
        }
        else
        {
            var total = options.BaseReadingKwh + device.GlobalOrdinal * options.DeviceStepKwh;
            application.AddRange(EncodeA14(total));
            for (var rate = 0; rate < options.RateCount; rate++)
                application.AddRange(EncodeA14(total / Math.Max(1, options.RateCount) + rate * 0.0001m));
        }

        // 老虚拟集中器使用 C=88H 表示终端从动响应；地址五字节必须整体回显，尤其 A3 中的 MSA 用于 ConrLine 命令关联。
        return BuildFrame(0x88, request.Raw.AsSpan(7, 5), application);
    }

    /// <summary>
    /// 把非负电能示值编码为 Gather 附录 A14 的五字节低位 BCD 在前格式。
    /// </summary>
    /// <param name="value">单位 kWh、最多四位小数的示值。</param>
    /// <returns>按万分位到十万位排列的五个 BCD 字节。</returns>
    private static byte[] EncodeA14(decimal value)
    {
        var bounded = Math.Clamp(decimal.Round(value, 4, MidpointRounding.AwayFromZero), 0m, 999_999.9999m);
        var digits = bounded.ToString("000000.0000", CultureInfo.InvariantCulture).Replace(".", string.Empty, StringComparison.Ordinal);
        var result = new byte[5];
        for (var index = 0; index < result.Length; index++)
        {
            var pairOffset = digits.Length - (index + 1) * 2;
            result[index] = (byte)(((digits[pairOffset] - '0') << 4) | (digits[pairOffset + 1] - '0'));
        }

        return result;
    }

    /// <summary>
    /// 按376.1双长度字段、用户区校验和和16H结束符组装完整帧。
    /// </summary>
    /// <param name="control">终端上行控制域。</param>
    /// <param name="address">从请求回显的五字节地址域。</param>
    /// <param name="application">AFN 起始的应用层数据。</param>
    /// <returns>完整且校验和正确的376.1帧。</returns>
    private static byte[] BuildFrame(byte control, ReadOnlySpan<byte> address, IReadOnlyCollection<byte> application)
    {
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
