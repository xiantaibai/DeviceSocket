using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 保存从协议帧中提取的结构化检索字段，供分类筛选和请求回复关联使用。
/// </summary>
/// <param name="Protocol">报文所属协议。</param>
/// <param name="TrafficKind">协议解析器识别出的协议无关业务类型。</param>
/// <param name="DeviceId">协议标准化后的设备编号；无法解析时为空字符串。</param>
/// <param name="InstructionKey">适合筛选展示的 AFN/FN、功能码或数据标识组合。</param>
/// <param name="ApplicationFunctionCode">376.1 AFN（应用层功能码）；非 376.1 协议时为空。</param>
/// <param name="Functions">376.1 数据单元功能号集合；协议不提供时为空集合。</param>
/// <param name="Sequence">376.1 PSEQ/RSEQ 的低四位序号；协议不提供时为空。</param>
public sealed record DebugTrafficMetadata(
    DeviceProtocol Protocol,
    ProtocolTrafficKind TrafficKind,
    string DeviceId,
    string InstructionKey,
    byte? ApplicationFunctionCode,
    IReadOnlyList<int> Functions,
    byte? Sequence);
