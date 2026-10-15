<#
.SYNOPSIS
为现有 GatherV4 正式场景保存实例身份、PerfView、完整 Dump 或进程资源时间序列。
.DESCRIPTION
在被测 Windows 服务器的管理员 PowerShell 中运行。Windows 服务使用内部名称定位；
IIS 或非服务 API 使用已核实的 PID。Sample 会重新解析服务 PID；重启跟踪必须使用 MachineWide。
脚本不会启动压测或重启服务。Dump 工具参数按 Sysinternals ProcDump 编写。
.PARAMETER Action
Inventory 列出候选实例；Trace 采集 PerfView；Dump 保存一份完整转储；
DumpSeries 保存三份；DumpWatch 等待条件触发；Sample 记录资源；Incident 同时布置资源、线程跟踪与退出监听，按 D 抓三份存活 Dump。
.PARAMETER ServiceName
Win32_Service.Name 的精确内部名称，可指定同一机器的多个实例；不接受 exe 名称作为模糊匹配。
.PARAMETER TargetProcessId
已核实的 API 等非服务进程 PID；不会自动跟随重启或 PID 复用。
.PARAMETER Mode
CPU 为默认采集；Thread 记录线程时间；GC 使用 GCOnly；Alloc 使用托管分配采样；Incident 固定使用整机 Thread。
.PARAMETER MachineWide
不设置 FocusProcess，覆盖同机多个服务以及服务重启产生的新 PID；必须按身份记录筛选分析。
.PARAMETER DurationSeconds
Trace 或 Sample 持续秒数默认 60；DumpWatch 和 Incident 的上限未填写时为 14400 秒；循环跟踪不保证保留整个时间窗口。
.PARAMETER IntervalSeconds
Sample 的采样间隔或 DumpSeries 每份完成后的间隔，单位秒，默认 2。
.PARAMETER CircularMB
PerfView 循环跟踪大小，单位 MB，默认 1024；事件很多时应检查实际保留时间范围。
.PARAMETER RunId
与 LoadTester 同轮一致的 RunId；除 Inventory 外必须填写。
.PARAMETER VersionTag
old 或 optimized，表示 GatherV4 改造前后版本，默认 old。
.PARAMETER Scenario
现有场景名；不带 .json 扩展名，不会改变场景配置。
.PARAMETER Stage
手工标记当前采集阶段，例如 steady-10000、same-device-sync-1000x2。
.PARAMETER OutputRoot
证据根目录，默认 D:\GatherEvidence；按版本、场景、RunId、服务器分目录。
.PARAMETER PerfViewPath
已安装 PerfView.exe 的路径，默认 C:\Tools\PerfView\PerfView.exe。
.PARAMETER ProcDumpPath
已安装 ProcDump 的路径，默认 C:\Tools\ProcDump\procdump64.exe。
.PARAMETER DumpTrigger
DumpWatch 触发方式，默认 ExceptionOrExit：未处理异常或退出；也可选 Exception、Exit、Cpu、Memory。
.PARAMETER CpuThresholdPercent
Cpu 触发的目标进程 CPU 占整机百分比，范围 1～100，默认 80；不采用单核口径，也不是整机所有进程之和。
.PARAMETER MemoryThresholdMB
Memory 触发的进程提交内存阈值，单位 MB，默认 512；不是 Working Set 或托管堆大小。
.PARAMETER ThresholdSeconds
Cpu/Memory 条件连续满足的秒数，默认 10；异常及退出模式不使用此参数。
.PARAMETER DumpLimit
DumpWatch 每个目标当前生命周期的最多转储份数，范围 1～3，默认 1；达到数量后结束该目标监听。
.PARAMETER IncidentTargetSelector
Incident 的主要转储对象，格式为 service:内部名称 或 pid:数字；其他目标只记录资源。多目标时必须明确填写。
.PARAMETER IncidentDumpIntervalSeconds
Incident 三份存活 Dump 每份写完后的等待秒数，默认 10，范围 1～60；与资源采样间隔分开。
.PARAMETER ShowMonitorWindows
Incident 自动打开资源、PerfView、Dump 三个只读状态窗口；命令行默认关闭，交互向导默认开启。窗口不启动新的采集器。
.PARAMETER ExpectedCreatedAtUtc
组合采集内部传递的单目标创建时间；不匹配即拒绝采集，防止子任务开始前发生 PID 复用。
.PARAMETER DryRun
仅解析目标并打印采集命令；不启动采集，也不创建证据文件。
.EXAMPLE
.\Invoke-GatherV4Diagnostics.ps1 -Action Trace -ServiceName '实际服务内部名称' -RunId v4-old-r1 -Scenario gatherv4-connections-10000 -Stage steady-10000 -DryRun
#>
[CmdletBinding()]
param(
    [ValidateSet('Inventory', 'Trace', 'Dump', 'DumpSeries', 'DumpWatch', 'Sample', 'Incident')]
    [string]$Action = 'Inventory',
    [string[]]$ServiceName = @(),
    [int[]]$TargetProcessId = @(),
    [ValidateSet('CPU', 'Thread', 'GC', 'Alloc')]
    [string]$Mode = 'CPU',
    [switch]$MachineWide,
    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 60,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 2,
    [ValidateRange(64, 32768)]
    [int]$CircularMB = 1024,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$RunId,
    [ValidateSet('old', 'optimized')]
    [string]$VersionTag = 'old',
    [ValidateSet('gatherv4-connections-10000', 'gatherv4-connections-50000',
        'gatherv4-http-reading-10000', 'gatherv4-reconnect-storm-10000',
        'gatherv4-reconnect-storm-50000')]
    [string]$Scenario = 'gatherv4-connections-10000',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$Stage = 'manual',
    [string]$OutputRoot = 'D:\GatherEvidence',
    [string]$PerfViewPath = 'C:\Tools\PerfView\PerfView.exe',
    [string]$ProcDumpPath = 'C:\Tools\ProcDump\procdump64.exe',
    [ValidateSet('ExceptionOrExit', 'Exception', 'Exit', 'Cpu', 'Memory')]
    [string]$DumpTrigger = 'ExceptionOrExit',
    [ValidateRange(1, 100)]
    [int]$CpuThresholdPercent = 80,
    [ValidateRange(1, 1048576)]
    [int]$MemoryThresholdMB = 512,
    [ValidateRange(1, 3600)]
    [int]$ThresholdSeconds = 10,
    [ValidateRange(1, 3)]
    [int]$DumpLimit = 1,
    [string]$IncidentTargetSelector = '',
    [ValidateRange(1, 60)]
    [int]$IncidentDumpIntervalSeconds = 10,
    [switch]$ShowMonitorWindows,
    [string]$ExpectedCreatedAtUtc = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ($Action -eq 'Inventory') {
    $candidates = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('GatherService.ConnectService.exe', 'GatherService.DirectiveService.exe',
            'GatherService.TimerTaskService.exe', 'w3wp.exe', 'dotnet.exe') -or
        $_.Name -match 'IssueWebApi'
    })
    $candidates | Select-Object Name, ProcessId, CreationDate, ExecutablePath, CommandLine | Format-List
    Get-CimInstance Win32_Service | Where-Object {
        $_.ProcessId -ne 0 -and $_.ProcessId -in $candidates.ProcessId
    } | Select-Object Name, DisplayName, State, ProcessId, PathName | Format-List
    Write-Host 'IIS/dotnet/IssueWebApi 是候选列表，请按应用池、路径和端口确认具体 API 实例。'
    return
}

