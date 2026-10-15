<#
.SYNOPSIS
验证三个自动状态窗口的启动计划、最新资源、UTF-16 日志、会话隔离与只读行为；不监控业务进程。
.PARAMETER LaunchWindows
额外打开三个真实显示窗口，读取已结束的临时测试状态，三秒后自行退出；默认只验证启动计划和快照。
#>
[CmdletBinding()]
param([switch]$LaunchWindows)
$ErrorActionPreference = 'Stop'
$scriptsDirectory = Split-Path -Parent $PSScriptRoot
$viewer = Join-Path $scriptsDirectory 'Watch-GatherV4Monitor.ps1'
$library = Join-Path $scriptsDirectory 'GatherV4.IncidentCapture.ps1'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent ('GatherMonitorWindowsTest-' + [guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw '测试必须位于独立临时目录。' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

<#
.SYNOPSIS
用假启动器验证只打开三个显示进程，不启动第二套 Sample、PerfView 或 DumpWatch。
.PARAMETER FailSecond
模拟第二个显示窗口打不开；其余窗口仍启动，不把显示失败当作采集器失败。
#>
function Test-WindowLaunchPlan {
    param([switch]$FailSecond)
    . $library
    $captureBase = Join-Path $testRoot "采集 输出 '空格'"
    $global:GatherMonitorLaunches = [Collections.Generic.List[object]]::new()
    function Start-Process {
        param($FilePath, $ArgumentList, $WindowStyle, [switch]$PassThru)
        $global:GatherMonitorLaunches.Add([pscustomobject]@{ Path = $FilePath; Arguments = $ArgumentList; Style = $WindowStyle })
        if ($FailSecond -and $global:GatherMonitorLaunches.Count -eq 2) { throw '模拟显示窗口启动失败' }
        return [pscustomobject]@{ Id = 30000 + $global:GatherMonitorLaunches.Count; StartTime = [datetime]::UtcNow }
    }
    $context = @{ SessionId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
    $processes = @(Start-IncidentStatusWindows -Context $context -ViewerPath $viewer)
    if ($global:GatherMonitorLaunches.Count -ne 3) { throw '没有自动布置三类状态窗口。' }
    foreach ($launch in $global:GatherMonitorLaunches) {
        if ($launch.Style -ne 'Normal' -or $launch.Arguments -notlike "*$viewer*" -or $launch.Arguments -notlike '*-ExpectedSessionId*') { throw '窗口可见性或本轮身份参数错误。' }
        if ($launch.Arguments -match 'Invoke-GatherV4Diagnostics|DumpWatch|Sample|PerfView\.exe') { throw '显示窗口重复启动了采集器。' }
    }
    if ($FailSecond) {
        if ($processes.Count -ne 2 -or $context.MonitorWindows[1].State -ne 'Failed') { throw '显示启动失败没有单独记录。' }
    }
    elseif ($processes.Count -ne 3 -or @($context.MonitorWindows | Where-Object State -ne 'Opened').Count -gt 0) { throw '状态窗口启动信息不完整。' }
    Write-Host "通过：三窗口自动启动计划；显示失败隔离=$([bool]$FailSecond)；没有重复采集。"
}

try {
    foreach ($path in @($viewer, $library)) {
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors) { throw ($parseErrors | Out-String) }
    }
    Test-WindowLaunchPlan
    Test-WindowLaunchPlan -FailSecond
    $csv = Join-Path $testRoot 'resources.csv'
    $rows = @(1..900 | ForEach-Object {
        [pscustomobject]@{ TimestampUtc = '2026-10-12T02:00:00Z'; Selector = 'service:connect-8890'; State = 'Running'; ProcessId = 12345; CpuPercentOfMachine = '42.0'; PrivateBytes = 104857600; Threads = 10; Handles = 20 }
    })
    $rows += [pscustomobject]@{ TimestampUtc = '2026-10-12T02:00:01Z'; Selector = 'service:connect-8890'; State = 'Running'; ProcessId = 12345; CpuPercentOfMachine = '7.8'; PrivateBytes = 209715200; Threads = 12; Handles = 30 }
    $rows += [pscustomobject]@{ TimestampUtc = '2026-10-12T02:00:01Z'; Selector = 'service:timer-8890'; State = 'Stopped'; ProcessId = ''; CpuPercentOfMachine = ''; PrivateBytes = ''; Threads = ''; Handles = '' }
    $rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
    # 模拟采样器正写最后一行：字段数已齐，但 CPU 与句柄值还未完成，必须沿用上一条完整样本。
    [IO.File]::AppendAllText($csv, '"2026-10-12T02:00:02Z","service:connect-8890","Running","12345","99.9","209715200","12","3', [Text.UTF8Encoding]::new($false))
    $log = Join-Path $testRoot 'procdump.stdout.log'
    [IO.File]::WriteAllText($log, "ProcDump v12.01`r`nPress Ctrl-C to end monitoring`r`n[10:12:00]Exception: E0434352.CLR`r`n", [Text.Encoding]::Unicode)
    $traceLog = Join-Path $testRoot 'perfview.log'
    '本轮 PerfView 正在采集测试说明。' | Set-Content -LiteralPath $traceLog -Encoding UTF8
    $ownDump = Join-Path $testRoot 'one-fixture-incident-1.dmp'
    [IO.File]::WriteAllText($ownDump, 'test-only')
    [IO.File]::WriteAllText((Join-Path $testRoot 'other-run-2.dmp'), 'other-test-only')
    $statePath = Join-Path $testRoot 'incident.json'
    $fixture = @{
        SessionId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; RunId = 'monitor-test'; State = 'Ready'
        PrimarySelector = 'service:connect-8890'; PrimaryProcessId = 12345; ChildStage = 'one-fixture'
        CoordinatorProcessId = $PID; CoordinatorCreatedAtUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')
        ResourceState = 'Ready'; ResourceCsv = $csv; TraceState = 'Collecting'; TraceSession = 'test-session'
        TracePath = 'test.etl'; PerfViewStartLog = $traceLog; PerfViewStopLog = $traceLog
        DumpState = 'Armed'; CaptureState = 'Waiting'; Trigger = $null; DumpLog = $log
    }
    $fixture | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    $before = @{}
    Get-ChildItem -LiteralPath $testRoot -File | ForEach-Object { $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $resources = (& $viewer -StatePath $statePath -ExpectedSessionId $fixture.SessionId -Panel Resources -Once 6>&1 | Out-String)
    if ($resources -notmatch '7\.8' -or $resources -match '42\.0|99\.9' -or $resources -notmatch 'Stopped') { throw '资源没有显示最新完整采样及停止背景服务。' }
    $trace = (& $viewer -StatePath $statePath -ExpectedSessionId $fixture.SessionId -Panel Trace -Once 6>&1 | Out-String)
    if ($trace -notmatch 'Collecting' -or $trace -notmatch '本轮 PerfView') { throw 'PerfView 状态或日志缺失。' }
    $dump = (& $viewer -StatePath $statePath -ExpectedSessionId $fixture.SessionId -Panel Dump -Once 6>&1 | Out-String)
    if ($dump -notmatch 'E0434352.CLR' -or $dump -notmatch 'one-fixture-incident-1.dmp' -or $dump -match 'other-run-2.dmp') { throw 'UTF-16 日志或转储范围隔离错误。' }
    foreach ($path in $before.Keys) { if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $before[$path]) { throw '状态查看修改了本轮证据。' } }
    $rejected = $false
    try { & $viewer -StatePath $statePath -ExpectedSessionId 'cccccccccccccccccccccccccccccccc' -Panel Resources -Once | Out-Null }
    catch { if ($_.Exception.Message -match '会话标识不同') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw '没有拒绝其他会话。' }
    Write-Host '通过：CSV 尾部最新完整样本、忽略写入中行、停止状态、UTF-16 日志、转储隔离、全证据只读及会话校验。'
    if ($LaunchWindows) {
        . $library
        $windowDirectory = Join-Path $testRoot "采集 输出 '空格'"
        New-Item -ItemType Directory -Path $windowDirectory -Force | Out-Null
        $captureBase = Join-Path $windowDirectory '窗口验证'
        $context = $fixture.Clone(); $context.State = 'Completed'
        $context | ConvertTo-Json | Set-Content -LiteralPath "$captureBase-incident.json" -Encoding UTF8
        $processes = @(Start-IncidentStatusWindows -Context $context -ViewerPath $viewer)
        try {
            if ($processes.Count -ne 3 -or @($context.MonitorWindows | Where-Object State -ne 'Opened').Count -gt 0) { throw '真实状态窗口没有全部启动。' }
            foreach ($process in $processes) {
                if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw '真实显示窗口没有正常读取终态并自动退出。' }
            }
            Write-Host '通过：三个真实状态进程启动、中文及空格路径传参、读取终态后三秒自动退出。'
        }
        finally {
            foreach ($process in $processes) { $process.Dispose() }
        }
    }
    Write-Host '自动监控状态窗口验证全部通过。' -ForegroundColor Green
}
finally {
    Remove-Variable -Name GatherMonitorLaunches -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^GatherMonitorWindowsTest-[a-f0-9]{32}$') { throw '拒绝清理非本次测试目录。' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
