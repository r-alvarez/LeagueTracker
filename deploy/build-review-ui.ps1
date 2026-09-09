# Build one embedded archive, compatible with the agent's existing flat updater.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$web = Join-Path $root 'src/leaguetracker-web'
Push-Location $web
try {
    npm ci --ignore-scripts --no-audit --no-fund --cache (Join-Path $web 'dist/npm-cache')
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed' }
    npm run build:desktop
    if ($LASTEXITCODE -ne 0) { throw 'Desktop UI build failed' }
    $archive = Join-Path $root 'src/LeagueTracker.RenderAgent/Assets/review-ui.zip'
    Copy-Item -LiteralPath (Join-Path $web 'dist/desktop/THIRD-PARTY-NOTICES.md') -Destination (Join-Path $root 'src/LeagueTracker.RenderAgent/Assets/review-THIRD-PARTY-NOTICES.md') -Force
    Compress-Archive -Path (Join-Path $web 'dist/desktop/*') -DestinationPath $archive -Force
    Write-Host "Embedded review UI: $archive"
} finally { Pop-Location }
