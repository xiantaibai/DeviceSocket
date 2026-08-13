# DeviceSocket.LoadTester

`DeviceSocket.LoadTester` 是独立于 `DeviceSocket.Simulator` 的无界面并发与稳定性测试工具。Simulator 继续用于单设备指令联调；LoadTester 用于批量连接、协议登录、周期心跳、自动重连、Gateway 重启恢复、长稳和多负载机分片测试。

## 功能

- Q/GDW 376.1：TCP 建连、登录确认严格匹配、心跳确认、退出登录。
- DL/T 645：TCP 建连、首个合法地址帧绑定、周期读数上报。
- 连接、协议登录、写入和心跳确认独立超时。
- 立即、固定、指数退避加抖动三种重连方式。
- Ramp 和 Burst 连接模型、最大并发建连限制。
- 预热、爬升、稳态、故障、恢复、冷却阶段编排。
- 重复设备身份、集中/分批主动断线和容量上限场景。
- 可按阶段注入网络时延、上下行丢包/黑洞、非法校验和、半包与粘包。
- 对需要确认的376.1下行帧和645读请求自动生成协议应答。
- 可选轮询 Gateway 状态 API，按测试前基线核对本轮新增 TCP 与认证设备峰值，并记录 CPU、内存、线程池、发送积压、Session 扫描和三条日志流丢弃。
- 多机 `RunId + ShardIndex + ShardCount` 地址分片。
- 每秒 JSONL 时间序列、节点 JSON/CSV/Markdown 报告。
- 可合并延迟直方图及多节点汇总报告。
- 配置化成功率、在线率、协议错误率和恢复时间验收门槛。

## 本地运行

先启动 Gateway，然后执行：

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\smoke-3761.json `
  --run-id smoke-001
```

645 示例：

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\smoke-645.json `
  --run-id smoke-645-001
```

进程退出码：

| 退出码 | 含义 |
| ---: | --- |
| 0 | 测试执行完成且全部启用门槛通过 |
| 1 | 文件、网络初始化或内部执行失败 |
| 2 | 命令行或配置校验失败 |
| 3 | 测试完成但验收门槛未通过 |
| 130 | 用户取消测试 |

## Gateway 重启恢复

使用 `Scenarios/restart-storm.json`。阶段进入 `restart-gateway-now` 后停止 Gateway，在该阶段内重新启动。设备会持续重连；进入 `recovery` 后，工具以该阶段目标在线数判断恢复时间。

真实设备模式使用 `exponentialJitter`，避免所有设备永久同步重试。最坏连接风暴使用 `immediate`，用于验证服务端容量和拒绝策略。

## 随仓库提供的场景

| 文件 | 用途 |
| --- | --- |
| `smoke-3761.json` | 10 台376.1设备的登录、心跳和基础报告冒烟测试 |
| `smoke-645.json` | 10 台645设备的地址绑定和周期通信冒烟测试 |
| `intranet-local-5000-10000.json` | 单台本地负载机分阶段模拟5,000和10,000台376.1终端 |
| `restart-smoke.json` | 10 台376.1设备的手工停服、重启和恢复验收 |
| `restart-storm.json` | 10,000 台设备在 Gateway 重启后的集中重连压力 |
| `protocol-chaos-3761.json` | 时延、丢包、黑洞、坏校验、半包、粘包、断线波次和恢复组合测试 |
| `duplicate-login.json` | 100 条连接复用 80 个设备身份，验证 Session 替换和在线表去重 |
| `capacity-limit-645.json` | 1,000 台设备冲击配置为 500 的服务端连接上限 |
| `soak-60000.json` | 60,000 台设备的多机爬升和六小时长稳测试 |
| `soak-80000.json` | 80,000 台设备的多机爬升和六小时上限长稳测试 |

### 本地连接内网 Gateway 的5,000～10,000终端测试

复制 `intranet-local-5000-10000.json`，至少把两个 `CHANGE-ME-INTRANET-IP` 替换为公司内网 Gateway 地址。默认只连接376.1的 TCP 5001端口，并关闭状态 API 观测，所以只替换 `target.host` 即可运行；如果本机可以访问 Gateway 的 HTTP 5080端口，可把 `serverObservation.enabled` 改为 `true`，同时替换 `statusUrl`，并按需增加服务端认证设备验收门槛。

该场景依次执行：60秒爬升至5,000台、稳定5分钟、90秒扩展至10,000台、稳定10分钟、10秒冷却下线。连接速率默认每秒250条，心跳为60秒加10秒随机抖动，断线后采用指数退避重连，避免内网服务突然承受同步重连风暴。

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\intranet-local-5000-10000.json `
  --run-id intranet-local-001
```

正式运行前先用 `Test-NetConnection <内网IP> -Port 5001` 验证网络和防火墙。Windows 本机还应执行 `netsh int ipv4 show dynamicport tcp`；若动态端口范围明显不足以容纳10,000条连接及重连产生的 `TIME_WAIT`，应先停在5,000阶段观察，或换用多台负载机分片测试。

## 多负载机运行

同一轮测试的所有节点必须使用相同配置、`RunId`、`ShardCount` 和 UTC 启动时间，只修改 `ShardIndex`：

