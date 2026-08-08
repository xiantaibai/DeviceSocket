namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单条自动验收规则的实测值、阈值与通过状态。
/// </summary>
public sealed record AcceptanceCheck(
    string Name,
    double Actual,
    string Comparator,
    double Threshold,
    bool Passed);
