<#
.SYNOPSIS
显示本轮资源、PerfView 或 Dump 状态；由一键监控自动打开，只读文件，不启动或结束采集器。
.PARAMETER StatePath
主窗口本次生成的 incident.json 完整路径。
.PARAMETER ExpectedSessionId
本次随机会话标识，必须与状态文件一致，避免显示其他运行的证据。
.PARAMETER Panel
Resources 显示最新资源，Trace 显示 PerfView 状态及日志，Dump 显示监听和转储进度。
.PARAMETER RefreshSeconds
显示刷新间隔，默认 2 秒，允许 1～30 秒。
.PARAMETER Once
只输出一次快照，不清屏、不等待，供远程查看或验证使用。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StatePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{32}$')][string]$ExpectedSessionId,
    [Parameter(Mandatory = $true)][ValidateSet('Resources', 'Trace', 'Dump')][string]$Panel,
    [ValidateRange(1, 30)][int]$RefreshSeconds = 2,
    [switch]$Once
)
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
读取完整的原子状态快照，允许主窗口替换文件；短暂写入或共享占用时下次再读。
.OUTPUTS
当前状态对象，或暂时不可读时为空；会话标识错误必须报错。
#>
function Read-MonitorState {
    $stream = $null; $reader = $null
    try {
        $stream = [IO.File]::Open($StatePath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
        $state = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
    }
    catch { return $null }
    finally { if ($reader) { $reader.Dispose() } elseif ($stream) { $stream.Dispose() } }
    if ($state.SessionId -ne $ExpectedSessionId) { throw '会话标识不同，拒绝显示其他运行。' }
    return $state
}

<#
.SYNOPSIS
读取最多 64 KB 日志或 CSV 片段，兼容 UTF-8 与 ProcDump UTF-16，不阻止采集器继续写文件。
.PARAMETER Path
只读证据路径；尚未生成时返回空文本。
.PARAMETER Tail
读取尾段；跳过截断的第一行，只显示完整记录。
.OUTPUTS
可读文本；共享占用或文件尚未写出时为空。
#>
function Read-MonitorText {
    param([string]$Path, [switch]$Tail)
    if (-not $Path) { return '' }
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $probe = New-Object byte[] ([int][Math]::Min(256L, $stream.Length))
        $probeCount = $stream.Read($probe, 0, $probe.Length)
        if ($probeCount -eq 0) { return '' }
        $zeros = 0
        for ($index = 0; $index -lt $probeCount; $index++) { if ($probe[$index] -eq 0) { $zeros++ } }
        $encoding = $(if ($zeros -gt $probeCount / 5) { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 })
        $offset = $(if ($Tail) { [Math]::Max(0L, $stream.Length - 65536L) } else { 0L })
        # UTF-16 必须按双字节边界解码；尾段截断行不会作为有效 CSV 行。
        if ($encoding -eq [Text.Encoding]::Unicode -and $offset % 2 -ne 0) { $offset-- }
        $stream.Position = $offset
        $buffer = New-Object byte[] ([int][Math]::Min(65536L, $stream.Length - $offset))
        $count = $stream.Read($buffer, 0, $buffer.Length)
        $text = $encoding.GetString($buffer, 0, $count).TrimStart([char]0xFEFF)
        if ($offset -gt 0) {
            $newline = $text.IndexOf("`n")
            if ($newline -lt 0) { return '' }
            $text = $text.Substring($newline + 1)
        }
        return $text
    }
    catch { return '' }
    finally { if ($stream) { $stream.Dispose() } }
}

<#
.SYNOPSIS
按选择器展示最近完整资源行，保留 PID 与采样时刻，不把没有新采样当成零 CPU。
.PARAMETER Path
本次资源 CSV 路径。
.OUTPUTS
便于控制台阅读的最新资源表和实际采样时间。
#>
function Get-MonitorResources {
    param([string]$Path)
    $head = Read-MonitorText -Path $Path
    $tail = Read-MonitorText -Path $Path -Tail
    if (-not $head -or -not $tail) { return '等待第一批资源记录。' }
    $header = ($head -split '\r?\n')[0]
    # 采样器按行写 CSV；只接收已有换行符的行，防止字段齐全但数值尚未写完时误显示。
    if (-not $tail.EndsWith("`n")) {
        $lastNewline = $tail.LastIndexOf("`n")
        if ($lastNewline -lt 0) { return '暂未读到完整资源行。' }
        $tail = $tail.Substring(0, $lastNewline + 1)
    }
    $lines = @($tail -split '\r?\n' | Where-Object { $_ -and $_ -ne $header })
    # CSV 可能在最后一行写到一半时被读取；不把未闭合或字段不齐的记录当成采样。
    $rows = @(($header + "`n" + ($lines -join "`n")) | ConvertFrom-Csv | Where-Object {
        $_.TimestampUtc -match '^\d{4}-\d\d-\d\dT' -and $_.Selector -and $_.State -and $_.Handles -ne $null
    })
    $latest = @{}
    foreach ($row in $rows) { $latest[$row.Selector] = $row }
    if ($latest.Count -eq 0) { return '暂未读到完整资源行。' }
    $table = @($latest.Values | Sort-Object Selector | ForEach-Object {
        $cpu = $(if ($_.CpuPercentOfMachine) { [double]::Parse($_.CpuPercentOfMachine, [Globalization.CultureInfo]::InvariantCulture).ToString('F1') } else { '-' })
        $memory = $(if ($_.PrivateBytes) { ([double]$_.PrivateBytes / 1MB).ToString('F1') } else { '-' })
        [pscustomobject]@{
            '实例' = $_.Selector; '状态' = $_.State; 'PID' = $_.ProcessId; 'CPU%整机' = $cpu
            'PrivateMiB' = $memory; '线程' = $_.Threads; '句柄' = $_.Handles; '采样UTC' = $_.TimestampUtc
        }
    })
    return $table | Format-Table -AutoSize | Out-String -Width 220
}

