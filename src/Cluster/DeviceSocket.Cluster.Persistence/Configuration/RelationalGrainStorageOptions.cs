namespace DeviceSocket.Cluster.Persistence.Configuration;

/// <summary>
/// Orleans 应用 Grain 关系型存储配置，对应配置节 <c>Orleans:Persistence</c>。
/// </summary>
public sealed class RelationalGrainStorageOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Orleans:Persistence";

    /// <summary>SQL Server 连接字符串；默认空值非法，部署时应通过环境变量或密钥配置覆盖。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>应用 Grain 状态表所在 Schema，默认 <c>orleans_app</c>；只能包含字母、数字和下划线。</summary>
    public string Schema { get; set; } = "orleans_app";

    /// <summary>数据库命令超时秒数，默认 30 秒；零值非法。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>是否在首次访问时幂等创建应用 Grain 表，默认 true；生产环境可设为 false 并由迁移流程执行脚本。</summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>每种 Grain 类型的独立表名配置。</summary>
    public RelationalGrainTableOptions Tables { get; set; } = new();
}
