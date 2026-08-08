using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.Dlt645;

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

    public byte[]? CreateDownstreamResponse(
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
        return Dlt645Codec.CreateFrame(device.MeterAddress, (byte)(0x80 | request.FunctionCode), data, preambleLength: 0);
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
