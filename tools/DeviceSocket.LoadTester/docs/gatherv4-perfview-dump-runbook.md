# GatherV4 PerfView 与 Dump 进阶参考：手动命令及各场景排查

> 日常压测不要从本文的复杂组合向导开始。先使用 [GatherV4 压测服务器轻量监控](gatherv4-simple-monitor.md)，只记录进程资源、服务状态和统一时间线；只有出现 CPU、内存、卡住或崩溃等明确症状时，才按本文单独运行对应的 PerfView 或 ProcDump 操作。

本手册按“完成一个场景，再开始下一个场景”编写。场景来自当前仓库的五份 `Scenarios/gatherv4-*.json`；本次不包含 Gather.TcpCenter。

旧版多窗口组合向导保留用于兼容既有取证，不再作为日常入口。新监控按固定 `Service`、`Port` 或 `ProcessId` 定位目标，常规压测不需要重复选择工具和 PID。

交互入口的固定工具路径在脚本顶部 `$configuredToolPaths` 中维护，首次部署时改为服务器实际安装位置。文件存在时自动使用；只有对应工具找不到时才询问本次替代路径。本文手动命令仍通过 `$common.PerfViewPath` 和 `$common.ProcDumpPath` 指定工具。

本文保留手动命令作为进阶入口，使用选项 8 时无需按本文另开资源、PerfView 或 Dump 监听。第 1～4 节说明参数方式，第 5 节列出各场景的采集时机，第 6～9 节用于参数查询、异常排查、查看产物和前后对比。本文一万连接和四分片示例用于其他运行方式，不能直接替换当前两分片复测参数；交互向导不会启动 LoadTester 或重启服务。

## 1. 先理解这份 ps1 做什么

`Invoke-GatherV4Diagnostics.ps1` 是**被测服务器上的采集工具入口**。每执行一次命令，只做 `-Action` 指定的一件事：

| Action | 做什么 | 什么时候用 | 何时返回 PowerShell 提示符 |
| --- | --- | --- | --- |
| `Inventory` | 显示候选进程路径、PID、服务内部名称 | 第一次配置；需要核对实例时 | 列表显示完就返回 |
| `Sample` | 每隔几秒记录 CPU、内存、线程和句柄到 CSV | 每个场景开始前启动，覆盖本轮压测 | 达到 DurationSeconds 或按 Ctrl+C |
| `Trace` | 调用 PerfView 采一段性能跟踪 | 在要观察的场景阶段执行 | 采集结束并完成合并、压缩后返回 |
| `Dump` | 调用 ProcDump 抓一份完整内存快照 | 内存异常，或需要保存某一时点 | Dump 写完后返回 |
| `DumpSeries` | 对同一进程连续抓三份完整快照 | 请求不再推进、疑似持续等待/卡住 | 三份都写完后返回 |
| `Incident` | 同时布置资源、整机 Thread 及主要对象退出监听；按 D 抓三份存活 Dump | old-r2 这类存活但心跳停滞的复现 | 按 D/Q、主要对象退出或到期后归档返回 |
| `DumpWatch` | 等待未处理异常、退出或 CPU/提交内存阈值后自动转储 | 压测前启动，补齐意外退出现场 | 各 PID 监听结束、时长上限或 Ctrl+C；取消时等待当前转储写完 |

脚本**不会启动 LoadTester、切换场景、部署版本或自动重启服务**。它不会根据 `-Stage` 等待某个阶段；时机由操作人员根据 LoadTester 日志判断。`-Scenario`、`-RunId`、`-VersionTag`、`-Stage` 用于标记产物，不能代替实际压测配置。

一个场景可以执行一次长时间 Sample，再在不同时间执行几次短时间 Trace。CPU、线程、GC、Dump 按需要选择，不需要全部执行。

## 2. 第一次使用：确认工具路径与被测实例

### 2.1 将脚本放到被测服务器

将仓库的 `scripts/Invoke-GatherV4Diagnostics.ps1` 复制到例如：

```text
D:\GatherDiagnostics\Invoke-GatherV4Diagnostics.ps1
```

在服务器打开**管理员 PowerShell**：

```powershell
$diag = 'D:\GatherDiagnostics\Invoke-GatherV4Diagnostics.ps1'
& $diag -Action Inventory
```

`&` 表示执行脚本路径。直接运行命令即可，不需要先加载采集脚本中的函数。

### 2.2 在同名进程中确认本次实例

| Inventory 字段 | 含义 | 后续怎么用 |
| --- | --- | --- |
| 进程 Name | exe 名称，例如 GatherService.ConnectService.exe | 认程序，不能区分多个同名实例 |
| ProcessId | 当前 PID | 非 Windows 服务的 API 用它；重启后会变 |
| ExecutablePath / PathName | 部署路径 | 判断是不是本次 GatherV4 部署 |
| Windows 服务 Name | 精确服务内部名称 | 填 `$connectService` 等变量 |
| Windows 服务 DisplayName | 服务管理器显示名称 | 辅助识别；脚本使用内部 Name |

当前场景 TCP 目标为 `192.168.111.222:8890`；部署有变化时以本轮配置为准。核对监听 PID、程序路径和服务内部名称：

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8890 |
    Select-Object LocalAddress, LocalPort, OwningProcess

Get-CimInstance Win32_Process -Filter "Name='GatherService.ConnectService.exe'" |
    Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine | Format-List

Get-CimInstance Win32_Service |
    Where-Object PathName -match 'GatherService' |
    Select-Object Name, DisplayName, State, ProcessId, PathName | Format-List
