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
    /// 验证 Gather 兼容模式接受地址匹配、无数据区且固定 SEQ 的老式 F1/F3 确认，同时标准模式保持严格拒绝。
    /// </summary>
    [Fact]
    public void GatherQGdwConfirmation_AcceptsLegacyAcknowledgementOnly()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 2;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        var devices = DeviceAddressAllocator.Allocate(options);
        var gatherProtocol = LoadTestProtocolFactory.Create(options.Target);
        var standardProtocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var legacyF1 = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(
            Convert.FromHexString("6832003200680B0133010002006000000100A316")).Package);
        var legacyF3 = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.Read(
            Convert.FromHexString("6832003200680B0133010002006000000400A616")).Package);

        Assert.True(gatherProtocol.IsConfirmation(legacyF1, devices[0], ProtocolOperation.Login, 7));
        Assert.True(gatherProtocol.IsConfirmation(legacyF1, devices[0], ProtocolOperation.Heartbeat, 9));
        Assert.True(gatherProtocol.IsConfirmation(legacyF3, devices[0], ProtocolOperation.Heartbeat, 11));
        Assert.False(gatherProtocol.IsConfirmation(legacyF1, devices[1], ProtocolOperation.Login, 7));
        Assert.False(standardProtocol.IsConfirmation(legacyF1, devices[0], ProtocolOperation.Login, 0));
    }

    /// <summary>
    /// 验证 GatherV4 登录帧设置 CON=1，且只有地址和 PSEQ 完全匹配的主站 AFN=00H/F1 才能完成业务登录。
    /// </summary>
    [Fact]
    public void GatherV4Login_RequestsAndMatchesExactF1Confirmation()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options.Target);
        var loginRaw = protocol.CreateLoginFrame(device, 7);
        var login = Assert.IsType<QGdw3761Frame>(protocol.Read(loginRaw).Frame);

        Assert.True(login.ConfirmationRequested);
        Assert.Equal(7, login.Sequence);

        var acknowledgementRaw = loginRaw.ToArray();
        acknowledgementRaw[6] = 0x40;
        acknowledgementRaw[12] = 0x00;
        acknowledgementRaw[13] = 0x67;
        acknowledgementRaw[14] = 0x00;
        acknowledgementRaw[15] = 0x00;
        acknowledgementRaw[16] = 0x01;
        acknowledgementRaw[17] = 0x00;
        acknowledgementRaw[^2] = QGdw3761Codec.CalculateChecksum(
            acknowledgementRaw.AsSpan(6, acknowledgementRaw.Length - 8));
        var acknowledgement = Assert.IsType<QGdw3761Frame>(protocol.Read(acknowledgementRaw).Frame);
        var acknowledgementClassification = Assert.IsAssignableFrom<IDownstreamFrameClassifier>(protocol)
            .ClassifyDownstream(acknowledgement, device);

        Assert.True(protocol.IsConfirmation(acknowledgement, device, ProtocolOperation.Login, 7));
        Assert.False(protocol.IsConfirmation(acknowledgement, device, ProtocolOperation.Login, 6));
        Assert.False(protocol.IsConfirmation(acknowledgement, device, ProtocolOperation.Heartbeat, 8));
        Assert.Null(protocol.CreateDownstreamResponse(acknowledgement, device, sequence: 1));
        Assert.Equal(DownstreamClassificationKind.GatherV4Acknowledgement, acknowledgementClassification.Kind);
    }

    /// <summary>
    /// 验证 GatherV4 下行命令应答保留 A3 并复用 PSEQ，使服务端 Code-A3-PSEQ 标识可以释放当前在途窗口。
    /// </summary>
    [Fact]
    public void GatherV4DownstreamResponse_PreservesA3AndPseq()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options.Target);
        var raw = protocol.CreateLoginFrame(device, 9);
        raw[6] = 0x4B;
        raw[11] = 0x4A;
        raw[12] = 0x0C;
        raw[13] = 0x79;
        raw[^2] = QGdw3761Codec.CalculateChecksum(raw.AsSpan(6, raw.Length - 8));
        var command = Assert.IsType<QGdw3761Frame>(protocol.Read(raw).Frame);

        var plan = Assert.IsType<DownstreamResponsePlan>(
            protocol.CreateDownstreamResponse(command, device, sequence: 1));
        var response = Assert.IsType<QGdw3761Frame>(
            protocol.Read(Assert.IsType<byte[]>(plan.Payload)).Frame);
        var classification = Assert.IsAssignableFrom<IDownstreamFrameClassifier>(protocol)
            .ClassifyDownstream(command, device);

        Assert.Equal(DownstreamResponseKind.GatherV4Command, plan.Kind);
        Assert.Equal(command.MasterStationAddress, response.MasterStationAddress);
        Assert.Equal(command.Sequence, response.Sequence);
        Assert.Equal(DownstreamClassificationKind.GatherV4Command, classification.Kind);
        Assert.True(classification.IsRecognizedCommand);
    }

    /// <summary>验证 GatherV4 可复现延迟、丢弃、重复、错误 A3 和错误 PSEQ 应答。</summary>
    /// <param name="mode">待模拟的终端应答模式。</param>
    /// <param name="expectedSendCount">预期写出次数。</param>
    [Theory]
    [InlineData(GatherV4ResponseMode.Delayed, 1)]
    [InlineData(GatherV4ResponseMode.Drop, 0)]
    [InlineData(GatherV4ResponseMode.Duplicate, 2)]
    [InlineData(GatherV4ResponseMode.WrongA3, 1)]
    [InlineData(GatherV4ResponseMode.WrongPseq, 1)]
    public void GatherV4ResponseMode_CreatesExpectedAssociationFault(
        GatherV4ResponseMode mode,
        int expectedSendCount)
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        options.Workload.GatherV4Response.Mode = mode;
        options.Workload.GatherV4Response.DelayMilliseconds = 321;
        options.Safety.AllowFaultInjection = true;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options);
        var command = CreateGatherReadingRequest(protocol, device, masterStationAddress: 37, sequence: 9, point: 1);

        var plan = Assert.IsType<DownstreamResponsePlan>(
            protocol.CreateDownstreamResponse(command, device, sequence: 2));

        Assert.Equal(expectedSendCount, plan.SendCount);
        Assert.Equal(mode == GatherV4ResponseMode.Delayed ? 321 : 0, plan.Delay.TotalMilliseconds);
        if (expectedSendCount == 0)
        {
            Assert.Null(plan.Payload);
            return;
        }

        var response = Assert.IsType<QGdw3761Frame>(protocol.Read(Assert.IsType<byte[]>(plan.Payload)).Frame);
        Assert.Equal(mode == GatherV4ResponseMode.WrongA3, response.MasterStationAddress != command.MasterStationAddress);
        Assert.Equal(mode == GatherV4ResponseMode.WrongPseq, response.Sequence != command.Sequence);
    }

    /// <summary>验证 GatherV4 业务上报模板按当前设备重写地址和 PSEQ，并保持业务 AFN 后重新计算校验和。</summary>
    [Fact]
    public void GatherV4ReportTemplate_RewritesDeviceAndSequence()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 2;
        var devices = DeviceAddressAllocator.Allocate(options);
        var sourceProtocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var template = sourceProtocol.CreateLoginFrame(devices[0], 1);
        template[12] = 0x0E;
        template[^2] = QGdw3761Codec.CalculateChecksum(template.AsSpan(6, template.Length - 8));
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        options.Workload.ReportsEnabled = true;
        options.Workload.GatherV4Report.FrameHexTemplate = Convert.ToHexString(template);

        Assert.Empty(LoadTestOptionsValidator.Validate(options));
        var protocol = LoadTestProtocolFactory.Create(options);
        var reportRaw = Assert.IsType<byte[]>(protocol.CreateReportFrame(devices[1], 11, DateTimeOffset.Now));
        var report = Assert.IsType<QGdw3761Frame>(protocol.Read(reportRaw).Frame);

        Assert.True(report.IsValid);
        Assert.Equal(devices[1].DeviceId, report.DeviceId);
        Assert.Equal(0x0E, report.Afn);
        Assert.Equal(11, report.Sequence);
        Assert.True(report.ConfirmationRequested);
    }

    /// <summary>
    /// 验证 Gather 兼容模式应答老服务下行帧时原样保留 MSA，使 ConrLine 能关联当前指令。
    /// </summary>
    [Fact]
    public void GatherDownstreamConfirmation_PreservesMasterStationAddress()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options.Target);
        var raw = protocol.CreateLoginFrame(device, 5);
        raw[6] &= 0x7F;
        raw[11] = 74;
        raw[13] |= 0x10;
        raw[^2] = QGdw3761Codec.CalculateChecksum(raw.AsSpan(6, raw.Length - 8));
        var request = Assert.IsType<QGdw3761Frame>(protocol.Read(raw).Frame);

        var plan = Assert.IsType<DownstreamResponsePlan>(protocol.CreateDownstreamResponse(request, device, 1));
        var encoded = Assert.IsType<byte[]>(plan.Payload);
        var response = Assert.IsType<QGdw3761Frame>(protocol.Read(encoded).Frame);

        Assert.Equal(37, request.MasterStationAddress);
        Assert.Equal(request.MasterStationAddress, response.MasterStationAddress);
        Assert.True(response.IsUpstream);
        Assert.False(response.IsPrimaryMessage);
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

        var plan = Assert.IsType<DownstreamResponsePlan>(protocol.CreateDownstreamResponse(request, device, 1));
        var encoded = Assert.IsType<byte[]>(plan.Payload);
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

        var plan = Assert.IsType<DownstreamResponsePlan>(protocol.CreateDownstreamResponse(request, device, 3));
        var encoded = Assert.IsType<byte[]>(plan.Payload);
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

    /// <summary>
    /// 验证 Gather 兼容模式把 AFN=0CH/F129 下行读表命令转换为业务响应，而不是提前发送 AFN=00H 通用确认清空当前命令。
    /// </summary>
    [Fact]
    public void GatherReadingCommand_CreatesParsableBusinessResponse()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Workload.GatherReadingResponse.BaseReadingKwh = 1_234.5678m;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options);
        var request = CreateGatherReadingRequest(protocol, device, masterStationAddress: 37, sequence: 9, point: 10);

        var plan = Assert.IsType<DownstreamResponsePlan>(
            protocol.CreateDownstreamResponse(request, device, sequence: 1));
        var response = Assert.IsType<QGdw3761Frame>(protocol.Read(Assert.IsType<byte[]>(plan.Payload)).Frame);

        Assert.Equal(DownstreamResponseKind.GatherReading, plan.Kind);
        Assert.Equal(1, plan.SendCount);
        Assert.Equal(0x88, response.Control);
        Assert.Equal(0x0C, response.Afn);
        Assert.Equal([129], response.Functions);
        Assert.Equal([10], response.Points);
        Assert.Equal(request.MasterStationAddress, response.MasterStationAddress);
        Assert.Equal(request.Sequence, response.Sequence);
        Assert.Equal(31, response.DataUnitPayload.Length);
        Assert.Equal(4, response.DataUnitPayload[5]);
        // A14 低位 BCD 在前：1234.5678 编码为 78 56 34 12 00。
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12, 0x00 }, response.DataUnitPayload[6..11]);
    }

    /// <summary>
    /// 验证 Gather 读表故障模型可稳定生成丢弃、延迟、业务失败和重复响应计划。
    /// </summary>
    /// <param name="mode">待验证应答模式。</param>
    /// <param name="expectedSendCount">预期发送次数。</param>
    /// <param name="expectsPayload">是否应生成业务帧。</param>
    [Theory]
    [InlineData(GatherReadingResponseMode.Drop, 0, false)]
    [InlineData(GatherReadingResponseMode.Delayed, 1, true)]
    [InlineData(GatherReadingResponseMode.BusinessError, 1, true)]
    [InlineData(GatherReadingResponseMode.Duplicate, 2, true)]
    public void GatherReadingResponseMode_CreatesExpectedPlan(
        GatherReadingResponseMode mode,
        int expectedSendCount,
        bool expectsPayload)
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Workload.GatherReadingResponse.Mode = mode;
        options.Workload.GatherReadingResponse.DelayMilliseconds = 321;
        options.Safety.AllowFaultInjection = true;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options);
        var request = CreateGatherReadingRequest(protocol, device, masterStationAddress: 3, sequence: 2, point: 1);

        var plan = Assert.IsType<DownstreamResponsePlan>(
            protocol.CreateDownstreamResponse(request, device, sequence: 1));

        Assert.Equal(expectedSendCount, plan.SendCount);
        Assert.Equal(expectsPayload, plan.Payload is not null);
        Assert.Equal(mode == GatherReadingResponseMode.Delayed ? 321 : 0, plan.Delay.TotalMilliseconds);
        Assert.Equal(mode == GatherReadingResponseMode.BusinessError, plan.IsBusinessFailure);
    }

    /// <summary>
    /// 验证 Gather 下行分类会把标准单 F129、混合功能单元和其他 AFN 分开，供报告区分服务端下发与模拟器兼容问题。
    /// </summary>
    [Fact]
    public void GatherDownstreamClassifier_SeparatesRecognizedAndUnsupportedFrames()
    {
        var options = TestOptionsFactory.Create();
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        var device = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options);
        var classifier = Assert.IsAssignableFrom<IDownstreamFrameClassifier>(protocol);
        var recognizedFrame = CreateGatherReadingRequest(protocol, device, 3, 2, 1);

        var recognized = classifier.ClassifyDownstream(recognizedFrame, device);

        Assert.Equal(DownstreamClassificationKind.GatherReadingF129, recognized.Kind);
        Assert.True(recognized.IsRecognizedCommand);
        Assert.False(recognized.IsUnsupportedCommand);

        var multiRaw = recognizedFrame.Raw.ToArray();
        multiRaw[16] = 0x03;
        multiRaw[^2] = QGdw3761Codec.CalculateChecksum(multiRaw.AsSpan(6, multiRaw.Length - 8));
        var multiFrame = Assert.IsType<QGdw3761Frame>(protocol.Read(multiRaw).Frame);
        var multi = classifier.ClassifyDownstream(multiFrame, device);

        Assert.Equal(DownstreamClassificationKind.GatherReadingMultiFunction, multi.Kind);
        Assert.False(multi.IsRecognizedCommand);
        Assert.True(multi.IsUnsupportedCommand);
        Assert.Equal([129, 130], multi.Functions);

        var otherAfnRaw = recognizedFrame.Raw.ToArray();
        otherAfnRaw[12] = 0x04;
        otherAfnRaw[^2] = QGdw3761Codec.CalculateChecksum(otherAfnRaw.AsSpan(6, otherAfnRaw.Length - 8));
        var otherAfnFrame = Assert.IsType<QGdw3761Frame>(protocol.Read(otherAfnRaw).Frame);
        var otherAfn = classifier.ClassifyDownstream(otherAfnFrame, device);

        Assert.Equal(DownstreamClassificationKind.OtherQGdw3761Command, otherAfn.Kind);
        Assert.True(otherAfn.IsUnsupportedCommand);
    }

    /// <summary>
    /// 根据指定地址、MSA、序号和测量点创建无数据区的 Gather AFN=0CH/F129 下行命令。
    /// </summary>
    /// <param name="protocol">用于创建基础地址帧和解析结果的协议适配器。</param>
    /// <param name="device">命令目标虚拟设备。</param>
    /// <param name="masterStationAddress">老服务用于关联命令的 MSA。</param>
    /// <param name="sequence">下行命令 PSEQ。</param>
    /// <param name="point">测量点 PN。</param>
    /// <returns>合法的读表下行帧。</returns>
    private static QGdw3761Frame CreateGatherReadingRequest(
        ILoadTestProtocol protocol,
        VirtualDeviceDescriptor device,
        byte masterStationAddress,
        byte sequence,
        int point)
    {
        var raw = protocol.CreateLoginFrame(device, sequence);
        raw[6] = 0x4B;
        raw[11] = (byte)(masterStationAddress << 1);
        raw[12] = 0x0C;
        raw[13] = (byte)(0x60 | (sequence & 0x0F));
        var group = (point - 1) / 8 + 1;
        raw[14] = (byte)(1 << ((point - 1) % 8));
        raw[15] = (byte)group;
        raw[16] = 0x01;
        raw[17] = 0x10;
        raw[^2] = QGdw3761Codec.CalculateChecksum(raw.AsSpan(6, raw.Length - 8));
        return Assert.IsType<QGdw3761Frame>(protocol.Read(raw).Frame);
    }
}
