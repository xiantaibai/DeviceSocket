<#
.SYNOPSIS
为 GatherV4 组合诊断提供并行资源记录、独立 PerfView 会话及按键现场抓取。
.DESCRIPTION
由 Invoke-GatherV4Diagnostics.ps1 加载，不单独执行。D 保存存活现场，Q 正常结束；没有共享目录也可使用。
只对已确认的主要对象抓 Dump，后台目标仅记录资源；关闭时只停止本次创建的采集任务和 ETW 会话。
#>

<#
.SYNOPSIS
检查当前 Windows 令牌是否已提升，避免 PerfView 自动请求提权后卡在未完成的就绪状态。
.OUTPUTS
管理员令牌返回真；需要在管理员 PowerShell 重新执行时返回假。
#>
function Test-IncidentAdministrator {
    return [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
自动启动三个独立的只读状态窗口；显示窗口失败只记录警告，不创建额外采集器。
.PARAMETER Context
本轮状态路径和随机会话标识；子窗口只能读取本轮证据，不控制业务进程。
.PARAMETER ViewerPath
同目录的状态显示脚本完整路径。
.OUTPUTS
本次创建的显示进程句柄，供结束时释放句柄；采集结束后子窗口自行退出。
#>
function Start-IncidentStatusWindows {
    param([hashtable]$Context, [string]$ViewerPath)
    $windows = @(); $Context.MonitorWindows = @()
    foreach ($panel in @('Resources', 'Trace', 'Dump')) {
        $entry = @{ Panel = $panel; State = 'Starting'; ProcessId = $null; CreatedAtUtc = $null; Error = $null }
        try {
            $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ViewerPath,
                '-StatePath', "$captureBase-incident.json", '-ExpectedSessionId', $Context.SessionId, '-Panel', $panel)
            if ($arguments | Where-Object { $_ -match '["\r\n]' }) { throw '状态窗口参数不能包含双引号或换行。' }
            # 用户明确选择自动打开可见监控窗口；采集工具自身仍隐藏，仅启动只读显示进程。
            $process = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -ArgumentList (($arguments | ForEach-Object { '"{0}"' -f $_ }) -join ' ') -WindowStyle Normal -PassThru
            $entry.ProcessId = $process.Id; $entry.CreatedAtUtc = $process.StartTime.ToUniversalTime().ToString('o'); $entry.State = 'Opened'
            $windows += $process
        }
        catch { $entry.State = 'Failed'; $entry.Error = $_.Exception.Message; Write-Warning "$panel 状态窗口无法打开：$($_.Exception.Message)。采集仍由主窗口管理。" }
        $Context.MonitorWindows += $entry
    }
    return $windows
}

<#
.SYNOPSIS
在独立 PowerShell 流水线调用既有采集脚本，保持工具日志、身份校验和安全取消行为一致。
.PARAMETER CollectorPath
底层采集脚本的完整路径。
.PARAMETER Arguments
此子任务的独立参数，Stage 必须为本次组合诊断唯一标签。
.PARAMETER LogPath
子任务结束时归档 PowerShell 输出和错误的位置。
.OUTPUTS
流水线、异步句柄、阶段及日志状态。
#>
function Start-IncidentTask {
    param([string]$CollectorPath, [hashtable]$Arguments, [string]$LogPath)
    $pipeline = [Management.Automation.PowerShell]::Create()
    try {
        $pipeline.AddCommand($CollectorPath).AddParameters($Arguments) | Out-Null
        return [pscustomobject]@{
            Pipeline = $pipeline; Pending = $pipeline.BeginInvoke(); Stage = $Arguments.Stage
            LogPath = $LogPath; Finished = $false
        }
    }
    catch { $pipeline.Dispose(); throw }
}

<#
.SYNOPSIS
结束属于本次组合诊断的子任务；DumpWatch 的 finally 使用官方取消协议，不强杀 ProcDump。
.PARAMETER Worker
本次创建的子任务；为空或已经释放时不重复结束。
#>
function Stop-IncidentTask {
    param([object]$Worker)
    if (-not $Worker -or $Worker.Finished) { return }
    try {
        if ($Worker.Pipeline.InvocationStateInfo.State -in @('Running', 'Stopping')) { $Worker.Pipeline.Stop() }
        try { $Worker.Pipeline.EndInvoke($Worker.Pending) | Out-Null }
        catch [Management.Automation.PipelineStoppedException] { }
        finally {
            @($Worker.Pipeline.Streams.Information | ForEach-Object { "$($_.MessageData)" }) +
                @($Worker.Pipeline.Streams.Warning | ForEach-Object { "警告：$_" }) +
                @($Worker.Pipeline.Streams.Error | ForEach-Object { "错误：$_" }) |
                Set-Content -LiteralPath $Worker.LogPath -Encoding UTF8
        }
    }
    finally { $Worker.Finished = $true; $Worker.Pipeline.Dispose() }
}

<#
.SYNOPSIS
读取子任务已落盘的状态；首次写入或覆盖期间的半份 JSON 视为暂时不可读。
.PARAMETER Worker
状态所属子任务。
.PARAMETER Suffix
sample 或 watch，决定状态文件后缀。
.OUTPUTS
可解析的状态对象；尚未落盘时为空。
#>
function Get-IncidentTaskState {
    param([object]$Worker, [string]$Suffix)
    $file = Get-ChildItem -LiteralPath $evidenceDirectory -Filter "$($Worker.Stage)-*-$Suffix.json" -File |
        Sort-Object LastWriteTimeUtc | Select-Object -Last 1
    if ($file) {
        try { return Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
        catch { return $null }
    }
}

<#
.SYNOPSIS
检查子任务是否提前失败，不能在资源或退出监听已经失效后仍显示全部就绪。
.PARAMETER Worker
当前组合诊断的子任务。
#>
function Assert-IncidentTaskRunning {
    param([object]$Worker)
    if ($Worker.Pipeline.InvocationStateInfo.State -in @('Failed', 'Stopped')) {
        throw "子任务 $($Worker.Stage) 已停止：$($Worker.Pipeline.InvocationStateInfo.Reason) $($Worker.Pipeline.Streams.Error | Out-String)"
    }
}

<#
.SYNOPSIS
以本轮唯一 SessionName 启动或停止 PerfView；隐藏辅助进程，不操作其他会话。
.PARAMETER Context
包含独立会话名、跟踪路径及工具状态的组合诊断上下文。
.PARAMETER Command
start 布置循环采集，stop 停止并合并压缩；命令的输出文件通过 DataFile 指定。
.OUTPUTS
本次创建的 PerfView 进程句柄。
#>
function Start-IncidentPerfViewCommand {
    param([hashtable]$Context, [ValidateSet('start', 'stop')][string]$Command)
    $arguments = @('/AcceptEula', '/NoGui', '/NoView', "/SessionName:$($Context.TraceSession)",
        "/DataFile:$($Context.TracePath)", "/LogFile:$captureBase-perfview-$Command.log", '/ThreadTime')
    if ($Command -eq 'start') { $arguments += "/CircularMB:$CircularMB" }
    else { $arguments += @('/Merge:true', '/Zip:true') }
    $arguments += $Command
    if ($arguments | Where-Object { $_ -match '["\r\n]' }) { throw 'PerfView 参数不能包含双引号或换行。' }
    Write-Host ("PerfView $Command：会话 $($Context.TraceSession)，文件 $($Context.TracePath)")
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $PerfViewPath; $info.Arguments = ($arguments | ForEach-Object { '"{0}"' -f $_ }) -join ' '
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    return [Diagnostics.Process]::Start($info)
}

<#
.SYNOPSIS
等待本次 PerfView 命令完成；合并期间定期提示，非零退出保留日志并明确失败。
.PARAMETER Process
本次创建的 PerfView 命令进程。
.PARAMETER Label
用于区分启动与合并的提示。
.PARAMETER TimeoutSeconds
启动时可限定等待秒数；零表示合并时等待工具完成，不强杀采集器。
#>
function Wait-IncidentPerfViewCommand {
    param([object]$Process, [string]$Label, [int]$TimeoutSeconds = 0)
    $clock = [Diagnostics.Stopwatch]::StartNew(); $nextNotice = 30
    while (-not $Process.WaitForExit(200)) {
        if ($TimeoutSeconds -gt 0 -and $clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw "PerfView $Label 超过 $TimeoutSeconds 秒，请检查本轮工具日志。" }
        if ($clock.Elapsed.TotalSeconds -ge $nextNotice) { Write-Host "PerfView $Label 仍在进行，已等待 $nextNotice 秒。"; $nextNotice += 30 }
    }
    if ($Process.ExitCode -ne 0) { throw "PerfView $Label 失败，退出码 $($Process.ExitCode)，请检查本轮工具日志。" }
}

<#
.SYNOPSIS
读取本窗口 D/Q 按键，或同一服务器另一窗口写入的本轮请求文件；请求不是自动业务探测。
.PARAMETER Context
含本轮随机 SessionId 和请求路径的状态；旧轮、格式错误或未知动作不会触发抓取。
.OUTPUTS
Capture、Finish 或空值；重复无效文件只提示一次。
#>
function Read-IncidentRequest {
    param([hashtable]$Context)
    try {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true).Key
            if ($key -eq [ConsoleKey]::D) { return 'Capture' }
            if ($key -eq [ConsoleKey]::Q) { return 'Finish' }
        }
    }
    catch [InvalidOperationException] { }
    catch [IO.IOException] { }
    if (Test-Path -LiteralPath $Context.RequestPath -PathType Leaf) {
        try {
            $text = Get-Content -LiteralPath $Context.RequestPath -Raw -Encoding UTF8
            $request = $text | ConvertFrom-Json -ErrorAction Stop
            if ($request.SessionId -eq $Context.SessionId -and $request.Action -in @('Capture', 'Finish')) { return $request.Action }
            if ($Context.LastInvalidRequest -ne $text) { Write-Warning '忽略旧轮或无效取证请求；必须使用当前 incident.json 中的 SessionId。'; $Context.LastInvalidRequest = $text }
        }
        catch { }
    }
}

