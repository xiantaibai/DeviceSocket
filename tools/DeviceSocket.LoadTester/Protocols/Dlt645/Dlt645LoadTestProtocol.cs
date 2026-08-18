using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.Dlt645;

/// <summary>
/// 实现 DL/T 645-2007 虚拟电表的首帧绑定、周期读数和服务端读请求应答；该协议没有独立登录或心跳确认。
/// </summary>
internal sealed class Dlt645LoadTestProtocol : ILoadTestProtocol
{
    public LoadProtocol Protocol => LoadProtocol.Dlt6452007;
    public bool RequiresLoginConfirmation => false;
    public bool RequiresHeartbeatConfirmation => false;

    public byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence) =>
        CreateReading(device, sequence);

    public byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        CreateReading(device, sequence);

    public byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        CreateReading(device, sequence);

    public byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence) => null;

    public DownstreamResponsePlan? CreateDownstreamResponse(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        byte sequence)
    {
        if (frame is not Dlt645Frame request ||
            !request.IsValid ||
            request.IsResponse ||
            !string.Equals(request.Address, device.MeterAddress, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 645 响应控制码置 DIR 位并保留请求功能码；数据标识回显后附加模拟读数。
        var identifier = request.Data.Take(Math.Min(4, request.Data.Length));
        var data = identifier.Concat(new byte[] { sequence, (byte)(sequence ^ 0x5A) }).ToArray();
        return DownstreamResponsePlan.Generic(
            Dlt645Codec.CreateFrame(device.MeterAddress, (byte)(0x80 | request.FunctionCode), data, preambleLength: 0));
    }

    public bool IsConfirmation(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        ProtocolOperation operation,
        byte sequence) => false;

    public ProtocolReadResult Read(ReadOnlySpan<byte> input)
    {
        var result = Dlt645Codec.Read(input);
        return new ProtocolReadResult(result.Status, result.Consumed, result.Package);
    }

    private static byte[] CreateReading(VirtualDeviceDescriptor device, byte sequence)
    {
        // 645 没有独立登录帧，首个合法地址帧即完成绑定；末尾读数随序号变化以模拟真实上报。
        Span<byte> data = [0x00, 0x00, 0x01, 0x00, sequence, (byte)(sequence ^ 0x5A)];
        return Dlt645Codec.CreateFrame(device.MeterAddress, 0x91, data, preambleLength: 4);
    }
}
