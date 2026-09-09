# Decisions — desktop review library

## 2026-09-09 — A renderer's queue is not its owner's library

The renderer account endpoint is intentionally broad: a renderer may need to
process games for every account. That is not permission to show every account
or its gameplay in the desktop review window. Review discovery therefore has a
separate `/api/agent/review/accounts` endpoint which requires a bound owner. It
returns the enrolled owner's accounts plus explicit shared-PC grants and
nothing else; it never inherits the legacy `AllowUnbound` rollout exception.
The agent does not fall back to broad renderer discovery when talking to an
older server; an empty picker is safer than exposing another person's games.
Deploy the API before the matching agent release for that reason.

Local files narrow that boundary again. The visible account set comes only from
full Riot IDs written directly into this PC's sidecars; match-history overlap
cannot establish an identity. Once established, those accounts' histories may
associate older local files across Riot ID changes. A file left on a reassigned
PC does not become visible merely because it exists there. Ben's or Vanessa's
agent therefore cannot discover Ruben's library merely through render scope or
a match the players shared.

## 2026-09-09 — The catalogue and playable footage are different counts

The gaming PC has 193 sidecars representing 179 distinct matches but only ten
retained MP4s. Showing `On this PC 10` made the retention window look like the
entire history. The library now reports permitted games recorded here and
currently playable videos separately. It keeps the existing 500-entry catalogue
bound; retention still evaluates the complete catalogue.

Rows join sidecars to match summaries by match ID only within the accounts
already proven by local identity metadata. Match IDs survive account renames
and avoid old sidecars whose player field was populated incorrectly without
allowing a shared participant to become a local account. The join supplies
account, champion, result, KDA, opponent, queue and loadout. The storage editor
is a modal with the same identity-scoped totals, so it no longer pushes the
library down the page.

A matched Preview opens the shared website `MatchDetail`, not a reduced local
detail page. The local MP4 overrides only `/vod/status`; match detail, review,
track, gameplan, full-game status and clips keep using the account-scoped
tracker responses. Files that cannot be matched while offline retain the raw
local player so footage is never made dependent on metadata availability.

## 2026-09-09 — Footage leads when it exists

`MatchStage` already defaults an unopened game to Footage and falls back to Map
when there is no footage. Desktop match rows had defeated that rule by requiring
the recording's stale player string to equal the account's current Riot ID.
The local recording lookup now uses the stable match ID alone after the library
has passed the account boundary. This supplies the local VOD to `MatchStage`, so
Footage is the default whenever the permitted recording exists; Map remains the
fallback when it does not.

## 2026-09-09 — Render work is not a local player (follow-up)

Seen on the combined gaming/renderer PC: review defaulted to HeraArgiva and
listed Ben even though live recordings on that machine belong to Ruben's main
and alt accounts. The agent legitimately renders replay clips for other users,
but that processing scope says nothing about who played a live game there.
Rendered clips do not enter `RecordingLibrary`; its finalized sidecars are the
authoritative local-player evidence.

The account picker is therefore the distinct current Riot IDs written directly
into local sidecars, newest recording first. Server discovery remains a private
candidate set used to resolve those identities, not the visible picker. A match
ID is deliberately not identity evidence: all ten participants share it, so a
duo partner's history can overlap a local file. Match-history enrichment runs
only for accounts already proven by sidecars, processes one account at a time,
and publishes matches incrementally. A render-only account with no full-game
sidecar never appears, its history is never scanned, and the native bridge does
not authorize requests for it.

The library itself is a paged match list, not a gallery of retained video
files. Duplicate sidecars from old capture restarts collapse into one match,
preferring a copy whose footage still exists. Every attributable game gets a
row with the player, champion, opponent, result, KDA and loadout when tracker
metadata is available. A row also says `Footage` while its MP4 remains on disk
and `Map` after retention has rotated the video. Both open the same match
review; the stage chooses Footage
when local video exists and otherwise falls back to Map.

Preview routing no longer waits for the optional rich-card context. A permitted
recording's own stable match ID and resolved owner open the shared match detail
directly; only a recording without a match association uses the raw local
player.

The "review window is busy" banner was the native bridge rejecting the ninth
request in a normal match-detail burst. Eight operations remain active at once,
but a bounded total capacity of 32 now queues the ordinary backlog. Entering a
detail page also stops loading the account's list page in parallel; it reloads
when the user returns to the library.
