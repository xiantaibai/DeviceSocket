using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Core.Decoding;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证调试工具接收层沿用核心协议的半包和粘包读取语义。
/// </summary>
public sealed class ProtocolFrameReaderTests
{
    /// <summary>
    /// 验证半包不消费数据，补齐后只消费第一帧并保留粘连的第二帧。
    /// </summary>
    [Fact]
    public void QGdw3761_PartialAndStickyFrames_AreReadIncrementally()
    {
        var first = QGdw3761Codec.CreateLinkRequest("0000", 1, QGdw3761LinkMessageKind.Login, 1);
        var second = QGdw3761Codec.CreateLinkRequest("0000", 1, QGdw3761LinkMessageKind.Heartbeat, 2);

        var partial = ProtocolFrameReader.Read(DeviceProtocol.QGdw3761, first.AsSpan(0, first.Length - 1));
        var sticky = ProtocolFrameReader.Read(DeviceProtocol.QGdw3761, first.Concat(second).ToArray());

        Assert.True(partial.NeedMoreData);
        Assert.Equal(0, partial.Consumed);
        Assert.False(sticky.NeedMoreData);
        Assert.Equal(first.Length, sticky.Consumed);
        Assert.True(sticky.Frame?.IsValid);
    }
}
