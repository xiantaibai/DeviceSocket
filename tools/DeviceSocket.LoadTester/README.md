# DeviceSocket.LoadTester

`DeviceSocket.LoadTester` 是独立于 `DeviceSocket.Simulator` 的无界面并发与稳定性测试工具。Simulator 继续用于单设备指令联调；LoadTester 用于批量连接、协议登录、周期心跳、自动重连、Gateway 重启恢复、长稳和多负载机分片测试。

## 功能

- Q/GDW 376.1：TCP 建连、登录确认、心跳确认、退出登录，并可显式兼容 Gather.TcpCenter 老式确认帧。
- DL/T 645：TCP 建连、首个合法地址帧绑定、周期读数上报。
- 连接、协议登录、写入和心跳确认独立超时。
- 立即、固定、指数退避加抖动三种重连方式。
- Ramp 和 Burst 连接模型、最大并发建连限制。
- 预热、爬升、稳态、故障、恢复、冷却阶段编排。
- 重复设备身份、集中/分批主动断线和容量上限场景。
- 可按阶段注入网络时延、上下行丢包/黑洞、非法校验和、半包与粘包。
- 对需要确认的376.1下行帧和645读请求自动生成协议应答。
- Gather.TcpCenter 兼容模式可识别 `AFN=0CH/F129` 读表命令，返回老 CmdCenter 能解析的 A15/A14 业务数据；该命令不会再额外发送可能提前清空当前命令的通用 `AFN=00H` 确认。
- 支持 `devicesOnly`、`apiOnly`、`combined` 三种运行角色；HTTP 使用独立连接池、有界并发和全局分片限速，不阻塞 TCP 收包循环。
- 可调用老 `api/ammeter/reading` 接口，按真实 `meter_id → deviceId` CSV 或连续 meter_id 映射逐设备下发，并组合 `show_result=false/true` 和同设备多命令阶段。
- 可选轮询 Gateway 状态 API，按测试前基线核对本轮新增 TCP 与认证设备峰值，并记录 CPU、内存、线程池、发送积压、Session 扫描和三条日志流丢弃。
- 支持固定行政区划下的顺序地址和区间无重复随机地址，多机 `RunId + ShardIndex + ShardCount` 地址分片保持一致。
- 每秒 JSONL 时间序列、节点 JSON/CSV/Markdown 报告。
- 可合并延迟直方图及多节点汇总报告。
- 配置化成功率、在线率、协议错误率和恢复时间验收门槛。
- 报告独立统计 API 成功/失败/超时与 P95、读表命令收到/应答/丢弃及业务失败/重复响应。

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
| `intranet-local-5000-10000.json` | 使用260810001～260820000真实测试设备，分阶段测试 Gather.TcpCenter 的100、1,000、5,000和10,000连接 |
| `gather-tcpcenter-storm-5000-10000.json` | Gather.TcpCenter 的5,000、10,000台集中建连及10,000台集中断线重连测试 |
| `gather-tcpcenter-reading-10000.json` | 10,000台老服务设备上线后，分阶段调用读表 API、返回业务帧并测试同设备多命令排队 |
| `restart-smoke.json` | 10 台376.1设备的手工停服、重启和恢复验收 |
| `restart-storm.json` | 10,000 台设备在 Gateway 重启后的集中重连压力 |
| `protocol-chaos-3761.json` | 时延、丢包、黑洞、坏校验、半包、粘包、断线波次和恢复组合测试 |
| `duplicate-login.json` | 100 条连接复用 80 个设备身份，验证 Session 替换和在线表去重 |
| `capacity-limit-645.json` | 1,000 台设备冲击配置为 500 的服务端连接上限 |
| `soak-60000.json` | 60,000 台设备的多机爬升和六小时长稳测试 |
| `soak-80000.json` | 80,000 台设备的多机爬升和六小时上限长稳测试 |

### 本地连接 Gather.TcpCenter 老服务的5,000～10,000终端测试

