<#
.SYNOPSIS
通过中文交互选择当前窗口用途、场景、目标实例和时长，再调用 GatherV4 采集脚本。
.DESCRIPTION
在被测服务器的管理员 PowerShell 中运行，与 Invoke-GatherV4Diagnostics.ps1 放在同一目录。
端口只查询本机监听进程；多个候选必须明确选择。每次重新查询目标，不从设置文件恢复旧 PID。
工具安装路径统一维护在脚本顶部，存在时直接使用，仅找不到文件时询问本次替代路径。
本轮元数据、目标定位方式和各用途参数保存为 JSON；第二个窗口可沿用并重新解析当前 PID，不启动压测或重启服务。
.PARAMETER CollectorPath
底层采集脚本路径，默认使用同目录 Invoke-GatherV4Diagnostics.ps1。
.PARAMETER SessionPath
本机默认设置文件路径，默认同目录 GatherV4.Diagnostics.Session.json；不属于 LoadTester 场景配置。
.PARAMETER DryRun
强制仅预览，任何最终选择都不会启动实际采集器。
.PARAMETER NoSave
不保存默认设置；用于只读预览或脚本目录不可写的环境。
.PARAMETER NoMonitorWindows
一键模式只保留主窗口，不自动打开三个状态窗口；适合远程控制台，采集内容不变。
.EXAMPLE
.\Start-GatherV4Diagnostics.ps1
.EXAMPLE
.\Start-GatherV4Diagnostics.ps1 -DryRun -NoSave
#>
[CmdletBinding()]
param(
    [string]$CollectorPath = (Join-Path $PSScriptRoot 'Invoke-GatherV4Diagnostics.ps1'),
    [string]$SessionPath = (Join-Path $PSScriptRoot 'GatherV4.Diagnostics.Session.json'),
    [switch]$DryRun,
    [switch]$NoSave,
    [switch]$NoMonitorWindows
)

$ErrorActionPreference = 'Stop'

# 固定安装配置：首次部署到测试服务器时改成实际完整路径，后续压测无需逐次填写。
# PerfViewPath 指向 PerfView.exe；ProcDumpPath 指向 Sysinternals ProcDump 的可执行文件。
# 这两项不从会话 JSON 恢复，避免旧默认值覆盖这里维护的固定安装路径。
$configuredToolPaths = @{
    PerfViewPath = 'C:\Tools\PerfView\PerfView.exe'
    ProcDumpPath = 'C:\Tools\ProcDump\procdump64.exe'
}

