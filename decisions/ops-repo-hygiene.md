# Decisions — ops/repo-hygiene

Two cards from the 2026-08-25 re-audit that live in files, not code paths:
O-N2 (third parties' identities in a public repo) and O-N5 (operator docs
describing persistence that no longer exists, no restore procedure). The
GitHub-side items — O-N1 (the "Main" ruleset targets no branch), O-N3
(private vulnerability reporting is off), the licence question and whether
to purge the two network-map commits — are toggles and legal calls, not
commits; they wait for Ruben.

## 2026-08-26 — A friend's Riot ID comes from the stack environment (O-N2)

The compose asserted Ben's Riot ID and first name in clear, the local
`docker-compose.yml` repeated the Riot ID, a test fixture used it as a
lobby participant and the handoff diagram named two friends. Ruben's own
IDs stay: they are the site's example address and his to publish.

`Accounts__List__2__GameName/TagLine/DisplayName` are now `${ACCOUNT_2_*}`
like the `OWNER_*` emails. The registry re-applies configured accounts on
every boot, so an entry whose three values are unset is **skipped** with
a log line rather than refused — `registry.db` already holds the account
from the boot that had them, and the site's "Add account" is the other
way in. Half an ID (a name without a tag) still refuses to boot: that is
a typo, not an unset environment.

Deploy step: set `ACCOUNT_2_GAMENAME`, `ACCOUNT_2_TAGLINE`,
`ACCOUNT_2_DISPLAYNAME` in the Portainer stack env before merging, or the
first boot after logs the skip and keeps tracking from the registry — the
same outcome, minus the assertion.

Rejected: dropping the block from the compose entirely. It still documents
which folder is whose (`/data/ben`) and re-asserts owner and display name
if the registry were ever rebuilt.

## 2026-08-26 — One operate page, restore exercised (O-N5)

`docs/operate.md` says where each piece of state lives, what is and is not
rebuildable, how to back up (a ZFS snapshot of the dataset — SQLite in WAL
mode is not safely copied file by file while the app runs) and the three
restore cases. The account-db case was run on a scratch instance with a
copy of the alt account's `games/` and `lp-history.csv` and nothing else;
the numbers on the page are from that run.

Stale sentences fixed in place: `agent-handoff.md` no longer says
site-added accounts live in `accounts.json` (replaced by `registry.db` in
bd99853), `handover-identity-model.md` no longer tells operators to put
backups in an `/imports` mount no compose defines. `decisions/main.md`'s
"ruleset deliberately not done" is left as the record of that day; the
operate page states the truth as of today (created 2026-08-18, targets no
branch, enforces nothing).