`intranet-local-5000-10000.json` 显式设置 `target.compatibilityProfile=gatherTcpCenter`，用于识别老服务返回的无数据区 `AFN=00H/F1` 确认。运行前按正式部署的 `App.config` 核对 `target.host` 和 `target.port`；Gather 老服务没有 DeviceSocket Gateway 状态接口，因此场景关闭 `serverObservation`，不能配置服务端认证设备数门槛。

场景使用固定行政区划 `2608` 和终端地址 `10001`～`20000`，最终设备号为 `260810001`～`260820000`，共10,000台。它先以每秒10条连接验证100台，再依次扩展到1,000、5,000和10,000台；5,000台稳定5分钟，10,000台稳定10分钟。心跳为60秒加10秒随机抖动，断线后采用指数退避重连。

老服务会在查询设备数据库前回复链路确认，因此报告中的“登录成功”表示收到老式 ACK，不等同于数据库已确认上线。必须确保这10,000个编号是专门预留且没有真实终端同时在线的测试设备，否则模拟登录可能接管同地址的指令路由，并在测试断开时改变设备在线状态。

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\intranet-local-5000-10000.json `
  --run-id gather-tcpcenter-001
```

正式运行前先用 `Test-NetConnection <内网IP> -Port <正式监听端口>` 验证网络和防火墙。Windows 本机还应执行 `netsh int ipv4 show dynamicport tcp`；若动态端口范围明显不足以容纳10,000条连接及重连产生的 `TIME_WAIT`，应先停在5,000阶段观察，或换用多台负载机分片测试。

### Gather.TcpCenter 的5,000～10,000终端暴风连接测试

`gather-tcpcenter-storm-5000-10000.json` 使用相同的 `260810001`～`260820000` 测试设备，但连接模型改为 `burst`。场景先用100台探活并全部退出，然后立即提交5,000台连接；稳定5分钟后立即追加5,000台达到10,000台。阶段没有设置 `connectionsPerSecond` 时不会进行每秒限速，只由 `maxConcurrentConnecting=1000` 限制同时处于建连和登录过程的设备数。

10,000台稳定后，场景会主动断开全部连接，并以250毫秒起始、最大10秒的指数抖动退避触发集中重连；它会验收最终至少9,800台在线、至少10,000次重连，并要求最近一次掉线在120秒内恢复。该场景会明显冲击老服务的监听队列、Socket处理线程和登录确认链路，只应使用预留测试设备，并提前通知正式环境的运维和业务人员。

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-storm-5000-10000.json `
  --run-id gather-tcpcenter-storm-001
```

如果本机动态端口不足或需要把压力分散到多台负载机，保持相同 RunId 和计划启动时间，分别设置 `--shard-index` 与 `--shard-count`。分片后每台负载机只生成自己负责的设备连接，但各分片必须同时启动才能形成同一轮连接风暴。

### 10,000台设备上线并调用老读表 API

`gather-tcpcenter-reading-10000.json` 使用 `role=combined`：同一 LoadTester 进程从 CSV 读取10,000个真实集中器编号并维护 TCP 会话，达到98%在线且连续稳定30秒后，独立 HTTP 工作线程才开始调用 `api/ammeter/reading`。TCP 收包和 HTTP 请求没有共用工作队列；正式极限测试仍可把角色拆为 `devicesOnly` 与 `apiOnly`，部署到两台负载机并使用相同设备映射。`apiOnly` 无法直接读取另一进程的在线数，应在设备节点稳定后再启动，或通过 `commandWorkload.startDelaySeconds` 留出上线时间；两个角色分别生成报告，不应作为同构分片合并。

运行前必须完成以下修改，否则示例映射只含3行，工具会在建立任何 TCP 连接前以“设备映射不完整”退出：

