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

Local files follow the same boundary. A catalogue entry is shown only when its
stable match ID occurs in one of the permitted account histories, or its saved
Riot ID exactly identifies a currently permitted account for offline use. A
file left on a reassigned PC does not become visible merely because it exists
there. Ben's or Vanessa's agent therefore cannot discover Ruben's accounts or
library unless an explicit shared-PC grant is added.

## 2026-09-09 — The catalogue and playable footage are different counts

The gaming PC has 193 sidecars representing 179 distinct matches but only ten
retained MP4s. Showing `On this PC 10` made the retention window look like the
entire history. The library now reports permitted games recorded here and
currently playable videos separately. It keeps the existing 500-entry catalogue
bound; retention still evaluates the complete catalogue.

Cards join sidecars to match summaries by match ID, not Riot ID. Match IDs
survive account renames and avoid old sidecars whose player field was populated
incorrectly. The join supplies account, champion, result, KDA, opponent, queue
and loadout, while the existing generated JPG supplies the preview image. The
storage editor is a modal with the same identity-scoped totals, so it no longer
pushes the library down the page.

## 2026-09-09 — Footage leads when it exists

`MatchStage` already defaults an unopened game to Footage and falls back to Map
when there is no footage. Desktop match rows had defeated that rule by requiring
the recording's stale player string to equal the account's current Riot ID.
The local recording lookup now uses the stable match ID alone after the library
has passed the account boundary. This supplies the local VOD to `MatchStage`, so
Footage is the default whenever the permitted recording exists; Map remains the
fallback when it does not.
