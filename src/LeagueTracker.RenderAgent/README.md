# LeagueTracker Render Agent

Runs on the **gaming PC** (the machine with League of Legends installed) and
turns archived replays into per-fight mp4 clips for the tracker:

1. Polls the tracker server for render jobs (outbound HTTP only - works from
   behind NAT, no ports opened on the gaming PC).
2. Downloads the job's `.rofl` into the League client's Replays folder and
   launches it through the client's local API (scan + watch). Vanguard denies
   direct launches of the game binary, so the client must be running and
   logged in - the agent waits while it isn't.
3. Drives Riot's official Replay API (`https://127.0.0.1:2999`): locks the
   camera on you, seeks to each kill/death window.
4. Records each window with ffmpeg (window capture) and uploads the mp4s.

The agent is windowless: no console, ffmpeg hidden, all output in `agent.log`
next to the exe (self-rotating). The only thing you ever see is the replay
window itself while a clip records.

## Setup on the gaming PC

1. Copy the published folder (exe + `appsettings.json`) anywhere.
2. Install ffmpeg: `winget install Gyan.FFmpeg` (or drop `ffmpeg.exe` next to
   the agent exe).
3. Edit `appsettings.json`: set `ServerUrl` to the tracker machine, e.g.
   `http://192.168.1.50:5170`.
4. Run `LeagueTracker.RenderAgent.exe`. First run auto-detects the League
   install and adds `EnableReplayApi=1` to `Config/game.cfg` if missing (the
   Replay API needs it; the game reads it at launch). Progress lands in
   `agent.log` next to the exe.

For always-on operation, drop a shortcut to the exe in `shell:startup` (it
must run in the interactive session - the game has to render to a real
desktop for capture to work). If no tracker is reachable at startup the agent
waits and retries forever, so a booting NAS is fine.

`LeagueTracker.ReplayLauncher.exe --register` (same publish flow) registers
the `leaguereplay://` protocol so the match pages' "watch replay" links launch
replays through the client too.

## Behaviour

- Never runs while you play: it skips whenever the tracker reports you in a
  live game or any League game process is running locally.
- Skips while the League client is closed - Vanguard only allows replay
  launches through the client, so jobs wait until you next open it.
- Replays are patch-locked by the client. The agent compares the replay's
  patch to the installed client and fails the job cleanly on mismatch - which
  is why it renders soon after each game, before the next patch lands.
- Failed jobs are marked on the server (visible on the Data & sync page) and
  retried only after "retry" is requested (`POST /api/render/{matchId}/retry`).
- Keep the replay window visible while recording (not minimized) - window
  capture grabs the window's contents.

## Test/debug environment flags

- `LT_MOCK_RENDER=1` - render ffmpeg test patterns instead of launching the
  game (verifies the queue/upload pipeline on a machine without League).
- `LT_ONCE=1` - process a single job, then exit.
- `LT_MAX_WINDOWS=1` - cap windows per job (quick smoke of a real render).
- `LT_SERVER_URL` / `LT_LEAGUE_PATH` / `LT_FFMPEG_PATH` - config overrides.

## Publish (from the dev machine)

```
dotnet publish src/LeagueTracker.RenderAgent -c Release
# output: src/LeagueTracker.RenderAgent/bin/Release/net10.0/win-x64/publish
```
