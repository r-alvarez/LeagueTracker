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
5. **Agent enrolment (once).** Cloudflare Zero Trust → Access → Applications
   → Add → Self-hosted: domain `league.rjav-tech.co.uk`, **path `api/agent`**,
   one policy with action **Bypass**, include Everyone. Path applications win
   over the site-wide one, so `/api/agent/*` reaches the tracker
   unauthenticated - and the tracker itself demands an approved agent key
   there (only enrol, ping and the release feed are open). Everything human
   stays behind the site-wide Access application exactly as before.

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

## C. Per player's PC (5 minutes, no admin rights, nothing to hand out)

1. **Download installer** from the site (Data & sync → Get the agent) or the
   GitHub release (`LeagueTracker.Agent-Setup-<version>.exe`; the zip is the
   portable alternative - unzip anywhere and double-click the exe). Run it:
   per-user install under `%LocalAppData%\Programs\LeagueTracker Agent`, no
   admin, Start Menu + Settings › Apps entries; Windows SmartScreen may warn
   once (unsigned).
2. The installer ends in the setup window (later: Start → LeagueTracker Agent,
   or the tray's Settings…): tracker
   URL `https://league.rjav-tech.co.uk`, role Recorder, optional recordings
   folder and title prefix. **No token.** **Test connection** enrols the
   machine and says "waiting for approval"; **Save**. (A join code from the
   Data page pre-fills the fields; a Cloudflare service token is only for
   machines that must skip approval.)
3. Owner: the site's Data & sync → **Agent access** lists the machine as
   pending → **Approve**. The agent notices within 20 s and starts. Revoke
   there cuts it off instantly; the machine keeps its key (`agent.key` next
   to the exe) and can be re-approved.
4. Tray: the tracker's bolt by the clock; the small dot is the state:
   - green = idle/watching, red = recording/uploading/rendering, grey with
     bars = paused, amber = waiting (tracker unreachable, or not yet
     approved), orange = last thing failed
   - right-click: **Pause/Resume** (the off switch - survives reboots),
     open tracker / recordings / log, Settings…, check for updates, quit.
5. Their page → Data & sync → Agents shows the machine `online · recorder`.
   Play a normal or ranked game; the VOD lands under their match page within
   minutes of the game ending (upload time depends on their upstream; it
   pauses while they play).

Uninstall: `LeagueTracker.RenderAgent.exe --uninstall` (recordings stay).

## D. The render box (owner's old PC)

Same zip, same setup window: the one URL (every account is discovered), no
token (approve it on the site), "This machine is: Renderer" (RecordGames off), `PostGameReview` off,
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
- **HDR off while playing.** An HDR desktop (Windows HDR, Auto HDR, RTX
  HDR) records bleached - the 8-bit capture clips it. The agent flags it
  as the heartbeat's `lastError` ("HDR is on for the display...") so the
  owner sees it on the Data page; the player fixes it with Win+Alt+B.
- **Profile precedence:** local `appsettings.json` > `LT_*` env > tracker
  profile > built-in default. A friend can override anything locally; the
  tracker fills the rest.
- **Renderer conflict:** two agents that both render would fight over the
  replay client on the same PC - never set `RenderReplays` on two agents on
  one machine. Different machines are fine (leases are per tracker). Same
  rule for two *installs*: don't run Setup.exe on a PC that already has the
  portable agent (Ruben's `E:\LeagueTrackerAgent`) - one folder per machine.
- **Same game, two players:** each player's tracker holds the match with its
  own `IsMe`; both recorders publish their own VOD to their own match page;
  the renderer may cut the same fight twice (once per tracker) - harmless.
- Old trackers without the `/api/agent/*` endpoints just get a working
  agent with no profile/heartbeat/updates - every call is best-effort.
