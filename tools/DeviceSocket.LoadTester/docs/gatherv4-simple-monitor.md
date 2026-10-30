# GatherV4 压测服务器轻量监控

日常压测只使用 `Start-GatherV4Monitor.ps1`。它只有一个窗口，只记录本机进程资源、Windows 服务状态和人工时间标记；不会操作业务服务，也不会启动 PerfView、ProcDump，不采集 RabbitMQ、Redis 或业务指标。

## 第一次部署：只配置一次

把下面两个文件复制到被测服务器的同一个目录：

```text
Start-GatherV4Monitor.ps1
GatherV4.Monitor.config.psd1
```

请直接复制仓库或发布目录中的文件，不要从网页复制脚本文本后另存。测试服务器使用 Windows PowerShell 5.1 时，这两个含中文的文件必须保留 **UTF-8 BOM**；用编辑器修改配置后也要选择“UTF-8 with BOM”，否则会出现中文乱码和大量假的语法错误。

编辑 `GatherV4.Monitor.config.psd1`：

- Connect 推荐使用监听端口，默认是 `8890`。
- Directive 和 TimerTask 推荐填写 Windows 服务的内部 `Name`，不要填写显示名称或 exe 名。
- IssueWebApi 是独立进程时使用监听端口，是 Windows 服务时使用内部 `Name`。
- 找不到准确实例的目标先保持 `Enabled = $false`，不要按重名 exe 猜测。

首次可在服务器执行下面的只读命令查服务内部名称：

```powershell
Get-CimInstance Win32_Service |
  Where-Object PathName -Match 'ConnectService|DirectiveService|TimerTaskService|IssueWebApi' |
  Select-Object Name, DisplayName, State, ProcessId, PathName |
  Format-List
```

配置完成后先验证，不创建文件：

```powershell
.\Start-GatherV4Monitor.ps1 -RunId config-check -DryRun
```

每次更新部署路径、端口或服务内部名称时重新验证；正常压测不需要重复选择 PID。

## 每轮压测：只输入 RunId

以五万设备人工指令为例：

```powershell
.\Start-GatherV4Monitor.ps1 `
  -RunId gatherv4-manual-command-50000-r2
```

脚本显示“轻量监控已启动”以后，再启动两台负载机。运行期间只有两个按键：

- `M`：输入一个时间标记，例如“开始全覆盖”“首次 HTTP 超时”“人工重启 Connect”。
- `Q`：正常结束监控并生成摘要和 ZIP。

也可以直接按 `Ctrl+C` 中断；脚本会尽量保留已落盘的 CSV 并执行归档。计划任务或自动测试使用 `-NonInteractive`，达到时长后自动结束。

其他场景只需要改变 RunId 和场景标签：

```powershell
.\Start-GatherV4Monitor.ps1 `
  -RunId gatherv4-reconnect-storm-50000-r2 `
  -Scenario gatherv4-reconnect-storm-50000
```

## 输出内容

默认目录：

```text
D:\GatherEvidence\<RunId>\server-monitor\<服务器>-<UTC时间>\
```

| 文件 | 内容 |
| --- | --- |
| `process-timeline.csv` | 每个目标的 CPU、Private Bytes、Working Set、线程、句柄、PID 和服务状态 |
| `events.csv` | 会话开始/结束、初始状态、PID/服务状态变化、人工标记的统一 UTC 时间线 |
| `summary.md` | 面向人员的资源峰值、不可用采样和状态变化摘要 |
| `summary.json` | 供后续自动分析使用的同口径摘要 |
| `manifest.json` | RunId、场景、服务器、时间范围、采样间隔和产物路径 |
| `monitor-config.psd1` | 本轮实际使用的目标配置快照 |
| `*.zip` | 上述证据的自动归档 |

## 场景与工具边界

| 情况 | 日常监控脚本 | 额外工具 |
| --- | --- | --- |
| 常规连接、人工指令、重连风暴 | 全程运行，记录资源和状态 | 默认不需要 |
| CPU 持续偏高 | 先用 CSV 确认进程和时段 | 对目标时段单独运行 PerfView CPU/ThreadTime |
| 内存持续增长 | 先确认 Private Bytes 增长趋势 | 单独运行 PerfView GC/Alloc，并在高水位抓完整 Dump |
| 请求卡住但进程存活 | 记录 `M` 标记并保留状态趋势 | 对已确认的主要进程抓间隔 Dump 或 ThreadTime |
| 服务自行停止/崩溃 | 时间线记录停止和 PID 变化 | 压测前单独给准确实例布置 ProcDump 异常/退出监听 |

PerfView 和 ProcDump 属于异常取证工具，不再由日常监控入口自动启动。需要进阶取证时再查阅 `gatherv4-perfview-dump-runbook.md`，不要把全部工具每轮同时开启。

## 重要限制

- 脚本不启动、停止或重启 GatherV4 业务服务，避免改变压测结果。
- 脚本不读取 RabbitMQ、Redis、HTTP 或 GatherV4 命令链指标。
- `ProcessId` 不跟随重启；长期监控优先使用 `Service` 或 `Port`。
- 端口对应多个进程或服务名不存在时会明确记录 `Ambiguous`、`Missing` 或 `Error`，不会擅自选择重名实例。
- CSV 与事件统一使用 UTC；分析报告再按服务器时区转换，避免两台负载机与服务器时间混淆。
