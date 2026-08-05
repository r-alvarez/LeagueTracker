# LeagueTracker — Project Instructions

## Git Commits & PRs
- NEVER add Co-Authored-By trailers to git commits.
- NEVER add "Generated with Claude Code" or any AI attribution to PR descriptions.
- Commit per logical unit during a task, not only at the end. Commit body explains WHY; subject is the what.

## Comments & Documentation
- No XML doc comments (`/// <summary>`) unless on public API contracts consumed by external teams.
- Never comment what code does — only comment WHY: non-obvious decisions, workarounds, business rules, or gotchas.
- If the code reads clearly, add nothing.
- Method names describe intent, variables name the concept not the type.
- Code should read top-to-bottom as a narrative — prefer named intermediates over complex one-liners.

## Code Style
- Prefer `is` / `is not` for null and shape checks over `==` / `!=`.
- `is { Count: > 0 }` for non-empty collection checks, not `.Any()` or `.Count > 0`.
- Negative patterns (`is not { IsActive: true }`) for early-return guards; positive patterns only when you use the matched result.
- Return `[]` for empty collections, not `Array.Empty<T>()` or `new List<T>()`.
- Expression-bodied members (`=>`) for one-line methods and properties, not block bodies.
- Remove unused `using` directives and keep the rest sorted when editing a file.
- Inline single-use locals; don't assign to a variable used only once (IDE0059).
- Don't wrap a value in `() =>` unless you need deferred evaluation or closure capture — pass the value directly.
