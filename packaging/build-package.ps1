<#
.SYNOPSIS
    小熊模拟器 Windows 端发布打包脚本。
.DESCRIPTION
    执行自包含发布（dotnet publish -r win-x64 --self-contained），
    装配 app、spec 契约、精简捆绑 QEMU 与 platform-tools，
    产出便携绿色 zip 压缩包与 Inno Setup 安装包。
#>

[CmdletBinding()]
param (
    [Parameter()]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [Parameter()]
    [string]$OutputDirectory = '',

    [Parameter()]
    [string]$StagingDirectory = '',

    [Parameter()]
    [string]$QemuPath = '',

    [Parameter()]
    [string]$AdbPath = '',

    [Parameter()]
    [switch]$SkipInstaller = $false,

    [Parameter()]
    [switch]$SkipDownload = $false
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\.."

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $ScriptDir "dist"
}
if (-not $StagingDirectory) {
    $StagingDirectory = Join-Path $ScriptDir "staging"
}

# 1. 读取版本契约
$VersionJsonPath = Join-Path $RepoRoot "spec\spec\version.json"
if (-not (Test-Path $VersionJsonPath)) {
    $VersionJsonPath = Join-Path $RepoRoot "spec\version.json"
}
if (-not (Test-Path $VersionJsonPath)) {
    throw "未找到版本契约文件: $VersionJsonPath"
}

$VersionData = Get-Content -Raw $VersionJsonPath | ConvertFrom-Json
$ProductVersion = $VersionData.product.version
if (-not $ProductVersion) {
    $ProductVersion = "0.1.0"
}

# 2. 解析 Git 提交短哈希
$CommitShort = $env:GIT_COMMIT_SHORT
if (-not $CommitShort -and $env:GITHUB_SHA) {
    $CommitShort = $env:GITHUB_SHA.Substring(0, [Math]::Min(7, $env:GITHUB_SHA.Length))
}
if (-not $CommitShort) {
    try {
        $gitOutput = git -C $RepoRoot rev-parse --short=7 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $gitOutput) {
            $CommitShort = $gitOutput.Trim()
        }
    } catch {
        # git 不可用时回退
    }
}
if (-not $CommitShort) {
    $CommitShort = "unknown"
}

$BuildId = "$ProductVersion+$CommitShort"

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "  小熊模拟器 Windows 发布打包" -ForegroundColor Cyan
Write-Host "  产品版本: $ProductVersion" -ForegroundColor Yellow
Write-Host "  提交短码: $CommitShort" -ForegroundColor Yellow
Write-Host "  构建标识: $BuildId" -ForegroundColor Yellow
Write-Host "  构建配置: $Configuration" -ForegroundColor Yellow
Write-Host "  输出目录: $OutputDirectory" -ForegroundColor Yellow
Write-Host "================================================================================" -ForegroundColor Cyan

# 清理暂存目录
if (Test-Path $StagingDirectory) {
    Write-Host "清理暂存目录: $StagingDirectory" -ForegroundColor Gray
    Remove-Item -Recurse -Force $StagingDirectory
}
New-Item -ItemType Directory -Force -Path $StagingDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$AppStaging = Join-Path $StagingDirectory "app"
$SpecStaging = Join-Path $StagingDirectory "spec"
$QemuStaging = Join-Path $StagingDirectory "qemu"
$AdbStaging = Join-Path $StagingDirectory "platform-tools"
$ImagesStaging = Join-Path $StagingDirectory "images"

# 3. 执行 dotnet publish
Write-Host ">>> 执行 .NET 自包含编译发布 (win-x64, ReadyToRun)..." -ForegroundColor Green
$ProjectFile = Join-Path $RepoRoot "src\XBear.App\XBear.App.csproj"

$publishArgs = @(
    "publish",
    $ProjectFile,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishReadyToRun=true",
    "-p:ProductVersion=$ProductVersion",
    "-p:GitCommitShort=$CommitShort",
    "-o", $AppStaging
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码: $LASTEXITCODE"
}

# 4. 组装 spec 共享契约
Write-Host ">>> 组装 spec 契约资产..." -ForegroundColor Green
$SpecSource = Join-Path $RepoRoot "spec\spec"
if (-not (Test-Path $SpecSource)) {
    $SpecSource = Join-Path $RepoRoot "spec"
}
New-Item -ItemType Directory -Force -Path $SpecStaging | Out-Null
Copy-Item -Path "$SpecSource\*" -Destination $SpecStaging -Recurse -Force
if (-not (Test-Path (Join-Path $SpecStaging "version.json"))) {
    throw "契约复制异常，缺少 version.json"
}

# 5. 组装 QEMU 运行时（精简捆绑）
Write-Host ">>> 收集 QEMU 运行时二进制与固件..." -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $QemuStaging | Out-Null

$ResolvedQemuDir = ""
if ($QemuPath -and (Test-Path $QemuPath)) {
    $ResolvedQemuDir = $QemuPath
} else {
    $qemuCandidates = @(
        "C:\Program Files\qemu",
        "$env:LOCALAPPDATA\Programs\qemu"
    )
    foreach ($cand in $qemuCandidates) {
        if (Test-Path (Join-Path $cand "qemu-system-x86_64.exe")) {
            $ResolvedQemuDir = $cand
            break
        }
    }
}

