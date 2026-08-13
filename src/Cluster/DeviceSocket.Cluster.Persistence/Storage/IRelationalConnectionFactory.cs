using System.Data.Common;

namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 创建数据库连接的可替换抽象，使 Grain 逻辑不依赖 SQL Server 客户端类型。
/// </summary>
public interface IRelationalConnectionFactory
{
    /// <summary>
    /// 创建尚未打开的数据库连接，调用方负责异步打开和释放。
    /// </summary>
    /// <returns>新的数据库连接。</returns>
    DbConnection CreateConnection();
}
