using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// The always-on replacement for the PowerShell watcher: polls the match list,
/// ingests every new game (match + timeline + at-game-time ranks) and attributes
/// the exact LP change once Riot's win/loss counter confirms a single new game.
public sealed class MatchPollerService(
    AccountScopes scopes,
    AccountRegistry accounts,
    AccountInitializer initializer,
    IOptions<RiotOptions> options,
    PerAccount<LiveGameState> liveStates,
    ILogger<MatchPollerService> logger) : BackgroundService
{
    /// After a live game ends, poll fast until its match shows up - Riot takes a
    /// minute or two to publish - then give up and fall back to the normal cadence.
    private static readonly TimeSpan FastCaptureWindow = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan FastCaptureDelay = TimeSpan.FromSeconds(15);
    /// While a game is live the banner is the product: re-read spectator every
    /// 30s so the start time lands (it reads 0 for the first minutes), and the
    /// end is noticed within half a minute rather than a whole poll interval.
    private static readonly TimeSpan LiveGameDelay = TimeSpan.FromSeconds(30);

    // The loop looks up at least this often even when nothing is due: an
    // account added from the admin page has no due time yet and should not
    // wait out a whole idle interval before its first pass.
    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(1);

    private readonly RiotOptions _options = options.Value;
    // Per-account pass state: first pass, the retention and alias clocks, and
    // when each account is next due.
    private readonly HashSet<string> _passed = [];
    private readonly Dictionary<string, DateTime> _lastRetentionSweepUtc = [];
    private readonly Dictionary<string, DateTime> _lastAliasCheckUtc = [];
    private readonly Dictionary<string, DateTime> _nextDueUtc = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the web host finish starting before the first poll.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            // Every due account, in turn - one Riot key, one limiter, so
            // sequential is also the polite order. The cadence is per account:
            // only the one in a game polls every 30 s. With one shared delay,
            // one friend's ranked game had every tracked account polled at
            // that rate, which saturates the dev key at 11 accounts (audit R-N1).
            foreach (var account in accounts.All)
            {
                if (_nextDueUtc.GetValueOrDefault(account.Id) > DateTime.UtcNow) continue;
                var live = liveStates.For(account);
                // A db that could not be opened is retried by the initializer on
                // its own schedule; polling it would only fail on the first query.
                if (!initializer.EnsureReady(account))
                {
                    Reschedule(account, live);
                    continue;
                }
                try
                {
                    await RunPassAsync(account, live, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (RiotApiKeyMissingException)
                {
                    logger.LogWarning("No Riot API key configured yet - poller idle. Set Riot:ApiKey, RIOT_API_KEY, or Riot:ApiKeyFile.");
                    RescheduleAll();
                    break;
                }
                catch (RiotApiException ex) when (ex.IsAuthFailure)
                {
                    logger.LogError("Riot API key rejected ({Status}). Refresh the key (personal keys: https://developer.riotgames.com).", ex.StatusCode);
                    RescheduleAll();
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Poll pass failed for {Account}", account.Slug);
                }
                Reschedule(account, live);
            }

            await Task.Delay(SleepUntil(accounts.All.Select(a => _nextDueUtc.GetValueOrDefault(a.Id)), DateTime.UtcNow), ct);
        }
    }

    private void Reschedule(Account account, LiveGameState live) =>
        _nextDueUtc[account.Id] = DateTime.UtcNow + Cadence(live, _options.PollSeconds);

    // One key serves every account: when it is missing or rejected there is
    // nothing to ask for anyone until the idle cadence comes round.
    private void RescheduleAll()
    {
        foreach (var account in accounts.All) _nextDueUtc[account.Id] = DateTime.UtcNow + IdleDelay(_options.PollSeconds);
    }

    internal static TimeSpan Cadence(LiveGameState live, int pollSeconds) =>
        live.FastCapturePending ? FastCaptureDelay
        : live.Current is not null ? LiveGameDelay
        : IdleDelay(pollSeconds);

    private static TimeSpan IdleDelay(int pollSeconds) => TimeSpan.FromSeconds(Math.Max(30, pollSeconds));

    internal static TimeSpan SleepUntil(IEnumerable<DateTime> dueUtc, DateTime nowUtc)
    {
        var wait = dueUtc.DefaultIfEmpty(nowUtc + MaxSleep).Min() - nowUtc;
        return wait < MinSleep ? MinSleep : wait > MaxSleep ? MaxSleep : wait;
    }

    private async Task RefreshAliasAsync(Account account, string puuid, RiotApiClient riot, CancellationToken ct)
    {
        AccountDto current;
        try
        {
            current = await riot.GetAccountByPuuidAsync(puuid, ct);
        }
        catch (RiotApiException ex) when (!ex.IsAuthFailure)
        {
            logger.LogWarning("Alias check for {RiotId} answered {Status} - keeping the current name", account.RiotId, ex.StatusCode);
            return;
        }
        if (current.GameName is not { Length: > 0 } || current.TagLine is not { Length: > 0 }) return;
        if (current.GameName == account.GameName && current.TagLine == account.TagLine) return;
        var taken = accounts.BySlug($"{current.GameName}-{current.TagLine}");
        if (taken is not null && taken.Id != account.Id)
        {
            logger.LogWarning("{RiotId} is now {New} on Riot's side, but that address belongs to another tracked account - not renaming", account.RiotId, $"{current.GameName}#{current.TagLine}");
            return;
        }
        accounts.Rename(account, current.GameName, current.TagLine);
    }

    private async Task CheckLiveGameAsync(LiveGameState live, string puuid, RiotApiClient riot, RankLookupService ranks, CancellationToken ct)
    {
        string? activeRaw;
        try
        {
            activeRaw = await riot.GetActiveGameRawAsync(puuid, ct);
        }
        catch (RiotApiException ex) when (!ex.IsAuthFailure)
        {
            // Spectator hiccups must never stall match capture.
            logger.LogWarning("Spectator check failed ({Status}); skipping this pass", ex.StatusCode);
            return;
        }

        if (activeRaw is not null)
        {
            var snapshot = LiveGameSnapshot.Parse(activeRaw, puuid);
            var isNew = live.Current?.GameId != snapshot.GameId;
            if (isNew) snapshot = await WithLobbyRanksAsync(snapshot, ranks, ct);
            live.SetLive(snapshot);
            if (isNew) logger.LogInformation("Live game detected: {MatchId} (queue {QueueId})", snapshot.MatchId, snapshot.QueueId);
        }
        else if (live.EndLiveIfAny(FastCaptureWindow) is { } endedMatchId)
        {
            logger.LogInformation("Live game {MatchId} ended - fast-capture mode until it is ingested", endedMatchId);
        }
    }

    /// Team-average rank values for the live lobby, same math as ingest uses
    /// for finished matches. Ranks are garnish - a failed lookup never blocks
    /// the live status.
    private async Task<LiveGameSnapshot> WithLobbyRanksAsync(LiveGameSnapshot snapshot, RankLookupService ranks, CancellationToken ct)
    {
        var ally = new List<double>();
        var enemy = new List<double>();
        foreach (var participant in snapshot.Participants)
        {
            if (participant.Puuid is not { Length: > 0 } puuid) continue;
            try
            {
                var entries = await ranks.GetEntriesAsync(puuid, TimeSpan.FromHours(1), ct);
                if (RankMath.SelectEntryForQueue(entries, snapshot.QueueId) is not { Tier.Length: > 0 } entry) continue;
                if (RankMath.ToValue(entry.Tier, entry.Rank, entry.LeaguePoints) is not { } value) continue;
                (participant.TeamId == snapshot.MyTeamId ? ally : enemy).Add(value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Rank lookup for a live participant failed: {Message}", ex.Message);
            }
        }
        return snapshot with
        {
            AvgAllyRankValue = ally is { Count: > 0 } ? ally.Average() : null,
            AvgEnemyRankValue = enemy is { Count: > 0 } ? enemy.Average() : null,
        };
    }

    private async Task RunPassAsync(Account account, LiveGameState live, CancellationToken ct)
    {
        using var scope = scopes.Create(account);
        var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
        var riot = scope.ServiceProvider.GetRequiredService<RiotApiClient>();
        var ingest = scope.ServiceProvider.GetRequiredService<MatchIngestService>();
        var lp = scope.ServiceProvider.GetRequiredService<LpService>();
        var player = scope.ServiceProvider.GetRequiredService<TrackedPlayerService>();

        var puuid = await player.GetPuuidAsync(ct);

        // Riot IDs change; the puuid does not. Once a day ask what this player
        // is called now, so the address follows the rename and the old one 301s.
        if (DateTime.UtcNow - _lastAliasCheckUtc.GetValueOrDefault(account.Id) > TimeSpan.FromHours(24))
        {
            _lastAliasCheckUtc[account.Id] = DateTime.UtcNow;
            await RefreshAliasAsync(account, puuid, riot, ct);
        }

        await CheckLiveGameAsync(live, puuid, riot, scope.ServiceProvider.GetRequiredService<RankLookupService>(), ct);

        // Full-game renders are big; expire unkept ones on a slow cadence.
        if (DateTime.UtcNow - _lastRetentionSweepUtc.GetValueOrDefault(account.Id) > TimeSpan.FromHours(6))
        {
            _lastRetentionSweepUtc[account.Id] = DateTime.UtcNow;
            var swept = scope.ServiceProvider.GetRequiredService<FullGameService>().SweepRetention(_options.FullGameRetentionDays);
            if (swept > 0) logger.LogInformation("Retention: deleted {Count} unkept full-game render(s) older than {Days} days", swept, _options.FullGameRetentionDays);
        }

        // Service (re)start: grab whatever of the last games' replays is still on
        // offer - the window is ~5 games, so an offline stretch can't be recovered later.
        if (_passed.Add(account.Id))
        {
            await scope.ServiceProvider.GetRequiredService<ReplayArchiveService>().SweepAsync(puuid, ct);
        }

        // First pass ever: current history is old news - baseline it and only watch forward.
        // (Backfill is explicit via /api/sync/history, mirroring the exporter.)
        if (!await db.KnownMatches.AnyAsync(ct))
        {
            var history = await riot.GetMatchIdsAsync(puuid, 0, 20, rankedOnly: false, ct);
            db.KnownMatches.AddRange(history.Select(id => new KnownMatch { Id = id }));
            await db.SaveChangesAsync(ct);
            await lp.TakeSnapshotAsync(puuid, ct);
            logger.LogInformation("First run: baselined {Count} existing games. New games are captured from now on.", history.Count);
            return;
        }

        // Look back 15, not 5: if the key was expired for a stretch (dev keys die
        // every 24h), several games can pile up unseen before it's refreshed.
        var recent = await riot.GetMatchIdsAsync(puuid, 0, 15, rankedOnly: false, ct);
        recent.Reverse();   // oldest first, so multi-game bursts stay in order

        var ingested = 0;
        foreach (var matchId in recent)
        {
            if (await db.KnownMatches.FindAsync([matchId], ct) is not null) continue;
            try
            {
                await IngestNewMatchAsync(matchId, puuid, db, riot, ingest, lp, ct);
                ingested++;
            }
            catch (RiotApiException ex) when (ex.IsAuthFailure)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // Corrupt match payload (no participants / tracked player missing) -
                // permanent, so remember it rather than retrying every pass.
                logger.LogWarning(ex, "Skipping unprocessable match {MatchId}", matchId);
                db.ChangeTracker.Clear();
                db.KnownMatches.Add(new KnownMatch { Id = matchId });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Transient (network, Riot 5xx) - leave unknown so the next pass retries.
                logger.LogWarning(ex, "Failed to ingest {MatchId}; will retry next pass", matchId);
                db.ChangeTracker.Clear();
            }
        }

        if (ingested > 0)
        {
            live.CaptureArrived();
            await scope.ServiceProvider.GetRequiredService<ReplayArchiveService>().SweepAsync(puuid, ct);
        }
    }

    private async Task IngestNewMatchAsync(
        string matchId, string puuid, LeagueDbContext db, RiotApiClient riot,
        MatchIngestService ingest, LpService lp, CancellationToken ct)
    {
        var matchRaw = await riot.GetMatchRawAsync(matchId, ct);
        string? timelineRaw = null;
        try
        {
            timelineRaw = await riot.GetTimelineRawAsync(matchId, ct);
        }
        catch (RiotApiException ex) when (!ex.IsAuthFailure)
        {
            logger.LogWarning("Timeline unavailable for {MatchId}: {Message}", matchId, ex.Message);
        }

        var match = await ingest.BuildMatchAsync(matchRaw, timelineRaw, puuid, withRanks: true, ranksAtGameTime: true, ct);
        match.RawPath = await ingest.SaveRawAsync(matchId, matchRaw, timelineRaw, ct);

        if (match.IsRanked && match.DurationSec >= 300)
        {
            await AttributeLpAsync(match, puuid, riot, lp, ct);
        }

        db.Matches.Add(match);
        db.KnownMatches.Add(new KnownMatch { Id = matchId });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Captured {Result} {Queue} {Champion} {K}/{D}/{A}{Lp}",
            match.Win ? "WIN" : "LOSS", match.QueueName, match.Champion, match.Kills, match.Deaths, match.Assists,
            match.LpChange is { } c ? $"  LP {(c >= 0 ? "+" : "")}{c}" : "");
    }

    private async Task AttributeLpAsync(Data.Match match, string puuid, RiotApiClient riot, LpService lp, CancellationToken ct)
    {
        var queueLabel = RankMath.QueueLabelForQueueId(match.QueueId);
        var queueType = RankMath.QueueTypeForQueueId(match.QueueId);
        var prev = await lp.GetLatestAsync(queueLabel, ct);

        // If the last snapshot already postdates this game (several games since the
        // last pass), the delta is unattributable anyway - don't wait on Riot.
        var alreadyCovered = prev is not null && match.GameEndUtc < prev.TimestampUtc;

        List<LeagueEntryDto> entries = [];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            entries = await riot.GetLeagueEntriesAsync(puuid, ct);
            if (alreadyCovered) break;
            var mine = entries.FirstOrDefault(e => e.QueueType == queueType);
            // Riot's win/loss counter registering the game is the signal that LP has settled.
            if (mine is not null && (prev is null || mine.Wins + mine.Losses > prev.Wins + prev.Losses)) break;
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        var rows = await lp.WriteSnapshotRowsAsync(entries, ct);
        var now = rows.FirstOrDefault(r => r.Queue == queueLabel);
        if (now is null) return;

        // Trust the delta only when exactly one game moved the counter; otherwise a
        // missed pass or dodge-LP would get blamed on this match.
        if (prev is not null && !alreadyCovered && (now.Wins + now.Losses) - (prev.Wins + prev.Losses) == 1)
        {
            match.LpChange = now.RankValue - prev.RankValue;
        }
        match.LpBefore = prev is null ? null : $"{prev.Tier} {prev.Division} {prev.Lp}";
        match.LpAfter = $"{now.Tier} {now.Division} {now.Lp}";
    }
}