```

把端口 OwningProcess 对上进程 PID，再对上服务 ProcessId。确认路径、GatherCode 后记录服务内部 Name。截图中的 ConnectService 是 Communication 的候选宿主，不能直接使用截图旧 PID。

| 服务 | 连接/重启场景 | HTTP 场景 |
| --- | --- | --- |
| Communication / ConnectService | 主要采集对象 | TCP 下行、应答、关联窗口的主要对象 |
| DirectiveService | 记录背景资源；异常时补采 | 指令处理的主要对象 |
| TimerTaskService | 记录背景资源；异常时补采 | 记录资源，具体任务需核对服务实现 |
| IssueWebApi 实际宿主 | 没有 HTTP 负载，不必默认加入 | 必须加入，观察同步等待和 API 返回 |

仓库没有服务器服务注册信息，也不包含 GatherV4 服务实现。内部名称和具体部署职责需现场核对；只填写本机实际存在、属于同一测试环境的实例。

### 2.3 HTTP 场景再确认 API

当前 API 为 `http://192.168.111.222:5000/Handmould/IssueCmd`：

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5000 |
    Select-Object LocalAddress, LocalPort, OwningProcess

# 仅 IIS 托管时使用，核对目标应用池对应的工作进程。
& "$env:windir\System32\inetsrv\appcmd.exe" list wp
```

API 可能是独立 exe、dotnet.exe 或 IIS w3wp.exe。HTTP.sys 若显示 PID 4，继续定位应用池及实际后端进程，不能把 PID 4 当业务 API 抓 Dump。API 如果是 Windows 服务，可直接使用内部名称加入服务列表，省去固定 PID。

## 3. 完整示例：现在只跑“一万连接”这一个场景

### 3.1 准备三个窗口

| 窗口 | 在哪台机器 | 用途 |
| --- | --- | --- |
| L：负载窗口 | LoadTester 所在机器 | 启动本轮场景，看阶段日志 |
| A：资源窗口 | 被测服务器，管理员 PowerShell | 持续执行 Sample，运行期间保留窗口 |
| B：诊断窗口 | 同一被测服务器，另一个管理员 PowerShell | 核对实例、预览命令、按时机 Trace/Dump |

如果负载机与服务同机，也使用不同窗口并记录资源争用。服务分布在多台服务器时，各服务器运行本机采集，同轮 RunId 相同，只传本机目标。

### 3.2 保存一个“本轮参数文件”

在被测服务器用编辑器新建 `D:\GatherDiagnostics\Current-GatherRun.ps1`，复制下面内容，修改工具路径及服务内部名称。这只是普通变量文件，不会采集或重启服务。

```powershell
$diag = 'D:\GatherDiagnostics\Invoke-GatherV4Diagnostics.ps1'
$common = @{
    RunId = 'gatherv4-connections-10000-old-r1'
    VersionTag = 'old'
    Scenario = 'gatherv4-connections-10000'
    OutputRoot = 'D:\GatherEvidence'
    PerfViewPath = 'C:\Tools\PerfView\PerfView.exe'   # 改为已安装路径。
    ProcDumpPath = 'C:\Tools\ProcDump\procdump64.exe' # 必须是 Sysinternals ProcDump。
}
$connectService = '替换为8890所属服务的内部Name'
$directiveService = '替换为同环境Directive服务的内部Name'
$timerService = '替换为同环境TimerTask服务的内部Name'

# 这里只保留本机实际存在、属于本次环境的服务。
$servicesOnThisHost = @($connectService, $directiveService, $timerService)

# 仅 HTTP 场景使用，届时改为已确认的 API PID。
# API 若是 Windows 服务，将内部名称加入服务数组，此值保留为 0。
$apiProcessId = 0
```

在窗口 A、B **分别执行一次**：

```powershell
. 'D:\GatherDiagnostics\Current-GatherRun.ps1'
$common
$servicesOnThisHost
```

开头的 `.` 是点源加载，让变量留在当前窗口；它后面必须有空格。A 的变量不会自动传给 B。`@common` 表示把表中的公共参数传给脚本，不是另一个文件或命令。

### 3.3 窗口 B：先预览命令

```powershell
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage preflight -Mode CPU -DurationSeconds 60 -DryRun
```

应输出实际 PerfView 命令，包含 `/FocusProcess:实际PID`。核对工具路径、PID 和输出目录。这一步不采集、不创建证据文件，但会查询服务，要求目标存在且工具路径有效。

### 3.4 窗口 A：先启动资源记录

```powershell
& $diag @common -Action Sample -ServiceName $servicesOnThisHost `
    -Stage whole-run -DurationSeconds 7200 -IntervalSeconds 2
```

每 2 秒记录一次，最多运行 7200 秒。两小时是监控上限，**不会把 30 分钟的压测场景改成两小时**。命令占用 A 窗口；运行中不持续打印表格是正常的，CSV 会逐批追加。

Sample 从压测前覆盖到场景结束及清理后。在线屏障可能额外等待，预计超过上限时加大 DurationSeconds。重启场景中按服务内部名称跟随新 PID，并保留停止状态。

### 3.5 窗口 L：启动这一份场景

以已发布工具为例，修改实际路径，RunId 与 `$common.RunId` **完全一致**：

```powershell
$loadTester = 'D:\LoadTester\DeviceSocket.LoadTester.exe'
$loadConfig = 'D:\LoadTester\Scenarios\gatherv4-connections-10000.json'
$runId = 'gatherv4-connections-10000-old-r1'
& $loadTester run --config $loadConfig --run-id $runId
```

若使用源码运行，选下面这一种启动方式：

```powershell
dotnet run --project 'D:\项目\DeviceSocket\tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj' -- `
    run --config 'D:\项目\DeviceSocket\tools\DeviceSocket.LoadTester\Scenarios\gatherv4-connections-10000.json' `
    --run-id 'gatherv4-connections-10000-old-r1'
```

