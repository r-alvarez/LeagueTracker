<#
.SYNOPSIS
  Build the render agent, stamp a version, zip it, and drop the zip where the
  trackers publish agent builds from - every agent then updates itself.

.DESCRIPTION
  Version = yyyy.M.d.HHmm (a 4-part System.Version; newer build = higher
  number, no bookkeeping). The zip holds the agent exe, ScreenRecorderLib.dll,
  the ReplayLauncher exe, an appsettings.template.json (never the live
  appsettings.json - agents keep theirs) and, when found on this machine,
  ffmpeg.exe so a friend's PC needs nothing else installed.

.PARAMETER ReleaseDir
  Where the trackers look (Agent:ReleaseDir - the shared agent-releases folder
  on the NAS, e.g. E:\TrueNas\apps\leaguetracker\agent-releases). Omit to just
  build the zip into the local out folder.

.PARAMETER Keep
  How many older zips to leave in ReleaseDir (default 3).

.EXAMPLE
  .\deploy\publish-agent.ps1 -ReleaseDir E:\TrueNas\apps\leaguetracker\agent-releases
#>
param(
    [string]$ReleaseDir = "",
    [int]$Keep = 3
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$now = (Get-Date).ToUniversalTime()
# UTC, to the second, every part under 65535 (assembly version limit):
# year . month*100+day . hour*100+minute . second - strictly increasing.
$version = "{0}.{1}.{2}.{3}" -f $now.Year, ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute), $now.Second
$out = Join-Path $root "src\LeagueTracker.RenderAgent\bin\publish-$version"

Write-Host "Publishing agent $version"
dotnet publish (Join-Path $root "src\LeagueTracker.RenderAgent") -c Release -o $out -p:Version=$version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "agent publish failed" }
dotnet publish (Join-Path $root "src\LeagueTracker.ReplayLauncher") -c Release -o $out -p:Version=$version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "replay launcher publish failed" }

# The build's appsettings is a template: the live file on each machine is
# theirs and the updater never touches it.
Move-Item (Join-Path $out "appsettings.json") (Join-Path $out "appsettings.template.json") -Force

# Our ScreenRecorderLib (upstream + HDR tone-map, deploy/screenrecorderlib)
# replaces the stock NuGet copy. Without it HDR desktops record bleached -
# fine for a dev zip, not for a release; the release workflow builds it first.
$patchedRecorder = Join-Path $root "deploy\screenrecorderlib\out\ScreenRecorderLib.dll"
if (Test-Path $patchedRecorder) {
    Copy-Item $patchedRecorder (Join-Path $out "ScreenRecorderLib.dll") -Force
    Write-Host "  bundled ScreenRecorderLib with HDR tone-mapping"
} else {
    Write-Warning "deploy\screenrecorderlib\out\ScreenRecorderLib.dll not built - shipping the stock library (HDR desktops record bleached); run deploy\screenrecorderlib\build.ps1"
}

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
# A package-manager shim (chocolatey, scoop) is a few KB that launches the
# real exe elsewhere - copying it ships nothing. Real ffmpeg is tens of MB.
if ($ffmpeg -and (Get-Item $ffmpeg).Length -lt 5MB) { Write-Warning "$ffmpeg looks like a shim, not ffmpeg itself - not bundling it"; $ffmpeg = $null }
if ($ffmpeg) { Copy-Item $ffmpeg (Join-Path $out "ffmpeg.exe") -Force; Write-Host "  bundled ffmpeg from $ffmpeg" }
else { Write-Warning "ffmpeg not on PATH - the zip ships without it (friends then need winget install Gyan.FFmpeg)" }

Get-ChildItem $out -Include *.pdb -Recurse | Remove-Item -Force

$zip = Join-Path $root "src\LeagueTracker.RenderAgent\bin\LeagueTracker.RenderAgent-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "  $zip ($size MB)"

if ($ReleaseDir) {
    New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
    # Copy under a temp name and rename: a tracker must never see a half-written zip.
    $tmp = Join-Path $ReleaseDir "$([IO.Path]::GetFileName($zip)).partial"
    Copy-Item $zip $tmp -Force
    Move-Item $tmp (Join-Path $ReleaseDir ([IO.Path]::GetFileName($zip))) -Force
    Write-Host "  published to $ReleaseDir"

    Get-ChildItem $ReleaseDir -Filter "LeagueTracker.RenderAgent-*.zip" |
        Sort-Object { [Version]($_.BaseName -replace '^.*-', '') } -Descending |
        Select-Object -Skip ($Keep + 1) |
        ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "  pruned $($_.Name)" }
}
# Setup.exe when Inno Setup is around (always in CI; locally: winget install JRSoftware.InnoSetup).
# Per-user installer: Start Menu, Settings > Apps, the agent's setup window at the end.
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if ($iscc) {
    $binDir = Join-Path $root "src/LeagueTracker.RenderAgent/bin"
    & $iscc "/DAppVersion=$version" "/DSourceDir=$out" "/DOutDir=$binDir" (Join-Path $root "deploy/agent-setup.iss")
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    $setup = Join-Path $binDir "LeagueTracker.Agent-Setup-$version.exe"
    Write-Host "  $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
    if ($ReleaseDir) { Copy-Item $setup (Join-Path $ReleaseDir ([IO.Path]::GetFileName($setup))) -Force }
} else {
    Write-Warning "Inno Setup (ISCC) not found - no Setup.exe built, zip only"
}

Remove-Item $out -Recurse -Force
Write-Host "Done - agents pick up $version on their next hourly check (or heartbeat hint), when idle."
