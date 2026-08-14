using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.QGdw3761.Commands;
using DeviceSocket.Rpc.Contracts.Gateway;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证 Gateway 的 376.1 PSEQ 分配、正式重试复用、严格应答匹配和迟到丢弃规则。
/// </summary>
public sealed class QGdw3761CommandCorrelationRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 首次可靠下发必须由 Gateway 写入 CON 和 PSEQ；同一 MsgId 的新 AttemptNo 必须复用 PSEQ，新命令才递增。
    /// </summary>
    [Fact]
    public void TryPrepare_RetryReusesSequence_AndNextCommandAdvances()
    {
        var registry = CreateRegistry();
        var first = CreateRequest("msg-1", attemptNo: 1);

        Assert.True(registry.TryPrepare(first, "session-1", 7, Now, out var prepared, out var error), error);
        var firstFrame = ReadFrame(prepared!.Payload);
        Assert.True(firstFrame.ConfirmationRequested);

        var retry = CreateRequest("msg-1", attemptNo: 2);
        Assert.True(registry.TryPrepare(retry, "session-1", 7, Now.AddSeconds(2), out var retried, out error), error);
        Assert.Equal(firstFrame.Sequence, ReadFrame(retried!.Payload).Sequence);

        var next = CreateRequest("msg-2", attemptNo: 1);
        Assert.True(registry.TryPrepare(next, "session-1", 7, Now.AddSeconds(3), out var nextPrepared, out error), error);
        Assert.Equal((firstFrame.Sequence + 1) & 0x0F, ReadFrame(nextPrepared!.Payload).Sequence);
    }

    /// <summary>
    /// 标准 AFN=00H/F3 确认必须回显 PSEQ 和原 AFN/DA/DT，匹配后携带原 MsgId、AttemptNo 和 SessionEpoch。
    /// </summary>
    [Fact]
    public void TryAcquireReply_ExactConfirmation_ReturnsOriginalCommandIdentity()
    {
        var registry = CreateRegistry();
        var request = CreateRequest("msg-confirm", attemptNo: 2);
        Assert.True(registry.TryPrepare(request, "session-1", 19, Now, out var prepared, out _));
        var confirmation = CreateConfirmation(prepared!.Payload);

        var status = registry.TryAcquireReply("session-1", confirmation, Now.AddSeconds(1), out var match);

        Assert.Equal(QGdw3761CommandReplyMatchStatus.Matched, status);
        Assert.NotNull(match);
        Assert.Equal("msg-confirm", match.MsgId);
        Assert.Equal(2, match.AttemptNo);
        Assert.Equal(19, match.SessionEpoch);
        Assert.True(match.Success);
    }

    /// <summary>
    /// 同一确认被并发或重复投递时，在首次发布结束前不能再次取得 Orleans 发布权。
    /// </summary>
    [Fact]
    public void TryAcquireReply_DuplicateWhilePublishing_IsIgnored()
    {
        var registry = CreateRegistry();
        Assert.True(registry.TryPrepare(CreateRequest("msg-duplicate"), "session-1", 1, Now, out var prepared, out _));
        var confirmation = CreateConfirmation(prepared!.Payload);

        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.Matched,
            registry.TryAcquireReply("session-1", confirmation, Now.AddSeconds(1), out _));
        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.DuplicateReply,
            registry.TryAcquireReply("session-1", confirmation, Now.AddSeconds(1), out _));
    }

    /// <summary>
    /// 超过单次 ResponseTimeout 的回复必须丢弃，但保留逻辑关联，使下一正式尝试继续复用 PSEQ。
    /// </summary>
    [Fact]
    public void TryAcquireReply_LateReplyIsDropped_ButRetryReusesSequence()
    {
        var registry = CreateRegistry();
        var first = CreateRequest("msg-late", responseTimeout: TimeSpan.FromSeconds(3));
        Assert.True(registry.TryPrepare(first, "session-1", 2, Now, out var prepared, out _));
        var sent = ReadFrame(prepared!.Payload);
        var confirmation = CreateConfirmation(prepared.Payload);

        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.LateReply,
            registry.TryAcquireReply("session-1", confirmation, Now.AddSeconds(4), out _));

        var retry = CreateRequest("msg-late", attemptNo: 2, responseTimeout: TimeSpan.FromSeconds(3));
        Assert.True(registry.TryPrepare(retry, "session-1", 2, Now.AddSeconds(5), out var retried, out _));
        Assert.Equal(sent.Sequence, ReadFrame(retried!.Payload).Sequence);
        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.Matched,
            registry.TryAcquireReply("session-1", CreateConfirmation(retried.Payload), Now.AddSeconds(6), out var match));
        Assert.Equal(2, match!.AttemptNo);
    }

    /// <summary>
    /// PSEQ 或被确认的数据单元标识不一致时不能仅凭设备编号误确认当前命令。
    /// </summary>
    [Fact]
    public void TryAcquireReply_MismatchedSequence_DoesNotConfirmCommand()
    {
        var registry = CreateRegistry();
        Assert.True(registry.TryPrepare(CreateRequest("msg-mismatch"), "session-1", 3, Now, out var prepared, out _));
        var confirmationBytes = QGdw3761Codec.CreateTerminalConfirmation(ReadFrame(prepared!.Payload));
        confirmationBytes[13] = (byte)((confirmationBytes[13] & 0xF0) | ((confirmationBytes[13] + 1) & 0x0F));
        RecalculateChecksum(confirmationBytes);

        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.CorrelationMismatch,
            registry.TryAcquireReply("session-1", ReadFrame(confirmationBytes), Now.AddSeconds(1), out _));
    }

    /// <summary>
    /// Session 断开或命令整体到期后必须删除关联，随后到达的旧确认不能调用 Orleans。
    /// </summary>
    [Fact]
    public void RemoveSessionAndRemoveExpired_DropOldAssociations()
    {
        var registry = CreateRegistry();
        Assert.True(registry.TryPrepare(CreateRequest("msg-disconnect"), "session-1", 1, Now, out var disconnected, out _));
        registry.RemoveSession("session-1");
        Assert.Equal(
            QGdw3761CommandReplyMatchStatus.NoPendingCommand,
            registry.TryAcquireReply("session-1", CreateConfirmation(disconnected!.Payload), Now.AddSeconds(1), out _));

        Assert.True(registry.TryPrepare(CreateRequest("msg-expired", expiresAt: Now.AddSeconds(5)), "session-2", 1, Now, out _, out _));
        Assert.Equal(1, registry.RemoveExpired(Now.AddSeconds(6)));
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// 创建使用默认容量和清理周期的被测关联表。
    /// </summary>
    /// <returns>新的独立关联表。</returns>
    private static QGdw3761CommandCorrelationRegistry CreateRegistry() =>
        new(Options.Create(new QGdw3761CommandCorrelationOptions()));

    /// <summary>
    /// 创建一条地址与 DeviceId 一致的 376.1 主站下行启动帧请求。
    /// </summary>
    /// <param name="msgId">逻辑命令编号。</param>
    /// <param name="attemptNo">正式发送次数。</param>
    /// <param name="responseTimeout">回复窗口。</param>
    /// <param name="expiresAt">整体到期时间。</param>
    /// <returns>Gateway RPC 请求。</returns>
    private static SendCommandRequest CreateRequest(
        string msgId,
        int attemptNo = 1,
        TimeSpan? responseTimeout = null,
        DateTimeOffset? expiresAt = null) => new()
        {
            MsgId = msgId,
            DeviceId = "3761:1234-00018",
            SessionId = "session-1",
            SessionEpoch = 1,
            Protocol = DeviceProtocol.QGdw3761.ToString(),
            Payload = CreateDownstreamCommand(),
            TraceId = $"trace-{msgId}",
            AttemptNo = attemptNo,
            RequireDeviceConfirmation = true,
            ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10),
            ExpiresAt = expiresAt ?? Now.AddMinutes(1)
        };

    /// <summary>
    /// 由现有链路测试帧改造出合法下行启动帧，并重新计算 CS；负载语义不影响关联层测试。
    /// </summary>
    /// <returns>完整且可由生产 Codec 解析的下行帧。</returns>
    private static byte[] CreateDownstreamCommand()
    {
        var bytes = QGdw3761Codec.CreateLinkRequest(
            "1234",
            18,
            QGdw3761LinkMessageKind.Login,
            sequence: 11);
        bytes[6] = 0x49;
        bytes[12] = 0x04;
        RecalculateChecksum(bytes);
        return bytes;
    }

    /// <summary>
    /// 从 Gateway 实际下发报文生成硬件终端应返回的 AFN=00H/F3 确认。
    /// </summary>
    /// <param name="downstream">Gateway 已写入 PSEQ 的下行报文。</param>
    /// <returns>解析后的终端上行确认。</returns>
    private static QGdw3761Frame CreateConfirmation(byte[] downstream) =>
        ReadFrame(QGdw3761Codec.CreateTerminalConfirmation(ReadFrame(downstream)));

    /// <summary>
    /// 使用生产 Codec 解析完整测试帧并断言合法。
    /// </summary>
    /// <param name="bytes">完整 376.1 报文字节。</param>
    /// <returns>合法帧模型。</returns>
    private static QGdw3761Frame ReadFrame(byte[] bytes)
    {
        var result = QGdw3761Codec.ReadCompleteFrame(bytes);
        Assert.Equal(FrameReadStatus.PackageReady, result.Status);
        var frame = Assert.IsType<QGdw3761Frame>(result.Package);
        Assert.True(frame.IsValid, frame.Error?.Message);
        return frame;
    }

    /// <summary>
    /// 修改测试报文字段后按 376.1 用户数据区规则重新计算 CS。
    /// </summary>
    /// <param name="bytes">包含固定头、用户数据区、CS 和 16H 的完整报文。</param>
    private static void RecalculateChecksum(byte[] bytes) =>
        bytes[^2] = QGdw3761Codec.CalculateChecksum(bytes.AsSpan(6, bytes.Length - 8));
}
