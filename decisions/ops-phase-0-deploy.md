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
