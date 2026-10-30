<#
.SYNOPSIS
验证轻量监控的配置解析、资源采样、统一时间线、摘要和 ZIP 归档；只监控当前 PowerShell 进程。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptsDirectory = Split-Path -Parent $PSScriptRoot
$monitor = Join-Path $scriptsDirectory 'Start-GatherV4Monitor.ps1'
$defaultConfig = Join-Path $scriptsDirectory 'GatherV4.Monitor.config.psd1'
# Windows PowerShell 5.1 会把无 BOM 的中文 UTF-8 当作系统代码页；部署文件必须保留 BOM，避免乱码破坏脚本语法。
foreach ($deploymentFile in @($monitor, $defaultConfig)) {
    $bytes = [IO.File]::ReadAllBytes($deploymentFile)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "Windows PowerShell 5.1 部署文件缺少 UTF-8 BOM：$deploymentFile"
    }
}
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent ('GatherSimpleMonitorTest-' + [guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw '测试目录必须位于独立临时目录。' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    $configPath = Join-Path $testRoot 'monitor.psd1'
    $escapedRoot = $testRoot.Replace("'", "''")
    @"
@{
    OutputRoot = '$escapedRoot'
    DurationSeconds = 2
    IntervalSeconds = 1
    Targets = @(
        @{ Name = 'CurrentPowerShell'; Type = 'ProcessId'; Value = $PID; Enabled = `$true }
    )
}
"@ | Set-Content -LiteralPath $configPath -Encoding UTF8

    & $monitor -RunId simple-monitor-test -Scenario test -ConfigPath $configPath -DryRun 6>&1 | Out-Null
    if (Test-Path -LiteralPath (Join-Path $testRoot 'simple-monitor-test')) { throw 'DryRun 不应创建证据目录。' }

    & $monitor -RunId simple-monitor-test -Scenario test -ConfigPath $configPath -NonInteractive
    $session = Get-ChildItem -LiteralPath (Join-Path $testRoot 'simple-monitor-test\server-monitor') -Directory | Select-Object -First 1
    if (-not $session) { throw '没有创建轻量监控会话目录。' }
    foreach ($name in @('process-timeline.csv', 'events.csv', 'summary.md', 'summary.json', 'manifest.json', 'monitor-config.psd1')) {
        $file = Join-Path $session.FullName $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "产物缺失或为空：$name" }
    }
    $rows = @(Import-Csv -LiteralPath (Join-Path $session.FullName 'process-timeline.csv'))
    if ($rows.Count -lt 2 -or @($rows | Where-Object State -eq 'Running').Count -lt 2) { throw '没有记录至少两次运行状态采样。' }
    $events = @(Import-Csv -LiteralPath (Join-Path $session.FullName 'events.csv'))
    if ('SessionStarted' -notin $events.Type -or 'SessionEnded' -notin $events.Type -or 'InitialState' -notin $events.Type) { throw '统一时间线缺少会话或初始状态事件。' }
    $summary = Get-Content -LiteralPath (Join-Path $session.FullName 'summary.json') -Raw | ConvertFrom-Json
    if ($summary.State -ne 'Completed' -or $summary.Targets[0].Target -ne 'CurrentPowerShell') { throw '摘要状态或目标不正确。' }
    $archive = "$($session.FullName).zip"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -eq 0) { throw '没有生成有效 ZIP 归档。' }
    Write-Host 'GatherV4 轻量监控测试通过。' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch '^GatherSimpleMonitorTest-[a-f0-9]{32}$') {
            throw '拒绝删除不属于本测试的目录。'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
