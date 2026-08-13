<#
.SYNOPSIS
先发布 DeviceSocket.Host，再构建 Linux 容器镜像，并可选导出离线 tar 包。

.DESCRIPTION
脚本在宿主机执行 dotnet publish，把框架依赖发布产物写入 bin/Release/net8.0/publish，
随后以 DeviceSocket.Host 项目目录作为 Docker 上下文。脚本只构建或导出镜像，不启动容器。
文件使用带 BOM 的 UTF-8 编码，以兼容 Windows PowerShell 5.1 对中文脚本的读取规则。

.PARAMETER ImageName
镜像名称，可包含私有仓库前缀；默认 devicesocket-host。

.PARAMETER Tag
镜像标签；默认 latest。

.PARAMETER Platform
目标 Linux 平台；默认 linux/amd64，也支持 linux/arm64。

.PARAMETER ArchivePath
可选离线 tar 输出路径。相对路径按仓库根目录解析；为空时只保留本地镜像。

.PARAMETER Pull
构建前拉取 ASP.NET 8 基础镜像的最新补丁版本。

.PARAMETER NoCache
禁用 Docker 构建缓存。

.EXAMPLE
.\build-docker-image.ps1 -Tag 1.0.0 -Pull

.EXAMPLE
.\build-docker-image.ps1 -Tag 1.0.0 -ArchivePath artifacts\docker\devicesocket-host-1.0.0.tar
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ImageName = "devicesocket-host",

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Tag = "latest",

    [Parameter()]
    [ValidateSet("linux/amd64", "linux/arm64")]
    [string]$Platform = "linux/amd64",

    [Parameter()]
    [string]$ArchivePath,

    [Parameter()]
    [switch]$Pull,

    [Parameter()]
    [switch]$NoCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker)
{
    throw "未找到 docker 命令，请先安装并启动 Docker Engine 或 Docker Desktop。"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet)
{
    throw "未找到 dotnet 命令，请先安装 .NET 8 SDK。"
}

$projectDirectory = $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory "..\..\..")).Path
$projectFile = Join-Path $projectDirectory "DeviceSocket.Host.csproj"
$dockerfile = Join-Path $projectDirectory "Dockerfile"
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $projectDirectory "bin\Release\net8.0\publish"))
$expectedPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $projectDirectory "bin\Release\net8.0")) + [System.IO.Path]::DirectorySeparatorChar

# 清理目标前验证它仍位于固定 Release 输出目录内，避免路径计算异常误删其他文件。
if (-not $publishDirectory.StartsWith($expectedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "发布目录不在预期的 Release 输出路径内：$publishDirectory"
}

if (Test-Path -LiteralPath $publishDirectory)
{
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Write-Host "正在发布 DeviceSocket.Host 到 $publishDirectory。"
& $dotnet.Source publish $projectFile `
    --configuration Release `
    --output $publishDirectory `
    /p:UseAppHost=false
if ($LASTEXITCODE -ne 0)
{
    throw "DeviceSocket.Host 发布失败，退出码：$LASTEXITCODE。"
}

$imageReference = "${ImageName}:${Tag}"
$buildArguments = @(
    "build",
    "--file", $dockerfile,
    "--platform", $Platform,
    "--tag", $imageReference
)

if ($Pull)
{
    $buildArguments += "--pull"
}

if ($NoCache)
{
    $buildArguments += "--no-cache"
}

$buildArguments += $projectDirectory
Write-Host "正在构建镜像 $imageReference，目标平台 $Platform。"
& $docker.Source @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw "Docker 镜像构建失败，退出码：$LASTEXITCODE。"
}

if (-not [string]::IsNullOrWhiteSpace($ArchivePath))
{
    $resolvedArchivePath = if ([System.IO.Path]::IsPathRooted($ArchivePath))
    {
        [System.IO.Path]::GetFullPath($ArchivePath)
    }
    else
    {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArchivePath))
    }

    $archiveDirectory = Split-Path -Parent $resolvedArchivePath
    if (-not [string]::IsNullOrWhiteSpace($archiveDirectory))
    {
        New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    }

    Write-Host "正在导出离线镜像包 $resolvedArchivePath。"
    & $docker.Source image save --output $resolvedArchivePath $imageReference
    if ($LASTEXITCODE -ne 0)
    {
        throw "Docker 镜像导出失败，退出码：$LASTEXITCODE。"
    }
}

Write-Host "镜像构建完成：$imageReference"