脚本不会检查 L 窗口的 RunId，需手工保持一致。

### 3.6 窗口 B：在需要的阶段采 PerfView

排查轮可以同时保留 Sample 并采短 Trace。最终性能对比的正式轮次，建议只做相同配置的资源/客户端/服务端监控，另一次同场景复现轮采 PerfView/Dump，避免取证干扰延迟和恢复结果。

看到 `[阶段开始] ramp-10000`，需要看建连/登录 CPU 时：

```powershell
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage ramp-10000 -Mode CPU -DurationSeconds 60
```

看到 `[在线屏障通过] steady-10000`，需要看万台稳态 CPU 时：

```powershell
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage steady-early -Mode CPU -DurationSeconds 60
```

这两条在不同阶段执行，不要立即连续粘贴。Trace 计时结束后还需合并、压缩，等 B 返回提示符再启动下一份。

CPU 不高但登录不再推进，在复现问题时选择线程采集：

```powershell
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage login-wait -Mode Thread -DurationSeconds 30
```

需要现场快照时，选择一份或三份：

```powershell
# 当前时点一份完整快照。
& $diag @common -Action Dump -ServiceName $connectService -Stage login-stall

# 或持续卡住时三份快照，每份写完后等 10 秒再抓下一份。
& $diag @common -Action DumpSeries -ServiceName $connectService `
    -Stage login-stall -IntervalSeconds 10
```

Dump 是瞬间快照，不是连续监控；无异常时不要求每场景都抓三份。

### 3.7 本轮结束：停止采样并检查文件

等 L 场景结束，留出清理观察时间，然后 A 按 Ctrl+C 停止 Sample。已写入 CSV 保留；达到 DurationSeconds 也会自动结束。手动中止可能没有 after 身份文件，用 CSV 最后几行确认结束时间。

B 查看本轮产物：

```powershell
$runDirectory = Join-Path $common.OutputRoot "$($common.VersionTag)\$($common.Scenario)\$($common.RunId)\$env:COMPUTERNAME"
Get-ChildItem -LiteralPath $runDirectory
$latestSample = Get-ChildItem -LiteralPath $runDirectory -Filter '*-Sample-*.csv' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestSample) {
    Import-Csv -LiteralPath $latestSample.FullName |
        Select-Object -Last 6 -Property TimestampUtc, Selector, State, ProcessId,
            CpuPercentOfMachine, PrivateBytes, Threads, Handles | Format-Table
}
```

完成归档后，再开始下一轮或下一个场景。

## 4. 切换场景、轮次或优化版时改什么

每轮重复：**修改参数文件 → A/B 分别重新点源加载 → 启动 Sample → 启动该场景 → 按需诊断 → 结束并归档。** 不在上一轮 Sample/Trace 还运行时切换。

| 变化 | 需要修改 | 不会自动发生的事 |
| --- | --- | --- |
| 换场景 | common.Scenario、common.RunId；L 窗口 JSON 路径/RunId | Scenario 不会启动或切换 LoadTester |
| 同场景再跑一轮 | RunId 的 r1 改 r2，各窗口一致 | 复用 RunId 会把多轮文件放在同一目录 |
| 测优化版 | 实际部署优化版；VersionTag=optimized，RunId 同步改名 | VersionTag 不会部署或切换版本 |
| 切到 HTTP | 核对 API/GatherCode/ItemCodes，填写 API PID 或内部名称 | API 不会自动加入三服务列表 |
| 服务/地址变化 | 重查路径、端口、服务名称/PID | 同名 exe 不代表同一个部署实例 |

下一轮只跑“五万连接”，在参数文件改这两项：

```powershell
$common.Scenario = 'gatherv4-connections-50000'
$common.RunId = 'gatherv4-connections-50000-old-r1'
# 版本仍是 old，工具路径和内部服务名未变时继续使用。
```

保存后，A/B 分别重新执行 `. 'D:\GatherDiagnostics\Current-GatherRun.ps1'`。只在 B 临时改变量不会改变 A；下次加载文件又会覆盖临时值。

五万场景需要四台负载机同时运行**同一个场景**的四个分片，不是跑四种场景。四台使用同一完整 CSV、RunId 和未来 UTC 启动时间，索引分别 0～3：

```powershell
# 只在协调机器计算一次，输出该值，再复制到其余三台；不要各自计算。
$startAtUtc = (Get-Date).ToUniversalTime().AddMinutes(5).ToString('yyyy-MM-ddTHH:mm:ssZ')
$startAtUtc

# 四台使用完全相同的 startAtUtc 字符串，只改 shard-index。
& $loadTester run --config 'D:\LoadTester\Scenarios\gatherv4-connections-50000.json' `
    --run-id 'gatherv4-connections-50000-old-r1' --shard-index 0 --shard-count 4 `
    --start-at-utc $startAtUtc
