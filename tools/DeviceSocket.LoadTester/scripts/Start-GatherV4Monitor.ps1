<#
.SYNOPSIS
用一个窗口记录 GatherV4 相关进程资源、服务状态和人工时间标记，结束后自动归档。
.DESCRIPTION
本脚本是压测期间的日常监控入口，只读取本机进程、Windows 服务和监听端口，不启动或停止业务服务，
也不启动 PerfView、ProcDump，不读取 RabbitMQ、Redis 或 GatherV4 业务指标。
目标在 GatherV4.Monitor.config.psd1 中维护一次；运行期间按 M 添加时间标记，按 Q 正常结束。
.PARAMETER RunId
与 LoadTester 相同的本轮标识，只允许英文字母、数字、点、下划线和横线。
.PARAMETER Scenario
当前压测场景标签，仅用于证据目录和时间线，默认五万设备人工指令场景。
.PARAMETER ConfigPath
固定监控目标配置，默认使用同目录 GatherV4.Monitor.config.psd1。
.PARAMETER OutputRoot
证据根目录；为空时使用配置中的 OutputRoot。
.PARAMETER DurationSeconds
最长监控秒数；零表示使用配置值。交互运行可提前按 Q 结束。
.PARAMETER IntervalSeconds
资源采样间隔秒数；零表示使用配置值，长时间压测建议 5～10 秒。
.PARAMETER NonInteractive
关闭按键读取，达到 DurationSeconds 后自动结束；供计划任务和自动测试使用。
.PARAMETER DryRun
只校验配置并解析一次目标，不创建证据目录。
.EXAMPLE
.\Start-GatherV4Monitor.ps1 -RunId gatherv4-manual-command-50000-r2
.EXAMPLE
.\Start-GatherV4Monitor.ps1 -RunId gatherv4-reconnect-storm-50000-r2 -Scenario gatherv4-reconnect-storm-50000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$RunId,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$Scenario = 'gatherv4-manual-command-50000',
    [string]$ConfigPath = '',
    [string]$OutputRoot = '',
    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,
    [ValidateRange(0, 60)]
    [int]$IntervalSeconds = 0,
    [switch]$NonInteractive,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
读取并校验轻量监控配置，返回启用的唯一目标。
.PARAMETER Path
PowerShell 数据文件完整路径。
.OUTPUTS
包含全局默认值和启用目标的配置对象。
#>
function Read-MonitorConfiguration {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "监控配置不存在：$Path" }
    $configuration = Import-PowerShellDataFile -LiteralPath $Path
    if (-not $configuration.ContainsKey('Targets')) { throw '监控配置缺少 Targets。' }
    $targets = @($configuration.Targets | Where-Object { -not $_.ContainsKey('Enabled') -or $_.Enabled })
    if ($targets.Count -eq 0) { throw '监控配置没有启用任何目标；请至少启用一个 Service、Port 或 ProcessId。' }
    $names = @{}
    foreach ($target in $targets) {
        foreach ($field in @('Name', 'Type', 'Value')) {
            if (-not $target.ContainsKey($field) -or [string]::IsNullOrWhiteSpace([string]$target[$field])) {
                throw "监控目标缺少 $field：$($target | ConvertTo-Json -Compress)"
            }
        }
        if ($target.Type -notin @('Service', 'Port', 'ProcessId')) {
            throw "监控目标 $($target.Name) 的 Type 只允许 Service、Port 或 ProcessId。"
        }
        if ($names.ContainsKey([string]$target.Name)) { throw "监控目标名称重复：$($target.Name)" }
        $names[[string]$target.Name] = $true
        if ($target.Type -in @('Port', 'ProcessId')) {
            $number = 0
            if (-not [int]::TryParse([string]$target.Value, [ref]$number) -or $number -le 0) {
                throw "监控目标 $($target.Name) 的 $($target.Type) 必须为正整数。"
            }
            if ($target.Type -eq 'Port' -and $number -gt 65535) { throw "监听端口超出范围：$number" }
        }
    }
    return [pscustomobject]@{
        OutputRoot = $(if ($configuration.OutputRoot) { [string]$configuration.OutputRoot } else { 'D:\GatherEvidence' })
        DurationSeconds = $(if ($configuration.DurationSeconds) { [int]$configuration.DurationSeconds } else { 7200 })
        IntervalSeconds = $(if ($configuration.IntervalSeconds) { [int]$configuration.IntervalSeconds } else { 5 })
        Targets = $targets
    }
}

