# LoadTester 离线详细报告使用手册

## 1. 用途与边界

`report generate` 用于在压测结束后联合分析以下三类文件：

- LoadTester 的 `node-summary.json` 与 `node-timeseries.jsonl`；
- 可选的 Gather.TcpCenter 服务指标 JSONL，例如 `20260821.jsonl`。
- 可选的测试背景 JSON，用于补充服务器规格、服务/代码版本、场景说明和关键运行参数。

命令只读取既有结果，不会重新建立 TCP 连接、发送设备报文或调用 HTTP API，也不会覆盖源报告。它把同一份事实模型分别渲染给两类受众：

| 受众 | 主要文件 | 适合场景 | 内容侧重 |
| --- | --- | --- | --- |
| 验收汇报 | `acceptance-report.html`、`acceptance-report.md` | 项目验收、改造收益汇报、管理评审 | 结论与验收规则、核心指标、报表视图和基线变化；不展示问题分析或排查建议 |
| 排查分析 | `diagnostic-report.html`、`diagnostic-report.md` | 开发定位、运维复盘、性能优化 | 全部证据、置信度、影响、建议、连接/登录/Pipeline/HTTP 明细 |
| 机器处理 | `analysis.json`、`phase-summary.csv` | 自动归档、二次脚本、Excel 分析 | 统一结构化事实和 HTTP 阶段数据 |

## 2. 最快上手

从仓库运行 PowerShell 包装脚本：

```powershell
.\tools\DeviceSocket.LoadTester\scripts\New-GatherLoadTestReport.ps1 `
  -ResultPath .\load-test-results\gather-http-after-r1\shard-000 `
  -ServerMetricsPath .\load-test-results\gather-http-after-r1\shard-000\20260821.jsonl `
  -BackgroundPath .\tools\DeviceSocket.LoadTester\docs\report-background.example.json `
  -Audience Both `
  -ServerTimeZone "China Standard Time" `
  -Title "Gather.TcpCenter 优化后 10000 台综合压测" `
  -Environment "公司测试环境" `
  -ServiceVersion "优化后代码" `
  -Tester "测试人员"
```

也可以直接调用 .NET 命令：

```powershell
dotnet run --project tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj -- `
  report generate `
  --input load-test-results\gather-http-after-r1\shard-000 `
  --server-metrics load-test-results\gather-http-after-r1\shard-000\20260821.jsonl `
  --background tools\DeviceSocket.LoadTester\docs\report-background.example.json `
  --audience both `
  --server-time-zone "China Standard Time"
