# 设备集群与可靠命令架构

## 容量边界

- Orleans Cluster 按接近 100 万设备同时在线设计。
- 单个 `DeviceSocket.Host` 只承担 6～8 万条 TCP 连接，百万在线设备需要多个 Gateway 节点共同承载。
- Gateway 数量和 Silo 数量独立扩展；Gateway 保存真实 Socket，Silo 不保存连接对象。
- 本阶段完成 Orleans 闭环、SQL Server 持久化，以及 Gateway 的 Q/GDW 376.1 `AFN=00H/F3` 协议应答关联；Kafka 和业务查询库不在本阶段范围内。

```text
业务入口 / 未来 Kafka
        │
        ▼
   IDeviceGrain ───────────────> GatewayNodeGrain ──MagicOnion──> DeviceSocket.Host
        │                                                        │
        ├──设备级唯一执行通道                                      ▼
        │                                                       Device
        ▼
IDeviceCommandGrain
        ▲
        │ 到期唤醒
DeviceCommandSchedulerGrain（固定分片）
```

## Grain 职责

### DeviceGrain

`IDeviceGrain` 是设备消息统一入口。每个 DeviceId 的 Grain 调用天然串行，状态只保存：

- 当前 Gateway、SessionId、SessionEpoch 和路由租约；
- 当前占用设备一问一答通道的 MsgId；
- 是否存在待处理工作、下一唤醒时间和调度版本；
- 固定 Scheduler 分片号和最后分配的设备命令顺序号。

DeviceGrain 不保存完整命令队列，不创建设备级 Timer/Reminder。`TouchAsync` 每次校验 Session，但默认每 30 秒最多持久化一次路由心跳，避免百万在线设备把每次心跳都转化为 SQL UPDATE。

### DeviceCommandGrain

每个 MsgId 对应一个 `IDeviceCommandGrain`，保存完整报文和运行状态：

- `EnqueuePending`、`Queued`、`Dispatching`、`AwaitingConfirmation`；
- `RetryPending`、`Confirmed`、`CompletedWithoutConfirmation`、`Expired`、`Failed`、`Cancelled`；
- NotBefore、ExpiresAt、ReplyDeadline、MaxAttempts 和退避时间；
- Priority、DeviceSequence、ActionVersion 和 ActiveSessionEpoch。
- TerminalAt 和 PayloadCompactedAt，用于终态热数据压缩和物理清理。

CommandGrain 不注册 Timer/Reminder。所有异步结果都校验 AttemptNo 和 ActionVersion，设备回复还要校验 SessionEpoch。

### DeviceCommandSchedulerGrain

设备使用稳定 FNV-1a 哈希映射到固定分片：

```text
ShardId = StableHash(DeviceId) & (ShardCount - 1)
```

默认 `ShardCount=1024`，必须是 2 的幂。每个分片只持有：

- 一个按 DueAt 排序的内存最小堆；
- 一个始终指向最近任务的动态 one-shot Timer；
- 一个低频持久化 Reminder，用于 Silo 退出后重建内存堆。

Reminder 数量固定为分片数，不随设备或命令数量增长。Silo 启动后 `DeviceCommandSchedulerBootstrapService` 分批幂等激活所有分片。每次恢复从 SQL 读取未来两分钟窗口，并修复超过恢复时间仍为 `EnqueuePending` 的命令。

## 一问一答流程

1. DeviceGrain 为命令保留设备内顺序号。
2. CommandGrain 先持久化完整命令，状态为 `EnqueuePending`。
3. DeviceGrain 持久化 `HasPendingWork` 和 `NextWakeAt`。
4. CommandGrain 转为 `Queued`；若步骤 2、3 之间退出，Scheduler 后续扫描并幂等修复。
5. DeviceGrain 只在设备通道空闲时，从 Command 类型表索引选择已经到期的最高优先级命令。
6. DeviceGrain 把命令设为当前 Active，再通过 MagicOnion 下发到当前 Gateway。
7. 写入 Socket 后进入 `AwaitingConfirmation`，在设备回复或回复截止时间到达前不会发送下一条命令。
8. 成功回复进入 `Confirmed` 并释放设备通道；失败回复、回复超时或明确未写入按 MaxAttempts 和退避策略重试。
9. ExpiresAt 到达后进入 `Expired`；最大次数耗尽进入 `Failed`，两者都释放设备通道。

