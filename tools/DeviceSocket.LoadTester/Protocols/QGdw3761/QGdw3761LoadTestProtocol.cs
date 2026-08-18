using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>
/// 实现面向当前 DeviceSocket 服务端的标准 Q/GDW 376.1 登录、心跳、退出、下行确认和 AFN=00H/F3 请求关联规则。
/// </summary>
internal sealed class QGdw3761LoadTestProtocol : ILoadTestProtocol
{
    public LoadProtocol Protocol => LoadProtocol.QGdw3761;
    public bool RequiresLoginConfirmation => true;
    public bool RequiresHeartbeatConfirmation => true;

    public byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence) =>
        QGdw3761Codec.CreateLinkRequest(
            device.AdministrativeDivision,
            device.TerminalAddress,
            QGdw3761LinkMessageKind.Login,
            sequence);

    public byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        QGdw3761Codec.CreateLinkRequest(
            device.AdministrativeDivision,
            device.TerminalAddress,
            QGdw3761LinkMessageKind.Heartbeat,
            sequence,
            now);

    public byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) => null;

    public byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence) =>
        QGdw3761Codec.CreateLinkRequest(
            device.AdministrativeDivision,
            device.TerminalAddress,
            QGdw3761LinkMessageKind.Logout,
            sequence);

    public DownstreamResponsePlan? CreateDownstreamResponse(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        byte sequence) =>
        frame is QGdw3761Frame request &&
        request.IsValid &&
        !request.IsUpstream &&
        request.ConfirmationRequested &&
        string.Equals(request.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
            ? DownstreamResponsePlan.Generic(QGdw3761TerminalResponseEncoder.CreateConfirmation(request))
            : null;

    public bool IsConfirmation(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        ProtocolOperation operation,
        byte sequence)
    {
        if (frame is not QGdw3761Frame response ||
            !response.IsValid ||
            response.Afn != 0x00 ||
            response.Sequence != (sequence & 0x0F) ||
            !response.Functions.Contains(3) ||
            !string.Equals(response.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            response.DataUnitPayload.Length < 6)
        {
            return false;
        }

        // AFN=00H/F3 的数据区回显被确认请求的 AFN、DA、DT 和 ERR，ERR=0 才代表成功。
        var expectedDt1 = operation == ProtocolOperation.Login ? (byte)0x01 : (byte)0x04;
        return response.DataUnitPayload[0] == 0x02 &&
               response.DataUnitPayload[3] == expectedDt1 &&
               response.DataUnitPayload[4] == 0x00 &&
               response.DataUnitPayload[5] == 0x00;
    }

    public ProtocolReadResult Read(ReadOnlySpan<byte> input)
    {
        var result = QGdw3761Codec.Read(input);
        return new ProtocolReadResult(result.Status, result.Consumed, result.Package);
    }
}
