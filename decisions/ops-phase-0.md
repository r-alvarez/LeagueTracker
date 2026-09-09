# ops/phase-0 - "stop the bleeding" (launch board b00751d4, phase 0)

Branch from main cf9442b, 2026-09-09. The whole phase taken at once on
Ruben's call; the Traefik health label (part of F2) skipped because Traefik
is not in front of this deployment yet. Deploy-side decisions are in
`ops-phase-0-deploy.md`.

## N1 - keep Npgsql, stop keying pools by schema
- Decision: one `NpgsqlDataSource` (one pool) for the process; the account
  schema goes into `search_path` on every connection open through an EF
  `DbConnectionInterceptor`, not into the connection string.
- Alternatives: another driver - none exists (Npgsql is the only maintained
  PostgreSQL ADO.NET driver and the EF provider sits on it; the wall was
  `Search Path=` per schema making every schema its own pool, never the
  driver). `HasDefaultSchema` + `IModelCacheKeyFactory` per schema - one
  compiled EF model per account and the schema baked into migration SQL;
  rejected. PgBouncer transaction pooling - `search_path` is session state,
  so it needs `SET LOCAL` in every transaction; extra infra for no gain once
  the pool is shared; rejected for now.
- Gotcha relied on: Npgsql issues DISCARD ALL when a connection returns to
  the pool, so a checkout starts on the role's default path and a context
  that skipped the interceptor finds no tables - fails closed, never
  cross-tenant. A test pins that two schemas share one physical connection.
- Trade-off: one extra round trip (`SET search_path`) per checkout. The
  compose caps the pool at 80 of max_connections=100 for pg_dump and psql.

## N5 - ceilings before lifecycle
- Global cap + per-user total + adder-can-untrack, nothing more: idle
  demotion and expiry are A2 (phase 2). A user's total charges what they own
  plus what they added and nobody claimed - once claimed it is the
  claimant's slot. The adder may untrack only while unclaimed, so an adder
  cannot pull a profile from under its owner. Needed a registry column
  (`AddedByUserId`, migration `AccountAddedBy`); the tenancy work was asked
  to avoid a second migration so the snapshot merged clean.

## B2a - secrets only to the operator's machines
- Rather than stripping *Secret*/*Token* for everyone (the audit's
  stop-gap, which turns YouTube off for the household too), the profile
  keeps them for keys bound to an admin. Everyone else gets the profile with
  YouTubeUpload=false so their agent records without failing uploads. The
  refresh-token rotation is Ruben's manual step.

## A6 - resolve endpoint now, search UI later
- Trimming `/api/accounts` to the caller's own accounts broke client-side
  slug resolution, so `/api/accounts/resolve` had to land in the same
  change. `OwnerUserId` is serialised for admins only (Admin and Machines
  pages need it); `mine` replaces it for everyone else. The front page lists
  own accounts and opens any other by name - the search box of N6, minus
  suggestions.

## D7 - output cache instead of hand-rolled caches
- ASP.NET output caching, 30 s, on the seven heaviest reads. It caches
  anonymous 200s only, so a signed-in owner never sees a stale read and a
  PublicReads flip stops serving within 30 s. The global limiter exempts
  agent-keyed requests because an upload is hundreds of chunk PUTs.

## D1 - bound while streaming
- Content-Length is the client's word, so every upload is copied through a
  bounded copy and the partial file discarded past the cap; the chunked
  `.part` is capped as an assembled file (offset + body), which was the
  open-ended path. Media allowance excludes `games/` (rebuildable from Riot).

## B4 - profile allow-list (agent side)
- Allowed: PollSeconds, CaptureFramerate, MaxWindowsPerJob, RecordFramerate,
  RecordQuality, RecordMaxHeight, CaptureBackend, HdrToneMap,
  RecordNamePrefix, UploadVods, UploadVodSidecars, UploadInGameMbps,
  YouTubeUpload, YouTubeClientId, YouTubeClientSecret, YouTubeRefreshToken,
  PostGameReviewDelaySec, PostGameReviewAutoAdvance, PostGameReviewWaitMin.
- Excluded on purpose: paths (FfmpegPath is arbitrary exe execution),
  identity, role, capture toggles, deletion budgets, YouTubeVisibility,
  PostGameReview/AutoLaunchClient (screen + synthesised input), IdleSeconds.
- Rejected: a deny-list - a new property would default to server-settable.
- Cost: the compose's KeepRecordingsAfterPublish and PostGameReview
  overrides are ignored; those machines set them locally.

## B9 - setup rules
- https required except localhost/127.0.0.1/[::1]; an `lt2:` paste over a
  different address asks with the parsed host (default No); Test only
  pings; Save writes, enrols, closes. Runtime still loads an http file - the
  window enforces it, not the agent.

## D2 - single winner without a schema change
- Conditional `UPDATE ... WHERE OwnerUserId IS NULL` inside the claim's
  transaction plus a `FOR UPDATE` on the account row for the one-live-
  challenge rule; the in-memory registry is updated only after the database
  says the write won. A cached icon mismatch (60 s cache) does not burn an
  attempt. Out of scope: larger proofs, release, displacement.

## F2 - readiness is "pool opens + a poll pass in the last 10 min"
- Liveness and readiness are anonymous (a probe has no cookie). The
  compose probe sends a Host header because production AllowedHosts would
  400 a bare loopback request.
