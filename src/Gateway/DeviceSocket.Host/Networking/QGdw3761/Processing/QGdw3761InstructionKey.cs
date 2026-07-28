using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing;

/// <summary>
/// 376.1 指令路由键；链路命令按类型匹配，普通指令按 AFN 和首个 Fn 匹配。
/// </summary>
public readonly record struct QGdw3761InstructionKey(
    QGdw3761LinkMessageKind LinkMessageKind,
    byte Afn,
    int Function,
    bool IsFallback = false)
{
    /// <summary>创建链路管理指令键。</summary>
    public static QGdw3761InstructionKey ForLink(QGdw3761LinkMessageKind kind) =>
        new(kind, 0, 0);

    /// <summary>创建普通 AFN/Fn 指令键。</summary>
    public static QGdw3761InstructionKey ForApplication(byte afn, int function) =>
        new(QGdw3761LinkMessageKind.Other, afn, function);

    /// <summary>未注册 376.1 指令的默认键。</summary>
    public static QGdw3761InstructionKey Fallback { get; } =
        new(QGdw3761LinkMessageKind.Other, 0, 0, true);
}
