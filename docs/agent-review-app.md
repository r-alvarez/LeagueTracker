# Agent gameplay review

Implementation on `feature/agent-review-app`, branched from `main` at
`cf9442b9b83d94cbabfed69e2e4ae9bfeb9b80c1`. Reviewed 2026-09-09.

## Decision and scope

The existing C# agent and React application can support gameplay review in a
desktop window. This implementation uses WinForms, WebView2 and the existing
match-review components. It requires no new programming language, repository,
Electron application or Rust toolchain. The website keeps its existing features.

The tray and Start Menu open an independent `--review` process. Opening it does
not restart the recorder, enrol a machine or wait for a reachable tracker.
Closing the window disposes WebView2 and the local media server. A game process,
or an imminent-game LCU phase, closes the window; Lobby and EndOfGame allow review.
The existing automatic League-client replay feature remains a separate setting.

The app provides:

- A local recording library, immediate playback, seeking, playback speed and
  saved playback position. Local input telemetry supplies APM without the server.
- Account selection, match history, the shared match detail, scoreboard,
  timeline, review moments, map, gameplan and available footage.
- Local-video preference, authenticated streaming of server videos and clips,
  saved analysis when disconnected, and an explicit link to the website.
- Pinning, deletion confirmation, configurable game-count and disk budgets,
  free-space protection, and a tray notification when a recording is ready.

New Riot-derived analysis still comes from the tracker. Opening a local video
does not need its analysis to be ready, but previously unseen metrics are not
computed offline. Previously fetched analysis and artwork are cached. A missing
cached resource stays unavailable until the tracker is reachable. This is a
self-contained review interface, not a replacement for the backend.

YouTube-only footage opens externally. Remote YouTube JavaScript is not loaded
into the document that has native capabilities. Existing website embeds remain.
The app watches recorded video and existing rendered replays; it does not decode
`.rofl` files itself or provide clip editing and cloud sharing.

## Re-review of the revised feasibility note

The revised `docs/agent-review-app-feasibility.md` in the
`explore+agent-review-app` worktree is substantially more accurate than its first
version. Its corrections to virtual-host interception, loopback video transport,
embedded assets, startup ordering, upload assumptions, retention, grants and
unmeasured performance estimates are sound.

Remaining qualifications:

1. `HttpListener` does belong to the BCL, but that does not make it the best
   per-user desktop server. It uses HTTP.sys on Windows; URL reservations and
   choosing/reserving an available port need care. Microsoft discourages it for
   new development. This implementation uses Kestrel bound directly to IPv4
   loopback port zero. It adds the ASP.NET Core runtime to the self-contained
   publish, so the footprint is more than the frontend archive alone.
   [Microsoft HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=net-10.0)
2. Closing review whenever gameflow leaves `None` would close it in the lobby
   and post-game screens. The implemented blocking phases are ChampSelect,
   GameStart, InProgress and Reconnect, with a game-process fallback.
3. Caching match detail alone is insufficient: the shared UI also requests
   track, review, gameplan, footage status and clips, and uses external artwork.
   The bridge covers those GET routes; artwork has its own constrained cache.
4. The reported 250 KB gzip size describes the website's main JavaScript chunk,
   not its complete distribution or the installed WebView2 runtime. Measurements
   below distinguish the entry chunk, complete frontend and native executable.
5. The 80–150 MB review-process estimate is not supported by our smoke tests.
   Treat the note's performance table as a hypothesis, not a product promise.
6. The `.uploaded` marker can mean sidecars alone were delivered. It is not proof
   that an MP4 has a backup. New `.review-published` markers require a complete
   MP4 upload or confirmed YouTube processing and linking.
   Legacy `.uploaded` files are not promoted using today's upload setting;
   their historical contents cannot prove that video was sent. The delivery
   sweep now probes `vod/status?includeApm=false` for these recordings. Only a
   hosted MP4 with identical byte length, match ID, video filename and recording
   start/end timestamps establishes a backup. The local files are held against
   writes/deletion during confirmation. Sidecars alone, unavailable trackers,
   mismatched recordings and older servers without `sizeBytes` remain protected
   when video delivery is enabled. No video download or re-upload is needed.
7. Reuse the site's sharing rules, but keep rendering and review discovery
   separate. `/api/agent/accounts` can remain broad for a renderer's work queue;
   `/api/agent/review/accounts` requires a bound owner and applies only owner
   access plus explicit shared-PC grants. It never inherits the legacy
   `AllowUnbound` exception. The desktop bridge only reads accounts returned by
   that personal endpoint and deliberately fails closed against an older
   server. This does not change the site's intentionally broader authenticated
   Read policy.
