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

**The app owns its users; an OIDC provider only authenticates them.** Ruben's
call, and the reason: this is meant to become a product, possibly commercial,
so ownership has to be ours, not a seat in someone else's identity org.
The app is a standard OIDC relying party (`AddOpenIdConnect`, code flow +
PKCE, id_token validated against the provider's JWKS) with its own session
cookie; users are keyed by `(issuer, subject)` with verified email as the
recovery link. **The provider is Authentik on the Portainer stack** —
unlimited users, self-signup and social sources when wanted, "Login with
Riot" (RSO is OIDC) addable as a second provider later. Invite-only for now
(Authentik enrollment closed; the app auto-creates its User row on first
login). Rejected: Cloudflare Access as the identity (every login is a Zero
Trust seat — 50 free — fine for friends, wrong for strangers or customers;
and its JWT would tie authorization to cookie forwarding through bypass
paths). Rejected: app-native passwords (nothing to gain over OIDC, a credential
store to protect). Trade-off accepted: Authentik's exposure and backups are
Ruben's; a rebuilt Authentik changes every `sub`, so email is the fallback
link. Cloudflare Access site-wide stays on as the outer wall for now — the
app does not depend on it, so taking it off for public reads changes nothing
in-process. Localhost review: a Development-only `/api/auth/dev-login`.

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
proves the session works.

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