if ([string]::IsNullOrWhiteSpace($RunId)) { throw '必须填写与 LoadTester 同轮一致的 RunId。' }
if ($ServiceName.Count + $TargetProcessId.Count -eq 0) { throw '必须指定 ServiceName 或 TargetProcessId。' }
if (@($ServiceName | Select-Object -Unique).Count -ne $ServiceName.Count -or
    @($TargetProcessId | Select-Object -Unique).Count -ne $TargetProcessId.Count) {
    throw '目标列表中存在重复名称或 PID。'
}
if ($ServiceName | Where-Object { [string]::IsNullOrWhiteSpace($_) }) { throw '服务内部名称不能为空。' }
if ($TargetProcessId | Where-Object { $_ -le 0 }) { throw '目标 PID 必须为正整数。' }
if ($MachineWide -and $Action -notin @('Trace', 'Incident')) { throw 'MachineWide 仅适用于 Trace 或 Incident。' }
if ($Action -in @('DumpWatch', 'Incident') -and -not $PSBoundParameters.ContainsKey('DurationSeconds')) { $DurationSeconds = 14400 }
if ($Action -eq 'Incident') { $Mode = 'Thread'; $MachineWide = $true }

$evidenceDirectory = Join-Path $OutputRoot "$VersionTag\$Scenario\$RunId\$env:COMPUTERNAME"
$captureStamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')
$captureKind = $(if ($Action -eq 'DumpWatch') { $DumpTrigger } else { $Mode })
$captureBase = Join-Path $evidenceDirectory "$Stage-$Action-$captureKind-$captureStamp"
$pinnedProcesses = @{}
$logicalProcessors = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors

<#
.SYNOPSIS
按精确服务名解析当前实例，并防止显式 PID 被其他进程复用。
.PARAMETER AllowStopped
采样时允许停止状态，保留缺失记录；采集及转储必须要求目标运行。
.OUTPUTS
目标选择器、服务状态、进程对象和 UTC 身份信息组成的记录数组。
#>
function Get-TargetRecords {
    param([switch]$AllowStopped)

    $records = @()
    if ($ServiceName.Count -gt 0) { $services = @(Get-CimInstance Win32_Service) }
    foreach ($name in $ServiceName) {
        $service = $services | Where-Object Name -eq $name
        if (-not $service) { throw "未找到服务内部名称：$name" }
        $process = $null
        if ($service.State -eq 'Running' -and $service.ProcessId -gt 0) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($service.ProcessId)"
        }
        if (-not $process -and -not $AllowStopped) { throw "服务未运行或进程已退出：$name" }
        $records += [pscustomobject]@{ Selector = "service:$name"; State = $service.State; Process = $process }
    }
    foreach ($processNumber in $TargetProcessId) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processNumber"
        $state = 'Missing'
        if ($process) {
            $state = 'Running'
            if (-not $pinnedProcesses.ContainsKey($processNumber)) {
                $pinnedProcesses[$processNumber] = $process.CreationDate
            }
            elseif ($pinnedProcesses[$processNumber] -ne $process.CreationDate) {
                $state = 'IdentityChanged'
                $process = $null
            }
        }
        if (-not $process -and -not $AllowStopped) { throw "目标 PID 已退出或身份改变：$processNumber" }
        $records += [pscustomobject]@{ Selector = "pid:$processNumber"; State = $state; Process = $process }
    }
    return $records
}

