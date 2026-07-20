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
$versionDirectory = Join-Path $installRoot ("app-" + (Get-Date -Format "yyyyMMddHHmmss"))
New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $versionDirectory -Recurse -Force

$extension = Join-Path $versionDirectory "CopyShell.ShellExtension.dll"
$regsvr32 = Join-Path $env:SystemRoot "System32\regsvr32.exe"
& $regsvr32 /s $extension
if ($LASTEXITCODE -ne 0) {
    throw "Shell Extension 注册失败，退出码：$LASTEXITCODE"
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
