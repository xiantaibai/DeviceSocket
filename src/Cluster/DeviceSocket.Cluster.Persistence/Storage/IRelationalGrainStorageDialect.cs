using DeviceSocket.Cluster.Persistence.Configuration;
using System.Data.Common;

namespace DeviceSocket.Cluster.Persistence.Storage;

/// <summary>
/// 封装数据库标识符、DDL、乐观并发写入和调度索引查询语法，未来数据库只需增加方言实现。
/// </summary>
public interface IRelationalGrainStorageDialect
{
    /// <summary>
    /// 判断首次 INSERT 异常是否由主键或唯一索引竞争引起，以便转换为 Orleans ETag 冲突。
    /// </summary>
    /// <param name="exception">数据库提供程序异常。</param>
    /// <returns>唯一约束冲突返回 true。</returns>
    bool IsUniqueConstraintViolation(DbException exception);

    /// <summary>
    /// 返回创建 Schema、四张应用 Grain 表和必要索引的幂等语句。
    /// </summary>
    /// <param name="options">已验证的表映射配置。</param>
    /// <returns>按执行顺序排列的 DDL 语句。</returns>
    IReadOnlyList<string> GetSchemaStatements(RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成按 GrainId 读取状态的 SQL。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>读取 ETag 和二进制状态的 SQL。</returns>
    string GetReadStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成首次插入状态并返回新 ETag 的 SQL。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>插入 SQL。</returns>
    string GetInsertStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成带旧 ETag 条件的更新并返回新 ETag 的 SQL。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>乐观并发更新 SQL。</returns>
    string GetUpdateStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成带 ETag 条件的状态删除 SQL。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <param name="options">表映射配置。</param>
    /// <returns>乐观并发删除 SQL。</returns>
    string GetDeleteStateSql(RelationalGrainStateTable table, RelationalGrainStorageOptions options);

    /// <summary>
    /// 返回指定类型表写入时需要同步维护的结构化索引列。
    /// </summary>
    /// <param name="table">Grain 类型表。</param>
    /// <returns>不包含通用状态列的索引列名。</returns>
    IReadOnlyList<string> GetIndexColumns(RelationalGrainStateTable table);

    /// <summary>
    /// 生成设备下一条可执行或未来命令的索引查询。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <param name="eligibleOnly">true 查询当前可执行最高优先级；false 查询最早未来命令。</param>
    /// <returns>队首查询 SQL。</returns>
    string GetNextCommandSql(RelationalGrainStorageOptions options, bool eligibleOnly);

    /// <summary>
    /// 生成调度分片设备时间窗口扫描 SQL。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>到期设备查询 SQL。</returns>
    string GetDueDevicesSql(RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成未完成入队命令恢复扫描 SQL。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>入队恢复查询 SQL。</returns>
    string GetPendingEnqueuesSql(RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成终态 CommandGrain 物理删除候选扫描 SQL。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>按分片和终态时间读取 MsgId 的 SQL。</returns>
    string GetCommandPurgeCandidatesSql(RelationalGrainStorageOptions options);

    /// <summary>
    /// 生成终态 CommandGrain Payload 压缩候选扫描 SQL。
    /// </summary>
    /// <param name="options">表映射配置。</param>
    /// <returns>排除已到删除期限记录后读取未压缩 MsgId 的 SQL。</returns>
    string GetPayloadCompactionCandidatesSql(RelationalGrainStorageOptions options);
}
