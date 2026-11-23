# DeviceSocket Demo

基于 .NET 8、SuperSocket、Orleans 和 MagicOnion 的设备通讯服务中心项目，当前支持 Q/GDW 376.1-2012 与 DL/T 645-2007，生产能力按阶段建设。

## 项目背景与目标

本服务作为设备与业务系统之间的通讯中心，主要负责设备消息上行转发、业务指令下行投递和指令结果回传。通讯中心维护连接、协议适配、路由、一问一答、调度、超时过期和通讯追踪；业务系统负责计费、分析、工单等业务规则及决定发送内容与时机。

首期目标为系统总在线 8～10 万台设备，支持多 Gateway/Silo 扩展，容量以真实混合负载和故障恢复测试为准。以下为待完成的正式交付范围，不代表当前均已实现：

- 独立监控看板：在线设备数、Socket 连接数、分类队列积压、指令受理/实际发送/应答完成速度、节点状态与告警。
- 设备和指令追踪：查询每台设备当前所在 Gateway/实例/Session，跨节点检索单设备上下行日志、指令时间线及批次进度。
- 统一发送接口：单条和批量均支持立即、延后、指定时间单次发送；批量覆盖同设备多指令及跨设备多指令，始终遵守单设备一问一答。
- 双向消息转发：设备上报和指令结果按约定可靠转发给业务系统，具备有界暂存、重投、查询和故障恢复。

完整职责、看板页面、指标口径、接口草案和时间/批次语义见 [项目背景](docs/architecture/project-background.md)；实施依赖与验收清单见 [生产化改造计划](docs/architecture/production-readiness-plan.md)。

协议实现依据仓库根目录中的两份协议文档：

- `376.1主站通信协议2013.pdf`
- `DLT645_2007国网智能电能表通信协议0929 (1).pdf`

## 当前能力

- TCP 5001：376.1 登录、退出登录、心跳、普通消息解析和链路确认。
- TCP 5002：645 报文解析，首个合法地址帧完成在线绑定。
- HTTP 5080：网关状态、在线设备、最近消息、最近错误、协议解码和本节点直接下发。
- HTTP/2 7001：网关内部 MagicOnion Unary 下发服务。
- Orleans：设备路由、SessionEpoch、网关节点租约，以及重要消息的持久化、重试、到期和确认状态。
- 内存实现：Gateway 本机在线表和近期消息；Orleans 应用状态已使用 SQL Server 四类独立表，集群成员和 Reminder 使用 ADO.NET。
- 业务上报与下发分别记录 `Direction`、`MsgId`、设备、Session、协议和原始报文；高频心跳默认关闭日志并可按设备临时开启。
- 协议指令通过统一泛型 Handler 接口和强类型指令键分发，新 Handler 由 Gateway 启动扫描自动注册。

## 项目结构

```text
src/
├─ Shared/
│  ├─ DeviceSocket.Core/                  协议编解码、连接及消息抽象
│  └─ DeviceSocket.Rpc.Contracts/         MagicOnion 接口和 MessagePack DTO
├─ Gateway/
│  └─ DeviceSocket.Host/                  SuperSocket 网关、Swagger、MagicOnion 服务
├─ Cluster/
│  ├─ DeviceSocket.Cluster.Contracts/     Orleans Grain 接口和传输模型
│  ├─ DeviceSocket.Cluster.Grains/        设备、可靠消息与网关 Grain 实现
│  ├─ DeviceSocket.Cluster.Persistence/   SQL Server 状态存储与命令调度索引
│  └─ DeviceSocket.Cluster.Silo/          独立 Orleans Silo 宿主
└─ Infrastructure/
   └─ DeviceSocket.Infrastructure.Memory/ 内存连接表和近期消息存储

tools/
├─ DeviceSocket.Debugger/                 Windows 协议调试 UI
├─ DeviceSocket.Debugger.Core/            指令集、编解码和 TCP 调试能力
├─ DeviceSocket.Simulator/                376.1 / 645 单设备联调模拟器
└─ DeviceSocket.LoadTester/               并发、重连、长稳和多机负载测试器
```