```

发布包中 `scripts\New-GatherLoadTestReport.ps1` 会优先调用发布目录内的 `DeviceSocket.LoadTester.dll`，不需要源码工程。

## 3. 输入目录规则

`--input`/`-ResultPath` 支持两种结构：

1. 单个分片目录：目录中直接包含 `node-summary.json`；
2. 一轮测试根目录：下级一个或多个 `shard-xxx` 目录中包含 `node-summary.json`。

多分片时，分析器按时间使用各节点最后一个已知值聚合在线数和累计计数，避免简单按采样行号相加造成时间错位。`node-timeseries.jsonl` 缺失时仍可使用最终摘要，但趋势和峰值精度会下降，并在报告中给出数据质量警告。

合并版诊断报告应直接把一轮测试根目录作为输入，不需要先对每个 `shard-xxx` 分别生成诊断报告。存在阶段漂移时，服务端有效诊断窗口会持续到最后一个分片进入 `cooldown`；这样某个负载节点卡顿期间，其余节点先缩容所造成的混合断连、重连和 Pipeline 压力仍会保留在诊断结论中。

服务指标建议显式通过 `--server-metrics` 指定。未指定时，工具会在输入目录中寻找同时包含 `HandlerName`、`SocketConnections` 和 `BusinessOnlineDevices` 的 JSONL；不会把 `node-timeseries.jsonl` 识别成服务指标。

## 4. 参数说明

| .NET 参数 | PowerShell 参数 | 默认值 | 意义与使用场景 |
| --- | --- | --- | --- |
| `--input` | `-ResultPath` | 必填 | 当前轮 LoadTester 结果目录，可以是分片目录或运行根目录 |
| `--server-metrics` | `-ServerMetricsPath` | 自动识别 | 当前轮 Gather.TcpCenter 指标 JSONL；正式验收建议显式填写 |
| `--background` | `-BackgroundPath` | 空 | 独立测试背景 JSON，记录服务器规格、服务/代码版本、场景说明和关键运行参数；不影响指标计算 |
| `--output` | `-OutputPath` | `<input>\analysis` | 新报告输出目录，不覆盖源结果 |
| `--audience` | `-Audience` | `both`/`Both` | `acceptance`、`diagnostic`、`both`，按报告使用人群选择 |
| `--baseline-input` | `-BaselineResultPath` | 空 | 改造前或上一版本的 LoadTester 结果目录；设置后生成关键指标对比 |
| `--baseline-server-metrics` | `-BaselineServerMetricsPath` | 自动识别 | 基线服务指标；不能脱离基线结果单独设置 |
| `--title` | `-Title` | 根据 RunId 生成 | 汇报标题，例如“优化后 10000 台综合压测” |
| `--environment` | `-Environment` | `未填写` | 被测环境、服务器规格或网络区域说明 |
| `--service-version` | `-ServiceVersion` | 背景 JSON 或 `未填写` | 临时覆盖 `service.version`；Git 分支和 Commit 应写入背景 JSON 的独立字段 |
| `--tester` | `-Tester` | `未填写` | 测试执行或报告整理人员 |
| `--server-time-zone` | `-ServerTimeZone` | `Local` | 解释服务端无偏移时间戳的系统时区；中国 Windows 通常用 `China Standard Time` |
| `--business-catchup-seconds` | `-BusinessCatchupSeconds` | `60` | 物理连接达到目标后，业务在线允许的最长追平秒数 |
| `--queue-age-seconds` | `-QueueAgeSeconds` | `30` | TcpCenter Pipeline 允许的最大排队年龄秒数 |
| `--ack-milliseconds` | `-AckMilliseconds` | `5000` | ACK 从组帧到 Socket Send 返回、以及 Socket Send 完成回调允许的最大毫秒数 |
| `--target-devices` | `-TargetDevices` | 自动推断 | 覆盖里程碑目标；只在报告中采用的目标与场景设备数不一致时填写 |

数值阈值必须大于零。它们只改变报告判定，不会修改原压测结果。修改默认阈值用于正式验收时，应在报告元数据或评审记录中说明依据，避免为了得到“通过”而事后放宽门槛。

## 5. 测试背景 JSON

测试背景与场景 JSON 分开维护。场景 JSON 决定压测如何执行；测试背景 JSON 只补充报告追溯信息，修改它不会重新计算或改变任何测试指标。参考文件为 [`report-background.example.json`](report-background.example.json)。

合并优先级固定为：命令行参数 > 测试背景 JSON > `node-summary.json` 自动推断 > `未填写`。目前可由命令行覆盖的字段只有标题、环境、服务版本和测试人员；服务器规格、分支、Commit 和关键参数统一由背景 JSON 提供。未知字段会直接拒绝，避免拼写错误被静默忽略。

| JSON 路径 | 类型 | 默认/null 语义 | 意义与使用场景 |
| --- | --- | --- | --- |
| `report` | object | `{}`；null 按空对象 | 报告身份信息分组，不影响压测结论 |
| `report.title` | string/null | null 时按 RunId 生成 | 验收和排查报告共同标题；`--title` 优先 |
| `report.environment` | string/null | null 显示未填写 | 公司测试环境、正式灰度环境等；`--environment` 优先 |
| `report.tester` | string/null | null 显示未填写 | 测试执行或报告整理人员；`--tester` 优先 |
| `scenario` | object | `{}`；null 按空对象 | 测试场景背景分组 |
| `scenario.name` | string/null | null 使用节点摘要的 `scenario.name` | 建议与实际执行场景 JSON 名称一致 |
| `scenario.description` | string/null | null 自动按目标设备、协议、角色和阶段数生成 | 面向验收人员说明本轮覆盖连接、登录、重连、HTTP 等范围 |
| `server` | object | `{}`；null 按空对象 | 被测服务器资产和硬件规格分组 |
| `server.name` | string/null | null 使用场景 `target.host` | 资产名、虚拟机名或测试节点名称 |
| `server.operatingSystem` | string/null | null 显示未填写 | 操作系统名称、版本和必要补丁信息 |
| `server.cpu` | string/null | null 显示未填写 | CPU 核数和型号，例如 `8 vCPU，Intel Xeon Gold 6230R` |
| `server.memory` | string/null | null 显示未填写 | 服务器内存容量和必要规格 |
| `server.network` | string/null | null 显示未填写 | 网卡带宽、内外网位置等网络背景 |
| `service` | object | `{}`；null 按空对象 | 被测服务和代码版本分组 |
| `service.name` | string/null | Gather 兼容模式自动为 `Gather.TcpCenter`，否则为 `DeviceSocket Gateway` | 被测服务显示名称 |
| `service.version` | string/null | null 显示未填写 | 发布版本、构建号或改造前后标识；`--service-version` 优先 |
| `service.codeBranch` | string/null | null 显示未填写 | 生成被测程序的准确 Git 分支 |
| `service.codeCommit` | string/null | null 显示未填写 | 生成被测程序的 Git Commit，建议不少于七位 |
| `service.parameters` | array | `[]`；null 按空列表；最多50项 | 与本轮结论相关的服务运行参数，不要复制全部配置文件 |
| `service.parameters[].name` | string | 必填、非空、大小写不敏感唯一 | 参数显示名称，例如 `Pipeline Worker` |
| `service.parameters[].value` | string | 必填、非空 | 包含单位或完整口径的值，例如 `50000 条`、`60 秒` |

示例：

```json
{
  "report": {
    "title": "Gather.TcpCenter 优化后 10000 台综合压测",
    "environment": "公司内网测试环境",
    "tester": "张三"
  },
  "scenario": {
    "name": "gather-tcpcenter-http-reading-10000",
    "description": "10000 台设备连接、登录、心跳、HTTP 抄读、终端应答与同设备多命令压测"
  },
  "server": {
    "name": "TCPCenter-TEST-01",
    "operatingSystem": "Windows Server 2022 Standard",
    "cpu": "8 vCPU，Intel Xeon Gold 6230R",
    "memory": "16 GB",
    "network": "1 Gbps 公司内网"
  },
  "service": {
    "name": "Gather.TcpCenter",
    "version": "2026.08.21-optimized",
    "codeBranch": "hyz_feat_对接电表",
    "codeCommit": "1234567",
    "parameters": [
      { "name": "Pipeline Worker", "value": "16" },
      { "name": "Pipeline Queue Capacity", "value": "50000 条" }
    ]
  }
}
```

## 6. 时间对齐与累计计数

LoadTester 时间使用带偏移的 UTC 时间。TcpCenter 日志若没有时区偏移，则按 `--server-time-zone` 转成 UTC 后再与测试开始、结束时间对齐。报告出现“指标时间与测试窗口不重叠”时，优先检查：

1. 两台服务器系统时间是否同步；
2. `--server-time-zone` 是否与 TcpCenter 记录时间一致；
3. 指标文件是否属于同一轮测试。

Pipeline 失败、拒绝、`AckedButRejected`、`StaleBusiness`、`HeartbeatCoalesced`、`AckFailed`、`AckSendAborted`、连接关闭分类和命令线路压力保护次数属于进程累计计数。分析器会保留测试开始前最近一个样本作为基线，报告展示测试窗口内增量，避免把服务启动以来的历史事件算入本轮。趋势和峰值还会读取 `PendingConfirmSend`、`PendingCommandLine`、`CommandLinePipeline`、`LifecyclePipeline`、`AckSendCompletedMaxElapsedMs` 和 `LoginBusinessReadyMaxElapsedMs`；旧版服务缺少这些字段时按零兼容。

## 7. 验收口径

详细报告在 LoadTester 原有验收结果之外增加以下检查：

- 客户端协议在线是否达到目标；
- 服务物理连接与业务在线是否达到目标；
- 业务在线追平时间是否低于门槛；
- Pipeline 最大排队年龄是否低于门槛；
- ACK 最大耗时是否低于门槛；
- ACK Socket 发送完成最大耗时是否低于门槛；
- Pipeline 失败、容量拒绝、ACK 后拒绝是否全部为零；
- TcpCenter 进程在测试窗口内是否保持稳定；显式或旧版命名为 `restart` 的计划重启场景允许一次 PID 变化，额外变化仍失败。

每项结果是“通过”“失败”或“不可评估”三态。任何失败或不可评估都会使综合验收结果为“未通过”。这样可以明确区分“指标证明通过”和“没有指标所以不知道”，避免缺少服务端数据时误报成功。

自动诊断同时标注证据置信度：

- `Fact`：计数、时间线或状态直接证明；
- `HighProbability`：多项指标强关联，但仍建议结合日志、PerfView 或 dump 最终确认；
- `Hypothesis`：当前证据不足，仅作为下一步排查方向。

自动诊断、置信度、影响和排查建议只写入排查分析版。验收汇报版只展示测试背景、结论与验收规则、核心指标、报表视图、可选基线对比和数据质量说明。

## 8. 改造前后对比

同一命令可加载一轮基线：

```powershell
.\tools\DeviceSocket.LoadTester\scripts\New-GatherLoadTestReport.ps1 `
  -ResultPath .\load-test-results\gather-http-after-r1\shard-000 `
  -ServerMetricsPath .\load-test-results\gather-http-after-r1\shard-000\20260821.jsonl `
  -BaselineResultPath .\load-test-results\gather-http-before-r1\shard-000 `
  -BaselineServerMetricsPath .\load-test-results\gather-http-before-r1\shard-000\20260820.jsonl `
  -Audience Both `
  -ServerTimeZone "China Standard Time"