<#
.SYNOPSIS
保存本轮身份和采集参数，使用 UTC 对齐 LoadTester 与 Collector。
.PARAMETER Records
当前目标记录，包括已停止服务，以便重启前后映射新旧 PID。
.PARAMETER Label
文件标记，通常为 before 或 after。
#>
function Save-Identity {
    param([object[]]$Records, [string]$Label)
    if ($DryRun) { return }
    $identities = @($Records | ForEach-Object {
        $process = $_.Process
        [pscustomobject]@{
            Selector = $_.Selector; State = $_.State
            ProcessId = $(if ($process) { $process.ProcessId } else { $null })
            CreatedAtUtc = $(if ($process) { $process.CreationDate.ToUniversalTime().ToString('o') } else { $null })
            Name = $(if ($process) { $process.Name } else { $null })
            ExecutablePath = $(if ($process) { $process.ExecutablePath } else { $null })
            CommandLine = $(if ($process) { $process.CommandLine } else { $null })
        }
    })
    [pscustomobject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString('o'); RunId = $RunId; Version = $VersionTag
        Scenario = $Scenario; Stage = $Stage; ComputerName = $env:COMPUTERNAME
        LogicalProcessors = $logicalProcessors; Action = $Action; Mode = $Mode
        MachineWide = [bool]$MachineWide; DurationSeconds = $DurationSeconds
        IntervalSeconds = $IntervalSeconds; CircularMB = $CircularMB; Targets = $identities
        DumpTrigger = $DumpTrigger; DumpLimit = $DumpLimit; CpuThresholdPercent = $CpuThresholdPercent
        MemoryThresholdMB = $MemoryThresholdMB; ThresholdSeconds = $ThresholdSeconds
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$captureBase-$Label.json" -Encoding UTF8
}

<#
.SYNOPSIS
打印并执行采集器；PerfView 等待真正退出，避免 GUI 程序尚未合并就开始下一轮。
.PARAMETER ToolPath
已安装采集工具的完整路径。
.PARAMETER ToolArguments
独立参数数组；PerfView 通过 Start-Process 逐项加引号以支持带空格的路径。
.PARAMETER IsPerfView
PerfView 使用隐藏窗口并等待退出；ProcDump 在当前控制台运行，便于 Ctrl+C 取消。
#>
function Invoke-Collector {
    param([string]$ToolPath, [string[]]$ToolArguments, [switch]$IsPerfView)
    if (-not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "工具不存在：$ToolPath" }
    if ($ToolArguments | Where-Object { $_ -match '["\r\n]' }) { throw '工具参数不能包含双引号或换行。' }
    Write-Host ("& '{0}' {1}" -f $ToolPath.Replace("'", "''"),
        (($ToolArguments | ForEach-Object { "'{0}'" -f $_.Replace("'", "''") }) -join ' '))
    if ($DryRun) { return }
    if ($IsPerfView) {
        $quotedArguments = ($ToolArguments | ForEach-Object { '"{0}"' -f $_ }) -join ' '
        $collector = Start-Process -FilePath $ToolPath -ArgumentList $quotedArguments `
            -WindowStyle Hidden -PassThru -Wait
        if ($collector.ExitCode -ne 0) { throw "PerfView 失败，退出码 $($collector.ExitCode)，请检查采集日志。" }
    }
    else {
        & $ToolPath @ToolArguments
        # ProcDump 12 正常写出一份转储时可返回 1；实际文件还会在转储分支校验，不能只按非零判失败。
        if ($LASTEXITCODE -notin @(0, 1)) { throw "ProcDump 失败或已取消，退出码 $LASTEXITCODE。" }
    }
}

<#
.SYNOPSIS
生成自动转储的独立参数，CPU 使用整机归一口径，异常模式不抓首次机会异常。
.PARAMETER ProcessNumber
执行前核实的目标 PID，禁止使用同名 exe 自动匹配。
.PARAMETER DumpDirectory
本次监听专属的输出目录，由 ProcDump 为每份文件自动命名。
.OUTPUTS
包含完整转储、触发条件、数量限制和明确 PID 的参数数组。
#>
function Get-DumpWatchArguments {
    param([int]$ProcessNumber, [string]$DumpDirectory)
    $result = @('-accepteula', '-ma')
    switch ($DumpTrigger) {
        'ExceptionOrExit' { $result += @('-e', '-t') }
        'Exception' { $result += '-e' }
        'Exit' { $result += '-t' }
        'Cpu' { $result += @('-c', "$CpuThresholdPercent", '-s', "$ThresholdSeconds") }
        'Memory' { $result += @('-m', "$MemoryThresholdMB", '-s', "$ThresholdSeconds") }
    }
    return $result + @('-n', "$DumpLimit", "$ProcessNumber", $DumpDirectory)
}

<#
.SYNOPSIS
读取正在写入的日志片段，兼容 ProcDump 的 UTF-16 输出以及旧版 UTF-8/ASCII 输出。
.PARAMETER Path
监控器的标准输出或错误日志路径。
.PARAMETER Tail
读取末尾片段；默认读取开头以确认监控是否已经挂接。
.OUTPUTS
最多 16 KB 的可读文本，不把长期监控的整个日志加载到内存。
#>
function Read-DumpWatchLog {
    param([string]$Path, [switch]$Tail)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $count = [int][Math]::Min(16384L, $stream.Length)
        if ($Tail -and $stream.Length -gt $count) { $stream.Position = $stream.Length - $count }
        $buffer = New-Object byte[] $count
        $read = $stream.Read($buffer, 0, $count)
        if ($read -eq 0) { return '' }
        $zeroCount = @($buffer | Where-Object { $_ -eq 0 }).Count
        $encoding = $(if ($zeroCount -gt $read / 5) { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 })
        return $encoding.GetString($buffer, 0, $read).Trim([char]0xFEFF)
    }
    finally { $stream.Dispose() }
}

<#
.SYNOPSIS
启动隐藏的 ProcDump，并以原始字节读取两个输出管道，避免中文路径因控制台编码丢失或管道阻塞。
.PARAMETER Worker
持有当前 PID、命令参数、专属目录及互斥锁的监听状态。
#>
function Start-DumpWatchWorker {
    param([object]$Worker)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $ProcDumpPath
    $info.Arguments = ($Worker.Arguments | ForEach-Object { '"{0}"' -f $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $Worker.Collector = [Diagnostics.Process]::Start($info)
    $Worker.CollectorId = $Worker.Collector.Id
    $Worker.Pipes = @()
    foreach ($entry in @(
        @{ Stream = $Worker.Collector.StandardOutput.BaseStream; Path = $Worker.StdOut },
        @{ Stream = $Worker.Collector.StandardError.BaseStream; Path = $Worker.StdErr }
    )) {
        $buffer = New-Object byte[] 4096
        $writer = [IO.File]::Open($entry.Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $Worker.Pipes += [pscustomobject]@{
            Stream = $entry.Stream; Buffer = $buffer; Writer = $writer
            Task = $entry.Stream.ReadAsync($buffer, 0, $buffer.Length); Closed = $false
        }
    }
}

<#
.SYNOPSIS
排空已完成的异步输出读取并刷新日志；每轮有读取上限，兼顾多个监听目标。
.PARAMETER Worker
已经启动的监听器及两个原始输出管道。
#>
function Receive-DumpWatchOutput {
    param([object]$Worker)
    foreach ($pipe in $Worker.Pipes) {
        $reads = 0
        while (-not $pipe.Closed -and $pipe.Task.IsCompleted -and $reads -lt 256) {
            $count = $pipe.Task.GetAwaiter().GetResult()
            if ($count -eq 0) { $pipe.Closed = $true; break }
            $pipe.Writer.Write($pipe.Buffer, 0, $count)
            $pipe.Task = $pipe.Stream.ReadAsync($pipe.Buffer, 0, $pipe.Buffer.Length)
            $reads++
        }
        $pipe.Writer.Flush()
    }
}

<#
.SYNOPSIS
监听器退出后继续读取管道至 EOF，避免把尚在异步缓冲中的完成日志误判为缺失。
.PARAMETER Worker
已经退出的监听器及输出管道。
#>
function Complete-DumpWatchOutput {
    param([object]$Worker)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (@($Worker.Pipes | Where-Object { -not $_.Closed }).Count -gt 0 -and $clock.Elapsed.TotalSeconds -lt 5) {
        Receive-DumpWatchOutput -Worker $Worker
        [Threading.Thread]::Sleep(10)
    }
}

<#
.SYNOPSIS
结合退出码和完成日志判断监听结束；ProcDump 写出文件后的正退出码不能直接作为失败。
.PARAMETER Worker
已经退出的监听器，输出管道应先读取到日志。
.OUTPUTS
零退出码，或返回转储份数且完成日志及文件数量一致时返回真；其余返回假。
#>
function Test-DumpWatchCompletion {
    param([object]$Worker)
    if ($Worker.ExitCode -eq 0) { return $true }
    # 真实 ProcDump 验证中，成功写一份返回 1、两份返回 2；必须同时核对完成日志和文件，防止接受错误码。
    if ($Worker.ExitCode -ge 1 -and $Worker.ExitCode -le $DumpLimit) {
        $log = Read-DumpWatchLog -Path $Worker.StdOut -Tail
        $completed = [regex]::Matches($log, 'Dump \d+ complete:').Count
        $files = @((Get-ChildItem -LiteralPath $Worker.Directory -Filter '*.dmp' -File) | Where-Object Length -gt 32).Count
        return $files -ge $Worker.ExitCode -and ($completed -ge $Worker.ExitCode -or $log -match 'Dump count reached\.')
    }
    return $false
}

<#
.SYNOPSIS
保存监听状态、身份、日志位置和转储清单，区分正常到期、捕获完成与监听失败。
.PARAMETER Workers
本轮每个唯一 PID 对应的监听记录。
.PARAMETER State
整轮监听状态，例如 Armed、Completed、TimedOut、Failed 或 Cancelled。
.PARAMETER StartedAtUtc
本轮监听开始的 UTC 时间。
#>
function Save-DumpWatchState {
    param([object[]]$Workers, [string]$State, [datetime]$StartedAtUtc)
    [pscustomobject]@{
        RunId = $RunId; Version = $VersionTag; Scenario = $Scenario; Stage = $Stage
        StartedAtUtc = $StartedAtUtc.ToString('o'); UpdatedAtUtc = [DateTime]::UtcNow.ToString('o')
        State = $State; DumpTrigger = $DumpTrigger; DurationSeconds = $DurationSeconds; DumpLimit = $DumpLimit
        Targets = @($Workers | ForEach-Object {
            [pscustomobject]@{
                ProcessId = $_.Target.ProcessId; CreatedAtUtc = $_.Target.CreationDate.ToUniversalTime().ToString('o')
                ExecutablePath = $_.Target.ExecutablePath; Selectors = $_.Selectors; Armed = $_.Armed
                CollectorId = $_.CollectorId
                ExitCode = $_.ExitCode; CancelRequested = $_.CancelRequested; Directory = $_.Directory
                StdOut = $_.StdOut; StdErr = $_.StdErr
                Dumps = @(Get-ChildItem -LiteralPath $_.Directory -Filter '*.dmp' -File | Select-Object FullName, Length, LastWriteTimeUtc)
            }
        })
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath "$captureBase-watch.json" -Encoding UTF8
}

<#
.SYNOPSIS
结束后导出监听时间窗内的程序异常与服务事件；日志读取失败单独记录，不覆盖主采集结果。
.PARAMETER StartedAtUtc
监听开始时间；导出窗口向前扩展两分钟，以保留启动前的相关事件。
#>
function Export-DumpWatchEvents {
    param([datetime]$StartedAtUtc)
    $manifest = @()
    $endUtc = [DateTime]::UtcNow
    foreach ($logName in @('Application', 'System')) {
        $path = "$captureBase-$logName-events.csv"
        try {
            $events = @(Get-WinEvent -FilterHashtable @{
                LogName = $logName; StartTime = $StartedAtUtc.AddMinutes(-2).ToLocalTime(); EndTime = $endUtc.ToLocalTime()
            } -MaxEvents 10000 -ErrorAction Stop)
            $selected = @($events | Where-Object {
                $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting', 'Service Control Manager') -or
                $_.Message -match 'Gather|HPSocket|AfterSaleApi'
            } | Select-Object @{Name = 'TimestampUtc'; Expression = { $_.TimeCreated.ToUniversalTime().ToString('o') }},
                Id, ProviderName, LevelDisplayName, Message)
            if ($selected.Count -gt 0) { $selected | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8 }
            else { '"TimestampUtc","Id","ProviderName","LevelDisplayName","Message"' | Set-Content -LiteralPath $path -Encoding UTF8 }
            $manifest += @{ Log = $logName; Status = 'Exported'; Count = $selected.Count; LimitReached = ($events.Count -eq 10000); Path = $path }
            if ($events.Count -eq 10000) { Write-Warning "$logName 事件达到 10000 条上限，较早事件可能未导出。" }
        }
        catch {
            $empty = $_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*'
            $manifest += @{ Log = $logName; Status = $(if ($empty) { 'Empty' } else { 'Failed' }); Error = $_.Exception.Message; Path = $path }
            if (-not $empty) { Write-Warning "无法导出 $logName 事件：$($_.Exception.Message)" }
        }
    }
    @{ StartedAtUtc = $StartedAtUtc.AddMinutes(-2).ToString('o'); EndedAtUtc = $endUtc.ToString('o'); Logs = $manifest } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$captureBase-events.json" -Encoding UTF8
}

<#
.SYNOPSIS
并行监听各个明确 PID，启动后确认挂接；到期或 Ctrl+C 时使用 ProcDump 的取消协议恢复目标。
.PARAMETER Records
已经核实的服务或固定 PID 记录；同一 PID 去重，只监控当前生命周期。
#>
function Invoke-DumpWatch {
    param([object[]]$Records)
    if (-not (Test-Path -LiteralPath $ProcDumpPath -PathType Leaf)) { throw "工具不存在：$ProcDumpPath" }
    $workers = @(); $startedAt = [DateTime]::UtcNow; $state = 'Cancelled'
    try {
        foreach ($group in @($Records | Group-Object { $_.Process.ProcessId })) {
            $target = $group.Group[0].Process
            $directory = "$captureBase-watch-$($target.ProcessId)"
            $arguments = @(Get-DumpWatchArguments -ProcessNumber $target.ProcessId -DumpDirectory $directory)
            if ($arguments | Where-Object { $_ -match '["\r\n]' }) { throw '工具参数不能包含双引号或换行。' }
            if ($DryRun) { Invoke-Collector -ToolPath $ProcDumpPath -ToolArguments $arguments; continue }
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $worker = [pscustomobject]@{
                Target = $target; Selectors = @($group.Group.Selector); Arguments = $arguments; Directory = $directory
                StdOut = "$directory\procdump.stdout.log"; StdErr = "$directory\procdump.stderr.log"
                Collector = $null; CollectorId = $null; Pipes = @(); Armed = $false; ExitCode = $null; CancelRequested = $false
                Mutex = $null; OwnsMutex = $false
            }
            $workers += $worker
            # 全局互斥覆盖同机不同终端会话，防止重复监听同一生命周期及取消其他向导的监听。
            $mutexName = "Global\GatherV4DumpWatch-$($target.ProcessId)-$($target.CreationDate.ToUniversalTime().Ticks)"
            $worker.Mutex = [Threading.Mutex]::new($false, $mutexName)
            try { $worker.OwnsMutex = $worker.Mutex.WaitOne(0) }
            catch [Threading.AbandonedMutexException] { $worker.OwnsMutex = $true }
            if (-not $worker.OwnsMutex) { throw "PID $($target.ProcessId) 已在另一个向导中监控，请勿重复启动。" }
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($target.ProcessId)"
            if (-not $current -or $current.CreationDate -ne $target.CreationDate) { throw '启动监听前目标已退出或 PID 身份变化，请重新选择。' }
            $existing = @(Get-CimInstance Win32_Process | Where-Object {
                $_.Name -match '^procdump(?:64|64a)?\.exe$' -and
                $_.CommandLine -match ('(?:^|\s|")' + $target.ProcessId + '(?=\s|"|$)') -and $_.CommandLine -notmatch '\s-cancel\s'
            })
            if ($existing.Count -gt 0) { throw "PID $($target.ProcessId) 已有 ProcDump 进程监听，请先在原监控窗口正常取消，不能重复启动。" }
            Start-DumpWatchWorker -Worker $worker
            Write-Host "正在挂接 PID $($target.ProcessId)，监控器 PID=$($worker.Collector.Id)，日志：$($worker.StdOut)"
        }
        if ($DryRun) { Write-Host "自动转储预览：$DumpTrigger，最多监听 $DurationSeconds 秒，每个 PID 最多 $DumpLimit 份；未启动监听。"; return }
        $startup = [Diagnostics.Stopwatch]::StartNew()
        while (@($workers | Where-Object { -not $_.Armed }).Count -gt 0) {
            foreach ($worker in $workers) {
                Receive-DumpWatchOutput -Worker $worker
                if (-not $worker.Armed -and (Read-DumpWatchLog -Path $worker.StdOut) -match 'Press Ctrl-C to end monitoring') {
                    $worker.Armed = $true
                    Write-Host "监听就绪：PID $($worker.Target.ProcessId)，触发方式 $DumpTrigger。" -ForegroundColor Green
                }
                if ($worker.Collector.HasExited) {
                    Complete-DumpWatchOutput -Worker $worker
                    $worker.ExitCode = $worker.Collector.ExitCode
                    if (-not (Test-DumpWatchCompletion -Worker $worker) -or -not $worker.Armed) { throw "ProcDump 启动失败，PID $($worker.Target.ProcessId)，退出码 $($worker.ExitCode)。$(Read-DumpWatchLog -Path $worker.StdErr -Tail) $(Read-DumpWatchLog -Path $worker.StdOut -Tail)" }
                }
            }
            if ($startup.Elapsed.TotalSeconds -ge 30) { throw '30 秒内未确认 ProcDump 监听就绪，请检查监控日志和安装版本。' }
            Start-Sleep -Milliseconds 200
        }
        Save-DumpWatchState -Workers $workers -State Armed -StartedAtUtc $startedAt
        Write-Host '全部监听已就绪，现在可在负载窗口开始本轮压测。Ctrl+C 会取消监听；不会停止业务进程。' -ForegroundColor Green
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
            foreach ($worker in $workers) {
                Receive-DumpWatchOutput -Worker $worker
                if ($null -eq $worker.ExitCode -and $worker.Collector.HasExited) {
                    Complete-DumpWatchOutput -Worker $worker
                    $worker.ExitCode = $worker.Collector.ExitCode
                    if (-not (Test-DumpWatchCompletion -Worker $worker)) { throw "PID $($worker.Target.ProcessId) 的 ProcDump 异常结束，退出码 $($worker.ExitCode)。$(Read-DumpWatchLog -Path $worker.StdErr -Tail) $(Read-DumpWatchLog -Path $worker.StdOut -Tail)" }
                    $dumps = @(Get-ChildItem -LiteralPath $worker.Directory -Filter '*.dmp' -File)
                    Write-Host "PID $($worker.Target.ProcessId) 监听结束，已生成 $($dumps.Count) 份 Dump：$($worker.Directory)"
                    Save-DumpWatchState -Workers $workers -State Monitoring -StartedAtUtc $startedAt
                }
            }
            if (@($workers | Where-Object { $null -eq $_.ExitCode }).Count -eq 0) { $state = 'Completed'; break }
            Start-Sleep -Milliseconds 500
        }
        if ($state -ne 'Completed') { $state = 'TimedOut'; Write-Host '已达到监听时长上限，开始取消剩余监听。' }
    }
    catch {
        if ($_.Exception -is [Management.Automation.PipelineStoppedException] -or $_.Exception -is [OperationCanceledException]) { $state = 'Cancelled' }
        else { $state = 'Failed' }
        throw
    }
    finally {
        if (-not $DryRun) {
            foreach ($worker in $workers) {
                try {
                    if ($worker.Collector -and -not $worker.Collector.HasExited) {
                        $worker.CancelRequested = $true
                        # 使用官方取消协议；不能 Kill 监控器，否则抓取中暂停的业务进程可能无法恢复。
                        $info = New-Object Diagnostics.ProcessStartInfo
                        $info.FileName = $ProcDumpPath; $info.Arguments = "-cancel $($worker.Target.ProcessId)"
                        $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                        $cancel = [Diagnostics.Process]::Start($info)
                        $cancel.WaitForExit(5000) | Out-Null
                        $cancel.Dispose()
                        $grace = [Diagnostics.Stopwatch]::StartNew()
                        $warned = $false
                        while (-not $worker.Collector.HasExited) {
                            try { Receive-DumpWatchOutput -Worker $worker }
                            catch { if (-not $warned) { Write-Warning "监控日志读取失败，仍等待取消完成：$($_.Exception.Message)"; $warned = $true } }
                            $worker.Collector.WaitForExit(200) | Out-Null
                            if (-not $warned -and $grace.Elapsed.TotalSeconds -ge 30) {
                                Write-Warning "已请求取消 PID $($worker.Target.ProcessId) 的监听，仍在完成转储；继续等待以恢复业务进程，勿强制杀死监控器。"
                                $warned = $true
                            }
                        }
                    }
                    if ($worker.Collector -and $worker.Collector.HasExited) {
                        $worker.ExitCode = $worker.Collector.ExitCode
                        Complete-DumpWatchOutput -Worker $worker
                    }
                }
                catch { Write-Warning "监听清理失败，PID $($worker.Target.ProcessId)：$($_.Exception.Message)" }
                finally {
                    foreach ($pipe in $worker.Pipes) { $pipe.Writer.Dispose() }
                    if ($worker.Collector -and $worker.Collector.HasExited) { $worker.Collector.Dispose() }
                    if ($worker.OwnsMutex) { $worker.Mutex.ReleaseMutex() }
                    if ($worker.Mutex) { $worker.Mutex.Dispose() }
                }
            }
            try { Save-DumpWatchState -Workers $workers -State $state -StartedAtUtc $startedAt }
            catch { Write-Warning "监听状态归档失败：$($_.Exception.Message)" }
            try { Save-Identity -Records @(Get-TargetRecords -AllowStopped) -Label after }
            catch { Write-Warning "结束身份归档失败：$($_.Exception.Message)" }
            # WER 等退出事件可能稍晚于转储写完；给事件日志短暂刷新机会，不把缺失事件当成没有异常。
            if (@($workers | Where-Object Collector).Count -gt 0) { [Threading.Thread]::Sleep(2000) }
            try { Export-DumpWatchEvents -StartedAtUtc $startedAt }
            catch { Write-Warning "事件日志归档失败：$($_.Exception.Message)" }
            Write-Host "自动监听记录：$captureBase-watch.json"
        }
    }
}

$targets = @(Get-TargetRecords -AllowStopped:($Action -in @('Sample', 'Incident')))
if ($ExpectedCreatedAtUtc) {
    $expectedCreation = [DateTimeOffset]::Parse($ExpectedCreatedAtUtc).UtcDateTime
    if ($targets.Count -ne 1 -or -not $targets[0].Process -or
        $targets[0].Process.CreationDate.ToUniversalTime() -ne $expectedCreation) {
        throw '目标创建时间与本轮确认不一致，拒绝采集复用 PID。'
    }
}
if ($Action -eq 'Trace' -and -not $MachineWide -and $targets.Count -ne 1) {
    throw '多个目标必须使用 MachineWide，同机只执行一套 PerfView 跟踪。'
}
if (-not $DryRun) { New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null }
Save-Identity -Records $targets -Label before

switch ($Action) {
    'Incident' {
        $incidentLibrary = Join-Path $PSScriptRoot 'GatherV4.IncidentCapture.ps1'
        if (-not (Test-Path -LiteralPath $incidentLibrary -PathType Leaf)) { throw '缺少 GatherV4.IncidentCapture.ps1，请复制完整采集脚本包。' }
        . $incidentLibrary
        Invoke-IncidentCapture -Records $targets -CollectorPath $PSCommandPath
    }
    'DumpWatch' { Invoke-DumpWatch -Records $targets }
    'Trace' {
        $arguments = @('/AcceptEula', '/NoGui', '/NoView', "/MaxCollectSec:$DurationSeconds",
            "/CircularMB:$CircularMB", '/Merge:true', '/Zip:true', "/LogFile:$captureBase.log")
        if (-not $MachineWide) { $arguments += "/FocusProcess:$($targets[0].Process.ProcessId)" }
        switch ($Mode) {
            'Thread' { $arguments += '/ThreadTime' }
            'GC' { $arguments += '/GCOnly' }
            'Alloc' { $arguments += '/DotNetAllocSampled' }
        }
        $arguments += @('collect', "$captureBase.etl")
        Invoke-Collector -ToolPath $PerfViewPath -ToolArguments $arguments -IsPerfView
        Save-Identity -Records @(Get-TargetRecords -AllowStopped) -Label after
    }
    { $_ -in 'Dump', 'DumpSeries' } {
        $count = $(if ($Action -eq 'DumpSeries') { 3 } else { 1 })
        for ($index = 1; $index -le $count; $index++) {
            # 同一组转储只能比较同一个进程生命周期，重启后必须重新启动采集。
            $current = @(Get-TargetRecords)
            foreach ($target in $targets) {
                $now = $current | Where-Object Selector -eq $target.Selector
                if ($now.Process.ProcessId -ne $target.Process.ProcessId -or
                    $now.Process.CreationDate -ne $target.Process.CreationDate) {
                    throw '转储序列期间进程已重启，请重新确认目标。'
                }
            }
            foreach ($process in @($targets.Process | Sort-Object ProcessId -Unique)) {
                $dump = "$captureBase-$($process.ProcessId)-$index.dmp"
                Invoke-Collector -ToolPath $ProcDumpPath -ToolArguments @('-accepteula', '-ma', "$($process.ProcessId)", $dump)
                if (-not $DryRun -and (-not (Test-Path -LiteralPath $dump -PathType Leaf) -or (Get-Item -LiteralPath $dump).Length -le 32)) {
                    throw "ProcDump 没有生成有效转储文件：$dump；请检查工具输出。"
                }
            }
            if ($index -lt $count -and -not $DryRun) { Start-Sleep -Seconds $IntervalSeconds }
        }
        Save-Identity -Records @(Get-TargetRecords -AllowStopped) -Label after
    }
    'Sample' {
        if ($DryRun) { Write-Host "资源采样预览：$DurationSeconds 秒，间隔 $IntervalSeconds 秒，输出 $captureBase.csv"; return }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $previous = @{}
        $sampleState = 'Cancelled'; $sampleCount = 0; $firstAt = $null; $lastAt = $null
        try {
            while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
                $rows = foreach ($target in @(Get-TargetRecords -AllowStopped)) {
                    $process = $target.Process
                    $sampleAt = $clock.Elapsed.TotalSeconds
                    $cpuDelta = $null
                    $elapsed = $null
                    $cpuPercent = $null
                    $cpuTotal = $null
                    $identity = $null
                    if ($process) {
                        $identity = "$($process.ProcessId):$($process.CreationDate.ToUniversalTime().Ticks)"
                        $cpuTotal = ([double]$process.KernelModeTime + [double]$process.UserModeTime) / 10000000
                        $last = $previous[$target.Selector]
                        # CPU 累计时间在重启后归零，不能跨 PID 或创建时间求差。
                        if ($last -and $last.Identity -eq $identity) {
                            $elapsed = $sampleAt - $last.At
                            # 显式使用 Double 重载，避免 PowerShell 把小数秒舍入为整数，导致低负载 CPU 被记录为零。
                            $cpuDelta = [Math]::Max(0.0, [double]($cpuTotal - $last.Cpu))
                            if ($elapsed -gt 0) { $cpuPercent = 100 * $cpuDelta / $elapsed / $logicalProcessors }
                        }
                        $previous[$target.Selector] = @{ Identity = $identity; At = $sampleAt; Cpu = $cpuTotal }
                    }
                    else { $previous.Remove($target.Selector) }
                    [pscustomobject]@{
                        TimestampUtc = [DateTime]::UtcNow.ToString('o'); RunId = $RunId
                        Version = $VersionTag; Scenario = $Scenario; Stage = $Stage
                        ComputerName = $env:COMPUTERNAME; Selector = $target.Selector; State = $target.State
                        ProcessId = $(if ($process) { $process.ProcessId } else { $null })
                        CreatedAtUtc = $(if ($process) { $process.CreationDate.ToUniversalTime().ToString('o') } else { $null })
                        LogicalProcessors = $logicalProcessors; IntervalSeconds = $elapsed
                        CpuTotalSeconds = $cpuTotal; CpuDeltaSeconds = $cpuDelta; CpuPercentOfMachine = $cpuPercent
                        PrivateBytes = $(if ($process) { $process.PrivatePageCount } else { $null })
                        WorkingSetBytes = $(if ($process) { $process.WorkingSetSize } else { $null })
                        Threads = $(if ($process) { $process.ThreadCount } else { $null })
                        Handles = $(if ($process) { $process.HandleCount } else { $null })
                    }
                }
                $rows | Export-Csv -LiteralPath "$captureBase.csv" -NoTypeInformation -Encoding UTF8 -Append
                $sampleCount++
                $lastAt = [DateTime]::UtcNow.ToString('o')
                if (-not $firstAt) {
                    $firstAt = $lastAt
                    # 第一批 CSV 落盘后才宣告就绪，组合采集据此避免先开压测后开资源记录。
                    @{ State = 'Ready'; FirstSampleUtc = $firstAt; SampleBatches = $sampleCount; Csv = "$captureBase.csv" } |
                        ConvertTo-Json | Set-Content -LiteralPath "$captureBase-sample.json" -Encoding UTF8
                    Write-Host "资源记录已就绪：$captureBase.csv" -ForegroundColor Green
                }
                $remaining = $DurationSeconds - $clock.Elapsed.TotalSeconds
                if ($remaining -gt 0) {
                    Start-Sleep -Milliseconds ([int]([Math]::Min($IntervalSeconds, $remaining) * 1000))
                }
            }
            $sampleState = 'Completed'
        }
        catch {
            if ($_.Exception -isnot [Management.Automation.PipelineStoppedException] -and $_.Exception -isnot [OperationCanceledException]) { $sampleState = 'Failed' }
            throw
        }
        finally {
            @{ State = $sampleState; FirstSampleUtc = $firstAt; LastSampleUtc = $lastAt; SampleBatches = $sampleCount; Csv = "$captureBase.csv" } |
                ConvertTo-Json | Set-Content -LiteralPath "$captureBase-sample.json" -Encoding UTF8
            try { Save-Identity -Records @(Get-TargetRecords -AllowStopped) -Label after }
            catch { Write-Warning "结束资源身份归档失败：$($_.Exception.Message)" }
            Write-Host "资源时间序列：$captureBase.csv"
        }
    }
}
