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
        };
        ApplyMatchDtoStats(match, info, me);

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
                PerksJson = PerksJsonFor(p),
                Spell1Casts = p.Spell1Casts,
                Spell2Casts = p.Spell2Casts,
                Spell3Casts = p.Spell3Casts,
                Spell4Casts = p.Spell4Casts,
                Summoner1Casts = p.Summoner1Casts,
                Summoner2Casts = p.Summoner2Casts,
                PingsJson = PingsJsonFor(p),
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
        match.CsAt10 = analysis.CsAt10;
        match.CsAt14 = analysis.CsAt14;
        match.CsAt15 = analysis.CsAt15;
        match.LaneGoldDiff10 = analysis.LaneGoldDiff10;
        match.LaneXpDiff10 = analysis.LaneXpDiff10;
        match.LaneCsDiff10 = analysis.LaneCsDiff10;
        match.LaneGoldDiff15 = analysis.LaneGoldDiff15;
        match.LaneXpDiff15 = analysis.LaneXpDiff15;
        match.LaneCsDiff15 = analysis.LaneCsDiff15;
        match.FirstToLevel2 = analysis.FirstToLevel2;
        match.SkillOrder = analysis.SkillOrder;
        match.DpmEarly = analysis.DpmEarly;
        match.DpmMid = analysis.DpmMid;
        match.DpmLate = analysis.DpmLate;
        match.FollowInDeaths = analysis.Deaths.Count(d => d.FollowTeammate is not null);
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static string PerksJsonFor(MatchParticipantDto p) =>
        p.Perks is null ? "" : JsonSerializer.Serialize(p.Perks, WebJson);

    public static string PingsJsonFor(MatchParticipantDto p)
    {
        var pings = new Dictionary<string, int>
        {
            ["On my way"] = p.OnMyWayPings, ["Missing"] = p.EnemyMissingPings, ["Danger"] = p.DangerPings,
            ["Get back"] = p.GetBackPings, ["Assist me"] = p.AssistMePings, ["All in"] = p.AllInPings,
            ["Push"] = p.PushPings, ["Hold"] = p.HoldPings, ["Retreat"] = p.RetreatPings,
            ["Need vision"] = p.NeedVisionPings, ["Enemy vision"] = p.EnemyVisionPings,
            ["Vision cleared"] = p.VisionClearedPings, ["Command"] = p.CommandPings, ["Generic"] = p.BasicPings,
        };
        var nonZero = pings.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        return nonZero is { Count: > 0 } ? JsonSerializer.Serialize(nonZero) : "";
    }

    /// Match-level stats read straight from the match payload (no timeline) -
    /// Riot challenge numbers verbatim where present, computed fallback otherwise.
    /// Shared by ingest and reprocess so history stays consistent with live capture.
    public static void ApplyMatchDtoStats(Match match, MatchInfoDto info, MatchParticipantDto me)
    {
        var opp = info.Participants.FirstOrDefault(p =>
            p.TeamId != me.TeamId && p.TeamPosition == me.TeamPosition && me.TeamPosition is { Length: > 0 });
        var teamKills = info.Participants.Where(p => p.TeamId == me.TeamId).Sum(p => p.Kills);
        var durMin = Math.Max(1.0, info.DurationSeconds / 60.0);

        match.OpponentChampion = opp?.ChampionName;
        match.EnemyJungler = info.Participants.FirstOrDefault(p => p.TeamId != me.TeamId && p.TeamPosition == "JUNGLE")?.ChampionName;
        match.SkillshotsHit = me.Challenges?.SkillshotsHit;
        match.SkillshotsDodged = me.Challenges?.SkillshotsDodged;
        match.SoloKills = (int)(me.Challenges?.SoloKills ?? 0);
        match.KillParticipation = me.Challenges?.KillParticipation
            ?? (teamKills > 0 ? Math.Round((me.Kills + me.Assists) / (double)teamKills, 3) : null);
        match.ControlWards = me.DetectorWardsPlaced > 0 ? me.DetectorWardsPlaced : (int)(me.Challenges?.ControlWardsPlaced ?? 0);
        match.WardsPlaced = me.WardsPlaced;
        match.WardsKilled = me.WardsKilled;
        match.DamageTakenPerMin = Math.Round(me.TotalDamageTaken / durMin, 1);
        match.TripleKills = me.TripleKills;
        match.QuadraKills = me.QuadraKills;
        match.PentaKills = me.PentaKills;
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
