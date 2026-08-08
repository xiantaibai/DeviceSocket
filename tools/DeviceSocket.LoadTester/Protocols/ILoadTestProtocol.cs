using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols;

internal interface ILoadTestProtocol
{
    LoadProtocol Protocol { get; }

    /// <summary>为指定设备创建协议登录或首帧绑定报文。</summary>
    byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence);
    bool RequiresLoginConfirmation { get; }

    /// <summary>为在线设备创建用于续租的协议心跳报文。</summary>
    byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now);
    bool RequiresHeartbeatConfirmation { get; }

    /// <summary>创建可选周期上报；当前协议没有独立上报模型时返回 null。</summary>
    byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now);

    /// <summary>创建可选退出登录报文；协议不支持时返回 null。</summary>
    byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence);

    /// <summary>为需要设备确认的服务端下行帧创建应答；无需或无法通用应答时返回 null。</summary>
    byte[]? CreateDownstreamResponse(IProtocolFrame frame, VirtualDeviceDescriptor device, byte sequence);

    /// <summary>严格校验服务端帧是否确认指定设备、操作和帧序号。</summary>
    bool IsConfirmation(IProtocolFrame frame, VirtualDeviceDescriptor device, ProtocolOperation operation, byte sequence);

    /// <summary>从当前 TCP 缓冲区读取一帧，并返回消费字节数和半包状态。</summary>
    ProtocolReadResult Read(ReadOnlySpan<byte> input);
}
