# LoadTester JSON 配置手册

本文档是 `DeviceSocket.LoadTester` 场景 JSON 的字段基准说明。目标是让第一次使用的人能够快速选场景、修改关键字段并理解测试结果，也作为以后新增或修改配置字段时必须同步维护的清单。

## 快速开始

1. 从 [Scenarios](../Scenarios) 目录选择最接近目标的场景并复制一份，不建议从空 JSON 开始编写。
2. 至少核对 `role`、`target`、`devices`、`scenario` 和 `acceptance`。
3. 测试 Gather 老服务 HTTP 下发时，再核对 `commandWorkload`、真实设备 CSV 和鉴权环境变量。
4. 正式压测使用新的 `distributed.runId`，改造前、改造后除 RunId 外应保持同一份场景配置。
5. 先用 10～100 台验证地址、端口、登录和报告，再扩大到 5,000～10,000 台。

运行示例：

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj `
  -c Release -- run `
  --config tools\DeviceSocket.LoadTester\Scenarios\gather-tcpcenter-connections-10000.json `
  --run-id gather-connections-001
```

常用命令行覆盖参数：

| 参数 | 意义 | 使用场景 |
| --- | --- | --- |
| `--run-id` | 覆盖 `distributed.runId` | 每次正式测试设置唯一编号；多机运行必须相同 |
| `--shard-index` | 覆盖 `distributed.shardIndex` | 多机运行时为每台负载机分配不同的零基序号 |
| `--shard-count` | 覆盖 `distributed.shardCount` | 指定共同分担设备空间的负载机总数 |
| `--start-at-utc` | 覆盖 `distributed.startAtUtc` | 让多台负载机在同一个 UTC 时刻启动 |
| `--output` | 覆盖 `reporting.outputDirectory` | 临时改变报告根目录而不修改场景文件 |

进程退出码：`0` 表示运行和验收均通过，`1` 表示执行异常，`2` 表示命令或配置错误，`3` 表示已完成但验收失败或存在跳过阶段，`130` 表示测试被取消。生成报告不等于测试通过，应同时查看 `runStatus` 和 `acceptance`。

## JSON 语法约定

- 字段名不区分大小写，文档和示例统一使用 camelCase。
- 枚举使用 camelCase 字符串，例如 `qGdw3761`、`gatherTcpCenter`、`skipPhase`。
- 支持 `//` 和 `/* ... */` 注释，也允许最后一个字段带逗号。
- 不认识的字段会直接报错，避免拼写错误被静默忽略。
- CSV 相对路径以场景 JSON 所在目录为基准，不以命令执行目录为基准。
- 比例字段分两类：名称以 `Rate` 结尾的一般使用 `0～1`；名称以 `Percentage` 结尾的使用 `0～100`。
- 所有分片共享的速率、并发、设备数均填写全局值，工具再按 `distributed.shardCount` 分摊。

## 最小 TCP 场景示例

```jsonc
{
  "role": "devicesOnly",
  "target": {
    "host": "192.168.111.18",
    "port": 6666,
    "protocol": "qGdw3761",
    "compatibilityProfile": "gatherTcpCenter"
  },
  "devices": {
    "count": 100,
    "generationMode": "sequential",
    "administrativeDivision": "2608",
    "addressStart": 10001
  },
  "scenario": {
    "name": "gather-smoke-100",
    "durationSeconds": 120
  },
  "acceptance": {
    "minimumConnectionSuccessRate": 0.95,
    "minimumLoginSuccessRate": 0.95,
    "minimumFinalOnlineRate": 0.95,
    "maximumProtocolErrorRate": 0.01
  },
  "distributed": {
    "runId": "gather-smoke-001",
    "shardIndex": 0,
    "shardCount": 1
  }
}
```

## 顶层运行角色：`role`

| 类型 | 默认值 | 可选值与意义 | 使用场景 |
| --- | --- | --- | --- |
| 枚举 | `devicesOnly` | `devicesOnly`：只维护 TCP 虚拟设备；`apiOnly`：只调用 HTTP API，设备由其他进程维护；`combined`：同一进程同时维护 TCP 和调用 HTTP | 一般连接压测使用 `devicesOnly`；需要端到端命令应答使用 `combined`；负载机资源不足或需隔离 HTTP 压力时使用 `apiOnly` |

