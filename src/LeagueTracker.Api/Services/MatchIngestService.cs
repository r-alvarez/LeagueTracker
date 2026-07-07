using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

/// Turns raw Riot JSON (match + optional timeline) into entities and a raw file
/// on disk. Shared by the live poller, the history backfill and the importer -
/// one code path, identical results.
public sealed class MatchIngestService(RankLookupService ranks, DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string GamesDir => paths.GamesDir;

    /// withRanks: look up all 10 participants' current League-V4 entries.
    /// ranksAtGameTime: mark those ranks as captured right after the game (live
    /// poller) rather than during a later backfill where they have drifted.
    public async Task<Match> BuildMatchAsync(
        string matchRaw, string? timelineRaw, string myPuuid,
        bool withRanks, bool ranksAtGameTime, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<RiotMatchDto>(matchRaw, Json)
            ?? throw new InvalidOperationException("Match JSON did not deserialize");
        var info = dto.Info;
        var me = info.Participants.FirstOrDefault(p => p.Puuid == myPuuid)
            ?? throw new InvalidOperationException($"Tracked player not found in match {dto.Metadata.MatchId}");

        var match = new Match
        {
            Id = dto.Metadata.MatchId,
            QueueId = info.QueueId,
            QueueName = RankMath.QueueName(info.QueueId),
            IsRanked = RankMath.RankedQueueIds.Contains(info.QueueId),
            GameMode = info.GameMode,
            GameVersion = info.GameVersion,
            GameCreationUtc = info.GameCreationUtc,
            GameEndUtc = info.GameEndUtc,
            DurationSec = info.DurationSeconds,
            HasTimeline = timelineRaw is not null,
            Champion = me.ChampionName,
            Position = me.TeamPosition,
            Win = me.Win,
            Kills = me.Kills,
            Deaths = me.Deaths,
            Assists = me.Assists,
            Cs = me.TotalMinionsKilled + me.NeutralMinionsKilled,
            Gold = me.GoldEarned,
            DamageToChampions = me.TotalDamageDealtToChampions,
            VisionScore = me.VisionScore,
            ChampLevel = me.ChampLevel,
            RanksAtGameTime = withRanks && ranksAtGameTime,
            SkillshotsHit = me.Challenges?.SkillshotsHit,
            SkillshotsDodged = me.Challenges?.SkillshotsDodged,
        };

        foreach (var p in info.Participants)
        {
            var participant = new MatchParticipant
            {
                MatchId = match.Id,
                ParticipantId = p.ParticipantId,
                Puuid = p.Puuid,
                RiotId = p.RiotId,
                Champion = p.ChampionName,
                TeamId = p.TeamId,
                IsMe = p.Puuid == myPuuid,
                IsAlly = p.TeamId == me.TeamId,
                Position = p.TeamPosition,
                Win = p.Win,
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                Cs = p.TotalMinionsKilled + p.NeutralMinionsKilled,
                Gold = p.GoldEarned,
                DamageToChampions = p.TotalDamageDealtToChampions,
                VisionScore = p.VisionScore,
                ChampLevel = p.ChampLevel,
                Summoner1Id = p.Summoner1Id,
                Summoner2Id = p.Summoner2Id,
                PrimaryStyleId = p.PrimaryStyleId,
                SubStyleId = p.SubStyleId,
                KeystoneId = p.KeystoneId,
                Items = p.ItemsCsv,
                SkillshotsHit = p.Challenges?.SkillshotsHit,
                SkillshotsDodged = p.Challenges?.SkillshotsDodged,
                SkillshotDodgesLateWindow = p.Challenges?.DodgeSkillShotsSmallWindow,
                KillParticipation = p.Challenges?.KillParticipation,
            };

            if (withRanks && match.IsRanked)
            {
                var entries = await ranks.GetEntriesAsync(p.Puuid, TimeSpan.FromHours(ranksAtGameTime ? 1 : 24), ct);
                if (RankMath.SelectEntryForQueue(entries, info.QueueId) is { } entry)
                {
                    participant.Tier = entry.Tier;
                    participant.Division = entry.Rank;
                    participant.Lp = entry.LeaguePoints;
                    participant.SeasonWins = entry.Wins;
                    participant.SeasonLosses = entry.Losses;
                    participant.RankValue = RankMath.ToValue(entry.Tier, entry.Rank, entry.LeaguePoints);
                    participant.RankQueue = RankMath.QueueLabel(entry.QueueType);
                }
            }

            match.Participants.Add(participant);
        }

        var allyValues = match.Participants.Where(p => p.IsAlly && p.RankValue is not null).Select(p => (double)p.RankValue!).ToList();
        var enemyValues = match.Participants.Where(p => !p.IsAlly && p.RankValue is not null).Select(p => (double)p.RankValue!).ToList();
        match.AvgAllyRankValue = allyValues is { Count: > 0 } ? allyValues.Average() : null;
        match.AvgEnemyRankValue = enemyValues is { Count: > 0 } ? enemyValues.Average() : null;
        match.AllyRanksKnown = allyValues.Count;
        match.EnemyRanksKnown = enemyValues.Count;

        if (timelineRaw is not null)
        {
            ApplyTimelineAnalysis(match, TimelineAnalyzer.Analyze(timelineRaw, info, me));
        }

        return match;
    }

    public static void ApplyTimelineAnalysis(Match match, TimelineAnalysis analysis)
    {
        foreach (var d in analysis.Deaths) d.MatchId = match.Id;
        foreach (var p in analysis.Positions) p.MatchId = match.Id;
        foreach (var k in analysis.Kills) k.MatchId = match.Id;
        foreach (var o in analysis.Objectives) o.MatchId = match.Id;
        foreach (var i in analysis.ItemEvents) i.MatchId = match.Id;
        match.DeathEvents = analysis.Deaths;
        match.PositionSamples = analysis.Positions;
        match.KillEvents = analysis.Kills;
        match.ObjectiveEvents = analysis.Objectives;
        match.ItemEvents = analysis.ItemEvents;
        match.TimeInEnemyHalfPct = analysis.TimeInEnemyHalfPct;
        match.AvgNearestAllyDist = analysis.AvgNearestAllyDist;
    }

    /// Persists the same faithful { matchId, match, timeline } wrapper the
    /// PowerShell exporter writes, so both tools' game folders stay interchangeable.
    public async Task<string> SaveRawAsync(string matchId, string matchRaw, string? timelineRaw, CancellationToken ct)
    {
        Directory.CreateDirectory(GamesDir);
        var path = Path.Combine(GamesDir, $"{matchId}.json");
        var wrapper = $"{{\"matchId\":{JsonSerializer.Serialize(matchId)},\"match\":{matchRaw},\"timeline\":{timelineRaw ?? "null"}}}";
        await File.WriteAllTextAsync(path, wrapper, ct);
        return path;
    }
}
