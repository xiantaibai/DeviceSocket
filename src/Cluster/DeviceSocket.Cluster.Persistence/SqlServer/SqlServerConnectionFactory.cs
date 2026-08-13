using System.Data.Common;
using DeviceSocket.Cluster.Persistence.Configuration;
using DeviceSocket.Cluster.Persistence.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Persistence.SqlServer;

/// <summary>
/// 使用 Microsoft.Data.SqlClient 创建 SQL Server 连接。
/// </summary>
public sealed class SqlServerConnectionFactory(IOptions<RelationalGrainStorageOptions> options)
    : IRelationalConnectionFactory
{
    /// <summary>
    /// 按当前已验证连接字符串创建未打开的 SQL Server 连接。
    /// </summary>
    /// <returns>新的 SqlConnection。</returns>
    public DbConnection CreateConnection() => new SqlConnection(options.Value.ConnectionString);
}
