param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/load-tester"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "DeviceSocket.LoadTester.csproj"
$repositoryPath = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$publishRoot = Join-Path $repositoryPath $OutputRoot

dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o (Join-Path $publishRoot "win-x64")
dotnet publish $project -c $Configuration -r linux-x64 --self-contained false -o (Join-Path $publishRoot "linux-x64")

Write-Host "Published: $publishRoot"
