# Security policy

## Reporting a vulnerability

Please report security issues privately through GitHub's
[private vulnerability reporting](https://github.com/r-alvarez/LeagueTracker/security/advisories/new)
for this repository — not in a public issue, discussion, or pull request.

Include what you found, where (tracker API, web app, Windows agent, deploy
scripts), how to reproduce it, and what you think the impact is. You'll get an
acknowledgement within a few days and a fix or a reasoned response as soon as
practical; credit in the release notes if you'd like it.

## Scope

- The tracker (`src/LeagueTracker.Api`, `src/leaguetracker-web`) and its
  container image (`ghcr.io/r-alvarez/leaguetracker`).
- The Windows agent and its installer (`src/LeagueTracker.RenderAgent`,
  `src/LeagueTracker.ReplayLauncher`, GitHub Releases tagged `agent-*`).
- The deployment scripts and workflows in `deploy/` and `.github/`.

Only the latest commit on `main` and the latest `agent-*` release are
supported. Please don't test against a live deployment you don't own; the
tracker runs on the owner's own hardware.

## Verifying what you downloaded

Every agent release carries `SHA256SUMS.txt` and a Sigstore build-provenance
attestation: `gh attestation verify <file> --repo r-alvarez/LeagueTracker`.
The container image is attested the same way and scanned with Trivy on every
publish (Security tab).
