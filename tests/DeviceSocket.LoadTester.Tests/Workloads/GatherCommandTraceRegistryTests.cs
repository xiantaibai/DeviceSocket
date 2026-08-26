using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Tests.Workloads;

/// <summary>
/// 验证 HTTP 期望命令与设备原始下行、协议识别和应答的有界关联口径。
/// </summary>
public sealed class GatherCommandTraceRegistryTests
{
    /// <summary>
    /// 验证原始下行、已识别、不支持、已应答和重复帧分别计数，不会把未知格式当成服务端未下发。
    /// </summary>
    [Fact]
    public void Registry_SeparatesDeliveryRecognitionResponseAndDuplicates()
    {
        var options = new DownstreamDiagnosticsOptions
        {
            MaxSamplesPerFingerprint = 2,
            MaxTotalSamples = 2,
            RawHexMaxBytes = 2,
            CorrelationTimeoutSeconds = 60
        };
        var registry = new GatherCommandTraceRegistry(options);
        var device = CreateDevice();
        var mapping = new GatherDeviceMeterMapping(device, 7001);
        var now = DateTimeOffset.UtcNow;
        var recognizedRequest = registry.Register("phase-a", 1, mapping, false, now);
        var recognizedMatch = registry.RecordDownstream(
            device,
            CreateClassification("recognized", DownstreamClassificationKind.GatherReadingF129, true, false),
            now.AddMilliseconds(10));
        registry.RecordHttpCompleted(recognizedRequest, now.AddMilliseconds(20), 200, true, null, false);
        registry.RecordResponse(recognizedMatch.RequestId, now.AddMilliseconds(30));

        var unsupportedRequest = registry.Register("phase-a", 2, mapping, false, now.AddSeconds(1));
        registry.RecordDownstream(
            device,
            CreateClassification("unsupported", DownstreamClassificationKind.GatherReadingMultiFunction, false, true),
            now.AddSeconds(1).AddMilliseconds(10));
        registry.RecordHttpCompleted(unsupportedRequest, now.AddSeconds(1).AddMilliseconds(20), 200, true, null, false);
        registry.RecordDownstream(
            device,
            CreateClassification("unsupported", DownstreamClassificationKind.GatherReadingMultiFunction, false, true),
            now.AddSeconds(1).AddMilliseconds(30));

        var phase = registry.GetPhaseSnapshot("phase-a");
        var summary = registry.GetSummary();
        var traces = registry.GetTraces();

        Assert.Equal(2, phase.EstimatedEnqueued);
        Assert.Equal(2, phase.RawDelivered);
        Assert.Equal(1, phase.Recognized);
        Assert.Equal(1, phase.Unsupported);
        Assert.Equal(1, phase.Responded);
        Assert.Equal(1, phase.DuplicateFrames);
        Assert.Equal(1, summary.DuplicateDownstreamFrames);
        Assert.Equal(CommandTraceOutcome.Responded, traces[0].Outcome);
        Assert.Equal(CommandTraceOutcome.UnsupportedDownstream, traces[1].Outcome);
        Assert.Equal(2, registry.GetSamples().Count);
        Assert.All(registry.GetSamples(), item => Assert.Equal(4, item.RawHex?.Length));
    }

    /// <summary>
    /// 验证链路确认、非法帧等非命令分类只进入诊断计数，不会错误消费下一个 HTTP 期望命令。
    /// </summary>
    [Fact]
    public void Registry_NonCommandFrame_DoesNotConsumePendingRequest()
    {
        var registry = new GatherCommandTraceRegistry(new DownstreamDiagnosticsOptions
        {
            CaptureUnknownFrames = false,
            CorrelationTimeoutSeconds = 60
        });
        var device = CreateDevice();
        var requestId = registry.Register(
            "phase-a",
            1,
            new GatherDeviceMeterMapping(device, 7001),
            false,
            DateTimeOffset.UtcNow);

        var nonCommand = registry.RecordDownstream(
            device,
            CreateClassification("confirm", DownstreamClassificationKind.NonPrimaryDownstream, false, false),
            DateTimeOffset.UtcNow);
        var command = registry.RecordDownstream(
            device,
            CreateClassification("reading", DownstreamClassificationKind.GatherReadingF129, true, false),
            DateTimeOffset.UtcNow.AddMilliseconds(1));

        Assert.Null(nonCommand.RequestId);
        Assert.Equal(requestId, command.RequestId);
        Assert.Equal(1, registry.GetSummary().UnmatchedDownstreamFrames);
        Assert.Equal(1, registry.GetPhaseSnapshot("phase-a").RawDelivered);
    }

    /// <summary>创建设备编号固定的单个测试终端。</summary>
    /// <returns>可用于命令映射和下行关联的设备描述。</returns>
    private static VirtualDeviceDescriptor CreateDevice() =>
        new(0, LoadProtocol.QGdw3761, "2608100001", "2608", 1, string.Empty, 123);

    /// <summary>创建字段完整且指纹可控的下行分类。</summary>
    /// <param name="fingerprint">测试指纹。</param>
    /// <param name="kind">协议分类。</param>
    /// <param name="recognized">是否已识别。</param>
    /// <param name="unsupported">是否不支持。</param>
    /// <returns>用于关联器测试的分类结果。</returns>
    private static DownstreamFrameClassification CreateClassification(
        string fingerprint,
        DownstreamClassificationKind kind,
        bool recognized,
        bool unsupported) =>
        new()
        {
            Kind = kind,
            IsRecognizedCommand = recognized,
            IsUnsupportedCommand = unsupported,
            AddressMatched = true,
            Control = 0x4B,
            Afn = 0x0C,
            Points = [1],
            Functions = recognized ? [129] : [129, 130],
            Sequence = 1,
            RawLength = 3,
            Fingerprint = fingerprint,
            Raw = [0x68, 0x01, 0x16],
            Reason = recognized ? "已识别" : "暂不支持"
        };
}