<#
.SYNOPSIS
根据精确服务名、监听端口或固定 PID 解析当前进程；不使用模糊进程名猜测实例。
.PARAMETER Target
配置中的单个监控目标。
.PARAMETER Services
本轮采样预先读取的 Windows 服务列表。
.OUTPUTS
统一的目标状态、服务状态和当前进程身份。
#>
function Resolve-MonitorTarget {
    param([hashtable]$Target, [object[]]$Services)

    $processId = 0
    $state = 'Missing'
    $serviceState = ''
    $detail = ''
    try {
        switch ($Target.Type) {
            'Service' {
                $service = $Services | Where-Object Name -eq ([string]$Target.Value)
                if (@($service).Count -gt 1) { throw "服务内部名称不唯一：$($Target.Value)" }
                if (-not $service) { $detail = 'Windows 服务不存在'; break }
                $serviceState = [string]$service.State
                if ($service.State -eq 'Running' -and [int]$service.ProcessId -gt 0) {
                    $processId = [int]$service.ProcessId
                    $state = 'Running'
                }
                else {
                    $state = 'Stopped'
                    $detail = "服务状态=$serviceState"
                }
            }
            'Port' {
                # LocalPort 无匹配时 Windows PowerShell 5.1 会把“未找到对象”当成错误；先读监听集合再过滤，才能正确记录 NotListening。
                $owners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
                    Where-Object LocalPort -eq ([int]$Target.Value) |
                    Select-Object -ExpandProperty OwningProcess -Unique)
                if ($owners.Count -eq 1) {
                    $processId = [int]$owners[0]
                    $state = 'Running'
                }
                elseif ($owners.Count -eq 0) {
                    $state = 'NotListening'
                    $detail = "端口 $($Target.Value) 未监听"
                }
                else {
                    $state = 'Ambiguous'
                    $detail = "端口 $($Target.Value) 对应多个 PID：$($owners -join ',')"
                }
            }
            'ProcessId' {
                $processId = [int]$Target.Value
                $state = 'Running'
            }
        }
        $process = $null
        if ($processId -gt 0) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
            if (-not $process) {
                $state = 'Missing'
                $detail = "PID $processId 不存在"
                $processId = 0
            }
        }
        return [pscustomobject]@{
            Name = [string]$Target.Name
            SelectorType = [string]$Target.Type
            SelectorValue = [string]$Target.Value
            State = $state
            ServiceState = $serviceState
            Detail = $detail
            Process = $process
        }
    }
    catch {
        return [pscustomobject]@{
            Name = [string]$Target.Name
            SelectorType = [string]$Target.Type
            SelectorValue = [string]$Target.Value
            State = 'Error'
            ServiceState = $serviceState
            Detail = $_.Exception.Message
            Process = $null
        }
    }
}

<#
.SYNOPSIS
追加一条统一 UTC 时间线事件，并保留到内存供摘要统计。
.PARAMETER Path
事件 CSV 路径。
.PARAMETER Events
本轮内存事件集合。
.PARAMETER Type
SessionStarted、InitialState、StateChanged、Marker 或 SessionEnded。
.PARAMETER Target
关联目标名称；会话事件使用空字符串。
.PARAMETER Previous
变化前状态。
.PARAMETER Current
变化后状态。
.PARAMETER Message
面向操作人员的简短说明。
#>
function Add-MonitorEvent {
    param(
        [string]$Path,
        [Collections.Generic.List[object]]$Events,
        [string]$Type,
        [string]$Target,
        [string]$Previous,
        [string]$Current,
        [string]$Message
    )

    $event = [pscustomobject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString('o')
        RunId = $RunId
        Scenario = $Scenario
        ComputerName = $env:COMPUTERNAME
        Type = $Type
        Target = $Target
        Previous = $Previous
        Current = $Current
        Message = $Message
    }
    $Events.Add($event)
    $event | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8 -Append
}

