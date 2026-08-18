<#
.SYNOPSIS
  Mint a YouTube refresh token for a Google Cloud OAuth client, without the
  client secret or the token ever leaving your own console.

.DESCRIPTION
  Runs the agent's built-in consent flow (LeagueTracker.RenderAgent.exe
  --youtube-auth) from a scratch copy of the exe, so nothing next to your
  real agent is touched. Prompts for the OAuth client id and secret (masked),
  opens the Google consent page - sign in as the CHANNEL's account - and
  prints the refresh token once, for pasting into the tracker (Portainer
  stack env vars: YT_*_REFRESH_TOKEN) or an agent's appsettings.json.

  Run this in your own terminal. Do not run it through a chat or a shared
  session - the token is printed to the console.

.PARAMETER AgentExe
  The agent exe to use. Default: the installed agent, else the repo's debug
  build. Any published build works; the exe is copied to a temp folder first.

.EXAMPLE
  .\deploy\youtube-auth.ps1
#>
param(
    [string]$AgentExe = ""
)

$ErrorActionPreference = "Stop"

if (-not $AgentExe) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\LeagueTracker Agent\LeagueTracker.RenderAgent.exe"),
        (Join-Path $PSScriptRoot "..\src\LeagueTracker.RenderAgent\bin\x64\Debug\net10.0-windows\win-x64\LeagueTracker.RenderAgent.exe"),
        (Join-Path $PSScriptRoot "..\src\LeagueTracker.RenderAgent\bin\x64\Release\net10.0-windows\win-x64\LeagueTracker.RenderAgent.exe")
    )
    $AgentExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $AgentExe) { throw "No agent exe found - pass -AgentExe <path to LeagueTracker.RenderAgent.exe> (an installed agent or a build)" }
}
$AgentExe = (Resolve-Path $AgentExe).Path

# A scratch copy: --youtube-auth writes youtube-token.json next to the exe it
# runs from, and the installed agent's folder must stay as it is.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("lt-youtube-auth-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force $scratch | Out-Null
$srcDir = Split-Path $AgentExe
if (Test-Path (Join-Path $srcDir "LeagueTracker.RenderAgent.dll")) {
    # A framework-dependent build (bin/): the exe needs its neighbours.
    Copy-Item (Join-Path $srcDir "*") $scratch -Recurse
} else {
    # A published single-file agent: the exe and its loose library suffice.
    Copy-Item $AgentExe $scratch
    foreach ($sidecar in @("ScreenRecorderLib.dll", "appsettings.json")) {
        $p = Join-Path $srcDir $sidecar
        if (Test-Path $p) { Copy-Item $p $scratch }
    }
}
Remove-Item (Join-Path $scratch "youtube-token.json"), (Join-Path $scratch "agent.log") -ErrorAction SilentlyContinue
$exe = Join-Path $scratch (Split-Path $AgentExe -Leaf)

$clientId = Read-Host "OAuth client id (Google Cloud > Credentials, type Desktop app)"
$secretSecure = Read-Host "OAuth client secret" -AsSecureString
$secret = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secretSecure))
if (-not $clientId -or -not $secret) { throw "client id and secret are both required" }

Write-Host ""
Write-Host "Opening the Google consent page - sign in as the account that owns the CHANNEL the videos should land on."
Write-Host "(The client id/secret are passed to the agent as process environment variables; nothing is written to disk but the token.)"
Write-Host ""

$env:LT_YOUTUBE_CLIENT_ID = $clientId
$env:LT_YOUTUBE_CLIENT_SECRET = $secret
$env:LT_SERVER_URL = "http://localhost"   # skips the first-run setup window; the auth flow never contacts a tracker
$env:LT_NO_TRAY = "1"
try {
    # The agent is a windowed app: "& exe" would return at once and the exit
    # code would be stale. Start-Process -Wait blocks until the consent flow
    # ends and hands back the real code.
    $proc = Start-Process -FilePath $exe -ArgumentList "--youtube-auth" -Wait -PassThru -NoNewWindow
    $code = $proc.ExitCode
} finally {
    Remove-Item Env:LT_YOUTUBE_CLIENT_ID, Env:LT_YOUTUBE_CLIENT_SECRET, Env:LT_SERVER_URL, Env:LT_NO_TRAY -ErrorAction SilentlyContinue
}

$tokenFile = Join-Path $scratch "youtube-token.json"
$logFile = Join-Path $scratch "agent.log"
if ($code -ne 0 -or -not (Test-Path $tokenFile)) {
    Write-Host ""
    Write-Host "Authorization did not complete (exit $code). The agent said:" -ForegroundColor Yellow
    if (Test-Path $logFile) { Get-Content $logFile | Select-Object -Last 4 | ForEach-Object { "  $_" } }
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

$token = (Get-Content $tokenFile -Raw | ConvertFrom-Json).refresh_token
$channelLine = (Get-Content $logFile | Select-String "YouTube authorized for channel" | Select-Object -Last 1)
Write-Host ""
if ($channelLine) { Write-Host "  $($channelLine.Line.Substring(11))" -ForegroundColor Cyan }
Write-Host ""
Write-Host "Refresh token for this client + channel (paste it where it belongs, then close this window):" -ForegroundColor Green
Write-Host ""
Write-Host "  $token"
Write-Host ""
Write-Host "  Tracker (Portainer stack env)  : YT_<agent>_REFRESH_TOKEN, next to YT_<agent>_CLIENT_ID / _CLIENT_SECRET"
Write-Host "  or an agent's appsettings.json : YouTubeClientId / YouTubeClientSecret / YouTubeRefreshToken"
Write-Host ""
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Scratch folder removed; the token exists only where you paste it."
