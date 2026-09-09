param([Parameter(Mandatory)][string]$FfmpegPath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'tests/LeagueTracker.ReviewSmoke/LeagueTracker.ReviewSmoke.csproj'
$fixture = Join-Path $root 'tests/LeagueTracker.ReviewSmoke/bin/fixture'
New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'metadata') | Out-Null
& $FfmpegPath -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=1280x720:rate=30' -t 20 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart -y (Join-Path $fixture 'Review-demo.mp4')
if ($LASTEXITCODE -ne 0) { throw 'Could not create the test video' }
@{
    videoFile = 'Review-demo.mp4'; matchId = 'EUW1_123'; activePlayer = 'Demo#EUW'
    recordingStartUtc = '2026-09-08T12:00:00Z'; recordingEndUtc = '2026-09-08T12:00:20Z'
    clockMap = @(@{ videoSec = 0; gameSec = 0 }, @{ videoSec = 20; gameSec = 20 })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'metadata/Review-demo.json')
dotnet build $project -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Smoke harness build failed' }
$exe = Join-Path $root 'tests/LeagueTracker.ReviewSmoke/bin/Release/net10.0-windows/win-x64/LeagueTracker.ReviewSmoke.exe'
$process = Start-Process -FilePath $exe -ArgumentList ('"' + $fixture + '"') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $fixture 'smoke.log') -RedirectStandardError (Join-Path $fixture 'smoke-error.log') -PassThru
if (-not $process.WaitForExit(60000)) { throw "Smoke test did not finish; inspect $fixture/smoke.log (PID $($process.Id))" }
Get-Content -LiteralPath (Join-Path $fixture 'smoke.log')
Get-Content -LiteralPath (Join-Path $fixture 'smoke-error.log')
if ($process.ExitCode -ne 0) { throw 'Review smoke test failed' }
