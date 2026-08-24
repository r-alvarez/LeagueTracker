# Handover — `auth/identity-model` (written 2026-08-17; merged to `main` 2026-08-18)

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

- **Merged into `main` on 2026-08-18** (`0a3ff2e` and the commits above it);
  the live site signs in through Auth0 (`c4c8ecc`). Phases 0–6 are done; the
  legacy mounts went with the `auth/legacy-mounts-removal` merge on
  2026-08-19 and `Agents__AllowUnbound` is off. Sections 2–4 remain the
  local-run and review reference; section 5 is history, kept for the order
  things had to happen in.
- Commits, in order: registry (`bd99853`), authentication (`15992b1`),
  authorization (`e0ca6c0`), accounts-by-puuid (`d2a242f`), agents (`5c6bb9b`),
  claims (`ebd42fa`), cutover material (`0a01953`), last fixes (`22e3373`).
- Settled after the merge, against the original design: recording is
  owner-based rather than role-based (`7d6cb11` — a renderer still uploads
  its own owner's games), sign-out also ends the Auth0 session (`8cf882d`),
  and the OIDC code comes back on a top-level GET with Lax cookies
  (`c4c8ecc` — form_post could not run on the http review instance).

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
| AgentRecorder (any agent of the owner, or the owner) | vods/*, matches/{id}/vod/link |
| AgentRender (agents only: renderer anywhere, recorder on own) | render/next, render/{id}/full, render/{id}/clips/{i}, complete, fail, release-stale |
| RenderRead (those agents, or the owner) | render/queue, matches/{id}/replay |

Global: `GET /api/accounts` anon · `POST /api/accounts` user + rate limit ·
`DELETE /api/accounts/{id}` owner/admin · `/api/auth/*` · `/api/me/*` (user:
agents, join-code, claims) · `/api/admin/*` (admin: users, agents, assign,
accounts/{id}/owner) · `/api/agent/enroll|ping|release*` anon ·
`/api/agent/accounts|profile|heartbeat|agents` agent key ·
`/api/render/pending` anon.

`/api/a/{region}/{slug}` is the only account mount. The Host-header `/api`,
`/api/a/{slug}` and `/api/agent/a/{region}/{slug}` mounts were removed on
2026-08-19 (`auth/legacy-mounts-removal`), once the agents on the new build
were the only ones heartbeating.

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
   `Agents__AllowUnbound=false`, and merge `auth/legacy-mounts-removal` (the
   follow-up, already prepared on top of this branch: removes the Host-header
   `/api` group, `/api/a/{slug}`, `/api/agent/a/...`, the `Hosts` bindings,
   the `CfAccess*` fields in the agent config, and points the waker at one URL).
   *Done 2026-08-19; the flag's code default is now `false` too.*
8. **Waker**: it now polls `/api/render/pending`; the stack rebuild picks it up.
9. Later, the public launch: `Auth__PublicReads=true`, site-wide Access app
   off. Nothing in-process changes.
10. **Invites** (branch `admin/invites`, 2026-08-19) — *Add a person* on
    `/admin` creates the row here, the identity at Auth0, and has Auth0 mail
    the "set your password" link. Three tenant steps, once:
    - **Applications → Create → Machine to Machine**, named e.g.
      `LeagueTracker invites`; authorise it for the *Auth0 Management API*
      with exactly `create:users read:users delete:users create:user_tickets`.
      Its client id/secret go into the stack as `AUTH0_MGMT_CLIENT_ID` /
      `AUTH0_MGMT_CLIENT_SECRET` (→ `Auth__Management__*`). Keep it separate
      from the login application: the login client never holds
      user-management scopes.
    - **Branding → Email Provider**: the built-in sender is test-only
      (`no-reply@auth0user.net`, 10/minute, and *no custom templates* - so
      the invite would arrive as Auth0's stock "reset your password" text).
      Configure a real one so invite and forgot-password mails come from our
      address. **Resend** is what this tracker uses: a native Auth0
      integration (paste an API key, no SMTP fields) and free at 3,000
      mails/month, 100/day, one custom domain. SendGrid is no longer an
      option worth listing - Twilio retired its free plan in July 2025.
      Domain: add `send.rjav-tech.co.uk` in Resend (a subdomain keeps the
      root domain's mail reputation out of it), then the MX + two TXT
      records it prints go into Cloudflare DNS with proxy **DNS only**, and
      paste only the short names (`send`, `resend._domainkey`) - Cloudflare
      appends the zone itself.
    - **Branding → Email Templates → Change Password**: this is the mail an
      invitee gets, and the same template serves forgot-password — both end
      in "set a password", so one wording covers both. Paste
      `docs/auth0-change-password-email.html` into *Message* and set *URL
      Lifetime* to 604800, the "7 days" the body promises. Mind the template
      picker: *Verification Email (using Link)* is a different mail (subject
      "Verify Your Account", link `/u/email-verification`) and the invite
      flow never sends it — testing that one against an already-verified
      address answers "This account is already verified", which is correct,
      not a fault. It is branded too (`docs/auth0-verification-email.html`)
      so the tenant has no stock Auth0 mail left in it. Both files are the
      only copy outside the dashboard. They are deliberately **light** though
      the app is dark: the dark version came out mid-grey in Outlook.com and
      in the Outlook iOS app, since each client rewrites dark mail its own
      way and `[data-ogsc]` hooks only reach Outlook.com. Don't restore the
      dark palette without testing in Outlook first.
    - **The logo in those mails** is `docs/brand/leaguetracker-mark.png`
      (the favicon rendered to PNG — email clients ignore SVG), served from
      `brand.rjav-tech.co.uk`, a Cloudflare Worker holding that one static
      asset, on a custom domain so the URL outlives wherever the file is
      hosted. Keep that host out of every Access application or the image
      becomes a login page in people's inboxes. Re-uploading `docs/brand`
      restores it if the Worker is ever lost.
    - Also: `Auth__InviteOnly=true` in the compose means a sign-in from an
      identity nobody invited or configured is refused with a page, not
      silently turned into a user. Leave *Disable Sign Ups* on as well -
      belt and braces.
    - If a mail does not arrive: *Copy link* on the person's row mints the
      same link (a Management-API password-change ticket, 7 days, single
      use) to hand over by other means. *Remove* deletes an invite that was
      never used - here and at Auth0.

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
- Done 2026-08-17 (later commits): the admin **People** card on the Data page
  (users, admin toggle, account owner by email) and the sign-in wall copy;
  README rewritten for the multi-user model (audit must-do #10). The header
  pill still only shows name/admin - fine until there is a profile page.

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