```

## 5. 五个场景分别何时采集

以下在 B 执行，默认已加载**当前场景**参数。Sample 在 A 独立运行；不同时间点命令分开执行，不把整段当自动流程。

### 5.1 一万连接：gatherv4-connections-10000

阶段：functional-100 → ramp-1000 → ramp-10000 → steady-10000 → 冷却。99% 在线连续稳定 60 秒后，保持 1800 秒。

按第 3 节完整示例执行：建连 CPU 60 秒，稳态早期 CPU 60 秒。需要比较变化时，在稳态末尾再采同模式同长度，Stage=steady-late。内存异常时，在稳态早期、末尾、冷却清理后三个时点分别 Dump。

验收：连接/登录成功率均 ≥95%，最终在线率 ≥98%，协议错误率 ≤1%。客户端 F1 确认与服务端业务在线分别观察。

### 5.2 五万连接：gatherv4-connections-50000

阶段：500 → 5000 → 25000 → 50000；steady-50000 在 99% 在线稳定 120 秒后保持 3600 秒。A 的 Sample 可用 14400 秒上限，覆盖屏障、一小时稳态和清理。

| 看到的阶段/现象 | B 动作 |
| --- | --- |
| ramp-25000 开始 | CPU 60 秒，Stage=ramp-25000 |
| ramp-50000 开始 | CPU 60 秒，Stage=ramp-50000 |
| steady-50000 在线屏障通过 | CPU 60 秒，Stage=steady-early |
| 稳态接近结束 | 同模式 CPU 60 秒，Stage=steady-late |
| 已发现 GC/内存压力 | 问题复现轮补 GC，必要时分阶段 Dump |

```powershell
# 当前实际窗口是 ramp-50000 时。
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage ramp-50000 -Mode CPU -DurationSeconds 60

# 已发现 GC 压力后单独执行，不紧接上一条盲目执行。
& $diag @common -Action Trace -ServiceName $connectService `
    -Stage steady-gc -Mode GC -DurationSeconds 300
```

同机多个服务异常时，可用一份整机跟踪分别分析各 PID：

```powershell
& $diag @common -Action Trace -ServiceName $servicesOnThisHost -MachineWide `
    -Stage steady-chain -Mode CPU -DurationSeconds 60
```

与其他 Trace 顺序执行，同机不同时启动多套 PerfView。五万除一万的质量门槛外，还要求二次服务级掉线数为 0。

### 5.3 一万 HTTP：gatherv4-http-reading-10000

先在参数文件填写 API 的实际 PID。API 如果是 Windows 服务，将内部名称加入 servicesOnThisHost，以下命令省去 TargetProcessId。

A 全程记录三服务及 API：

```powershell
& $diag @common -Action Sample -ServiceName $servicesOnThisHost `
    -TargetProcessId $apiProcessId -Stage whole-run -DurationSeconds 14400 -IntervalSeconds 2
```

| HTTP 阶段 | 计划请求 | 目标速率/并发 | 诊断重点 |
| --- | --- | --- | --- |
| functional-sync-20 | 20 台各 1 条，20 条 | 2 RPS / 10 | 功能链路是否完成 |
| steady-sync-1000 | 1000 台各 1 条，1000 条 | 10 RPS / 100 | 同步等待、下发和结果返回 |
| same-device-sync-1000x2 | 1000 台各 2 条，2000 条 | 10 RPS / 100 | 第一条响应后第二条是否及时下发 |

全部同步，客户端超时 90 秒；计划共 3020 条，不等于实际成功数。响应模式 normal，delayMilliseconds=1000 不表示注入一秒延迟。

HTTP 与 TCP 阶段独立协调，观察 `[HTTP阶段开始]`、`[HTTP排空]`，不要只按 command-steady-10000 或固定分钟数猜时机。

| 时机 | 建议动作 |
| --- | --- |
| 在线门槛即将满足、20 条功能段尚未开始 | 整机 CPU 30 秒覆盖短功能段；错过后下一诊断轮重采 |
| steady-sync-1000 请求开始 | 整机 CPU 60 秒 |
| 同设备第二条不下发/同步请求明显等待 | 复现轮整机 Thread 30 秒 |
| 某实例处理不再推进 | 仅对异常实例 DumpSeries |

```powershell
# 当前是 steady-sync-1000 请求窗口；同机服务/API 一份跟踪。
& $diag @common -Action Trace -ServiceName $servicesOnThisHost `
    -TargetProcessId $apiProcessId -MachineWide `
    -Stage steady-sync-1000 -Mode CPU -DurationSeconds 60

# 同设备第二条等待异常的复现窗口，单独执行。
& $diag @common -Action Trace -ServiceName $servicesOnThisHost `
    -TargetProcessId $apiProcessId -MachineWide `
    -Stage same-device-sync-1000x2 -Mode Thread -DurationSeconds 30

# 按现象选对象；这里示例为 API 等待不再推进。
& $diag @common -Action DumpSeries -TargetProcessId $apiProcessId `
    -Stage api-result-wait -IntervalSeconds 10
# 若异常在 Directive/Communication，改为 ServiceName 对应内部名称。
```

HTTP 200 不代表业务成功，适配器按 JSON Status/Msg 判定；Status=false 可能是已入队后的结果超时。F3 应答验证 A3/PSEQ 关联和窗口释放，不能证明真实电量正确落库。

全局验收：API/同步结果成功率 ≥95%、目标覆盖率 ≥98%、命令交付率 ≥95%、识别率 ≥99%、未观察命令 ≤50、API P95 ≤90000ms。各阶段另验收原始/识别交付率 ≥95%、应答率 ≥99%，排空失败终止。90 秒是失败门槛，对比仍报告实际延迟和超时。

### 5.4 一万重启：gatherv4-reconnect-storm-10000

这一轮只跑一万重启场景。A 全程 Sample；L 保持运行。跨重启 Trace 会占用 B，所以人工重启还需要服务器上的**窗口 C**。

| 时机 | 窗口 | 操作 |
| --- | --- | --- |
| steady-before-restart 稳定期间 | B | 可选单进程 CPU 60 秒，保存重启前基线 |
| restart-gatherv4-now 准备提示 | B | 启动下面整机 Trace，核对采集日志已开始 |
| 提示“请立即操作”，处于操作窗口 | C | 只重启已确认的 Communication |
| 恢复一直不推进 | B 跟踪结束后或额外诊断窗口 | 对新进程 DumpSeries，不抓旧 PID |
| recovery-10000 在线屏障通过后 | B | 可选新进程 CPU 60 秒 |

B：

```powershell
& $diag @common -Action Trace -ServiceName $servicesOnThisHost -MachineWide `
    -Stage restart-and-recovery -Mode CPU -DurationSeconds 300 -CircularMB 2048
```

