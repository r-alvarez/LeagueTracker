# Handing the tracker to another player

One tracker instance per player on the NAS; one **recorder** agent on each
player's gaming PC; one **renderer** agent on the dedicated replay box that
serves every tracker. Nobody but the tracker owner touches credentials.

```
 Ben's PC ──recorder──▶ league-ben ─┐
 Vanessa's PC ─recorder─▶ league-vanessa ├─▶ renderer (old PC) pulls replay jobs from all
 Ruben's PC ──recorder──▶ league / league-alt ┘      and uploads clips back
                 │
                 └──▶ YouTube (one shared channel; token lives on the NAS)
```

What a friend gets, automatically, once their agent runs: their games recorded
and published to YouTube with the link on their match page, review data
(APM, markers) overlaid on the video, and **team-fight clips of the fights they
were not in** cut from the replay by the renderer (VOD-covered matches render
fight windows only - the rule from 2026-08-04, no configuration needed).

## A. One-time, on the NAS (owner)

1. **YouTube profile.** In the Portainer stack env set `YT_CLIENT_ID`,
   `YT_CLIENT_SECRET`, `YT_REFRESH_TOKEN` (the values from your
   `appsettings.json` and `youtube-token.json` next to the agent). Every
   tracker then serves them from `GET /api/agent/profile`; agents pull them at
   start and hourly. Rotate the token here, never on a friend's machine.
   - Quota: 10,000 units/day **per Google Cloud project** = ~6 uploads/day
     across everyone sharing that project. If three players fill it, either
     request a quota increase or give each tracker its own OAuth client
     (own project → own quota) via per-service `Agent__Profile__YouTube*`
     overrides - all still authorized against the same channel.
2. **Release folder.** `mkdir /mnt/MediaPool/apps/leaguetracker/agent-releases`
   (mounted read-only into every tracker as `/agent-releases`).
3. **Publish the agent** from the dev machine:
   `deploy\publish-agent.ps1 -ReleaseDir <NAS>\apps\leaguetracker\agent-releases`
   - version = `yyyy.M.d.HHmm`, zip bundles ffmpeg; agents update themselves
     when idle (no game, no upload) within the hour or on the next heartbeat.
4. **Waker:** `PC_MAC` / `WOL_BROADCAST` in the compose now belong to the
   render box, not the gaming PC - update when the renderer moves.

## B. Per new player (owner) - "the upgrade to multi-account"

1. Copy the `leaguetracker-vanessa` block in `deploy/truenas/compose.yml`,
   fill `Riot__GameName` / `Riot__TagLine` (+ `Riot__Region`/`Riot__Platform`
   if not EUW), pick a `RecordNamePrefix`; create the `/data` folder; push
   (Portainer redeploys).
2. DNS: `league-<name>.rjav-tech.co.uk` → tunnel/NAS (split-horizon too).
3. Cloudflare Access: application for the hostname (their email allowed), and
   a **service token per agent** (Zero Trust → Access → Service Auth) so one
   friend's token can be revoked without touching the others.
4. First run: `POST /api/analytics/reprocess` isn't needed for a fresh
   tracker; history sync from the Data page pulls their past games.
5. Add the new hostname to the **renderer's** `ServerUrl` list.

## C. Per player's PC (5 minutes, no admin rights)

1. Unzip the latest `LeagueTracker.RenderAgent-<version>.zip` anywhere
   (e.g. `C:\LeagueTrackerAgent`).
2. Rename `appsettings.template.json` → `appsettings.json` and set **only**:
   ```jsonc
   "ServerUrl": "https://league-ben.rjav-tech.co.uk",
   "CfAccessClientId": "<their service token id>",
   "CfAccessClientSecret": "<their service token secret>",
   "RenderReplays": false,          // recorder-only: never drives the replay client
   "RecordingsDir": "D:\\League\\Recordings"   // optional; default = My Videos\LeagueTracker
   ```
   Everything else (YouTube, queues, title prefix) comes from the tracker.
   Leave `PostGameReview` off unless they ask for it - it takes the screen.
3. Run `LeagueTracker.RenderAgent.exe --install`. It registers itself to start
   at logon (per-user Run key), starts now, and shows a message box with
   anything missing. Look for the dot by the clock:
   - green = idle/watching, red = recording/uploading/rendering, grey with
     bars = paused, amber = waiting for the tracker, orange = last thing failed
   - right-click: **Pause/Resume** (the off switch - survives reboots),
     open tracker / recordings / log, check for updates, quit.
4. Check the tracker's **Data page → Agents**: their machine should be
   `online · recorder`, `YouTube` not flagged. Play a normal or ranked game;
   the VOD lands under their match page within minutes of the game ending
   (upload time depends on their upstream; it pauses while they play).

Uninstall: `LeagueTracker.RenderAgent.exe --uninstall` (recordings stay).

## D. The render box (owner's old PC)

Same zip. `appsettings.json`: all tracker URLs comma-separated, one service
token, `"RecordGames": false` (renderer-only), `"PostGameReview": false`,
League installed and a client logged in (Vanguard only allows replays through
the client; any account works), and `IdleSeconds` can drop to ~10 since
nobody uses it. `--install` as above. Renders run whenever no game process
is up on that box; the gaming PCs are never used for rendering again.

## Operational notes

- **Pause** stops new recordings/renders/reviews; an upload in flight
  finishes (it's invisible and stopping it only loses work).
- **Updates** never overwrite `appsettings.json` or `youtube-token.json`;
  the previous build stays as `*.prev`. A failed update is logged, reported
  in the heartbeat's `lastError`, and retried at the next published version.
- **Profile precedence:** local `appsettings.json` > `LT_*` env > tracker
  profile > built-in default. A friend can override anything locally; the
  tracker fills the rest.
- **Renderer conflict:** two agents that both render would fight over the
  replay client on the same PC - never set `RenderReplays` on two agents on
  one machine. Different machines are fine (leases are per tracker).
- **Same game, two players:** each player's tracker holds the match with its
  own `IsMe`; both recorders publish their own VOD to their own match page;
  the renderer may cut the same fight twice (once per tracker) - harmless.
- Old trackers without the `/api/agent/*` endpoints just get a working
  agent with no profile/heartbeat/updates - every call is best-effort.