同设备批量入口只负责批量受理和连续分配顺序号，执行仍严格逐条一问一答。优先级只重排等待命令，不抢占已经 Active 的命令。未来命令未到 NotBefore 时不会阻塞已经到期的其他命令。

## 调用方向与幂等

关键同步调用保持单向依赖：

```text
业务入口 / Scheduler -> DeviceGrain -> CommandGrain
                              │
                              └────> GatewayNodeGrain / Gateway RPC
```

CommandGrain 不同步反调 DeviceGrain，避免 Grain 循环等待。Scheduler 到 DeviceGrain、DeviceGrain 到 Scheduler 的通知使用 Orleans OneWay；状态先写 SQL，再发送通知。通知丢失时由分片恢复扫描补齐。

Gateway 对可能重复的物理下发继续使用 `MsgId + AttemptNo` 去重：

- 明确未写入时增加 AttemptNo；
- 无法判断是否写入时保持相同 AttemptNo；
- 旧 ActionVersion 的结果不能覆盖新状态；
- 旧 SessionEpoch 的设备回复不能确认重连后的命令。

Q/GDW 376.1 可靠下发还增加协议级幂等：Gateway 为新 MsgId 分配 PSEQ、强制 `CON=1` 并重算 CS；同一 MsgId 的新 AttemptNo 复用 PSEQ。终端的 `AFN=00H/F3` 只有同时匹配 Session、设备、RSEQ 和原 AFN/DA/DT，且没有超过本次回复截止时间，才恢复原 MsgId 并提交 DeviceGrain。硬件侧报文格式、缓存重放和迟到丢弃规则见 [Q/GDW 376.1 可靠命令与终端应答约定](../protocols/qgdw3761-reliable-command-response.md)。

## SQL Server 持久化

应用状态使用自定义 `IGrainStorage`，数据库方言和连接创建分别由 `IRelationalGrainStorageDialect`、`IRelationalConnectionFactory` 隔离。首期实现使用 `Microsoft.Data.SqlClient`；未来替换数据库时增加连接工厂、方言和迁移，不修改 Grain 状态机。

每种 Grain 类型一张表：

| 命名存储 | SQL Server 表 | 内容 |
| --- | --- | --- |
| `DeviceStore` | `orleans_app.OrleansDeviceState` | 路由、Active 命令、NextWakeAt |
| `CommandStore` | `orleans_app.OrleansDeviceCommandState` | Payload、状态、回复超时、重试策略 |
| `GatewayStore` | `orleans_app.OrleansGatewayNodeState` | Gateway 实例和租约 |
| `SchedulerStore` | `orleans_app.OrleansDeviceCommandSchedulerState` | 分片恢复时间和统计水位 |

每张表保存 Orleans 二进制 State、SchemaVersion 和 bigint ETag。调度所需字段同时作为结构化列原子更新，避免扫描或反序列化 Payload：

- 设备到期索引：`SchedulerShard + HasPendingWork + NextWakeAt`；
- 设备队列索引：`DeviceId + Status + NotBefore + Priority DESC + DeviceSequence`；
- 入队恢复索引：`SchedulerShard + Status + EnqueueRecoveryAt`。
- 终态删除索引：`SchedulerShard + TerminalAt`；
- Payload 压缩索引：`SchedulerShard + PayloadCompactedAt + TerminalAt`。

## CommandGrain 历史清理

命令执行生命周期与业务历史查询分开保存。Kafka 和业务查询库负责长期历史；Orleans 的 `OrleansDeviceCommandState` 只保留调度闭环需要的短期状态：

1. `Confirmed`、`CompletedWithoutConfirmation`、`Expired`、`Failed`、`Cancelled` 首次进入时记录 `TerminalAt`。
2. 终态满 `PayloadRetentionDays=7` 天后，由对应 CommandGrain 清空 Payload，保留 PayloadHash、状态、失败说明、尝试次数和时间。
3. 终态满 `CommandRetentionDays=30` 天后，调用 `ClearStateAsync` 物理删除整条 CommandGrain 状态。
4. 非终态命令不因保留期限直接删除。回复超时先按 MaxAttempts 重试并进入 `Failed`，整体 ExpiresAt 到期则进入 `Expired`，之后才参与清理。

