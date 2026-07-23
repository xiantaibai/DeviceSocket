# DeviceSocket Demo

基于 .NET 8 和 SuperSocket 2.1 的 Q/GDW 376.1-2012、DL/T 645-2007 协议验证服务。

协议实现依据当前目录中的：

- `376.1主站通信协议2013.pdf`（正文版本 Q/GDW 376.1-2012）
- `DLT645_2007国网智能电能表通信协议0929 (1).pdf`

## 功能

- TCP 5001：376.1 登录、退出登录、心跳、普通消息解析以及链路确认。
- TCP 5002：645 报文解析；第一条合法帧使用表地址完成在线绑定。
- HTTP 5080：在线数量、设备列表、最近消息、最近错误、协议解码和指定设备下发。
- Swagger UI：`http://127.0.0.1:5080/swagger`，可查看并在线调用全部 HTTP API。
- 内存在线注册表和有界消息/错误存储，可通过接口替换 Redis 或数据库。
- 错误长度、校验和、帧头和结束符不会中断服务。

## 项目结构

```text
src/
├─ DeviceSocket.Core/
│  ├─ Protocols/Common/       协议公共帧模型、错误模型和 Hex 工具
│  ├─ Protocols/QGdw3761/     376.1 帧模型及编解码器
│  ├─ Protocols/Dlt645/       645 帧模型及编解码器
│  └─ Runtime/                连接、消息存储和运行指标抽象
├─ DeviceSocket.Host/
│  ├─ Api/                    HTTP 端点与请求模型
│  └─ Networking/             公共网络能力、业务处理器及协议 TCP 服务
└─ DeviceSocket.Infrastructure.Memory/
   ├─ Connections/            内存在线设备注册表
   └─ Messages/               内存最近消息和错误存储
```

HTTP 路由集中在 `DeviceApiEndpoints`，`Program.cs` 只负责宿主启动和依赖注入。协议专属代码分别位于 `QGdw3761`、`Dlt645` 目录，新增协议时按相同结构扩展。

## 启动

```powershell
dotnet restore DeviceSocket.sln
dotnet build DeviceSocket.sln --no-restore
dotnet run --project src\DeviceSocket.Host\DeviceSocket.Host.csproj --no-build
```

默认监听：

```text
376.1 TCP :5001
645    TCP :5002
HTTP        :5080
```

可在 `src/DeviceSocket.Host/appsettings.json` 修改端口。

## 模拟设备

另开终端模拟 376.1 设备：

```powershell
dotnet run --project tools\DeviceSocket.Simulator\DeviceSocket.Simulator.csproj --no-build -- 3761 127.0.0.1 5001 3301 1
```

设备 ID 为 `3761:3301-00001`。模拟器会发送登录和心跳，并验证服务端的 `AFN=00/F3` 确认。

模拟 645 设备：

```powershell
dotnet run --project tools\DeviceSocket.Simulator\DeviceSocket.Simulator.csproj --no-build -- 645 127.0.0.1 5002 123456789012
```

设备 ID 为 `645:123456789012`。模拟器发送一条带 4 个 `FEH` 前导字节的读数据正常应答，然后等待服务端下发。

## 查询状态

```powershell
Invoke-RestMethod http://127.0.0.1:5080/api/status/connections
Invoke-RestMethod http://127.0.0.1:5080/api/devices
Invoke-RestMethod http://127.0.0.1:5080/api/messages/recent
Invoke-RestMethod http://127.0.0.1:5080/api/errors/recent
```

## Swagger UI

服务启动后访问：

```text
http://127.0.0.1:5080/swagger
```

OpenAPI JSON 地址为：

```text
http://127.0.0.1:5080/swagger/v1/swagger.json
```

## 给指定设备下发

376.1 示例：

```powershell
$body = @{
  protocol = '376.1'
  hex = '684A004A00680001330100000061000004000200000100009D16'
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://127.0.0.1:5080/api/devices/3761%3A3301-00001/messages' `
  -ContentType 'application/json' `
  -Body $body
```

645 读当前正向有功总电能示例：

```powershell
$body = @{
  protocol = '645'
  hex = '68129078563412681104333334336816'
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://127.0.0.1:5080/api/devices/645%3A123456789012/messages' `
  -ContentType 'application/json' `
  -Body $body
```

下发接口只接受一个完整、校验正确且帧内地址与目标设备一致的报文。发送失败不会自动重试，避免控制命令重复执行。

## 独立解码

```powershell
$body = @{
  protocol = '645'
  hex = '68129078563412681104333334336816'
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://127.0.0.1:5080/api/tools/decode' `
  -ContentType 'application/json' `
  -Body $body
```

## 测试

```powershell
dotnet test tests\DeviceSocket.Tests\DeviceSocket.Tests.csproj --no-build --no-restore
```

目前测试覆盖协议长度、地址字节序、376.1 登录/心跳/确认、645 数据域 `±33H`、FEH 前导、半包、垃圾前导、校验和异常及重复登录 Session 清理。

## 存储替换说明

`IOnlineDeviceRegistry`、`IRecentStore<ReceivedDeviceMessage>` 和 `IRecentStore<DeviceErrorEvent>` 是存储边界。

Redis 或数据库只能保存设备在线元数据和消息记录，不能保存 SuperSocket 的实际 Session。多节点部署时仍需保留节点本地 `SessionId -> IAppSession` 映射，并通过 Redis/MQ 把下发请求路由到持有连接的节点。

## 6 万连接

本 Demo 不在开发机执行 6 万连接压测。部署到独立 8 核 16 GB 环境后，再使用多个压测源 IP 或多台压测机完成 1 万、3 万、6.5 万阶梯验证。业务代码没有为每个连接创建独立线程、定时器或无界队列。
