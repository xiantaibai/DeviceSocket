<#
.SYNOPSIS
验证自动 Dump 向导、PID 身份保护及真实 ProcDump 的退出抓取、多目标抓取和取消行为。
.PARAMETER ProcDumpPath
可选的真实 ProcDump 完整路径；为空时只运行不启动采集器的预览和身份校验测试。
.PARAMETER KeepEvidence
保留本次临时测试目录；默认在验证后删除仅由本脚本创建的目录。
.DESCRIPTION
真实集成测试只启动本脚本创建的短期 PowerShell 辅助进程，绝不使用业务服务或外部 PID。
测试监控到期后验证目标仍存活；辅助进程自行结束，不用 Stop-Process 清理。
#>
[CmdletBinding()]
param([string]$ProcDumpPath = '', [switch]$KeepEvidence)

$ErrorActionPreference = 'Stop'
$scriptsDirectory = Split-Path -Parent $PSScriptRoot
$collector = Join-Path $scriptsDirectory 'Invoke-GatherV4Diagnostics.ps1'
$wizard = Join-Path $scriptsDirectory 'Start-GatherV4Diagnostics.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('GatherDumpWatchTest-' + [guid]::NewGuid().ToString('N'))
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw '测试目录必须位于系统临时目录内。' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

<#
.SYNOPSIS
以中文模拟回答运行完整向导预览，确认触发条件、单位、默认时长和固定 PID 转换。
.PARAMETER Name
验证场景的中文名称。
.PARAMETER Answers
从用途选择到最终确认的回答队列。
.OUTPUTS
完整的预览文本；出现多问、少问或实际写证据文件时失败。
#>
function Invoke-WizardPreviewTest {
    param([string]$Name, [string[]]$Answers)
    $global:GatherWatchAnswers = [Collections.Generic.Queue[string]]::new()
    foreach ($answer in $Answers) { $global:GatherWatchAnswers.Enqueue($answer) }
    # 用回答队列模拟操作人员，任何额外问题都会使测试明确失败。
    function Read-Host {
        param([string]$Prompt)
        if ($global:GatherWatchAnswers.Count -eq 0) { throw "模拟回答不足：$Prompt" }
        return $global:GatherWatchAnswers.Dequeue()
    }
    # 提供固定身份及服务映射；预览不读取或监控真实业务服务。
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($ClassName -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ NumberOfLogicalProcessors = 4 } }
        if ($ClassName -eq 'Win32_Service') { return [pscustomobject]@{ Name = 'test-v4-connect'; State = 'Running'; ProcessId = 34999 } }
        return [pscustomobject]@{
            ProcessId = 34999; Name = 'GatherService.ConnectService.exe'; CreationDate = [datetime]'2026-10-12T00:00:00Z'
            ExecutablePath = 'D:\Mock\Connect8890\GatherService.ConnectService.exe'; CommandLine = 'mock'
        }
    }
    $existingFile = (Get-Process -Id $PID).Path.Replace("'", "''")
    $source = (Get-Content -LiteralPath $wizard -Raw -Encoding UTF8).Replace("'C:\Tools\ProcDump\procdump64.exe'", "'$existingFile'")
    $block = [scriptblock]::Create($source)
    $result = (& $block -CollectorPath $collector -SessionPath (Join-Path $testRoot 'unused.json') -DryRun -NoSave 6>&1 | Out-String)
    if ($global:GatherWatchAnswers.Count -ne 0) { throw "$Name 有未消费的回答。" }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'preview-must-not-exist')) { throw '预览写入了采集文件。' }
    Write-Host "通过：$Name"
    return $result
}

<#
.SYNOPSIS
模拟进程在解析后发生 PID 复用，验证监听在启动工具前拒绝新身份。
#>
function Test-IdentityChange {
    $global:GatherWatchProcessReads = 0
    # 第二次解析同 PID 时更换创建时间，模拟已退出的 PID 被其他进程复用。
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($ClassName -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ NumberOfLogicalProcessors = 4 } }
        if (-not $Filter) { return @() }
        $global:GatherWatchProcessReads++
        $created = [datetime]'2026-10-12T00:00:00Z'
        if ($global:GatherWatchProcessReads -gt 1) { $created = $created.AddSeconds(1) }
        return [pscustomobject]@{
            ProcessId = 34999; Name = 'GatherService.ConnectService.exe'; CreationDate = $created
            ExecutablePath = 'D:\Mock\GatherService.ConnectService.exe'; CommandLine = 'mock'
        }
    }
    $rejected = $false
    try {
        & $collector -Action DumpWatch -TargetProcessId 34999 -RunId identity-changed -ProcDumpPath (Get-Process -Id $PID).Path -OutputRoot $testRoot
    }
    catch { if ($_.Exception.Message -match 'PID 身份变化') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw '没有拒绝 PID 复用。' }
    $state = Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*-watch.json' | Select-Object -First 1
    $record = Get-Content -LiteralPath $state.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.Targets[0].CollectorId) { throw '身份校验失败仍启动了监控器。' }
    Write-Host '通过：PID 复用被拒绝，未启动工具。'
}

