<#
.SYNOPSIS
    在 CI 或纯净构建环境中自动获取 QEMU 与 Android platform-tools (adb) 依赖。
.DESCRIPTION
    读取 bundle-manifest.json 声明的官方下载地址与校验哈希，
    带重试下载、哈希比对校验与静默解压，输出至指定目录，支持与 actions/cache 联动。
#>

[CmdletBinding()]
param (
    [Parameter()]
    [string]$TargetDirectory = '',

    [Parameter()]
    [int]$MaxRetries = 3
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $TargetDirectory) {
    $TargetDirectory = Join-Path $ScriptDir "downloads"
}

$ManifestPath = Join-Path $ScriptDir "bundle-manifest.json"
if (-not (Test-Path $ManifestPath)) {
    throw "未找到依赖清单文件: $ManifestPath"
}

$Manifest = Get-Content -Raw $ManifestPath | ConvertFrom-Json

New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
$QemuTargetDir = Join-Path $TargetDirectory "qemu"
$AdbTargetDir = Join-Path $TargetDirectory "platform-tools"

# 通用带重试的下载函数
function Download-FileWithRetry {
    param (
        [string]$Url,
        [string]$Destination,
        [int]$Retries = 3
    )

    $attempt = 0
    while ($attempt -lt $Retries) {
        $attempt++
        try {
            Write-Host "  正在下载 (尝试 $attempt/$Retries): $Url -> $Destination" -ForegroundColor Cyan
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
            $wc = New-Object System.Net.WebClient
            $wc.Headers.Add("User-Agent", "XBear-Packaging-Agent/1.0")
            $wc.DownloadFile($Url, $Destination)
            if (Test-Path $Destination) {
                return
            }
        } catch {
            Write-Warning "下载失败 (尝试 $attempt/$Retries): $_"
            if ($attempt -lt $Retries) {
                Start-Sleep -Seconds ($attempt * 3)
            } else {
                throw "下载经过 $Retries 次重试后仍失败: $Url"
            }
        }
    }
}

# 1. 处理 QEMU
Write-Host ">>> 准备 QEMU 依赖..." -ForegroundColor Green
if (Test-Path (Join-Path $QemuTargetDir "qemu-system-x86_64.exe")) {
    Write-Host "  QEMU 依赖已存在于 $QemuTargetDir，跳过下载。" -ForegroundColor Cyan
} else {
    $QemuUrl = $Manifest.qemu.ci.downloadUrl
    $QemuExpectedHash = $Manifest.qemu.ci.sha512
    $QemuInstaller = Join-Path $TargetDirectory "qemu-setup.exe"

    if (-not (Test-Path $QemuInstaller)) {
        Download-FileWithRetry -Url $QemuUrl -Destination $QemuInstaller -Retries $MaxRetries
    }

    Write-Host "  校验 QEMU 安装包 SHA-512..." -ForegroundColor Cyan
    $actualHash = (Get-FileHash -Path $QemuInstaller -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actualHash -ne $QemuExpectedHash.ToLowerInvariant()) {
        Remove-Item -Force $QemuInstaller
        throw "QEMU 安装包哈希校验失败！期望: $QemuExpectedHash, 实际: $actualHash"
    }
    Write-Host "  QEMU 安装包哈希校验通过。" -ForegroundColor Green

    # 解压或静默安装 QEMU
    Write-Host "  解压/安装 QEMU 至 $QemuTargetDir ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $QemuTargetDir | Out-Null

    $sevenZip = "C:\Program Files\7-Zip\7z.exe"
    if (Test-Path $sevenZip) {
        & $sevenZip x $QemuInstaller "-o$QemuTargetDir" -y | Out-Null
    } else {
        # 使用 NSIS 静默解压参数 /S /D=
        $process = Start-Process -FilePath $QemuInstaller -ArgumentList "/S /D=$QemuTargetDir" -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "QEMU 静默安装失败，退出码: $($process.ExitCode)"
        }
    }

    if (-not (Test-Path (Join-Path $QemuTargetDir "qemu-system-x86_64.exe"))) {
        throw "QEMU 解压后未找到 qemu-system-x86_64.exe"
    }
    Write-Host "  QEMU 准备就绪。" -ForegroundColor Green
}

# 2. 处理 platform-tools (adb)
Write-Host ">>> 准备 Android platform-tools (adb) 依赖..." -ForegroundColor Green
if (Test-Path (Join-Path $AdbTargetDir "adb.exe")) {
    Write-Host "  platform-tools 依赖已存在于 $AdbTargetDir，跳过下载。" -ForegroundColor Cyan
} else {
    $AdbUrl = $Manifest.platformTools.ci.downloadUrl
    $AdbExpectedHash = $Manifest.platformTools.ci.sha256
    $AdbZip = Join-Path $TargetDirectory "platform-tools.zip"

    if (-not (Test-Path $AdbZip)) {
        Download-FileWithRetry -Url $AdbUrl -Destination $AdbZip -Retries $MaxRetries
    }

    Write-Host "  校验 platform-tools SHA-256..." -ForegroundColor Cyan
    $actualHash = (Get-FileHash -Path $AdbZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $AdbExpectedHash.ToLowerInvariant()) {
        Remove-Item -Force $AdbZip
        throw "platform-tools 哈希校验失败！期望: $AdbExpectedHash, 实际: $actualHash"
    }
    Write-Host "  platform-tools 哈希校验通过。" -ForegroundColor Green

    Write-Host "  解压 platform-tools 至 $TargetDirectory ..." -ForegroundColor Cyan
    Expand-Archive -Path $AdbZip -DestinationPath $TargetDirectory -Force

    $unpackedDir = Join-Path $TargetDirectory "platform-tools"
    if (-not (Test-Path (Join-Path $unpackedDir "adb.exe"))) {
        throw "platform-tools 解压后未找到 adb.exe"
    }
    Write-Host "  platform-tools 准备就绪。" -ForegroundColor Green
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "  所有外部运行时依赖准备完毕！" -ForegroundColor Cyan
Write-Host "  QEMU 目录: $QemuTargetDir" -ForegroundColor Cyan
Write-Host "  adb 目录:  $AdbTargetDir" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
