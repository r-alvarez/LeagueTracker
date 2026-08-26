# Decisions — api/enrol-hardening

Three cards from the 2026-08-25 re-audit on the one anonymous write the
internet can reach: M-H6 (enrolment lock-out), T-N10 (forged client
address), T-N1 (per-machine credentials selected by a self-chosen name).

## 2026-08-26 — A join code is required to enrol (M-H6)

`POST /api/agent/enroll` created a pending record for anyone: twenty
codeless posts (seven addresses, or one address with a forged
`CF-Connecting-IP`) filled the global cap and every real machine got 429
until the owner deleted them one by one. Reproduced live on 2026-08-25.

Now a first announcement without a valid join code is refused outright
(403 with a message) unless `Agents:AllowUnbound` is on — the record could
never have been approved by an owner anyway, only assigned by an admin.
The pending caps (20 global, 3 per address) guard only that legacy door,
count only unbound records, and unbound pendings whose machine has not
re-announced for a day are dropped first. A code-bound enrolment is never
capped: the code is the owner's hand. Re-announcements of a known key
(every 20 s while a machine waits for approval) touch nothing.

Twenty *first* announcements per address per hour, counted in the store
rather than by the rate-limiter middleware: the middleware cannot tell a
new record from a waiting machine's re-announce, and the latter runs 180
times an hour. Guessing an 8-character join code from a 32-letter alphabet
inside its 15-minute life is infeasible regardless; the budget is hygiene.

The agent shows a 403 as "no enrolment answer" today — it treats every
non-2xx as unreachable. Surfacing the message is an agent change and rides
with the agent branch.

## 2026-08-26 — `CF-Connecting-IP` is believed only from Cloudflare (T-N10)

The header overrode the peer address for any caller. After the
forwarded-headers pass the peer is the edge that spoke to Traefik, so the
override now applies only when that peer is inside Cloudflare's published
ranges (`Proxy:ClientIpHeaderFrom`, defaulting to the 2026-08-26 lists).
A header from anywhere else is ignored and said once in the log, because
the failure mode is the one D-H6 fixed: if Traefik ever stops forwarding
the edge address, every enrolment shares Traefik's and the per-address cap
locks friends out after three. `Proxy:ClientIpHeaderFrom: ["0.0.0.0/0",
"::/0"]` restores the old trust-everyone behaviour if it is ever needed.

The ranges are a static list, not fetched: Cloudflare changes them rarely,
a fetch at boot is one more thing to fail, and the config override exists.

## 2026-08-26 — Per-agent overrides are keyed by key id (T-N1)

`Agent__Profiles__<name>__*` selected overrides by the name the machine
typed at enrol. Any signed-in user can mint a recorder code, enrol a key
named after another machine (the compose file named it; renderer names are
listed to every user), approve it themselves and receive that machine's
YouTube client secret and refresh token. Reproduced live.

Overrides are now keyed by the key record's id — the 12-hex surrogate an
admin sees beside each machine on the Machines page. It is not guessable
and not chosen by the enrolling side. Blocks that match no key are named
in the boot log, so a forgotten substitution is loud rather than a quota
surprise.

Rejected: rejecting name collisions at enrol. It closes the reproduced
case but not the general one — a machine that has never enrolled under
the name in the compose is still claimable — and two friends may
legitimately both own a "DESKTOP". Rejected: keying by owner user id.
Same opacity, and it cannot give one of an owner's two machines its own
Google project.

This is a mitigation. The audit's must-do #2 (T-B4: stop shipping channel
credentials to agents at all — server-side upload or per-player OAuth) is
a design conversation and its own branch.

**Deploy step.** The two override blocks in `deploy/truenas/compose.yml`
carry `<ruben-key-id>` / `<ben-key-id>` placeholders: read the ids from
the Machines page (as admin) or `GET /api/admin/agents` and substitute
them in the merge commit. Until then Ruben's agent falls back to
`KeepRecordingsAfterPublish=false` (published recordings are pruned) and
Ben's uploads use the shared Google project; the boot log names both.
