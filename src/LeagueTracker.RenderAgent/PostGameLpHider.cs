namespace LeagueTracker.RenderAgent;

/// Keeps the League client's post-game LP out of sight for a player who
/// switched on Hide LP on the tracker's Data page - the site already kept LP
/// off its own pages, but the client is where the number actually lands.
/// Done the way the client's own buttons do it, through its local API: the
/// post-game lobby is dismissed as soon as it opens, and rank-change
/// notifications (promotion, demotion ceremonies) are acknowledged unseen.
/// Nothing is injected into the client or drawn over it, so there is nothing
/// for Vanguard or Riot's third-party rules to object to. The game's stats
/// are not lost: the tracker records them, which is where this player reads
/// them anyway.
public sealed class PostGameLpHider(string leagueRoot, IReadOnlyList<TrackerClient> servers)
{
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(2);
    // Tight around the end of a game: every tick the lobby stays open is a
    // chance to read the number.
    private static readonly TimeSpan PostGameTick = TimeSpan.FromMilliseconds(250);
    // The toggle lives on the site; a flip reaches the PC within this.
    private static readonly TimeSpan SettingEvery = TimeSpan.FromMinutes(5);
    // A missed read is asked again well before the game it was for ends.
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    // The tracker client waits minutes for uploads; this loop can't - a
    // connection left dead by sleep would hold it past the next post-game.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<long> LpQueues = [420, 440];

    private LcuClient? _lcu;
    private bool _hideLp;
    private string? _settingRiotId;
    private DateTime _settingReadUtc = DateTime.MinValue;
    private bool _readFailing;
    private long _dismissedGameId;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !RenderAgent.StopRequested)
        {
            var phase = default(string);
            try
            {
                phase = await PassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Hide LP pass failed: {ex.Message}");
            }
            try { await Task.Delay(IsPostGame(phase) ? PostGameTick : IdleTick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static bool IsPostGame(string? phase) => phase is "PreEndOfGame" or "EndOfGame";

    /// Only ranked games move LP. An unknown queue counts as ranked: a
    /// dismissed normal-game lobby costs nothing, a seen LP gain is the one
    /// thing this exists to prevent.
    internal static bool MovesLp(long queueId) => queueId < 0 || LpQueues.Contains(queueId);

    private async Task<string?> PassAsync(CancellationToken ct)
    {
        _lcu ??= LcuClient.TryConnect(leagueRoot);
        if (_lcu is null) return null;
        if (await _lcu.GetGameflowPhaseAsync(ct) is not { } phase)
        {
            // Closed or restarting: the next lockfile carries a new port and token.
            _lcu.Dispose();
            _lcu = null;
            return null;
        }

        await RefreshSettingAsync(_lcu, ct);
        if (!_hideLp || !IsPostGame(phase)) return phase;

        var session = await _lcu.GetGameSessionAsync(ct);
        if (!MovesLp(session?.QueueId ?? -1)) return phase;

        // Ceremonies can queue up during the honor screen, before the lobby.
        foreach (var id in await _lcu.GetRankedNotificationIdsAsync(ct))
        {
            await _lcu.AcknowledgeRankedNotificationAsync(id, ct);
        }

        if (phase is "EndOfGame" && await _lcu.DismissPostGameStatsAsync(ct))
        {
            var gameId = session?.GameId ?? 0;
            if (gameId != _dismissedGameId) Log.Info($"Hide LP: dismissed the post-game lobby of game {gameId}");
            _dismissedGameId = gameId;
        }
        return phase;
    }

    /// Per signed-in account: a shared PC follows whoever is logged in, and
    /// an account the trackers don't know is left alone.
    private async Task RefreshSettingAsync(LcuClient lcu, CancellationToken ct)
    {
        if (await lcu.GetCurrentRiotIdAsync(ct) is not { Length: > 0 } riotId) return;
        var every = _readFailing ? RetryEvery : SettingEvery;
        if (riotId == _settingRiotId && DateTime.UtcNow - _settingReadUtc < every) return;
        _settingReadUtc = DateTime.UtcNow;

        if (await ReadAccountsAsync(ct) is not { } known)
        {
            // No answer is not "off": a missed first read taken as "off" once
            // showed a whole morning's LP to a player who had it on.
            // The same player keeps the last answer; a new one starts unknown.
            if (!_readFailing) Log.Warn($"Hide LP: no tracker answered for {riotId} - asking again every {RetryEvery.TotalSeconds:0}s");
            _readFailing = true;
            if (riotId != _settingRiotId) _hideLp = false;
            _settingRiotId = riotId;
            return;
        }

        var hide = known.Exists(a => a.HideLp && string.Equals(a.RiotId, riotId, StringComparison.OrdinalIgnoreCase));
        if (hide != _hideLp || riotId != _settingRiotId || _readFailing)
        {
            Log.Info(hide ? $"Hide LP is on for {riotId} - ranked post-game lobbies will be skipped" : $"Hide LP is off for {riotId}");
        }
        _readFailing = false;
        _settingRiotId = riotId;
        _hideLp = hide;
    }

    /// Every reachable tracker's accounts, or null when none answered.
    private async Task<List<TrackerAccount>?> ReadAccountsAsync(CancellationToken ct)
    {
        List<TrackerAccount>? known = null;
        foreach (var server in servers)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);
            try
            {
                if (await server.GetAccountsAsync(timeout.Token) is { } accounts) (known ??= []).AddRange(accounts);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out: counts as no answer from this tracker.
            }
        }
        return known;
    }
}
