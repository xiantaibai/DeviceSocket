using DeviceSocket.Cluster.Contracts.Devices;

namespace DeviceSocket.CommandCenter.Host.Commands;

/// <summary>
/// CommandCenter 对一次下发请求返回的统一结果。
/// </summary>
public sealed record CommandDispatchResult(
    bool Success,
    string MsgId,
    string DeviceId,
    CommandDeliveryStatus Status,
    string Message,
    string? GatewayNodeId = null,
    int BytesWritten = 0);
