namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示负载测试配置包含一个或多个无法安全执行的错误。
/// </summary>
public sealed class ConfigurationValidationException(IReadOnlyList<string> errors) : Exception(
    string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