1. 将 `commandWorkload.baseUrl` 改为正式老 `Gather.Control.Api` 的可达地址。
2. 从测试数据库导出真实映射 CSV，表头固定为 `meterId,deviceId`。`deviceId` 可写九位老集中器号（如 `260810001`），也可写规范值（如 `3761:2608-10001`）。一万台必须各有唯一 `meterId` 和 `deviceId`。
3. 把场景中的 `devices.deviceMappingFile` 改为该 CSV 文件名，并保证有效数据行数与 `devices.count` 完全一致。相对路径按场景 JSON 所在目录解析，方便 Windows/Linux 发布包使用相同结构。
4. 如果 API 校验 `gather-authorize-key`，运行进程前设置 `GATHER_AUTHORIZE_KEY` 环境变量；密钥只进入请求头，不会写入 JSON 场景或测试报告。若该环境不需要鉴权，可同时把两个鉴权配置字段设为空字符串。

同一份 CSV 同时决定 TCP 模拟设备身份和 HTTP 请求的 `meter_id`，因此 `commandWorkload.deviceMappingFile` 可以省略；只有希望 HTTP 使用另一份映射时才单独设置。CSV 数据行从零开始形成稳定全局序号，多机分片按该序号取模，同一行不会被两台负载机同时模拟。

CSV 示例：

```csv
meterId,deviceId
700001,260810001
700002,260810002
700003,3761:2608-10003
```

Windows PowerShell 运行示例：

```powershell
$env:GATHER_AUTHORIZE_KEY = '<正式环境分配的测试密钥>'
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-reading-10000.json `
  --run-id gather-reading-001
```

默认 HTTP 阶段按风险递增：100条同步结果冒烟、10,000台各一次异步入队、同步并发20、同步并发50、1,000台各5条异步排队、100台各5条同步排队。不要一开始把10,000条请求全部设为 `showResult=true`：老 API 每个同步请求会创建 Redis 订阅并最多等待60秒，极易先耗尽 IIS 请求线程或 Redis 连接，无法区分 TCP 服务本身的稳定性。

读表业务响应默认返回总电量和4个费率值，地址、MSA、PN、PSEQ 均取自下行请求。若要验证慢设备或坏设备是否阻塞其他终端，可复制场景并调整：

```json
"gatherReadingResponse": {
  "enabled": true,
  "mode": "delayed",
  "affectedDevicePercentage": 1,
  "delayMilliseconds": 5000,
  "rateCount": 4,
  "baseReadingKwh": 1000,
  "deviceStepKwh": 0.01
}
```

`mode` 支持 `normal`、`delayed`、`drop`、`businessError`、`duplicate`。`affectedDevicePercentage=1` 会用稳定设备序号散列选中约100台，所有分片结果可复现。故障响应应单独成轮执行，不要和首次万人连接风暴混在同一份结论里。

### Dump 回归 V2 综合场景

`gather-tcpcenter-dump-regression-10000-v2.json` 用于复现 CPU dump 暴露的登录/心跳同步处理、连接查找锁竞争和全断重连压力。它依次执行100台探活、5,000台稳定、10,000台稳定、全量断线和10,000台恢复，同时按100、1,000、5,000和10,000设备台阶执行老读表 API。场景文件在每个配置项附近给出了中文注释，可直接作为改造前后测试配置附件。

V2 场景不会再因一万台设备在600秒内未达到稳定在线而丢失报告。每个 HTTP 阶段拥有独立在线门槛：达到门槛就执行，超时默认标记为 `Skipped` 并继续 TCP 场景；最终 `RunStatus` 为 `CompletedWithSkippedPhases`，退出码仍为3，不能把“生成了报告”误解成“测试通过”。若确实需要旧版超时立即终止行为，可把阶段的 `onlineGateFailureAction` 设置为 `abortRun`，中止前仍会生成部分报告。

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-dump-regression-10000-v2.json `
  --run-id gather-dump-before-r1
