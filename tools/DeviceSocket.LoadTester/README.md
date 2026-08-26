# DeviceSocket.LoadTester

`DeviceSocket.LoadTester` 是独立于 `DeviceSocket.Simulator` 和 `DeviceSocket.Debugger` 的并发与稳定性测试工具。Simulator、Debugger 继续用于单设备协议与指令联调；LoadTester 负责批量 TCP 建连、376.1 登录、心跳、自动重连、Gather 读表指令应答、多负载机分片和测试报告。

当前正式场景聚焦 `Gather.TcpCenter` 老服务，使用数据库真实存在的 `meterId,deviceId` CSV。场景目录只保留可用于正式回归和验收的脚本，协议异常、重复登录、645 容量等开发专项不再与正式场景混放。

## 核心能力

- 使用 Q/GDW 376.1 模拟 TCP 建连、登录确认、心跳确认和退出登录。
- `gatherTcpCenter` 兼容模式可识别老服务固定 SEQ、无回显数据区确认和 `AFN=0CH/F129` 读表命令。
- 根据下行地址、MSA、PN、PSEQ 生成老 CmdCenter 可解析的 A15/A14 读表业务应答。
- 连接、登录、写入、心跳和 HTTP 请求分别设置超时，单条链路阻塞不会永久占用设备任务。
- 支持 Ramp、Burst、指数退避重连、故障与恢复阶段。
- 支持 `devicesOnly`、`apiOnly`、`combined` 三种运行角色；TCP 收包与 HTTP 工作队列相互独立。
- 使用在线注册表选择 HTTP 目标，避免向当前已经离线的设备发送抄读请求。
- 多分片按 CSV 数据行全局序号分配设备，不同负载机不会重复模拟同一设备。
- TCP 建连速率、HTTP RPS、HTTP 并发和在线门槛均按全局值配置，再由工具按分片数换算。
- 输出每秒时间序列、异常恢复检查点、节点报告、CSV、Markdown 和多节点合并报告。
- 可联合 LoadTester 结果与 Gather.TcpCenter 服务端指标 JSONL 生成验收报告和诊断报告。

## 正式场景

| 文件 | 用途 | 是否包含 HTTP | 是否需要人工操作 |
| --- | --- | --- | --- |
| `gather-tcpcenter-connections-10000.json` | 100、1,000、5,000、10,000 台分阶段建连，观察登录追平和 30 分钟在线基线 | 否 | 否 |
| `gather-tcpcenter-reconnect-storm-10000.json` | 一万台稳定在线后重启 TCP 服务，验证真实断线和全量重连恢复 | 否 | 需要重启服务 |
| `gather-tcpcenter-http-reading-10000.json` | 一万台在线并调用读表 API，模拟设备应答和同设备多命令排队 | 是 | 否 |
| `gather-tcpcenter-soak-10000.json` | 一万台六小时长稳，观察内存、线程、句柄、Socket 和在线数趋势 | 否 | 否 |
| `gather-tcpcenter-reconnect-storm-50000.json` | 四分片共五万台稳定在线后重启 TCP 服务，验证大规模真实重连风暴 | 否 | 需要重启服务 |
| `gather-tcpcenter-http-reading-50000.json` | 四分片共五万台在线、全局最高 50 RPS，并对全部设备各异步抄读一次 | 是 | 否 |

`Scenarios/gather-device-10000.csv` 和 `Scenarios/gather-device-50000.csv` 分别是当前一万台、五万台正式设备映射。多分片运行时每台负载机都部署同一份完整 CSV，由工具自动固定分配设备；CSV 格式示例移到 `docs/examples/gather-meter-device-map.example.csv`，示例数据不能直接用于正式压测。

## 运行前检查

1. 确认场景中的 `target.host`、`target.port` 和 HTTP `baseUrl` 属于同一套待测环境。
2. 确认 CSV 中每个设备都是压测专用设备，且没有真实终端同时在线。
3. 确认 CSV 有效数据行数等于 `devices.count`，`meterId` 和规范化后的 `deviceId` 均不重复。
4. 从负载机执行 `Test-NetConnection <目标IP> -Port <端口>` 或 Linux `nc -vz <目标IP> <端口>`。
5. Windows 负载机执行 `netsh int ipv4 show dynamicport tcp`，确认单个源 IP 的临时端口能容纳本分片连接及重连产生的 `TIME_WAIT`。
6. 压测前保存服务端版本、代码分支、机器规格、应用配置和监控基线。

Gather 老服务可能在完成数据库在线登记前就回复协议确认。因此客户端“登录成功”表示已收到老式 ACK，不完全等于业务在线表已经更新。验收登录慢路径时，应把 LoadTester 的 TCP/登录时间序列与服务端指标 JSONL 对齐分析。

## 一万台纯设备压测

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-connections-10000.json `
  --run-id gather-connections-after-r1
```

该场景不调用 HTTP，也不主动断开连接，适合作为改造前后共同基线。重点观察：

