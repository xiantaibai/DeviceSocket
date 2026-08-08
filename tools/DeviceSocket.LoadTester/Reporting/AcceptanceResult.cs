namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一轮测试的全部启用验收规则及最终判定。
/// </summary>
public sealed record AcceptanceResult(bool Passed, IReadOnlyList<AcceptanceCheck> Checks);