```powershell
# 节点 1
DeviceSocket.LoadTester run --config soak-60000.json --run-id soak-001 `
  --shard-index 0 --shard-count 4 --start-at-utc 2026-08-07T14:00:00Z

# 节点 2
DeviceSocket.LoadTester run --config soak-60000.json --run-id soak-001 `
  --shard-index 1 --shard-count 4 --start-at-utc 2026-08-07T14:00:00Z
```

四个分片分别生成 `node-summary.json`。将分片目录复制到同一个父目录后合并：

```powershell
DeviceSocket.LoadTester report merge --input collected\soak-001 --output collected\soak-001\aggregate
```

合并器会拒绝 RunId 不同、目标不同、设备空间/场景/验收配置不同、分片重复或分片缺失的报告。单一源 IP 连接固定目标端口会受临时端口和 `TIME_WAIT` 限制，6 万连接应使用多个源 IP 或多台负载机。

部署到外网负载机时，应先复制场景文件并把 `target.host` 改为 Gateway 的可达地址；如需服务端容量验收，同时把 `serverObservation.statusUrl` 改为可达的 HTTP 状态地址。若状态 API 不允许外网访问，可禁用 `serverObservation`，并把两个 `ObservedAuthenticatedDevices` 门槛设为 `null`，此时报告仍保留全部客户端指标。

## 场景配置

- `target`：协议、Gateway 主机和端口。
- `devices`：全局设备总数、起始地址、376.1 行政区划。
- `devices.spanAdministrativeDivisions`：376.1 设备超过单区划 65,535 个终端地址时，递增行政区划并从终端地址 1 继续；默认关闭，8 万场景显式开启。
- `connection`：Ramp/Burst、连接速率、并发度和超时。
- `heartbeat`：心跳间隔、抖动和确认超时。
- `workload`：额外周期上报；376.1 当前使用协议心跳，645 支持读数上报。
- `reconnect`：重连方式、退避、抖动和最大次数；0 次上限表示无限重试。
- `scenario.phases`：阶段类型、持续时间、目标设备比例和阶段连接速率。
- `scenario.phases.faults`：时延、丢包、校验损坏、粘包、分片写入和断线波次。
- `devices.duplicateIdentityPercentage`：复用协议身份的连接比例，用于重复登录竞争。
- `serverObservation`：Gateway 状态 API 采样；启动前先记录基线，容量验收使用本轮新增认证设备峰值；不可用只记录采样失败，不阻断 TCP 负载。
- `acceptance`：自动验收门槛；省略或设为 `null` 表示不启用该规则。`minimumObservedAuthenticatedDevices` 和 `maximumObservedAuthenticatedDevices` 均指扣除测试前基线后的本轮新增认证设备峰值。
- `distributed`：运行编号、分片编号和计划启动时间。

阶段缩容会先降低预期在线数，再关闭多余设备，避免把正常冷却误判为故障。报告保留最后一个非 Cooldown 阶段作为最终验收快照。

故障注入说明：

- `networkLatencyMilliseconds` 在每次客户端写入前增加延迟。
- `outboundPacketLossRate` 和 `inboundPacketLossRate` 使用 0-1 概率；两者设为 1 可形成客户端黑洞。
- `corruptChecksumRate` 修改完整帧的 CS 字节。
- `duplicateFrameRate` 在同一次 Socket 写入中拼接两个完整帧，验证服务端粘包处理。
- `fragmentSizeBytes` 和 `fragmentDelayMilliseconds` 把完整帧拆成多次写入，验证半包处理。
- `disconnectPercentage`、`disconnectBatchSize` 和 `disconnectIntervalMilliseconds` 控制集中或分批断线。

`capacity-limit-645.json` 默认假设测试环境把 Gateway 最大连接数设为 500；启动 Gateway 前可在 PowerShell 中设置 `$env:DeviceSocket__ConnectionLimit__MaxConnections = '500'`。该场景以状态 API 观测到的本轮新增认证设备峰值判定容量，不以645客户端写入首帧后的瞬时状态代替服务端绑定结果。多节点运行时应设置未来的统一 `startAtUtc`，让所有节点在设备建连前完成基线采样。只要配置了任一服务端认证设备门槛，启动前基线采样失败就会使验收失败，不会把不可用误判为零设备。

## 报告文件

每个节点输出到：

```text
<outputDirectory>/<runId>/shard-<index>/
├─ node-timeseries.jsonl
├─ node-summary.json
├─ errors.csv
└─ report.md
```

默认只输出聚合信息，不逐设备保存原始报文。调试协议内容应继续使用 `DeviceSocket.Simulator` 或 Debugger。

8 核 16 GiB Linux Gateway 的文件描述符、分阶段容量、采集命令和验收口径见 `docs/operations/linux-single-node-load-test.md`。

## 发布

安装 .NET 8 SDK 后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools\DeviceSocket.LoadTester\publish.ps1
```

脚本生成框架依赖的 `win-x64` 和 `linux-x64` 包。目标负载机需要安装 .NET 8 Runtime。外网部署时无需开放 LoadTester 入站控制端口，只需允许到 Gateway TCP 端口的出站连接。

## 验证

```powershell
dotnet test tests\DeviceSocket.LoadTester.Tests\DeviceSocket.LoadTester.Tests.csproj -c Release
```
