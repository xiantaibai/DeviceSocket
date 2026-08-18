namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示负载测试配置包含一个或多个无法安全执行的错误。
/// </summary>
/// <param name="errors">已发现的全部配置错误；调用方应传入至少一项带配置路径的中文说明。</param>
public sealed class ConfigurationValidationException(IReadOnlyList<string> errors) : Exception(
    string.Join(Environment.NewLine, errors))
{
    /// <summary>
    /// 获取本次校验发现的全部错误；集合至少包含一项，每项均带配置路径，便于一次修正多个问题。
    /// </summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}
