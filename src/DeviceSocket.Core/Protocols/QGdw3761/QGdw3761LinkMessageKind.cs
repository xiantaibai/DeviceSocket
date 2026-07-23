namespace DeviceSocket.Core.Protocols.QGdw3761;

/// <summary>
/// Q/GDW 376.1 链路接口检测（AFN=02H）的业务类型。
/// </summary>
public enum QGdw3761LinkMessageKind
{
    Other,
    Login,
    Logout,
    Heartbeat
}
