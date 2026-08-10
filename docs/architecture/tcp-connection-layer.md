# TCP 连接层生产化优化说明

## 1. 文档范围与容量目标

本文记录 `DeviceSocket.Host` 设备 TCP 连接层截至 2026-08-10 的生产化能力和本版优化细节，覆盖：

- SuperSocket TCP 接入与进程级连接限流；
- Session 登录、合法活动、重复登录和关闭生命周期；
- 僵尸连接清理与 TCP KeepAlive；
- 协议拆包、发送超时和有界背压；
- 连接层指标、配置和验证范围。

本版按单台 8 核 16 GB Linux Gateway 承载 60,000～80,000 条设备长连接设计。设备通常每 120 秒上报一次心跳：

- 60,000 条连接均匀分布时，约为每秒 500 条上行心跳和 500 条下行确认；
- 80,000 条连接均匀分布时，约为每秒 667 条上行心跳和 667 条下行确认；
- 生产验收还必须覆盖同步心跳、集中重连、网络黑洞、慢接收设备和大批僵尸连接回收等峰值。

默认连接硬上限设置为 85,000，为 80,000 条目标连接保留约 5,000 条接入余量。该配置只表示 Gateway 允许接入，不等同于目标 Linux 主机已经通过 80,000 条连接长稳验收。

## 2. 当前连接层结构

### 2.1 SuperSocket 子宿主

Q/GDW 376.1 和 DL/T 645 分别使用独立 SuperSocket 子宿主，但共用同一个进程级 `DeviceConnectionLimiter` 和 `DeviceTcpSessionManager`。

两个子宿主分别从 `DeviceSocket:Listeners:QGdw3761` 和 `DeviceSocket:Listeners:Dlt645` 读取监听地址、端口、`BackLog` 和 `NoDelay`。`BackLog` 属于单个 Listener，用来限制已经完成 TCP 握手但尚未被 SuperSocket `AcceptAsync` 取走的连接队列，不是在线 Session 上限。

两个子宿主均注册官方 Session 容器和空闲清理中间件：

```csharp
.UseInProcSessionContainer()
.UseClearIdleSession()
```

`ClearIdleSessionInterval` 和 `IdleSessionTimeOut` 分别映射到 `IdleScanIntervalSeconds`、`IdleTimeoutSeconds`。官方中间件按底层收发活动清理传输层空闲连接，关闭事件仍经过统一生命周期回调释放设备绑定、配额和指标。

### 2.2 分层接入保护

`DeviceConnectionLimiter` 在 Session 进入协议处理链路前依次检查：

1. Gateway 设备 TCP 总连接数；
2. 尚未完成协议登录或设备绑定的连接数；
3. 可选的单远端 IP 并发连接数；
4. 当前自然秒内允许接入的连接数。

两个协议端口共用同一组容量计数，不能分别突破总连接上限。被拒绝的连接不会进入协议解析和设备注册流程。

单 IP 限制默认值为零，即关闭。设备可能共享运营商 NAT，压测连接也可能来自少量负载机，因此不能默认按 IP 限制。关闭该能力时，本版不再执行 `IPAddress.ToString()`、不在 `Admission` 保存地址，也不维护 IP 计数字典，减少大连接量下无业务价值的字符串和字典开销。

### 2.3 Session 双层空闲治理

`DeviceTcpSessionManager` 使用单个并发字典跟踪全部设备 Session，不为每条连接创建 Timer。`DeviceTcpSessionCleanupWorker` 通过 `AddHostedService` 注册，由外层 Generic Host 自动启动。

默认每 30 秒执行一轮协议层扫描：

- Session 建立后超过 30 秒仍未完成登录或设备绑定，按登录超时关闭；
- 已认证 Session 超过 420 秒没有收到合法协议帧，按合法报文空闲超时关闭；
- 垃圾字节、非法校验和和错误帧不会刷新合法活动时间；
- 单轮最多并发关闭 128 条连接，避免集中回收冲击线程池。

协议层看门狗和 SuperSocket 官方空闲清理用途不同：官方 `LastActiveTime` 反映底层收发活动，无效字节和服务端下发也可能刷新；协议层只认可通过协议校验的上行帧，并额外负责登录期限。

扫描式超时存在一个扫描周期的执行误差：30 秒登录期限通常在 30～60 秒内执行关闭，420 秒空闲期限通常在 420～450 秒内执行关闭。420 秒默认值约等于三次 120 秒心跳周期，再加网络抖动和扫描误差。

### 2.4 TCP KeepAlive

