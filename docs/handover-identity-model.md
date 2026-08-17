# Handover — `auth/identity-model` (written 2026-08-17, from the work machine)

Everything you need to pick this up from another machine: what exists, how to
run it, what to check, and the exact cutover steps. The design rationale is in
`decisions/auth-identity-model.md` (read that first if anything here looks
odd); the design + plan artifact is
https://claude.ai/code/artifact/26776fbf-c2c6-401e-acbe-bd98deb13d7a and the
audit it answers is https://claude.ai/code/artifact/0b8e6029-1c5f-4e8f-8e83-da8d180e473d
(section 1, must-do #1).

Note for a Claude session at home: the work machine's Claude memory (local
run recipe, review habits, this project's decisions) does not travel; this
file plus `decisions/` carries what it held. Paste it in or point Claude at it.

## 1. State of the branch

- Branch `auth/identity-model`, 10 commits on top of `main` (`76b634e`),
  pushed to origin. **Not merged. Do not merge into `main` until the cutover
  prerequisites below are in place** — a push to `main` deploys.
- Phases 0–5 of the plan are implemented and verified on a local instance;
  phase 6 is the deploy-time cutover (section 5 here).
- Commits, in order: registry (`bd99853`), authentication (`15992b1`),
  authorization (`e0ca6c0`), accounts-by-puuid (`d2a242f`), agents (`5c6bb9b`),
  claims (`ebd42fa`), cutover material (`0a01953`), last fixes (`22e3373`).

What it does, in one paragraph: the app owns its users (Auth0 only
authenticates), a user owns the Riot accounts they proved are theirs (profile-
icon claim, or assigned by an admin), an agent key belongs to one user with a
role (recorder on the owner's accounts, renderer on everyone's render work),
and every endpoint carries a policy — anonymous reads are 401 until
`Auth:PublicReads=true`, owners mutate, agents upload/render, admins see all.
`registry.db` (next to the account folders) replaces `accounts.json` and
`agents.json`.

## 2. Getting going at home

```
git fetch origin && git checkout auth/identity-model
cd src/leaguetracker-web && npm install && npm run build      # outputs into ../LeagueTracker.Api/wwwroot
cd ../LeagueTracker.Api && dotnet build
dotnet run --no-build --no-launch-profile -- --urls http://localhost:5399 --environment Development
```

- The `--` matters: `dotnet run` otherwise eats `--environment`. Development
  enables `/api/auth/dev-login` and the Vite CORS origin; nothing else differs.
- Data: `appsettings.json` points at `../../data` (repo-root `data/`,
  gitignored) with `riot-api-key.txt` inside it. At home you need that folder
  with a key file (first line = the key) — the claim flow and the alias check
  call Riot; everything else works keyless on imported games. First boot
  creates `data/registry.db` (delete it to re-run the import; `agents.json`
  becomes `agents.json.imported` after import).
- Sign in locally: `http://localhost:5399/api/auth/dev-login?email=you@example.com&name=Ruben&admin=true&returnUrl=/`
  (cookie session, persistent). A second browser profile with another email
  and no `admin` shows the visitor view. Sign out: `/auth/logout`.
- Real Auth0 round-trip locally (optional): `dotnet user-secrets set
  Auth:Oidc:Authority https://<tenant>.<region>.auth0.com/`, `…ClientId`,
  `…ClientSecret` (UserSecretsId is in the csproj), allow
  `http://localhost:5399/auth/callback` on the Auth0 app, then `/auth/login`.
- The 5399 exe locks `bin/Debug`; stop it before `dotnet build`.

## 3. Review checklist (what to click on 5399)

Signed in as admin (dev-login with `admin=true`):
- Header shows "Ruben · admin · Sign out"; every page renders as before.
- Data & sync: owner view — sync/import/reprocess, **What visitors see**
  (share recordings / hide LP), **Machines** (Add a machine… mints a join code
  and an `lt2:` paste; pending machines with Approve/Reject; approved ones with
  Restart/Revoke and, for admin, an owner-email box), clip rendering, storage,
  exports.
- Match page: keep/delete/re-render/replay links visible.

Signed in as a non-admin email (visitor):
- Data & sync shows only the public figures and **Is this your Riot account?**
  (Claim → starter icon named → Verify; with your real account and the client
  you can complete it: set the icon, press Verify, the page reloads owned).
- Match page: no delete/render/replay controls; no VOD card unless the owner
  turned "Share my recordings" on.
- Anonymous window: sign-in wall.

By curl (replace `$A=http://localhost:5399/api/a/euw/ImRA-87166`):
- `GET $A/status` anonymous → 401; with a dev-login cookie → 200 (visitor gets
  `apiKeyConfigured:null`, `job:null`, `agents:[]`).
- `POST $A/analytics/reprocess` with the cookie but without
  `X-Requested-With: LeagueTracker` → 403 (CSRF guard); with it, as owner/admin
  → 202.
- Agent: `POST /api/agent/enroll {"key":"<32+ chars>","name":"x","machine":"y","code":"<join code>"}`
  → pending & bound; approve on the Data page; then `X-Agent-Key: <key>` on
  `POST $A/render/next` → 200/204, on `GET $A/export/matches.csv` → 403.
- `GET /api/render/pending` anonymous → `{"pending":N}` (the waker's endpoint).

## 4. Route → policy map (the whole authorization surface)

| Policy | Routes under `/api/a/{region}/{slug}` |
|---|---|
| Read (anon once PublicReads, else any signed-in principal) | status, live, matches, matches/facets, matches/{id}, series, lens, challenges/percentiles, fundamentals, review, reel, reviews, stats, stoploss, lp/*, analytics/summary |
| MediaRead (owner, or public if MediaPublic, or an agent that could have produced it) | matches/{id}/clips, clips/{i}, vod, vod/status, vod/thumb, fullgame, fullgame/status |
| Owner (owner or admin) | sync/history, import, analytics/reprocess, ranks/backfill, jobs/status, storage, export/*, deletes of clip/vod/fullgame, fullgame request/keep, render retry/dismiss, PUT settings |
| AgentRecorder (recorder on its owner's account, or the owner) | vods/*, matches/{id}/vod/link |
| AgentRender (agents only: renderer anywhere, recorder on own) | render/next, render/{id}/full, render/{id}/clips/{i}, complete, fail, release-stale |
| RenderRead (those agents, or the owner) | render/queue, matches/{id}/replay |

Global: `GET /api/accounts` anon · `POST /api/accounts` user + rate limit ·
`DELETE /api/accounts/{id}` owner/admin · `/api/auth/*` · `/api/me/*` (user:
agents, join-code, claims) · `/api/admin/*` (admin: users, agents, assign,
accounts/{id}/owner) · `/api/agent/enroll|ping|release*` anon ·
`/api/agent/accounts|profile|heartbeat|agents` agent key ·
`/api/render/pending` anon.

Legacy mounts still alive on purpose: Host-header `/api` and `/api/a/{slug}`
(full API, same policies) and `/api/agent/a/{region}/{slug}` (agent subset
only). They go in the follow-up commit after the agents update (step 5.7).

## 5. Cutover (do in this order; also `docs/agent-handoff.md` §E)

1. **Auth0**: tenant → Applications → Regular Web Application. Allowed
   callback URLs `https://league.rjav-tech.co.uk/auth/callback`,
   `http://localhost:5399/auth/callback`; allowed logout URLs
   `https://league.rjav-tech.co.uk/`. Connections: Username-Password with
   *Disable Sign Ups* on (invite-only), plus Google/Discord if wanted. Enable
   MFA policy and brute-force protection. Create your user and the friends'
   (same emails you will use below). Note the issuer
   `https://<tenant>.<region>.auth0.com/` (trailing slash), client id, secret.
2. **Portainer stack env** (never in the repo): `AUTH0_ISSUER`,
   `AUTH0_CLIENT_ID`, `AUTH0_CLIENT_SECRET`, `AUTH_ADMINS=<your email>`,
   `OWNER_RUBEN=<your email>`, `OWNER_BEN=<Ben's email>`. The compose maps them
   to `Auth__Oidc__*`, `Auth__Admins`, `Accounts__List__N__Owner`; it also sets
   `Auth__PublicReads=false`, `Agents__AllowUnbound=true`, `AllowedHosts`.
3. **Cloudflare Access, site-wide app**: add the friends' emails to the policy
   (today it is you only) — otherwise they never reach the login button.
4. **Merge and deploy** (`git merge auth/identity-model` into `main`, push).
   First boot logs: accounts registered/imported, puuids hoisted, agent keys
   imported (files kept as `*.imported` under `/data`).
5. **Verify live**: sign in via Auth0 on the site; `GET /api/auth/me` shows
   you as admin with `ownedAccountIds` = your two accounts. Ben signs in once
   → his user joins the config-seeded row by email.
6. **Cloudflare Access, path app**: add Self-hosted app domain
   `league.rjav-tech.co.uk` path `api`, policy Bypass / Everyone; delete the
   old `api/agent` one. Until this is done, agents on the new build (which
   call `/api/a/...`) are blocked by Access — the old build on
   `/api/agent/a/...` keeps working, so order matters only for the agent
   rollout, not for the site.
7. **Agents**: Data page → Machines: the four imported keys show as
   unassigned; assign each (recorders → the player's email, render box → you,
   role renderer). Publish the agent build (`deploy\publish-agent.ps1`);
   watch `/api/me/agents` heartbeats report the new version. Then set
   `Agents__AllowUnbound=false`, and make the follow-up commit that removes
   the Host-header `/api` group, `/api/a/{slug}`, `/api/agent/a/...`, the
   `Hosts` bindings and the `CfAccess*` fields in the agent config.
8. **Waker**: it now polls `/api/render/pending`; the stack rebuild picks it up.
9. Later, the public launch: `Auth__PublicReads=true`, site-wide Access app
   off. Nothing in-process changes.

## 6. Open items and follow-ups

- Must-do #2 (YouTube refresh token in `/api/agent/profile`) is untouched —
  still behind the Agent policy, still the shared channel token.
- Must-do #4 (poller inversion): unowned public profiles get polled like
  everyone else; `POST /api/accounts` is rate-limited to 5/hour per user as
  the stopgap.
- RSO: a second OIDC provider registered directly in the app when the
  production key + RSO arrive; the `UserLogin` table is ready for it.
- `/import` only accepts folders under the account's data folder or the
  `/imports` mount now (audit T-B6) — put backups there.
- The stale-slug redirect is 308 (method-preserving), not 301.
- Puuids are encrypted per API-key holder: if the key holder ever changes,
  set `Accounts__List__N__Puuid` or let the poller re-resolve; folders and ids
  don't move.
- SPA follow-ups worth a small pass: an admin page for users (`/api/admin/users`
  exists, no UI), the header pill could link to `/api/me`, and the login wall
  copy once Auth0 is real.
- README still describes the old single-user model; rewrite before the Riot
  application (audit must-do #10).

## 7. Files that matter

- `src/LeagueTracker.Api/Registry/*` — entities, DbContext (+UTC converter),
  UserStore, ClaimService, RegistryBootstrap.
- `src/LeagueTracker.Api/Auth/*` — AuthSetup (schemes, CSRF guard),
  AgentKeyAuthentication, Caller, Policies (the `Access` enum + handler),
  AuthEndpoints, ManagementEndpoints (`/api/me`, `/api/admin`).
- `src/LeagueTracker.Api/Accounts/AccountRegistry.cs` (registry-backed,
  config sync, rename), `PerAccount.cs` (binding middleware over route values).
- `src/LeagueTracker.Api/Program.cs` — `MapAccountApi(api, agentOnly)` with one
  group per policy; global routes.
- `src/leaguetracker-web/src/auth.ts`, `api.ts` (`apiFetch`), `account.ts`
  (global roots, previous slugs), `components/Machines.tsx`,
  `components/ClaimAccount.tsx`, `pages/DataPage.tsx`.
- `src/LeagueTracker.RenderAgent/TrackerClient.cs`, `AgentConfig.cs`
  (`JoinCode`), `SetupForm.cs` (`lt2:` paste).
- `deploy/truenas/compose.yml`, `deploy/truenas/waker/waker.py`,
  `docs/agent-handoff.md`.