8. Evergreen runtime installation is part of agent setup. The build verifies
   Microsoft's signature on the small bootstrapper and embeds it in the agent.
   Setup.exe runs it silently when the runtime is missing; ZIP setup and the
   first review launch after an update use the same installation helper.
   Existing installations are reused. Failure leaves recording available and
   offers retry inside the app rather than requiring a separate browser install.
   [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

The overall recommendation remains WebView2 with the existing languages. Rust
and Tauri would still use WebView2 on Windows; changing the shell language does
not remove browser/video decoder costs. A native-only review UI is possible,
but would require maintaining another implementation of the charts and review
experience. The evidence so far supports measuring this shared implementation
before accepting that ongoing cost.

No paid UI framework or new commercial service is required. WebView2's SDK
licence is included in the agent notices, and the frontend build generates its
dependency notices. Existing distribution obligations still apply, including
FFmpeg and the WebView2 Runtime/bootstrapper terms. This is not a claim that every
development/deployment tool has unrestricted use under every licence.

## Boundaries and storage

The embedded static UI is mapped to `https://app.leaguetracker.invalid`.
JSON requests use a correlated message bridge with bounded concurrency,
allowlisted operations, validated account IDs and paths, and credentials kept
in C#. Navigation, popups, host objects, downloads and unsolicited permissions
are disabled. CSP excludes remote scripts and frames.

Only media and allowlisted Riot artwork use the on-demand loopback server.
It validates Host, Origin, method and a random session capability. Video routes
accept recording IDs or validated account/resource pairs, not filesystem paths
or arbitrary destination URLs. Local MP4s stream with HEAD/206/416 range support;
remote media forwards authentication natively. Video is not buffered into a
single in-memory response. Tokens and keys are not logged.

Sidecars remain the recording catalogue. Pins, manual deletion and automatic
retention share a named mutex. A persistent read handle protects the selected
local video even while paused between range requests. Finalizing recordings,
pins and pending video deliveries survive cleanup. For local-only recording,
old unpinned games rotate within the limits. Keep-all disables all automatic
eviction, including eviction under low disk space. Recording admission checks
the full free-space floor on both recording and scratch drives and pauses new
recordings below it. This deliberately changes the previous half-floor admission
and the G-N2 rule that still evicted published games under disk pressure with
keep-all enabled. At a 6 GB floor, 5 GB available now refuses the next recording.
Sidecars survive deletion. Unknown MP4s are left alone.

Recording-ready tray notifications are opt-in (`NotifyRecordingReady`, off by
default), controlled in Agent settings under This machine. They open review only
when clicked. The existing League-client replay automation remains a separate
opt-in setting.

Library preferences are saved in `metadata/library-settings.json`; explicit
preferences take precedence over profile defaults. The default is 20 games
with the existing recording budget and keep-all setting. The library shows the
latest 500 catalogue entries, counts retained sidecars separately from playable
MP4s, and enriches permitted recordings from account match history by stable
match ID. This preserves account association across Riot ID changes and lets
the cards show thumbnails, champion, result, KDA, opponent and loadout. Very
large keep-all archives need local pagination in a subsequent iteration. A
matched card opens the same `MatchDetail` used by the website and substitutes
only its local `/vod/status` response; scoreboard, verdict, timeline, track,
gameplan and clips still come through the account-scoped tracker APIs. An
unmatched or offline-only file retains the simple local player. Retention
examines the whole catalogue.

Analysis caches are partitioned by installation and agent-key identity, keyed
by schema and URL, capped at 128 MiB per identity. Artwork is separately capped
at 128 MiB per installation. Successful JSON responses can be reused for five
minutes; Refresh forces discovery and history revalidation. Network/server
failures can return saved responses, while live authorization denials take
precedence. Artwork manifests refresh daily. The browser HTTP cache is requested
at 32 MiB; this is not a hard cap on its entire profile or GPU resources.
Rotated identities/installations can leave old cache directories on disk.

The recording loops' existing server-validation startup dependency remains.
Offline review is independent; this branch does not promise that a newly
started recorder captures games when its tracker has never become reachable.
Free space is checked at admission and cleanup, not continuously while encoding.

## Build and test

```powershell
./deploy/build-review-ui.ps1
./deploy/get-webview-bootstrapper.ps1
dotnet build src/LeagueTracker.RenderAgent -c Release
dotnet test tests/LeagueTracker.RenderAgent.Tests
dotnet test tests/LeagueTracker.Api.Tests
cd src/leaguetracker-web
npm run lint
npm run build
```

`deploy/publish-agent.ps1` builds the desktop UI first and requires its embedded
archive and verified runtime bootstrapper. Inno Setup includes the native WebView2 loader and generated notices;
the published files remain flat for the existing updater. Agent CI builds both
parts, and frontend changes now participate in the agent release decision.

`--ensure-webview2` prepares the runtime without entering recorder startup or
enrolment. Setup.exe invokes it before the final setup page; the ZIP setup form
also prepares review when saving. The review window shows preparation progress
and performs the same check for older installations receiving a ZIP update.
No runtime installer is started when the current user already has access to an
Evergreen installation. Concurrent setup attempts serialize through a local
file lock and recheck availability after acquiring it. Closing review cancels
its wait, but does not terminate Microsoft's shared installer.

The bootstrapper requires an internet connection only when the runtime is
missing. Fully offline first-time installation would require bundling the much
larger standalone runtime installer. The shared Evergreen runtime keeps itself
updated through Microsoft's updater and is not removed when the agent is
uninstalled. No browser is launched to complete normal agent setup.

Launch the built agent with `--review` (optionally `--last`). Review uses that
build's config, key and recording folder. No release has been distributed.

The Windows-only, opt-in smoke harness is outside the solution's normal test
run. With WebView2 installed and League closed:

```powershell
./deploy/test-review-ui.ps1 -FfmpegPath 'C:\path\to\ffmpeg.exe'
```

It generates a synthetic 20-second 720p30 H.264 MP4 in its own `bin/fixture`,
opens the real review form offscreen with no server, checks playback and a seek,
captures screenshots, and checks that the WebView process tree exits. It does
not run the recorder. Logs and images are under
`tests/LeagueTracker.ReviewSmoke/bin/fixture`.

## Observations, not customer benchmarks

Validation performed on this development PC with WebView2 152.0.4191.66:

| Check | Result |
|---|---|
| Agent tests | 120 passed |
| API tests, including PostgreSQL-backed discovery/grant tests | 255 passed |
| Desktop and website production frontend builds | Passed |
| Frontend lint | Passed; existing React warnings |
| Self-contained agent publish | Passed; embedded UI and flat native loader verified |
| Missing-asset publish guards | Both missing UI and missing bootstrapper correctly fail publishing |
| Runtime setup failure/retry/cancellation | Covered with simulated installers; clean-machine installation still needs a VM check |
| Published `--ensure-webview2` with an installed runtime | Passed without entering recorder startup |
| Local playback, metadata and seek in WebView2 | Passed |
| WebView processes remaining after closing | 0 in all three smoke runs |
| First development smoke run | Library 1,502 ms from harness timer; seek 118 ms |
| Release-mode smoke run | Library 2,068 ms from process creation; seek 781 ms |
| Runtime-setup follow-up smoke run | Library 6,562 ms from process creation (1,697 ms from harness timer); seek 103 ms |
| Sum of host + WebView working sets during playback | 464 MiB, 468.2 MiB and 475.6 MiB |
| Desktop entry JavaScript | About 251 KB / 80 KB gzip; charts and review load on demand |
| Embedded frontend ZIP, including fonts/notices | 447,938 bytes after rebasing the dependency updates |
| Self-contained agent executable, before bundling FFmpeg | About 147.1 MB, including the 1.78 MB runtime bootstrapper |

The main-branch executable was about 116.6 MB. The approximately 30.5 MB
increase includes the ASP.NET Core runtime for Kestrel, WebView2 integration,
the embedded UI and bootstrapper. The shared WebView2 Runtime, if missing,
requires an additional installation; its size is not included in that figure.

The first timer excluded process startup and part of native initialization.
The second captures process-creation-to-library readiness. Neither is a repeated
benchmark, and other build/test work was active during the release-mode run.
Working-set sums can count shared pages more than once and do not include GPU
memory. These numbers must not be advertised as cold-start, private-memory,
seek-percentile or in-game FPS guarantees.

Before broad release, measure repeated cold/warm starts and seek percentiles
with real full-length 1440p60 recordings, large catalogues and full telemetry;
profile CPU, private memory and GPU allocations on a lower-spec PC; verify
automatic teardown while entering a real game; and exercise installer/update
flows and authenticated online review end to end. Inno installer execution,
live-game FPS impact and a live server-backed desktop session were not tested
here. Passing this branch's functional tests establishes feasibility, not
completion of those rollout checks.