接受设备连接后按 Socket 设置：

- 空闲 180 秒后开始 TCP KeepAlive；
- 探测间隔 30 秒；
- 连续失败 3 次后由操作系统判定连接失效。

平台不支持细粒度 KeepAlive 参数时只告警一次，协议层合法报文空闲超时仍负责最终回收。

## 3. 本版优化明细

### 3.1 修复超时扫描误关健康连接

旧实现先生成超时 Session 列表，再使用有限并发逐条关闭。在扫描与实际关闭之间，设备可能刚完成登录或收到合法心跳，但旧候选仍会调用关闭。

本版改为：

1. 初次扫描只保存 `SessionEntry` 引用和候选原因；
2. 真正关闭前确认字典中仍是同一个 `SessionEntry`，避免 SessionId 复用误关新连接；
3. 在单 Session 生命周期锁内重新判断认证状态和最后合法活动时间；
4. 只有仍符合原超时条件时才切换为 Closing 并执行底层关闭。

登录认证、合法心跳刷新和超时关闭认领共用单 Session 生命周期锁，因此只有同一条连接发生局部竞争，不会引入全局锁。已经先完成的登录或心跳可以撤销旧扫描候选；已经先认领的关闭会阻止迟到活动重新激活失效 Session。

### 3.2 移除重复发送信号量

SuperSocket 2.1.0 的 `PipeConnectionBase.SendAsync` 已为每条连接维护发送锁，并在写入 `PipeWriter` 前完成串行化。本版删除 `SessionEntry` 中额外的 `SemaphoreSlim`：

- 不再为 85,000 条 Session 常驻第二套发送信号量；
- 每次下发减少一次重复异步锁等待；
- 仍由 SuperSocket 保证同一连接的写入顺序；
- Gateway 继续保留单 Session 和全局待发送消息、字节背压计数。

发送超时由 `DeviceTcpSessionManager` 创建 15 秒链接取消令牌，并传入 SuperSocket `SendAsync`。该超时覆盖等待 SuperSocket 内部发送锁和异步 Pipe 写入。调用方主动取消只终止本次调用，不会按发送超时关闭 Session。

`SuperSocketOptions.SendTimeout` 仍使用同一个配置值并按毫秒设置，用于框架可能使用的同步 Socket 路径；根据 .NET `Socket.SendTimeout` 语义，它不能作为异步 `SendAsync` 的独立超时保证。异步下发的确定性超时以 Gateway 传入的取消令牌为准。

达到以下任一条件时直接关闭慢 Session，并把失败返回给调用方：

- 单 Session 待发送消息超过 16 条；
- 单 Session 待发送数据超过 65,536 字节；
- Gateway 待发送数据超过 256 MiB；
- 单次发送超过 15 秒。

该策略只保护进程内存和连接可用性，不增加发送重试，也不改变可靠消息状态机。

### 3.3 避免半包和粘包重复复制

旧 PipelineFilter 每次收到数据都会对完整 `UnreadSequence` 执行 `ToArray()`。376.1 最大帧约 16 KB，使用 4 KB 接收缓冲时，大帧分段到达会反复复制累计的 4 KB、8 KB、12 KB 和 16 KB 数据；粘包还会把后续帧一并复制。

本版两个协议 Filter 均改为：

1. 使用 `SequenceReader` 检查起始符和固定头；
2. 从协议长度字段计算当前帧精确总长度；
3. 半包直接返回，不复制、不消费；
4. 完整帧到齐后只复制当前帧的精确范围；
5. Codec 通过 `ReadCompleteFrame` 直接持有该数组，不再为 `Raw` 复制第二份完整帧；
6. 非法头部仍进入原 Codec 错误隔离路径，保持垃圾消费和错误帧记录语义。

这项优化不会改变半包、粘包、非法校验和及协议错误处理结果。

### 3.4 心跳日志在 RawHex 转换前过滤

每条合法上行帧原先分别为最近消息和通信日志执行一次 `HexCodec.ToHex(frame.Raw)`。本版先根据协议分类和设备策略判断是否需要记录：

- 非心跳只生成一次 `RawHex`，并由最近消息和上行日志复用；
- 心跳默认不生成 `RawHex`、不进入最近消息、也不写上行日志；
- 心跳活动刷新、运行指标和协议确认不受日志过滤影响；
- 指定设备或稳定采样命中的心跳及其确认分别进入上行和下行日志。

原始指令仍统一通过 `IDeviceTrafficSink` 写入独立设备日志，程序日志不重复记录原始报文。完整策略和 EventId 见 `logging.md`。

