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
- A window whose recording freezes (hung replay simulation) is retried once on
  a freshly relaunched game process; if it freezes again, that window is
  skipped, the remaining windows still render, and the job fails naming the
  skipped windows - partial coverage is never silent.
- A job postponed 3 times for the identical reason is failed instead of
  recycled: identical repeats mean deterministic, and deterministic failures
  belong on the Data page, not in an invisible retry loop.
- Keep the replay window visible while recording (not minimized) - window
  capture grabs the window's contents.

## Live-game recording

With `RecordGames` on (the default), the agent also records your own games:
when the local client's gameflow phase turns `InProgress` (a real game -
replay renders report `WatchInProgress` and never trigger it), the game
window is captured via Desktop Duplication straight into NVENC on the GPU,
so the encoding cost while playing is negligible. Recording stops when the
game ends and produces, per game, in `RecordingsDir`:

- `<date>_<matchId>.mp4` - the full VOD (faststart, browser-playable).
- `<date>_<matchId>.json` - match id, queue, active player, and a
  video-time -> game-clock map (sampled from the Live Client API) so
  timeline events can be placed on the video.
- `<date>_<matchId>.jpg` - a mid-game thumbnail.

While recording runs, the capture writes a fragmented `.part.mp4`, so a
crash or power cut costs seconds of footage, not the game; interrupted
recordings are finalized on the next agent start. If NVENC refuses to
start, one CPU-encoder retry (x264 veryfast) happens before giving up on
that game. The game must be on the primary display (fullscreen or
borderless both work - Desktop Duplication captures either).

## Test/debug environment flags

- `LT_MOCK_RENDER=1` - render ffmpeg test patterns instead of launching the
  game (verifies the queue/upload pipeline on a machine without League).
- `LT_RECORD_TEST=1` - record 10s of the primary desktop through the real
  capture/encode/finalize path, then exit (verifies NVENC without a game).
- `LT_RECORD=0` / `LT_RECORDINGS_DIR` - recording overrides.
- `LT_ONCE=1` - process a single job, then exit.
- `LT_MAX_WINDOWS=1` - cap windows per job (quick smoke of a real render).
- `LT_SERVER_URL` / `LT_LEAGUE_PATH` / `LT_FFMPEG_PATH` - config overrides.

## Publish (from the dev machine)

```
dotnet publish src/LeagueTracker.RenderAgent -c Release
# output: src/LeagueTracker.RenderAgent/bin/Release/net10.0/win-x64/publish
```
