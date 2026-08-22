namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 编排离线详细报告的读取、分析和写入，供命令行入口和测试复用。
/// </summary>
internal static class DetailedReportGenerator
{
    /// <summary>
    /// 根据既有结果生成详细报告，不发起网络请求也不修改源结果。
    /// </summary>
    /// <param name="options">输入、输出、受众、阈值和可选基线参数。</param>
    /// <param name="cancellationToken">取消分析和文件写入的令牌。</param>
    /// <returns>最终分析模型；调用方可据此返回自动化验收退出码。</returns>
    internal static async Task<DetailedReportAnalysis> GenerateAsync(
        DetailedReportGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var analysis = await DetailedReportAnalyzer.AnalyzeAsync(options, cancellationToken);
        await DetailedReportWriter.WriteAsync(
            analysis,
            options.Audience,
            options.OutputDirectory,
            cancellationToken);
        return analysis;
    }
}
