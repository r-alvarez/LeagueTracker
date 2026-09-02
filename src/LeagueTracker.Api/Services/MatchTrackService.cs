using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record TrackParticipant(int Pid, string Champion, int TeamId, bool IsMe, bool IsAlly, string Position);
// P is indexed by participant id minus one; a slot is null when the frame
// carried no sample for that player.
public sealed record TrackFrame(int T, int[]?[] P);
public sealed record TrackKill(int T, int Killer, int Victim, int[] Assists, int[] Damage, int X, int Y);
public sealed record TrackObjective(int T, string Kind, string SubKind, bool ByMyTeam, int Killer, int X, int Y);
public sealed record MatchTrack(
    string MatchId, int DurationSec, int MyPid,
    TrackParticipant[] Participants, TrackFrame[] Frames, TrackKill[] Kills, TrackObjective[] Objectives);

// Kept out of the match detail payload: that one loads on every match page,
// the map is opened on demand.
public sealed class MatchTrackService(LeagueDbContext db)
{
    public async Task<MatchTrack?> GetAsync(string matchId, CancellationToken ct)
    {
        var match = await db.Matches.AsNoTracking()
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null) return null;

        var samples = await db.PositionSamples.AsNoTracking().Where(s => s.MatchId == matchId).ToListAsync(ct);
        if (samples is not { Count: > 0 }) return null;

        var kills = await db.KillEvents.AsNoTracking().Where(k => k.MatchId == matchId).ToListAsync(ct);
        var objectives = await db.ObjectiveEvents.AsNoTracking().Where(o => o.MatchId == matchId).ToListAsync(ct);
        return Build(match, samples, kills, objectives);
    }

    public static MatchTrack Build(
        Match match,
        IReadOnlyCollection<PositionSample> samples,
        IReadOnlyCollection<KillEvent> kills,
        IReadOnlyCollection<ObjectiveEvent> objectives)
    {
        var participants = match.Participants
            .OrderBy(p => p.ParticipantId)
            .Select(p => new TrackParticipant(p.ParticipantId, p.Champion, p.TeamId, p.IsMe, p.IsAlly, p.Position))
            .ToArray();
        var slots = Math.Max(10, participants.Length == 0 ? 0 : participants.Max(p => p.Pid));

        var frames = samples
            .GroupBy(s => s.TimeSec)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var positions = new int[]?[slots];
                foreach (var sample in g.Where(s => s.ParticipantId >= 1 && s.ParticipantId <= slots))
                {
                    positions[sample.ParticipantId - 1] = [sample.X, sample.Y];
                }
                return new TrackFrame(g.Key, positions);
            })
            .ToArray();

        var trackKills = kills
            .OrderBy(k => k.TimeSec)
            .Select(k => new TrackKill(k.TimeSec, k.KillerParticipantId, k.VictimParticipantId, Ids(k.AssistIds), Ids(k.DamagePids), k.X, k.Y))
            .ToArray();
        var trackObjectives = objectives
            .OrderBy(o => o.TimeSec)
            .Select(o => new TrackObjective(o.TimeSec, o.Kind, o.SubKind, o.ByMyTeam, o.KillerParticipantId, o.X, o.Y))
            .ToArray();

        var myPid = match.Participants.FirstOrDefault(p => p.IsMe)?.ParticipantId ?? 0;
        return new MatchTrack(match.Id, (int)Math.Round(match.DurationSec), myPid, participants, frames, trackKills, trackObjectives);
    }

    private static int[] Ids(string csv) =>
        [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)];
}
