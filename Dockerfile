# --- SPA build ---------------------------------------------------------------
FROM node:24-alpine AS web
WORKDIR /web
COPY src/leaguetracker-web/package*.json ./
# npm ci refuses Windows-generated lockfiles that lack the linux/wasm optional
# deps; npm install stays lockfile-driven but tolerates them.
RUN npm install --no-audit --no-fund
COPY src/leaguetracker-web/ ./
# vite.config points outDir at the API project for local dev; in the image the
# SPA lands in dist and is overlaid onto the runtime layer below.
RUN npm run build -- --outDir dist --emptyOutDir

# --- API build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/LeagueTracker.Api/LeagueTracker.Api.csproj LeagueTracker.Api/
RUN dotnet restore LeagueTracker.Api
COPY src/LeagueTracker.Api/ LeagueTracker.Api/
RUN dotnet publish LeagueTracker.Api -c Release -o /app/publish --no-restore

# --- Runtime ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# The aspnet image ships no curl/wget and `dotnet` cannot make an HTTP call
# by itself, so the probe below needs a client; curl is the one every
# compose healthcheck override and hand check can reuse.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
# chown beats whatever restrictive mode the build context arrived with (the
# deploy clone's umask leaks into publish output via preserved source perms).
COPY --chown=568:568 --from=api /app/publish .
COPY --chown=568:568 --from=web /web/dist ./wwwroot
# What GET /api/version reports as builtUtc - the only way to tell which build
# is live, since .git is not in the build context and the site sits behind
# Access. After both COPYs on purpose: Docker re-runs this exactly when either
# stage produced something new, so an all-cached rebuild keeps the old stamp,
# which is correct - the image is the same one.
RUN date -u +%Y-%m-%dT%H:%M:%SZ > /app/build-info.txt && chown 568:568 /app/build-info.txt
# Unprefixed `Urls` because appsettings.json carries a localhost value for host
# runs, and app config (JSON) outranks ASPNETCORE_URLS; plain env vars outrank both.
ENV Urls=http://+:5170 \
    Riot__DataDir=/data \
    Riot__ApiKeyFile=/data/riot-api-key.txt
EXPOSE 5170
# Liveness only: the process answers. Readiness (/readyz: database reachable,
# poller passing) belongs to the compose that knows the deployment.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5170/healthz || exit 1
ENTRYPOINT ["dotnet", "LeagueTracker.Api.dll"]
