using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Protocols;

namespace DeviceSocket.LoadTester.Tests.Protocols;

/// <summary>
/// 验证负载设备协议适配器生成的身份报文和严格确认匹配规则。
/// </summary>
public sealed class LoadTestProtocolTests
{
    /// <summary>
    /// 验证376.1登录确认必须匹配设备、序号和登录数据单元。
    /// </summary>
    [Fact]
    public void QGdwLoginConfirmation_MatchesExactRequest()
    {
        var options = TestOptionsFactory.Create();
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var request = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(protocol.CreateLoginFrame(device, 7)).Package);
        var response = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(QGdw3761Codec.CreateLinkConfirmation(request)).Package);

        Assert.True(protocol.IsConfirmation(response, device, ProtocolOperation.Login, 7));
        Assert.False(protocol.IsConfirmation(response, device, ProtocolOperation.Heartbeat, 7));
        Assert.False(protocol.IsConfirmation(response, device, ProtocolOperation.Login, 8));
    }

    /// <summary>
    /// 验证645首帧携带当前设备地址并能被核心编解码器正常解析。
    /// </summary>
    [Fact]
    public void Dlt645LoginFrame_BindsDeviceAddress()
    {
        var options = TestOptionsFactory.Create(LoadProtocol.Dlt6452007);
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.Dlt6452007);

        var frame = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(protocol.CreateLoginFrame(device, 1)).Package);

        Assert.True(frame.IsValid);
        Assert.Equal(device.DeviceId, frame.DeviceId);
        Assert.False(protocol.RequiresLoginConfirmation);
    }

    /// <summary>
    /// 验证376.1需要确认的服务端下行帧会生成合法的终端方向确认帧。
    /// </summary>
    [Fact]
    public void QGdwDownstreamFrame_CreatesTerminalConfirmation()
    {
        var options = TestOptionsFactory.Create();
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var raw = protocol.CreateLoginFrame(device, 5);
        raw[6] &= 0x7F;
        raw[13] |= 0x10;
        raw[^2] = QGdw3761Codec.CalculateChecksum(raw.AsSpan(6, raw.Length - 8));
        var request = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(raw).Package);

        var encoded = Assert.IsType<byte[]>(protocol.CreateDownstreamResponse(request, device, 1));
        var response = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(encoded).Package);

        Assert.True(response.IsValid);
        Assert.True(response.IsUpstream);
        Assert.False(response.IsPrimaryMessage);
        Assert.Equal(0x00, response.Afn);
        Assert.Equal(5, response.Sequence);
    }

    /// <summary>
    /// 验证645读请求会生成带响应方向位和相同数据标识的合法设备应答。
    /// </summary>
    [Fact]
    public void Dlt645DownstreamRequest_CreatesDeviceResponse()
    {
        var options = TestOptionsFactory.Create(LoadProtocol.Dlt6452007);
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.Dlt6452007);
        var requestRaw = Dlt645Codec.CreateFrame(device.MeterAddress, 0x11, [0x00, 0x00, 0x01, 0x00]);
        var request = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(requestRaw).Package);

        var encoded = Assert.IsType<byte[]>(protocol.CreateDownstreamResponse(request, device, 3));
        var response = Assert.IsType<Dlt645Frame>(Dlt645Codec.Read(encoded).Package);

        Assert.True(response.IsValid);
        Assert.True(response.IsResponse);
        Assert.Equal(request.DataIdentifier, response.DataIdentifier);
    }

    /// <summary>
    /// 验证虚拟设备不会应答发往其他协议地址的服务端下行帧。
    /// </summary>
    [Theory]
    [InlineData(LoadProtocol.QGdw3761)]
    [InlineData(LoadProtocol.Dlt6452007)]
    public void DownstreamFrameForAnotherDevice_IsIgnored(LoadProtocol loadProtocol)
    {
        var options = TestOptionsFactory.Create(loadProtocol);
        options.Devices.Count = 2;
        var devices = DeviceAddressAllocator.Allocate(options);
        var protocol = LoadTestProtocolFactory.Create(loadProtocol);
        var otherDevice = devices[1];
        byte[] raw;
        if (loadProtocol == LoadProtocol.QGdw3761)
        {
            raw = protocol.CreateLoginFrame(otherDevice, 5);
            raw[6] &= 0x7F;
            raw[13] |= 0x10;
            raw[^2] = QGdw3761Codec.CalculateChecksum(raw.AsSpan(6, raw.Length - 8));
        }
        else
        {
            raw = Dlt645Codec.CreateFrame(otherDevice.MeterAddress, 0x11, [0x00, 0x00, 0x01, 0x00]);
        }

        var frame = Assert.IsAssignableFrom<DeviceSocket.Core.Protocols.Common.IProtocolFrame>(
            protocol.Read(raw).Frame);

        Assert.Null(protocol.CreateDownstreamResponse(frame, devices[0], 1));
    }
}
