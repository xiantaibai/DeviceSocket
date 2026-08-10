using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Protocols.Dlt645;

/// <summary>
/// DL/T 645-2007 完整帧的解析结果，数据域已执行减 33H 还原。
/// </summary>
public sealed record Dlt645Frame : IProtocolFrame
{
    /// <inheritdoc />
    public DeviceProtocol Protocol => DeviceProtocol.Dlt6452007;
    /// <inheritdoc />
    public required bool IsValid { get; init; }
    /// <inheritdoc />
    public ProtocolError? Error { get; init; }
    /// <inheritdoc />
    public required byte[] Raw { get; init; }
    /// <inheritdoc />
    public string? DeviceId { get; init; }
    public int PreambleLength { get; init; }
    public string? Address { get; init; }
    public byte Control { get; init; }
    public bool IsResponse { get; init; }
    public bool IsAbnormalResponse { get; init; }
    public bool HasMoreFrames { get; init; }
    public byte FunctionCode { get; init; }
    public byte[] EncodedData { get; init; } = [];
    public byte[] Data { get; init; } = [];
    public uint? DataIdentifier { get; init; }

    /// <inheritdoc />
    public ProtocolTrafficKind TrafficKind => ProtocolTrafficKind.Business;

    /// <inheritdoc />
    public string Summary
    {
        get
        {
            if (!IsValid)
                return $"645 error {Error?.Code}: {Error?.Message}";

            var dataIdentifier = DataIdentifier.HasValue ? DataIdentifier.Value.ToString("X8") : "-";
            return $"645 {DeviceId} C={Control:X2} Func={FunctionCode:X2} DI={dataIdentifier}";
        }
    }

    /// <summary>
    /// 创建一个携带原始字节和错误原因的无效 645 帧。
    /// </summary>
    public static Dlt645Frame Invalid(ProtocolErrorCode code, string message, byte[] raw) => new()
    {
        IsValid = false,
        Error = new ProtocolError(code, message),
        Raw = raw
    };
}