<#
.SYNOPSIS
根据资源时间序列和状态事件生成机器可读 JSON 与通俗中文摘要。
.PARAMETER Rows
本轮全部资源采样行。
.PARAMETER Events
本轮全部时间线事件。
.PARAMETER Directory
会话证据目录。
.PARAMETER SessionState
Completed、Cancelled 或 Failed。
.PARAMETER StartedAtUtc
本轮开始 UTC 时间。
.PARAMETER EndedAtUtc
本轮结束 UTC 时间。
#>
function Write-MonitorSummary {
    param(
        [Collections.Generic.List[object]]$Rows,
        [Collections.Generic.List[object]]$Events,
        [string]$Directory,
        [string]$SessionState,
        [string]$StartedAtUtc,
        [string]$EndedAtUtc
    )

    $targetSummaries = @()
    foreach ($group in @($Rows | Group-Object Target)) {
        $running = @($group.Group | Where-Object State -eq 'Running')
        $targetEvents = @($Events | Where-Object { $_.Target -eq $group.Name -and $_.Type -eq 'StateChanged' })
        $targetSummaries += [pscustomobject]@{
            Target = $group.Name
            Samples = $group.Count
            RunningSamples = $running.Count
            UnavailableSamples = $group.Count - $running.Count
            StateChanges = $targetEvents.Count
            PeakCpuPercent = $(if ($running.Count) { [Math]::Round([double](($running | Measure-Object CpuPercentOfMachine -Maximum).Maximum), 2) } else { $null })
            PeakPrivateBytes = $(if ($running.Count) { [long](($running | Measure-Object PrivateBytes -Maximum).Maximum) } else { $null })
            PeakWorkingSetBytes = $(if ($running.Count) { [long](($running | Measure-Object WorkingSetBytes -Maximum).Maximum) } else { $null })
            PeakThreads = $(if ($running.Count) { [int](($running | Measure-Object Threads -Maximum).Maximum) } else { $null })
            PeakHandles = $(if ($running.Count) { [int](($running | Measure-Object Handles -Maximum).Maximum) } else { $null })
            FinalState = [string]$group.Group[-1].State
            FinalProcessId = $group.Group[-1].ProcessId
        }
    }
    $summary = [ordered]@{
        SchemaVersion = 1
        RunId = $RunId
        Scenario = $Scenario
        ComputerName = $env:COMPUTERNAME
        State = $SessionState
        StartedAtUtc = $StartedAtUtc
        EndedAtUtc = $EndedAtUtc
        SampleRows = $Rows.Count
        EventCount = $Events.Count
        Targets = $targetSummaries
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Directory 'summary.json') -Encoding UTF8

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("# GatherV4 服务器轻量监控摘要")
    $lines.Add('')
    $lines.Add("- RunId：``$RunId``")
    $lines.Add("- 场景：``$Scenario``")
    $lines.Add("- 服务器：``$env:COMPUTERNAME``")
    $lines.Add("- 状态：$SessionState")
    $lines.Add("- UTC 时间：$StartedAtUtc 至 $EndedAtUtc")
    $lines.Add('')
    $lines.Add('| 目标 | 采样 | 不可用 | 状态变化 | CPU 峰值 | Private 峰值 MiB | Working Set 峰值 MiB | 线程/句柄峰值 | 最终状态 |')
    $lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |')
    foreach ($item in $targetSummaries) {
        $privateMiB = $(if ($null -ne $item.PeakPrivateBytes) { [Math]::Round($item.PeakPrivateBytes / 1MB, 1) } else { '-' })
        $workingMiB = $(if ($null -ne $item.PeakWorkingSetBytes) { [Math]::Round($item.PeakWorkingSetBytes / 1MB, 1) } else { '-' })
        $cpu = $(if ($null -ne $item.PeakCpuPercent) { "$($item.PeakCpuPercent)%" } else { '-' })
        $lines.Add("| $($item.Target) | $($item.Samples) | $($item.UnavailableSamples) | $($item.StateChanges) | $cpu | $privateMiB | $workingMiB | $($item.PeakThreads)/$($item.PeakHandles) | $($item.FinalState) |")
    }
    $lines.Add('')
    $lines.Add('说明：本摘要只描述本机进程资源和服务状态，不包含 RabbitMQ、Redis 或 GatherV4 业务指标。')
    $lines | Set-Content -LiteralPath (Join-Path $Directory 'summary.md') -Encoding UTF8
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    # Windows PowerShell 5.1 在参数默认值绑定阶段可能尚未提供 PSScriptRoot，必须进入脚本后再解析同目录配置。
    $ConfigPath = Join-Path $PSScriptRoot 'GatherV4.Monitor.config.psd1'
}
$configuration = Read-MonitorConfiguration -Path $ConfigPath
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = $configuration.OutputRoot }
if ($DurationSeconds -eq 0) { $DurationSeconds = $configuration.DurationSeconds }
if ($IntervalSeconds -eq 0) { $IntervalSeconds = $configuration.IntervalSeconds }
if ($DurationSeconds -lt 1 -or $DurationSeconds -gt 86400) { throw 'DurationSeconds 必须为 1～86400。' }
if ($IntervalSeconds -lt 1 -or $IntervalSeconds -gt 60) { throw 'IntervalSeconds 必须为 1～60。' }

