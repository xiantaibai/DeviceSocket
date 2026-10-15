<#
.SYNOPSIS
验证组合取证、跨窗口设置复用、PID 重查与安全结束；原生转储只针对本测试新建的辅助进程。
.PARAMETER ProcDumpPath
可选的真实 ProcDump 路径；不填写时只验证向导与参数。
.PARAMETER PerfViewPath
可选真实 PerfView 路径；为空时使用记录 start/stop 参数的测试工具，不宣称生成了真实 ETL。
.PARAMETER KeepEvidence
保留本次唯一临时目录；默认按绝对路径边界安全清理。
#>
[CmdletBinding()]
param([string]$ProcDumpPath = '', [string]$PerfViewPath = '', [switch]$KeepEvidence)
$ErrorActionPreference = 'Stop'
$scriptsDirectory = Split-Path -Parent $PSScriptRoot
$collector = Join-Path $scriptsDirectory 'Invoke-GatherV4Diagnostics.ps1'
$wizard = Join-Path $scriptsDirectory 'Start-GatherV4Diagnostics.ps1'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent ('GatherIncidentTest-' + [guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝在临时目录之外创建测试。' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$sessionPath = Join-Path $testRoot 'session.json'
$global:GatherIncidentFixturePid = 34999
$global:GatherIncidentFixtureCreated = [datetime]'2026-10-12T00:00:00Z'
$global:GatherIncidentFixtureAmbiguous = $false
$global:GatherIncidentFixtureStopped = $false
$nativeCollector = $collector

<#
.SYNOPSIS
模拟向导问答，实际写本测试设置文件但只预览采集；任何多余或缺少问题都视为失败。
.PARAMETER Answers
按显示顺序给出的回答。
.OUTPUTS
完整预览文本。
#>
function Invoke-TestWizard {
    param([string[]]$Answers)
    $global:GatherIncidentAnswers = [Collections.Generic.Queue[string]]::new()
    foreach ($answer in $Answers) { $global:GatherIncidentAnswers.Enqueue($answer) }
    function Read-Host {
        param([string]$Prompt)
        if ($global:GatherIncidentAnswers.Count -eq 0) { throw "多余问题：$Prompt" }
        return $global:GatherIncidentAnswers.Dequeue()
    }
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($ClassName -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ NumberOfLogicalProcessors = 4 } }
        if ($ClassName -eq 'Win32_Service') {
            return [pscustomobject]@{
                Name = 'mock-connect'; State = $(if ($global:GatherIncidentFixtureStopped) { 'Stopped' } else { 'Running' })
                ProcessId = $(if ($global:GatherIncidentFixtureStopped) { 0 } else { $global:GatherIncidentFixturePid })
                PathName = 'D:\Mock\GatherService.ConnectService.exe'
            }
        }
        $process = [pscustomobject]@{
            ProcessId = $global:GatherIncidentFixturePid; CreationDate = $global:GatherIncidentFixtureCreated
            Name = 'GatherService.ConnectService.exe'; ExecutablePath = 'D:\Mock\GatherService.ConnectService.exe'; CommandLine = 'mock-api -pool test'
        }
        if ($global:GatherIncidentFixtureAmbiguous -and -not $Filter) { return @($process, $process) }
        return $process
    }
    $existingTool = (Get-Process -Id $PID).Path.Replace("'", "''")
    $source = (Get-Content -LiteralPath $wizard -Raw -Encoding UTF8).
        Replace("'C:\Tools\ProcDump\procdump64.exe'", "'$existingTool'").Replace("'C:\Tools\PerfView\PerfView.exe'", "'$existingTool'")
    $output = (& ([scriptblock]::Create($source)) -CollectorPath $collector -SessionPath $sessionPath -DryRun 6>&1 | Out-String)
    $output | Add-Content -LiteralPath (Join-Path $testRoot 'wizard-previews.log') -Encoding UTF8
    if ($global:GatherIncidentAnswers.Count -ne 0) { throw '向导未消费全部回答。' }
    return $output
}

<#
.SYNOPSIS
轮询本测试某运行的组合状态；不读取外部业务运行。
.PARAMETER Run
本测试的运行标签。
.OUTPUTS
该测试运行的最新组合状态。
#>
function Get-TestIncidentState {
    param([string]$Run)
    $file = Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*-incident.json' -File |
        Where-Object FullName -Like "*\$Run\*" | Select-Object -Last 1
    if ($file) {
        $stream = $null; $reader = $null
        try {
            $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            return $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
        }
        catch { return $null }
        finally { if ($reader) { $reader.Dispose() } elseif ($stream) { $stream.Dispose() } }
    }
}

