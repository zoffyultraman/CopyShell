[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Files,

    [Parameter(Mandatory = $true)]
    [string]$SigningCertificateBase64,

    [Parameter(Mandatory = $true)]
    [string]$SigningCertificatePassword
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $tool = Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object {
            Join-Path $_.FullName "x64\signtool.exe"
        } |
        Where-Object {
            Test-Path -LiteralPath $_
        } |
        Select-Object -First 1
    if (-not $tool) {
        throw "找不到 Windows SDK 签名工具。"
    }
    return $tool
}

if ([string]::IsNullOrWhiteSpace($SigningCertificateBase64) -or
    [string]::IsNullOrWhiteSpace($SigningCertificatePassword)) {
    throw "发布签名需要证书和密码。"
}

$resolvedFiles = $Files | ForEach-Object {
    (Resolve-Path -LiteralPath $_).Path
}
$certificatePath = Join-Path $env:RUNNER_TEMP "CopyShell-binary-signing.pfx"
try {
    [IO.File]::WriteAllBytes(
        $certificatePath,
        [Convert]::FromBase64String($SigningCertificateBase64))
    $signTool = Find-SignTool
    foreach ($file in $resolvedFiles) {
        & $signTool sign `
            /fd SHA256 `
            /td SHA256 `
            /tr "http://timestamp.digicert.com" `
            /f $certificatePath `
            /p $SigningCertificatePassword `
            $file
        if ($LASTEXITCODE -ne 0) {
            throw "文件签名失败：$file"
        }
        & $signTool verify /pa /v $file
        if ($LASTEXITCODE -ne 0) {
            throw "文件签名验证失败：$file"
        }
    }
}
finally {
    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue
}