`apiOnly` 和 `combined` 必须启用 `commandWorkload.enabled`。`apiOnly` 无法读取另一进程的实时在线数，通常配合 `commandWorkload.startDelaySeconds` 使用。

## TCP 目标：`target`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `target.host` | string | `127.0.0.1` | TCP 服务主机名或 IP，不能为空 | 本地调试填回环地址；内网/外网负载机填写其实际可达地址 |
| `target.port` | int | `0` | 有效端口为 1～65535；省略或填 0 时，376.1 自动使用 5001，645 自动使用 5002 | 连接 Gather 老服务时必须按其 `App.config` 填实际监听端口 |
| `target.protocol` | enum | `qGdw3761` | `qGdw3761`：Q/GDW 376.1；`dlt6452007`：DL/T 645-2007 | 集中器场景用 376.1，电表直连场景用 645 |
| `target.compatibilityProfile` | enum | `deviceSocket` | `deviceSocket` 使用当前 Gateway 的严格确认规则；`gatherTcpCenter` 接受 Gather 老服务的固定 SEQ、无回显数据区确认 | 老项目必须设置 `gatherTcpCenter`；该值目前只能与 `qGdw3761` 组合 |

## 设备身份：`devices`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `devices.count` | int | `100` | 所有分片合计的虚拟连接数，必须大于 0 | 多机时仍填写总设备数，不填单机设备数 |
| `devices.generationMode` | enum | `sequential` | `sequential` 连续生成；`random` 在闭区间无重复抽取；`csv` 读取真实映射 | 数据库设备连续用 sequential；随机抽样用 random；正式老服务优先用 csv |
| `devices.deviceMappingFile` | string/null | `null` | CSV 模式必填；格式为 `meterId,deviceId`，数据行数必须等于 `devices.count`，两列均不得重复 | 使用数据库已存在的真实设备和电表映射 |
| `devices.addressStart` | long | `1` | sequential 的首地址，random 的闭区间下界；必须大于 0。376.1 单区划最大 65535，645 最大 12 位十进制地址 | 376.1 设备号后五位的起始值，或 645 表地址起始值 |
| `devices.addressEnd` | long/null | `null` | random 模式必填的闭区间上界，必须不小于 `addressStart` 且不超过 65535；其他模式忽略 | 从一段已预置设备地址中稳定随机抽样 |
| `devices.administrativeDivision` | string | `3301` | 376.1 设备号前四位，必须是四位十进制数字 | 必须填写数据库真实存在的行政区划码，例如 `2608` |
| `devices.spanAdministrativeDivisions` | bool | `false` | false 时终端地址不能跨过 65535；true 时地址耗尽后区划码递增并从地址 1 继续。random 不允许开启 | 超过单区划 65,535 台的超大 sequential 场景 |
| `devices.duplicateIdentityPercentage` | double | `0` | 0～100，按比例让额外连接复用已有协议身份；CSV 模式必须为 0 | 测试重复登录、Session 替换和旧连接清理，不用于常规容量基线 |

CSV 示例：

```csv
meterId,deviceId
100001,260810001
100002,260810002
```

`deviceId` 是四位行政区划码加五位终端地址。CSV 会在任何连接发起前完整校验；多机运行时每台负载机必须部署内容和文件名一致的 CSV。