<#
.SYNOPSIS
用不支持 ProcDump 参数的测试宿主模拟工具启动失败，验证状态归档及子进程清理。
#>
function Test-StartupFailure {
    # 身份保持不变，让错误发生在工具启动阶段，而不是前置 PID 校验。
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($ClassName -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ NumberOfLogicalProcessors = 4 } }
        if (-not $Filter) { return @() }
        return [pscustomobject]@{
            ProcessId = 34999; Name = 'GatherService.ConnectService.exe'; CreationDate = [datetime]'2026-10-12T00:00:00Z'
            ExecutablePath = 'D:\Mock\GatherService.ConnectService.exe'; CommandLine = 'mock'
        }
    }
    $failed = $false
    try { & $collector -Action DumpWatch -TargetProcessId 34999 -RunId startup-failure -ProcDumpPath (Get-Process -Id $PID).Path -OutputRoot $testRoot }
    catch { if ($_.Exception.Message -match 'ProcDump 启动失败') { $failed = $true } else { throw } }
    if (-not $failed) { throw '工具启动失败没有被识别。' }
    $state = Get-TestWatchState -Run startup-failure
    if ($state.State -ne 'Failed' -or $state.Targets[0].Armed -or (Get-Process -Id $state.Targets[0].CollectorId -ErrorAction SilentlyContinue)) {
        throw '启动失败的状态或子进程清理不正确。'
    }
    Write-Host '通过：启动失败正确归档，没有留下监听子进程。'
}

<#
.SYNOPSIS
读取指定集成场景的最终状态，避免通过文件存在就误判真实转储成功。
.PARAMETER Run
测试运行编号。
.OUTPUTS
该运行的监听状态 JSON 对象。
#>
function Get-TestWatchState {
    param([string]$Run)
    $file = Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*-watch.json' |
        Where-Object FullName -Like "*\$Run\*" | Sort-Object LastWriteTimeUtc | Select-Object -Last 1
    if (-not $file) { throw "$Run 未生成状态文件。" }
    return Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
}

