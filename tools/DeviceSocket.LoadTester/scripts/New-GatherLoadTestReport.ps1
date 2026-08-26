<#
.SYNOPSIS
从既有 LoadTester 结果和可选 Gather.TcpCenter 指标生成双受众详细报告。

.DESCRIPTION
脚本只读取压测产物，不会重新发起 TCP 或 HTTP 请求。多分片报告应把包含全部 shard-xxx 的整轮结果根目录传给 ResultPath，工具会自动校验并合并节点数据。开发目录下通过 dotnet run 调用；发布目录下优先调用同级 DeviceSocket.LoadTester.dll。

.EXAMPLE
.\scripts\New-GatherLoadTestReport.ps1 -ResultPath .\load-test-results\run-001 -ServerMetricsPath .\load-test-results\run-001\20260821.jsonl -BackgroundPath .\docs\report-background.example.json -Audience Both -ServerTimeZone "China Standard Time"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [string]$ServerMetricsPath,

    [string]$BackgroundPath,

    [ValidateSet("Acceptance", "Diagnostic", "Both")]
    [string]$Audience = "Both",

    [string]$OutputPath,

    [string]$BaselineResultPath,

    [string]$BaselineServerMetricsPath,

    [string]$Title,

    [string]$Environment,

    [string]$ServiceVersion,

    [string]$Tester,

    [string]$ServerTimeZone = "Local",

    [ValidateRange(1, 86400)]
    [int]$BusinessCatchupSeconds = 60,

    [ValidateRange(1, 86400)]
    [int]$QueueAgeSeconds = 30,

    [ValidateRange(1, 600000)]
    [int]$AckMilliseconds = 5000,

    [ValidateRange(1, 1000000)]
    [int]$TargetDevices
)

$loadTesterDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $loadTesterDirectory "DeviceSocket.LoadTester.csproj"
$publishedDllPath = Join-Path $loadTesterDirectory "DeviceSocket.LoadTester.dll"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $ResultPath "analysis"
}

$reportArguments = @(
    "report", "generate",
    "--input", $ResultPath,
    "--output", $OutputPath,
    "--audience", $Audience,
    "--server-time-zone", $ServerTimeZone,
    "--business-catchup-seconds", $BusinessCatchupSeconds,
    "--queue-age-seconds", $QueueAgeSeconds,
    "--ack-milliseconds", $AckMilliseconds
)

if (![string]::IsNullOrWhiteSpace($ServerMetricsPath)) {
    $reportArguments += @("--server-metrics", $ServerMetricsPath)
}
if (![string]::IsNullOrWhiteSpace($BackgroundPath)) {
    $reportArguments += @("--background", $BackgroundPath)
}
if (![string]::IsNullOrWhiteSpace($BaselineResultPath)) {
    $reportArguments += @("--baseline-input", $BaselineResultPath)
}
if (![string]::IsNullOrWhiteSpace($BaselineServerMetricsPath)) {
    $reportArguments += @("--baseline-server-metrics", $BaselineServerMetricsPath)
}
if (![string]::IsNullOrWhiteSpace($Title)) {
    $reportArguments += @("--title", $Title)
}
if (![string]::IsNullOrWhiteSpace($Environment)) {
    $reportArguments += @("--environment", $Environment)
}
if (![string]::IsNullOrWhiteSpace($ServiceVersion)) {
    $reportArguments += @("--service-version", $ServiceVersion)
}
if (![string]::IsNullOrWhiteSpace($Tester)) {
    $reportArguments += @("--tester", $Tester)
}
if ($PSBoundParameters.ContainsKey("TargetDevices")) {
    $reportArguments += @("--target-devices", $TargetDevices)
}

if (Test-Path -LiteralPath $publishedDllPath) {
    & dotnet $publishedDllPath @reportArguments
}
else {
    & dotnet run --project $projectPath -- @reportArguments
}

exit $LASTEXITCODE
