[CmdletBinding()]
param(
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

$classKey = "Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}\InprocServer32"
$extension = $null

if (Test-Path -LiteralPath $classKey) {
    $extension = (Get-Item -LiteralPath $classKey).GetValue("")
}

if ($extension -and (Test-Path -LiteralPath $extension)) {
    $unregisterExitCode = Invoke-RegSvr32 -DllPath $extension -Unregister
    if ($unregisterExitCode -ne 0) {
        throw "Failed to unregister the Shell Extension. Exit code: $unregisterExitCode"
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
    Write-Warning "Some files are still in use. Restart Windows Explorer and remove: $installRoot"
}

Write-Host "CopyShell has been uninstalled for the current user."
