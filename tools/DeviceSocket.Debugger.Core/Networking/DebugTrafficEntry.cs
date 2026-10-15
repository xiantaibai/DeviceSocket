namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 表示调试工具中的一条连接、发送、接收或错误记录。
/// </summary>
/// <param name="Timestamp">记录产生时间。</param>
/// <param name="Direction">发送、接收、异常或系统状态方向。</param>
/// <param name="Title">适合列表摘要展示的标题。</param>
/// <param name="RawHex">完整原始报文 HEX；无原始报文时为空字符串。</param>
/// <param name="Details">协议解析详情或状态说明。</param>
/// <param name="Category">界面分类；未显式指定时按系统状态处理。</param>
/// <param name="Metadata">从合法协议帧提取的结构化字段；状态或解析失败时可以为空。</param>
/// <param name="TrackResponse">发送记录是否应进入请求回复关联队列；自动终端应答应设为 false。</param>
public sealed record DebugTrafficEntry(
    DateTimeOffset Timestamp,
    TrafficDirection Direction,
    string Title,
    string RawHex,
    string Details,
    DebugTrafficCategory Category = DebugTrafficCategory.System,
    DebugTrafficMetadata? Metadata = null,
    bool TrackResponse = true);
