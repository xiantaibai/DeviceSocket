namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示 HTTP 期望命令与设备下行帧之间的关联可信度。
/// </summary>
public enum CommandAssociationConfidence
{
    /// <summary>尚未关联到下行帧。</summary>
    None,

    /// <summary>依据老服务同设备串行命令队列和设备地址 FIFO 顺序关联。</summary>
    Ordered,

    /// <summary>报文指纹与已经关联的命令一致，判定为同一命令的重试或重复下发。</summary>
    Duplicate,

    /// <summary>无法与本轮任何已计划命令可靠关联。</summary>
    Unmatched
}
