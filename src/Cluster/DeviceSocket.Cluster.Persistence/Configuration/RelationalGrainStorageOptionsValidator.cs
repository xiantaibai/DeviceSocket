using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Persistence.Configuration;

/// <summary>
/// 验证 SQL Server 连接、超时和可安全引用的 Schema/表名。
/// </summary>
public sealed partial class RelationalGrainStorageOptionsValidator : IValidateOptions<RelationalGrainStorageOptions>
{
    /// <summary>
    /// 验证关系型 Grain 存储配置。
    /// </summary>
    /// <param name="name">命名选项名称，本配置不区分名称。</param>
    /// <param name="options">待验证配置。</param>
    /// <returns>成功结果或具体配置错误集合。</returns>
    public ValidateOptionsResult Validate(string? name, RelationalGrainStorageOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            failures.Add("Orleans:Persistence:ConnectionString 不能为空。");
        if (options.CommandTimeoutSeconds <= 0)
            failures.Add("Orleans:Persistence:CommandTimeoutSeconds 必须大于零。");
        ValidateIdentifier(options.Schema, "Orleans:Persistence:Schema", failures);
        ValidateIdentifier(options.Tables.Device, "Orleans:Persistence:Tables:Device", failures);
        ValidateIdentifier(options.Tables.Command, "Orleans:Persistence:Tables:Command", failures);
        ValidateIdentifier(options.Tables.Gateway, "Orleans:Persistence:Tables:Gateway", failures);
        ValidateIdentifier(options.Tables.Scheduler, "Orleans:Persistence:Tables:Scheduler", failures);

        var tableNames = new[]
        {
            options.Tables.Device,
            options.Tables.Command,
            options.Tables.Gateway,
            options.Tables.Scheduler
        };
        if (tableNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tableNames.Length)
            failures.Add("每种 Grain 类型必须配置不同的状态表名。");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// 验证标识符只包含安全字符，防止配置内容进入 SQL 标识符时形成注入。
    /// </summary>
    /// <param name="value">标识符值。</param>
    /// <param name="path">错误消息中的配置路径。</param>
    /// <param name="failures">累计错误集合。</param>
    private static void ValidateIdentifier(string value, string path, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96 || !SafeIdentifier().IsMatch(value))
            failures.Add($"{path} 长度不得超过 96，且只能包含字母、数字和下划线并以字母或下划线开头。");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
