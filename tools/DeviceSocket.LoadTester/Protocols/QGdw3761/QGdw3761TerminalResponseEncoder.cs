using System.Buffers.Binary;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

internal static class QGdw3761TerminalResponseEncoder
{
    internal static byte[] CreateConfirmation(QGdw3761Frame request)
    {
        if (!request.IsValid || request.IsUpstream || request.Raw.Length < 18)
            throw new ArgumentException("只能确认合法的376.1服务端下行帧。", nameof(request));

        var application = new byte[]
        {
            0x00,
            (byte)(0x60 | request.Sequence),
            0x00, 0x00, 0x04, 0x00,
            request.Afn,
            request.Da1, request.Da2, request.Dt1, request.Dt2,
            0x00
        };
        return BuildFrame(0x80, request.Raw.AsSpan(7, 5), application);
    }

    private static byte[] BuildFrame(byte control, ReadOnlySpan<byte> address, ReadOnlySpan<byte> application)
    {
        const int fixedOverheadLength = 8;
        var userDataLength = 1 + address.Length + application.Length;
        // 终端应答使用 DIR=1、PRM=0；长度域仍按376.1规则左移两位并写入协议标识10b。
        var lengthField = checked((ushort)((userDataLength << 2) | 0b10));
        var frame = new byte[userDataLength + fixedOverheadLength];
        frame[0] = 0x68;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), lengthField);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), lengthField);
        frame[5] = 0x68;
        frame[6] = control;
        address.CopyTo(frame.AsSpan(7, 5));
        application.CopyTo(frame.AsSpan(12));
        frame[^2] = QGdw3761Codec.CalculateChecksum(frame.AsSpan(6, userDataLength));
        frame[^1] = 0x16;
        return frame;
    }
}
