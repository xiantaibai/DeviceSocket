# GatherV4 人工命令下发与自动确认压测

## 测试目标与准备

本场景验证 IssueWebApi → DirectiveWorker → ConnectWorker → 模拟设备 F1 → 回包解析 → API 完整结果返回。F1 是任务全部确认，不包含真实表计示值；不据此验收真实读数或落库。建议首轮使用独立测试 GatherCode，暂停该测试分区的定时采集，避免后台任务污染按设备 FIFO 的关联结果。

场景位于 `Scenarios/gatherv4-manual-command-10000.json`、`Scenarios/gatherv4-manual-command-50000.json`。文件中的 `192.168.111.222` 是文档保留地址，必须替换 TCP host、API baseUrl、safety.allowedTargets，并核对 GatherCode、ItemCodes、真实 CSV。

每台机器都部署同一完整 CSV，按 shardIndex 固定切片。五万台默认两个节点，可覆盖为四个；不要把五万连接理解为五万 HTTP 同时请求。运行前核对负载机端口、文件句柄及 CPU，Windows/Linux 负载机时钟均使用同步 UTC；DataTime 默认按负载机本地时间生成，两台机器业务时区应一致，或显式填写固定业务时间。

## 先做功能预检

两份人工命令场景默认使用 `meterType=1`、`itemCodes=["0D_82"]`，对应 AFN=0DH/F82“测量点 A 相有功功率曲线”。本地 GatherV4 已有该采集项分组和编码器；测试环境仍需部署相同配置。设备收到后自动返回 AFN=00H/F1 全部确认，预期返回 DataType 为 `0D_82`，因此 `expectedResultItemCodes=[]` 保持按请求项校验，不模拟真实曲线数据。

设备映射必须使用 `docs/gatherv4-device-mapping-query.sql` 从待测环境重新导出。脚本会对齐服务端数据库回退条件并按 `GatherCode` 过滤；只判断电表 `ID > 0` 不足以证明该表能被人工下发接口读取。导出后在所有负载机部署同一份完整 CSV，不要分别截取文件。

先用少量设备验证真实返回 `Data` 的 MeterId、ValidData、DataType。默认 expectedResultItemCodes 为空时按 itemCodes 检查全部返回项，透明转发返回类型不同必须明确填写。例如请求 `16_1` 而 F1 解析结果为 `00_1` 时配置 `["00_1"]`，不要关闭 validateResultData 掩盖差异。

核对一请求实际下发数量，填 expectedCommandsPerRequest。服务端不同 ItemCodes 可能合并或拆分，不应按数组长度猜测。TCP 帧没有 HTTP requestId/IssueMark，工具标记为 Ordered 推断关联，不能证明逐 CommandId 精确匹配。

默认依次运行：20 条功能验证；10、50、100 RPS 阶梯；同一批 1000 台每台连续 5 条；全设备各一条。阶段之间保留 30 秒确认后观察和 20 秒静默排空。RPS 是目标上限，实际速率还受同步 API 延迟和并发上限影响，不能只看配置值宣称吞吐达到。

## 工具批量模拟人工下发

在发布目录执行，先确认配置已替换为测试环境：

```powershell
.\DeviceSocket.LoadTester.exe run --config Scenarios\gatherv4-manual-command-10000.json --run-id v4-manual-10000-r1
```

五万台两节点使用同一个 RunId、将来时刻的 ISO-8601 UTC 时间和完整 CSV，第一台 shard-index=0，第二台改为 1：

```powershell
.\DeviceSocket.LoadTester.exe run --config Scenarios\gatherv4-manual-command-50000.json --run-id v4-manual-50000-r1 --shard-index 0 --shard-count 2 --start-at-utc 2026-11-01T02:00:00Z
```

上面时间仅为格式示例，必须替换成实际计划时刻。Linux 使用同名可执行文件 `./DeviceSocket.LoadTester`，路径分隔符改 `/`；若部署框架依赖产物，使用 `dotnet DeviceSocket.LoadTester.dll` 加相同参数。

## 从业务页面手动下发

复用相同场景并加 `--role devicesOnly`，工具关闭 HTTP 驱动但维持设备上线、心跳、自动 F1 与命令统计：

```powershell
.\DeviceSocket.LoadTester.exe run --config Scenarios\gatherv4-manual-command-10000.json --run-id v4-manual-ui-r1 --role devicesOnly
```

