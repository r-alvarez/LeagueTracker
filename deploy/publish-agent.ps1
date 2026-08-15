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
$version = (Get-Date).ToUniversalTime().ToString("yyyy.M.d.HHmm")   # UTC: CI and local publishes must sort together
$out = Join-Path $root "src\LeagueTracker.RenderAgent\bin\publish-$version"

Write-Host "Publishing agent $version"
dotnet publish (Join-Path $root "src\LeagueTracker.RenderAgent") -c Release -o $out -p:Version=$version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "agent publish failed" }
dotnet publish (Join-Path $root "src\LeagueTracker.ReplayLauncher") -c Release -o $out -p:Version=$version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "replay launcher publish failed" }

# The build's appsettings is a template: the live file on each machine is
# theirs and the updater never touches it.
Move-Item (Join-Path $out "appsettings.json") (Join-Path $out "appsettings.template.json") -Force

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
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
Remove-Item $out -Recurse -Force
Write-Host "Done - agents pick up $version on their next hourly check (or heartbeat hint), when idle."