### 3.5 Session 注册异常不再静默运行

Session 打开回调现在检查 `DeviceTcpSessionManager.RegisterSession` 返回值。若出现重复 SessionId 或管理状态冲突：

- 不增加已打开连接指标；
- 记录一次程序告警；
- 立即以 `ApplicationError` 关闭未能被看门狗跟踪的 Session。

这避免异常连接脱离登录期限和合法活动清理。

### 3.6 调整 8 万连接容量上限

`MaxConnections` 默认值由 70,000 调整为 85,000，允许接入 80,000 条目标连接并保留少量余量。`ReceiveBufferSize` 和 `SendBufferSize` 继续保持 4 KB：

- 当前心跳和设备指令以小报文为主，4 KB 能减少大量长连接下的缓冲内存压力；
- 协议层支持跨多次读取组合半包，大于 4 KB 的帧不会因此解析失败；
- 增大缓冲会提高每连接内存和 Linux Socket 缓冲压力，应由大报文占比和压测数据驱动，而不是直接放大。

### 3.7 按监听器拆分连接等待队列

旧配置把 Q/GDW 376.1 和 DL/T 645 的端口放在 `DeviceSocket` 根节点，并共用一个 `BackLog=8192`。该参数虽然最终正确传入了 SuperSocket `ListenOptions`，但配置结构和容量说明容易把它误解为 8 万在线连接的组成部分。

本版改为每个协议 Listener 独立配置：

- `BackLog` 默认 4096，只表示已完成握手并等待应用接受的连接队列长度；
- `MaxConnections=85000` 继续负责 Gateway 已接受设备连接的进程级硬上限；
- 两个协议可根据各自的集中重连压力独立调整地址、端口、`BackLog` 和 `NoDelay`；
- IP、端口和正数 `BackLog` 在主宿主启动时统一校验，错误配置不会延迟到子宿主监听阶段；
- TCP 服务启动日志会记录实际 IP、端口和 `BackLog`，便于核对部署覆盖值。

4096 是集中建连压测的起始值，并不是由 60,000～80,000 条稳态长连接推导出来的。Linux 上只有观察到监听端口队列持续接近上限，或 `ListenOverflows`、`ListenDrops` 增长时才增大；如果队列始终接近零，则不需要因为在线连接数增加而同步放大。

## 4. 配置基线

监听配置位于 `DeviceSocket:Listeners:{协议}`。

| 参数 | 默认值 | 单位与生产语义 |
| --- | ---: | --- |
| `Ip` | `Any` | 监听地址；支持 Any、IPv6Any 或具体 IP |
| `Port` | 5001 / 5002 | Q/GDW 376.1 和 DL/T 645 的独立 TCP 端口 |
| `BackLog` | 4096 | 条；已完成握手、等待应用 Accept 的连接队列上限，不是在线连接上限 |
| `NoDelay` | true | 禁用 Nagle 算法，降低设备小报文发送延迟 |

连接容量、超时和缓冲配置位于 `DeviceSocket:ConnectionLimit`。

| 参数 | 默认值 | 单位与生产语义 |
| --- | ---: | --- |
| `MaxConnections` | 85000 | 条；Gateway 设备 TCP 总连接硬上限 |
| `MaxUnauthenticatedConnections` | 5000 | 条；未登录或未绑定设备的连接上限 |
| `MaxConnectionsPerIp` | 0 | 条；零表示关闭单 IP 限制和相关地址统计 |
| `MaxConnectionsPerSecond` | 5000 | 条/秒；零表示关闭接入速率限制 |
| `LoginTimeoutSeconds` | 30 | 秒；完成登录或首帧设备绑定的期限 |
| `IdleTimeoutSeconds` | 420 | 秒；传输层空闲和合法报文空闲期限 |
| `IdleScanIntervalSeconds` | 30 | 秒；官方清理和协议层看门狗扫描周期 |
| `MaxConcurrentSessionCloses` | 128 | 条；单轮扫描最大并发关闭数 |
| `MaxConsecutiveProtocolErrors` | 5 | 条；连续非法协议帧关闭阈值 |
| `SendTimeoutSeconds` | 15 | 秒；等待 SuperSocket 发送锁和异步 Pipe 写入的总期限 |
| `MaxPendingSendMessagesPerSession` | 16 | 条；单 Session 待发送消息上限 |
| `MaxPendingSendBytesPerSession` | 65536 | 字节；单 Session 待发送数据上限 |
| `MaxPendingSendBytes` | 268435456 | 字节；Gateway 待发送数据总上限 |
| `ReceiveBufferSize` | 4096 | 字节；SuperSocket 接收和 Socket 接收缓冲配置 |
| `SendBufferSize` | 4096 | 字节；SuperSocket 发送和 Socket 发送缓冲配置 |
| `PackageHandlingTimeoutSeconds` | 10 | 秒；单条协议包处理期限 |
| `TcpKeepAliveEnabled` | true | 是否启用传输层 KeepAlive |
| `TcpKeepAliveTimeSeconds` | 180 | 秒；开始 KeepAlive 探测前的空闲时间 |
| `TcpKeepAliveIntervalSeconds` | 30 | 秒；相邻 KeepAlive 探测间隔 |
| `TcpKeepAliveRetryCount` | 3 | 次；KeepAlive 失败重试次数 |

