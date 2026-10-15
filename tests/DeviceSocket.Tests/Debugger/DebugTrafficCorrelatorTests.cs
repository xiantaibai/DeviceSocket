using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.Networking;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证调试通信记录按照设备和 376.1 序号关联请求回复，并正确隔离心跳确认。
/// </summary>
public sealed class DebugTrafficCorrelatorTests
{
    /// <summary>
    /// 验证 AFN=00H/F3 确认帧与先前心跳使用相同设备和序号时继承心跳分类及响应耗时。
    /// </summary>
    [Fact]
    public void HeartbeatConfirmation_WithMatchingDeviceAndSequence_IsClassifiedAsHeartbeat()
    {
        var correlator = new DebugTrafficCorrelator();
        var sentAt = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(8));
        var sent = new DebugTrafficEntry(
            sentAt,
            TrafficDirection.Send,
            "376.1 心跳",
            "68 ... 16",
            "上行心跳",
            DebugTrafficCategory.Heartbeat,
            CreateMetadata(ProtocolTrafficKind.Heartbeat, "AFN=02H / F3", 2, 3, 3));
        var received = new DebugTrafficEntry(
            sentAt.AddMilliseconds(37),
            TrafficDirection.Receive,
            "376.1 心跳确认",
            "68 ... 16",
            "AFN=00H/F3 确认",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=00H / F3", 0, 3, 3));

        correlator.Observe(sent);
        var result = correlator.Observe(received);

        Assert.True(result.IsMatched);
        Assert.Equal(DebugTrafficCategory.Heartbeat, result.Category);
        Assert.Equal(TimeSpan.FromMilliseconds(37), result.ResponseTime);
        Assert.Contains("37 ms", result.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证 Gather 老服务把心跳回复为 AFN=00H/F1 且固定使用 RSEQ=0 时，仍按同设备最近心跳归类。
    /// </summary>
    [Fact]
    public void GatherLegacyF1Confirmation_WithDifferentSequence_IsClassifiedAsHeartbeat()
    {
        var correlator = new DebugTrafficCorrelator();
        var sentAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.FromHours(8));
        var sent = new DebugTrafficEntry(
            sentAt,
            TrafficDirection.Send,
            "376.1 心跳",
            "68 ... 16",
            "AFN=02H/F3 上行心跳，PSEQ=2",
            DebugTrafficCategory.Heartbeat,
            CreateMetadata(ProtocolTrafficKind.Heartbeat, "AFN=02H / F3", 2, 3, 2));
        var received = new DebugTrafficEntry(
            sentAt.AddMilliseconds(21),
            TrafficDirection.Receive,
            "376.1 通用确认",
            "68 32 00 32 00 68 0B 33 15 23 00 14 00 60 00 00 01 00 EB 16",
            "Gather 老服务 AFN=00H/F1 确认，RSEQ=0",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=00H / F1", 0, 1, 0));

        correlator.Observe(sent);
        var result = correlator.Observe(received);

        Assert.True(result.IsMatched);
        Assert.Equal(DebugTrafficCategory.Heartbeat, result.Category);
        Assert.Equal(TimeSpan.FromMilliseconds(21), result.ResponseTime);
    }

    /// <summary>
    /// 验证相同 AFN=00H/F1 回复关联到登录请求时仍保持系统分类，避免把登录确认误标为心跳。
    /// </summary>
    [Fact]
    public void GatherLegacyF1Confirmation_AfterLogin_IsClassifiedAsSystem()
    {
        var correlator = new DebugTrafficCorrelator();
        var sentAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.FromHours(8));
        var sent = new DebugTrafficEntry(
            sentAt,
            TrafficDirection.Send,
            "376.1 登录",
            "68 ... 16",
            "AFN=02H/F1 登录，PSEQ=1",
            DebugTrafficCategory.System,
            CreateMetadata(ProtocolTrafficKind.LinkManagement, "AFN=02H / F1", 2, 1, 1));
        var received = new DebugTrafficEntry(
            sentAt.AddMilliseconds(18),
            TrafficDirection.Receive,
            "376.1 通用确认",
            "68 ... 16",
            "Gather 老服务 AFN=00H/F1 确认，RSEQ=0",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=00H / F1", 0, 1, 0));

        correlator.Observe(sent);
        var result = correlator.Observe(received);

        Assert.True(result.IsMatched);
        Assert.Equal(DebugTrafficCategory.System, result.Category);
    }

    /// <summary>
    /// 验证 GatherV4 的 AFN=00H/F1 必须匹配设备和 PSEQ，错误序号不能沿用老服务的最近请求兜底规则。
    /// </summary>
    [Fact]
    public void GatherV4F1Confirmation_RequiresExactSequence()
    {
        var correlator = new DebugTrafficCorrelator(
            QGdw3761ServerCompatibilityProfile.GatherV4);
        var sentAt = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(8));
        var sent = new DebugTrafficEntry(
            sentAt,
            TrafficDirection.Send,
            "GatherV4 登录",
            "68 ... 16",
            "AFN=02H/F1，CON=1，PSEQ=7",
            DebugTrafficCategory.System,
            CreateMetadata(ProtocolTrafficKind.LinkManagement, "AFN=02H / F1", 2, 1, 7));
        var wrongSequence = new DebugTrafficEntry(
            sentAt.AddMilliseconds(10),
            TrafficDirection.Receive,
            "GatherV4 登录确认",
            "68 ... 16",
            "AFN=00H/F1，RSEQ=0",
            DebugTrafficCategory.System,
            CreateMetadata(ProtocolTrafficKind.LinkConfirmation, "AFN=00H / F1", 0, 1, 0));
        var matchingSequence = wrongSequence with
        {
            Timestamp = sentAt.AddMilliseconds(20),
            Details = "AFN=00H/F1，RSEQ=7",
            Metadata = CreateMetadata(
                ProtocolTrafficKind.LinkConfirmation,
                "AFN=00H / F1",
                0,
                1,
                7)
        };

        correlator.Observe(sent);
        var unmatched = correlator.Observe(wrongSequence);
        var matched = correlator.Observe(matchingSequence);

        Assert.False(unmatched.IsMatched);
        Assert.True(matched.IsMatched);
        Assert.Equal(DebugTrafficCategory.System, matched.Category);
        Assert.Equal(TimeSpan.FromMilliseconds(20), matched.ResponseTime);
    }

    /// <summary>
    /// 验证没有对应发送请求的业务帧保留业务分类并标记为主动上报或未匹配。
    /// </summary>
    [Fact]
    public void BusinessFrame_WithoutPendingRequest_IsMarkedAsUnmatched()
    {
        var correlator = new DebugTrafficCorrelator();
        var received = new DebugTrafficEntry(
            DateTimeOffset.Now,
            TrafficDirection.Receive,
            "主动业务上报",
            "68 ... 16",
            "业务数据",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=0CH / F129", 12, 129, 5));

        var result = correlator.Observe(received);

        Assert.False(result.IsMatched);
        Assert.Equal(DebugTrafficCategory.Business, result.Category);
        Assert.Contains("未匹配", result.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证自动终端应答不会登记为新的待回复请求，避免后续同序号下行帧被错误关联。
    /// </summary>
    [Fact]
    public void AutomaticTerminalReply_DoesNotEnterPendingRequestQueue()
    {
        var correlator = new DebugTrafficCorrelator(
            QGdw3761ServerCompatibilityProfile.GatherV4);
        var sentAt = DateTimeOffset.Now;
        var automaticReply = new DebugTrafficEntry(
            sentAt,
            TrafficDirection.Send,
            "自动回复 · AFN=00H/F3",
            "68 ... 16",
            "终端确认",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=00H / F3", 0, 3, 6),
            TrackResponse: false);
        var laterCommand = new DebugTrafficEntry(
            sentAt.AddMilliseconds(25),
            TrafficDirection.Receive,
            "新的主站下行",
            "68 ... 16",
            "复用了同一个四位 PSEQ",
            DebugTrafficCategory.Business,
            CreateMetadata(ProtocolTrafficKind.Business, "AFN=0CH / F129", 12, 129, 6));

        correlator.Observe(automaticReply);
        var result = correlator.Observe(laterCommand);

        Assert.False(result.IsMatched);
        Assert.Contains("未匹配", result.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证没有本地请求可匹配的 376.1 主站启动帧显示为服务端下行，而不是误标成终端主动上报。
    /// </summary>
    [Fact]
    public void PrimaryDownstreamCommand_WithoutPendingRequest_IsMarkedAsServerDownstream()
    {
        var correlator = new DebugTrafficCorrelator(
            QGdw3761ServerCompatibilityProfile.GatherV4);
        var received = new DebugTrafficEntry(
            DateTimeOffset.Now,
            TrafficDirection.Receive,
            "376.1 AFN=0DH/F87",
            "68 ... 16",
            "GatherV4 主站下行",
            DebugTrafficCategory.Business,
            CreateMetadata(
                ProtocolTrafficKind.Business,
                "AFN=0DH / F87",
                0x0D,
                87,
                1,
                isUpstream: false,
                isPrimaryMessage: true));

        var result = correlator.Observe(received);

        Assert.False(result.IsMatched);
        Assert.Equal("服务端下行 / 待应答", result.StatusText);
    }

    /// <summary>
    /// 创建测试所需的 376.1 结构化通信元数据。
    /// </summary>
    /// <param name="trafficKind">协议业务类型。</param>
    /// <param name="instructionKey">AFN/FN 筛选键。</param>
    /// <param name="afn">AFN（应用层功能码）。</param>
    /// <param name="function">数据单元功能号。</param>
    /// <param name="sequence">PSEQ/RSEQ 低四位序号。</param>
    /// <param name="isUpstream">可选的 376.1 DIR 方向。</param>
    /// <param name="isPrimaryMessage">可选的 376.1 PRM 启动标志。</param>
    /// <returns>固定设备号的测试元数据。</returns>
    private static DebugTrafficMetadata CreateMetadata(
        ProtocolTrafficKind trafficKind,
        string instructionKey,
        byte afn,
        int function,
        byte sequence,
        bool? isUpstream = null,
        bool? isPrimaryMessage = null) =>
        new(
            DeviceProtocol.QGdw3761,
            trafficKind,
            "3761:2608-10001",
            instructionKey,
            afn,
            [function],
            sequence,
            isUpstream,
            isPrimaryMessage);
}