## 建连行为：`connection`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `connection.startMode` | enum | `ramp` | `ramp` 按速率平滑建连；`burst` 在并发闸门内尽快发起 | 日常容量和真实服务重启恢复优先使用 ramp；只验证无速率上限的客户端突发时才用 burst |
| `connection.connectionsPerSecond` | int | `100` | 所有分片合计连接数/秒；ramp 时必须大于 0。阶段可覆盖该值 | 控制服务端接受连接和登录的增长斜率 |
| `connection.maxConcurrentConnecting` | int | `200` | 单个负载进程同时处于建连或登录过程的最大设备数，必须大于 0 | 防止负载机瞬间创建过多套接字任务；burst 的主要保护闸门 |
| `connection.connectTimeoutSeconds` | int | `5` | 单次 TCP 建连超时秒数，必须大于 0 | 内网一般 3～5 秒；跨公网可适当放大 |
| `connection.loginTimeoutSeconds` | int | `5` | 登录帧发送后等待确认的超时秒数，必须大于 0 | 服务端登录处理慢时用于区分连接成功和协议上线失败 |
| `connection.writeTimeoutSeconds` | int | `5` | 单次写入及半包分片写入总超时秒数，必须大于 0 | 检测服务端长期不读取导致的发送阻塞 |
| `connection.noDelay` | bool | `true` | true 禁用 Nagle，降低小报文等待；false 允许 TCP 合并小包 | 终端协议通常保持 true；研究包数和吞吐时才改为 false |

## 心跳：`heartbeat`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `heartbeat.enabled` | bool | `true` | 登录后是否周期发送协议心跳 | 长连接在线测试保持 true；只测试空闲连接时可关闭 |
| `heartbeat.intervalSeconds` | int | `60` | 心跳周期秒数，启用时必须大于 0 | 应尽量接近真实终端周期，过小会把测试变成报文吞吐测试 |
| `heartbeat.jitterSeconds` | int | `5` | 0～小于心跳周期，用于按设备打散发送时刻；0 表示不打散 | 真实稳态用抖动；心跳风暴场景可设 0 |
| `heartbeat.responseTimeoutSeconds` | int | `5` | 等待心跳确认的秒数，必须大于 0；超时进入断线重连 | 测试服务端卡顿和链路恢复能力 |

## 设备业务上报：`workload`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `workload.reportsEnabled` | bool | `false` | 是否发送协议专属的额外周期业务上报，不影响协议心跳 | 测试服务端上行业务吞吐时开启 |
| `workload.reportIntervalSeconds` | int | `300` | 上报周期秒数，开启上报时必须大于 0 | 按真实设备采集周期配置 |
| `workload.jitterSeconds` | int | `10` | 0～小于上报周期，用于打散设备上报；0 表示同时上报 | 稳态用抖动，上报风暴用 0 |
| `workload.gatherReadingResponse` | object | 默认对象 | Gather 老服务读表命令的终端应答模型 | 调用 `api/ammeter/reading` 做端到端测试时使用 |

### Gather 读表应答：`workload.gatherReadingResponse`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `workload.gatherReadingResponse.enabled` | bool | `true` | 是否识别 AFN=0CH/F129 并返回读表业务帧 | 只观察下行而不应答时关闭 |
| `workload.gatherReadingResponse.mode` | enum | `normal` | `normal` 正常应答；`delayed` 延迟后正常应答；`drop` 不应答；`businessError` 返回业务失败；`duplicate` 返回两次 | 验证慢设备隔离、超时、失败处理和重复应答幂等；非 normal 模式仅支持 Gather 兼容配置 |
| `workload.gatherReadingResponse.affectedDevicePercentage` | double | `100` | 0～100，只有命中的设备应用所选异常模式；0 表示都按正常设备处理 | 在一万台中混入少量慢设备或异常设备 |
| `workload.gatherReadingResponse.delayMilliseconds` | int | `1000` | delayed 模式的额外等待毫秒数，不能小于 0；0 表示立即应答 | 构造 1 秒、10 秒或接近 API 超时的慢设备 |
| `workload.gatherReadingResponse.rateCount` | int | `4` | 返回读表帧携带的费率示值数量，允许 0～8 | 模拟不同费率档案的电表数据 |
| `workload.gatherReadingResponse.baseReadingKwh` | decimal | `1000` | 第一台设备的基础电能示值，范围 0～999999.9999 kWh | 生成可识别且有效的测试读数 |
| `workload.gatherReadingResponse.deviceStepKwh` | decimal | `0.01` | 每增加一个全局设备序号时读数增加的 kWh，不能小于 0 | 避免一万台终端返回完全相同的数据 |

## HTTP 指令负载：`commandWorkload`

