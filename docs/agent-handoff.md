# Handing the tracker to another player

One website - `league.rjav-tech.co.uk/{region}/{RiotId}/...` (op.gg style:
`/euw/ImRA-87166/matches`, `/euw/TheCosmicPeach-TTV/data`) - one process hosting
every tracked account with its own data folder; one **recorder** agent on
each player's gaming PC; one **renderer** agent on the dedicated replay box
that serves every account. Nobody but the tracker owner touches credentials.

```
 Ben's PC ──recorder──┐                                         ┌─ /ImRA-87166
 Vanessa's PC ─recorder┼─▶ league.rjav-tech.co.uk (one process) ├─ /ImRA-5957
 Ruben's PC ──recorder─┘              ▲                         ├─ /TheCosmicPeach-TTV
                                      │                         └─ /...
                      renderer (old PC) pulls replay jobs for every account
                                      │
                                      └──▶ YouTube (one shared channel; token on the NAS)
```

An agent is given ONE URL; it asks the server for its accounts and treats
each as a tracker (`/api/a/{region}/{RiotId}`), so a recording lands on the account
that was playing (the live client's Riot ID decides - a duo game exists on
both players' pages, each PC's VOD goes to its own player) and the renderer
pulls jobs from all of them. The old per-account hostnames still resolve to
the same process and mean "that account", so nothing from the
three-container era breaks.

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
   (`/data/agent-releases` in the container; the tracker also mirrors GitHub releases into it).
3. **Publish the agent** from the dev machine:
   `deploy\publish-agent.ps1 -ReleaseDir <NAS>\apps\leaguetracker\agent-releases`
   - version = `yyyy.M.d.HHmm`, zip bundles ffmpeg; agents update themselves
     when idle (no game, no upload) within the hour or on the next heartbeat.
4. **Waker:** `PC_MAC` / `WOL_BROADCAST` in the compose now belong to the
   render box, not the gaming PC - update when the renderer moves.

## B. Per new player (owner) - "the upgrade to multi-account"

1. On the site, open the account switcher → **＋ Add account…**, type their
   Riot ID (`GameName#TAG`) and pick the region, **Track**. The server checks
   it with Riot (canonical casing, puuid), creates
   `/mnt/MediaPool/apps/leaguetracker/<GameName>-<TAG>/`, its database, and
   adds it to the poller's round; it lands you on their page
   (`league.rjav-tech.co.uk/euw/<GameName>-<TAG>/`). Runtime-added accounts
   are remembered in `/data/accounts.json` (survive redeploys); the three
   original ones stay in the compose (`Accounts__List__N`) - both sources
   are fine, config wins on a duplicate. `DELETE /api/accounts/{slug}`
   untracks a runtime-added one (folder kept).
2. Cloudflare Access: add their email to the `league.rjav-tech.co.uk`
   application (they see every account's pages - fine among friends;
   per-account visibility by email is a later step), and create a **service
   token per agent** (Zero Trust → Access → Service Auth) so one friend's
   token can be revoked without touching the others.
3. First run: history sync from their Data page pulls their past games.

## C. Per player's PC (5 minutes, no admin rights)

Owner, once per machine: Cloudflare Zero Trust → Access → Service Auth →
create a token (`agent-<name>`), allow it on the site's Access application;
then on the site, **Data & sync → Get the agent → Make a join code…**, paste
the token, pick the role (Recorder for a player's PC) and their title
prefix, copy the `lt1:…` code and send it to them privately (it holds the
token; the server never sees it - it is built in your browser).

Player:
1. **Download agent** from the same card (or the zip from the GitHub
   release), unzip anywhere (e.g. `C:\LeagueTrackerAgent`).
2. Double-click `LeagueTracker.RenderAgent.exe` → the setup window opens →
   paste the join code (everything fills in) → **Test connection** (should
   list the accounts and "YouTube ready") → **Save**. Run-at-logon is
   registered, the agent starts, a message box says if anything is missing.
   (Later: tray **Settings…** or `--setup`; `--install` forces the window.)
3. Look for the tracker's bolt by the clock; the small dot is the state:
   - green = idle/watching, red = recording/uploading/rendering, grey with
     bars = paused, amber = waiting for the tracker, orange = last thing failed
   - right-click: **Pause/Resume** (the off switch - survives reboots),
     open tracker / recordings / log, Settings…, check for updates, quit.
4. On the site, their page → **Data & sync → Agents** shows their machine
   `online · recorder`. Play a normal or ranked game; the VOD lands under
   their match page within minutes of the game ending (upload time depends
   on their upstream; it pauses while they play).

Uninstall: `LeagueTracker.RenderAgent.exe --uninstall` (recordings stay).

## D. The render box (owner's old PC)

Same zip, same setup window: the one URL (every account is discovered), one service
token, "This machine is: Renderer" (RecordGames off), `PostGameReview` off,
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