<#
.SYNOPSIS
落盘组合诊断状态、取证时点和文件清单，区分全部就绪、人工取证、退出取证和失败。
.PARAMETER Context
本次组合诊断的可序列化状态；只保存身份及工具证据，不把 Running 当作通讯健康。
#>
function Save-IncidentState {
    param([hashtable]$Context)
    $record = @{}
    foreach ($key in $Context.Keys) { if ($key -notin @('LastInvalidRequest')) { $record[$key] = $Context[$key] } }
    $record.UpdatedAtUtc = [DateTime]::UtcNow.ToString('o')
    $record.Artifacts = @(Get-ChildItem -LiteralPath $evidenceDirectory -File -Recurse |
        Where-Object { $_.Name.StartsWith((Split-Path -Leaf $captureBase)) -or $_.FullName.StartsWith((Join-Path $evidenceDirectory $Context.ChildStage)) } |
        Select-Object FullName, Length, LastWriteTimeUtc)
    $json = $record | ConvertTo-Json -Depth 9
    # 用同目录临时文件替换，另一个窗口轮询时不会读到半份状态。
    $temporary = "$captureBase-incident.tmp"
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($true))
    $destination = "$captureBase-incident.json"
    if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $destination) }
}

<#
.SYNOPSIS
组合布置资源、线程 ETW 及主要对象退出监听；人工按 D 时安全切换到三份存活转储。
.PARAMETER Records
本轮已确认的服务和 PID 记录；主要转储对象必须处于运行状态，背景服务允许停止。
.PARAMETER CollectorPath
本次底层脚本路径，供独立流水线复用已有 Sample、DumpWatch 和 DumpSeries。
#>
function Invoke-IncidentCapture {
    param([object[]]$Records, [string]$CollectorPath)
    if (-not $IncidentTargetSelector -and $Records.Count -eq 1) { $IncidentTargetSelector = $Records[0].Selector }
    $primary = @($Records | Where-Object Selector -eq $IncidentTargetSelector)
    if ($primary.Count -ne 1 -or -not $primary[0].Process) { throw '组合诊断必须指定一个正在运行的主要转储对象，例如 service:GatherConnectService-8890。' }
    foreach ($tool in @($PerfViewPath, $ProcDumpPath)) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "工具不存在：$tool" } }
    $viewerPath = Join-Path $PSScriptRoot 'Watch-GatherV4Monitor.ps1'
    if ($ShowMonitorWindows -and -not (Test-Path -LiteralPath $viewerPath -PathType Leaf)) { throw '缺少 Watch-GatherV4Monitor.ps1，请复制完整四脚本包。' }
    $target = $primary[0].Process
    $sessionId = [guid]::NewGuid().ToString('N')
    $context = @{
        SessionId = $sessionId; RunId = $RunId; Version = $VersionTag; Scenario = $Scenario
        State = 'Starting'; StartedAtUtc = [DateTime]::UtcNow.ToString('o'); ReadyAtUtc = $null
        PrimarySelector = $primary[0].Selector; PrimaryProcessId = $target.ProcessId
        PrimaryCreatedAtUtc = $target.CreationDate.ToUniversalTime().ToString('o'); PrimaryPath = $target.ExecutablePath
        ResourceSelectors = @($Records.Selector); TraceSession = "GatherV4Incident-$sessionId"
        TracePath = "$captureBase.etl"; TraceState = 'NotStarted'; Trigger = $null; TriggeredAtUtc = $null
        TraceStopRequestedAtUtc = $null; Dumps = @(); Error = $null
        RequestPath = "$captureBase-request.json"; ChildStage = "$Stage-$captureStamp"
        DumpIntervalSeconds = $IncidentDumpIntervalSeconds; DurationSeconds = $DurationSeconds
        CoordinatorProcessId = $PID; CoordinatorCreatedAtUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')
        ResourceCsv = $null; DumpLog = $null; ResourceState = 'NotStarted'; DumpState = 'NotStarted'
        PerfViewStartLog = "$captureBase-perfview-start.log"; PerfViewStopLog = "$captureBase-perfview-stop.log"
        CaptureState = 'Waiting'; MonitorWindowsRequested = [bool]$ShowMonitorWindows; MonitorWindows = @()
    }
    if ($DryRun) {
        Write-Host "组合诊断预览：先并行资源记录 + 独立整机 Thread 跟踪 + PID $($target.ProcessId) 异常/退出监听。"
        Write-Host "按 D 才安全取消此监听并抓三份存活 Dump，间隔 $IncidentDumpIntervalSeconds 秒；按 Q 仅归档；上限 $DurationSeconds 秒。"
        Write-Host "资源对象：$($Records.Selector -join ', ')；主要转储：$IncidentTargetSelector；不启动任何采集器。"
        if ($ShowMonitorWindows) { Write-Host '执行时自动打开 Resources / Trace / Dump 三个只读状态窗口；本预览不打开窗口。' }
        return
    }
    if (-not (Test-IncidentAdministrator)) {
        $context.State = 'Failed'
        $context.Error = '一键取证需要已提升的管理员 PowerShell。请右键以管理员身份运行；脚本不自动提权，也不会在此令牌下启动采集器。'
        Save-IncidentState -Context $context
        throw $context.Error
    }
    $mutex = [Threading.Mutex]::new($false, 'Global\GatherV4IncidentCapture')
    $ownsMutex = $false; $sampleWorker = $null; $watchWorker = $null; $startProcess = $null; $stopProcess = $null
    $traceRequested = $false; $failure = $null
    $statusProcesses = @()
    try {
        try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw '本机已有组合诊断窗口；请在原窗口按 D 或 Q，不要重复布置。' }
        Save-IncidentState -Context $context
        if ($ShowMonitorWindows) {
            $statusProcesses = @(Start-IncidentStatusWindows -Context $context -ViewerPath $viewerPath)
            Save-IncidentState -Context $context
        }
        $commonArguments = @{ RunId = $RunId; VersionTag = $VersionTag; Scenario = $Scenario; OutputRoot = $OutputRoot }
        $watchArguments = $commonArguments.Clone()
        $watchArguments.Action = 'DumpWatch'; $watchArguments.TargetProcessId = @([int]$target.ProcessId)
        $watchArguments.ExpectedCreatedAtUtc = $context.PrimaryCreatedAtUtc
        $watchArguments.Stage = "$($context.ChildStage)-exit"; $watchArguments.ProcDumpPath = $ProcDumpPath
        $watchArguments.DumpTrigger = 'ExceptionOrExit'; $watchArguments.DumpLimit = 1
        $watchArguments.DurationSeconds = [int][Math]::Min(86400, $DurationSeconds + 600)
        $watchWorker = Start-IncidentTask -CollectorPath $CollectorPath -Arguments $watchArguments -LogPath "$captureBase-exit-task.log"
        $sampleArguments = $commonArguments.Clone()
        $sampleArguments.Action = 'Sample'; $sampleArguments.ServiceName = $ServiceName; $sampleArguments.TargetProcessId = $TargetProcessId
        $sampleArguments.Stage = "$($context.ChildStage)-resources"; $sampleArguments.IntervalSeconds = $IntervalSeconds
        $sampleArguments.DurationSeconds = [int][Math]::Min(86400, $DurationSeconds + 600)
        $sampleWorker = Start-IncidentTask -CollectorPath $CollectorPath -Arguments $sampleArguments -LogPath "$captureBase-resource-task.log"
        $startup = [Diagnostics.Stopwatch]::StartNew()
        do {
            Assert-IncidentTaskRunning -Worker $watchWorker; Assert-IncidentTaskRunning -Worker $sampleWorker
            $watchState = Get-IncidentTaskState -Worker $watchWorker -Suffix watch
            $sampleState = Get-IncidentTaskState -Worker $sampleWorker -Suffix sample
            if ($watchState.State -eq 'Failed') { throw '退出监听启动失败，请检查本轮日志。' }
            if ($startup.Elapsed.TotalSeconds -ge 45) { throw '45 秒内资源记录和退出监听没有全部就绪，不能开始本轮压测。' }
            Start-Sleep -Milliseconds 200
        } until ($watchState -and $watchState.State -eq 'Armed' -and $sampleState -and $sampleState.State -eq 'Ready')
        $context.ResourceCsv = $sampleState.Csv; $context.ResourceState = 'Ready'; $context.DumpState = 'Armed'
        $context.DumpLog = $watchState.Targets[0].StdOut
        Save-IncidentState -Context $context
        # 独立会话名避免 stop 结束别人的 PerfView；失败时也只清理自己的会话。
        $traceRequested = $true; $context.TraceState = 'Starting'
        $startProcess = Start-IncidentPerfViewCommand -Context $context -Command start
        Wait-IncidentPerfViewCommand -Process $startProcess -Label '启动' -TimeoutSeconds 60
        $context.TraceState = 'Collecting'; $context.State = 'Ready'; $context.ReadyAtUtc = [DateTime]::UtcNow.ToString('o')
        Save-IncidentState -Context $context
        Write-Host '全部采集已就绪，现在再启动压测。出现心跳停滞/集中掉线时，在本窗口按 D；正常结束按 Q。' -ForegroundColor Green
        Write-Host "主要转储：$IncidentTargetSelector；其他对象只采资源。状态：$captureBase-incident.json"
        if ($ShowMonitorWindows) { Write-Host '资源、PerfView、Dump 状态窗口已自动布置；D/Q 在主窗口操作，子窗口不控制采集。' }
        Write-Host '不要另开 PerfView 或对主要对象重复启动 ProcDump。Ctrl+C 只结束并归档，不代表要抓存活 Dump。'
        $requestLiteral = $context.RequestPath.Replace("'", "''")
        Write-Host "无可用交互键盘时，可在另一窗口写请求：@{ SessionId='$sessionId'; Action='Capture' } | ConvertTo-Json | Set-Content -LiteralPath '$requestLiteral' -Encoding UTF8"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
            Assert-IncidentTaskRunning -Worker $watchWorker; Assert-IncidentTaskRunning -Worker $sampleWorker
            if ($sampleWorker.Pipeline.InvocationStateInfo.State -eq 'Completed') { throw '资源记录提前结束，请检查采样状态和本轮时长。' }
            if ($watchWorker.Pipeline.InvocationStateInfo.State -eq 'Completed') {
                $watchState = Get-IncidentTaskState -Worker $watchWorker -Suffix watch
                $context.Dumps = @($watchState.Targets | ForEach-Object { $_.Dumps })
                if ($watchState.State -ne 'Completed' -or $context.Dumps.Count -eq 0) { throw '退出监听提前结束且没有有效转储，请检查本轮工具日志。' }
                $context.Trigger = 'ExceptionOrExit'; break
            }
            $request = Read-IncidentRequest -Context $context
            if ($request -eq 'Capture') { $context.Trigger = 'ManualIncident'; break }
            if ($request -eq 'Finish') { $context.Trigger = 'NormalFinish'; break }
            Start-Sleep -Milliseconds 200
        }
        if (-not $context.Trigger) { $context.Trigger = 'DurationLimit' }
        $context.TriggeredAtUtc = [DateTime]::UtcNow.ToString('o'); $context.State = 'Finalizing'
        $context.TraceStopRequestedAtUtc = [DateTime]::UtcNow.ToString('o')
        # 先在后台停止跟踪以保存故障前循环窗口，合并与取消/转储并行，避免先等合并再丢失现场。
        $stopProcess = Start-IncidentPerfViewCommand -Context $context -Command stop
        $context.TraceState = 'Stopping'; Save-IncidentState -Context $context
        $context.CaptureState = 'CancellingExitWatch'; Save-IncidentState -Context $context
        Stop-IncidentTask -Worker $watchWorker
        $context.DumpState = 'Stopped'
        $watchState = Get-IncidentTaskState -Worker $watchWorker -Suffix watch
        $context.Dumps = @($watchState.Targets | ForEach-Object { $_.Dumps })
        if ($context.Trigger -eq 'ManualIncident') {
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($target.ProcessId)"
            if (-not $current -or $current.CreationDate -ne $target.CreationDate) { throw '按键取证时主要对象已退出或身份改变，保留退出/跟踪证据；不得给复用 PID 抓存活 Dump。' }
            Write-Host '退出监听已安全结束，开始抓三份完整存活 Dump。' -ForegroundColor Cyan
            $context.CaptureState = 'WritingDumpSeries'; Save-IncidentState -Context $context
            $dumpArguments = $commonArguments.Clone()
            $dumpArguments.Action = 'DumpSeries'; $dumpArguments.TargetProcessId = @([int]$target.ProcessId)
            $dumpArguments.ExpectedCreatedAtUtc = $context.PrimaryCreatedAtUtc
            $dumpArguments.Stage = "$($context.ChildStage)-incident"; $dumpArguments.IntervalSeconds = $IncidentDumpIntervalSeconds
            $dumpArguments.ProcDumpPath = $ProcDumpPath
            & $CollectorPath @dumpArguments
            $context.Dumps += @(Get-ChildItem -LiteralPath $evidenceDirectory -Filter "$($dumpArguments.Stage)-*.dmp" -File |
                Select-Object FullName, Length, LastWriteTimeUtc)
            if (@($context.Dumps | Where-Object { $_.FullName -like "*$($dumpArguments.Stage)-*" }).Count -ne 3) { throw '存活 Dump 系列未完整生成三份，请检查保留的部分证据。' }
        }
        $context.State = $(if ($context.Trigger -eq 'DurationLimit') { 'TimedOut' } else { 'Completed' })
        $context.CaptureState = 'Finished'
    }
    catch {
        $failure = $_
        $context.State = $(if ($_.Exception -is [Management.Automation.PipelineStoppedException] -or $_.Exception -is [OperationCanceledException]) { 'Cancelled' } else { 'Failed' })
        $context.Error = $_.Exception.Message
    }
    finally {
        if ($ownsMutex) {
            # PowerShell 停止流水线时可能直接进入 finally 而不经过 catch；未到终态必须归档为取消。
            if ($context.State -in @('Starting', 'Ready', 'Finalizing')) {
                $context.State = 'Cancelled'
                if (-not $context.Trigger) { $context.Trigger = 'Cancelled' }
                if (-not $context.TriggeredAtUtc) { $context.TriggeredAtUtc = [DateTime]::UtcNow.ToString('o') }
            }
            try {
                foreach ($worker in @($watchWorker, $sampleWorker)) {
                    try { Stop-IncidentTask -Worker $worker } catch { Write-Warning "子任务结束失败：$($_.Exception.Message)"; $context.State = 'Failed'; $context.Error = $_.Exception.Message }
                }
                $context.ResourceState = 'Stopped'; $context.DumpState = 'Stopped'
                try {
                    if ($traceRequested -and -not $stopProcess) {
                        $context.TraceStopRequestedAtUtc = [DateTime]::UtcNow.ToString('o')
                        $stopProcess = Start-IncidentPerfViewCommand -Context $context -Command stop
                    }
                    if ($stopProcess) {
                        Wait-IncidentPerfViewCommand -Process $stopProcess -Label '停止及合并'
                        $traceFiles = @(Get-Item -LiteralPath "$captureBase.etl", "$captureBase.etl.zip" -ErrorAction SilentlyContinue | Where-Object Length -gt 32)
                        if ($traceFiles.Count -eq 0) { throw 'PerfView 返回成功但未生成有效跟踪文件。' }
                        $context.TraceState = 'Saved'
                    }
                }
                catch { $context.TraceState = 'Failed'; $context.State = 'Failed'; $context.Error = $_.Exception.Message; if (-not $failure) { $failure = $_ } }
                try { Save-Identity -Records @(Get-TargetRecords -AllowStopped) -Label after } catch { Write-Warning "结束身份归档失败：$($_.Exception.Message)" }
                try { Export-DumpWatchEvents -StartedAtUtc ([DateTimeOffset]::Parse($context.StartedAtUtc).UtcDateTime) } catch { Write-Warning "事件归档失败：$($_.Exception.Message)" }
                # 启动失败或目标退出时也保留已生成的部分转储，不能只在人工抓取成功分支登记。
                $context.Dumps = @(Get-ChildItem -LiteralPath $evidenceDirectory -Filter '*.dmp' -File -Recurse |
                    Where-Object { $_.FullName.StartsWith((Join-Path $evidenceDirectory $context.ChildStage)) } |
                    Select-Object FullName, Length, LastWriteTimeUtc)
                Save-IncidentState -Context $context
                Write-Host "组合诊断结束：$($context.State)，完整目录：$evidenceDirectory"
            }
            finally { $mutex.ReleaseMutex() }
        }
        if ($startProcess -and $startProcess.HasExited) { $startProcess.Dispose() }
        if ($stopProcess -and $stopProcess.HasExited) { $stopProcess.Dispose() }
        # 子窗口只读终态并自行退出；不强杀窗口进程，也不把关显示窗口视为停止采集。
        foreach ($process in $statusProcesses) { $process.Dispose() }
        $mutex.Dispose()
    }
    if ($failure) { throw $failure }
    if ($context.State -eq 'Failed') { throw $context.Error }
}