C 先加载同轮参数，**只在 LoadTester 指定操作窗口执行**：

```powershell
. 'D:\GatherDiagnostics\Current-GatherRun.ps1'
$stopRequestedAtUtc = [DateTime]::UtcNow
Stop-Service -Name $connectService -ErrorAction Stop
(Get-Service -Name $connectService).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
# 两版本使用相同策略；示例停止后等待 5 秒。
Start-Sleep -Seconds 5
Start-Service -Name $connectService -ErrorAction Stop
(Get-Service -Name $connectService).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
$serviceRunningAtUtc = [DateTime]::UtcNow
Get-CimInstance Win32_Service | Where-Object Name -eq $connectService |
    Select-Object Name, State, ProcessId
Get-NetTCPConnection -State Listen -LocalPort 8890

$runDirectory = Join-Path $common.OutputRoot "$($common.VersionTag)\$($common.Scenario)\$($common.RunId)\$env:COMPUTERNAME"
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$restartStamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')
[pscustomobject]@{
    StopRequestedAtUtc = $stopRequestedAtUtc
    ServiceRunningAtUtc = $serviceRunningAtUtc
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory "restart-$restartStamp.json") -Encoding UTF8
```

一万：准备 10 秒、操作窗口 60 秒；重启前 100% 在线稳定 60 秒并保持 300 秒。恢复 ≤180 秒、至少 9800 次重连、最终在线 ≥98%、每设备最多 2 次重连、二次掉线 0、恢复后稳定在线至少 120 秒。

只重启目标 Communication，不按 exe 名称批量重启。Service Running、端口可用、业务在线恢复分别记录。操作超时的轮次记录实际情况，不与正常时序混合；不要为了等诊断采集而改变正式重启时序。

### 5.5 五万重启：gatherv4-reconnect-storm-50000

沿用上一节窗口流程，切换本场景参数和四台负载机配置；跨重启 Trace 改为 600 秒：

```powershell
& $diag @common -Action Trace -ServiceName $servicesOnThisHost -MachineWide `
    -Stage restart-and-recovery -Mode CPU -DurationSeconds 600 -CircularMB 2048
