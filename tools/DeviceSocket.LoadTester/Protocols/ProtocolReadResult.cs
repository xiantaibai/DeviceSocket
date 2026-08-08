using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.LoadTester.Protocols;

internal readonly record struct ProtocolReadResult(
    FrameReadStatus Status,
    int Consumed,
    IProtocolFrame? Frame);
