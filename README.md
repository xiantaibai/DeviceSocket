# DeviceSocket Demo

基于 .NET 8、SuperSocket、Orleans 和 MagicOnion 的设备通信验证项目，当前支持 Q/GDW 376.1-2012 与 DL/T 645-2007。

协议实现依据仓库根目录中的两份协议文档：

- `376.1主站通信协议2013.pdf`
- `DLT645_2007国网智能电能表通信协议0929 (1).pdf`

## 当前能力

- TCP 5001：376.1 登录、退出登录、心跳、普通消息解析和链路确认。
- TCP 5002：645 报文解析，首个合法地址帧完成在线绑定。
- HTTP 5080：网关状态、在线设备、最近消息、最近错误、协议解码和本节点直接下发。
- HTTP/2 7001：网关内部 MagicOnion Unary 下发服务。
- Orleans：设备路由、SessionEpoch、网关节点租约，以及重要消息的持久化、重试、到期和确认状态。
- 内存实现：当前 Demo 的在线表、近期消息和 Orleans 状态；后续可以替换 Redis 或数据库。
- 每条合法上报与下发均记录 `Direction`、`MsgId`、设备、Session、协议和原始报文日志。
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
│  └─ DeviceSocket.Cluster.Silo/          独立 Orleans Silo 宿主
└─ Infrastructure/
   └─ DeviceSocket.Infrastructure.Memory/ 内存连接表和近期消息存储

tools/
├─ DeviceSocket.Debugger/                 Windows 协议调试 UI
├─ DeviceSocket.Debugger.Core/            指令集、编解码和 TCP 调试能力
└─ DeviceSocket.Simulator/                376.1 / 645 设备模拟器
```

仓库保留总解决方案 `DeviceSocket.sln`，并提供 Gateway、Cluster、Tools 三个 `.slnf`，便于只加载当前服务及其依赖。

## 构建

```powershell
dotnet restore DeviceSocket.sln
dotnet build DeviceSocket.sln -c Release --no-restore
dotnet test tests\DeviceSocket.Tests\DeviceSocket.Tests.csproj -c Release --no-build --no-restore
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

如果只验证协议解析、不启动 Orleans，可以将网关 `appsettings.json` 中的 `Orleans:Enabled` 改为 `false`。

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
- `SendReliableMessageAsync`：重要消息，先持久化，再下发；默认五分钟到期，并根据确定失败、结果不确定和协议确认状态安排重试。

调用方重试重要消息时必须复用原 `MsgId`。同一 `MsgId` 如果更换设备、协议或报文会被拒绝。`Completed` 只表示已写入设备 Socket；只有要求并收到协议层确认时才会进入 `Confirmed`。

## 日志

Gateway 通过 Serilog 将日志分为两类：程序日志写入 `logs/app/app-.jsonl`，设备上报和下发 Command 日志写入 `logs/command/command-.jsonl`。两者均按行保存结构化 JSON；Command 日志可通过 `Direction`、`MsgId`、`TraceId`、设备、协议和发送次数检索。

日志级别、路径、滚动保留和异步缓冲配置集中在 `src/Gateway/DeviceSocket.Host/Configuration/Logging/serilog.json`。详细字段和异常策略参见 `docs/architecture/logging.md`。

## 连接容量限制

Gateway 对 376.1 和 DL/T 645 使用同一个进程级连接配额，默认最大连接数为 70,000。配置位置为 `DeviceSocket:ConnectionLimit:MaxConnections`；容量检查通过 SuperSocket `MiddlewareBase.RegisterSession` 执行，达到上限时返回 `false`，由 SuperSocket 2.1.0 立即以 `Rejected` 原因关闭，不会进入登录、协议解析和设备注册流程。

当前连接数继续通过原有 `GET /api/status/connections` 查询。拒绝洪峰只记录前十次和每一千次告警，避免恶意或异常重连产生过量磁盘日志。

## 当前 Demo 边界

- Orleans 使用本机单 Silo、内存 Grain Storage 和内存 Reminder，进程重启后状态会丢失，仅用于开发验证。
- 生产部署需要把 Orleans 集群发现、`DeviceStore`、`GatewayStore`、`CommandStore` 和 Reminder 存储替换为 Redis、数据库或其他持久化实现。
- 真实 Socket 只存在 Gateway 本机内存中，Orleans 只保存可序列化的路由元数据。
- 上报业务异步分发、Kafka/Outbox 和各协议的响应匹配规则属于下一阶段；当前已建立 `MsgId`、SessionEpoch、节点租约、Gateway 幂等、可靠消息状态机和自动重试基础。
- 6 万连接需要在独立 8 核 16 GB 环境分阶段压测，本地开发阶段以业务链路通畅和异常隔离为目标。

生产化的分阶段改造内容、测试矩阵和上线门槛参见 `docs/architecture/production-readiness-plan.md`。