```

四台共同运行本场景，仅 shard-000 输出操作提示。准备 20 秒、操作窗口 60 秒；重启前 99% 在线稳定 120 秒并保持 300 秒。恢复 ≤300 秒、至少 49000 次重连，其他稳定性门槛同一万。恢复速率 1000/s 是全局预算，不是每台各 1000/s。

**重启场景必须使用 MachineWide**：新 PID 与旧 PID 不同，旧 PID 的 FocusProcess 不能保证采到新 CLR 事件。整机 ETL 分别选新旧 PID 分析，Sample 通过内部服务名跟随。整机采集不表示同时运行多个压测场景。

## 6. 参数速查与常见问题

| 参数 | 含义 | 默认值/例子 |
| --- | --- | --- |
| Action | 本次只做哪件事 | Inventory / Sample / Trace / Dump / DumpSeries / DumpWatch / Incident |
| ServiceName | 本机 Windows 服务内部 Name，可为数组 | connectService / servicesOnThisHost |
| TargetProcessId | 已确认非服务进程 PID，可为数组，不跟随回收 | HTTP API |
| RunId | 本轮归档标记，Inventory 外必填 | 与 --run-id 一致 |
| Scenario | 五个场景名之一，不带 .json，仅标记 | gatherv4-connections-10000 |
| VersionTag | 版本标签，不执行部署 | old / optimized，默认 old |
| Stage | 当前窗口手工标签，不等待阶段 | whole-run / steady-early |
| DurationSeconds | Sample/Trace 最长运行秒数，或 DumpWatch 全部就绪后的监听上限 | 普通默认 60；DumpWatch 未指定时默认 14400；向导一万连接建议 7200 |
| IntervalSeconds | Sample 间隔或 DumpSeries 每份写完后的等待 | 默认 2，DumpSeries 可用 10 |
| Mode | **只用于 Trace**：CPU/Thread/GC/Alloc | 默认 CPU；线程/GC/分配按需选 |
| MachineWide | **只用于 Trace**：整机一份，多个目标/跨重启必用 | 单个稳定进程可省略 |
| CircularMB | **只用于 Trace**：循环跟踪大小 MB，不保证保留全程 | 默认 1024 |
| OutputRoot | 证据根目录 | D:\GatherEvidence |
| PerfViewPath / ProcDumpPath | 工具实际路径 | 修改为已安装路径 |
| DumpTrigger | DumpWatch 的触发条件 | 默认 ExceptionOrExit；Exception / Exit / Cpu / Memory |
| CpuThresholdPercent | Cpu 触发时该进程 CPU 占整机的百分比，不是单核，也不是全机总和 | 默认 80，范围 1～100，不使用 -u |
| MemoryThresholdMB | Memory 触发的进程提交内存，单位 MB，不是工作集或 GC 堆 | 默认 512，范围 1～1048576 |
| ThresholdSeconds | Cpu/Memory 条件连续满足秒数，其他触发忽略 | 默认 10，范围 1～3600 |
| DumpLimit | 每个 PID 当前生命周期最多转储份数 | 默认 1，范围 1～3 |
| IncidentTargetSelector | 组合诊断的主要转储对象；其他目标只采资源 | service:内部名称 或 pid:数字；多目标必须明确选一个运行对象 |
| IncidentDumpIntervalSeconds | 按 D 后每份 Dump 写完的等待秒数，不是资源间隔 | 默认 10 秒，范围 1～60，共三份 |
| ShowMonitorWindows | 一键模式自动打开资源、PerfView、Dump 三个只读状态窗口，共用本轮采集 | 直接命令行默认不打开；交互选项 8 默认打开，向导加 `-NoMonitorWindows` 仅关闭本次显示 |
| DryRun | 查询目标、预览命令，不采集/写文件 | 首次采集前用一次 |

常见问题：

- **找不到服务**：核对内部 Name，不用 DisplayName/exe；确认服务在当前机器。
- **目标未运行**：Trace/Dump 要求运行；重启后重新确认 API PID。
- **多个目标必须 MachineWide**：同机多个服务用一份整机 Trace，不是多个并行 PerfView。
- **Sample 一直不返回**：它在持续监控；其他命令去 B，结束时 A 按 Ctrl+C。
- **Trace 超过指定秒数**：还要 rundown、合并、压缩；看日志，等返回再启动下一份。
- **产物仍在上一场景目录**：当前窗口没重新加载本轮参数文件。
- **API 回收后采不到**：固定 PID 不跟随回收，重新确认并启动新采样/诊断，保留旧文件。
- **DumpSeries 身份改变**：进程重启了；同组必须来自同一生命周期。
- **Sample/Dump 文件名带 CPU**：文件名包含默认 Mode，只有 Trace 使用 Mode，不代表生成了 CPU 跟踪。
- **ps1 被执行策略拒绝**：按环境规则核对执行策略和下载文件标记；无此错误时不需要改策略。

## 7. 怎么看产物

### 7.1 文件位置和类型

例如：

```text
D:\GatherEvidence\old\gatherv4-connections-10000\gatherv4-connections-10000-old-r1\服务器名\
```

| 文件 | 用什么看 | 能回答什么 |
| --- | --- | --- |
| *-Sample-*.csv | Excel / Import-Csv | CPU、内存、线程、句柄的时间变化 |
| .etl.zip / .etl | PerfView | CPU 热点、线程等待、GC/分配调用链 |
| .dmp | 匹配运行时的 WinDbg/SOS | 当前线程、锁、对象和保留引用 |
| before/after.json | 文本编辑器 | RunId、版本标签、PID/创建时间、路径 |
| .log | 文本编辑器 | PerfView 采集和合并是否有问题 |

CSV 的首个样本、新 PID 首个样本没有 CPU 增量，属于正常情况。PrivateBytes/WorkingSetBytes 是字节，可除以 1MB 显示 MiB。Stopped/Missing/IdentityChanged 行不能当正常零 CPU 样本；手动停止 Sample 没有 after 文件时，CSV 仍可使用。

### 7.2 PerfView

1. 打开本轮 ETL/ZIP，先对照身份 JSON 核对 PID、创建时间和时间窗。
2. CPU Stacks → 选目标 PID → By Name / Call Tree，看 Exclusive、Inclusive 和调用者。
3. CPU 不高但等待：使用 Thread 模式文件中的 Thread Time Stacks，看影响业务推进的等待路径。
4. GCStats 查 GC；Alloc 模式额外查看分配栈。
5. 重启后分别看旧进程、新进程，不把同名 exe 当同一生命周期。

FocusProcess 只缩小非内核事件，内核 CPU 仍涉及整机，分析时还需选 PID。方法符号缺失时补齐部署匹配的 DLL/PDB，保持默认 CLR rundown。检查丢事件和实际保留窗口：循环文件只保留尾段时，不能声称覆盖全程。

### 7.3 Dump

.NET Framework 4.x 在匹配位数和 CLR/DAC/SOS 的 WinDbg 中打开：

```text
.symfix
.reload
.loadby sos clr
!threads
~*e !clrstack
!syncblk
!threadpool
!eeheap -gc
!dumpheap -stat
```

三份快照看线程是否持续停在影响业务的同一路径。内存进一步 `!dumpheap -type 实际类型` → `!do 对象地址` → `!gcroot 对象地址`。崩溃先 `!analyze -v`、`.ecxr`，再看异常栈和 `!pe`。其他 CLR 版本使用相应扩展。

空闲收包等待不等于死锁，工作集不降、Gen2 多不等于泄漏；完整堆也可能包含尚未回收的死对象，需结合对象增长和保留根判断。

## 8. 自动监听：补齐本轮异常停止现场

上轮老版五万场景在 2.5 万阶段失稳，之后服务自行停止，却没有退出 Dump。下一轮诊断复现时，推荐在独立窗口提前启动交互入口 **5：自动监控并抓 Dump**，默认触发 **1：未处理异常或退出**。工具路径使用向导顶部固定配置，全部监听就绪后再开始 LoadTester。

```powershell
& $diag @common -Action DumpWatch -ServiceName $connectService `
    -DumpTrigger ExceptionOrExit -DumpLimit 1 -DurationSeconds 14400 -Stage armed-before-run
```

其他触发方式也可以从向导选择。下面手动示例**选一种**，不要同时对同一 PID 启动多个监听器：

```powershell
# 该进程 CPU 占整机达到 80%，持续 10 秒后抓一份；不是单核或全机 CPU 总和。
& $diag @common -Action DumpWatch -ServiceName $connectService `
    -DumpTrigger Cpu -CpuThresholdPercent 80 -ThresholdSeconds 10 -DumpLimit 1 -DurationSeconds 14400

