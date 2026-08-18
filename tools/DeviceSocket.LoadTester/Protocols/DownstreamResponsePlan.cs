namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 描述虚拟设备收到下行帧后应执行的协议响应、延迟和重复发送行为。
/// </summary>
/// <param name="Payload">待写入 Socket 的完整报文；为空表示有意不应答。</param>
/// <param name="Kind">响应所属的业务类型。</param>
/// <param name="Delay">首次响应前等待时长。</param>
/// <param name="SendCount">相同响应的发送次数；零表示不发送。</param>
/// <param name="IsBusinessFailure">是否为协议格式合法但业务数据无效的模拟失败响应。</param>
internal sealed record DownstreamResponsePlan(
    byte[]? Payload,
    DownstreamResponseKind Kind,
    TimeSpan Delay,
    int SendCount,
    bool IsBusinessFailure = false)
{
    /// <summary>
    /// 为普通协议应答创建无延迟、单次发送计划。
    /// </summary>
    /// <param name="payload">完整协议响应报文。</param>
    /// <returns>可直接由虚拟会话执行的响应计划。</returns>
    internal static DownstreamResponsePlan Generic(byte[] payload) =>
        new(payload, DownstreamResponseKind.Generic, TimeSpan.Zero, 1);
}