清理复用现有 1024 个 `DeviceCommandSchedulerGrain`，不新增设备级或命令级 Timer/Reminder。首次扫描按分片编号分散到一个清理周期内；每个分片按 `CleanupBatchSize` 和 `CleanupMaxBatchesPerRun` 有界处理，并以固定 16 路并发调用候选 CommandGrain。SQL 只负责返回 MsgId，CommandGrain 在串行上下文中再次校验终态和 TerminalAt，因此迟到回复、活动命令或旧扫描结果不能被误删。

现有表升级时会新增两个可空时间列和两个清理索引；旧终态记录按原 `UpdatedAt` 分批回填 `TerminalAt`。生产环境若关闭 `AutoCreateSchema`，必须在切换新版 Silo 前通过受控数据库迁移执行等价 DDL。

`AutoCreateSchema=true` 时首次访问会幂等创建四张应用表。生产环境可在首次迁移完成后设为 false，让运行账号只保留读写权限。

Orleans Membership 和 Reminder 使用官方 ADO.NET 表，不属于应用 Grain 类型表。部署数据库必须依次执行与 Orleans 8.2.0 对应的官方脚本：

1. [SQLServer-Main.sql](https://github.com/dotnet/orleans/blob/v8.2.0/src/AdoNet/Shared/SQLServer-Main.sql)
2. [SQLServer-Clustering.sql](https://github.com/dotnet/orleans/blob/v8.2.0/src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql)
3. [SQLServer-Reminders.sql](https://github.com/dotnet/orleans/blob/v8.2.0/src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql)

应用没有使用 Orleans 通用 ADO.NET Grain Persistence 表，因此不需要执行 `SQLServer-Persistence.sql`。

## 配置

主要配置节：

- `Orleans:Cluster`：ClusterId、ServiceId、Silo/Gateway 端口、AdvertisedIPAddress；
- `Orleans:Persistence`：连接字符串、Schema、命令超时、自动建表和四张表映射；
- `Orleans:CommandScheduling`：分片数、恢复窗口、批量上限、TTL、回复超时、最大次数、退避、路由写入合并间隔，以及整数单位的清理配置。
- `DeviceSocket:Protocols:QGdw3761:CommandCorrelation`：单 Gateway 关联容量和节点级清理周期，全部使用整数。

清理配置均使用整数，不使用 .NET TimeSpan 的天数格式：

```json
{
  "PayloadRetentionDays": 7,
  "CommandRetentionDays": 30,
  "CleanupScanIntervalMinutes": 15,
  "CleanupBatchSize": 200,
  "CleanupMaxBatchesPerRun": 5
}
```

连接字符串应由 `Orleans__Persistence__ConnectionString` 或密钥系统覆盖，禁止把生产凭据写入配置文件。多机部署必须覆盖 `AdvertisedIPAddress`，不能使用默认回环地址。

## 当前未接入部分

- 当前 Gateway 应答闭环覆盖 Q/GDW 376.1 `AFN=00H/F3` 逐数据单元确认；需要返回业务数据的查询类命令，应为对应 AFN/Fn 继续增加专用响应 Handler，不能复用 F3 伪造业务数据响应。
- Gateway 仍会把设备活动逐条调用到 Orleans；本阶段只合并 SQL 写入。百万在线设备的最终吞吐还需要后续在 Gateway 合并或批量发布活动事件。
- Kafka、业务命令历史查询库、跨设备批量拆分和 Control API 不属于本次 Orleans 改造。

## 验收方式

- 常规测试启动真实 Orleans TestingHost，验证设备串行、批量、延迟、回复、超时、重试、取消、过期、旧 Session、恢复状态机，以及终态 Payload 压缩和物理删除前的二次校验。
- `DeviceCommandCapacityInvariantTests` 每次测试对 100 万个设备编号执行稳定哈希，检查 1024 分片覆盖和热点边界。
- Windows 验收环境设置 `DEVICESOCKET_SQLSERVER_INTEGRATION=1` 后，LocalDB 测试会创建随机数据库并验证真实 DDL、索引和队首查询，结束后删除数据库。
- 设置 `DEVICESOCKET_MILLION_SQLSERVER=1` 后，容量测试会临时写入 100 万条 DeviceGrain 状态并验证单分片有界到期扫描；该测试不会在普通 CI 中隐式启动数据库。
