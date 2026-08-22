namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 定义离线详细报告的目标受众；不同受众共享同一份分析事实，但采用不同的信息密度和结论表达方式。
/// </summary>
internal enum DetailedReportAudience
{
    /// <summary>仅生成面向项目负责人、客户和验收人员的结论型报告。</summary>
    Acceptance,

    /// <summary>仅生成面向开发和运维人员的证据型排障报告。</summary>
    Diagnostic,

    /// <summary>同时生成验收报告和排障报告。</summary>
    Both
}