该部分当前只支持 `qGdw3761 + gatherTcpCenter`，调用老服务 `api/ammeter/reading`。TCP 收包循环与 HTTP 请求使用独立任务和连接池。

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `commandWorkload.enabled` | bool | `false` | 是否执行 HTTP 指令阶段；`apiOnly`/`combined` 必须为 true | 只测试连接时关闭，测试服务端下发时开启 |
| `commandWorkload.baseUrl` | string | 空 | Gather.Control.Api 的绝对 HTTP/HTTPS 根地址 | 例如 `http://192.168.111.18:9011`，不是 TCP 地址 |
| `commandWorkload.targetDeviceSource` | enum | `fixedOrdinal` | `fixedOrdinal` 按 GlobalOrdinal 固定截取 HTTP 目标设备；`onlineRegistry` 从本进程 TCP 会话的全部在线设备快照中按阶段数量选取目标，只允许与 `combined` 角色组合 | 需要保证 HTTP 只请求当前真正在线的设备时使用 `onlineRegistry`；`apiOnly` 保持 `fixedOrdinal` |
| `commandWorkload.endpointPath` | string | `/api/ammeter/reading` | POST 接口相对路径，不能为空 | 老服务路由有差异时覆盖 |
| `commandWorkload.authorizationHeaderName` | string | `gather-authorize-key` | 鉴权请求头名称；与环境变量名任一为空时不发送鉴权头 | 正式环境通常保持默认名称 |
| `commandWorkload.authorizationKeyEnvironmentVariable` | string | `GATHER_AUTHORIZE_KEY` | 保存鉴权值的环境变量名；配置了头名和变量名但环境变量缺失时终止并出中止报告 | 密钥不写入 JSON、控制台或报告 |
| `commandWorkload.deviceMappingFile` | string/null | `null` | HTTP 使用的 `meterId,deviceId` CSV；为空且设备为 CSV 模式时复用 `devices.deviceMappingFile` | TCP 和 HTTP 使用不同映射时才单独设置 |
| `commandWorkload.meterIdStart` | int/null | `null` | 不使用 CSV 时，从该 meter_id 按全局设备序号递增，必须大于 0 | 数据库 meter_id 连续且与生成设备一一对应时使用 |
| `commandWorkload.operatorUserId` | int | `0` | 请求体 `oper_uid`，作为操作人审计标识 | 按正式 API 的审计要求填写 |
| `commandWorkload.operationSource` | int | `6` | 请求体 `oper_source`，默认对应老项目 Test 来源 | 除非老服务枚举定义不同，否则保持 6 |
| `commandWorkload.readingModel` | int | `0` | 请求体 `reading_model`，默认普通 Q/GDW 376.1 抄读 | 测试其他抄读模式时按老 API 枚举填写 |
| `commandWorkload.readingCode` | int | `0` | 请求体 `readingCode`，原样透传给老 API | 需要指定读数项时按业务约定填写 |
| `commandWorkload.requestTimeoutSeconds` | int | `70` | 单次 HTTP 端到端超时秒数，必须大于 0 | `showResult=true` 时应大于老 API 约 60 秒的设备结果等待上限 |
| `commandWorkload.requiredOnlineRate` | double | `0.98` | 0～1 且大于 0；阶段未配置明确在线数时，按 `targetDeviceCount × requiredOnlineRate` 计算在线门槛；`onlineRegistry` 可由任意当前在线设备满足阶段配额 | 普通全量阶段要求 98% 在线再发命令 |
| `commandWorkload.onlineStableSeconds` | int | `30` | 在线门槛连续保持秒数，不能小于 0；0 表示达到即放行 | 避免刚上线又掉线时提前开始 API 压测 |
| `commandWorkload.onlineWaitTimeoutSeconds` | int | `600` | 等待稳定在线的最长秒数，必须大于 0 | 一万台上线较慢时可放大；超时动作由阶段决定 |
| `commandWorkload.startDelaySeconds` | int | `0` | HTTP 工作负载启动前额外等待秒数，不能小于 0 | `apiOnly` 部署时等待另一台设备负载机先上线 |
| `commandWorkload.phases` | array | `[]` | 按顺序执行的 HTTP 阶段；启用时至少一个 | 从小流量功能验证逐步扩展到全量和阻塞场景 |

