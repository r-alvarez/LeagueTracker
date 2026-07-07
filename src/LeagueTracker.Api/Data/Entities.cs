using System.ComponentModel.DataAnnotations;

namespace LeagueTracker.Api.Data;

public sealed class Match
{
    /// Riot match id, e.g. EUW1_7234567890.
    [Key] public string Id { get; set; } = "";
    public int QueueId { get; set; }
    public string QueueName { get; set; } = "";
    public bool IsRanked { get; set; }
    public string GameMode { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public DateTime GameCreationUtc { get; set; }
    public DateTime GameEndUtc { get; set; }
    public double DurationSec { get; set; }
    public bool HasTimeline { get; set; }
    /// Path of the raw { matchId, match, timeline } JSON on disk (kept out of the db - timelines are MBs).
    public string RawPath { get; set; } = "";

    // Tracked player's headline stats, denormalised for cheap match-list queries.
    public string Champion { get; set; } = "";
    public string Position { get; set; } = "";
    public bool Win { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Cs { get; set; }
    public int Gold { get; set; }
    public int DamageToChampions { get; set; }
    public int VisionScore { get; set; }
    public int ChampLevel { get; set; }

    // Team-vs-team rank picture (from League-V4 entries at capture time).
    public double? AvgAllyRankValue { get; set; }
    public double? AvgEnemyRankValue { get; set; }
    public int AllyRanksKnown { get; set; }
    public int EnemyRanksKnown { get; set; }
    /// True when ranks were captured minutes after the game (poller) rather than
    /// during a later backfill, i.e. they are the ranks at game time.
    public bool RanksAtGameTime { get; set; }

    // LP attribution - null until snapshots bracket exactly this one game.
    public int? LpChange { get; set; }
    public string? LpBefore { get; set; }
    public string? LpAfter { get; set; }

    // Movement metrics for the tracked player, derived from the position track
    // (60s frames, so coarse by nature).
    public double? TimeInEnemyHalfPct { get; set; }
    public int? AvgNearestAllyDist { get; set; }

    // My skillshot counters, denormalised from my participant row for list queries.
    public int? SkillshotsHit { get; set; }
    public int? SkillshotsDodged { get; set; }

    public List<MatchParticipant> Participants { get; set; } = [];
    public List<Death> DeathEvents { get; set; } = [];
    public List<PositionSample> PositionSamples { get; set; } = [];
    public List<KillEvent> KillEvents { get; set; } = [];
    public List<ObjectiveEvent> ObjectiveEvents { get; set; } = [];
    public List<ItemEvent> ItemEvents { get; set; } = [];
}

public sealed class MatchParticipant
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int ParticipantId { get; set; }
    public string Puuid { get; set; } = "";
    public string RiotId { get; set; } = "";
    public string Champion { get; set; } = "";
    public int TeamId { get; set; }
    public bool IsMe { get; set; }
    public bool IsAlly { get; set; }
    public string Position { get; set; } = "";
    public bool Win { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Cs { get; set; }
    public int Gold { get; set; }
    public int DamageToChampions { get; set; }
    public int VisionScore { get; set; }
    public int ChampLevel { get; set; }

    // Rank at capture time; null tier = unranked or never looked up.
    public string? Tier { get; set; }
    public string? Division { get; set; }
    public int? Lp { get; set; }
    public int? SeasonWins { get; set; }
    public int? SeasonLosses { get; set; }
    public int? RankValue { get; set; }
    public string? RankQueue { get; set; }

    // Riot challenge counters (per-game totals; absent on old/corrupt games).
    public int? SkillshotsHit { get; set; }
    public int? SkillshotsDodged { get; set; }
    /// Dodges with under ~250ms to react - the clutch ones.
    public int? SkillshotDodgesLateWindow { get; set; }
    public double? KillParticipation { get; set; }

    // Loadout - ids resolved to names client-side against static data.
    public int Summoner1Id { get; set; }
    public int Summoner2Id { get; set; }
    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }
    public int KeystoneId { get; set; }
    /// Final items, item0..item6 comma-separated.
    public string Items { get; set; } = "";
}

/// One death of the tracked player, extracted from the match timeline:
/// when, where, by whom, and their economy at that moment.
public sealed class Death
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int TimeSec { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string KilledBy { get; set; } = "";
    public string AssistedBy { get; set; } = "";
    /// Every enemy champion that landed damage pre-death (catches participants
    /// present in the fight without official kill credit).
    public string DamageFrom { get; set; } = "";
    public int EnemiesOnYou { get; set; }
    public int Bounty { get; set; }
    public int Shutdown { get; set; }
    public int? MyLevel { get; set; }
    public int? MyTotalGold { get; set; }
    public int? MyCs { get; set; }

    // True collapse picture: every player's position interpolated to the death
    // timestamp between the two bounding 60s frames (the kill event's own
    // position is exact; everyone else's is an estimate).
    /// Enemies within 2000 units - who actually converged, not who got credit.
    public int? EnemiesNearDeath { get; set; }
    /// Allies (excluding me) within 2000 units - was this a group state or was I alone.
    public int? AlliesNearDeath { get; set; }
    public int? NearestAllyDist { get; set; }

    // Damage picture from victimDamageReceived (the ~10s window before death).
    public int? TotalDamageReceived { get; set; }
    public int? DamageInstanceCount { get; set; }
    /// Share of the total dealt by the single biggest source (1.0 = pure one-shot,
    /// low = whittled down while overstaying - different leaks, different fixes).
    public double? TopSourceShare { get; set; }
    public string? TopSource { get; set; }

    // Overstay signal: this death came shortly after my team took an objective.
    public int? SecondsAfterObjective { get; set; }
    public string? ObjectiveBefore { get; set; }

    public List<DeathDamage> DamageInstances { get; set; } = [];
}

/// One entry of the kill event's victimDamageReceived - every damage instance
/// in the ~10s before the death: source, ability, and split by damage type.
public sealed class DeathDamage
{
    public long Id { get; set; }
    public long DeathId { get; set; }
    public string Source { get; set; } = "";
    public string SpellName { get; set; } = "";
    public int Physical { get; set; }
    public int Magic { get; set; }
    public int TrueDamage { get; set; }
    public int Total => Physical + Magic + TrueDamage;
}

/// Per-frame (60s) position of every participant - the continuous track that
/// death snapshots can't give: isolation over the whole game, near-misses,
/// how grouped the map actually was.
public sealed class PositionSample
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int ParticipantId { get; set; }
    public int TimeSec { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class KillEvent
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int TimeSec { get; set; }
    public int KillerParticipantId { get; set; }
    public int VictimParticipantId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// BUILDING_KILL / ELITE_MONSTER_KILL sequence - lets deaths be lined up
/// against objectives (conversion after won fights, overstay after baron).
public sealed class ObjectiveEvent
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int TimeSec { get; set; }
    /// TOWER, INHIBITOR, DRAGON, BARON, HERALD, GRUBS, ATAKHAN
    public string Kind { get; set; } = "";
    /// Tower tier / dragon element when Riot provides it.
    public string SubKind { get; set; } = "";
    public bool ByMyTeam { get; set; }
    public int KillerParticipantId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// The tracked player's item timeline (purchases, sells, undos) - build
/// verification and panic-buy spotting.
public sealed class ItemEvent
{
    public long Id { get; set; }
    public string MatchId { get; set; } = "";
    public int TimeSec { get; set; }
    /// PURCHASED, SOLD, UNDO
    public string Kind { get; set; } = "";
    public int ItemId { get; set; }
}

/// Point-in-time record of the tracked player's rank/LP per queue. Riot's own
/// wins/losses counters ride along so LP deltas can be attributed to exactly
/// one game (or refused when several games share the gap).
public sealed class LpSnapshot
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Queue { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Division { get; set; } = "";
    public int Lp { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int RankValue { get; set; }
}

/// Every match id ever seen (baselined or fully ingested) - the poller's memory.
public sealed class KnownMatch
{
    [Key] public string Id { get; set; } = "";
}

public sealed class KeyValue
{
    [Key] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
