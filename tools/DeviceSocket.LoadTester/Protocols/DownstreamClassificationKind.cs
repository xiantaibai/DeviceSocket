namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 区分服务端下行帧的协议形态，避免把“已到达但当前模拟器不支持”误判成“服务端未下发”。
/// </summary>
public enum DownstreamClassificationKind
{
    /// <summary>无法归入其他类型的有效下行帧。</summary>
    Unknown,

    /// <summary>帧校验失败，不能安全生成业务响应。</summary>
    InvalidFrame,

    /// <summary>报文地址与当前 TCP 会话模拟设备不一致。</summary>
    AddressMismatch,

    /// <summary>方向或 PRM（启动标志位）不符合主站主动下行命令。</summary>
    NonPrimaryDownstream,

    /// <summary>Gather 老服务标准 AFN=0CH、单数据单元 F129 普通读表命令。</summary>
    GatherReadingF129,

    /// <summary>AFN=0CH 中包含 F129，但同时携带多个功能单元，当前业务应答编码器尚不支持。</summary>
    GatherReadingMultiFunction,

    /// <summary>AFN=0CH 的其他功能单元，可能属于冻结数据、透明转发或型号专属抄读。</summary>
    GatherAfn0CUnsupportedFunction,

    /// <summary>非 AFN=0CH 的有效 376.1 主站下行命令。</summary>
    OtherQGdw3761Command,

    /// <summary>非 Gather 兼容协议的普通下行帧。</summary>
    GenericProtocolFrame
}