$services = @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue)
$resolved = @($configuration.Targets | ForEach-Object { Resolve-MonitorTarget -Target $_ -Services $services })
if ($DryRun) {
    Write-Host "配置有效：$ConfigPath" -ForegroundColor Green
    $resolved | Select-Object Name, SelectorType, SelectorValue, State, ServiceState, @{ Name = 'ProcessId'; Expression = { if ($_.Process) { $_.Process.ProcessId } } }, Detail | Format-Table -AutoSize
    return
}

$mutex = New-Object Threading.Mutex($false, 'Local\GatherV4SimpleMonitor')
$ownsMutex = $false
try { $ownsMutex = $mutex.WaitOne(0) }
catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
if (-not $ownsMutex) { $mutex.Dispose(); throw '本机已有一个轻量监控窗口正在运行，请先结束原窗口。' }

$sessionStamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss')
$sessionDirectory = Join-Path $OutputRoot "$RunId\server-monitor\$env:COMPUTERNAME-$sessionStamp"
New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
$resourcePath = Join-Path $sessionDirectory 'process-timeline.csv'
$eventPath = Join-Path $sessionDirectory 'events.csv'
$archivePath = "$sessionDirectory.zip"
Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $sessionDirectory 'monitor-config.psd1')

$logicalProcessors = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$rows = [Collections.Generic.List[object]]::new()
$events = [Collections.Generic.List[object]]::new()
$previousCpu = @{}
$previousState = @{}
$startedAtUtc = [DateTime]::UtcNow.ToString('o')
$sessionState = 'Cancelled'
$failure = $null
$stopRequested = $false
$stopReason = '达到时长上限'
$sequence = 0
$clock = [Diagnostics.Stopwatch]::StartNew()

Add-MonitorEvent -Path $eventPath -Events $events -Type 'SessionStarted' -Target '' -Previous '' -Current 'Running' -Message "间隔 $IntervalSeconds 秒，最长 $DurationSeconds 秒"
Write-Host "`nGatherV4 轻量监控已启动" -ForegroundColor Green
Write-Host "RunId：$RunId"
Write-Host "场景：$Scenario"
Write-Host "输出：$sessionDirectory"
Write-Host '只记录进程资源和服务状态，不操作业务服务，不启动 PerfView/ProcDump。'
if (-not $NonInteractive) { Write-Host '运行期间：按 M 添加时间标记，按 Q 正常结束。' -ForegroundColor Cyan }