有效设备映射与 `meterIdStart` 必须二选一。CSV 设备模式会自动把 `devices.deviceMappingFile` 当作有效 HTTP 映射，因此这时不要再设置 `meterIdStart`。

`targetDeviceSource=onlineRegistry` 时，工具先等待本节点在线设备达到阶段配额，再对注册表做一次快照，并从全部当前在线设备中按 GlobalOrdinal 稳定排序选出 `targetDeviceCount` 对应的本地数量；因此固定序号靠前的设备离线时，会由其他在线设备补足。进入队列前和真正发送 HTTP 前还会各核对一次在线状态，已离线请求以及快照时未补足的计划请求都会计入 `ApiRequestsSkippedOffline`，不会调用离线集中器对应的 API。

### HTTP 阶段：`commandWorkload.phases[]`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `commandWorkload.phases[].name` | string | 空 | 阶段名称，不能为空，写入控制台和报告 | 使用 `functional-100`、`all-10000` 等可识别名称 |
| `commandWorkload.phases[].targetDeviceCount` | int | `0` | 参与阶段的全局设备数，0 表示全部，不能超过 `devices.count`；`onlineRegistry` 下表示从当前在线快照中选取的数量，不限定固定序号前缀 | 先 100、1000、5000 再扩至 10000 |
| `commandWorkload.phases[].requestsPerDevice` | int | `1` | 每台目标设备发送的命令数，必须大于 0 | 大于 1 用于验证同设备连续命令是否排队阻塞 |
| `commandWorkload.phases[].requestsPerSecond` | int | `10` | 所有分片合计请求数/秒，必须大于 0 | 控制业务 API 吞吐，逐级寻找容量拐点 |
| `commandWorkload.phases[].maxConcurrency` | int | `10` | 所有分片合计最大在途 HTTP 请求数，必须大于 0 | `showResult=true` 时限制同步等待请求占用 |
| `commandWorkload.phases[].showResult` | bool | `false` | false 表示 API 快速受理；true 表示 API 同步等待设备业务结果 | false 测下发吞吐，true 测端到端应答和阻塞风险 |
| `commandWorkload.phases[].pauseAfterSeconds` | int | `0` | 阶段结束后的等待秒数，不能小于 0；0 表示立即进入下一阶段 | 留出命令队列和服务端资源回落时间 |
| `commandWorkload.phases[].minimumOnlineCount` | int/null | `null` | 阶段要求的全局在线数；null 按 `requiredOnlineRate` 计算，0 表示不等待，不能大于该阶段有效 `targetDeviceCount`。`onlineRegistry` 下任意在线设备均可满足门槛 | 小规模阶段设精确门槛，全量阶段可设 9800 |
| `commandWorkload.phases[].onlineStableSeconds` | int/null | `null` | null 继承全局稳定时长，0 表示达到在线数立即放行 | 为功能冒烟设置较短稳定时间，为全量阶段设置较长时间 |
| `commandWorkload.phases[].onlineWaitTimeoutSeconds` | int/null | `null` | null 继承全局等待上限；设置后必须大于 0 | 不同规模使用不同上线等待时间 |
| `commandWorkload.phases[].onlineGateFailureAction` | enum | `skipPhase` | `skipPhase` 跳过并继续出报告；`runPhase` 在线不足仍执行；`abortRun` 终止本轮并出部分报告 | 正式回归推荐 skipPhase；诊断离线请求才用 runPhase；严格前置条件用 abortRun |

## 自动重连：`reconnect`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `reconnect.mode` | enum | `exponentialJitter` | `immediate` 立即；`fixed` 固定延迟；`exponentialJitter` 指数退避加抖动 | 服务重启风暴用 immediate；真实终端稳定性测试用 exponentialJitter |
| `reconnect.initialDelayMilliseconds` | int | `500` | fixed 的固定延迟，也是指数退避基数；不能小于 0，0 表示不等待 | 控制第一次重连冲击 |
| `reconnect.maximumDelaySeconds` | int | `30` | 指数退避硬上限秒数，不能小于 0 | 防止连续失败后等待无限增长 |
| `reconnect.multiplier` | double | `2` | 指数增长倍数，必须不小于 1 | 2 表示 0.5、1、2、4 秒逐步退避 |
| `reconnect.jitterRatio` | double | `0.2` | 0～1 的随机抖动比例；0 表示所有设备重试时刻一致 | 真实场景保持抖动；制造整齐重连风暴时设 0 |
| `reconnect.maximumAttempts` | int | `0` | 最大连续重连次数，不能小于 0；0 表示无限重试 | 长稳测试通常为 0，失败快速结束的功能测试可设正数 |

