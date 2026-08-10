using System.Buffers;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Dlt645;
using DeviceSocket.Host.Networking.QGdw3761;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证设备协议 PipelineFilter 在半包和粘包场景只消费完整帧，并复用精确截取的帧数组。
/// </summary>
public sealed class ProtocolPipelineFilterTests
{
    /// <summary>
    /// 376.1 半包不得推进读取位置，完整粘包每次只能消费一条帧。
    /// </summary>
    [Fact]
    public void QGdw3761Filter_ShouldWaitForPartialFrameAndConsumeOneStickyFrame()
    {
        var first = QGdw3761Codec.CreateLinkRequest("3301", 1, QGdw3761LinkMessageKind.Login);
        var second = QGdw3761Codec.CreateLinkRequest("3301", 2, QGdw3761LinkMessageKind.Heartbeat);
        var filter = new QGdw3761PipelineFilter();
        var partialReader = new SequenceReader<byte>(new ReadOnlySequence<byte>(first.AsMemory(0, first.Length - 1)));

        Assert.Null(filter.Filter(ref partialReader));
        Assert.Equal(0, partialReader.Consumed);

        var sticky = first.Concat(second).ToArray();
        var stickyReader = new SequenceReader<byte>(new ReadOnlySequence<byte>(sticky));
        var firstFrame = filter.Filter(ref stickyReader);

        Assert.True(firstFrame.IsValid);
        Assert.Equal(first.Length, stickyReader.Consumed);
        Assert.Equal(first, firstFrame.Raw);
        var secondFrame = filter.Filter(ref stickyReader);
        Assert.True(secondFrame.IsValid);
        Assert.Equal(sticky.Length, stickyReader.Consumed);
    }

    /// <summary>
    /// 645 半包不得推进读取位置，带 FEH 前导的粘包仍必须逐帧消费。
    /// </summary>
    [Fact]
    public void Dlt645Filter_ShouldWaitForPartialFrameAndConsumeOneStickyFrame()
    {
        var first = Dlt645Codec.CreateFrame("123456789012", 0x11, [0x00, 0x00, 0x01, 0x00], preambleLength: 4);
        var second = Dlt645Codec.CreateFrame("123456789013", 0x11, [0x00, 0x00, 0x01, 0x00]);
        var filter = new Dlt645PipelineFilter();
        var partialReader = new SequenceReader<byte>(new ReadOnlySequence<byte>(first.AsMemory(0, first.Length - 1)));

        Assert.Null(filter.Filter(ref partialReader));
        Assert.Equal(0, partialReader.Consumed);

        var sticky = first.Concat(second).ToArray();
        var stickyReader = new SequenceReader<byte>(new ReadOnlySequence<byte>(sticky));
        var firstFrame = filter.Filter(ref stickyReader);

        Assert.True(firstFrame.IsValid);
        Assert.Equal(first.Length, stickyReader.Consumed);
        Assert.Equal(first, firstFrame.Raw);
        var secondFrame = filter.Filter(ref stickyReader);
        Assert.True(secondFrame.IsValid);
        Assert.Equal(sticky.Length, stickyReader.Consumed);
    }

    /// <summary>
    /// 完整帧专用入口必须把调用方独占数组直接交给帧模型，避免 Codec 再复制一份 Raw。
    /// </summary>
    [Fact]
    public void ReadCompleteFrame_ShouldTransferOwnedRawArrays()
    {
        var qGdw3761Bytes = QGdw3761Codec.CreateLinkRequest("3301", 1, QGdw3761LinkMessageKind.Login);
        var dlt645Bytes = Dlt645Codec.CreateFrame("123456789012", 0x11, [0x00, 0x00, 0x01, 0x00]);

        var qGdw3761 = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(qGdw3761Bytes).Package);
        var dlt645 = Assert.IsType<Dlt645Frame>(Dlt645Codec.ReadCompleteFrame(dlt645Bytes).Package);

        Assert.Same(qGdw3761Bytes, qGdw3761.Raw);
        Assert.Same(dlt645Bytes, dlt645.Raw);
    }
}
