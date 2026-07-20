[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ShellExtension,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d+\.\d+\.\d+\.\d+$")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    [string]$SigningCertificateBase64,

    [string]$SigningCertificatePassword,

    [string]$AppInstallerUri,

    [string]$PackageUri
)

$ErrorActionPreference = "Stop"
$identityRoot = $PSScriptRoot
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$extension = (Resolve-Path -LiteralPath $ShellExtension).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output "msix-layout"
$packagePath = Join-Path $output "CopyShell-x64.msix"

function Find-WindowsSdkTool([string]$Name) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $tool = Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object {
            Join-Path $_.FullName "x64\$Name"
        } |
        Where-Object {
            Test-Path -LiteralPath $_
        } |
        Select-Object -First 1

    if (-not $tool) {
        throw "找不到 Windows SDK 工具：$Name"
    }
    return $tool
}

function New-Logo([string]$Path, [int]$Size) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($Size, $Size)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.Clear([Drawing.Color]::FromArgb(19, 94, 168))
            $font = [Drawing.Font]::new(
                "Segoe UI",
                [Math]::Max(12, $Size * 0.36),
                [Drawing.FontStyle]::Bold,
                [Drawing.GraphicsUnit]::Pixel)
            try {
                $format = [Drawing.StringFormat]::new()
                try {
                    $format.Alignment = [Drawing.StringAlignment]::Center
                    $format.LineAlignment = [Drawing.StringAlignment]::Center
                    $graphics.DrawString(
                        "C",
                        $font,
                        [Drawing.Brushes]::White,
                        [Drawing.RectangleF]::new(0, 0, $Size, $Size),
                        $format)
                }
                finally {
                    $format.Dispose()
                }
            }
            finally {
                $font.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $stage -Recurse -Force
Copy-Item -LiteralPath $extension -Destination $stage -Force

$assets = Join-Path $stage "Assets"
New-Item -ItemType Directory -Path $assets -Force | Out-Null
New-Logo (Join-Path $assets "Square44x44Logo.png") 44
New-Logo (Join-Path $assets "Square150x150Logo.png") 150
New-Logo (Join-Path $assets "StoreLogo.png") 50

$manifest = Get-Content `
    -LiteralPath (Join-Path $identityRoot "AppxManifest.xml") `
    -Raw
$manifest = $manifest.Replace('$VERSION$', $Version)
$manifest = $manifest.Replace(
    '$PUBLISHER$',
    [Security.SecurityElement]::Escape($Publisher))
$manifestPath = Join-Path $stage "AppxManifest.xml"
$manifest | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$makeAppx = Find-WindowsSdkTool "makeappx.exe"
$makeAppxOutput = & $makeAppx pack /d $stage /p $packagePath /o 2>&1
$makeAppxExitCode = $LASTEXITCODE
$makeAppxOutput | Write-Host
if ($makeAppxExitCode -ne 0) {
    throw "MSIX 打包失败，退出码：$makeAppxExitCode。$($makeAppxOutput -join ' ')"
}

$hasCertificate = -not [string]::IsNullOrWhiteSpace(
    $SigningCertificateBase64)
$hasPassword = -not [string]::IsNullOrWhiteSpace(
    $SigningCertificatePassword)
if ($hasCertificate -xor $hasPassword) {
    throw "签名证书和密码必须同时提供。"
}

if ($hasCertificate) {
    $certificatePath = Join-Path $env:RUNNER_TEMP "CopyShell-signing.pfx"
    try {
        [IO.File]::WriteAllBytes(
            $certificatePath,
            [Convert]::FromBase64String($SigningCertificateBase64))
        $signTool = Find-WindowsSdkTool "signtool.exe"
        & $signTool sign `
            /fd SHA256 `
            /td SHA256 `
            /tr "http://timestamp.digicert.com" `
            /f $certificatePath `
            /p $SigningCertificatePassword `
            $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "MSIX 签名失败，退出码：$LASTEXITCODE"
        }
        & $signTool verify /pa /v $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "MSIX 签名验证失败，退出码：$LASTEXITCODE"
        }
    }
    finally {
        Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue
    }
}

if ($AppInstallerUri -or $PackageUri) {
    if (-not $AppInstallerUri -or -not $PackageUri) {
        throw "AppInstallerUri 和 PackageUri 必须同时提供。"
    }
    if (-not $hasCertificate) {
        throw "自动更新清单只能为已签名的 MSIX 生成。"
    }

    $appInstaller = Get-Content `
        -LiteralPath (Join-Path $identityRoot "CopyShell.appinstaller.template") `
        -Raw
    $appInstaller = $appInstaller.Replace('$VERSION$', $Version)
    $appInstaller = $appInstaller.Replace(
        '$PUBLISHER$',
        [Security.SecurityElement]::Escape($Publisher))
    $appInstaller = $appInstaller.Replace(
        '$APPINSTALLER_URI$',
        [Security.SecurityElement]::Escape($AppInstallerUri))
    $appInstaller = $appInstaller.Replace(
        '$PACKAGE_URI$',
        [Security.SecurityElement]::Escape($PackageUri))
    $appInstaller | Set-Content `
        -LiteralPath (Join-Path $output "CopyShell.appinstaller") `
        -Encoding utf8NoBOM
}

Write-Host "CopyShell MSIX：$packagePath"
