# GatherV4 止血版兼容说明

本文记录 DeviceSocket.LoadTester 对 GatherV4 主通讯链路的实际覆盖范围。容量结论必须以本文件标记为“已支持”的能力和服务端 OpenTelemetry 证据为边界，不能把尚未实现的模拟行为写成已验证。

## 本次已落地

| 能力 | 状态 | 实际行为与证据口径 |
| --- | --- | --- |
| 独立兼容配置 | 已支持 | 使用 `target.compatibilityProfile=gatherV4`，不改变 `deviceSocket` 和 `gatherTcpCenter` 既有行为 |
| 登录与心跳 | 已支持 | 发送 CON=1 的 AFN=02H/F1、F3，只有收到地址和 PSEQ 均匹配的 AFN=00H/F1 才记为成功 |
| 流式接收 | 已支持 | 复用现有 376.1 流式拆包器，处理半包、粘包、多帧和尾部残帧 |
| 命令关联响应 | 已支持 | 对地址匹配的主站下行命令生成 AFN=00H/F3，保留请求 A3、复用 PSEQ 并重新计算校验和 |
| 命令故障模式 | 已支持 | `normal`、`delayed`、`drop`、`duplicate`、`wrongA3`、`wrongPseq`；按设备稳定选样 |
| HTTP 正式入口 | 已支持 | 调用 `/Handmould/IssueCmd` 或 `/Handmould/IssueCmdSync`，按 GatherV4 `HandmouldRequest` 和 `Status/Msg` 解析 |
| 设备身份与分片 | 已支持 | 九位编号按前四位 AreaCode、后五位 Address；CSV 的 AmmeterId 与设备号在多节点间确定性切分 |
| 主动上报模板 | 部分支持 | 可导入一条真实完整帧，动态改写设备地址、PSEQ、CON 和校验和；尚未单列上报 ACK RTT/超时 |
| 阶段在线屏障 | 已支持 | 可要求协议在线数达到比例并连续稳定后才进入阶段计时；超时终止并保留部分报告 |
| 资源自监控 | 已支持 | 报告记录压测进程 CPU、内存、GC、线程池、进程线程数和句柄数；Socket/端口仍使用现有预检与时间序列口径 |
| 固定随机重放 | 已支持 | `distributed.randomSeed` 控制设备抖动及故障选择，参与多分片配置签名 |
| 高负载保护 | 已支持 | 超过阈值、故障模式和目标白名单在启动前校验，不满足显式授权即拒绝运行 |

## 止血版明确不覆盖

1. AFN=00H/F3 当前用于验证命令已下发、`Code-A3-PSEQ` 关联和同设备发送窗口释放，不等价于真实电量采集结果。要验收解析、落库和具体 `ItemCode` 数据，必须增加对应 AFN/FN 的真实业务回包模板。
2. 尚未实现 GatherV4 Redis 通讯隔离注入。Ready/Processing 的 Key、SchemaVersion、CommandId 和租约规则属于 GatherV4 契约，未建立共享 helper 前不在 LoadTester 内复制常量。
3. 尚未覆盖人工/普通/补抄的优先级、FIFO/LIFO 自动断言，也未覆盖客户端 RST、半开连接、旧 Session 迟到报文和同步心跳洪峰。
4. 主动上报确认目前只能被识别并避免重复应答，尚未输出独立的上报 ACK 成功、非法、重复、超时和 RTT 直方图。
5. GatherV4 通过 OTLP/HTTP Protobuf 输出服务端指标；当前 `--server-metrics` 解析器仍只支持旧 Gather.TcpCenter 自定义 JSONL。Collector 指标必须按同一 RunId 独立归档，不能伪装为旧格式导入。
6. 未在本次止血中新增 LoadTester 自身 OTLP exporter。现有 JSONL/JSON/CSV/Markdown 报告和资源采样仍是客户端事实来源。

因此，当前版本可以形成 GatherV4 的连接、合法登录 ACK、心跳、真实重启恢复、HTTP 到 TCP 下行可达性以及命令关联窗口结论；在补齐真实业务响应模板和服务端 OTel 自动合并前，不能形成“采集数据正确入库”或“全部 P0 场景完成”的结论。

## 交付场景

| 场景 | 用途 | 运行前必须修改 |
| --- | --- | --- |
| `Scenarios/gatherv4-connections-10000.json` | 一万连接、登录 ACK、心跳和稳态基线 | TCP 地址、端口、CSV、目标白名单 |
| `Scenarios/gatherv4-http-reading-10000.json` | 一万在线、IssueCmd、下行关联应答及同设备连续命令 | TCP/API 地址、CSV、GatherCode、ItemCodes、白名单 |
| `Scenarios/gatherv4-reconnect-storm-10000.json` | 一万稳定在线后人工重启 Communication 并观察全量恢复 | TCP 地址、端口、CSV、白名单；按提示执行服务重启 |

三份场景都使用在线屏障。屏障时间不包含在 `durationSeconds` 内：先达到配置的在线率并连续稳定，再开始该阶段的保持计时。这样报告不会把登录尚未追平的爬升过程当作稳态样本。

## 首轮执行顺序

1. 复制正式场景为测试环境专用文件，填写 TCP/API 地址、`safety.allowedTargets`、真实 CSV、GatherCode 和 ItemCodes；禁止把凭据写入 JSON。
2. 把连接场景临时降到 100 台，执行登录和心跳冒烟；确认客户端登录成功数与服务端 `gather.tcp.bound.connections` 对齐。
3. 扩到 1,000 台，检查 LoadTester CPU、线程、句柄、Socket、网络和端口预检，同时观察 GatherV4 OTel 的帧、ACK、回调耗时和 GC。
4. 执行一万台纯连接基线；只有在线屏障、客户端资源和服务端 OTel 都正常，才进入 HTTP 场景。
5. HTTP 先保留 20 条功能阶段。确认命令下行、F3 关联、下一条命令释放和 API 返回口径后，再逐步提高 RPS/并发。
6. 最后执行真实重启风暴。出现控制台操作窗口后重启 Communication，LoadTester 不需要人工按键，也不要停止负载进程。

## GatherV4 服务端同步检查

- 测试实例连接上限必须高于目标规模，示例中的 20,000 不能用于三万或五万结论。
- Communication、DirectiveService、IssueWebApi 的 GatherCode、Redis DB 和测试设备档案必须属于同一环境。
- 记录 `UseReadyCommandDispatch`、`UseCommandProcessingLease` 的实际值；二者组合不同必须分轮测试，不得合并为一个结果。
- 容量测试必须启用 `GatherService.OpenTelemetry`，Collector 不可用时不得继续出具服务端容量结论。
- 保存 GatherV4 Git 提交、发布目录版本、服务器规格、Config.json 脱敏快照、Collector 时间窗以及 LoadTester RunId。

## 后续优先顺序

1. 为确定的真实 `ItemCode` 增加读数/控制类回包模板和黄金报文测试。
2. 增加主动上报 ACK 独立状态机与指标，并补同步心跳洪峰及混合流量场景。
3. 由 GatherV4 提供 Ready/Processing 共享 Key/DTO helper 后，再实现 Redis 通讯隔离模式。
4. 固定 Collector 的机器可读导出格式后，新增 GatherV4 OTel 报告适配器和自动验收项。
5. 补客户端 RST、半开、重复 Session、迟到响应、非法长度/结束字节及恢复场景，再扩展到三万/五万多节点矩阵。
