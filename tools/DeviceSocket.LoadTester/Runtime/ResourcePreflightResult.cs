namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 表示负载启动前采集的本机资源事实和非阻断风险提示，用于控制台展示并写入节点报告。
/// </summary>
/// <param name="Facts">运行时、操作系统、CPU、内存和目标解析等客观信息。</param>
/// <param name="Warnings">可能让负载机先成为瓶颈但不直接阻止测试的风险说明。</param>
internal sealed record ResourcePreflightResult(
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Warnings);
