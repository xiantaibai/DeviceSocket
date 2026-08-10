# 日志与观测性设计

Gateway 使用 Serilog 统一承接日志，并把程序运行日志、设备上行日志和设备下行日志拆成三个相互隔离的异步文件流。当前阶段先在 Gateway 内完成封装；等多个服务的字段和采集方式稳定后，再评估提取独立日志类库，避免过早形成难以演进的公共 API。

## 1. 日志分类

| 分类 | 写入入口 | `SourceContext` / `LogType` | 默认文件 | 内容 |
| --- | --- | --- | --- | --- |
| 程序日志 | `ILogger<T>` | 业务类全名 / `Application` | `logs/app/app-.jsonl` | 启停、连接生命周期、协议错误、集群状态、异常和日志健康告警 |
| 设备上行 | `IDeviceTrafficSink` | `DeviceSocket.Traffic.Upstream` / `DeviceUpstream` | `logs/device/upstream-.jsonl` | 设备上报、登录、退出和按策略保留的心跳原始指令 |
| 设备下行 | `IDeviceTrafficSink` | `DeviceSocket.Traffic.Downstream` / `DeviceDownstream` | `logs/device/downstream-.jsonl` | 普通下发、链路确认和与已保留心跳对应的心跳确认 |

设备原始报文只能通过 `IDeviceTrafficSink` 记录，禁止在程序日志中重复输出完整 `RawHex`。三条流独立缓冲、独立滚动，使大量通信数据不会挤占程序故障日志，上行洪峰也不会直接占满下行缓冲。

`syslog`、`httplog`、`extHttplog` 和 `GrpcLog` 属于后续统一日志类库的候选语义。本版 Gateway 尚未具备对应的完整中间件和客户端拦截器，因此不提前创建空壳分类。

## 2. 源生成日志与 EventId

Gateway 固定模板日志统一使用 `LoggerMessageAttribute` 编译时源生成，不在高频路径调用字符串插值或重复解析模板。EventId 分段如下：

| 范围 | 用途 | 已使用事件 |
| --- | --- | --- |
| 1000～1099 | TCP 与 Session | 1001～1009：监听启动、连接冲突、开关连接、接入拒绝、KeepAlive、关闭失败、清理扫描 |
| 1100～1199 | 协议处理 | 1101～1103：协议拒绝、处理超时、链路确认失败 |
| 1200～1299 | Gateway 集群协作 | 1201～1205：RPC 下发、节点租约和设备路由发布失败 |
| 1900～1999 | 日志设施健康 | 1901～1902：缓冲丢弃和 80% 高水位 |
| 2000～2099 | 设备上行 | 2001 普通上行，2002 心跳上行 |
| 2100～2199 | 设备下行 | 2101 普通下行，2102 心跳确认 |

设备通信日志核心字段包括：

- `Direction`：`Upstream` 表示上报，`Downstream` 表示下发；
- `EventId`、`EventName`：用于稳定检索和告警，不依赖消息文本；
- `MsgId`、`AttemptNo`、`TraceId`：关联跨服务调用和可靠消息发送次数；
- `DeviceId`、`SessionId`、`Protocol`：定位设备连接；
- `TrafficKind`：区分业务、心跳、链路管理和确认；
- `Source`、`Bytes`、`RawHex`、`OccurredAt`：消息来源、长度、原始报文和发生时间。

程序日志的 `SysLogJsonFormatter` 输出 `Longdate`、`Level`、`Logger`、`LogType`、`EventId`、`EventName`、`TraceId`、`SpanId`、`MsgId`、`Host`、`Application`、`Info`、`Exception` 和 `Properties`。程序文件默认过滤 `Microsoft.*` 日志，控制台仍保留启动诊断信息。

## 3. 心跳日志策略

心跳约每两分钟一次，6～8 万连接会产生持续的大量上行和确认日志。默认配置 `HeartbeatSampleRate = 0` 且设备列表为空，因此：

1. 合法心跳仍刷新 Session 活动时间、增加心跳指标并正常发送确认；
2. 在生成 `RawHex` 和最近消息对象前完成日志策略判断；
3. 未命中策略的上行心跳及其下行确认都不写通信日志；
4. 业务报文、登录、退出和普通链路确认始终记录。

排查单台设备时，把设备编号加入 `HeartbeatEnabledDeviceIds`。该列表通过配置热更新，无需重启 Gateway。需要观察一部分设备时可设置 `HeartbeatSampleRate`，取值为 0～1；采样使用稳定的设备哈希，同一设备跨进程重启保持相同结果，避免逐条心跳随机采样造成日志忽隐忽现。

示例：

```json
{
  "DeviceLogging": {
    "HeartbeatSampleRate": 0.001,
    "HeartbeatEnabledDeviceIds": ["330100000001"]
  }
}
```