if ($ResolvedQemuDir) {
    Write-Host "  命中宿主 QEMU 安装: $ResolvedQemuDir" -ForegroundColor Cyan
    # 必需可执行程序
    Copy-Item -Path (Join-Path $ResolvedQemuDir "qemu-system-x86_64.exe") -Destination $QemuStaging -Force
    Copy-Item -Path (Join-Path $ResolvedQemuDir "qemu-img.exe") -Destination $QemuStaging -Force

    # 依赖动态链接库
    Get-ChildItem -Path $ResolvedQemuDir -Filter "*.dll" | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $QemuStaging -Force
    }

    # 必需固件/BIOS
    $QemuShareSource = Join-Path $ResolvedQemuDir "share"
    $QemuShareDest = Join-Path $QemuStaging "share"
    if (Test-Path $QemuShareSource) {
        New-Item -ItemType Directory -Force -Path $QemuShareDest | Out-Null
        # 复制 BIOS、UEFI 固件与核心按键映射
        Get-ChildItem -Path $QemuShareSource -Include "bios*.bin", "edk2*", "qboot.bin", "kvmvapic.bin", "linuxboot*.bin", "multiboot*.bin", "vgabios*.bin" -File | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $QemuShareDest -Force
        }
        $KeymapsDir = Join-Path $QemuShareSource "keymaps"
        if (Test-Path $KeymapsDir) {
            Copy-Item -Path $KeymapsDir -Destination $QemuShareDest -Recurse -Force
        }
    }
    Write-Host "  QEMU 组装完成，精简后大小: $([Math]::Round((Get-ChildItem -Path $QemuStaging -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 2)) MB" -ForegroundColor Cyan
} else {
    Write-Warning "未在本机找到 QEMU 安装路径。打包将保留空 qemu 目录，可通过 -QemuPath 指定。"
}

# 6. 组装 platform-tools (adb)
Write-Host ">>> 收集 Android Debug Bridge (adb) 工具..." -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $AdbStaging | Out-Null

$ResolvedAdbDir = ""
if ($AdbPath -and (Test-Path $AdbPath)) {
    $ResolvedAdbDir = $AdbPath
} else {
    $adbCandidates = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools"
    )
    foreach ($cand in $adbCandidates) {
        if (Test-Path (Join-Path $cand "adb.exe")) {
            $ResolvedAdbDir = $cand
            break
        }
    }
    if (-not $ResolvedAdbDir) {
        $adbCmd = Get-Command adb -ErrorAction SilentlyContinue
        if ($adbCmd) {
            $ResolvedAdbDir = Split-Path -Parent $adbCmd.Source
        }
    }
}

if ($ResolvedAdbDir) {
    Write-Host "  命中宿主 adb 工具: $ResolvedAdbDir" -ForegroundColor Cyan
    $adbFiles = @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll")
    foreach ($f in $adbFiles) {
        $src = Join-Path $ResolvedAdbDir $f
        if (Test-Path $src) {
            Copy-Item -Path $src -Destination $AdbStaging -Force
        }
    }
} else {
    Write-Warning "未找到 adb 工具路径。打包将保留空 platform-tools 目录，可通过 -AdbPath 指定。"
}

# 7. 预留 images 目录
New-Item -ItemType Directory -Force -Path $ImagesStaging | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ImagesStaging "manifests") | Out-Null

# 8. 生成绿色便携 Zip
$ZipFileName = "XBear-win-x64-$ProductVersion.zip"
$ZipFilePath = Join-Path $OutputDirectory $ZipFileName
Write-Host ">>> 压缩便携绿色包: $ZipFilePath ..." -ForegroundColor Green
if (Test-Path $ZipFilePath) {
    Remove-Item -Force $ZipFilePath
}

# 引入压缩类库并创建 zip 归档
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($StagingDirectory, $ZipFilePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$zipSizeMb = [Math]::Round((Get-Item $ZipFilePath).Length / 1MB, 2)
Write-Host "  便携绿色包创建成功: $ZipFileName ($zipSizeMb MB)" -ForegroundColor Green

# 9. 编译 Inno Setup 安装包
if (-not $SkipInstaller) {
    Write-Host ">>> 探测 Inno Setup 编译器 (ISCC)..." -ForegroundColor Green
    $isccPaths = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\ProgramData\chocolatey\bin\iscc.exe"
    )

    $isccExecutable = ""
    foreach ($p in $isccPaths) {
        if ($p -and (Test-Path $p)) {
            $isccExecutable = $p
            break
        }
    }

    if ($isccExecutable) {
        Write-Host "  使用 Inno Setup: $isccExecutable" -ForegroundColor Cyan
        $issFile = Join-Path $ScriptDir "installer.iss"
        $isccArgs = @(
            "/DMyAppVersion=$ProductVersion",
            "/DMyAppBuildId=$BuildId",
            "/DStagingDir=$StagingDirectory",
            "/DOutputDir=$OutputDirectory",
            $issFile
        )
        & $isccExecutable @isccArgs
        if ($LASTEXITCODE -eq 0) {
            $setupExe = Join-Path $OutputDirectory "XBear-Setup-$ProductVersion.exe"
            if (Test-Path $setupExe) {
                $setupSizeMb = [Math]::Round((Get-Item $setupExe).Length / 1MB, 2)
                Write-Host "  安装程序生成成功: XBear-Setup-$ProductVersion.exe ($setupSizeMb MB)" -ForegroundColor Green
            }
        } else {
            Write-Warning "Inno Setup 编译退出异常: $LASTEXITCODE"
        }
    } else {
        Write-Host "  未在系统检测到 ISCC.exe (Inno Setup)。已完成便携绿色包构建。" -ForegroundColor Yellow
        Write-Host "  注：CI 流水线中将通过 choco install innosetup 自动安装并编译 .exe 安装程序。" -ForegroundColor Yellow
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "  发布打包完成！产物输出至: $OutputDirectory" -ForegroundColor Cyan
Get-ChildItem -Path $OutputDirectory | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "================================================================================" -ForegroundColor Cyan
