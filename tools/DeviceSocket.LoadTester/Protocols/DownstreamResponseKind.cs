namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 区分通用协议确认与 Gather 读表业务响应，便于会话应用不同故障模型并单独计量。
/// </summary>
internal enum DownstreamResponseKind
{
    /// <summary>
    /// 普通协议确认或非 Gather 业务响应。
    /// </summary>
    Generic,

    /// <summary>
    /// Gather.TcpCenter AFN=0CH/F129 读表业务响应。
    /// </summary>
    GatherReading,

    /// <summary>
    /// GatherV4 使用设备编号、A3 和 PSEQ 关联的376.1命令响应。
    /// </summary>
    GatherV4Command
}
