using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.Networking;

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
    /// 创建测试所需的 376.1 结构化通信元数据。
    /// </summary>
    /// <param name="trafficKind">协议业务类型。</param>
    /// <param name="instructionKey">AFN/FN 筛选键。</param>
    /// <param name="afn">AFN（应用层功能码）。</param>
    /// <param name="function">数据单元功能号。</param>
    /// <param name="sequence">PSEQ/RSEQ 低四位序号。</param>
    /// <returns>固定设备号的测试元数据。</returns>
    private static DebugTrafficMetadata CreateMetadata(
        ProtocolTrafficKind trafficKind,
        string instructionKey,
        byte afn,
        int function,
        byte sequence) =>
        new(
            DeviceProtocol.QGdw3761,
            trafficKind,
            "3761:2608-10001",
            instructionKey,
            afn,
            [function],
            sequence);
}