仓库保留总解决方案 `DeviceSocket.sln`，并提供 Gateway、Cluster、Tools 三个 `.slnf`，便于只加载当前服务及其依赖。

## 构建

```powershell
dotnet restore DeviceSocket.sln
dotnet build DeviceSocket.sln -c Release --no-restore
dotnet test tests\DeviceSocket.Tests\DeviceSocket.Tests.csproj -c Release --no-build --no-restore
dotnet test tests\DeviceSocket.LoadTester.Tests\DeviceSocket.LoadTester.Tests.csproj -c Release --no-build --no-restore
```

## 本机启动顺序

分别打开两个终端，按以下顺序启动：

```powershell
dotnet run --project src\Cluster\DeviceSocket.Cluster.Silo\DeviceSocket.Cluster.Silo.csproj
```

```powershell
dotnet run --project src\Gateway\DeviceSocket.Host\DeviceSocket.Host.csproj
```

访问地址：

```text
网关 Swagger：http://127.0.0.1:5080/swagger
```

当前 Gateway 配置默认 `Orleans:Enabled=false`，用于独立 TCP/协议验证。验证完整可靠指令链路前，需准备 SQL Server 应用表和 Membership/Reminder 表、配置 Silo，并接通 Gateway 客户端；当前客户端仍使用本机发现，生产集群发现及 ClusterId/ServiceId 对齐属于待办，不能只开启布尔开关便视为生产链路就绪。数据库和现有契约见 [集群与可靠命令架构](docs/architecture/cluster-and-rpc.md)。

## Docker 镜像与 Linux 部署

`DeviceSocket.Host` 的构建脚本先在宿主机执行 `dotnet publish`，再用精简 Dockerfile 复制发布产物。默认构建 `linux/amd64` 镜像，也可以导出 tar 供离线 Linux 服务器加载：

```powershell
src/Gateway/DeviceSocket.Host/build-docker-image.ps1 `
  -Tag 1.0.0 `
  -Pull `
  -ArchivePath artifacts/docker/devicesocket-host-1.0.0.tar
```

Linux Compose 配置采用 host 网络、20 万文件描述符、13 GiB 容器内存上限、非 root 用户、只读根文件系统和宿主机日志挂载。部署步骤、端口安全边界、日志热更新与升级回滚见 `src/Gateway/DeviceSocket.Host/deploy/linux/README.md`。

## 模拟设备登录

376.1：

```powershell
dotnet run --project tools\DeviceSocket.Simulator\DeviceSocket.Simulator.csproj -- 3761 127.0.0.1 5001 3301 1
```

设备 ID 为 `3761:3301-00001`。

645：

```powershell
dotnet run --project tools\DeviceSocket.Simulator\DeviceSocket.Simulator.csproj -- 645 127.0.0.1 5002 123456789012
```

设备 ID 为 `645:123456789012`。

## 并发与稳定性测试

`DeviceSocket.LoadTester` 是独立的无界面负载测试项目，不改变单设备 Simulator。当前正式场景面向 Gather.TcpCenter，包含一万/五万台的连接、重连和读表场景，以及一万台六小时长稳；支持多机分片及报告合并。这些场景不等于 DeviceSocket 完整通讯链路的八万/十万验收：

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-connections-10000.json `
  --run-id gather-connections-001
```

多机部署、场景字段、退出码和报告说明参见 `tools/DeviceSocket.LoadTester/README.md`。

## 业务服务下发

业务服务引用 `DeviceSocket.Cluster.Contracts` 和 `Microsoft.Orleans.Client`，连接 Orleans 后直接取得设备 Grain。统一使用两个以 `Send` 开头的入口：

```csharp
var deviceGrain = clusterClient.GetGrain<IDeviceGrain>("3761:3301-00001");

var normalResult = await deviceGrain.SendBestEffortMessageAsync(
    new BestEffortDeviceMessage
    {
        Protocol = "376.1",
        Payload = Convert.FromHexString("684A004A00680001330100000061000004000200000100009D16"),
        TraceId = traceId
    });

var importantResult = await deviceGrain.SendReliableMessageAsync(
    new ReliableDeviceMessage
    {
        MsgId = msgId,
        Protocol = "376.1",
        Payload = Convert.FromHexString("684A004A00680001330100000061000004000200000100009D16"),
        TraceId = traceId
    });