```

工具会比较连接成功率、登录成功率、API 成功率、协议在线峰值、业务在线峰值、业务追平时间、队列年龄和 ACK 耗时。为了让变化有意义，改造前后应保持场景 JSON、设备 CSV、负载机数量、网络、目标服务器规格和阈值一致；建议各执行三轮，以中位数作为正式对比并保留最差一轮附件。

新版节点报告会把每个 HTTP 阶段的预校验拒绝、估算入队、原始下行、协议识别、不支持格式、设备应答、重复帧、未观测缺口、三段比例、独立 P95、排空结果和熔断状态合并到详细报告。原始下行交付率回答“服务端是否真正把命令送到设备连接”，协议识别率回答“模拟器是否兼容正式服务的实际下行格式”，应答率只针对已识别命令计算，三者不能再混为一个交付率。若 TCP 场景包含显式 `cooldown` 阶段，服务端验收与自动问题诊断在 cooldown 开始时截止；计划缩容之后的连接关闭、ACK 中止和命令暂停另存为冷却窗口摘要，不再误判为有效压测失败。

需要定位单台设备时，回到源分片目录查看 `phase-targets.csv` 或 `command-traces.jsonl`；需要补协议兼容时，先看 `downstream-classification.csv` 的分类数量，再从 `downstream-unknown-samples.jsonl` 读取按指纹限量保存的原始样本。详细报告只聚合这些事实，不复制全部逐请求明细。

## 9. 输出文件

```text
analysis/
├─ acceptance-report.html
├─ acceptance-report.md
├─ diagnostic-report.html
├─ diagnostic-report.md
├─ analysis.json
├─ phase-summary.csv
└─ charts/
   ├─ online-timeline.svg
   ├─ business-online-gap.svg
   ├─ connection-errors.svg
   ├─ api-phases.svg
   └─ command-funnel.svg
