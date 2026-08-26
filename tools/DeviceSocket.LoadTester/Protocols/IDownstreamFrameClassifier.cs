using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 定义协议适配器对服务端下行帧进行结构化分类的能力；分类与是否生成响应相互独立。
/// </summary>
internal interface IDownstreamFrameClassifier
{
    /// <summary>
    /// 对一条已经拆包的下行帧进行分类。
    /// </summary>
    /// <param name="frame">已完成协议拆包的帧。</param>
    /// <param name="device">收到该帧的虚拟设备。</param>
    /// <returns>包含协议字段、指纹和支持状态的分类结果。</returns>
    DownstreamFrameClassification ClassifyDownstream(IProtocolFrame frame, VirtualDeviceDescriptor device);
}