<#
.SYNOPSIS
启动短期测试辅助进程，最长四十秒后自行退出；不操作任何已经存在的业务进程。
.PARAMETER Seconds
辅助进程自行等待的秒数。
.OUTPUTS
本脚本创建的进程句柄。
#>
function Start-TestHelper {
    param([int]$Seconds)
    return Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList "-NoProfile -NonInteractive -Command `"Start-Sleep -Seconds $Seconds`"" -WindowStyle Hidden -PassThru
}

<#
.SYNOPSIS
用独立流水线模拟 Ctrl+C，并验证重复监听不能取消原监听、停止后目标仍存活且有取消归档。
#>
function Test-InterruptedWatch {
    $helper = Start-TestHelper -Seconds 40
    $pipeline = [Management.Automation.PowerShell]::Create()
    $pipeline.AddScript('param($collector,$number,$tool,$root) & $collector -Action DumpWatch -TargetProcessId $number -RunId native-interrupt -DurationSeconds 60 -ProcDumpPath $tool -OutputRoot $root').AddParameters(
        @{ collector = $collector; number = $helper.Id; tool = $ProcDumpPath; root = $testRoot }) | Out-Null
    $pending = $pipeline.BeginInvoke()
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew(); $state = $null
        while ($clock.Elapsed.TotalSeconds -lt 15) {
            try { $state = Get-TestWatchState -Run native-interrupt } catch {}
            if ($state -and $state.State -eq 'Armed') { break }
            [Threading.Thread]::Sleep(200)
        }
        if (-not $state -or $state.State -ne 'Armed') { throw '未能注册用于中断验证的监听。' }
        $monitorId = $state.Targets[0].CollectorId
        $rejected = $false
        try { & $collector -Action DumpWatch -TargetProcessId $helper.Id -RunId native-duplicate -DurationSeconds 1 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot }
        catch { if ($_.Exception.Message -match '另一个向导中监控') { $rejected = $true } else { throw } }
        if (-not $rejected -or -not (Get-Process -Id $monitorId -ErrorAction SilentlyContinue)) { throw '重复监听未被拒绝或影响了原监控器。' }
        $pipeline.Stop()
        $final = Get-TestWatchState -Run native-interrupt
        if ($final.State -ne 'Cancelled' -or $helper.HasExited -or (Get-Process -Id $monitorId -ErrorAction SilentlyContinue)) { throw '中断清理没有保留目标或归档状态错误。' }
        Write-Host '通过：重复监听被拒绝且原监听继续运行；模拟 Ctrl+C 安全取消并归档。'
    }
    finally {
        if ($pipeline.InvocationStateInfo.State -eq 'Running') { $pipeline.Stop() }
        $pipeline.Dispose()
    }
}

try {
    foreach ($path in @($collector, $wizard)) {
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors) { throw ($parseErrors | Out-String) }
    }
    $previewRoot = Join-Path $testRoot 'preview-must-not-exist'
    $prefix = @('5', '2', '1', 'watch-test', '3', 'test-v4-connect', '1', '1')
    $crash = Invoke-WizardPreviewTest -Name '异常或退出默认监听，不立即抓取' -Answers ($prefix + @('1', '', '', $previewRoot, '2'))
    if ($crash -notmatch "'-e' '-t' '-n' '1' '34999'" -or $crash -notmatch '最多监听 14400 秒') { throw '异常监听默认参数不正确。' }
    $cpu = Invoke-WizardPreviewTest -Name 'CPU 阈值采用进程占整机口径' -Answers ($prefix + @('4', '70', '12', '2', '120', $previewRoot, '2'))
    if ($cpu -notmatch "'-c' '70' '-s' '12' '-n' '2'" -or $cpu -match "'-u'") { throw 'CPU 阈值口径不正确。' }
    $memory = Invoke-WizardPreviewTest -Name '内存阈值及数量上限' -Answers ($prefix + @('5', '600', '8', '1', '90', $previewRoot, '2'))
    if ($memory -notmatch "'-m' '600' '-s' '8' '-n' '1'") { throw '内存阈值参数不正确。' }
    Test-IdentityChange
    Test-StartupFailure
    if ($ProcDumpPath) {
        if (-not (Test-Path -LiteralPath $ProcDumpPath -PathType Leaf)) { throw '真实 ProcDump 文件不存在。' }
        $helper = Start-TestHelper -Seconds 40
        & $collector -Action DumpWatch -TargetProcessId $helper.Id -RunId native-timeout -DurationSeconds 2 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot
        $state = Get-TestWatchState -Run native-timeout
        if ($state.State -ne 'TimedOut' -or -not $state.Targets[0].Armed -or -not $state.Targets[0].CancelRequested -or $helper.HasExited) { throw '到期取消或目标恢复验证失败。' }
        if (Get-Process -Id $state.Targets[0].CollectorId -ErrorAction SilentlyContinue) { throw '到期后留下了监听器。' }
        Write-Host '通过：真实监听到期取消，目标继续存活。'

        $helper = Start-TestHelper -Seconds 7
        & $collector -Action DumpWatch -TargetProcessId $helper.Id -RunId native-exit -DurationSeconds 30 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot
        $state = Get-TestWatchState -Run native-exit
        if ($state.State -ne 'Completed' -or $state.Targets[0].Dumps.Count -ne 1) { throw '自然退出没有捕获转储。' }
        $stream = [IO.File]::OpenRead($state.Targets[0].Dumps[0].FullName)
        try {
            $bytes = New-Object byte[] 4; $stream.Read($bytes, 0, 4) | Out-Null
            if ([Text.Encoding]::ASCII.GetString($bytes) -ne 'MDMP') { throw '退出转储文件头无效。' }
        }
        finally { $stream.Dispose() }
        Write-Host '通过：真实进程自然退出捕获一份转储，正退出码不会误判失败。'

        $helperPath = Join-Path $testRoot 'GatherDumpWatchCrashHelper.cs'
        $helperExe = Join-Path $testRoot 'GatherDumpWatchCrashHelper.exe'
        $helperSource = @'
using System;
using System.Threading;
using System.Runtime.InteropServices;
/// <summary>为自动转储集成验证提供一次可控的进程内未处理异常。</summary>
public static class GatherDumpWatchCrashHelper
{
    /// <summary>只修改当前测试进程的错误显示模式，避免未处理异常打开交互错误窗口。</summary>
    /// <param name="flags">当前进程错误显示标志。</param>
    /// <returns>原有的错误显示标志。</returns>
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint flags);
    /// <summary>短暂等待监控挂接后，在本测试控制台进程抛出未处理异常，不操作其他进程。</summary>
    /// <exception cref="InvalidOperationException">五秒后主动产生用于自动转储验证的测试异常。</exception>
    public static void Main() { SetErrorMode(2); Thread.Sleep(5000); throw new InvalidOperationException("GatherDumpWatch test"); }
}
'@
        [IO.File]::WriteAllText($helperPath, $helperSource, [Text.UTF8Encoding]::new($true))
        # 独立 .NET Framework 控制台不经过 PowerShell 的宿主异常处理，真正验证第二次机会异常触发。
        $compileScript = Join-Path $testRoot 'compile-helper.ps1'
        $compileSource = @'
<#
.SYNOPSIS
只将本测试生成的带中文说明的辅助类型编译成临时控制台程序。
.PARAMETER Source
本次唯一测试目录内的 C# 源文件。
.PARAMETER Destination
本次测试辅助程序的完整输出路径。
#>
param([string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -Path $Source -OutputAssembly $Destination -OutputType ConsoleApplication
'@
        [IO.File]::WriteAllText($compileScript, $compileSource, [Text.UTF8Encoding]::new($true))
        & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $compileScript -Source $helperPath -Destination $helperExe
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $helperExe)) { throw '测试辅助程序编译失败。' }
        $helper = Start-Process -FilePath $helperExe -WindowStyle Hidden -PassThru
        & $collector -Action DumpWatch -TargetProcessId $helper.Id -DumpTrigger Exception -RunId native-exception -DurationSeconds 30 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot
        $state = Get-TestWatchState -Run native-exception
        if ($state.State -ne 'Completed' -or $state.Targets[0].Dumps.Count -ne 1) { throw '仅未处理异常模式没有保存现场。' }
        Write-Host '通过：真实辅助进程的未处理异常自动生成一份转储。'

        $helpers = @(1, 2 | ForEach-Object { Start-TestHelper -Seconds 40 })
        & $collector -Action DumpWatch -TargetProcessId @($helpers.Id) -DumpTrigger Memory -MemoryThresholdMB 1 -ThresholdSeconds 1 -RunId native-multiple -DurationSeconds 20 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot
        $state = Get-TestWatchState -Run native-multiple
        if ($state.State -ne 'Completed' -or $state.Targets.Count -ne 2 -or @($state.Targets | Where-Object { $_.Dumps.Count -ne 1 }).Count -gt 0) { throw '多个目标没有分别捕获转储。' }
        if (@($helpers | Where-Object HasExited).Count -gt 0) { throw '阈值转储导致目标提前退出。' }
        Write-Host '通过：两个目标同时按内存阈值各抓一份，目标继续运行。'
        $helper = Start-TestHelper -Seconds 40
        & $collector -Action DumpWatch -TargetProcessId $helper.Id -DumpTrigger Memory -MemoryThresholdMB 1 -ThresholdSeconds 1 -DumpLimit 2 -RunId native-limit -DurationSeconds 20 -ProcDumpPath $ProcDumpPath -OutputRoot $testRoot
        $state = Get-TestWatchState -Run native-limit
        if ($state.State -ne 'Completed' -or $state.Targets[0].Dumps.Count -ne 2 -or $helper.HasExited) { throw '两份数量限制验证失败。' }
        Write-Host '通过：数量限制为两份，捕获到上限后停止监听，目标仍存活。'
        Test-InterruptedWatch
    }
    else { Write-Host '未指定 ProcDumpPath，跳过真实辅助进程转储测试。' }
    Write-Host '自动 Dump 验证全部通过。' -ForegroundColor Green
}
finally {
    Remove-Variable -Name GatherWatchAnswers, GatherWatchProcessReads -Scope Global -ErrorAction SilentlyContinue
    if ($KeepEvidence) { Write-Host "测试证据：$testRoot" }
    else {
        # 只清理本次唯一临时目录，并在递归删除前重新验证绝对路径边界。
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch '^GatherDumpWatchTest-[a-f0-9]{32}$') { throw '拒绝删除不属于本测试的目录。' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
