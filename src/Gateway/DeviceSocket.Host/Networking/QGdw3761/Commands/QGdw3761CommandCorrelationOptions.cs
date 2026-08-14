namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// 配置单个 Gateway 节点的 Q/GDW 376.1 可靠命令协议应答关联。
/// </summary>
public sealed class QGdw3761CommandCorrelationOptions
{
    /// <summary>
    /// 配置路径；部署配置应放在 DeviceSocket:Protocols:QGdw3761:CommandCorrelation 节点。
    /// </summary>
    public const string SectionName = "DeviceSocket:Protocols:QGdw3761:CommandCorrelation";

    /// <summary>
    /// 当前 Gateway 最多保留的设备命令关联数，默认 100000 条；每个在线 Session 最多一条，零值无效。
    /// </summary>
    public int MaxPendingCommands { get; set; } = 100_000;

    /// <summary>
    /// 清除整体到期关联的扫描周期，单位为秒，默认 30 秒；零值无效，设备断线仍会立即清理。
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 30;
}
