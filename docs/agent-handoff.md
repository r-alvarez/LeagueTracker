# Handing the tracker to another player

One website - `league.rjav-tech.co.uk/{region}/{RiotId}/...` (op.gg style:
`/euw/ImRA-87166/matches`, `/euw/TheCosmicPeach-TTV/data`) - one process hosting
every tracked account with its own data folder; one **recorder** agent on
each player's gaming PC; one **renderer** agent on the dedicated replay box
that serves every account. Nobody but the tracker owner touches credentials.

Identity, in one paragraph: people sign in through Auth0 (the app keeps its
own users; Auth0 only vouches), a person **owns** the Riot accounts they
proved are theirs (profile-icon check, or assigned by the admin), and each
agent **belongs to one person** - a recorder acts on its owner's accounts, the
renderer (admin-approved) on everyone's render work. The API authorizes
itself on every endpoint; Cloudflare Access stays in front of the site as
the outer wall for now (`Auth__PublicReads=false`), and is bypassed for
`/api` because the API needs no help.

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
pulls jobs from all of them. The old per-account hostnames still land on the
site (Traefik and AllowedHosts keep them), but the account is always in the
path now - a hostname means nothing on its own.

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
     across everyone sharing that project (`videos.insert` costs 1,600). Two
     ways out, in order: give a busy player's agent its own project via
     per-agent overrides - `Agent__Profiles__<key id>__YouTubeClientId /
     ClientSecret / RefreshToken` on the tracker (the id an admin sees beside
     the machine on the Machines page - never the name), blank = shared values; the
     refresh token is minted for the *same channel* with the new client
     (`--youtube-auth` with `LT_YOUTUBE_CLIENT_ID/SECRET` set) - and, for a
     product, Google's YouTube API quota-extension audit.
2. **Release folder.** `mkdir /mnt/MediaPool/apps/leaguetracker/agent-releases`
   (`/data/agent-releases` in the container; the tracker also mirrors GitHub releases into it).
3. **Publish the agent** from the dev machine:
   `deploy\publish-agent.ps1 -ReleaseDir <NAS>\apps\leaguetracker\agent-releases`
   - version = `yyyy.M.d.HHmm`, zip bundles ffmpeg; agents update themselves
     when idle (no game, no upload) within the hour or on the next heartbeat.
4. **Waker:** `PC_MAC` / `WOL_BROADCAST` / `UNIFI_URL` are Portainer stack
   env vars (with `UNIFI_USER`/`UNIFI_PASS`), never in the compose - they
   describe the render box's network; update them when the renderer moves.
5. **Cloudflare Access.** Zero Trust → Access → Applications → Add →
   Self-hosted: domain `league.rjav-tech.co.uk`, **path `api`**, one policy
   with action **Bypass**, include Everyone (replaces the older `api/agent`
   application - delete that one). Path applications win over the site-wide
   one, so the whole API reaches the tracker unauthenticated and the tracker
   authorizes every call itself: humans by their session cookie, agents by
   their key. The site-wide application keeps the SPA shell behind Access;
   its policy must list every friend's email or they never reach the login.
6. **Auth0 (once).** A Regular Web Application: allowed callback
   `https://league.rjav-tech.co.uk/auth/callback` (add
   `http://localhost:5399/auth/callback` for local review), allowed logout
   `https://league.rjav-tech.co.uk/`; connections as you like (Username-Password
   with *Disable Sign Ups* on while invite-only, Google/Discord); MFA and
   brute-force protection on. Put the issuer (`https://<tenant>.<region>.auth0.com/`),
   client id and secret in the Portainer stack env as `AUTH0_ISSUER`,
   `AUTH0_CLIENT_ID`, `AUTH0_CLIENT_SECRET`; `AUTH_ADMINS` = your email;
   `OWNER_RUBEN` / `OWNER_BEN` = the owners' emails (the compose maps them to
   `Accounts__List__N__Owner`). Users are created from those emails at boot
   and join on first login, so ownership is in place before anyone signs in.
   Friends are created or invited from the Auth0 dashboard.


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
2. Cloudflare Access: add their email to the site-wide `league.rjav-tech.co.uk`
   application (that is the outer wall - without it they never reach the
   login button). Auth0: create/invite their user with the same email.
3. Ownership: either they claim the account themselves (Data page →
   "Is this your Riot account?" → set the named profile icon → Verify), or you
   assign it: `POST /api/admin/accounts/{id}/owner {"ownerEmail": "..."}`
   (or `Accounts__List__N__Owner` in the compose for configured accounts).
4. First run: history sync from their Data page pulls their past games -
   theirs to run now, not yours.

## C. Per player's PC (5 minutes, no admin rights, nothing to hand out)

1. **Download installer** from the site (Data & sync → Get the agent) or the
   GitHub release (`LeagueTracker.Agent-Setup-<version>.exe`; the zip is the
   portable alternative - unzip anywhere and double-click the exe). Run it:
   per-user install under `%LocalAppData%\Programs\LeagueTracker Agent`, no
   admin, Start Menu + Settings › Apps entries; Windows SmartScreen may warn
   once (unsigned).
