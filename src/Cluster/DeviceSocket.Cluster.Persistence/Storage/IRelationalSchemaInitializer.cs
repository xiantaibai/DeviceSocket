namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 确保应用 Grain 独立表在首次读写或索引扫描前可用。
/// </summary>
public interface IRelationalSchemaInitializer
{
    /// <summary>
    /// 幂等完成 Schema 初始化；关闭自动建表时只完成一次快速检查标记。
    /// </summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>初始化任务。</returns>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
}
