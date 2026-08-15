using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// The game cut down to what is worth re-watching before the next one: the
/// moments the player was actually in, as replay timestamps.
///
/// Scoped deliberately to moments with the player in them, because the render
/// agent drives the replay with ONE camera lock (the dropdown click is the
/// only route to a follow-cam, so re-aiming it per moment is the fragile
/// thing the clip pipeline already does). Fights the player missed are not
/// dropped from review - they are exactly what the auto-rendered fight clips
/// on the match page are for, filmed from someone who was there.
public sealed class ReviewReelService(LeagueDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record FightDto(
        int StartSec, int EndSec, string Kind, string Result, bool Participated,
        int Allies, int Enemies, int AllyKills, int EnemyKills, int GoldSwing, bool ConvertedObjective);

    /// One stop: a replay window, what it is, and the facts to check it
    /// against. StartSec/EndSec are game-clock seconds, which is what the
    /// Replay API's playback time takes.
    public sealed record Moment(
        string Kind, int TimeSec, int StartSec, int EndSec, string Title, string Detail);

    /// The reel plus who the camera belongs to - the agent needs both to lock
    /// the replay's camera, and the tracker is the only side that knows which
    /// participant "me" is.
    public sealed record Reel(
        string MatchId, string? MyRiotId, string? MyChampion, IReadOnlyList<Moment> Moments);

    /// The engage forms well before the first kill lands - the same 20s of
    /// approach the fight clips use, for the same reason.
    private const int PreRollSec = 20;
    private const int FightPostSec = 10;
    /// A death's window ends a few beats AFTER the death, not at it: timeline
    /// seconds truncate and the agent's advance poll fires on >= EndSec, so a
    /// zero post-roll skipped to the next moment while the killing blow was
    /// still landing - the player never saw themselves die. The replay parks a
    /// dead champion's camera at their own fountain, so anything much longer
    /// is empty base; three seconds buys the death itself and no more.
    private const int DeathPostSec = 3;
    /// A kill outside any fight is a pick or a solo kill - the same few beats
    /// after it as a death gets: the blow lands, the camera moves on.
    private const int KillPostSec = 3;

    /// Teamfights always; skirmishes once they cost two lives. The same
    /// significance gate the fight-clip planner uses.
    private static bool Significant(FightDto f) =>
        f.Kind is "teamfight" || (f.Kind is "skirmish" && f.AllyKills + f.EnemyKills >= 2);

    /// A fight's claim on a review slot. Gold is the honest spine (it already
    /// prices kills by who died); the rest are what gold under-counts - bodies
    /// on the floor, a teamfight's weight, and buying an objective with it.
    private static int Impact(List<FightDto> group) =>
        Math.Abs(group.Sum(f => f.GoldSwing))
        + 400 * group.Sum(f => f.AllyKills + f.EnemyKills)
        + (group.Any(f => f.Kind is "teamfight") ? 1500 : 0)
        + (group.Any(f => f.ConvertedObjective) ? 800 : 0);

    public async Task<Reel?> GetAsync(string matchId, CancellationToken ct)
    {
        var match = await db.Matches.AsNoTracking()
            .AsSplitQuery()
            .Include(m => m.Participants)
            .Include(m => m.DeathEvents.OrderBy(d => d.TimeSec))
            .FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null) return null;
        var me = match.Participants.FirstOrDefault(p => p.IsMe);
        var myKills = me is null ? [] : await db.KillEvents.AsNoTracking()
            .Where(k => k.MatchId == matchId && k.KillerParticipantId == me.ParticipantId)
            .OrderBy(k => k.TimeSec)
            .ToListAsync(ct);

        var fights = (match.FightsJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<List<FightDto>>(match.FightsJson, Json) ?? []
                : [])
            // Only fights the player was in: the camera is locked to them for
            // the whole session, and a fight across the map from that camera
            // is thirty seconds of fog.
            .Where(f => f.Participated && Significant(f))
            .OrderBy(f => f.StartSec)
            .ToList();

        // Every significant fight the player was in - no budget. Ruben's rule
        // (2026-08-15): the review is complete or it is not a review; a long
        // war simply takes longer to watch, and the hotkeys skip.
        var chosen = MergeAdjacent(fights).Select(g => FightMoment(g).Moment).ToList();

        // A death or a kill inside a fight IS that fight; one nothing covers is
        // the pick, the caught-out or the solo kill - the review's best material.
        bool Covered(int t) => chosen.Any(m => t >= m.StartSec && t <= m.EndSec);
        chosen.AddRange(match.DeathEvents.Where(d => !Covered(d.TimeSec)).Select(DeathMoment));
        chosen.AddRange(myKills.Where(k => !Covered(k.TimeSec)).Select(k => KillMoment(k, match)));

        return new Reel(matchId, me?.RiotId, me?.Champion, Deoverlap([.. chosen.OrderBy(m => m.StartSec)]));
    }

    /// Fights whose windows touch are one fight to a reviewer - the re-engage
    /// after a trade is the same decision continuing. Left separate they seek
    /// backwards over tape just watched.
    private static List<List<FightDto>> MergeAdjacent(List<FightDto> fights)
    {
        var groups = new List<List<FightDto>>();
        foreach (var f in fights)
        {
            if (groups.Count > 0 && f.StartSec - PreRollSec <= groups[^1][^1].EndSec + FightPostSec) groups[^1].Add(f);
            else groups.Add([f]);
        }
        return groups;
    }

    private static (Moment Moment, int Impact) FightMoment(List<FightDto> group)
    {
        var gold = group.Sum(f => f.GoldSwing);
        var lead = group.OrderByDescending(f => Math.Abs(f.GoldSwing)).First();
        var name = group.Count > 1
            ? $"{(group.All(f => f.Kind is "teamfight") ? "Teamfight" : "Fight")} - {group.Count} exchanges"
            : $"{(lead.Kind is "teamfight" ? "Teamfight" : "Skirmish")} {lead.Allies}v{lead.Enemies}";
        var detail = string.Join(" · ", new[]
        {
            string.Join(" then ", group.Select(f => $"{f.AllyKills}-{f.EnemyKills}")),
            $"{(gold > 0 ? "+" : "")}{gold}g",
            group.Count > 1 ? null : lead.Result,
            group.Any(f => f.ConvertedObjective) ? "converted" : "no objective after",
        }.OfType<string>());

        return (new Moment(
            "fight",
            group[0].StartSec,
            Math.Max(0, group[0].StartSec - PreRollSec),
            group[^1].EndSec + FightPostSec,
            name,
            detail), Impact(group));
    }

    private static Moment KillMoment(KillEvent k, Match match)
    {
        var victim = match.Participants.FirstOrDefault(p => p.ParticipantId == k.VictimParticipantId)?.Champion;
        var assists = k.AssistIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        return new Moment("kill", k.TimeSec, Math.Max(0, k.TimeSec - PreRollSec), k.TimeSec + KillPostSec,
            victim is { Length: > 0 } ? $"Kill - {victim}" : "Kill",
            assists > 0 ? $"{assists} assist{(assists == 1 ? "" : "s")}" : "solo kill");
    }

    private static Moment DeathMoment(Death d)
    {
        // Facts the tape can be checked against - who, how outnumbered, where.
        // The judgement stays the player's.
        var detail = string.Join(" · ", new[]
        {
            d is { EnemiesNearDeath: int on, AlliesNearDeath: int with } ? $"{on} on you, {with} with you" : null,
            d.Zone is { Length: > 0 } ? d.Zone : null,
            d.Shutdown > 0 ? "shutdown given" : null,
            d is { SecondsAfterObjective: int after and <= 30, ObjectiveBefore: { Length: > 0 } obj }
                ? $"{after}s after {obj.ToLowerInvariant()}" : null,
        }.OfType<string>());

        return new Moment(
            "death",
            d.TimeSec,
            Math.Max(0, d.TimeSec - PreRollSec),
            d.TimeSec + DeathPostSec,
            d.KilledBy is { Length: > 0 } by ? $"Death to {by}" : "Death",
            detail);
    }

    /// Nothing plays the same second twice. A death butting against the fight
    /// that killed them, or two picks 10s apart, would otherwise seek backwards
    /// over what was just watched. Windows give ground; one swallowed whole
    /// drops out rather than becoming a zero-length seek.
    private static List<Moment> Deoverlap(List<Moment> moments)
    {
        var kept = new List<Moment>();
        var playedTo = -1;
        foreach (var m in moments)
        {
            var start = Math.Max(m.StartSec, playedTo);
            if (start >= m.EndSec) continue;
            kept.Add(m with { StartSec = start });
            playedTo = m.EndSec;
        }
        return kept;
    }
}
