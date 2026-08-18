namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 表示虚拟设备某个通信步骤超时，并携带可直接映射到独立指标的超时类别。
/// </summary>
/// <param name="kind">连接、登录、写入或心跳超时类别。</param>
/// <param name="message">包含设备身份和超时上下文的错误说明。</param>
internal sealed class DeviceTimeoutException(DeviceTimeoutKind kind, string message) : TimeoutException(message)
{
    /// <summary>
    /// 获取本次超时对应的设备通信步骤。
    /// </summary>
    public DeviceTimeoutKind Kind { get; } = kind;
}
