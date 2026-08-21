namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 表示调试界面使用的通信记录分类，用于把高频心跳与业务回复、异常和连接状态分开展示。
/// </summary>
public enum DebugTrafficCategory
{
    /// <summary>普通业务指令、业务回复或设备主动上报。</summary>
    Business,

    /// <summary>设备心跳及与心跳请求匹配的确认报文。</summary>
    Heartbeat,

    /// <summary>协议校验、网络收发或本地操作产生的异常。</summary>
    Error,

    /// <summary>连接、断开、登录和退出等系统或链路状态。</summary>
    System
}
