[CmdletBinding()]
param(
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"
$classKey = "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}\InprocServer32"
$extension = $null

if (Test-Path -LiteralPath $classKey) {
    $extension = (Get-Item -LiteralPath $classKey).GetValue("")
}

if ($extension -and (Test-Path -LiteralPath $extension)) {
    $regsvr32 = Join-Path $env:SystemRoot "System32\regsvr32.exe"
    & $regsvr32 /s /u $extension
    if ($LASTEXITCODE -ne 0) {
        throw "Shell Extension 注销失败，退出码：$LASTEXITCODE"
    }
}
else {
    $registryKeys = @(
        "Registry::HKEY_CURRENT_USER\Software\Classes\*\shellex\ContextMenuHandlers\CopyShell",
        "Registry::HKEY_CURRENT_USER\Software\Classes\Directory\shellex\ContextMenuHandlers\CopyShell",
        "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}"
    )
    foreach ($key in $registryKeys) {
        Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Process explorer.exe
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\CopyShell"
try {
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction Stop
}
catch {
    Write-Warning "部分文件仍被 Windows 资源管理器占用。重启资源管理器后请删除：$installRoot"
}

Write-Host "CopyShell 已为当前用户卸载。"