```

- `SendBestEffortMessageAsync`：普通消息，不持久化、不自动重试，离线或失败时允许丢弃。
- `SendReliableMessageAsync`：可靠业务消息，先持久化，默认从受理时起五分钟到期；按设备唯一通道等待协议应答，并根据确定失败、结果不确定和回复超时安排重试。
- `SendReliableMessagesAsync`：已有同设备批量入口，逐条执行；跨设备持久化批次和统一 HTTP 模式接口属于规划能力。

调用方重试可靠消息时必须复用原 `MsgId`。同一 `MsgId` 如果更换设备、协议或报文会被拒绝。`WrittenToSocket` 只表示写入 Socket；可靠业务入口固定要求设备确认，进入 `Confirmed` 也需按协议区分确认与业务数据结果。普通消息在可靠指令占用设备通道时会被拒绝。

现有 `NotBefore` 提供延迟执行基础；新 HTTP 接口计划将立即、延后和指定时间单次发送统一映射为持久化 NotBefore/ExpiresAt，批量共用首次受理时间基准。具体草案见 [项目背景中的发送接口](docs/architecture/project-background.md#41-统一接口草案)。

## 日志

Gateway 通过 Serilog 将日志分为三类：程序日志写入 `logs/app/app-.jsonl`，设备上行写入 `logs/device/upstream-.jsonl`，设备下行写入 `logs/device/downstream-.jsonl`。固定模板使用 `LoggerMessageAttribute` 编译时源生成；设备通信日志可通过 `EventId`、`Direction`、`MsgId`、`TraceId`、设备、协议和发送次数检索。

心跳日志默认在生成 `RawHex` 前过滤，可通过 `HeartbeatEnabledDeviceIds` 热开启指定设备，或通过 `HeartbeatSampleRate` 对设备做稳定采样。日志级别、路径、滚动保留和异步缓冲配置集中在 `src/Gateway/DeviceSocket.Host/Configuration/Logging/serilog.json`；Docker 部署将对应配置挂载到容器内以保留热更新能力。详细字段、EventId 和异常策略参见 `docs/architecture/logging.md`。

## 连接容量限制

Gateway 对 376.1 和 DL/T 645 使用同一个进程级连接配额，默认最大连接数为 85,000，为 80,000 条目标连接预留少量接入余量。配置位置为 `DeviceSocket:ConnectionLimit:MaxConnections`；容量检查通过 SuperSocket `MiddlewareBase.RegisterSession` 执行，达到上限时返回 `false`，由 SuperSocket 2.1.0 立即以 `Rejected` 原因关闭，不会进入登录、协议解析和设备注册流程。

当前连接数继续通过原有 `GET /api/status/connections` 查询。拒绝洪峰只记录前十次和每一千次告警，避免恶意或异常重连产生过量磁盘日志。

## 当前实现边界

- Orleans 已具备 SQL Server 状态、成员和 Reminder 存储配置，仍需完成生产集群接入、多 Silo、数据库备份恢复和故障切换验收。
- 真实 Socket 只存在 Gateway 本机内存中，Orleans 只保存可序列化的路由元数据。
- 376.1 已接通 AFN=00H/F3 应答关联；业务数据应答、645 业务闭环、路由对账、排队过期收敛和回包恢复仍需完善。
- 上报/结果可靠转发、跨设备批次、统一单条/批量模式接口、独立监控看板和完整 OTel 属于本期改造计划。
- 当前 HTTP 本节点直接下发接口尚未统一进入可靠命令通道；近期消息和本地日志不能替代命令历史与结果查询模型。
- 8～10 万总在线需要在目标部署环境完成连接、心跳、问答、转发、看板和日志/OTel 混合负载的长稳与故障验证。

TCP 连接容量、僵尸回收、KeepAlive、重复登录关闭和发送背压参数参见 `docs/architecture/tcp-connection-layer.md`。生产化的其余分阶段改造内容、测试矩阵和上线门槛参见 `docs/architecture/production-readiness-plan.md`。