# 或提交内存达到 512 MB，持续 10 秒；阈值按实际基线调整，不把工作集或 GC 堆当成提交内存。
& $diag @common -Action DumpWatch -ServiceName $connectService `
    -DumpTrigger Memory -MemoryThresholdMB 512 -ThresholdSeconds 10 -DumpLimit 1 -DurationSeconds 14400

# 或仅未处理异常，不捕获所有首次机会异常；正常退出不触发这个模式。
& $diag @common -Action DumpWatch -ServiceName $connectService `
    -DumpTrigger Exception -DumpLimit 1 -DurationSeconds 14400
```

多个 ServiceName/PID 会同时监听，同 PID 去重；每个目标目录单独保存 DMP 和原始输出/错误日志。`*-watch.json` 保存身份、监听状态、监控器 PID、退出码及转储清单。达到份数或当前进程退出后结束该目标；不会追随新 PID，重启和 IIS 回收后重新核对并启动。

Ctrl+C 或到期会按 ProcDump 官方取消协议停止监听，并等待抓取中的目标恢复，不会停止业务进程。也可在另一个窗口按状态 JSON 中的**当前目标 PID**取消：

```powershell
$watch = Get-Content -LiteralPath '填写本轮实际-watch.json完整路径' -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = $watch.Targets[0] # 多目标时选择对应项，不能沿用旧运行的 PID。
$targetProcessId = [int]$entry.ProcessId
$current = Get-CimInstance Win32_Process -Filter "ProcessId=$targetProcessId"
if (-not $current -or $current.CreationDate.ToUniversalTime().ToString('o') -ne $entry.CreatedAtUtc) {
    throw '目标生命周期已改变，请在原监听窗口取消，不使用过期 PID。'
}
& $common.ProcDumpPath -cancel $targetProcessId
```

不要对同一 PID 同时开其他 ProcDump，取消协议会影响该 PID 的全部监听。正常停止和计划重启也会触发 ExceptionOrExit/Exit，不能只凭 DMP 判断崩溃。断电、主机重启等不保证有完整转储。脚本不安装全局事后调试器、不启动或重启服务。

监听结束会尽力导出开始前两分钟至结束后短暂等待的 Application/System 相关事件，CSV 使用 UTC，`*-events.json` 记录空结果、权限错误和最新 10000 条限制。迟到 WER、日志滚动或导出失败仍需按分析报告补导历史事件；空结果不证明没有异常。

上轮 Connect CPU 约为整机 22%，主要竞争来自 AfterSaleApi/TimerTask，因此 Cpu=80% 不保证抓到首轮掉线。先用异常/退出模式，并在资源窗口加入相关服务/API；未退出的业务停滞仍需在出现时手工抓三份 Dump 和 Thread 跟踪。完整 Dump 暂停目标并写磁盘，取证时段归诊断，不纳入正式延迟/恢复对比。后台服务不用窗口无响应条件 -h 判断业务卡死。

## 8.1 old-r2 存活失效：一键布置和按键取证

old-r2 已达到五万在线，但共同全在线样本仅约 71 秒，随后心跳确认停滞、在线归零；Connect PID 17040 在监听结束仍 Running。单靠 ExceptionOrExit 没有触发 Dump。下一轮先完善诊断覆盖，使用向导 **8：一键布置故障取证**，不改变压测场景或老服务配置。

部署时将 `Start-GatherV4Diagnostics.ps1`、`Invoke-GatherV4Diagnostics.ps1`、`GatherV4.IncidentCapture.ps1`、`Watch-GatherV4Monitor.ps1` 放在同一目录，工具路径只维护向导顶部。它需要已提升的管理员 PowerShell，预览不需要；不自动弹出提权。

```powershell
# servicesOnThisHost 包括已核对的 Connect、Directive 和任务中心；停止背景服务也可记录。
# 此处仅 Connect 作为三份 Dump 的主要对象，其他进程只采资源；API PID 另按实际宿主加入。
& $diag @common -Action Incident -ServiceName $servicesOnThisHost `
    -IncidentTargetSelector "service:$connectService" -DurationSeconds 14400 `
    -IntervalSeconds 2 -IncidentDumpIntervalSeconds 10 -CircularMB 2048 -Stage armed-before-run -ShowMonitorWindows
