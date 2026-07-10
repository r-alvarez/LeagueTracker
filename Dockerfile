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
WORKDIR /app
# chown beats whatever restrictive mode the build context arrived with (the
# deploy clone's umask leaks into publish output via preserved source perms).
COPY --chown=568:568 --from=api /app/publish .
COPY --chown=568:568 --from=web /web/dist ./wwwroot
# Unprefixed `Urls` because appsettings.json carries a localhost value for host
# runs, and app config (JSON) outranks ASPNETCORE_URLS; plain env vars outrank both.
ENV Urls=http://+:5170 \
    Riot__DataDir=/data \
    Riot__ApiKeyFile=/data/riot-api-key.txt
EXPOSE 5170
ENTRYPOINT ["dotnet", "LeagueTracker.Api.dll"]
