using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Protocols.QGdw3761;

/// <summary>
/// Q/GDW 376.1-2012 完整帧的解析结果，包含链路层、地址域和首个数据单元信息。
/// </summary>
public sealed record QGdw3761Frame : IProtocolFrame
{
    /// <inheritdoc />
    public DeviceProtocol Protocol => DeviceProtocol.QGdw3761;
    /// <inheritdoc />
    public required bool IsValid { get; init; }
    /// <inheritdoc />
    public ProtocolError? Error { get; init; }
    /// <inheritdoc />
    public required byte[] Raw { get; init; }
    /// <inheritdoc />
    public string? DeviceId { get; init; }
    public int UserDataLength { get; init; }
    public byte Control { get; init; }
    public bool IsUpstream { get; init; }
    public bool IsPrimaryMessage { get; init; }
    public byte LinkFunctionCode { get; init; }
    public string? AdministrativeDivision { get; init; }
    public ushort TerminalAddress { get; init; }
    public byte MasterStationAddress { get; init; }
    public bool IsGroupAddress { get; init; }
    public byte Afn { get; init; }
    public byte SequenceByte { get; init; }
    public byte Sequence { get; init; }
    public bool HasTimeTag { get; init; }
    public bool IsFirstFrame { get; init; }
    public bool IsFinalFrame { get; init; }
    public bool ConfirmationRequested { get; init; }
    public byte Da1 { get; init; }
    public byte Da2 { get; init; }
    public byte Dt1 { get; init; }
    public byte Dt2 { get; init; }
    public IReadOnlyList<int> Points { get; init; } = [];
    public IReadOnlyList<int> Functions { get; init; } = [];
    public byte[] DataUnitPayload { get; init; } = [];
    public QGdw3761LinkMessageKind LinkMessageKind { get; init; }

    /// <inheritdoc />
    public ProtocolTrafficKind TrafficKind => LinkMessageKind switch
    {
        QGdw3761LinkMessageKind.Heartbeat => ProtocolTrafficKind.Heartbeat,
        QGdw3761LinkMessageKind.Login or QGdw3761LinkMessageKind.Logout => ProtocolTrafficKind.LinkManagement,
        _ => ProtocolTrafficKind.Business
    };

    /// <inheritdoc />
    public string Summary => IsValid
        ? $"376.1 {DeviceId} AFN={Afn:X2} Fn={string.Join(',', Functions.Select(x => $"F{x}"))} {LinkMessageKind}"
        : $"376.1 error {Error?.Code}: {Error?.Message}";

    /// <summary>
    /// 创建一个可交给业务层记录的无效帧，避免解析异常直接终止连接。
    /// </summary>
    public static QGdw3761Frame Invalid(ProtocolErrorCode code, string message, byte[] raw) => new()
    {
        IsValid = false,
        Error = new ProtocolError(code, message),
        Raw = raw
    };
}
