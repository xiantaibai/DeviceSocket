using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 根据服务端兼容类型识别 376.1 主站下行指令，并生成与现有 LoadTester 一致的终端回复。
/// </summary>
public static class QGdw3761AutoReplyPlanner
{
    /// <summary>
    /// 尝试为一条完整下行帧创建自动回复计划。
    /// </summary>
    /// <param name="request">已经完成长度和校验和验证的 376.1 帧。</param>
    /// <param name="serverProfile">当前连接选择的服务端兼容类型。</param>
    /// <param name="options">老 Gather 抄读业务回复使用的模拟表计参数。</param>
    /// <param name="replyTime">写入抄读业务数据的终端时间。</param>
    /// <returns>需要回复时返回完整计划；非法帧、上行帧、服务端确认或无需确认的帧返回 null。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 或 <paramref name="options"/> 为空时抛出。</exception>
    /// <exception cref="ArgumentException">抄读模拟参数超出协议编码范围时抛出。</exception>
    public static QGdw3761AutoReplyPlan? TryCreate(
        QGdw3761Frame request,
        QGdw3761ServerCompatibilityProfile serverProfile,
        QGdw3761AutoReplyOptions options,
        DateTimeOffset replyTime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (!request.IsValid || request.IsUpstream || !request.IsPrimaryMessage)
            return null;

        // GatherV4 的链路确认也是 PRM=1 的下行帧；必须先排除 AFN=00H/F1，避免 ACK 相互确认形成死循环。
        if (IsServerAcknowledgement(request))
            return null;

        if (serverProfile == QGdw3761ServerCompatibilityProfile.GatherTcpCenter &&
            IsSinglePointReadingRequest(request))
        {
            var response = QGdw3761ReadingResponseEncoder.Create(
                request.AdministrativeDivision ?? string.Empty,
                request.TerminalAddress,
                request.MasterStationAddress,
                request.IsGroupAddress,
                request.Sequence,
                request.Points[0],
                options.RateCount,
                options.TotalReadingKwh,
                replyTime,
                businessError: false);
            return new QGdw3761AutoReplyPlan
            {
                Kind = QGdw3761AutoReplyKind.GatherReadingData,
                ResponseBytes = response,
                DeviceId = request.DeviceId ?? string.Empty,
                RequestInstruction = CreateRequestInstruction(request),
                ResponseInstruction = "AFN=0CH / F129（抄读业务数据）",
                Summary =
                    $"自动回复老 Gather 抄读指令；PN={request.Points[0]}，RSEQ={request.Sequence}，" +
                    $"总电能={options.TotalReadingKwh:0.0000} kWh，费率数={options.RateCount}。"
            };
        }

        if (serverProfile == QGdw3761ServerCompatibilityProfile.GatherV4)
        {
            // GatherV4 的业务结果链路实际解析 AFN=00H/F1；F3 只是逐数据单元的协议确认，
            // 无法产生“全部确认”的业务结果，会让 API 任务继续等待或被重新下发。
            var taskResult = QGdw3761Codec.CreateTerminalAllResult(request);
            return new QGdw3761AutoReplyPlan
            {
                Kind = QGdw3761AutoReplyKind.GatherV4Confirmation,
                ResponseBytes = taskResult,
                DeviceId = request.DeviceId ?? string.Empty,
                RequestInstruction = CreateRequestInstruction(request),
                ResponseInstruction = "AFN=00H / F1（任务全部确认）",
                Summary =
                    $"自动完成 GatherV4 下行任务；复用 A3={request.MasterStationAddress}、" +
                    $"RSEQ={request.Sequence}，服务端应据此完成业务结果并继续下一条任务。"
            };
        }

        var shouldConfirm = request.ConfirmationRequested;
        if (!shouldConfirm)
            return null;

        // 通用 376.1 模式仍严格按 CON=1 生成逐数据单元 F3 确认。
        var confirmation = QGdw3761Codec.CreateTerminalConfirmation(request);
        return new QGdw3761AutoReplyPlan
        {
            Kind = QGdw3761AutoReplyKind.StandardConfirmation,
            ResponseBytes = confirmation,
            DeviceId = request.DeviceId ?? string.Empty,
            RequestInstruction = CreateRequestInstruction(request),
            ResponseInstruction = "AFN=00H / F3（终端确认）",
            Summary = $"自动确认 CON=1 下行指令；回显 AFN={request.Afn:X2}H、RSEQ={request.Sequence}。"
        };
    }

    /// <summary>
    /// 判断报文是否为当前业务编码器支持的单测量点 AFN=0CH/F129 抄读指令。
    /// </summary>
    /// <param name="frame">合法的主站下行启动帧。</param>
    /// <returns>仅包含 F129 且仅包含一个测量点时返回 true。</returns>
    private static bool IsSinglePointReadingRequest(QGdw3761Frame frame) =>
        frame.Afn == 0x0C &&
        frame.Functions.Count == 1 &&
        frame.Functions[0] == 129 &&
        frame.Points.Count == 1;

    /// <summary>
    /// 判断下行帧是否为服务端对链路请求返回的 AFN=00H/F1 全部确认。
    /// </summary>
    /// <param name="frame">合法的 376.1 主站下行帧。</param>
    /// <returns>PN=0、F1 且没有附加数据时返回 true。</returns>
    private static bool IsServerAcknowledgement(QGdw3761Frame frame) =>
        frame.Afn == 0x00 &&
        frame.Points.Count == 1 &&
        frame.Points[0] == 0 &&
        frame.Functions.Count == 1 &&
        frame.Functions[0] == 1 &&
        frame.DataUnitPayload.Length == 0;

    /// <summary>
    /// 创建回复看板使用的 AFN、FN、PN 和 PSEQ 指令摘要。
    /// </summary>
    /// <param name="frame">已经识别的主站下行帧。</param>
    /// <returns>可直接展示的下行指令摘要。</returns>
    private static string CreateRequestInstruction(QGdw3761Frame frame)
    {
        var functions = frame.Functions.Count == 0
            ? "无 FN"
            : string.Join(",", frame.Functions.Select(value => $"F{value}"));
        var points = frame.Points.Count == 0
            ? "无 PN"
            : string.Join(",", frame.Points.Select(value => $"PN{value}"));
        return $"AFN={frame.Afn:X2}H / {functions} / {points} / PSEQ={frame.Sequence}";
    }
}
