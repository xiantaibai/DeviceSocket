using System.Text;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示回复看板中的一次服务端下行指令处理结果，保留请求、回复和失败原因供排障复制。
/// </summary>
public sealed class AutoReplyDashboardItemViewModel
{
    /// <summary>
    /// 创建一条成功发送的自动回复记录。
    /// </summary>
    /// <param name="timestamp">回复发送完成时间。</param>
    /// <param name="plan">协议识别和回复编码计划。</param>
    /// <param name="requestHex">服务端下发的完整原始报文。</param>
    /// <param name="responseHex">调试终端发出的完整回复报文。</param>
    /// <param name="elapsed">从开始处理到写入 TCP 流的耗时。</param>
    /// <returns>状态为“已回复”的看板记录。</returns>
    public static AutoReplyDashboardItemViewModel Success(
        DateTimeOffset timestamp,
        QGdw3761AutoReplyPlan plan,
        string requestHex,
        string responseHex,
        TimeSpan elapsed) =>
        new(timestamp, plan, requestHex, responseHex, elapsed, null);

    /// <summary>
    /// 创建一条识别成功但回复发送失败的记录。
    /// </summary>
    /// <param name="timestamp">失败发生时间。</param>
    /// <param name="plan">协议识别和回复编码计划。</param>
    /// <param name="requestHex">服务端下发的完整原始报文。</param>
    /// <param name="responseHex">已经生成但未确认发送成功的回复报文。</param>
    /// <param name="elapsed">处理到失败的耗时。</param>
    /// <param name="error">发送或编码失败原因。</param>
    /// <returns>状态为“回复失败”的看板记录。</returns>
    public static AutoReplyDashboardItemViewModel Failure(
        DateTimeOffset timestamp,
        QGdw3761AutoReplyPlan plan,
        string requestHex,
        string responseHex,
        TimeSpan elapsed,
        string error) =>
        new(timestamp, plan, requestHex, responseHex, elapsed, error);

    /// <summary>
    /// 根据协议计划和实际发送结果初始化回复看板记录。
    /// </summary>
    /// <param name="timestamp">处理完成时间。</param>
    /// <param name="plan">自动回复计划。</param>
    /// <param name="requestHex">下行请求 HEX。</param>
    /// <param name="responseHex">上行回复 HEX。</param>
    /// <param name="elapsed">回复处理耗时。</param>
    /// <param name="error">失败原因；为空表示发送成功。</param>
    private AutoReplyDashboardItemViewModel(
        DateTimeOffset timestamp,
        QGdw3761AutoReplyPlan plan,
        string requestHex,
        string responseHex,
        TimeSpan elapsed,
        string? error)
    {
        Timestamp = timestamp;
        TimeText = timestamp.ToString("HH:mm:ss.fff");
        IsSuccess = string.IsNullOrWhiteSpace(error);
        StatusText = IsSuccess ? "已回复" : "回复失败";
        DeviceText = string.IsNullOrWhiteSpace(plan.DeviceId) ? "未知设备" : plan.DeviceId;
        ReplyKindText = plan.Kind switch
        {
            QGdw3761AutoReplyKind.GatherReadingData => "老 Gather 抄读数据",
            QGdw3761AutoReplyKind.GatherV4Confirmation => "GatherV4 任务完成",
            _ => "376.1 指令确认"
        };
        RequestInstruction = plan.RequestInstruction;
        ResponseInstruction = plan.ResponseInstruction;
        ElapsedText = $"{elapsed.TotalMilliseconds:0.0} ms";
        Summary = string.IsNullOrWhiteSpace(error)
            ? plan.Summary
            : $"{plan.Summary} 失败原因：{error}";
        RequestHex = requestHex;
        ResponseHex = responseHex;
        Error = error ?? string.Empty;
        SecondaryText = $"{DeviceText} · {RequestInstruction} → {ResponseInstruction} · {ElapsedText}";
        FullText = BuildFullText();
    }

    /// <summary>获取记录完成时间。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>获取列表使用的毫秒级时间文本。</summary>
    public string TimeText { get; }

    /// <summary>获取回复是否已经成功写入 TCP 流。</summary>
    public bool IsSuccess { get; }

    /// <summary>获取回复是否失败，用于绑定失败样式。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>获取“已回复”或“回复失败”状态文本。</summary>
    public string StatusText { get; }

    /// <summary>获取下行报文携带的设备编号。</summary>
    public string DeviceText { get; }

    /// <summary>获取业务数据回复或协议确认等回复类型。</summary>
    public string ReplyKindText { get; }

    /// <summary>获取被回复的服务端下行指令摘要。</summary>
    public string RequestInstruction { get; }

    /// <summary>获取调试终端实际发出的回复指令摘要。</summary>
    public string ResponseInstruction { get; }

    /// <summary>获取处理和发送耗时。</summary>
    public string ElapsedText { get; }

    /// <summary>获取列表第二行的设备、请求、回复和耗时摘要。</summary>
    public string SecondaryText { get; }

    /// <summary>获取自动回复业务说明或失败原因。</summary>
    public string Summary { get; }

    /// <summary>获取服务端下发的完整 HEX。</summary>
    public string RequestHex { get; }

    /// <summary>获取调试终端生成的完整回复 HEX。</summary>
    public string ResponseHex { get; }

    /// <summary>获取失败原因；成功时为空字符串。</summary>
    public string Error { get; }

    /// <summary>获取详情区域使用的完整可复制文本。</summary>
    public string FullText { get; }

    /// <summary>
    /// 构造包含请求、响应和状态的完整可复制文本。
    /// </summary>
    /// <returns>回复看板详情文本。</returns>
    private string BuildFullText()
    {
        var builder = new StringBuilder()
            .Append("时间：").AppendLine(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append("状态：").AppendLine(StatusText)
            .Append("设备：").AppendLine(DeviceText)
            .Append("类型：").AppendLine(ReplyKindText)
            .Append("收到指令：").AppendLine(RequestInstruction)
            .Append("回复指令：").AppendLine(ResponseInstruction)
            .Append("处理耗时：").AppendLine(ElapsedText)
            .Append("说明：").AppendLine(Summary)
            .AppendLine("下行请求 HEX：")
            .AppendLine(RequestHex)
            .AppendLine("上行回复 HEX：")
            .Append(ResponseHex);
        return builder.ToString();
    }
}