强类型配置在 Gateway 启动时执行校验。监听地址、端口、`BackLog`、容量、超时、缓冲或背压参数无效时拒绝启动，避免错误配置进入生产。

## 5. 当前主动关闭场景

Gateway 会在以下场景关闭真实 TCP Session：

- 登录超时；
- 合法协议帧空闲超时；
- SuperSocket 官方传输层空闲超时；
- 连续非法协议帧达到阈值；
- 同一 DeviceId 被新 Session 重复登录替换；
- 376.1 退出登录确认完成；
- 旧 Session 或未登录 Session 继续发送心跳；
- 发送超时；
- 单 Session 或 Gateway 发送积压达到背压上限；
- Session 注册状态冲突。

重复登录时先把在线表切换到新 Session，再关闭旧 Session。旧 Session 的迟到关闭事件按 SessionId 条件清理，不会删除新连接路由。

## 6. 指标与自动化验证

`GET /api/status/connections` 提供：

- 当前、累计打开/关闭和历史峰值连接数，以及已跟踪、已认证和未认证连接数；
- 总容量、未认证容量、单 IP 和速率拒绝累计数；
- 当前与历史峰值待发送消息数、字节数；
- 登录超时、空闲超时、协议错误、重复登录、退出、发送超时、发送背压和旧 Session 关闭累计数；
- 心跳接收、心跳确认、心跳日志过滤和按设备/采样记录数量；
- Session 扫描次数、最近及最大耗时、扫描 Session、候选和实际关闭数量；
- 三条日志流的缓冲水位、容量、丢弃数与通信日志提交失败数；
- 进程 CPU、工作集、托管堆、GC 暂停、线程池排队、句柄数和运行时间。

本版自动化测试新增或继续覆盖：

- 登录超时和合法帧空闲超时使用不同条件；
- 扫描生成候选后完成登录，不会被旧登录超时结果关闭；
- 扫描生成候选后收到心跳，不会被旧空闲结果关闭；
- SuperSocket 官方超时与应用主动超时不重复计数；
- 单 Session 发送背压关闭及计数归零；
- 连接和发送积压历史峰值使用原子更新且不会回退；
- 心跳默认在 RawHex 生成前过滤，设备列表热更新后立即记录；
- 上下行日志类别、EventId 和结构化字段保持隔离；
- 376.1、645 半包不消费，粘包逐帧消费；
- 完整帧数组直接转交 Codec 的 Raw 模型，不再二次复制；
- 总连接、未认证、单 IP 和每秒接入限制保持并发准确。

## 7. 生产验收仍需完成

本版完成代码层优化，但单节点 80,000 条连接仍需在目标 Linux 环境实测：

- 60,000、70,000、80,000 分阶段连接建立和至少 24 小时长稳；
- 心跳完全均匀与同秒集中两种模型；
- 网络黑洞、慢接收、集中断线和集中重连；
- 376.1 最大帧、半包、粘包和非法帧洪峰；
- CPU、工作集、托管堆、GC 暂停、线程池、文件描述符和 Socket 状态；
- 两个监听端口的等待接受队列，以及 Linux `ListenOverflows`、`ListenDrops` 增量；
- 程序、设备上行和设备下行日志的磁盘吞吐、异步队列丢弃和保留空间；
- Orleans 路由队列、租约周期和 120 秒设备心跳的匹配情况。

可执行步骤和验收口径见 `../operations/linux-single-node-load-test.md`。暂不增加每连接定时器、发送自动重试、多级超时状态或更大的默认缓冲区。后续调整必须由压测指标证明瓶颈，避免连接层重新复杂化。