- TCP 连接到达 10,000 的时间；
- 协议登录确认追上 TCP 连接的时间；
- 服务端业务在线追上物理连接的时间；
- 心跳确认、超时、远端关闭和自动重连；
- 稳态 CPU、内存、线程、句柄、Socket 和 Redis 指标。

场景中已经包含 100 台探活和分阶段爬升，不再单独维护 smoke 或 5,000 台脚本。

## 一万台真实服务重启风暴

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-reconnect-storm-10000.json `
  --run-id gather-restart-after-r1
```

控制台进入 `restart-tcpcenter-now` 后会先显示 5 秒准备倒计时，再给出 30 秒操作窗口。请在出现“请立即操作”后重启被测 `Gather.TcpCenter`，无需按键确认；多分片时只由 `shard-000` 提示，但所有分片都会等待相同窗口。该阶段没有配置客户端主动断线，因此测量包含：

- 服务端进程退出造成的真实远端断线；
- TCP 服务冷启动和监听恢复；
- 一万台终端自动重连；
- 登录、内部状态和业务在线恢复。

如果没有执行重启，`minimumReconnects` 门槛会失败，避免把一次没有注入故障的运行误判为通过。报告把本阶段首次中断标记为计划重启，恢复后的再次掉线仍按非预期事件验收；连接和登录成功率只采用重启前正常阶段，恢复时间则取整轮最严重事件。正式比较时，改造前后应在相同阶段位置、相同服务停止时长和相同负载机条件下执行。

## 一万台设备加 HTTP 指令

```powershell
$env:GATHER_AUTHORIZE_KEY = '<测试环境密钥>'
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-http-reading-10000.json `
  --run-id gather-http-after-r1
```

测试环境不需要鉴权时，把 `authorizationKeyEnvironmentVariable` 保持为空，不会发送鉴权请求头。需要鉴权时将其改为 `GATHER_AUTHORIZE_KEY`，密钥只放入进程环境变量，不写入场景和报告。

当前脚本面向 8 核 16 GiB 测试服务器，不再保留低性能服务器的“最多 1,000 请求、5 RPS”专版。标准阶段为：

1. `functional-sync-smoke-20`：20 条同步请求验证 HTTP → TCP 下发 → 终端应答 → 结果返回闭环。
2. `async-ramp-5/10/20/50rps`：每级 500 台设备，逐级确认服务端命令消费和终端应答可以排空。
3. `all-devices-async-once`：对全部在线设备各提交一次异步抄读；一万台时为 10,000 条。
4. `same-device-queue-1000x5`：1,000 台设备各连续提交 5 条异步命令，观察同设备队列和队头阻塞。
5. `final-sync-20x2`：大流量排空后再执行 40 条同步请求，检查结果订阅和 HTTP 等待资源是否退化。

不要直接把全部设备请求都改为 `showResult=true`。同步接口可能为每条请求创建结果订阅并长时间等待设备，容易先耗尽 HTTP 请求线程或 Redis 连接，掩盖 TCP 服务本身的结论。

`targetDeviceSource=onlineRegistry` 表示每个 combined 分片只从本机当前在线设备中选取目标。进入队列和真正发送前会再次检查在线状态，离线目标记入 `ApiRequestsSkippedOffline`，不会继续调用 API。

## 六小时长稳

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-soak-10000.json `
  --run-id gather-soak-after-r1
```

默认稳态 21,600 秒。正式发布验收可改为 86,400 秒执行 24 小时长稳。长稳场景不混入 HTTP 或人工故障，主要判断资源是否随时间持续增长、在线数是否缓慢下降以及心跳错误是否累积。

## 3～5 万台多分片扩展

正式场景中的设备数量、阶段比例、连接速率和 HTTP 目标均按全局口径设计。仓库已经提供两个五万台四分片场景；继续复制扩容时必须同时调整：

1. 从数据库导出包含全部目标设备的完整 CSV，例如 `gather-device-30000.csv` 或 `gather-device-50000.csv`。
2. 把 `devices.count` 改为 CSV 的有效数据行数，并修改 `devices.deviceMappingFile`。
3. 修改场景名和 RunId，避免与一万台结果混合。
4. 所有负载机部署内容完全一致的场景和完整 CSV；不要提前手工拆成多个 CSV。
5. 所有节点使用相同 `--run-id`、`--shard-count`、`--start-at-utc`，只修改 `--shard-index`。

CSV 数据行从零开始形成 `GlobalOrdinal`。第 `n` 行由 `n % shardCount` 对应的节点负责，因此设备不会重复，也不会因节点数量变化而改变全局设备集合。

以 4 个分片运行 50,000 台重连风暴为例：

```powershell
# 四台负载机使用相同命令，只把 shard-index 分别改为 0、1、2、3。
DeviceSocket.LoadTester run `
  --config Scenarios\gather-tcpcenter-reconnect-storm-50000.json `
  --run-id gather-reconnect-storm-50000-r1 `
  --shard-index 0 `
  --shard-count 4 `
  --start-at-utc 2026-08-22T14:00:00Z
```