```

改造后只替换 RunId，例如 `gather-dump-after-r1`，其余场景、设备 CSV、服务端配置和负载机必须保持不变。建议改造前后各运行三轮，正式对比使用中位数并保留最差一轮。

### 低 CPU 服务器：10,000 台连接、最多 1,000 次 HTTP 抄读

`gather-tcpcenter-dump-regression-10000-v2-http-1000.json` 保留 V2 的 10,000 台 TCP、心跳、全断和恢复流程，但整轮只有一个 HTTP 阶段：1,000 台设备各抄读一次，因此请求总量严格为 1,000。默认限速 5 请求/秒、最大并发 5，并使用 `showResult=false` 异步受理，降低测试服务器的 HTTP 线程和同步等待压力。

需要临时降到 100 或 500 次时，只修改该阶段的 `targetDeviceCount` 并保持 `requestsPerDevice=1`；需要调节提交速度时修改 `requestsPerSecond` 和 `maxConcurrency`。整轮请求预算计算公式为所有 HTTP 阶段的 `targetDeviceCount × requestsPerDevice` 之和，不要通过增加 `requestsPerDevice` 调高速度。

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  run --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-dump-regression-10000-v2-http-1000.json `
  --run-id gather-lowcpu-before-r1
```

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

完整字段、默认值、边界、组合约束和使用场景统一维护在 [LoadTester JSON 配置手册](docs/json-configuration.md)。下面仅保留高频配置摘要；新增或修改字段时必须同步更新配置手册。

- `target`：协议、服务端主机、端口和兼容配置。
- `role`：默认 `devicesOnly`；`combined` 同进程运行 TCP 设备和 HTTP 指令，`apiOnly` 只运行 HTTP 指令并由其他节点提供在线设备。
- `target.compatibilityProfile`：默认 `deviceSocket`，使用当前 Gateway 的严格确认规则；`gatherTcpCenter` 保持376.1帧格式不变，但接受 Gather 老服务固定 SEQ、无回显数据区的 F1/F3 确认。后者当前只允许与 `qGdw3761` 组合。
- `devices`：全局设备总数、设备地址生成模式、起止地址、376.1行政区划和真实设备 CSV。
- `devices.generationMode`：`sequential` 从 `addressStart` 连续递增；`random` 在 `addressStart`～`addressEnd` 闭区间内无重复抽取；`csv` 从 `devices.deviceMappingFile` 逐行读取真实376.1设备。默认 `sequential`。
- `devices.deviceMappingFile`：CSV 模式必填，格式固定为 `meterId,deviceId`；数据行数必须等于 `devices.count`，`meterId` 和规范化后的 `deviceId` 都不能重复。
- `devices.administrativeDivision`：376.1设备号前四位，必须填写数据库真实存在的固定行政区划；`addressStart` 对应后五位终端地址的起始值。
- `devices.spanAdministrativeDivisions`：376.1 设备超过单区划 65,535 个终端地址时，递增行政区划并从终端地址 1 继续；默认关闭，8 万场景显式开启。
- `connection`：Ramp/Burst、连接速率、并发度和超时。
- `heartbeat`：心跳间隔、抖动和确认超时。
- `workload`：额外周期上报和 `gatherReadingResponse` 读表业务响应/故障模型；376.1 常规在线维持仍使用协议心跳。
- `commandWorkload`：老读表 API 地址、鉴权环境变量名、真实电表映射、在线稳定门槛、请求超时和 HTTP 阶段。
- `commandWorkload.phases`：目标全局设备数、每设备命令数、所有分片合计 RPS/并发、`showResult`、阶段后排空等待和可选阶段在线门槛。工具会按 `shardCount` 自动分摊 RPS、并发和要求在线数。
- `commandWorkload.phases[].minimumOnlineCount`：阶段要求的全局在线设备数；空值继承 `requiredOnlineRate`，0 表示不等待。多机分片会按稳定全局序号换算成本地门槛。
- `commandWorkload.phases[].onlineStableSeconds`：阶段门槛需要连续保持的秒数；空值继承全局配置，0 表示达到数量后立即放行。
- `commandWorkload.phases[].onlineWaitTimeoutSeconds`：阶段最长等待秒数；空值继承全局配置，必须大于0。
- `commandWorkload.phases[].onlineGateFailureAction`：`skipPhase` 默认跳过并继续，`runPhase` 明确带风险向可能离线的设备发请求，`abortRun` 中止测试但仍生成部分报告。
- `reconnect`：重连方式、退避、抖动和最大次数；0 次上限表示无限重试。
- `scenario.phases`：阶段类型、持续时间、目标设备比例和阶段连接速率。
- `scenario.phases.faults`：时延、丢包、校验损坏、粘包、分片写入和断线波次。
- `devices.duplicateIdentityPercentage`：复用协议身份的连接比例，用于重复登录竞争。
- `serverObservation`：Gateway 状态 API 采样；启动前先记录基线，容量验收使用本轮新增认证设备峰值；不可用只记录采样失败，不阻断 TCP 负载。
- `acceptance`：自动验收门槛；省略或设为 `null` 表示不启用该规则。`minimumObservedAuthenticatedDevices` 和 `maximumObservedAuthenticatedDevices` 均指扣除测试前基线后的本轮新增认证设备峰值。
- `distributed`：运行编号、分片编号和计划启动时间。
- `reporting.checkpointIntervalSeconds`：异常恢复检查点覆盖周期，单位秒，默认10且不能小于采样周期；它不改变最终报告采样口径。

正式环境使用数据库已存在设备时，可按下面两种方式配置。376.1终端地址虽然显示为五位十进制文本，但协议字段为16位无符号数，因此有效范围为 `00001`～`65535`。

顺序模式会生成 `3301 + 12001`、`3301 + 12002`、`3301 + 12003`……，其中前四位固定，后五位从起始值递增：

```json
"devices": {
  "count": 5000,
  "generationMode": "sequential",
  "administrativeDivision": "3301",
  "addressStart": 12001,
  "spanAdministrativeDivisions": false
}
```

随机模式会从指定闭区间无重复选取设备；同一个 `distributed.runId` 在单机或多分片下得到相同设备集合，修改 RunId 后重新抽取。为保证登录设备确实存在，必须确保该区间内每一个“行政区划 + 终端地址”组合都已在数据库预置：

```json
"devices": {
  "count": 5000,
  "generationMode": "random",
  "administrativeDivision": "3301",
  "addressStart": 10001,
  "addressEnd": 20000,
  "spanAdministrativeDivisions": false
}
```

随机区间容量必须不少于唯一设备数；如果配置了 `duplicateIdentityPercentage`，容量按扣除重复登录连接后的唯一身份数校验。数据库地址存在断号时不要使用覆盖断号的随机区间，应改用多个连续区间分别测试，或使用顺序模式选择一段连续已存在地址。

设备编号不连续或必须精确选取数据库设备时使用 CSV 模式。`deviceId` 可写九位老编号、`2608-10001` 或 `3761:2608-10001`，加载后统一规范化；后五位终端地址必须在 `00001`～`65535`。CSV 模式不允许再设置重复身份比例：

```json
"devices": {
  "count": 10000,
  "generationMode": "csv",
  "deviceMappingFile": "gather-meter-device-map.csv"
}
```

工具会在建立任何 TCP 连接前读取完整文件，并拒绝无效格式、重复主键、重复设备号以及行数不等于 `devices.count` 的配置。多机运行时每台机器必须部署内容一致、文件名一致的 CSV。

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
├─ run-checkpoint.json
├─ node-summary.json
├─ errors.csv
└─ report.md
```

`run-checkpoint.json` 每隔 `checkpointIntervalSeconds` 先写临时文件再原子替换，记录最后一次完整累计指标和负载机资源。如果进程被强制结束、机器断电或无法进入最终报告收尾，可使用它和 `node-timeseries.jsonl` 定位最后状态；正常超时、阶段跳过、运行异常和 Ctrl+C 会直接生成带运行状态与原因的 `node-summary.json`、`errors.csv` 和 `report.md`。

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