```

HTML 自包含样式和内嵌图表，可直接用浏览器打开或打印为 PDF。Markdown 引用 `charts` 下的 SVG，移动 Markdown 时需要同时保留该目录。`analysis.json` 使用 camelCase 属性和字符串枚举；当前 `schemaVersion=5`，新增 HTTP 原始下行交付、协议识别、不支持格式与重复帧口径，外部脚本应先检查版本再读取字段。

## 10. 退出码与常见问题

| 退出码 | 含义 |
| ---: | --- |
| `0` | 详细报告生成成功，且全部验收项通过 |
| `1` | 输入读取、时区解析或报告写入失败 |
| `2` | 命令行参数错误 |
| `3` | 报告已生成，但存在失败或不可评估项 |
| `130` | 用户取消 |

常见问题：

- 只有客户端结果：报告仍会生成，但业务在线、Pipeline、ACK 和服务进程稳定性标记为不可评估。
- 图表没有服务端曲线：检查服务指标路径、时区和测试窗口是否重叠。
- 输出结果为退出码 3：先打开 `acceptance-report.html` 看失败门槛，再打开 `diagnostic-report.html` 查看证据和排查建议；这不表示文件生成失败。
- 多分片数据不全：先确认每个 `shard-xxx` 都有 `node-summary.json`，且确实属于同一 RunId 和同一轮测试。