五万台 HTTP 场景把 50 RPS、HTTP 并发和目标数都视为四个分片合计值，运行命令只需把配置换成 `gather-tcpcenter-http-reading-50000.json`。默认会对五万台设备各提交一次异步命令，理论发送时间约 1,000 秒；只做抽样时可复制场景并把 `all-devices-async-once-50000.targetDeviceCount` 从 `0` 改为 `1000`。

多分片配置口径：

- `devices.count` 是所有分片合计设备数。
- `connection.connectionsPerSecond` 和阶段 `connectionsPerSecond` 是所有分片合计速率；工具允许小数本地速率，精确除以分片数。
- `commandWorkload.phases[].requestsPerSecond` 和 `maxConcurrency` 是所有分片合计 HTTP 预算。
- `targetDeviceCount=0` 表示全部全局设备，HTTP 场景扩到 50,000 台后会覆盖 50,000 台，而不是每个分片各覆盖 50,000 台。
- `maxConcurrentConnecting` 是单个负载进程的本地上限，需按负载机 CPU 和端口情况设置。
- 验收优先使用比例字段，避免从 10,000 扩到 50,000 后仍保留固定 9,800 的无效门槛。

单个源 IP 连接同一目标 IP/端口受临时端口空间限制。30,000～50,000 台不要放在一台 Windows 负载机上，建议把每个源 IP 的长期连接控制在约 8,000～12,000，并根据实际动态端口范围、`TIME_WAIT` 和负载机资源复测。多台负载机比在同一机器启动多个进程更容易隔离 CPU、端口和 GC 干扰。

HTTP combined 场景在多分片时由每台负载机同时发请求。全局 200 RPS 会被四个分片换算为每台约 50 RPS；如果扩容后希望增加总请求速率，应修改场景中的全局值，不能给每个节点分别填写 200 RPS。

## 多节点报告合并

每个节点输出到：

```text
<outputDirectory>/<runId>/shard-<index>/
├─ node-timeseries.jsonl
├─ run-checkpoint.json
├─ node-summary.json
├─ errors.csv
├─ http-errors.csv
├─ command-traces.jsonl
├─ phase-targets.csv
├─ downstream-classification.csv
├─ downstream-unknown-samples.jsonl
└─ report.md
```

HTTP 指令场景中，`phase-targets.csv`/`command-traces.jsonl` 用于按 meterId、设备号和请求序号定位端到端缺口；`downstream-classification.csv`/`downstream-unknown-samples.jsonl` 用于区分服务端未下发与模拟器尚未兼容的 376.1 报文格式。纯连接场景仍会创建空表头文件，便于自动收集脚本保持固定文件清单。

收集所有分片目录后执行：

```powershell
DeviceSocket.LoadTester report merge `
  --input collected\gather-connections-50000-r1 `
  --output collected\gather-connections-50000-r1\aggregate
```

合并器会拒绝 RunId、目标、场景配置、设备空间、分片总数不一致，以及分片重复或缺失的报告。

## 离线验收和诊断报告

压测结束后可以联合 LoadTester 结果与 Gather.TcpCenter 指标 JSONL 生成两类 HTML：

```powershell
.\tools\DeviceSocket.LoadTester\scripts\New-GatherLoadTestReport.ps1 `
  -ResultPath .\load-test-results\gather-http-after-r1\shard-000 `
  -ServerMetricsPath .\load-test-results\gather-http-after-r1\shard-000\20260821.jsonl `
  -BackgroundPath .\tools\DeviceSocket.LoadTester\docs\report-background.example.json `
  -Audience Both
```

- `acceptance-report.html`：面向验收汇报，只包含测试背景、结论、验收规则、核心指标和报表视图。
- `diagnostic-report.html`：面向开发和运维，增加问题分析、时间线和排查数据。

背景 JSON、阈值、时区、改造前后对比和输出文件说明见 [离线详细报告使用手册](docs/detailed-report.md)。

## 配置文档

所有 JSON 字段的路径、类型、默认值、零值语义、约束和使用场景统一维护在 [LoadTester JSON 配置手册](docs/json-configuration.md)。新增、删除或修改配置字段时，必须在同一变更中更新该文档、至少一个正式场景和配置覆盖测试。

常用规则：

- 场景 JSON 支持 `//` 和 `/* */` 注释，但不允许未知字段。
- 相对 CSV 路径按场景 JSON 所在目录解析。
- `distributed.runId` 用于目录、随机种子和多节点合并，同一轮所有分片必须一致。
- `checkpointIntervalSeconds` 不能小于 `sampleIntervalSeconds`。
- `minimumFinalOnlineCount` 等固定数量门槛不适合直接复用到不同设备规模；可设为 `null` 并使用比例门槛。

## 发布

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools\DeviceSocket.LoadTester\publish.ps1
```

脚本生成框架依赖的 `win-x64` 和 `linux-x64` 包。目标负载机需要安装 .NET 8 Runtime；LoadTester 不需要开放入站端口，只需要能够访问 TCP 服务和可选 HTTP API。

## 验证

```powershell
dotnet test tests\DeviceSocket.LoadTester.Tests\DeviceSocket.LoadTester.Tests.csproj -c Release
```