## TCP 场景：`scenario`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `scenario.name` | string | `smoke` | 场景名称，不能为空，进入报告 | 应包含被测目的和规模，便于改造前后对比 |
| `scenario.durationSeconds` | int | `60` | `scenario.phases` 为空时的单一全量稳态时长，必须大于 0 | 简单冒烟和短稳态场景 |
| `scenario.phases` | array | `[]` | 按顺序执行的设备规模和故障阶段；非空时忽略顶层 duration | 容量爬升、暴风断线、恢复和冷却的综合场景 |

### TCP 阶段：`scenario.phases[]`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `scenario.phases[].name` | string | `steady` | 阶段名称，不能为空 | 使用 `ramp-5000`、`restart-storm` 等名称 |
| `scenario.phases[].kind` | enum | `steady` | `warmup`、`ramp`、`steady`、`fault`、`recovery`、`cooldown`；主要用于报告分类，不自动改变行为 | 按阶段业务目的选择，具体行为仍由其他字段决定 |
| `scenario.phases[].durationSeconds` | int | `60` | 阶段保持时间秒数，必须大于 0 | 稳态阶段通常比爬升阶段更长 |
| `scenario.phases[].targetDevicePercentage` | double | `100` | 0～100，按全局设备数计算本阶段目标在线设备；0 会缩容并停止所有设备 | 50 表示保持一半规模，100 表示全部设备 |
| `scenario.phases[].connectionsPerSecond` | int/null | `null` | null 继承 `connection.connectionsPerSecond`；设置后必须大于 0 | 为不同爬升阶段配置不同速率 |
| `scenario.phases[].faults` | object | 全部为 0 | 本阶段故障快照；进入下一阶段时被下一阶段配置覆盖 | 只在 fault 阶段设置非零值，恢复阶段恢复为零 |

### 故障注入：`scenario.phases[].faults`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `scenario.phases[].faults.networkLatencyMilliseconds` | int | `0` | 每次上行发送前增加的毫秒时延，不能小于 0 | 模拟慢网络和高延迟终端 |
| `scenario.phases[].faults.outboundPacketLossRate` | double | `0` | 0～1，上行整帧丢弃概率 | 模拟终端上报丢失和心跳黑洞 |
| `scenario.phases[].faults.inboundPacketLossRate` | double | `0` | 0～1，下行整帧忽略概率 | 模拟设备收不到服务端命令 |
| `scenario.phases[].faults.corruptChecksumRate` | double | `0` | 0～1，破坏上行帧校验字节的概率 | 验证非法帧处理和协议解析稳定性 |
| `scenario.phases[].faults.duplicateFrameRate` | double | `0` | 0～1，重复发送同一上行帧的概率 | 验证粘包、重复帧和业务幂等 |
| `scenario.phases[].faults.fragmentSizeBytes` | int | `0` | 大于 0 时按该字节数拆分写入；0 表示完整发送 | 制造 TCP 半包 |
| `scenario.phases[].faults.fragmentDelayMilliseconds` | int | `0` | 相邻半包片段之间等待的毫秒数，不能小于 0 | 扩大半包停留窗口，测试拆包缓存 |
| `scenario.phases[].faults.disconnectPercentage` | double | `0` | 0～100，阶段开始后由客户端主动断开的当前设备比例 | 100 可做自动化全断重连，但不包含服务端进程冷启动和内部状态恢复，不能替代真实服务重启验收 |
| `scenario.phases[].faults.disconnectBatchSize` | int | `0` | 每批主动断线设备数；0 表示一次性断开全部目标 | 一次性暴风或分批断线 |
| `scenario.phases[].faults.disconnectIntervalMilliseconds` | int | `0` | 相邻断线批次间隔毫秒数；0 表示不等待 | 控制断线波次密度 |