<#
.SYNOPSIS
读取中文问题的回答；输入 q 时取消当前向导，不对目标进程执行操作。
.PARAMETER Prompt
显示给操作人员的完整问题。
.PARAMETER Default
回车时使用的默认值；为空时要求输入。
.OUTPUTS
去除首尾空格的输入字符串。
#>
function Read-WizardAnswer {
    param([string]$Prompt, [string]$Default = '')
    $suffix = $(if ($Default) { " [回车=$Default，q=退出]" } else { ' [q=退出]' })
    while ($true) {
        $answer = (Read-Host "$Prompt$suffix").Trim()
        if ($answer -ieq 'q') { throw [OperationCanceledException]::new('已取消，未启动新的采集。') }
        if (-not $answer) { $answer = $Default }
        if ($answer) { return $answer }
        Write-Host '请填写一个值。' -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
显示带编号的选项并校验回答，避免非法编号落入其他动作。
.PARAMETER Prompt
当前选择的业务问题。
.PARAMETER Options
按编号顺序排列的选项文本。
.PARAMETER Default
默认编号；零表示必须明确输入，不自动选择多个实例中的某一个。
.OUTPUTS
从一开始的有效选项编号。
#>
function Read-WizardChoice {
    param([string]$Prompt, [string[]]$Options, [int]$Default = 0)
    Write-Host "`n$Prompt" -ForegroundColor Cyan
    for ($index = 0; $index -lt $Options.Count; $index++) { Write-Host "  $($index + 1). $($Options[$index])" }
    while ($true) {
        $defaultText = $(if ($Default -gt 0) { "$Default" } else { '' })
        $answer = Read-WizardAnswer -Prompt '输入编号' -Default $defaultText
        $number = 0
        if ([int]::TryParse($answer, [ref]$number) -and $number -ge 1 -and $number -le $Options.Count) { return $number }
        Write-Host "请输入 1～$($Options.Count) 的编号。" -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
读取有范围的整数，单位显示在问题中，非法输入允许重新填写。
.PARAMETER Prompt
包含单位和用途的问题。
.PARAMETER Default
回车使用的推荐值。
.PARAMETER Minimum
允许的最小值。
.PARAMETER Maximum
允许的最大值。
.OUTPUTS
范围内的整数。
#>
function Read-WizardNumber {
    param([string]$Prompt, [int]$Default, [int]$Minimum, [int]$Maximum)
    while ($true) {
        $defaultText = $(if ($Default -ge $Minimum -and $Default -le $Maximum) { "$Default" } else { '' })
        $answer = Read-WizardAnswer -Prompt $Prompt -Default $defaultText
        $number = 0
        if ([int]::TryParse($answer, [ref]$number) -and $number -ge $Minimum -and $number -le $Maximum) { return $number }
        Write-Host "请输入 $Minimum～$Maximum 的整数。" -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
读取与底层脚本一致的文件标签，防止 RunId 或 Stage 生成越界路径。
.PARAMETER Prompt
标签用途；RunId 必须说明与负载窗口相同。
.PARAMETER Default
建议标签。
.OUTPUTS
仅含英文字母、数字、下划线、点和横线的标签。
#>
function Read-WizardTag {
    param([string]$Prompt, [string]$Default)
    while ($true) {
        $answer = Read-WizardAnswer -Prompt $Prompt -Default $Default
        if ($answer -match '^[A-Za-z0-9_.-]+$') { return $answer }
        Write-Host '仅使用英文字母、数字、下划线、点和横线。' -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
读取绝对路径；工具必须已经存在，输出目录可以在执行采集时新建。
.PARAMETER Prompt
工具或目录的用途。
.PARAMETER Default
上次路径或安装路径建议。
.PARAMETER ExistingFile
要求为已存在的文件，不接受不存在的采集工具。
.OUTPUTS
已规范化的绝对路径。
#>
function Read-WizardPath {
    param([string]$Prompt, [string]$Default, [switch]$ExistingFile)
    while ($true) {
        $answer = (Read-WizardAnswer -Prompt $Prompt -Default $Default).Trim('"')
        try {
            if (-not [IO.Path]::IsPathRooted($answer) -or $answer -match '["\r\n]') { throw '请输入完整绝对路径。' }
            $path = [IO.Path]::GetFullPath($answer)
            if ($ExistingFile -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw '文件不存在。' }
            if (-not $ExistingFile -and (Test-Path -LiteralPath $path -PathType Leaf)) { throw '输出路径是文件，请填写目录。' }
            return $path
        }
        catch { Write-Host $_.Exception.Message -ForegroundColor Yellow }
    }
}

<#
.SYNOPSIS
优先使用脚本顶部维护的固定工具路径；路径无效时只询问本次替代路径。
.PARAMETER ToolName
显示给操作人员的工具名称。
.PARAMETER ConfiguredPath
脚本顶部配置的绝对文件路径，不读取会话文件中遗留的安装路径。
.OUTPUTS
已存在的工具绝对路径；本次替代值不会保存，固定修改应维护顶部配置。
#>
function Get-WizardToolPath {
    param([string]$ToolName, [string]$ConfiguredPath)
    try {
        if ([IO.Path]::IsPathRooted($ConfiguredPath) -and $ConfiguredPath -notmatch '["\r\n]') {
            $fullPath = [IO.Path]::GetFullPath($ConfiguredPath)
            if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
                Write-Host "使用已配置的 $ToolName：$fullPath"
                return $fullPath
            }
        }
    }
    catch { Write-Host "固定路径校验失败：$($_.Exception.Message)" -ForegroundColor Yellow }
    Write-Host "未找到已配置的 $ToolName：$ConfiguredPath" -ForegroundColor Yellow
    Write-Host '请填写本次替代路径；要固定使用新位置，请修改脚本顶部 configuredToolPaths。'
    return Read-WizardPath -Prompt "$ToolName 完整路径？" -Default $ConfiguredPath -ExistingFile
}

<#
.SYNOPSIS
读取设置文件中的一个默认值，缺失时使用当前版本的推荐值。
.PARAMETER Name
设置字段名称。
.PARAMETER Fallback
没有保存值时的推荐默认值。
.OUTPUTS
有效的非空字符串。
#>
function Get-WizardDefault {
    param([string]$Name, [string]$Fallback)
    if ($savedSettings -and $savedSettings.PSObject.Properties[$Name] -and
        -not [string]::IsNullOrWhiteSpace([string]$savedSettings.$Name)) { return [string]$savedSettings.$Name }
    return $Fallback
}

<#
.SYNOPSIS
计算定位字符串的稳定签名，不把 API 启动参数中的潜在凭据写入设置文件。
.PARAMETER Text
服务或进程的定位文本。
.OUTPUTS
SHA-256 十六进制签名。
#>
function Get-WizardSignature {
    param([string]$Text)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '') }
    finally { $hash.Dispose() }
}

<#
.SYNOPSIS
取得目标不含 PID 的设置键，服务按内部名称，非服务按路径和命令行签名定位。
.PARAMETER Target
当前重新解析的目标。
.OUTPUTS
跨窗口可复用且不包含旧 PID 的目标键。
#>
function Get-WizardTargetKey {
    param([object]$Target)
    if ($Target.ServiceName) { return "service:$($Target.ServiceName)" }
    return 'process:' + (Get-WizardSignature -Text "$($Target.Name)|$($Target.Path)|$($Target.CommandLine)")
}

<#
.SYNOPSIS
解析服务当前身份，资源及组合诊断允许加入暂时停止的背景服务。
.PARAMETER Service
刚从 Win32_Service 读取的服务记录。
.PARAMETER Source
本次选择来源，用于摘要核对。
.OUTPUTS
可跟随服务启动的目标；非资源用途不能选择停止状态。
#>
function Get-WizardServiceTarget {
    param([object]$Service, [string]$Source)
    if ($Service.State -eq 'Running' -and $Service.ProcessId -gt 0) {
        return Get-WizardTarget -ProcessNumber $Service.ProcessId -ExactServiceName $Service.Name -Source $Source
    }
    if ($action -notin @('Sample', 'Incident')) { throw "服务 $($Service.Name) 未运行，本用途需要正在运行的进程。" }
    $match = [regex]::Match($Service.PathName, '^"([^\"]+\.exe)"|^(.+?\.exe)(?:\s|$)', 'IgnoreCase')
    if (-not $match.Success) { throw '无法解析停止服务的部署路径，请核对注册信息。' }
    $path = $(if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value })
    $name = [IO.Path]::GetFileName($path)
    if ($name -eq 'Gather.TcpCenter.WinService.exe') { throw '本次压测不包含 TcpCenter。' }
    return [pscustomobject]@{
        ServiceName = $Service.Name; ProcessId = 0; CreatedAt = $null; State = $Service.State
        Name = $name; Path = $path; CommandLine = ''; Source = $Source
    }
}

<#
.SYNOPSIS
从保存的定位方式重新查询实例；旧 PID 不保存、不恢复，不能唯一定位时要求重新选择。
.OUTPUTS
当前目标数组；任一目标失效时整组重选，避免悄悄遗漏原监控对象。
#>
function Restore-WizardTargets {
    $restored = @()
    foreach ($entry in @($savedSettings.Targets)) {
        if ($entry.ServiceName) {
            $service = Get-CimInstance Win32_Service | Where-Object Name -eq $entry.ServiceName
            if (-not $service) { throw "已保存服务不存在：$($entry.ServiceName)" }
            $target = Get-WizardServiceTarget -Service $service -Source '本轮服务设置（重新查询 PID）'
            if ($entry.Path -and $target.Path -ine $entry.Path) { throw "服务部署路径已变化：$($entry.ServiceName)，需要重新选择。" }
        }
        else {
            $matches = @(Get-CimInstance Win32_Process | Where-Object {
                $_.Name -eq $entry.Name -and $_.ExecutablePath -ieq $entry.Path -and
                (Get-WizardSignature -Text $_.CommandLine) -eq $entry.CommandLineSignature
            })
            if ($matches.Count -ne 1) { throw "已保存进程无法唯一定位：$($entry.Name)，请重新选择当前实例。" }
            $target = Get-WizardTarget -ProcessNumber $matches[0].ProcessId -Source '本轮路径和启动参数签名（重新查询 PID）' -KeepPid
        }
        $restored += $target
    }
    if ($restored.Count -eq 0) { throw '旧设置没有保存目标，需要首次补选。' }
    return $restored
}

<#
.SYNOPSIS
读取当前用途的默认参数；不保存或恢复主要转储对象的旧 PID。
.PARAMETER Name
白名单中的采集参数名。
.PARAMETER Fallback
没有保存值时的当前推荐值。
.OUTPUTS
当前用途保存值或推荐值。
#>
function Get-WizardActionDefault {
    param([string]$Name, $Fallback)
    if ($reuseSavedRun -and $savedSettings.DefaultsByAction) {
        $defaults = $savedSettings.DefaultsByAction.$action
        if ($defaults -and $defaults.PSObject.Properties[$Name]) { return $defaults.$Name }
    }
    return $Fallback
}

<#
.SYNOPSIS
读取可被其他窗口原子替换的设置快照，允许文件共享删除，短暂占用时重试。
.PARAMETER Path
本机本轮设置文件的完整路径。
.OUTPUTS
完整解析的设置对象；持续损坏或不可访问时抛出原始错误。
#>
function Read-WizardSession {
    param([string]$Path)
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $stream = $null; $reader = $null
        try {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            return $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
        }
        catch { if ($attempt -eq 9) { throw }; Start-Sleep -Milliseconds 100 }
        finally { if ($reader) { $reader.Dispose() } elseif ($stream) { $stream.Dispose() } }
    }
}

<#
.SYNOPSIS
原子保存本轮设置并合并其他窗口的用途参数，旧轮参数与工具安装路径不带入新轮。
.PARAMETER Targets
本轮当前目标，持久化只保存内部名称或进程定位签名。
.PARAMETER Arguments
当前用途的采集参数，按白名单保存。
#>
function Save-WizardSession {
    param([object[]]$Targets, [hashtable]$Arguments)
    $fullPath = [IO.Path]::GetFullPath($SessionPath)
    $mutex = [Threading.Mutex]::new($false, ('Global\GatherV4WizardSession-' + (Get-WizardSignature -Text $fullPath.ToUpperInvariant())))
    $ownsMutex = $false; $temporary = $null
    try {
        try { $ownsMutex = $mutex.WaitOne(5000) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw '其他窗口正在保存本轮设置，请稍后重试。' }
        $defaultsByAction = @{}
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            try {
                $latest = Read-WizardSession -Path $fullPath
                if ($latest.RunId -eq $runId -and $latest.Scenario -eq $scenario -and $latest.VersionTag -eq $version) {
                    foreach ($property in $latest.DefaultsByAction.PSObject.Properties) { $defaultsByAction[$property.Name] = $property.Value }
                }
            }
            catch { Write-Host '旧设置不可解析，重新保存本轮设置。' -ForegroundColor Yellow }
        }
        $currentDefaults = @{}
        foreach ($name in @('Stage', 'Mode', 'DurationSeconds', 'IntervalSeconds', 'CircularMB', 'DumpTrigger',
            'CpuThresholdPercent', 'MemoryThresholdMB', 'ThresholdSeconds', 'DumpLimit', 'IncidentDumpIntervalSeconds', 'ShowMonitorWindows')) {
            if ($Arguments.ContainsKey($name)) { $currentDefaults[$name] = $Arguments[$name] }
        }
        if ($action -eq 'Incident') {
            $primary = $Targets | Where-Object {
                $(if ($_.ServiceName) { "service:$($_.ServiceName)" } else { "pid:$($_.ProcessId)" }) -eq $Arguments.IncidentTargetSelector
            }
            $currentDefaults.PrimaryTargetKey = Get-WizardTargetKey -Target $primary
            if (-not $defaultsByAction.ContainsKey('Sample')) {
                $defaultsByAction.Sample = @{ Stage = 'whole-run'; DurationSeconds = $Arguments.DurationSeconds; IntervalSeconds = $Arguments.IntervalSeconds }
            }
            if (-not $defaultsByAction.ContainsKey('Trace')) {
                $defaultsByAction.Trace = @{ Stage = 'manual'; Mode = 'Thread'; DurationSeconds = 300; CircularMB = $Arguments.CircularMB }
            }
        }
        $defaultsByAction[$action] = $currentDefaults
        $settings = @{
            SchemaVersion = 2; Scenario = $scenario; VersionTag = $version; RunId = $runId
            OutputRoot = $Arguments.OutputRoot; UpdatedAtUtc = [DateTime]::UtcNow.ToString('o')
            DefaultsByAction = $defaultsByAction
            Targets = @($Targets | ForEach-Object {
                if ($_.ServiceName) { @{ ServiceName = $_.ServiceName; Path = $_.Path } }
                else { @{ Name = $_.Name; Path = $_.Path; CommandLineSignature = (Get-WizardSignature -Text $_.CommandLine) } }
            })
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
        $temporary = "$fullPath.$([guid]::NewGuid().ToString('N')).tmp"
        [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 9), [Text.UTF8Encoding]::new($true))
        if ([IO.File]::Exists($fullPath)) { [IO.File]::Replace($temporary, $fullPath, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $fullPath) }
        Write-Host "本轮设置已保存：$fullPath（跨窗口沿用，PID 每次重新查询）"
    }
    finally {
        if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ($ownsMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

<#
.SYNOPSIS
查询当前进程身份，并优先映射到明确的 Windows 服务，以便资源采样跟随重启。
.PARAMETER ProcessNumber
本机当前 PID；拒绝 PID 4 和本次不包含的 TcpCenter。
.PARAMETER Source
端口、服务列表或人工 PID 的来源说明。
.PARAMETER ExactServiceName
人工选择的精确服务名；为空时查询该进程承载的服务。
.PARAMETER KeepPid
直接输入 PID 时保留 PID 选择器，避免替操作人员猜测服务身份。
.OUTPUTS
包含名称、路径、创建时间、来源以及底层选择器的目标对象。
#>
function Get-WizardTarget {
    param([int]$ProcessNumber, [string]$Source, [string]$ExactServiceName = '', [switch]$KeepPid)
    if ($ProcessNumber -le 0 -or $ProcessNumber -eq 4) { throw '该 PID 不能定位业务实例；HTTP.sys/IIS 请查应用池或实际后端 PID。' }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessNumber"
    if (-not $process) { throw '进程已经退出，请重新选择。' }
    if ($process.Name -eq 'Gather.TcpCenter.WinService.exe') { throw '本次压测不包含 TcpCenter，请选择 GatherV4 实例。' }
    if (-not $process.ExecutablePath) { throw '无法读取部署路径，请使用管理员 PowerShell 重新确认实例。' }
    $serviceName = $ExactServiceName
    if (-not $serviceName -and -not $KeepPid) {
        $services = @(Get-CimInstance Win32_Service | Where-Object ProcessId -eq $ProcessNumber)
        if ($services.Count -eq 1) { $serviceName = $services[0].Name }
        elseif ($services.Count -gt 1) {
            $choice = Read-WizardChoice -Prompt '该进程承载多个服务，选择跟随重启的内部服务名' `
                -Options @($services | ForEach-Object { "$($_.Name)（$($_.DisplayName)）" })
            $serviceName = $services[$choice - 1].Name
        }
    }
    return [pscustomobject]@{
        ServiceName = $serviceName; ProcessId = $process.ProcessId; CreatedAt = $process.CreationDate; State = 'Running'
        Name = $process.Name; Path = $process.ExecutablePath; CommandLine = $process.CommandLine; Source = $Source
    }
}

<#
.SYNOPSIS
按编号选择一个或多个候选；多候选时必须明确输入，不按 CPU 或同名进程默认选择。
.PARAMETER Options
已解析身份的候选目标。
.OUTPUTS
去重后的候选目标数组。
#>
function Select-WizardTargets {
    param([object[]]$Options)
    if ($Options.Count -eq 0) { return @() }
    for ($index = 0; $index -lt $Options.Count; $index++) {
        $target = $Options[$index]
        Write-Host "`n[$($index + 1)] $($target.Name)  PID=$($target.ProcessId)  服务=$($target.ServiceName)"
        Write-Host "    路径：$($target.Path)"
        if ($target.Name -in @('w3wp.exe', 'dotnet.exe')) { Write-Host "    启动参数（核对应用池或 API）：$($target.CommandLine)" }
        Write-Host "    来源：$($target.Source)"
    }
    while ($true) {
        $defaultText = $(if ($Options.Count -eq 1) { '1' } else { '' })
        $answer = Read-WizardAnswer -Prompt '选择实例编号，可用逗号分隔，例如 1,3' -Default $defaultText
        $indexes = @()
        $valid = $true
        foreach ($part in $answer.Split(',')) {
            $number = 0
            if (-not [int]::TryParse($part.Trim(), [ref]$number) -or $number -lt 1 -or $number -gt $Options.Count) {
                $valid = $false; break
            }
            $indexes += $number - 1
        }
        if ($valid) { return @($indexes | Select-Object -Unique | ForEach-Object { $Options[$_] }) }
        Write-Host '实例编号无效，请重新选择。' -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
交互选择本机目标，支持监听端口、Gather 服务列表、内部名称和明确 PID。
.OUTPUTS
当前进程生命周期内去重的目标数组；目标无法解析时可重新选择。
#>
function Read-WizardTargets {
    $selected = @()
    while ($true) {
        $method = Read-WizardChoice -Prompt '如何确认要监控的实例？' -Default 1 -Options @(
            '输入监听端口（ConnectService/API 常用）',
            '从 Gather 服务列表选择（Directive/TimerTask 常用）',
            '输入 Windows 服务内部名称',
            '输入已确认的 PID（IIS/API 非服务进程）'
        )
        $candidates = @()
        try {
            switch ($method) {
                1 {
                    $port = Read-WizardNumber -Prompt '本机监听端口是多少？' -Default 8890 -Minimum 1 -Maximum 65535
                    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop)
                    foreach ($number in @($listeners.OwningProcess | Select-Object -Unique)) {
                        $candidates += Get-WizardTarget -ProcessNumber $number -Source "本机监听端口 $port"
                    }
                    if ($candidates.Count -eq 0) { throw '未找到本机监听进程；检查服务是否启动，或改用服务列表。' }
                }
                2 {
                    $services = @(Get-CimInstance Win32_Service | Where-Object {
                        ($_.State -eq 'Running' -or $action -in @('Sample', 'Incident')) -and
                        $_.PathName -match 'ConnectService|DirectiveService|TimerTaskService|IssueWebApi'
                    } | Sort-Object Name)
                    foreach ($service in $services) {
                        $candidates += Get-WizardServiceTarget -Service $service -Source 'Gather 服务列表'
                    }
                    if ($candidates.Count -eq 0) { throw '未找到已运行的候选服务；可改用精确内部名称或 PID。' }
                }
                3 {
                    $name = Read-WizardAnswer -Prompt 'Windows 服务内部 Name 是什么？'
                    $service = Get-CimInstance Win32_Service | Where-Object Name -eq $name
                    if (-not $service) { throw '该内部名称不存在。' }
                    $candidates += Get-WizardServiceTarget -Service $service -Source '人工内部名称'
                }
                4 {
                    $number = Read-WizardNumber -Prompt '已核实的业务进程 PID 是多少？' -Default 0 -Minimum 1 -Maximum 2147483647
                    $candidates += Get-WizardTarget -ProcessNumber $number -Source '人工 PID（不跟随重启）' -KeepPid
                }
            }
        }
        catch {
            if ($_.Exception -is [OperationCanceledException]) { throw }
            Write-Host "定位失败：$($_.Exception.Message)" -ForegroundColor Yellow
            continue
        }
        foreach ($target in @(Select-WizardTargets -Options $candidates)) {
            $duplicate = @($selected | Where-Object {
                ($target.ProcessId -gt 0 -and $_.ProcessId -eq $target.ProcessId -and $_.CreatedAt -eq $target.CreatedAt) -or
                ($target.ServiceName -and $_.ServiceName -eq $target.ServiceName)
            })
            if ($duplicate.Count -gt 0) { Write-Host "PID $($target.ProcessId) 已选择，避免重复计算资源。" }
            else { $selected += $target }
        }
        if ($selected.Count -gt 0) {
            $more = Read-WizardChoice -Prompt '是否再添加其他本机服务？' -Default 1 -Options @('选好了，继续', '继续添加')
            if ($more -eq 1) { return $selected }
        }
    }
}

<#
.SYNOPSIS
执行前复查 PID、服务映射和创建时间，避免回答期间进程重启后使用过期摘要。
.PARAMETER Targets
操作人员刚确认的目标身份。
#>
function Assert-WizardTargets {
    param([object[]]$Targets)
    foreach ($target in $Targets) {
        if ($target.ProcessId -eq 0 -and $target.ServiceName -and $action -in @('Sample', 'Incident')) {
            $service = Get-CimInstance Win32_Service | Where-Object Name -eq $target.ServiceName
            if (-not $service -or $service.State -ne $target.State) { throw '选择后背景服务状态已变化，请重新运行向导核对当前身份。' }
            continue
        }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($target.ProcessId)"
        if (-not $process -or $process.CreationDate -ne $target.CreatedAt) { throw '选择后进程身份已变化，请重新运行向导确认。' }
        if ($target.ServiceName) {
            $service = Get-CimInstance Win32_Service | Where-Object Name -eq $target.ServiceName
            if (-not $service -or $service.State -ne 'Running' -or $service.ProcessId -ne $target.ProcessId) {
                throw '选择后服务已停止或更换 PID，请重新确认。'
            }
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $CollectorPath -PathType Leaf)) { throw '请把本脚本与 Invoke-GatherV4Diagnostics.ps1 放在同一目录，或指定 CollectorPath。' }
    $savedSettings = $null
    if (Test-Path -LiteralPath $SessionPath -PathType Leaf) {
        try { $savedSettings = Read-WizardSession -Path $SessionPath }
        catch { Write-Host '上次设置无法读取，本次使用初始默认值。' -ForegroundColor Yellow }
    }
    Write-Host 'GatherV4 采集向导：回车接受默认值，任何问题输入 q 可退出。' -ForegroundColor Cyan
    Write-Host '本窗口负责采集；LoadTester 在负载窗口单独运行。'
    if ($savedSettings) { Write-Host "已读取上次设置：$SessionPath（不会恢复旧 PID）" }
    $purpose = Read-WizardChoice -Prompt '这个窗口现在要做什么？' -Default 1 -Options @(
        '全程记录 CPU/内存/线程/句柄（资源窗口 A）',
        '采一段 PerfView（诊断窗口 B）',
        '抓一份完整 Dump（异常现场）',
        '连续抓三份 Dump（业务持续卡住）',
        '自动监控并抓 Dump（压测前启动，异常或退出时推荐）',
        '只查看本机候选进程和服务',
        '退出',
        '一键自动启动监控（推荐：打开资源/PerfView/Dump 状态窗口，主窗口按 D 取证）'
    )
    if ($purpose -eq 7) { return }
    if ($purpose -eq 6) { & $CollectorPath -Action Inventory; return }
    $action = $(if ($purpose -eq 8) { 'Incident' } else { @('Sample', 'Trace', 'Dump', 'DumpSeries', 'DumpWatch')[$purpose - 1] })
    $reuseSavedRun = $false; $quickReuse = $false
    if ($savedSettings -and $savedSettings.RunId) {
        Write-Host "本机已维护的一轮：$($savedSettings.Scenario) / $($savedSettings.VersionTag) / $($savedSettings.RunId)"
        Write-Host "输出根目录：$($savedSettings.OutputRoot)；已保存 $(@($savedSettings.Targets | Where-Object { $_ }).Count) 个目标定位方式。"
        $reuseChoice = Read-WizardChoice -Prompt '这个新窗口是否继续当前这一轮？' -Default 1 -Options @(
            '沿用本轮数据和本用途参数，重新查询当前 PID', '同一轮，重新选择目标或修改本用途参数', '开始新一轮，重新维护场景和 RunId'
        )
        $reuseSavedRun = $reuseChoice -ne 3; $quickReuse = $reuseChoice -eq 1
    }
    $scenarios = @('gatherv4-connections-10000', 'gatherv4-connections-50000',
        'gatherv4-http-reading-10000', 'gatherv4-reconnect-storm-10000', 'gatherv4-reconnect-storm-50000')
    if ($reuseSavedRun) {
        $scenario = [string]$savedSettings.Scenario; $version = [string]$savedSettings.VersionTag; $runId = [string]$savedSettings.RunId
        if ($scenario -notin $scenarios -or $version -notin @('old', 'optimized') -or $runId -notmatch '^[A-Za-z0-9_.-]+$') { throw '已保存本轮数据无效，请重新维护新一轮。' }
    }
    else {
        $defaultScenario = 1
        $savedScenario = Get-WizardDefault -Name Scenario -Fallback $scenarios[0]
        $scenarioIndex = [Array]::IndexOf($scenarios, $savedScenario)
        if ($scenarioIndex -ge 0) { $defaultScenario = $scenarioIndex + 1 }
        $choice = Read-WizardChoice -Prompt '这轮正在压测哪个场景？' -Default $defaultScenario -Options @(
            '一万连接（30 分钟稳态）', '五万连接（1 小时稳态，以实际分片数为准）',
            '一万在线 + HTTP 同步指令', '一万重启恢复', '五万重启恢复（以实际分片数为准）'
        )
        $scenario = $scenarios[$choice - 1]
        $defaultVersion = $(if ((Get-WizardDefault -Name VersionTag -Fallback old) -eq 'optimized') { 2 } else { 1 })
        $versionChoice = Read-WizardChoice -Prompt '服务器当前实际运行哪个版本？' -Default $defaultVersion -Options @('老版本 old', '优化版 optimized')
        $version = @('old', 'optimized')[$versionChoice - 1]
        $defaultRunId = "$scenario-$version-r1"
        if ($savedSettings -and $savedScenario -eq $scenario -and
            (Get-WizardDefault -Name VersionTag -Fallback old) -eq $version) {
            $previousRunId = Get-WizardDefault -Name RunId -Fallback $defaultRunId
            if ($reuseChoice -eq 3 -and $previousRunId -match '^(.*)-r(\d+)$') {
                $defaultRunId = "$($Matches[1])-r$([int]$Matches[2] + 1)"
            }
            elseif ($reuseChoice -ne 3) { $defaultRunId = $previousRunId }
        }
        $runId = Read-WizardTag -Prompt '本轮 RunId 是什么？必须与 LoadTester --run-id 一致；新一轮改 r2/r3' -Default $defaultRunId
    }
    if ($action -in @('Sample', 'Trace', 'Incident') -and $scenario -eq 'gatherv4-connections-50000') {
        Write-Host '本轮请同时核对 Connect、Directive、TimerTask 和相关 IIS/API 实例，避免只看 Connect 而遗漏同机 CPU 竞争；TcpCenter 不在范围内。'
    }
    $targets = @()
    if ($quickReuse) {
        try { $targets = @(Restore-WizardTargets) }
        catch { Write-Host "本轮目标需要补选：$($_.Exception.Message)" -ForegroundColor Yellow; $quickReuse = $false }
    }
    if ($targets.Count -eq 0) { $targets = @(Read-WizardTargets) }
    $arguments = @{ Action = $action; RunId = $runId; Scenario = $scenario; VersionTag = $version }
    $serviceNames = @($targets | Where-Object ServiceName | Select-Object -ExpandProperty ServiceName -Unique)
    $processNumbers = @($targets | Where-Object { -not $_.ServiceName } | Select-Object -ExpandProperty ProcessId -Unique)
    if ($serviceNames.Count -gt 0) { $arguments.ServiceName = $serviceNames }
    if ($processNumbers.Count -gt 0) { $arguments.TargetProcessId = $processNumbers }
    if ($quickReuse) {
        $arguments.Stage = Get-WizardActionDefault -Name Stage -Fallback $(if ($action -eq 'Sample') { 'whole-run' } elseif ($action -in @('DumpWatch', 'Incident')) { 'armed-before-run' } elseif ($action -eq 'DumpSeries') { 'stalled' } else { 'manual' })
        if ($action -in @('Sample', 'DumpWatch', 'Incident')) {
            $arguments.DurationSeconds = [int](Get-WizardActionDefault -Name DurationSeconds -Fallback $(if ($scenario -eq 'gatherv4-connections-10000') { 7200 } else { 14400 }))
        }
        if ($action -in @('Sample', 'Incident', 'DumpSeries')) {
            $arguments.IntervalSeconds = [int](Get-WizardActionDefault -Name IntervalSeconds -Fallback $(if ($action -eq 'DumpSeries') { 10 } else { 2 }))
        }
        if ($action -eq 'Trace') {
            $arguments.Mode = Get-WizardActionDefault -Name Mode -Fallback Thread
            $arguments.DurationSeconds = [int](Get-WizardActionDefault -Name DurationSeconds -Fallback 300)
            $arguments.CircularMB = [int](Get-WizardActionDefault -Name CircularMB -Fallback 2048)
        }
        if ($action -eq 'DumpWatch') {
            $arguments.DumpTrigger = Get-WizardActionDefault -Name DumpTrigger -Fallback ExceptionOrExit
            foreach ($entry in @(@('DumpLimit', 1), @('CpuThresholdPercent', 80), @('MemoryThresholdMB', 512), @('ThresholdSeconds', 10))) {
                $arguments[$entry[0]] = [int](Get-WizardActionDefault -Name $entry[0] -Fallback $entry[1])
            }
        }
        if ($action -eq 'Incident') {
            $arguments.CircularMB = [int](Get-WizardActionDefault -Name CircularMB -Fallback 2048)
            $arguments.IncidentDumpIntervalSeconds = [int](Get-WizardActionDefault -Name IncidentDumpIntervalSeconds -Fallback 10)
        }
        Write-Host '已沿用本轮元数据、目标定位及本用途参数；下面摘要会显示实际当前 PID。'
    }
    elseif ($action -eq 'Incident') {
        $arguments.DurationSeconds = Read-WizardNumber -Prompt '一键监控最多持续多少秒？全部就绪后再开压测' -Default 14400 -Minimum 1 -Maximum 86400
        $arguments.IntervalSeconds = Read-WizardNumber -Prompt '资源记录间隔多少秒？' -Default 2 -Minimum 1 -Maximum 60
        $arguments.IncidentDumpIntervalSeconds = Read-WizardNumber -Prompt '按 D 后每份 Dump 写完间隔多少秒？共三份' -Default 10 -Minimum 1 -Maximum 60
        $arguments.CircularMB = Read-WizardNumber -Prompt '整机 Thread 循环跟踪保留大小 MB（只保证尾部窗口）' -Default 2048 -Minimum 64 -Maximum 32768
        $arguments.Stage = 'armed-before-run'
    }
    elseif ($action -eq 'Sample') {
        $defaultSeconds = $(if ($scenario -eq 'gatherv4-connections-10000') { 7200 } else { 14400 })
        $arguments.DurationSeconds = Read-WizardNumber -Prompt '资源记录最多持续多少秒？结束可按 Ctrl+C，不改变压测时长' -Default $defaultSeconds -Minimum 1 -Maximum 86400
        $arguments.IntervalSeconds = Read-WizardNumber -Prompt '每隔多少秒记录一次？' -Default 2 -Minimum 1 -Maximum 60
        $arguments.Stage = 'whole-run'
    }
    elseif ($action -eq 'DumpWatch') {
        $triggerChoice = Read-WizardChoice -Prompt '满足什么条件时自动抓 Dump？' -Default 1 -Options @(
            '未处理异常或进程退出（不自动识别存活但通讯失效）',
            '仅未处理异常', '仅进程退出（正常退出也会触发）',
            '该进程 CPU 占整机达到阈值', '该进程提交内存达到阈值'
        )
        $arguments.DumpTrigger = @('ExceptionOrExit', 'Exception', 'Exit', 'Cpu', 'Memory')[$triggerChoice - 1]
        if ($arguments.DumpTrigger -eq 'Cpu') {
            $arguments.CpuThresholdPercent = Read-WizardNumber -Prompt '目标进程 CPU 占整机达到多少百分比？不是单核，也不是全机器 CPU 总和' -Default 80 -Minimum 1 -Maximum 100
        }
        if ($arguments.DumpTrigger -eq 'Memory') {
            $arguments.MemoryThresholdMB = Read-WizardNumber -Prompt '进程提交内存达到多少 MB？不是工作集或托管堆' -Default 512 -Minimum 1 -Maximum 1048576
        }
        if ($arguments.DumpTrigger -in @('Cpu', 'Memory')) {
            $arguments.ThresholdSeconds = Read-WizardNumber -Prompt '阈值连续满足多少秒后触发？' -Default 10 -Minimum 1 -Maximum 3600
        }
        $arguments.DumpLimit = Read-WizardNumber -Prompt '每个进程最多抓几份？达到数量后结束该进程监听' -Default 1 -Minimum 1 -Maximum 3
        $defaultSeconds = $(if ($scenario -eq 'gatherv4-connections-10000') { 7200 } else { 14400 })
        $arguments.DurationSeconds = Read-WizardNumber -Prompt '监听最多持续多少秒？全部监听就绪后再启动压测；结束按 Ctrl+C 取消' -Default $defaultSeconds -Minimum 1 -Maximum 86400
        $arguments.Stage = 'armed-before-run'
        Write-Host '多个 PID 同时监听；每个 PID 只跟随当前生命周期，重启后需重新运行向导。'
        if ($scenario -match 'reconnect' -and $arguments.DumpTrigger -in @('ExceptionOrExit', 'Exit')) {
            Write-Host '本场景的计划重启也会触发退出 Dump，请按重启时机标注，不能直接判为崩溃。' -ForegroundColor Yellow
        }
    }
    else {
        Write-Host 'Stage 只是文件标签，不会等待阶段：请按负载窗口日志选择执行时机。'
        if ($action -ne 'Trace') {
            $stageLabels = @('异常当前快照', '业务持续卡住', '稳态早期', '稳态末尾', '冷却清理后', '手工填写其他标签')
            $stageTags = @('snapshot', 'stalled', 'steady-early', 'steady-late', 'after-cooldown', '')
            $defaultStageChoice = $(if ($action -eq 'DumpSeries') { 2 } else { 1 })
        }
        elseif ($scenario -match 'reconnect') {
            $stageLabels = @('重启前稳定期间', '跨重启及恢复窗口', '恢复后稳定期间', '手工填写其他标签')
            $stageTags = @('steady-before-restart', 'restart-and-recovery', 'recovered-steady', '')
            $defaultStageChoice = 2
        }
        elseif ($scenario -match 'http') {
            $stageLabels = @('20 条功能请求', '1000 台各一条同步请求', '1000 台各两条，同设备连续命令', '手工填写其他标签')
            $stageTags = @('functional-sync-20', 'steady-sync-1000', 'same-device-sync-1000x2', '')
            $defaultStageChoice = 2
        }
        elseif ($scenario -match '50000') {
            $stageLabels = @('爬升到 25000 台', '爬升到 50000 台', '五万稳态早期', '五万稳态末尾', '手工填写其他标签')
            $stageTags = @('ramp-25000', 'ramp-50000', 'steady-early', 'steady-late', '')
            $defaultStageChoice = 3
        }
        else {
            $stageLabels = @('爬升到 10000 台', '一万稳态早期', '一万稳态末尾', '手工填写其他标签')
            $stageTags = @('ramp-10000', 'steady-early', 'steady-late', '')
            $defaultStageChoice = 2
        }
        $stageChoice = Read-WizardChoice -Prompt '这次采集对应哪个阶段？' -Options $stageLabels -Default $defaultStageChoice
        $arguments.Stage = $stageTags[$stageChoice - 1]
        if (-not $arguments.Stage) { $arguments.Stage = Read-WizardTag -Prompt '其他阶段标签（英文或数字）？' -Default manual }
        if ($action -eq 'DumpSeries') {
            $arguments.IntervalSeconds = Read-WizardNumber -Prompt '每份 Dump 写完后间隔多少秒？共抓三份' -Default 10 -Minimum 1 -Maximum 60
        }
    }
    if ($action -eq 'Trace' -and -not $quickReuse) {
        $modeChoice = Read-WizardChoice -Prompt 'PerfView 采集哪种模式？' -Default 1 -Options @(
            'CPU：先找消耗 CPU 的调用链', 'Thread：CPU 不高但业务慢/等待',
            'GC：检查 GC 压力', 'Alloc：已发现分配压力后短时间采样'
        )
        $arguments.Mode = @('CPU', 'Thread', 'GC', 'Alloc')[$modeChoice - 1]
        $defaultSeconds = $(if ($scenario -match 'reconnect' -and $arguments.Stage -eq 'restart-and-recovery') {
            if ($scenario -match '50000') { 600 } else { 300 }
        } elseif ($arguments.Mode -in @('Thread', 'Alloc')) { 30 } elseif ($arguments.Mode -eq 'GC') { 300 } else { 60 })
        $arguments.DurationSeconds = Read-WizardNumber -Prompt 'PerfView 采集多少秒？结束还需合并压缩' -Default $defaultSeconds -Minimum 1 -Maximum 86400
        # 重启会换 PID；同机多个目标也只能使用一套整机跟踪，再按 PID 分别分析。
        if ($targets.Count -gt 1 -or $scenario -match 'reconnect') {
            $arguments.MachineWide = $true
            Write-Host '本次使用整机跟踪，覆盖多个服务或重启后的新 PID。'
        }
        $defaultCircular = $(if ($arguments.ContainsKey('MachineWide')) { 2048 } else { 1024 })
        $arguments.CircularMB = Read-WizardNumber -Prompt '循环跟踪保留大小 MB（事件多时可能只保留尾段）' -Default $defaultCircular -Minimum 64 -Maximum 32768
    }
    if ($action -eq 'Trace' -and ($targets.Count -gt 1 -or $scenario -match 'reconnect')) { $arguments.MachineWide = $true }
    if ($action -eq 'Incident') {
        $runningTargets = @($targets | Where-Object { $_.ProcessId -gt 0 })
        if ($runningTargets.Count -eq 0) { throw '组合诊断至少需要一个正在运行的主要对象。' }
        $primaryKey = Get-WizardActionDefault -Name PrimaryTargetKey -Fallback ''
        $primary = @($runningTargets | Where-Object { (Get-WizardTargetKey -Target $_) -eq $primaryKey })
        if (-not $quickReuse -or $primary.Count -ne 1) {
            $primaryChoice = Read-WizardChoice -Prompt '异常时主要给哪个进程抓三份 Dump？其他对象仅记录资源' -Default 1 -Options @(
                $runningTargets | ForEach-Object { "$($_.Name)，服务=$($_.ServiceName)，当前 PID=$($_.ProcessId)，$($_.Path)" }
            )
            $primary = @($runningTargets[$primaryChoice - 1])
        }
        $arguments.IncidentTargetSelector = $(if ($primary[0].ServiceName) { "service:$($primary[0].ServiceName)" } else { "pid:$($primary[0].ProcessId)" })
        $arguments.Mode = 'Thread'; $arguments.MachineWide = $true
        $arguments.ShowMonitorWindows = -not [bool]$NoMonitorWindows
        Write-Host '一键模式自动并行资源、整机 PerfView 和退出监听。全部就绪后开跑；主窗口掉线按 D，正常结束按 Q。' -ForegroundColor Cyan
        if ($arguments.ShowMonitorWindows) { Write-Host '同时自动打开三个只读状态窗口；无需手动开窗口或再次维护参数，关闭子窗口不停止采集。' }
    }
    if ($action -in @('Trace', 'Incident')) {
        $arguments.PerfViewPath = Get-WizardToolPath -ToolName 'PerfView.exe' -ConfiguredPath $configuredToolPaths.PerfViewPath
    }
    if ($action -in @('Dump', 'DumpSeries', 'DumpWatch', 'Incident')) {
        Write-Host '完整 Dump 会暂停目标并写磁盘，请将本时段作为诊断证据。'
        $arguments.ProcDumpPath = Get-WizardToolPath -ToolName 'ProcDump' `
            -ConfiguredPath $configuredToolPaths.ProcDumpPath
    }
    if ($quickReuse) { $arguments.OutputRoot = Get-WizardDefault -Name OutputRoot -Fallback 'D:\GatherEvidence' }
    else {
        $arguments.OutputRoot = Read-WizardPath -Prompt '采集文件保存到哪个根目录？' `
            -Default (Get-WizardDefault -Name OutputRoot -Fallback 'D:\GatherEvidence')
    }
    $outputDirectory = Join-Path $arguments.OutputRoot "$version\$scenario\$runId\$env:COMPUTERNAME"
    Write-Host "`n即将执行的操作" -ForegroundColor Cyan
    Write-Host "机器：$env:COMPUTERNAME    动作：$action"
    if ($action -in @('Trace', 'Incident')) { Write-Host "模式：$($arguments.Mode)    整机跟踪：$($arguments.ContainsKey('MachineWide'))    循环缓冲：$($arguments.CircularMB) MB" }
    if ($action -eq 'Incident') {
        Write-Host "按 D 转储主要对象：$($arguments.IncidentTargetSelector)；三份，间隔 $($arguments.IncidentDumpIntervalSeconds) 秒。"
        Write-Host "自动打开三个状态窗口：$($arguments.ShowMonitorWindows)；采集控制统一在本窗口。"
    }
    if ($arguments.PerfViewPath) { Write-Host "PerfView 工具：$($arguments.PerfViewPath)" }
    if ($arguments.ProcDumpPath) { Write-Host "Dump 工具：$($arguments.ProcDumpPath)" }
    if ($action -eq 'DumpWatch') {
        Write-Host "触发：$($arguments.DumpTrigger)    每个 PID 最多：$($arguments.DumpLimit) 份"
        if ($arguments.DumpTrigger -eq 'Cpu') { Write-Host "CPU 阈值：进程占整机 $($arguments.CpuThresholdPercent)%" }
        if ($arguments.DumpTrigger -eq 'Memory') { Write-Host "提交内存阈值：$($arguments.MemoryThresholdMB) MB" }
        if ($arguments.ThresholdSeconds) { Write-Host "连续满足：$($arguments.ThresholdSeconds) 秒" }
    }
    Write-Host "场景：$scenario    版本：$version    RunId：$runId"
    Write-Host "阶段标记：$($arguments.Stage)"
    if ($arguments.ContainsKey('DurationSeconds')) { Write-Host "时长上限：$($arguments.DurationSeconds) 秒" }
    if ($arguments.ContainsKey('IntervalSeconds')) { Write-Host "间隔：$($arguments.IntervalSeconds) 秒" }
    Write-Host "目录：$outputDirectory"
    foreach ($target in $targets) {
        $selector = $(if ($target.ServiceName) {
            if ($action -in @('Dump', 'DumpSeries', 'DumpWatch')) { "服务=$($target.ServiceName)（绑定当前 PID，重启后重新选择）" }
            else { "服务=$($target.ServiceName)（资源记录可跟随服务重启）" }
        } else { '固定 PID（退出后需重新选择）' })
        Write-Host "目标：$($target.Name)  PID=$($target.ProcessId)  $selector"
        Write-Host "      $($target.Path)    来源：$($target.Source)"
        if ($target.Name -in @('w3wp.exe', 'dotnet.exe')) { Write-Host "      启动参数：$($target.CommandLine)" }
    }
    if ($DryRun) { Write-Host '已指定 DryRun：无论如何选择，均不会启动实际采集。' -ForegroundColor Yellow }
    $execute = Read-WizardChoice -Prompt '确认目标、场景和 RunId 后怎么处理？' -Default 1 -Options @(
        $(if ($DryRun) { '输出命令预览' } else { '开始执行' }), '仅预览命令，不采集', '取消'
    )
    if ($execute -eq 3) { Write-Host '已取消。'; return }
    Assert-WizardTargets -Targets $targets
    if (-not $NoSave) {
        try {
            Save-WizardSession -Targets $targets -Arguments $arguments
        }
        catch { Write-Host "无法保存默认设置，可继续采集：$($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if ($execute -eq 2 -or $DryRun) { $arguments.DryRun = $true }
    & $CollectorPath @arguments
    Write-Host $(if ($arguments.DryRun) { '预览完成，未启动采集。' } else { "本次操作完成，查看目录：$outputDirectory" }) -ForegroundColor Green
    Write-Host '其他窗口或下一次操作，重新运行本向导即可；同轮保持 RunId 相同。'
}
catch {
    if ($_.Exception -is [OperationCanceledException]) { Write-Host $_.Exception.Message; return }
    Write-Error "采集向导未完成：$($_.Exception.Message)"
}
