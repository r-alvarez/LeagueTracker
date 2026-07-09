using System.Text.Json;

namespace LeagueTracker.Api.Services;

public sealed record LiveParticipant(int ChampionId, int TeamId, string? RiotId, bool IsMe);

public sealed record LiveGameSnapshot(
    long GameId,
    string MatchId,
    int QueueId,
    DateTime? StartedUtc,
    DateTime DetectedUtc,
    int MyChampionId,
    int MyTeamId,
    IReadOnlyList<LiveParticipant> Participants)
{
    /// Spectator reports gameStartTime as 0 until a few minutes in; treat that as unknown.
    public static LiveGameSnapshot Parse(string raw, string myPuuid)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var gameId = root.GetProperty("gameId").GetInt64();
        var platform = root.TryGetProperty("platformId", out var pf) ? pf.GetString() ?? "" : "";
        var queueId = root.TryGetProperty("gameQueueConfigId", out var q) ? q.GetInt32() : 0;
        var startMs = root.TryGetProperty("gameStartTime", out var st) ? st.GetInt64() : 0;

        var participants = new List<LiveParticipant>();
        var myChampionId = 0;
        var myTeamId = 0;
        if (root.TryGetProperty("participants", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                var championId = p.TryGetProperty("championId", out var ch) ? ch.GetInt32() : 0;
                var teamId = p.TryGetProperty("teamId", out var t) ? t.GetInt32() : 0;
                var riotId = p.TryGetProperty("riotId", out var r) ? r.GetString() : null;
                var isMe = p.TryGetProperty("puuid", out var pu) && pu.GetString() == myPuuid;
                if (isMe)
                {
                    myChampionId = championId;
                    myTeamId = teamId;
                }
                participants.Add(new LiveParticipant(championId, teamId, riotId, isMe));
            }
        }

        return new LiveGameSnapshot(
            gameId,
            $"{platform.ToUpperInvariant()}_{gameId}",
            queueId,
            startMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime : null,
            DateTime.UtcNow,
            myChampionId,
            myTeamId,
            participants);
    }
}

/// Shared between the poller (writer) and /api/live (reader). Also carries the
/// "a game just ended" flag that switches the poller to its fast-capture cadence.
public sealed class LiveGameState
{
    private readonly object _gate = new();
    private LiveGameSnapshot? _current;
    private DateTime? _fastCaptureUntilUtc;

    public LiveGameSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool FastCapturePending
    {
        get { lock (_gate) return _fastCaptureUntilUtc is { } until && DateTime.UtcNow < until; }
    }

    public void SetLive(LiveGameSnapshot snapshot)
    {
        lock (_gate)
        {
            // Keep the first snapshot's DetectedUtc while the same game stays live.
            if (_current?.GameId == snapshot.GameId) snapshot = snapshot with { DetectedUtc = _current.DetectedUtc };
            _current = snapshot;
        }
    }

    /// Returns the match id of the game that just ended, or null when nothing was live.
    public string? EndLiveIfAny(TimeSpan fastCaptureWindow)
    {
        lock (_gate)
        {
            if (_current is null) return null;
            var matchId = _current.MatchId;
            _current = null;
            _fastCaptureUntilUtc = DateTime.UtcNow + fastCaptureWindow;
            return matchId;
        }
    }

    public void CaptureArrived()
    {
        lock (_gate) _fastCaptureUntilUtc = null;
    }
}
