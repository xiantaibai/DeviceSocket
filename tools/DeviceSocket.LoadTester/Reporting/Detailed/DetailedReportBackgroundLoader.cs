using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 读取并校验独立测试背景 JSON，确保字段拼写错误、重复参数和空参数不会静默进入正式报告。
/// </summary>
internal static class DetailedReportBackgroundLoader
{
    /// <summary>
    /// 读取可选背景文件；未提供路径时返回空配置，提供路径时要求文件存在且符合背景配置结构。
    /// </summary>
    /// <param name="path">命令行 <c>--background</c> 指定的 JSON 文件路径。</param>
    /// <param name="cancellationToken">取消文件读取的令牌。</param>
    /// <returns>完成 null 节归一化的背景配置。</returns>
    /// <exception cref="FileNotFoundException">显式背景文件不存在时抛出。</exception>
    /// <exception cref="ConfigurationValidationException">参数名称、值或数量不符合约束时抛出。</exception>
    internal static async Task<DetailedReportBackgroundOptions> LoadAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DetailedReportBackgroundOptions();

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"测试背景 JSON 不存在：{fullPath}", fullPath);

        await using var stream = File.OpenRead(fullPath);
        var options = await JsonSerializer.DeserializeAsync<DetailedReportBackgroundOptions>(
            stream,
            CreateSerializerOptions(),
            cancellationToken) ?? throw new ConfigurationValidationException(["测试背景 JSON 内容不能为空。"]);
        Normalize(options);
        Validate(options);
        return options;
    }

    /// <summary>
    /// 创建大小写不敏感但拒绝未知字段的 JSON 选项，允许尾随逗号和注释以便人工维护配置。
    /// </summary>
    /// <returns>测试背景专用 JSON 序列化选项。</returns>
    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// 把显式 null 的配置节和参数列表恢复为空对象，保证后续合并代码无需分散处理 null。
    /// </summary>
    /// <param name="options">反序列化后的背景配置。</param>
    private static void Normalize(DetailedReportBackgroundOptions options)
    {
        options.Report ??= new DetailedReportIdentityOptions();
        options.Scenario ??= new DetailedReportScenarioOptions();
        options.Server ??= new DetailedReportServerOptions();
        options.Service ??= new DetailedReportServiceOptions();
        options.Service.Parameters ??= [];
    }

    /// <summary>
    /// 校验关键运行参数数量、空值和名称唯一性，避免报告背景出现无法解释的参数行。
    /// </summary>
    /// <param name="options">已完成 null 归一化的背景配置。</param>
    /// <exception cref="ConfigurationValidationException">发现一个或多个错误时抛出全部错误。</exception>
    private static void Validate(DetailedReportBackgroundOptions options)
    {
        var errors = new List<string>();
        if (options.Service.Parameters.Count > 50)
            errors.Add("service.parameters: 最多允许 50 项，避免测试背景区域失去可读性。");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < options.Service.Parameters.Count; index++)
        {
            var parameter = options.Service.Parameters[index];
            if (string.IsNullOrWhiteSpace(parameter.Name))
                errors.Add($"service.parameters[{index}].name: 不能为空。");
            else if (!names.Add(parameter.Name.Trim()))
                errors.Add($"service.parameters[{index}].name: 参数名称“{parameter.Name}”重复。");
            if (string.IsNullOrWhiteSpace(parameter.Value))
                errors.Add($"service.parameters[{index}].value: 不能为空。");
        }

        if (errors.Count > 0)
            throw new ConfigurationValidationException(errors);
    }
}
