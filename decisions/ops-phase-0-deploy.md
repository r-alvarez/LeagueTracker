# ops/phase-0-deploy — health, UTC, log caps, backup schedule

Branch: `ops/phase-0-deploy`, from `main` at cf9442b (2026-09-09). The
deploy half of the launch board's phase 0: F2, E1, F10, N13, N15, plus the
pool-size half of N1.

## Decisions

### F2 — curl in the runtime image, `/readyz` from the compose

The `aspnet:10.0` runtime image has no curl or wget and `dotnet` cannot
issue an HTTP request on its own. Alternatives: (a) bash's `/dev/tcp`
redirection, which needs no package but cannot tell a 200 from a 503
without parsing the response by hand; (b) a second tiny probe binary
copied in from a builder stage; (c) `apt-get install curl`. Chosen (c):
the probe reads as a one-liner anyone can rerun by hand inside the
container, and the same binary serves the compose-level override. Cost: one
package to patch with the base image.

The Dockerfile's `HEALTHCHECK` hits `/healthz` (liveness) because the image
knows nothing about the deployment; the TrueNAS compose replaces it with
`/readyz` so a wedged poller or an unreachable database shows unhealthy in
Portainer. The probe sends `Host: league.rjav-tech.co.uk` because
production's `AllowedHosts` would 400 a bare loopback request — the
alternative, adding `localhost` to `AllowedHosts`, widens what the app
accepts for the sake of a probe.

No auto-restart on unhealthy: Docker only reports it. An `autoheal` sidecar
was considered and left out — the poller wedging is a bug to read the log
for, not to mask with a restart loop.

### E1 — UTC by flag, not by re-initdb

The cluster was created with `TZ=Europe/London` in the environment, and
initdb persists that as `timezone`/`log_timezone` in `postgresql.conf`.
Removing the env var alone would leave the server on London time.
Alternatives: (a) `ALTER SYSTEM SET timezone = 'UTC'` by hand on the NAS;
(b) `postgres -c timezone=UTC -c log_timezone=UTC` as the service's
command. Chosen (b): it is in the compose, survives a restore of the cluster
folder and a fresh initdb alike, and needs no step anyone can forget.
Npgsql moves `timestamptz` as UTC regardless; this is about `SHOW timezone`,
the server log and any hand-run `psql` reading the same clock as the app.

### F10 — json-file with a cap, not a log driver change

Alternatives: `local` driver (compressed, capped by default at 100 MB x 5)
or `journald`. Chosen: keep `json-file` with `max-size`/`max-file`, because
Portainer's log viewer and `docker logs` read it without a flag and the
NAS has nothing consuming journald. 50 MB for the app is a day or two of
its chattiest output; 6 MB per sidecar is months of theirs.

### N13 — the dev database stays published, on loopback only

The host-run recipe (`dotnet run` against `appsettings.json`'s
`localhost:5432`) is how the app is developed, so dropping the port
publication would break the daily loop. Binding it to 127.0.0.1 keeps that
and stops a laptop on a cafe Wi-Fi from offering a Postgres with a
well-known password to the room. The password is now
`${POSTGRES_PASSWORD:-leaguetracker}` in both places the compose uses it;
`appsettings.json` keeps the same default (it is under `src/`, which this
branch does not touch) and user-secrets is the override path for a host run.

### N15 — sleep-until-02:00 in the script, not busybox crond

`sleep $INTERVAL_SECONDS` in a loop meant "every 24 h from container
start", and the container starts on every push to main. Alternatives: (a)
busybox `crond` in the postgres-alpine image; (b) computing the seconds
until the next `BACKUP_AT` (UTC) and sleeping that. Chosen (b): crond wants
a crontab under a named user and the sidecar runs as uid 568 with no
passwd entry, and the one loop keeps the atomic rename, the retention sweep
and the custom-format dump exactly where they were. Epoch arithmetic is
used instead of `date -d` because busybox's parser is the part that
differs between builds. No dump on start: a deploy-time dump per push was
noise in the retention window, and the next 02:00 is at most a day away.
