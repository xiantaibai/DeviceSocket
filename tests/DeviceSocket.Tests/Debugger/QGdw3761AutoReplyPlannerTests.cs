using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证调试工具对老 Gather 和 GatherV4 服务端下行指令的自动回复规则。
/// </summary>
public sealed class QGdw3761AutoReplyPlannerTests
{
    private const string GatherReadingRequestHex =
        "68 32 00 32 00 68 4B 10 32 AC 1C 3A 0C 62 10 01 01 10 1F 16";

    /// <summary>
    /// 验证老 Gather 单点 F129 抄读命令会返回包含指定总示值和费率数的业务帧。
    /// </summary>
    [Fact]
    public void GatherReadingRequest_CreatesBusinessDataResponse()
    {
        var request = ParseFrame(GatherReadingRequestHex);

        var plan = QGdw3761AutoReplyPlanner.TryCreate(
            request,
            QGdw3761ServerCompatibilityProfile.GatherTcpCenter,
            new QGdw3761AutoReplyOptions
            {
                RateCount = 2,
                TotalReadingKwh = 1234.5678m
            },
            new DateTimeOffset(2026, 10, 13, 8, 9, 10, TimeSpan.FromHours(8)));

        var actual = Assert.IsType<QGdw3761AutoReplyPlan>(plan);
        var response = Assert.IsType<QGdw3761Frame>(
            QGdw3761Codec.ReadCompleteFrame(actual.ResponseBytes).Package);
        Assert.Equal(QGdw3761AutoReplyKind.GatherReadingData, actual.Kind);
        Assert.Equal(request.DeviceId, actual.DeviceId);
        Assert.True(response.IsValid);
        Assert.True(response.IsUpstream);
        Assert.False(response.IsPrimaryMessage);
        Assert.Equal(0x0C, response.Afn);
        Assert.Equal([129], response.Functions);
        Assert.Equal(request.Points, response.Points);
        Assert.Equal(request.Sequence, response.Sequence);
        Assert.Contains("总电能=1234.5678", actual.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证 GatherV4 主站下行会返回保留 A3、复用 PSEQ 的 F1 任务全部确认。
    /// </summary>
    [Fact]
    public void GatherV4Command_CreatesCorrelatedTerminalConfirmation()
    {
        var request = ParseFrame(GatherReadingRequestHex);

        var plan = QGdw3761AutoReplyPlanner.TryCreate(
            request,
            QGdw3761ServerCompatibilityProfile.GatherV4,
            new QGdw3761AutoReplyOptions(),
            DateTimeOffset.Now);

        var actual = Assert.IsType<QGdw3761AutoReplyPlan>(plan);
        var response = Assert.IsType<QGdw3761Frame>(
            QGdw3761Codec.ReadCompleteFrame(actual.ResponseBytes).Package);
        Assert.Equal(QGdw3761AutoReplyKind.GatherV4Confirmation, actual.Kind);
        Assert.Equal(request.MasterStationAddress, response.MasterStationAddress);
        Assert.Equal(request.Sequence, response.Sequence);
        Assert.Equal(0x00, response.Afn);
        Assert.Equal([1], response.Functions);
        Assert.Empty(response.DataUnitPayload);
        Assert.Contains("任务全部确认", actual.ResponseInstruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证通用模式只响应 CON=1 命令，避免对不要求确认的下行报文擅自回包。
    /// </summary>
    [Fact]
    public void StandardCommand_RequiresConfirmationFlag()
    {
        var request = ParseFrame(GatherReadingRequestHex);
        var withoutConfirmation = QGdw3761AutoReplyPlanner.TryCreate(
            request,
            QGdw3761ServerCompatibilityProfile.Standard,
            new QGdw3761AutoReplyOptions(),
            DateTimeOffset.Now);
        var prepared = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(
            QGdw3761Codec.PrepareReliableDownstreamCommand(request, sequence: 9)).Package);

        var withConfirmation = QGdw3761AutoReplyPlanner.TryCreate(
            prepared,
            QGdw3761ServerCompatibilityProfile.Standard,
            new QGdw3761AutoReplyOptions(),
            DateTimeOffset.Now);

        Assert.Null(withoutConfirmation);
        Assert.Equal(
            QGdw3761AutoReplyKind.StandardConfirmation,
            Assert.IsType<QGdw3761AutoReplyPlan>(withConfirmation).Kind);
    }

    /// <summary>
    /// 验证服务端返回的从动确认帧不会再次触发终端自动回复，防止形成确认循环。
    /// </summary>
    [Fact]
    public void ServerConfirmation_DoesNotCreateAnotherReply()
    {
        var terminalRequest = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(
            QGdw3761Codec.CreateLinkRequest(
                "2315",
                20,
                QGdw3761LinkMessageKind.Heartbeat,
                sequence: 6,
                confirmationRequested: true)).Package);
        var serverConfirmation = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(
            QGdw3761Codec.CreateLinkConfirmation(terminalRequest)).Package);

        var plan = QGdw3761AutoReplyPlanner.TryCreate(
            serverConfirmation,
            QGdw3761ServerCompatibilityProfile.GatherV4,
            new QGdw3761AutoReplyOptions(),
            DateTimeOffset.Now);

        Assert.Null(plan);
    }

    /// <summary>
    /// 验证现场持续重发的 AFN=10H/F1、AFN=0DH/F82-F87 均能生成 GatherV4 F1 任务全部确认。
    /// </summary>
    /// <param name="requestHex">服务端实际下发的完整报文。</param>
    /// <param name="expectedAfn">需要完成的原始下行 AFN。</param>
    /// <param name="expectedFunction">需要在回复看板展示的原始功能号。</param>
    /// <param name="expectedPoint">需要在回复看板展示的原始测量点。</param>
    /// <param name="expectedResponseHex">需要实际写入 TCP 连接的完整 F1 任务结果。</param>
    [Theory]
    [InlineData(
        "68 CA 00 CA 00 68 4B 32 17 90 42 52 10 61 00 00 01 00 02 6B E4 64 10 00 68 40 70 21 00 00 00 68 11 04 35 33 B3 35 06 16 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 11 16",
        0x10,
        1,
        0,
        "68 32 00 32 00 68 80 32 17 90 42 52 00 61 00 00 01 00 4F 16")]
    [InlineData(
        "68 4E 00 4E 00 68 4B 32 17 90 42 58 0D 61 01 01 02 0A 00 10 13 10 26 01 04 98 16",
        0x0D,
        82,
        1,
        "68 32 00 32 00 68 80 32 17 90 42 58 00 61 00 00 01 00 55 16")]
    [InlineData(
        "68 4E 00 4E 00 68 4B 32 17 90 42 5A 0D 61 01 01 04 0A 00 10 13 10 26 01 04 9C 16",
        0x0D,
        83,
        1,
        "68 32 00 32 00 68 80 32 17 90 42 5A 00 61 00 00 01 00 57 16")]
    [InlineData(
        "68 4E 00 4E 00 68 4B 32 17 90 42 A2 0D 61 01 01 40 0A 00 11 13 10 26 01 01 1E 16",
        0x0D,
        87,
        1,
        "68 32 00 32 00 68 80 32 17 90 42 A2 00 61 00 00 01 00 9F 16")]
    public void GatherV4ObservedCommands_CreateTerminalConfirmation(
        string requestHex,
        byte expectedAfn,
        int expectedFunction,
        int expectedPoint,
        string expectedResponseHex)
    {
        var request = ParseFrame(requestHex);

        var plan = QGdw3761AutoReplyPlanner.TryCreate(
            request,
            QGdw3761ServerCompatibilityProfile.GatherV4,
            new QGdw3761AutoReplyOptions(),
            DateTimeOffset.Now);

        var actual = Assert.IsType<QGdw3761AutoReplyPlan>(plan);
        var response = Assert.IsType<QGdw3761Frame>(
            QGdw3761Codec.ReadCompleteFrame(actual.ResponseBytes).Package);
        Assert.False(request.ConfirmationRequested);
        Assert.Equal(expectedAfn, request.Afn);
        Assert.Equal([expectedFunction], request.Functions);
        Assert.Equal([expectedPoint], request.Points);
        Assert.Equal(QGdw3761AutoReplyKind.GatherV4Confirmation, actual.Kind);
        Assert.Equal(request.MasterStationAddress, response.MasterStationAddress);
        Assert.Equal(request.Sequence, response.Sequence);
        Assert.Equal(0x00, response.Afn);
        Assert.Equal([1], response.Functions);
        Assert.Empty(response.DataUnitPayload);
        Assert.Equal(expectedResponseHex, HexCodec.ToHex(actual.ResponseBytes, true));
        Assert.Contains($"F{expectedFunction}", actual.RequestInstruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把测试样本 HEX 转换为已经通过协议校验的完整帧。
    /// </summary>
    /// <param name="hex">完整 376.1 报文。</param>
    /// <returns>解析成功的 376.1 帧。</returns>
    private static QGdw3761Frame ParseFrame(string hex)
    {
        Assert.True(HexCodec.TryParse(hex, out var bytes, out var error), error);
        var frame = Assert.IsType<QGdw3761Frame>(QGdw3761Codec.ReadCompleteFrame(bytes).Package);
        Assert.True(frame.IsValid);
        return frame;
    }
}
