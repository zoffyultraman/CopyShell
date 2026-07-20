[CmdletBinding()]
param(
    [string]$SourceDirectory = $PSScriptRoot,
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"

function Invoke-RegSvr32 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DllPath,

        [switch]$Unregister
    )

    if ($DllPath.Contains('"')) {
        throw "The Shell Extension path contains an invalid quote character."
    }

    $regsvr32 = Join-Path $env:SystemRoot "System32\regsvr32.exe"
    $arguments = @("/s")
    if ($Unregister) {
        $arguments += "/u"
    }
    $arguments += '"' + $DllPath + '"'

    $process = Start-Process `
        -FilePath $regsvr32 `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    $exitCode = $process.ExitCode
    $process.Dispose()
    return $exitCode
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$requiredFiles = @(
    "CopyShell.App.exe",
    "CopyShell.Worker.exe",
    "CopyShell.ShellExtension.dll"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $source $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required installation file is missing: $path"
    }
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\CopyShell"
$versionName = "app-" + (Get-Date -Format "yyyyMMddHHmmss")
$versionName += "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$versionDirectory = Join-Path $installRoot $versionName
New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $versionDirectory -Recurse -Force

$extension = Join-Path $versionDirectory "CopyShell.ShellExtension.dll"
$classKey = "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}\InprocServer32"
$previousExtension = $null
if (Test-Path -LiteralPath $classKey) {
    $previousExtension = (Get-Item -LiteralPath $classKey).GetValue("")
}
if ($previousExtension -and (Test-Path -LiteralPath $previousExtension)) {
    $unregisterExitCode = Invoke-RegSvr32 `
        -DllPath $previousExtension `
        -Unregister
    if ($unregisterExitCode -ne 0) {
        throw "Failed to unregister the previous Shell Extension. Exit code: $unregisterExitCode"
    }
}

$registerExitCode = Invoke-RegSvr32 -DllPath $extension
if ($registerExitCode -ne 0) {
    if ($previousExtension -and (Test-Path -LiteralPath $previousExtension)) {
        $rollbackExitCode = Invoke-RegSvr32 -DllPath $previousExtension
        if ($rollbackExitCode -ne 0) {
            Write-Warning "Failed to restore the previous Shell Extension. Exit code: $rollbackExitCode"
        }
    }
    throw "Failed to register the Shell Extension. Exit code: $registerExitCode"
}

$currentFile = Join-Path $installRoot "current.txt"
$versionDirectory | Set-Content -LiteralPath $currentFile -Encoding utf8

$oldVersions = Get-ChildItem -LiteralPath $installRoot -Directory -Filter "app-*"
foreach ($oldVersion in $oldVersions) {
    if ($oldVersion.FullName -eq $versionDirectory) {
        continue
    }

    try {
        Remove-Item -LiteralPath $oldVersion.FullName -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "The previous version is still in use and will be removed later: $($oldVersion.FullName)"
    }
}

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Process explorer.exe
}

Write-Host "CopyShell has been installed for the current user."
Write-Host "Installation directory: $versionDirectory"
if (-not $RestartExplorer) {
    Write-Host "Restart Windows Explorer if the context menu does not appear immediately."
}
