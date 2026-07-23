using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;

namespace DeviceSocket.Tests.Protocols.Dlt645;

/// <summary>
/// 验证 DL/T 645-2007 编解码、前导字节和错误帧恢复行为。
/// </summary>
public sealed class Dlt645CodecTests
{
    /// <summary>
    /// 验证带 4 个 FEH 前导的应答帧可还原地址、数据域和数据标识。
    /// </summary>
    [Fact]
    public void ResponseFrame_RoundTripsAddressDataAndPreamble()
    {
        var data = new byte[] { 0x00, 0x00, 0x01, 0x00, 0x34, 0x12 };
        var bytes = Dlt645Codec.CreateFrame("123456789012", 0x91, data, preambleLength: 4);

        var result = Dlt645Codec.Read(bytes);

        Assert.Equal(bytes.Length, result.Consumed);
        var frame = Assert.IsType<Dlt645Frame>(result.Package);
        Assert.True(frame.IsValid);
        Assert.Equal(4, frame.PreambleLength);
        Assert.Equal("123456789012", frame.Address);
        Assert.Equal("645:123456789012", frame.DeviceId);
        Assert.True(frame.IsResponse);
        Assert.False(frame.IsAbnormalResponse);
        Assert.Equal((uint)0x00010000, frame.DataIdentifier);
        Assert.Equal(data, frame.Data);
        Assert.NotEqual(data, frame.EncodedData);
    }

    /// <summary>
    /// 验证输入半包时等待后续数据而不产生错误帧。
    /// </summary>
    [Fact]
    public void PartialFrame_WaitsForMoreData()
    {
        var bytes = Dlt645Codec.CreateFrame("123456789012", 0x91, [0x00, 0x00, 0x01, 0x00]);
        var result = Dlt645Codec.Read(bytes.AsSpan(0, bytes.Length - 2));
        Assert.Equal(FrameReadStatus.NeedMoreData, result.Status);
    }

    /// <summary>
    /// 验证错误校验和转换为可观察错误帧而不是抛出异常。
    /// </summary>
    [Fact]
    public void BadChecksum_ReturnsErrorPackageWithoutThrowing()
    {
        var bytes = Dlt645Codec.CreateFrame("123456789012", 0x91, [0x00, 0x00, 0x01, 0x00]);
        bytes[^2] ^= 0xFF;

        var frame = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(bytes).Package);

        Assert.False(frame.IsValid);
        Assert.Equal(ProtocolErrorCode.InvalidChecksum, frame.Error?.Code);
    }

    /// <summary>
    /// 验证超过 4 个 FEH 前导时返回可消费的错误，避免连接解析停滞。
    /// </summary>
    [Fact]
    public void MoreThanFourPreambleBytes_ProducesRecoverableError()
    {
        var frame = Dlt645Codec.CreateFrame("123456789012", 0x91, []);
        var bytes = Enumerable.Repeat((byte)0xFE, 5).Concat(frame).ToArray();

        var result = Dlt645Codec.Read(bytes);

        Assert.Equal(FrameReadStatus.PackageReady, result.Status);
        Assert.False(result.Package?.IsValid);
        Assert.True(result.Consumed > 0);
    }

    /// <summary>
    /// 验证通过校验和但不符合 BCD 规则的地址仍会被拒绝。
    /// </summary>
    [Fact]
    public void NonBcdAddress_IsRejectedAfterChecksumValidation()
    {
        var bytes = Dlt645Codec.CreateFrame("123456789012", 0x93, []);
        bytes[1] = 0xFA;
        bytes[^2] = Dlt645Codec.CalculateChecksum(bytes.AsSpan(0, bytes.Length - 2));

        var frame = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(bytes).Package);

        Assert.False(frame.IsValid);
        Assert.Equal(ProtocolErrorCode.InvalidAddress, frame.Error?.Code);
    }

    /// <summary>
    /// 验证非读写数据命令不会把普通数据域误判为数据标识。
    /// </summary>
    [Fact]
    public void NonDataCommand_DoesNotInventADataIdentifier()
    {
        var bytes = Dlt645Codec.CreateFrame("123456789012", 0x93, [0x12, 0x90, 0x78, 0x56, 0x34, 0x12]);
        var frame = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(bytes).Package);
        Assert.True(frame.IsValid);
        Assert.Null(frame.DataIdentifier);
    }
}
