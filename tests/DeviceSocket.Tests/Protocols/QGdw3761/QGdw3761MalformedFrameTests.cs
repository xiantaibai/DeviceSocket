using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Tests.Protocols.QGdw3761;

/// <summary>
/// 验证 Q/GDW 376.1 异常固定报文头的隔离和恢复行为。
/// </summary>
public sealed class QGdw3761MalformedFrameTests
{
    /// <summary>
    /// 验证缺少重复长度域的异常候选帧会被一次消费，且不会吞掉紧随其后的合法帧。
    /// </summary>
    [Fact]
    public void MissingRepeatedLength_IsReportedOnceBeforeFollowingValidFrame()
    {
        var malformed = new byte[]
        {
            0x68, 0x02, 0x00, 0x68,
            0xC9, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x70, 0x00, 0x00, 0x01, 0x00,
            0x3C, 0x16
        };
        var validBytes = QGdw3761Codec.CreateLinkRequest("1234", 1, QGdw3761LinkMessageKind.Login);
        var input = malformed.Concat(validBytes).ToArray();

        var invalid = QGdw3761Codec.Read(input);
        var valid = QGdw3761Codec.Read(input.AsSpan(invalid.Consumed));

        Assert.Equal(malformed.Length, invalid.Consumed);
        Assert.Equal(ProtocolErrorCode.InvalidSecondStart, invalid.Package?.Error?.Code);
        Assert.True(valid.Package?.IsValid);
        Assert.Equal(validBytes.Length, valid.Consumed);
    }
}