优先使用设备列表临时诊断；只有需要整体趋势样本时才启用比例采样。`EventId` 适合查询和告警，不适合作为采样依据，因为同一类心跳共享相同 EventId，无法稳定选出部分设备。

## 4. 缓冲、滚动与失败语义

默认配置：

| 配置 | 默认值 | 含义 |
| --- | ---: | --- |
| `ApplicationBufferSize` | 10,000 | 程序日志异步队列容量 |
| `UpstreamBufferSize` | 50,000 | 设备上行异步队列容量 |
| `DownstreamBufferSize` | 50,000 | 设备下行异步队列容量 |
| `FileSizeLimitBytes` | 268,435,456 | 单个滚动文件上限，约 256 MiB |
| `RetainedFileCount` | 31 | 每条流最多保留文件数 |
| `RetainedDays` | 30 | 每条流最长保留天数 |
| `FlushIntervalSeconds` | 1 | 文件 Sink 刷盘周期 |
| `MonitorIntervalSeconds` | 10 | 缓冲健康扫描周期 |

- 三条流均使用非阻塞异步 Sink，Socket 收发线程不等待磁盘 I/O；
- 缓冲满时丢弃新日志而不阻塞通信链路，累计丢弃数暴露到状态 API；
- 缓冲达到 80% 或产生新增丢弃时，健康 Worker 写入程序告警日志；
- `IDeviceTrafficSink.TryWrite` 隔离同步日志异常，不允许异常传播到协议和 Socket 路径；
- 日志用于审计和排障，不承担可靠消息状态存储。

## 5. 状态接口

`GET /api/status/connections` 除连接和发送指标外，还提供：

- `applicationLogCount`、`upstreamLogCount`、`downstreamLogCount` 及各自容量、累计丢弃数；
- 上下行日志提交数、同步写入失败数、心跳过滤数、按设备记录数和按采样记录数；
- 心跳接收与心跳确认发送数；
- 当前/历史发送积压、Session 扫描次数、耗时、候选和实际关闭数量；
- 进程 CPU、工作集、托管堆、GC 提交量和暂停比例；
- 线程池线程、排队工作项、可用 Worker/I/O 线程、进程句柄数和运行时间。

LoadTester 会轮询该接口，把峰值和相对测试前基线新增的日志丢弃写入节点及汇总报告。状态接口当前是压测和本机诊断入口，不等于完整的 OpenTelemetry 指标出口；生产发布前仍需补齐鉴权、Prometheus/OpenTelemetry 导出和集中告警。

## 6. 配置位置

Gateway 日志实现集中在 `src/Gateway/DeviceSocket.Host/Configuration/Logging`：

- `serilog.json`：级别、文件、滚动、缓冲和心跳策略；
- `SerilogLoggingExtensions.cs`：三条 Serilog 流的注册与分流；
- `Messages/*.cs`：`LoggerMessageAttribute` 源生成事件；
- `SysLogJsonFormatter.cs`：程序日志 JSON Lines 格式；
- `DeviceLoggingOptions.cs`：强类型配置与中文语义；
- `LogBufferMonitor*`、`LogBufferHealthWorker.cs`：异步缓冲健康监控。

配置文件复制到构建和发布目录并支持重新加载。相对路径以 Gateway 内容根目录解析；生产环境可改为挂载数据盘的绝对路径。

Linux Docker Compose 将 `deploy/linux/config/serilog.json` 只读挂载到容器 `/app/Configuration/Logging/serilog.json`，并把 `/app/logs` 挂载到宿主机持久化目录。修改宿主机日志配置可触发热更新；容器内仍使用非 root UID 1654，因此宿主机日志目录必须按该 UID 授权。

## 7. 本版验证记录

2026-08-10 完成以下验证：

- `dotnet format DeviceSocket.sln --verify-no-changes --no-restore` 通过；
- Release 全解决方案构建通过，零警告、零错误；
- Gateway/Core 测试 53 项通过，LoadTester 测试 39 项通过；
- `linux-x64` Gateway 与 LoadTester 框架依赖发布包生成成功；
- 本机真实 TCP 冒烟接入 10 台 376.1 设备，心跳接收 32 次、确认发送 32 次、日志策略过滤 32 次；
- 冒烟期间程序、上行、下行日志丢弃均为 0，通信日志同步提交失败为 0；上行/下行文件分别生成 2001/2101 结构化事件，未生成 2002/2102 心跳事件。

当前开发机未安装 WSL Linux 发行版或 Docker，因此以上结果不代表 6～8 万 Linux 单节点容量已经验收。目标 Linux 主机仍需按 `../operations/linux-single-node-load-test.md` 执行分阶段测试并归档报告。
