# Decisions — auth/identity-model (branch of main)

Folded into `decisions/main.md` at merge. Context: audit section 1 + must-do #1
(artifact 0b8e6029). Settled with Ruben on 2026-08-17 before any code.

## 2026-08-17 — Identity, tenancy and authorization: the model

**Three concepts, three tables, one process.** A *User* is a person the app
knows (its own row, its own id); an *Account* is a Riot player (surrogate id,
puuid attribute, `GameName#TAG` a mutable alias); an *Agent key* is a machine
bound to exactly one User. They live in a small global `registry.db`
(EF Core SQLite next to the account folders), replacing `accounts.json` and
`agents.json`, which the audit already called the de-facto user table
(T-M5). Per-account SQLites are untouched.

**The app owns its users; a managed OIDC provider only authenticates them.**
Ruben's call, and the reason: this is meant to become a product, possibly
commercial, so ownership has to be ours, not a seat in someone else's identity
org. The app is a standard OIDC relying party (`AddOpenIdConnect`, code flow +
PKCE, id_token validated against the provider's JWKS) with its own session
cookie. A `User` row is ours; a `UserLogin (UserId, Issuer, Subject)` link
table holds provider identities, one user many logins, so a provider swap or
a second provider ("Login with Riot" — RSO is OIDC) is a new login linked by
verified email, never a migration. **The provider is Auth0** (managed:
passwords, social login for friends, MFA, lockout, breached-password
detection are theirs to run; ~25k MAU free), invite-only for now (public
signups disabled on the connection; the app creates its User row on first
login). Rejected — Authentik on the Portainer stack, first proposed then
withdrawn on Ruben's objection: `auth.rjav-tech.co.uk` is the SSO for the
whole homelab (TrueNAS, Portainer, Homarr), so making it the public
tracker's provider would put every stranger's login attempt, credential-
stuffing run and Authentik CVE on the homelab's door; an IdP's blast radius
is everything that trusts it. Rejected — Cloudflare Access as the identity
(every login is a Zero Trust seat, 50 free: right for friends, wrong for
strangers or customers; and its JWT would tie authorization to cookie
forwarding through bypass paths); Access-for-SaaS as a stop-gap was offered
and declined in favour of one provider from day one so nobody re-links.
Rejected — app-native passwords (nothing to gain over OIDC, a credential
store to protect). Cloudflare Access site-wide stays on as the outer wall
for now — during invite-only a stranger never reaches the login button — and
the app does not depend on it, so taking it off for public reads changes
nothing in-process. Isolation rule that goes with this: the tracker's blast
radius is its container plus `/data`; it sits on no Docker network that can
reach Authentik, Portainer or the TrueNAS API and holds no homelab secret —
only its own Auth0 client secret and the Riot key. Localhost review: a
Development-only `/api/auth/dev-login`; a real Auth0 round-trip is also
testable locally by allowing `http://localhost:5399/auth/callback` on the
Auth0 application.

**Account = surrogate id + puuid; RiotId is an alias, never a key.** Riot
encrypts puuids per API-key *holder* (their own PUUID post), which is the
likely story behind the two puuids in the corpus — so the raw puuid is a
unique, refreshable attribute, not the primary key, and a key-holder change
is a re-resolve, not a migration. URLs stay `/euw/Name-TAG` resolved through
the alias; the poller re-reads `account-v1 by-puuid`, updates the alias on a
rename, and the old slug 301s. Data folders never move: `DataDir` is
assigned once (existing folders as they are; new ones by surrogate id).

**Ownership is proved by Riot, held by us.** Riot's third-party verification
code no longer exists (gone with the summonerId/accountId sweep; not in the
API list), so the claim flow is op.gg's: the server picks a profile-icon id,
the player sets it in the client, `summoner-v4 by-puuid` confirms, the
Account's `OwnerUserId` is set. RSO becomes a second claim method when the
production key arrives. Admin can assign owners by hand (that is how the
three existing accounts get theirs on day one, from config).

**What an unowned public account exposes: Riot-derived data only.** Matches,
stats, lens, fundamentals, ranks, live game — all public. Owner-only: the
Data page, jobs (sync/import/reprocess/backfill), exports, deletes, settings
(HideLp, media sharing), agent management. **Media (VODs, clips, full-game
renders, YouTube links) is owner-only by default**, public per account when
the owner flips "share my recordings" — a stranger's footage defaults to
private. Anyone signed in may add an unowned public profile (rate-limited);
the "poll it forever" cost of that is must-do #4's problem and is noted as
the dependency it is.

**Agent keys bind to one owner; permitted accounts follow ownership.** The
brief said "one owner/account"; one *account* would break two real installs
(Ruben's PC records for both ImRA accounts; the render box serves everyone).
So: key → `OwnerUserId` + `Role` (recorder | renderer). A recorder acts on
its owner's accounts (`/api/agent/accounts` lists exactly those, with
puuids, so the "who was playing" pick is by puuid — which also ends the
duo-game VOD landing on the partner's page, M-H5). A renderer is
admin-approved and reaches every account's render endpoints and nothing
else. Binding happens at enrol through a **join code** minted by a
signed-in owner on their Data page (short-lived, single-use); the pending
record is born owned, approval stays a click that shows the machine name;
admin sees all. Agent identity (name, machine) comes from the key record —
`?agent=` and the heartbeat's self-declared name are ignored. The agent
surface shrinks to enroll/ping/release/profile/heartbeat/accounts plus, per
account: render/*, vods/*, matches/{id}/{replay,reel,vod/status,vod/link}.
Import, sync, reprocess, backfill, export, delete, account add/remove and
approve are gone from it.

**One mount, policies on endpoints, Access bypass widened to `api`.**
`MapAccountApi` is mounted once at `/api/a/{region}/{slug}`; every endpoint
carries a policy: `Read` (anonymous when `Auth:PublicReads=true`, else any
authenticated principal — the transitional setting while Access stays on),
`Owner`, `AgentRecorder`, `AgentRender`, `MediaRead`, `Admin`. Agents call
the same URLs with `X-Agent-Key`; humans carry the app session cookie.
Cloudflare's path application moves from `api/agent` to `api` (Bypass): the
API authorizes itself, the SPA shell stays behind the site-wide app. The
Host-header `/api` group, `/api/a/{slug}` and `/api/agent/a/…` mounts are
retired once every agent heartbeat reports the new build. `/api/agents/*`
becomes `/api/me/agents` (owner-scoped) and `/api/admin/agents`, ending the
`api/agent`↔`api/agents` prefix overlap (T-B7). Cookie sessions bring CSRF:
non-GET requests authenticated by cookie must carry
`X-Requested-With: LeagueTracker` (the SPA's fetch wrapper sets it); agent
requests are header-authenticated and exempt. CORS for the Vite origin is
Development-only. `AllowedHosts` is pinned.

**Migration is import-then-flag, zero downtime.** First boot on the new
build imports config + `accounts.json` + `agents.json` into `registry.db`
(files kept as `*.imported`), hoists each account's puuid out of its own
`KeyValues`, seeds owners from `Accounts__List__N__Owner=<email>` and admins
from `Auth__Admins`. Existing approved keys start unbound; `Agents:AllowUnbound`
(on for the rollout) keeps them working as today, Ruben assigns the four
owners on the Data page, the flag comes off. Old agent URLs stay alive as
the curated subset until the heartbeats show the new agent build. The Access
rule change is the last step, after `/api/auth/me` on the deployed site
proves the session works — and the site-wide Access policy, today "Ruben
only", gets the friends' emails first, or they never reach the login button.
Auth0 side (console, not code): a Regular Web Application with callback
`https://league.rjav-tech.co.uk/auth/callback`, connections as wanted
(username-password with sign-ups disabled, Google/Discord), MFA and
brute-force protection on; issuer, client id and secret go into the Portainer
stack env as `Auth__Oidc__Authority/ClientId/ClientSecret` (never in the repo,
never read by me).

**Reviewable phases, each on localhost:5399 before its commit** (dev login
needs `--environment Development` on top of the usual `--no-launch-profile
--urls` recipe): (0) registry.db + import as a read-only shadow; (1)
authentication schemes + `/api/auth/me` + header sign-in, nothing enforced;
(2) policies on the human surface, `/api/me`, `/api/admin`, SPA owner view;
(3) accounts by id + puuid, alias refresh, 301, settings, media policy;
(4) bound agent keys, join codes, agent policies, curated legacy mount,
new agent build; (5) icon-verify claims; (6) cutover — deploy, Access rule,
assign owners, agents self-update, drop the legacy mounts.

**Deliberately not in this branch:** the YouTube token (must-do #2), the
poller inversion (#4), `PublicReads=true` and the site-wide Access app coming
off, RSO, per-user quotas. Adjacent one-line fixes T-B5 (`VideoTargetPath`
via `ValidId`) and T-B6 (import path containment) sit on the same endpoints
this branch touches and are folded into phase 2 unless Ruben says otherwise.

## 2026-08-17 — Implementation notes (phases 0–5 built on the branch)

**What matches the design above, and where the build settled a detail:**

- **registry.db** at `<DataRoot>/registry.db` (EF Core SQLite, `EnsureCreated`
  + the same PRAGMA-driven column upgrades the account dbs use). `Account`
  itself is the EF entity (config still binds to it); a first boot imports
  `accounts.json`/`agents.json` and renames them `*.imported`; configuration is
  matched to its stored row by `DataDir`, then Riot ID, so a redeploy never
  duplicates an account or moves a folder. `Owner` (email) is a config-only
  property resolved to `OwnerUserId` at boot; the puuid is hoisted from each
  account's `KeyValues`. All registry timestamps get a UTC-kind converter (SQLite
  drops the kind and an hour's drift read as an expired claim in the browser).
- **Per-account state is keyed by `Account.Id`** (PerAccount, AccountInitializer,
  the poller's pass state) so a rename keeps leases and running jobs.
- **Authentication:** cookie `lt.session` (HttpOnly, SameSite=Lax, 30-day
  sliding, persistent — a session cookie died with the headless browser and
  would with a friend's window), OIDC scheme registered only when
  `Auth:Oidc` is configured, `AgentKey` scheme chosen by a policy scheme when
  the header is present. `/auth/logout` clears the app cookie only. Dev login is
  mapped only in the Development environment (`dotnet run … -- --environment
  Development` — the `--` matters, `dotnet run` eats `--environment` otherwise).
- **Policies as one enum** (`Access`: Read, User, Owner, Admin, Agent,
  AgentRecorder, AgentRender, RenderRead, MediaRead) with a single handler; the
  account API is mapped through one group per policy so the matrix is legible
  in Program.cs. `RenderRead` (render queue, replay file) was added because
  the owner and the render agents both need them and policies AND together.
- **`/api/render/pending`** (anonymous, counts only) exists for the waker,
  whose per-account `/render/queue` became owner/agent-only.
- **Redirect for a stale slug is 308**, the method-preserving permanent
  redirect (a POST to an old address must not turn into a GET).
- **Legacy mounts:** `/api/agent/a/…` is a *curated* mount (status, reel,
  vod/status, uploads, render work — nullable groups map nothing else) under
  the Agent policy; the Host-header `/api` and `/api/a/{slug}` mounts keep the
  full API under the same policies until the cutover's follow-up commit.
- **Folded fixes:** `FullGameService.VideoTargetPath` validates the id and the
  upload checks the match exists (T-B5); `/import` only accepts folders under
  the account's data folder or the `/imports` mount (T-B6).
- **Agent build:** account calls go to `/api/a/{region}/{slug}` on the key
  (same URL whether keyed or Access-token'd); `JoinCode` (config/env
  `LT_JOIN_CODE`, the setup window's field, `lt2:` one-line paste from the
  Data page) is sent at enrolment; accounts payload carries puuids. The
  Cloudflare service-token fields stay readable for one more release.
- **Verified on localhost:5399** per phase: idempotent boot/import; dev-login,
  `/api/auth/me`, agent key scheme (401/403 texts); the full authorization
  matrix by curl (anonymous / signed-in visitor / owner-admin / agent key,
  CSRF header); join-code enrolment binding a key; a real agent process
  enrolling with a renderer code, being approved, discovering accounts,
  claiming a job and uploading (mock render); settings + media policy flip;
  a simulated rename (registry edit) → config precedence log + 308; a real
  claim round-trip against Riot (icon mismatch reported with the live icon).

## 2026-08-19 — Cutover tail: one mount, unbound keys refused by default

**Decision:** merge `auth/legacy-mounts-removal` into `main` and set
`Agents__AllowUnbound` to `false` — in the compose *and* as the code default.

**Why now:** the identity merge deployed on 2026-08-18 14:20 and five agent
releases followed it the same day (`agent-2026.818.2024.12` onward), all
calling `/api/a/{region}/{slug}` on the key; the Host-header `/api`,
`/api/a/{slug}` and `/api/agent/a/…` mounts had nothing left to serve. The
merge conflicted only on `/render/{matchId}/retry`, where main had grown the
`keep` flag (`4041886`) on a group the branch made non-nullable — resolved
by keeping the flag.

**Default flipped, not just the env value:** the artifact's plan was "grace
flag until you assign them, then false". Leaving the code default at `true`
would have meant a deployment that forgot the env var lets any pre-ownership
key act on every account — the permissive setting should be the one you have
to ask for. Cost: a local review instance with an imported, unassigned
`agents.json` key gets 403 on account routes until the key is assigned on
the Data page, which is the same thing production now does.

**Rejected:** deleting the `AllowUnbound` option outright — one config value
is a cheaper rollback than a revert if a heartbeat turns out to be on an old
build after all.

**Not verified from the repo:** that every machine's heartbeat reports the
new build, and the Cloudflare Access `api` Bypass application — both are
console state; check the Data page before pushing (a push to `main` deploys).
