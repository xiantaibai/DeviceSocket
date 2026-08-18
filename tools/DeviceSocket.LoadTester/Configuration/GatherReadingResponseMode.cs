namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定 Gather.TcpCenter 读表命令的终端业务应答行为。
/// </summary>
public enum GatherReadingResponseMode
{
    /// <summary>
    /// 立即返回可被老 CmdCenter 正常解析的读表示值。
    /// </summary>
    Normal,

    /// <summary>
    /// 等待配置时长后返回正常读表示值，用于验证慢设备隔离能力。
    /// </summary>
    Delayed,

    /// <summary>
    /// 不返回读表业务帧，用于触发老服务单设备命令超时。
    /// </summary>
    Drop,

    /// <summary>
    /// 返回数据无效的读表业务帧，用于验证业务失败链路。
    /// </summary>
    BusinessError,

    /// <summary>
    /// 连续返回两条相同读表业务帧，用于验证重复应答处理。
    /// </summary>
    Duplicate
}