## 报告：`reporting`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `reporting.outputDirectory` | string | `load-test-results` | 报告根目录，不能为空；其下按 RunId 和分片创建目录 | 多次对比时保持同一根目录，用 RunId 区分 |
| `reporting.sampleIntervalSeconds` | int | `1` | 时间序列采样周期秒数，必须大于 0 | 1 秒适合问题定位；超长测试可调大以减少磁盘写入 |
| `reporting.checkpointIntervalSeconds` | int | `10` | 原子覆盖 `run-checkpoint.json` 的周期，必须大于 0 且不小于采样周期 | 进程被强制结束时保留最后累计指标 |
| `reporting.writeConsoleSummary` | bool | `true` | 是否每次采样输出控制台摘要，不影响文件报告 | 后台部署或减少控制台开销时关闭 |

## 服务端观测：`serverObservation`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `serverObservation.enabled` | bool | `false` | 是否轮询 Gateway 状态 API；失败只记录不可用采样，不中断负载 | 当前 DeviceSocket Gateway 容量测试可开启；无该接口的 Gather 老服务关闭 |
| `serverObservation.statusUrl` | string | `http://127.0.0.1:5080/api/status/connections` | 绝对 HTTP/HTTPS 状态地址 | 多机/外网部署时改为负载机可达地址 |
| `serverObservation.timeoutSeconds` | int | `2` | 单次采样超时秒数，启用时必须大于 0 | 状态接口应快速返回，避免观测影响负载 |

启用后工具在任何设备连接前记录基线，报告中的认证设备门槛使用“本轮峰值减测试前基线”，不是服务端绝对总数。

## 自动验收：`acceptance`

所有可空验收字段设置为 `null` 都表示关闭该规则。比例使用 0～1；数量使用所有分片合计值。默认只启用最终在线率和非法协议帧率两项。

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `acceptance.minimumConnectionSuccessRate` | double/null | `null` | TCP 建连成功次数/尝试次数的下限 | 容量爬升和网络质量验收 |
| `acceptance.minimumLoginSuccessRate` | double/null | `null` | 协议登录成功次数/登录尝试次数的下限 | 区分仅连上端口与真正设备上线 |
| `acceptance.minimumFinalOnlineRate` | double/null | `0.95` | 结束测量时在线设备数/期望在线数的下限 | 常规稳定性测试建议 0.95～0.99 |
| `acceptance.maximumProtocolErrorRate` | double/null | `0.01` | 非法帧数/接收帧数的上限 | 检测拆包、协议兼容和校验异常 |
| `acceptance.maximumRecoverySeconds` | double/null | `null` | 故障结束后恢复目标在线规模的最长秒数，设置后必须大于 0 | 服务重启和断线风暴恢复测试 |
| `acceptance.minimumFinalOnlineCount` | int/null | `null` | 最终在线设备数下限，不能小于 0 | 需要精确要求 9800 或 10000 台时使用 |
| `acceptance.maximumFinalOnlineCount` | int/null | `null` | 最终在线设备数上限，不能小于 0且不能小于最小值 | 验证缩容阶段只保留预期设备数 |
| `acceptance.minimumReconnects` | long/null | `null` | 所有分片合计的累计自动重连次数下限，不能小于 0；节点退出码按全局序号分摊本地配额，汇总报告按全局值重新验收 | 确认断线故障确实触发重连链路 |
| `acceptance.minimumObservedAuthenticatedDevices` | int/null | `null` | 服务端观测到的本轮新增认证设备峰值下限 | 校验客户端认为在线与服务端认证数一致；必须启用 serverObservation |
| `acceptance.maximumObservedAuthenticatedDevices` | int/null | `null` | 服务端观测到的本轮新增认证设备峰值上限 | 限制重复 Session 或连接泄漏；必须启用 serverObservation |
| `acceptance.minimumApiSuccessRate` | double/null | `null` | HTTP 状态和响应体 `success=true` 的业务成功率下限 | HTTP 指令负载验收 |
| `acceptance.minimumApiTargetCoverageRate` | double/null | `null` | 取值 0～1；实际发起 HTTP 请求数除以“已发起 + 因设备离线跳过”的计划请求数，空值不验收 | 防止只执行少量在线目标时 `minimumApiSuccessRate` 仍显示很高 |
| `acceptance.minimumGatherReadingResponseRate` | double/null | `null` | 模拟终端已应答读表命令数/收到读表命令数的下限 | 端到端设备消息应答验收 |
| `acceptance.maximumApiP95Milliseconds` | long/null | `null` | HTTP 端到端延迟 P95 上限毫秒数，设置后必须大于 0 | 检测同步等待、队列阻塞和服务端卡顿 |

