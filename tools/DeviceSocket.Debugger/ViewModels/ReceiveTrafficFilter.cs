namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示接收中心顶部的分类筛选范围。
/// </summary>
public enum ReceiveTrafficFilter
{
    /// <summary>仅显示普通业务回复和主动业务上报。</summary>
    Business,

    /// <summary>仅显示心跳及其确认报文。</summary>
    Heartbeat,

    /// <summary>显示异常和连接、链路状态。</summary>
    Status,

    /// <summary>显示接收中心保留的全部记录。</summary>
    All
}
