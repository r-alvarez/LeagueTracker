# Decisions — fix/audit-quick-wins

Four "broken today" items from the 2026-08-25 re-audit that needed no design
conversation: T-N3, A-N1, A-N2, M-N3.

## 2026-08-26 — Login `returnUrl` takes the `Url.IsLocalUrl` shape (T-N3)

`SafeReturn` accepted anything starting with `/` except `//`. Browsers treat
a backslash like a slash in the path of a special scheme, so `/\evil.com`
resolved to `https://evil.com` after a genuine Auth0 sign-in — an open
redirect from the real site. Now: `/` alone, or `/` followed by neither `/`
nor `\`, and no control characters (header splitting). That is the test
MVC's `IsLocalUrl` applies; copying the three-line rule beat referencing
`UrlHelperBase` for it.

## 2026-08-26 — Champion-table KDA is the ratio of sums (A-N1)

The table printed `7.5 / 4.7 / 7.4 (4.7)` for Ahri: the ratio was a mean of
per-game ratios with zero-death games clamped to one death, so every perfect
game scored its whole K+A and dragged the mean up. Ranked sites, and the
K/D/A line beside it, mean `(ΣK+ΣA)/ΣD`. One `AggregateKda` helper feeds the
split, matchup and overall rows; a split with no deaths at all divides by
one (the alternative, a "Perfect" string, would change the response type for
a case that needs hundreds of games without dying). The per-game rows and
CSV keep their "Perfect" rule — a single game with 0 deaths is a real
outcome, an aggregate with 0 deaths is a rounding fiction.

Measured on the local corpus after the change: overall 4.07 → 2.67, Ahri
4.7 → 3.17, Viktor 3.73 → 2.43 — the numbers the audit predicted.

## 2026-08-26 — Multikill tiers are exclusive (A-N2)

Riot increments every threshold crossed: a penta arrives as
`tripleKills=1, quadraKills=1, pentaKills=1`. Summing the three read one
penta as "1 / 1 / 1" and scored it three multikills in the Fundamentals
metric. Exclusive counts (`triples − quadras`, `quadras − pentas`, `pentas`)
keep the three columns the UI already shows; `largestMultiKill` was the
other option but collapses a two-penta game to one. The `multiKills` metric
is now `tripleKills` alone, which is exactly its own label ("triples and
better"). Raw CSV columns stay raw. Local ranked totals: 24/3/0 → 21/3/0.

## 2026-08-26 — A full-game lease is sized to the game (M-N3)

All render leases were 30 minutes; a full-game render plays the replay at
speed 1 and then uploads a multi-GB file, so any game past ~28 minutes
outlived its lease and the owner's other agent claimed it again — two
renders, two uploads, and a queue reading "pending" while one was already
running. Full jobs now lease `2 × duration + 30 min`; clips keep 30 min.

Rejected: renewing the lease from the agent's heartbeat. It is the more
general fix but needs an agent release to do anything, and the failure it
guards against — an agent dying mid-job — is already covered by
`release-stale` on restart. An over-long lease only delays the retry after
a crash that never restarts.