try {
    while (-not $stopRequested -and $clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $sequence++
        $sampleAtUtc = [DateTime]::UtcNow
        $services = @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue)
        $batch = foreach ($target in $configuration.Targets) {
            $current = Resolve-MonitorTarget -Target $target -Services $services
            $process = $current.Process
            $identity = ''
            $cpuTotal = $null
            $cpuPercent = $null
            if ($process) {
                $identity = "$($process.ProcessId):$($process.CreationDate.ToUniversalTime().Ticks)"
                $cpuTotal = ([double]$process.KernelModeTime + [double]$process.UserModeTime) / 10000000
                $last = $previousCpu[$current.Name]
                if ($last -and $last.Identity -eq $identity) {
                    $elapsed = ($sampleAtUtc - $last.TimestampUtc).TotalSeconds
                    if ($elapsed -gt 0) {
                        $delta = [Math]::Max(0.0, [double]($cpuTotal - $last.CpuTotal))
                        $cpuPercent = 100 * $delta / $elapsed / $logicalProcessors
                    }
                }
                $previousCpu[$current.Name] = @{ Identity = $identity; TimestampUtc = $sampleAtUtc; CpuTotal = $cpuTotal }
            }
            else { $previousCpu.Remove($current.Name) }

            $stateKey = "$($current.State)|$(if ($process) { $process.ProcessId })|$identity|$($current.Detail)"
            if (-not $previousState.ContainsKey($current.Name)) {
                Add-MonitorEvent -Path $eventPath -Events $events -Type 'InitialState' -Target $current.Name -Previous '' -Current $stateKey -Message $current.Detail
            }
            elseif ($previousState[$current.Name] -ne $stateKey) {
                Add-MonitorEvent -Path $eventPath -Events $events -Type 'StateChanged' -Target $current.Name -Previous $previousState[$current.Name] -Current $stateKey -Message $current.Detail
                Write-Host "[$($sampleAtUtc.ToString('HH:mm:ss'))] $($current.Name)：$($previousState[$current.Name]) -> $stateKey" -ForegroundColor Yellow
            }
            $previousState[$current.Name] = $stateKey

            [pscustomobject]@{
                TimestampUtc = $sampleAtUtc.ToString('o')
                Sequence = $sequence
                RunId = $RunId
                Scenario = $Scenario
                ComputerName = $env:COMPUTERNAME
                Target = $current.Name
                SelectorType = $current.SelectorType
                SelectorValue = $current.SelectorValue
                State = $current.State
                ServiceState = $current.ServiceState
                ProcessId = $(if ($process) { $process.ProcessId } else { $null })
                CreatedAtUtc = $(if ($process) { $process.CreationDate.ToUniversalTime().ToString('o') } else { $null })
                CpuTotalSeconds = $cpuTotal
                CpuPercentOfMachine = $cpuPercent
                PrivateBytes = $(if ($process) { $process.PrivatePageCount } else { $null })
                WorkingSetBytes = $(if ($process) { $process.WorkingSetSize } else { $null })
                Threads = $(if ($process) { $process.ThreadCount } else { $null })
                Handles = $(if ($process) { $process.HandleCount } else { $null })
                Detail = $current.Detail
            }
        }
        foreach ($row in @($batch)) { $rows.Add($row) }
        @($batch) | Export-Csv -LiteralPath $resourcePath -NoTypeInformation -Encoding UTF8 -Append

        $status = (@($batch) | ForEach-Object { "$($_.Target)=$($_.State)" }) -join '  '
        $percent = [Math]::Min(100, [int](100 * $clock.Elapsed.TotalSeconds / $DurationSeconds))
        Write-Progress -Activity "GatherV4 轻量监控：$RunId" -Status $status -PercentComplete $percent

        $wait = [Diagnostics.Stopwatch]::StartNew()
        while (-not $stopRequested -and $wait.Elapsed.TotalSeconds -lt $IntervalSeconds -and $clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
            if (-not $NonInteractive) {
                try {
                    if ([Console]::KeyAvailable) {
                        $key = [Console]::ReadKey($true).Key
                        if ($key -eq [ConsoleKey]::Q) {
                            $stopRequested = $true
                            $stopReason = '操作人员按 Q 正常结束'
                        }
                        elseif ($key -eq [ConsoleKey]::M) {
                            $message = Read-Host '输入时间标记，例如“开始全覆盖”或“首次超时”'
                            if (-not [string]::IsNullOrWhiteSpace($message)) {
                                Add-MonitorEvent -Path $eventPath -Events $events -Type 'Marker' -Target '' -Previous '' -Current '' -Message $message.Trim()
                                Write-Host "已记录标记：$message" -ForegroundColor Cyan
                            }
                        }
                    }
                }
                catch [InvalidOperationException] {
                    # 不支持 Console 键盘读取的宿主仍可持续采样，通过 Ctrl+C 或时长结束。
                }
            }
            Start-Sleep -Milliseconds 200
        }
    }
    $sessionState = 'Completed'
}
catch {
    $failure = $_
    if ($_.Exception -is [Management.Automation.PipelineStoppedException] -or $_.Exception -is [OperationCanceledException]) {
        $sessionState = 'Cancelled'
        $stopReason = '操作人员中断'
    }
    else {
        $sessionState = 'Failed'
        $stopReason = $_.Exception.Message
    }
}
finally {
    Write-Progress -Activity "GatherV4 轻量监控：$RunId" -Completed
    $endedAtUtc = [DateTime]::UtcNow.ToString('o')
    try {
        Add-MonitorEvent -Path $eventPath -Events $events -Type 'SessionEnded' -Target '' -Previous 'Running' -Current $sessionState -Message $stopReason
        Write-MonitorSummary -Rows $rows -Events $events -Directory $sessionDirectory -SessionState $sessionState -StartedAtUtc $startedAtUtc -EndedAtUtc $endedAtUtc
        [ordered]@{
            SchemaVersion = 1
            RunId = $RunId
            Scenario = $Scenario
            ComputerName = $env:COMPUTERNAME
            State = $sessionState
            StartedAtUtc = $startedAtUtc
            EndedAtUtc = $endedAtUtc
            IntervalSeconds = $IntervalSeconds
            DurationSeconds = $DurationSeconds
            ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
            ResourceTimeline = $resourcePath
            EventTimeline = $eventPath
            Scope = 'ProcessResourcesAndServiceStateOnly'
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sessionDirectory 'manifest.json') -Encoding UTF8
        Compress-Archive -Path (Join-Path $sessionDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
        Write-Host "`n监控已结束：$sessionState" -ForegroundColor Green
        Write-Host "摘要：$(Join-Path $sessionDirectory 'summary.md')"
        Write-Host "归档：$archivePath"
    }
    catch {
        Write-Warning "监控证据归档失败：$($_.Exception.Message)；未压缩文件仍保留在 $sessionDirectory"
        if (-not $failure) { $failure = $_ }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

if ($failure -and $sessionState -eq 'Failed') { throw $failure }