<#
.SYNOPSIS
判断原主窗口是否仍属于同一进程生命周期；主窗口异常退出时只结束显示，不代替其清理采集。
.PARAMETER State
含主窗口 PID 和创建时间的当前状态。
.OUTPUTS
原主窗口仍存在且身份相同返回真。
#>
function Test-MonitorCoordinator {
    param([object]$State)
    $process = Get-Process -Id ([int]$State.CoordinatorProcessId) -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    return $process.StartTime.ToUniversalTime() -eq [DateTimeOffset]::Parse($State.CoordinatorCreatedAtUtc).UtcDateTime
}

<#
.SYNOPSIS
显示对应采集任务的状态、最近证据和说明；本函数仅输出，绝不调用 PerfView 或 ProcDump。
.PARAMETER State
已确认属于本次运行的完整状态对象。
#>
function Show-MonitorPanel {
    param([object]$State)
    $title = @{ Resources = '资源监控'; Trace = 'PerfView 跟踪'; Dump = 'Dump 监听与取证' }[$Panel]
    Write-Host "GatherV4 | $title | $($State.RunId)" -ForegroundColor Cyan
    Write-Host "整体状态：$($State.State)    主对象：$($State.PrimarySelector) / PID $($State.PrimaryProcessId)"
    Write-Host '本窗口只读显示。取证 D / 正常结束 Q 在主窗口操作；关闭此窗口不会停止采集。' -ForegroundColor Yellow
    Write-Host "状态文件：$StatePath"
    switch ($Panel) {
        'Resources' {
            Write-Host "资源任务：$($State.ResourceState)    CSV：$($State.ResourceCsv)"
            Write-Host (Get-MonitorResources -Path $State.ResourceCsv)
        }
        'Trace' {
            Write-Host "PerfView：$($State.TraceState)    独立会话：$($State.TraceSession)"
            Write-Host "文件：$($State.TracePath)"
            $path = $(if ($State.TraceState -in @('Stopping', 'Saved', 'Failed')) { $State.PerfViewStopLog } else { $State.PerfViewStartLog })
            $log = Read-MonitorText -Path $path -Tail
            Write-Host $(if ($log) { (($log -split '\r?\n' | Select-Object -Last 14) -join "`n") } else { '等待工具启动或写出日志。' })
        }
        'Dump' {
            Write-Host "异常/退出监听：$($State.DumpState)    现场抓取：$($State.CaptureState)    触发：$($State.Trigger)"
            Write-Host '业务不响应仍需在主窗口按 D；异常或退出由监听器触发。'
            $log = Read-MonitorText -Path $State.DumpLog -Tail
            if ($log) { Write-Host (($log -split '\r?\n' | Select-Object -Last 10) -join "`n") }
            $directory = Split-Path -Parent $StatePath
            $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.dmp' -Recurse -File | Where-Object {
                $_.FullName.StartsWith((Join-Path $directory $State.ChildStage))
            })
            if ($files.Count -gt 0) {
                Write-Host '已出现的转储文件（写入中长度可能增长，完成以主窗口结果为准）：'
                Write-Host ($files | Select-Object Name, Length | Format-Table -AutoSize | Out-String -Width 160)
            }
            else { Write-Host '尚未触发转储，没有 DMP 属于正常等待。' }
        }
    }
    if ($State.Error) { Write-Host "主窗口报告：$($State.Error)" -ForegroundColor Red }
}

$lastState = $null
while ($true) {
    $state = Read-MonitorState
    if ($state) { $lastState = $state }
    if (-not $Once) { try { Clear-Host } catch { } }
    if ($lastState) {
        try { $host.UI.RawUI.WindowTitle = "GatherV4 $Panel | $($lastState.RunId)" } catch { }
        Show-MonitorPanel -State $lastState
        if ($Once) { break }
        if ($lastState.State -in @('Completed', 'TimedOut', 'Cancelled', 'Failed')) {
            Write-Host '本轮采集已结束，状态窗口三秒后自动关闭。' -ForegroundColor Green
            Start-Sleep -Seconds 3
            break
        }
        if (-not (Test-MonitorCoordinator -State $lastState)) {
            Write-Host '原主窗口已退出或身份改变，停止显示。请核对采集器与证据，不把此情况当作正常归档。' -ForegroundColor Yellow
            break
        }
    }
    elseif ($Once) { throw '尚未读到有效本轮状态。' }
    else { Write-Host '等待本轮主窗口状态；本窗口不执行采集。' }
    Start-Sleep -Seconds $RefreshSeconds
}
