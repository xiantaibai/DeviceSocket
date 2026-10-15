namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 表示调试工具针对服务端下行指令生成的自动回复类型。
/// </summary>
public enum QGdw3761AutoReplyKind
{
    /// <summary>Gather 老服务 AFN=0CH/F129 抄读指令的完整业务数据回复。</summary>
    GatherReadingData,

    /// <summary>标准 376.1 或 Gather 老服务对 CON=1 下行命令的 AFN=00H/F3 终端确认。</summary>
    StandardConfirmation,

    /// <summary>GatherV4 按 A3 和 PSEQ 关联的 AFN=00H/F1 任务全部确认。</summary>
    GatherV4Confirmation
}
