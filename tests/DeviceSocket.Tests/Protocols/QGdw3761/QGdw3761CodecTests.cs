using System.Buffers.Binary;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Tests.Protocols.QGdw3761;

/// <summary>
/// 验证 Q/GDW 376.1 编解码、链路报文和异常恢复行为。
/// </summary>
public sealed class QGdw3761CodecTests
{
    /// <summary>
    /// 验证登录帧可往返解析，且长度字段的协议标识和长度计算符合文档。
    /// </summary>
    [Fact]
    public void LoginFrame_RoundTripsAccordingToDocumentLengthRules()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest("1234", 1, QGdw3761LinkMessageKind.Login, sequence: 5);

        var result = QGdw3761Codec.Read(bytes);

        Assert.Equal(FrameReadStatus.PackageReady, result.Status);
        Assert.Equal(bytes.Length, result.Consumed);
        var frame = Assert.IsType<QGdw3761Frame>(result.Package);
        Assert.True(frame.IsValid);
        Assert.Equal("3761:1234-00001", frame.DeviceId);
        Assert.Equal(0x02, frame.Afn);
        Assert.Equal(5, frame.Sequence);
        Assert.Equal([0], frame.Points);
        Assert.Equal([1], frame.Functions);
        Assert.Equal(QGdw3761LinkMessageKind.Login, frame.LinkMessageKind);

        var encodedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1, 2));
        Assert.Equal(0b10, encodedLength & 0b11);
        Assert.Equal(bytes.Length, (encodedLength >> 2) + 8);
    }

    /// <summary>
    /// 验证心跳帧携带 6 字节 BCD 终端时钟。
    /// </summary>
    [Fact]
    public void Heartbeat_ContainsSixByteTerminalClock()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest(
            "3301",
            257,
            QGdw3761LinkMessageKind.Heartbeat,
            heartbeatTime: new DateTimeOffset(2026, 7, 23, 8, 9, 10, TimeSpan.FromHours(8)));

        var frame = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(bytes).Package);

        Assert.True(frame.IsValid);
        Assert.Equal(QGdw3761LinkMessageKind.Heartbeat, frame.LinkMessageKind);
        Assert.Equal(new byte[] { 0x10, 0x09, 0x08, 0x23, 0x07, 0x26 }, frame.DataUnitPayload);
    }

    /// <summary>
    /// 验证链路确认使用 AFN=00H、每个数据单元确认 F3，并复用请求序号。
    /// </summary>
    [Fact]
    public void LinkConfirmation_UsesAfn00AndPerDataUnitF3()
    {
        var request = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(
            QGdw3761Codec.CreateLinkRequest("1234", 18, QGdw3761LinkMessageKind.Login, sequence: 7)).Package);

        var response = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(
            QGdw3761Codec.CreateLinkConfirmation(request)).Package);

        Assert.True(response.IsValid);
        Assert.Equal(0x00, response.Afn);
        Assert.Equal([3], response.Functions);
        Assert.Equal(7, response.Sequence);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x01, 0x00, 0x00 }, response.DataUnitPayload);
    }

    /// <summary>
    /// 验证可靠下行准备会保留 SEQ 高位语义、置 CON、重写 PSEQ 和 CS，终端确认则完整回显原 AFN/DA/DT。
    /// </summary>
    [Fact]
    public void ReliableDownstreamAndTerminalConfirmation_RoundTripCorrelationFields()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest("1234", 18, QGdw3761LinkMessageKind.Login, sequence: 2);
        bytes[6] = 0x49;
        bytes[12] = 0x04;
        bytes[^2] = QGdw3761Codec.CalculateChecksum(bytes.AsSpan(6, bytes.Length - 8));
        var command = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(bytes).Package);

        var prepared = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(
            QGdw3761Codec.PrepareReliableDownstreamCommand(command, sequence: 9)).Package);
        var response = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(
            QGdw3761Codec.CreateTerminalConfirmation(prepared)).Package);

        Assert.True(prepared.ConfirmationRequested);
        Assert.Equal(9, prepared.Sequence);
        Assert.True(response.IsUpstream);
        Assert.False(response.IsPrimaryMessage);
        Assert.Equal(9, response.Sequence);
        Assert.Equal(new byte[] { prepared.Afn, prepared.Da1, prepared.Da2, prepared.Dt1, prepared.Dt2, 0x00 }, response.DataUnitPayload);
    }

    /// <summary>
    /// 验证输入半包时请求继续收取数据，不提前消费缓冲区。
    /// </summary>
    [Fact]
    public void PartialFrame_WaitsForMoreData()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest("1234", 1, QGdw3761LinkMessageKind.Login);
        var result = QGdw3761Codec.Read(bytes.AsSpan(0, bytes.Length - 1));
        Assert.Equal(FrameReadStatus.NeedMoreData, result.Status);
        Assert.Equal(0, result.Consumed);
    }

    /// <summary>
    /// 验证错误校验和转换为错误帧而不是抛出异常。
    /// </summary>
    [Fact]
    public void BadChecksum_ReturnsErrorPackageWithoutThrowing()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest("1234", 1, QGdw3761LinkMessageKind.Login);
        bytes[^2]++;

        var frame = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(bytes).Package);

        Assert.False(frame.IsValid);
        Assert.Equal(ProtocolErrorCode.InvalidChecksum, frame.Error?.Code);
    }

    /// <summary>
    /// 验证帧前垃圾字节可被独立消费，后续合法帧仍能继续解析。
    /// </summary>
    [Fact]
    public void GarbagePrefix_IsConsumedBeforeFollowingValidFrame()
    {
        var frame = QGdw3761Codec.CreateLinkRequest("1234", 1, QGdw3761LinkMessageKind.Login);
        var bytes = new byte[] { 0x01, 0x02 }.Concat(frame).ToArray();

        var garbage = QGdw3761Codec.Read(bytes);
        var valid = QGdw3761Codec.Read(bytes.AsSpan(garbage.Consumed));

        Assert.Equal(2, garbage.Consumed);
        Assert.False(garbage.Package?.IsValid);
        Assert.True(valid.Package?.IsValid);
    }
}