```

退出监听和第一批资源落盘确认后启动本轮唯一 SessionName 的 PerfView Thread 循环会话，显示“全部采集已就绪”，此时再开 LoadTester。同机组合窗口重复启动会被拒绝；不要再单独启动相同 ProcDump 或另一份 PerfView。

交互用途 8 默认自动打开资源、PerfView 和 Dump 三个可见状态窗口，共用本轮身份和证据，不必再维护参数或分别启动监听。它们只读显示，关闭子窗口不停止采集，主窗口结束后自动显示终态并关闭；D/Q 都在主窗口操作。远程只用主窗口可给向导加 `-NoMonitorWindows`；命令行 Incident 只有加 `-ShowMonitorWindows` 才打开显示窗口。

- 异常时按 **D**：标记 UTC 时点，后台停止自己的 ETW 会话并合并；安全取消主要对象的退出监听，核对 PID 和创建时间，再抓三份完整存活 Dump。资源记录覆盖取证过程。
- 正常结束按 **Q**：不额外抓 Dump，归档已有跟踪、资源、身份和事件。
- 主要对象未处理异常/退出：沿用退出监听得到的转储并保存跟踪；退出转储不能据此区分计划重启与崩溃，不对复用 PID 补抓存活 Dump。
- Ctrl+C 或到期：安全结束本轮采集并保留文件；Ctrl+C 不等同于 D。

用户当前没有实时共享目录，业务异常由人看到负载日志后按 D；没有实现自动协议在线/心跳健康判据。远程控制台不能读取按键时，窗口显示带当前随机 SessionId 的本机请求文件命令，可从另一个窗口手工触发；旧轮请求被忽略。

`*-incident.json` 记录就绪、触发、跟踪和文件清单，`*-sample.json` 记录资源实际首末时间；工具失败会标为 Failed，不把没有 DMP 的正常结束写成成功捕获。ETW 循环文件只保证尾部窗口，仍需验证时间范围和丢失事件。完整 Dump 影响目标进程，此模式用于诊断复现；正式版本对比统一采集方式。

跨窗口操作也已缩短：第一个窗口确认时保存本轮元数据、目标定位方式及各用途参数；第二个窗口选用途后选“1 沿用”，重新解析当前 PID 后只核对摘要。目标无法唯一定位时重选；“2 同轮修改”或“3 新轮”用于更换配置。详细问答参见 [交互向导](gatherv4-diagnostics-wizard.md)。

## 9. 同一场景的老版本与优化版怎么比较

对某场景分别测 old 与 optimized，再整理该场景结果，五个场景各自成组。每版本建议至少三轮，按相同阶段计算每轮指标，比较中位数及波动。失败轮也保留，不只挑成功轮。

固定服务器/实例、LoadTester 版本、场景内容、CSV、四分片拓扑、randomSeed、心跳、日志、Redis DB、GatherCode、测试档案、数据库初始状态、连接上限和人工停机策略。记录 UseReadyCommandDispatch / UseCommandProcessingLease 实际组合；若属于优化内容，明确列为对比变量，不混合不同组合。

| 场景 | 对比项目 |
| --- | --- |
| 一万连接 | TCP/F1 登录追平、业务在线、心跳/掉线、30 分钟稳态资源、清理恢复 |
| 五万连接 | 各规模在线追平、一小时稳定、各服务 CPU、每连接内存、资源增长斜率、二次掉线 |
| HTTP | 三阶段分别统计成功/超时、实际速率、P50/P95/P99、交付/识别/应答、排空、同设备第二条、链路 CPU 成本 |
| 一万重启 | 实际停机、启动/监听、TCP/F1/业务恢复、180 秒门槛、重连、恢复后资源 |
| 五万重启 | 四分片全局恢复、300 秒门槛、重连、二次掉线、新进程 CPU/GC |

CPU 采用实际间隔：`CpuPercentOfMachine = CpuDeltaSeconds / IntervalSeconds / LogicalProcessors ×100`。重启累计 CPU 归零，窗口成本累加各生命周期有效 CpuDeltaSeconds；同 PID+创建时间对应多选择器时去重。HTTP 固定窗口 CPU 秒/成功闭环数同时列失败和背景 CPU，不能只看比值。

按项目要求启用 GatherService.OpenTelemetry，由 Collector 保留本 RunId 的 UTC 时间窗。客户端协议在线和服务端 `gather.tcp.bound.connections` 分别核对。老部署缺 OTel 时标明缺失，只比较已有客户端/OS 证据，不填推算值。

LoadTester 的 --server-metrics 仍只解析旧 Gather.TcpCenter JSONL，**不要传 V4 Collector 导出或本脚本 CSV**。这些产物按 RunId 独立归档，对齐 UTC 作附件。

现有报告可生成部分客户端基线对比，发布目录示例：

```powershell
.\scripts\New-GatherLoadTestReport.ps1 `
    -ResultPath '.\load-test-results\gatherv4-http-reading-10000-optimized-r1' `
    -BaselineResultPath '.\load-test-results\gatherv4-http-reading-10000-old-r1' `
    -Audience Both -ServerTimeZone 'China Standard Time' `
    -Title 'GatherV4 一万在线同步指令改造前后对比'
```

自动比较不覆盖全部 V4 延迟、恢复和 OS/OTel，需补齐上表。归档客户端分片报告/时间序列，HTTP 命令追踪/目标/错误/分类，OTel、CSV、身份 JSON、ETL/日志、异常 Dump、版本及脱敏配置。五万必须确认连接上限高于目标。

## 10. 范围与验证说明

当前正式场景只有一万/五万连接、一万 HTTP、一万/五万重启。全部关闭主动业务上报，没有 GatherV4 五万 HTTP、六小时长稳或协议故障专项。在线屏障耗时额外计入运行时间，不按阶段秒数相加猜采集时机。心跳均为 60 秒、抖动 10 秒、响应超时 10 秒。

固定场景验证既定规模和 HTTP 10 RPS 预算，不直接等于最大容量；F3 关联确认不等于电量落库正确。按项目兼容说明确认覆盖边界。

交互入口支持固定工具路径与自动 Dump 监听；新增 `DumpWatch`，不会立即转储。自动监听验证覆盖触发参数、PID 身份变化、启动失败、退出/阈值抓取、多个目标和安全取消；真实集成验证只针对脚本创建的短期辅助进程，没有监控、停止或重启测试业务服务。脚本和指南命令块经过语法检查，实际服务器工具版本、实例及权限仍需现场确认。

回归验证脚本为 `scripts/tests/Test-GatherV4DumpWatch.ps1`。不传参数只跑预览、身份及启动失败验证；传 `-ProcDumpPath '实际完整路径'` 才对其创建的辅助进程进行真实转储验证，默认清理自己创建的临时目录。正式测试服务器只需部署两个采集脚本，回归脚本不是运行依赖。

参数依据：[PerfView 官方定义](https://github.com/microsoft/perfview/blob/main/src/PerfView/CommandLineArgs.cs)、[ProcDump 官方文档](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)、[.NET Framework SOS](https://learn.microsoft.com/en-us/dotnet/framework/tools/sos-dll-sos-debugging-extension)。
