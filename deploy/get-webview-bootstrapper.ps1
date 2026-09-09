[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$asset = Join-Path $PSScriptRoot '../src/LeagueTracker.RenderAgent/Assets/MicrosoftEdgeWebview2Setup.exe'
$download = $asset + '.download'
try {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $download -TimeoutSec 120
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
        throw 'The WebView2 bootstrapper does not have a valid Microsoft signature'
    }
    if ((Get-Item -LiteralPath $download).Length -gt 8MB) { throw 'Unexpected WebView2 bootstrapper size' }
    Move-Item -LiteralPath $download -Destination $asset -Force
    Write-Host "WebView2 bootstrapper verified: $((Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash)"
} finally {
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
}
