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

## Setup on a player's PC

Full walkthrough (owner side included) in `docs/agent-handoff.md`. Short form:

1. Unzip the latest `LeagueTracker.RenderAgent-<version>.zip` (published by
   `deploy/publish-agent.ps1`; ships ffmpeg) anywhere.
2. Double-click the exe. Without settings it opens the setup window: tracker
   URL (one - the agent asks the server which accounts it hosts and treats
   each as a tracker, so a recording lands on the account that was playing
   and the renderer serves all of them), Cloudflare Access service token, the role (Recorder for a player's
   PC, Renderer for the dedicated render box, Both), recordings folder; Test
   connection, Save. Save writes `appsettings.json` (only those keys - other
   keys already in the file survive), registers a per-user run-at-logon
   entry, starts the agent and reports in a message box. Everything else,
   YouTube credentials included, arrives from the tracker's agent profile
   (`GET /api/agent/profile`); any key written locally wins over it.
   `--install` forces the window + registration, `--setup` just the window
   (restarts a running agent on save), `--uninstall` reverses it. First run
   auto-detects the League install and adds `EnableReplayApi=1` to
   `Config/game.cfg` if missing.

The agent lives in the tray: the dot by the clock is green idle, red busy
(recording/uploading/rendering), grey-with-bars paused, amber waiting for
the tracker, orange when the last thing failed. Right-click for
**Pause/Resume** (a `paused` file next to the exe - survives reboots; also
`--pause`/`--resume`), open tracker/recordings/log, Settings…, check for
updates, quit.
Quit and deploy stops go through `stop.requested`, so nothing is cut short.

Every poll the agent posts a heartbeat (version, role, state, last recording,
YouTube health) that the tracker's Data page shows under **Agents**. Builds
dropped in the trackers' release folder install themselves when the agent is
idle (previous build kept as `*.prev`; `appsettings.json` and
`youtube-token.json` are never touched); dev builds (version 0.0.0.0) never
self-update.

`LeagueTracker.ReplayLauncher.exe --register` (in the same zip) registers the
`leaguereplay://` protocol so the match pages' "watch replay" links launch
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

HDR desktops (Windows HDR, Auto HDR, RTX HDR) are handled by the WGC engine:
the agent's ScreenRecorderLib build (`deploy/screenrecorderlib`) captures the
desktop as scRGB and tone-maps it to SDR on the GPU, so the VOD looks like the
screen. The stock library - and the ddagrab fallback, which has no HDR path -
would record the 8-bit clamp instead: brighter, paler, highlights blown. The
agent probes the display at each capture start, records `displayHdr` in the
game's `.json`, and warns on the heartbeat (tray + Data page "Last error")
only when an HDR desktop ends up on a path that can't handle it. `HdrToneMap:
false` (or `LT_HDR_TONEMAP=0`) forces the stock behaviour - a support knob.

### Automatic YouTube publishing

With `YouTubeUpload` on, every finished recording is uploaded to the
authorized YouTube channel and the resulting link is registered with the
tracker that owns the match - the storage-free review mode with zero manual
steps. Delivery (tracker sidecars/VOD, YouTube, link, retention) runs on its
own loop, so the recorder is watching for the next game the moment one ends,
and uploads **do not stop for a game**: while one runs they are paced to
`UploadInGameMbps` (0 = half the line's measured idle upstream, never under
3 Mbps), full speed otherwise, so game 1 is on YouTube by the time game 2
ends without lagging it. Uploads are resumable (a deploy or a dead connection
continues from the last acknowledged byte). Once a video is processed by
YouTube and linked on the tracker the local mp4 is deleted and the sidecars
kept, unless `KeepRecordingsAfterPublish` (local file or tracker profile) says
keep. The video title is the recording's file name minus its separators
("Road to Platinum 03 Aug 2026 Game 2").

One-time setup:

1. Google Cloud Console: create a project, enable the **YouTube Data API
   v3**, create an OAuth client ID of type **Desktop app**, and set the OAuth
   consent screen to **In production** (left in "Testing", refresh tokens
   expire after 7 days).
2. Put the client id/secret in `appsettings.json` (`YouTubeClientId` /
   `YouTubeClientSecret`) and set `"YouTubeUpload": true`.
3. Run `LeagueTracker.RenderAgent.exe --youtube-auth` once and approve in the
   browser **with the channel's Google account** - pick the channel itself on
   the chooser (brand channels are listed under the account). The refresh
   token lands in `youtube-token.json` next to the exe; `agent.log` names the
   authorized channel so a wrong-account consent is caught immediately.
   `deploy/youtube-auth.ps1` wraps this for a tracker-held token: it runs
   the flow from a scratch copy, takes the client secret masked, and prints
   the token once for the Portainer stack environment.

Caveats: the API prices an upload at 1600 of the default 10,000 daily quota
units per Google project (~6 uploads/day - the excess just queues to the
next day, and the agent's row on the Data page says so), and Google forces
uploads from unaudited API projects to **private** regardless of
`YouTubeVisibility` until the project passes their audit/exception process.
A busy player's agent can be given its own project via the tracker's
per-agent profile (`Agent__Profiles__<agent>__YouTube*`).

### Diagnostics from the tracker

The Data page's Agent access row has **Log**: the agent ships the last
512 KB of `agent.log` on its next heartbeat and the file appears on the row
(newest five kept per agent under `<data root>/agent-logs/`). No file to find
on the player's PC.

## Test/debug environment flags

- `LT_MOCK_RENDER=1` - render ffmpeg test patterns instead of launching the
  game (verifies the queue/upload pipeline on a machine without League).
- `LT_RECORD_TEST=1` - record 10s of the primary desktop through the real
  capture/encode/finalize path, then exit (verifies NVENC without a game).
- `LT_RECORD=0` / `LT_RECORDINGS_DIR` - recording overrides.
- `LT_ONCE=1` - process a single job, then exit.
- `LT_RENDER=0` - RenderReplays override; `LT_NO_TRAY=1` - no tray icon.
- `LT_MAX_WINDOWS=1` - cap windows per job (quick smoke of a real render).
- `LT_SERVER_URL` / `LT_LEAGUE_PATH` / `LT_FFMPEG_PATH` - config overrides.

## Publish (from the dev machine)

```
deploy\publish-agent.ps1 -ReleaseDir <NAS>pps\leaguetrackergent-releases
```

Stamps `yyyy.M.d.HHmm`, zips agent + launcher + ScreenRecorderLib + ffmpeg +
`appsettings.template.json`, drops it in the shared release folder every
tracker serves from `/api/agent/release`. Agents update themselves within the
hour (or on the next heartbeat's version hint), only while idle. Without
`-ReleaseDir` the zip just lands in `src/LeagueTracker.RenderAgent/bin/`.

## Deploying over a running agent

Never hard-kill the agent - it may have just claimed a render job (a claim
can land seconds after the last log line), and a kill mid-render orphans
the replay process, which then blocks all rendering as "Game client
running". Instead:

1. Create `stop.requested` next to the deployed exe.
2. Wait for the process to exit (in-flight jobs postpone cleanly and
   re-lease; an in-flight game recording finalizes what it has).
3. Replace the exe, delete nothing else, relaunch. The agent deletes the
   sentinel on startup.

If an agent was hard-killed anyway, the next agent cleans up: a game
process while the client reports out-of-game for 3 consecutive polls is
recognized as an orphaned replay and killed.