如果同时配置最小和最大数量，最小值不能大于最大值。故障注入场景要根据预期行为调整门槛，例如 `drop` 模式不应再要求 100% 读表应答率。

## 分布式运行：`distributed`

| 字段 | 类型 | 默认值 | 意义与约束 | 使用场景 |
| --- | --- | --- | --- | --- |
| `distributed.runId` | string | 空 | 单机空值会自动生成；多机必须显式设置相同值，最长 100 字符 | 报告目录、随机设备种子和多节点合并身份；改造前后应使用不同 RunId |
| `distributed.shardIndex` | int | `0` | 当前节点零基分片编号，范围 0～`shardCount-1` | 两台负载机分别使用 0、1 |
| `distributed.shardCount` | int | `1` | 分片总数，必须大于 0 且不能大于设备总数 | 决定设备、HTTP RPS、并发和在线门槛的分摊 |
| `distributed.startAtUtc` | ISO-8601/null | `null` | 多节点统一 UTC 启动时间；null 立即启动 | 多机正式对比测试减少启动时间偏差 |

多机报告合并要求 RunId、分片总数、目标、设备空间、流量和验收配置一致。`shardIndex`、输出目录和机器名允许不同。

扩展到 30,000～50,000 台时，每台负载机必须部署内容一致的完整 CSV，不要手工拆分文件。CSV 数据行号是稳定全局序号，工具按 `GlobalOrdinal % shardCount` 自动分配设备；`devices.count`、TCP 建连速率、HTTP RPS、HTTP 并发和 HTTP 目标数继续填写所有分片合计值。只有 `connection.maxConcurrentConnecting` 是单个负载进程的本地上限。多节点启动方式和端口容量注意事项见项目 [LoadTester 使用说明](../README.md)。

## 场景选型

| 场景文件 | 适用目的 |
| --- | --- |
| `gather-tcpcenter-connections-10000.json` | 一万台纯设备建连、登录追平、心跳和 30 分钟容量基线 |
| `gather-tcpcenter-reconnect-storm-10000.json` | 一万台稳定在线后真实重启 Gather.TcpCenter，验证全量重连恢复 |
| `gather-tcpcenter-http-reading-10000.json` | 面向 8 核 16 GiB 测试服务器的一万台在线、HTTP 抄读、终端应答和多命令阻塞测试 |
| `gather-tcpcenter-soak-10000.json` | 一万台六小时长稳；复制并替换完整 CSV 后可扩展到 3～5 万多分片 |

## 新增或修改字段时的维护清单

配置代码变更不能只修改 Options 类。提交前必须同时完成：

1. 更新强类型配置属性的中文 XML 注释，写明完整路径、类型/单位、默认值、null/零值语义和影响。
2. 更新本文件对应字段表、枚举值、组合约束和使用场景。
3. 更新 `LoadTestOptionsValidator` 的边界校验和中文错误信息。
4. 更新至少一个场景 JSON 或示例，并在 JSON 支持的地方就近添加中文注释。
5. 如果字段影响报告、分片一致性或验收，更新报告模型、配置签名和相关测试。
6. 运行 `DeviceSocket.LoadTester.Tests`；其中配置文档覆盖测试会检查每个可序列化配置属性是否在本文件登记。

JSON 场景中的注释用于解释该场景为什么这样取值，不能替代本文件的通用字段说明。
