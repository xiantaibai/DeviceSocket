# 日志设计

Gateway 使用 Serilog 统一承接日志，但按用途输出到两个相互隔离的文件流。

## 日志分类

| 分类 | 写入入口 | 默认文件 | 内容 |
| --- | --- | --- | --- |
| 程序日志 | `ILogger<T>` | `logs/app/app-.jsonl` | 启停、连接生命周期、协议错误、异常和日志健康告警 |
| Command 日志 | `IDeviceTrafficSink` | `logs/command/command-.jsonl` | 每一条设备上报和服务端下发的结构化原始指令 |

Command 日志使用紧凑 JSON Lines 格式，每行是一条独立事件。核心字段包括：

- `Direction`：`Upstream` 表示上报，`Downstream` 表示下发；
- `MsgId`：单条消息的全链路唯一标识；
- `AttemptNo`：可靠消息当前发送次数，普通消息允许为空；
- `TraceId`：跨服务调用的链路标识；
- `DeviceId`、`SessionId`、`Protocol`：设备连接上下文；
- `Source`、`Bytes`、`RawHex`、`OccurredAt`：消息来源、长度、原始报文和实际发生时间。

原始报文只能通过 `IDeviceTrafficSink` 记录，不得再用 `ILogger<T>` 重复输出。这样程序日志不混入大量通信数据，Command 日志也不会夹杂框架运行信息。

程序日志同样使用 JSON Lines 格式，由 `SysLogJsonFormatter` 输出 `Longdate`、`Level`、`Logger`、`LocalHost`、`Callsite`、`TraceId`、`Host`、`Application`、`Info` 和 `Exception` 字段。写入程序日志文件时过滤 `SourceContext` 以 `Microsoft.` 开头的框架日志，但控制台仍保留这些信息，避免丢失本地启动诊断能力。

## 配置位置

所有 Gateway 日志配置集中放在 `src/Gateway/DeviceSocket.Host/Configuration/Logging`：

- `serilog.json`：日志级别、文件路径、滚动保留、缓冲容量和监控周期；
- `SerilogLoggingExtensions.cs`：Serilog Sink 分流与注册；
- `SysLogJsonFormatter.cs`：程序日志 JSON Lines 字段格式；
- `DeviceLoggingOptions.cs`：强类型配置；
- `CommandLogAsyncMonitor.cs`、`CommandLogHealthWorker.cs`：Command 异步缓冲的积压和丢弃监控。

配置文件会复制到构建和发布目录，路径为相对于 Gateway 内容根目录的相对路径，也可以改成绝对路径。

## 性能与异常策略

- 两类文件均通过异步 Sink 写入，Socket 收发线程不等待磁盘 I/O；
- Command 缓冲满时不阻塞通信链路，丢弃数量由监控器写入程序告警日志；
- Command 缓冲达到 80% 时写入程序告警日志；
- 单条日志构造异常由 `IDeviceTrafficSink.TryWrite` 隔离，不允许传播到协议处理和 Socket 收发链路；
- 默认按天和文件大小双重滚动，保留数量与天数同时受配置约束。

Command 日志用于审计和排障，不承担重要消息的可靠投递状态存储。可靠消息仍由 Orleans `IDeviceCommandGrain` 持久化并按状态机重试。