2. **The owner** opens their Data page → Machines → **Add a machine…** and
   sends the friend the join code (eight letters like `K7Q2-9DFM`, or the
   one-line `lt2:` paste that also carries the site address). It lives 15
   minutes and works once.
3. The installer ends in the setup window (later: Start → LeagueTracker Agent,
   or the tray's Settings…): paste the code (or the `lt2:` line), tracker
   URL `https://league.rjav-tech.co.uk`, role Recorder, optional recordings
   folder and title prefix. **No token.** **Test connection** enrols the
   machine - it is the owner's from that moment - and says "waiting for
   approval"; **Save**.
4. Owner: Data & sync → Machines → **Waiting for approval** → **Approve**.
   The agent notices within 20 s and starts. Revoke there cuts it off
   instantly; the machine keeps its key (`agent.key` next to the exe) and can
   be re-approved. A machine enrolled without a code shows as unassigned;
   only an admin can approve it (and assign its owner).
5. Tray: the tracker's bolt by the clock; the small dot is the state:
   - green = idle/watching, red = recording/uploading/rendering, grey with
     bars = paused, amber = waiting (tracker unreachable, or not yet
     approved), orange = last thing failed
   - right-click: **Pause/Resume** (the off switch - survives reboots),
     open tracker / recordings / log, Settings…, check for updates, quit.
6. Their page → Data & sync → Machines shows the machine `online · recorder`.
   Play a normal or ranked game; the VOD lands under their match page within
   minutes of the game ending (upload time depends on their upstream; it
   pauses while they play).

Uninstall: `LeagueTracker.RenderAgent.exe --uninstall` (recordings stay).

## D. The render box (owner's old PC)

Same zip, same setup window: the one URL (every account is discovered), a
**renderer** join code (only an admin can mint one - the renderer reaches
every account's render work), "This machine is: Renderer" (RecordGames off; the post-game review only runs on recording machines),
League installed and a client logged in (Vanguard only allows replays through
the client; any account works), and `IdleSeconds` can drop to ~10 since
nobody uses it. `--install` as above. Renders run whenever no game process
is up on that box; the gaming PCs are never used for rendering again.

## Operational notes

- **Uploads run during games, paced.** Delivery is its own loop in the
  agent; a running game caps the upload at half the line's measured idle
  upstream (`UploadInGameMbps` to pin it). Game 1 should be on YouTube by
  the end of game 2; if a player says otherwise, ask for their **Log** from
  the Agent access row (the "sendlog" command ships it within a minute).
- **Recordings are deleted once safe** (YouTube processed + linked), per
  the tracker profile: shared `KeepRecordingsAfterPublish=false`, Ruben's key
  overridden to `true`. A new agent that should keep files gets its own
  `Agent__Profiles__<key id>__KeepRecordingsAfterPublish: "true"` line.

- **Pause** stops new recordings/renders/reviews; an upload in flight
  finishes (it's invisible and stopping it only loses work).
- **Updates** never overwrite `appsettings.json` or `youtube-token.json`;
  the previous build stays as `*.prev`. A failed update is logged, reported
  in the heartbeat's `lastError`, and retried at the next published version.
- **HDR desktops record correctly through WGC** (the agent's own
  ScreenRecorderLib build tone-maps scRGB to SDR). If a player's card shows
  the "HDR is on ... fell back to Desktop Duplication" error, WGC failed to
  start on their PC and that game recorded washed out - the fix is whatever
  stopped WGC (agent.log), not their HDR setting.
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

## E. Cutover to the identity model (done 2026-08-18/19; kept for the order)

1. Merge `auth/identity-model`; Portainer redeploys with the new env
   (`AUTH0_*`, `AUTH_ADMINS`, `OWNER_*`, `Agents__AllowUnbound=true`). First
   boot imports `accounts.json`/`agents.json` into `/data/registry.db` (the
   files stay as `*.imported`) and hoists each account's puuid.
2. Sign in on the live site through Auth0; `GET /api/auth/me` shows you as
   admin with your two accounts. Add the friends' emails to the site-wide
   Access policy.
3. Cloudflare: add the `api` Bypass application, delete `api/agent`.
4. Data page → Machines: assign the four existing keys to their owners
   (recorders → the player, the render box → you as renderer). Publish the new
   agent build; watch the heartbeats report it (`/api/me/agents`).
5. Flip `Agents__AllowUnbound` to `false`. Then merge
   `auth/legacy-mounts-removal` (prepared on top of this branch): it removes
   the Host-header `/api` group, `/api/a/{slug}` and `/api/agent/a/...`
   mounts, the `Hosts` bindings, the agent's Cloudflare service-token fields,
   and points the waker at one URL. (Done 2026-08-19.)
6. Public launch, later: `Auth__PublicReads=true` and the site-wide Access
   application off - nothing in-process changes.
