using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 定义 LoadTester 面向不同终端协议的无状态编解码和请求确认关联能力，使设备生命周期与具体帧格式解耦。
/// </summary>
internal interface ILoadTestProtocol
{
    /// <summary>
    /// 获取当前适配器对应的配置协议类型。
    /// </summary>
    LoadProtocol Protocol { get; }

    /// <summary>
    /// 为指定设备创建协议登录或首帧绑定报文。
    /// </summary>
    /// <param name="device">携带协议地址的虚拟设备。</param>
    /// <param name="sequence">用于请求确认关联的设备会话序号。</param>
    /// <returns>可以直接写入 TCP 流的完整协议帧。</returns>
    byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence);

    /// <summary>
    /// 获取登录帧发送后是否必须收到协议确认才能进入在线状态。
    /// </summary>
    bool RequiresLoginConfirmation { get; }

    /// <summary>
    /// 为在线设备创建用于续租的协议心跳报文。
    /// </summary>
    /// <param name="device">携带协议地址的虚拟设备。</param>
    /// <param name="sequence">用于请求确认关联的设备会话序号。</param>
    /// <param name="now">写入协议时间字段的当前时刻。</param>
    /// <returns>可以直接写入 TCP 流的完整心跳帧。</returns>
    byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now);

    /// <summary>
    /// 获取心跳发送后是否必须收到协议确认才能判定连接健康。
    /// </summary>
    bool RequiresHeartbeatConfirmation { get; }

    /// <summary>
    /// 创建可选周期业务上报。
    /// </summary>
    /// <param name="device">携带协议地址的虚拟设备。</param>
    /// <param name="sequence">本次上报使用的设备会话序号。</param>
    /// <param name="now">写入协议时间字段的当前时刻。</param>
    /// <returns>完整上报帧；当前协议没有独立上报模型时返回 <see langword="null"/>。</returns>
    byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now);

    /// <summary>
    /// 创建设备主动退出登录的可选报文。
    /// </summary>
    /// <param name="device">携带协议地址的虚拟设备。</param>
    /// <param name="sequence">本次退出请求使用的设备会话序号。</param>
    /// <returns>完整退出帧；协议不支持主动退出时返回 <see langword="null"/>。</returns>
    byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence);

    /// <summary>
    /// 为服务端下行帧创建包含响应类别、延迟和发送次数的终端执行计划。
    /// </summary>
    /// <param name="frame">已经完成校验和拆包的服务端下行帧。</param>
    /// <param name="device">收到下行帧的虚拟设备。</param>
    /// <param name="sequence">可供新应答帧使用的设备会话序号。</param>
    /// <returns>终端应答计划；帧不属于当前设备或协议无需应答时返回 <see langword="null"/>。</returns>
    DownstreamResponsePlan? CreateDownstreamResponse(IProtocolFrame frame, VirtualDeviceDescriptor device, byte sequence);

    /// <summary>
    /// 严格校验服务端帧是否确认指定设备、操作和帧序号，防止并发下行帧误释放登录或心跳等待。
    /// </summary>
    /// <param name="frame">待匹配的有效服务端帧。</param>
    /// <param name="device">期望被确认的虚拟设备。</param>
    /// <param name="operation">期望被确认的登录或心跳操作。</param>
    /// <param name="sequence">原始请求使用的设备会话序号。</param>
    /// <returns>完全匹配设备、操作和序号时返回 true；否则返回 false。</returns>
    bool IsConfirmation(IProtocolFrame frame, VirtualDeviceDescriptor device, ProtocolOperation operation, byte sequence);

    /// <summary>
    /// 从当前 TCP 缓冲区读取一帧，并返回消费字节数、有效帧和半包状态。
    /// </summary>
    /// <param name="input">当前尚未消费的连续 TCP 字节。</param>
    /// <returns>协议拆包结果；非法前缀也必须消费至少一个字节，避免接收循环停滞。</returns>
    ProtocolReadResult Read(ReadOnlySpan<byte> input);
}
