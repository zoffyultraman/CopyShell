[CmdletBinding()]
param(
    [string]$SourceDirectory = $PSScriptRoot,
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$requiredFiles = @(
    "CopyShell.App.exe",
    "CopyShell.ShellExtension.dll"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $source $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "缺少安装文件：$path"
    }
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\CopyShell"
$versionDirectory = Join-Path $installRoot (
    "app-" +
    (Get-Date -Format "yyyyMMddHHmmss") +
    "-" +
    [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $versionDirectory -Recurse -Force

$extension = Join-Path $versionDirectory "CopyShell.ShellExtension.dll"
$regsvr32 = Join-Path $env:SystemRoot "System32\regsvr32.exe"
$classKey = "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}\InprocServer32"
$previousExtension = $null
if (Test-Path -LiteralPath $classKey) {
    $previousExtension = (Get-Item -LiteralPath $classKey).GetValue("")
}
if ($previousExtension -and (Test-Path -LiteralPath $previousExtension)) {
    & $regsvr32 /s /u $previousExtension
    if ($LASTEXITCODE -ne 0) {
        throw "旧版 Shell Extension 注销失败，退出码：$LASTEXITCODE"
    }
}

& $regsvr32 /s $extension
if ($LASTEXITCODE -ne 0) {
    if ($previousExtension -and (Test-Path -LiteralPath $previousExtension)) {
        & $regsvr32 /s $previousExtension
    }
    throw "Shell Extension 注册失败，退出码：$LASTEXITCODE"
}

$currentFile = Join-Path $installRoot "current.txt"
$versionDirectory | Set-Content -LiteralPath $currentFile -Encoding utf8

Get-ChildItem -LiteralPath $installRoot -Directory -Filter "app-*" |
    Where-Object {
        $_.FullName -ne $versionDirectory
    } |
    ForEach-Object {
        try {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "旧版本仍被资源管理器占用，将在以后清理：$($_.FullName)"
        }
    }

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Process explorer.exe
}

Write-Host "CopyShell 已为当前用户安装。"
Write-Host "安装位置：$versionDirectory"
if (-not $RestartExplorer) {
    Write-Host "如果右键菜单没有立即出现，请重新启动 Windows 资源管理器。"
}
