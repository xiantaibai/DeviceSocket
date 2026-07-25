namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 区分调试记录来自发送、接收还是工具内部状态。
/// </summary>
public enum TrafficDirection
{
    /// <summary>工具自身的连接或配置状态。</summary>
    System,

    /// <summary>向远端发送的数据。</summary>
    Send,

    /// <summary>从远端收到的数据。</summary>
    Receive,

    /// <summary>不会终止程序的错误记录。</summary>
    Error
}