<#
.SYNOPSIS
对新建辅助进程布置组合采集，以带随机会话标识的请求模拟 D/Q；不操作任何业务 PID。
.PARAMETER Run
本测试运行标签。
.PARAMETER Request
Capture 或 Finish；为空时验证时长到期。
.PARAMETER RealPerfView
真实或测试 PerfView 工具路径。
.OUTPUTS
组合诊断最终状态。
#>
function Invoke-NativeIncidentTest {
    param([string]$Run, [string]$Request, [string]$RealPerfView)
    $helperSeconds = $(if ($Request -eq 'Exit') { 20 } else { 90 })
    $helper = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList "-NoProfile -NonInteractive -Command `"Start-Sleep -Seconds $helperSeconds`"" -WindowStyle Hidden -PassThru
    $pipeline = [Management.Automation.PowerShell]::Create()
    $parameters = @{
        Action = 'Incident'; TargetProcessId = @($helper.Id); RunId = $Run; OutputRoot = $testRoot
        DurationSeconds = $(if ($Request) { 60 } else { 2 }); IntervalSeconds = 1; IncidentDumpIntervalSeconds = 1
        CircularMB = 64; ProcDumpPath = $ProcDumpPath; PerfViewPath = $RealPerfView
    }
    $pipeline.AddCommand($nativeCollector).AddParameters($parameters) | Out-Null
    $pending = $pipeline.BeginInvoke()
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew(); $state = $null
        do {
            $state = Get-TestIncidentState -Run $Run
            if ($pipeline.InvocationStateInfo.State -in @('Failed', 'Completed')) {
                $pipeline.EndInvoke($pending) | Out-Null
                throw '全部就绪前采集提前结束。'
            }
            if ($clock.Elapsed.TotalSeconds -gt 45) { throw '未在 45 秒内全部就绪。' }
            Start-Sleep -Milliseconds 200
        } until ($state -and $state.State -eq 'Ready')
        if ($Request -in @('Capture', 'Finish', 'Interrupt')) {
            # 旧轮请求不能触发：先发错误 SessionId，再发当前轮请求。
            @{ SessionId = 'stale-session'; Action = 'Capture' } | ConvertTo-Json | Set-Content -LiteralPath $state.RequestPath -Encoding UTF8
            Start-Sleep -Seconds 1
            if ((Get-TestIncidentState -Run $Run).State -ne 'Ready') { throw '旧轮请求触发了采集。' }
            if ($Request -eq 'Interrupt') { $pipeline.Stop() }
            else { @{ SessionId = $state.SessionId; Action = $Request } | ConvertTo-Json | Set-Content -LiteralPath $state.RequestPath -Encoding UTF8 }
        }
        try { $pipeline.EndInvoke($pending) | Out-Null }
        catch [Management.Automation.PipelineStoppedException] { if ($Request -ne 'Interrupt') { throw } }
        if ($pipeline.HadErrors -and $Request -ne 'Interrupt') { throw ($pipeline.Streams.Error | Out-String) }
        $final = Get-TestIncidentState -Run $Run
        if ($helper.HasExited -and $Request -ne 'Exit') { throw '组合采集提前结束了目标辅助进程。' }
        if ($final.TraceState -ne 'Saved') { throw '跟踪未归档。' }
        $expected = $(if ($Request -eq 'Capture') { 3 } elseif ($Request -eq 'Exit') { 1 } else { 0 })
        if ($Request -eq 'Interrupt' -and $final.State -ne 'Cancelled') { throw '模拟 Ctrl+C 没有归档 Cancelled。' }
        if ($Request -eq 'Exit' -and $final.Trigger -ne 'ExceptionOrExit') { throw '目标退出未联动归档。' }
        if ($Request -eq 'Exit' -and -not $helper.HasExited) { throw '退出取证场景的辅助进程没有自然退出。' }
        if ($final.Dumps.Count -ne $expected) { throw "预期 $expected 份转储，实际 $($final.Dumps.Count)。" }
        foreach ($dump in $final.Dumps) {
            $stream = [IO.File]::OpenRead($dump.FullName)
            try { $bytes = New-Object byte[] 4; $stream.Read($bytes, 0, 4) | Out-Null; if ([Text.Encoding]::ASCII.GetString($bytes) -ne 'MDMP') { throw '转储文件头无效。' } }
            finally { $stream.Dispose() }
        }
        $watchFile = Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*-watch.json' | Where-Object FullName -Like "*\$Run\*" | Select-Object -Last 1
        $watch = Get-Content -LiteralPath $watchFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if (Get-Process -Id $watch.Targets[0].CollectorId -ErrorAction SilentlyContinue) { throw '组合结束后留下 ProcDump 监听器。' }
        $sampleFile = Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*-sample.json' | Where-Object FullName -Like "*\$Run\*" | Select-Object -Last 1
        $sample = Get-Content -LiteralPath $sampleFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $sample.LastSampleUtc -or $sample.State -ne 'Cancelled') { throw '资源取消没有保存覆盖范围。' }
        $targetState = $(if ($Request -eq 'Exit') { '目标已自然退出' } else { '目标仍存活' })
        Write-Host "通过：$Run；$($final.State)；转储 $expected 份；$targetState，监听已清理。"
        return $final
    }
    finally {
        if ($pipeline.InvocationStateInfo.State -eq 'Running') { $pipeline.Stop() }
        $pipeline.Dispose()
    }
}

try {
    $previewRoot = Join-Path $testRoot 'preview-must-not-exist'
    Invoke-TestWizard -Answers @('1','2','1','reuse-test','3','mock-connect','1','1','120','2',$previewRoot,'2') | Out-Null
    $second = Invoke-TestWizard -Answers @('2','1','2')
    if ($second -notmatch 'reuse-test' -or $second -notmatch '/ThreadTime' -or $second -notmatch 'MaxCollectSec:300') { throw '第二窗口未沿用公共数据或当前用途推荐值。' }
    $global:GatherIncidentFixturePid = 34998; $global:GatherIncidentFixtureCreated = $global:GatherIncidentFixtureCreated.AddSeconds(10)
    $incident = Invoke-TestWizard -Answers @('8','1','1','2')
    if ($incident -notmatch 'PID=34998' -or $incident -notmatch '按 D 才' -or $incident -notmatch '三份') { throw '组合预览未使用新 PID 或触发语义错误。' }
    Invoke-TestWizard -Answers @('8','1','2') | Out-Null
    $saved = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8
    if ($saved -match '3499[89]|ProcessId|CreatedAt|mock-api') { throw '设置文件保存了旧 PID、创建时间或完整启动参数。' }
    $settings = $saved | ConvertFrom-Json
    if (-not $settings.DefaultsByAction.Sample -or -not $settings.DefaultsByAction.Trace -or -not $settings.DefaultsByAction.Incident) { throw '保存当前用途时丢失了其他窗口的参数。' }
    if (Test-Path -LiteralPath $previewRoot) { throw '预览启动了采集或写入证据。' }
    Write-Host '通过：第二窗口仅用途、沿用、确认；重查新 PID；按用途保留参数；预览未采集。'

    Invoke-TestWizard -Answers @('1','3','2','1','api-reuse','4','34998','1','1','120','2',$previewRoot,'2') | Out-Null
    $global:GatherIncidentFixturePid = 34997
    $api = Invoke-TestWizard -Answers @('1','1','2')
    if ($api -notmatch 'PID=34997' -or (Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8) -match 'mock-api|34997|34998') { throw '非服务进程未按签名重查或保存了旧 PID/启动参数。' }
    $global:GatherIncidentFixtureAmbiguous = $true
    $ambiguous = Invoke-TestWizard -Answers @('1','1','4','34997','1','1','120','2',$previewRoot,'2')
    if ($ambiguous -notmatch '无法唯一定位') { throw '多个同路径同启动参数实例没有要求重选。' }
    $global:GatherIncidentFixtureAmbiguous = $false
    $global:GatherIncidentFixtureStopped = $true
    Invoke-TestWizard -Answers @('1','3','2','1','stopped-background','3','mock-connect','1','1','60','1',$previewRoot,'2') | Out-Null
    $global:GatherIncidentFixtureStopped = $false
    $resumed = Invoke-TestWizard -Answers @('1','1','2')
    if ($resumed -notmatch 'PID=34997') { throw '停止的背景服务启动后没有重查身份。' }
    Write-Host '通过：API 按签名重查、歧义重选、停止背景服务加入及启动后重查。'

    $rejected = $false
    try {
        $created = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").CreationDate.ToUniversalTime().AddSeconds(1).ToString('o')
        & $collector -Action Dump -TargetProcessId $PID -ExpectedCreatedAtUtc $created -RunId wrong-identity -OutputRoot $testRoot -DryRun
    }
    catch { if ($_.Exception.Message -match '创建时间与本轮确认不一致') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw '没有拒绝子任务传入的过期创建时间。' }
    Write-Host '通过：过期创建时间在启动采集器前被拒绝。'

    if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        $rejected = $false
        try {
            & $collector -Action Incident -TargetProcessId $PID -RunId admin-required -OutputRoot $testRoot `
                -PerfViewPath (Get-Process -Id $PID).Path -ProcDumpPath (Get-Process -Id $PID).Path
        }
        catch { if ($_.Exception.Message -match '已提升的管理员 PowerShell') { $rejected = $true } else { throw } }
        if (-not $rejected -or (Get-TestIncidentState -Run admin-required).TraceState -ne 'NotStarted') { throw '普通令牌没有在工具启动前被拒绝。' }
        Write-Host '通过：未提升令牌直接拒绝，未自动提权或启动采集器。'
    }

    if ($ProcDumpPath) {
        if (-not (Test-Path -LiteralPath $ProcDumpPath -PathType Leaf)) { throw '真实 ProcDump 不存在。' }
        $tool = $PerfViewPath
        if (-not $tool) {
            $tool = Join-Path $testRoot 'FakePerfView.exe'
            $source = @'
using System;
using System.IO;
/// <summary>测试组合调度的 PerfView 替身，只记录本次参数，不创建 ETW 会话。</summary>
public static class FakePerfView
{
    /// <summary>记录 start/stop 参数，stop 写出明确标记为测试数据的占位文件。</summary>
    /// <param name="args">本测试调用的命令行参数。</param>
    public static void Main(string[] args)
    {
        string file = null, log = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("/DataFile:")) file = arg.Substring(10);
            if (arg.StartsWith("/LogFile:")) log = arg.Substring(9);
        }
        File.WriteAllLines(log, args);
        if (args[args.Length - 1] == "stop") File.WriteAllText(file, "FAKE_ETW_FOR_INCIDENT_WORKFLOW_TEST_ONLY_" + new string('x', 100));
    }
}
'@
            $sourcePath = Join-Path $testRoot 'FakePerfView.cs'
            [IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($true))
            $compilePath = Join-Path $testRoot 'compile.ps1'
            [IO.File]::WriteAllText($compilePath, 'param($Source,$Destination) Add-Type -Path $Source -OutputAssembly $Destination -OutputType ConsoleApplication', [Text.UTF8Encoding]::new($true))
            & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $compilePath -Source $sourcePath -Destination $tool
            if ($LASTEXITCODE -ne 0) { throw '测试工具编译失败。' }
            Write-Host '使用测试 PerfView；以下只验证调度与真实 ProcDump，不宣称验证真实 ETL。'
            # 只有测试 PerfView 时在本次临时副本替换管理员探测，生产脚本及真实 ETW 测试没有绕过权限。
            $nativeCollector = Join-Path $testRoot 'Invoke-GatherV4Diagnostics.ps1'
            Copy-Item -LiteralPath $collector -Destination $nativeCollector
            $libraryPath = Join-Path $scriptsDirectory 'GatherV4.IncidentCapture.ps1'
            $librarySource = Get-Content -LiteralPath $libraryPath -Raw -Encoding UTF8
            $tokens = $null; $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($librarySource, [ref]$tokens, [ref]$parseErrors)
            $guard = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-IncidentAdministrator' }, $true)
            if (-not $guard) { throw '未找到测试所需的管理员探测方法。' }
            $librarySource = $librarySource.Replace($guard.Extent.Text, 'function Test-IncidentAdministrator { return $true }')
            [IO.File]::WriteAllText((Join-Path $testRoot 'GatherV4.IncidentCapture.ps1'), $librarySource, [Text.UTF8Encoding]::new($true))
        }
        Invoke-NativeIncidentTest -Run native-capture -Request Capture -RealPerfView $tool | Out-Null
        Invoke-NativeIncidentTest -Run native-finish -Request Finish -RealPerfView $tool | Out-Null
        Invoke-NativeIncidentTest -Run native-duration -Request '' -RealPerfView $tool | Out-Null
        Invoke-NativeIncidentTest -Run native-interrupt -Request Interrupt -RealPerfView $tool | Out-Null
        Invoke-NativeIncidentTest -Run native-exit -Request Exit -RealPerfView $tool | Out-Null
    }
    Write-Host '组合取证与跨窗口复用验证全部通过。' -ForegroundColor Green
}
finally {
    Remove-Variable -Name GatherIncidentFixturePid,GatherIncidentFixtureCreated,GatherIncidentAnswers,GatherIncidentFixtureAmbiguous,GatherIncidentFixtureStopped -Scope Global -ErrorAction SilentlyContinue
    if ($KeepEvidence) { Write-Host "测试证据：$testRoot" }
    else {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch '^GatherIncidentTest-[a-f0-9]{32}$') { throw '拒绝删除不属于本测试的目录。' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