待协议在线达到目标并稳定后，从 GatherV4 业务页面选择 CSV 内真实设备和电表下发。默认一万场景观察 30 分钟、五万场景 60 分钟，可按 scenario.phases 调整；正常结束会输出报告，Ctrl+C 提前结束保留取消前报告并标记 Cancelled。纯设备模式无法从客户端核验页面请求的完整 API 结果，报告不会把 F1 写出写成“主站任务完成”。

## 结果文件与故障模式

- node-summary.json/node-timeseries.jsonl：metrics.gatherV4Commands 保存收到帧、观察去重命令、正确 F1、故意丢弃、故障应答、疑似重发、确认后再次到达、后续命令等待与应答延迟。
- gatherv4-command-samples.jsonl：保留节点最近 500 条回复样本，含设备、会话代次、指纹、状态及实际回复 HEX，支持查看“回复了什么”。
- command-traces.jsonl：工具 HTTP 请求的一对多子命令关联，区分 expectedDownstreamCommands、observedDownstreamCommands、correctlyRespondedCommands。
- http-errors.csv/jsonl：区分 EMPTY_RESULT、PARTIAL_RESULT、INVALID_RESULT、RESULT_DEVICE_MISMATCH、业务失败、网络失败和超时；configuredDeviceFault 保留目标设备的稳定故障注入背景，不据此把所有失败自动归因于注入。

重复相同完整帧只称“疑似重发”：没有业务 CommandId 时，无法区分新任务、序号复用和真正重投。每连接仅保留最近 64 个指纹、默认观察 60 秒，断线清空，不能用于证明长时间 exactly-once。

delayed 模式复用会话已有调度，不暂停心跳接收。maximumPendingResponsesPerDevice 默认 32，队满明确失败，断线取消旧会话待回复项。drop、duplicate、wrongA3、wrongPseq、deny 必须允许故障注入；deny 发 F2，错误序号与 F2 都不计正确 F1。故障轮应独立命名 RunId 并修改预期验收规则，不能混入正常容量基线。

## 合并与诊断报告

把所有 shard-xxx 放回同一个运行目录：

```powershell
.\DeviceSocket.LoadTester.exe report merge --input load-test-results\v4-manual-50000-r1 --output load-test-results\v4-manual-50000-r1\merged
.\DeviceSocket.LoadTester.exe report generate --input load-test-results\v4-manual-50000-r1 --output load-test-results\v4-manual-50000-r1\analysis --audience both --server-metrics evidence\gatherv4-otlp.jsonl
```

GatherV4 的 --server-metrics 使用 **Collector file exporter 的标准 OTLP JSONL**（每行一个完整 ExportMetricsServiceRequest），或单个标准 JSON 文件。结构为 resourceMetrics[].resource.attributes、scopeMetrics[].metrics[].gauge/sum.dataPoints[]，时间为 timeUnixNano，数值为 asInt/asDouble；累计 sum 的 aggregationTemporality 必须为 2（CUMULATIVE），保留 service.instance.id、queue、queue_type、operation、result 等标签。

只归档本轮目标服务/测试分区的 Collector 文件。文件需要与压测起止时间重叠；工具剔除全部分片进入 cooldown 后的指标，按实例/固定维度/时间去重，并识别累计值回退或 startTime 改变。Delta Sum 会被跳过并告警；未知指标不导入；单个维度超过十万点或序列超过 4096 会明确拒绝。二进制 OTLP、Prometheus exposition 和旧 TcpCenter JSONL 不能冒充该格式。

报告独立展示 response.matched/unmatched、command.inflight、popped/send_accepted/send_failed/restored、mark_sessions/mark_expired、pipeline.pending/rejected、redis.operations(operation=processing_complete,result=success) 及资源证据。缺失指标不补零；Counter 少于两个样本不计算增量。

Processing 完成操作次数只是服务聚合证据，不证明每个任务租约都已删除。定位仍需按服务端 CommandId/IssueMark 和 Processing 记录对账；工具不读取或清空 Redis 队列。HTTP 超时也不会触发再次调用 API 或删除服务端任务，避免把结果未知变成重复下发。

验收汇报只展示结论与数据；诊断报告额外展示缺口和排查建议。真实环境发布验收前，应完成单设备、100 台、1000 台功能基线，再扩大至一万／五万台。
