namespace LeagueTracker.Api.Riot;

public sealed class AccountDto
{
    public string Puuid { get; set; } = "";
    public string GameName { get; set; } = "";
    public string TagLine { get; set; } = "";
}

public sealed class LeagueEntryDto
{
    public string QueueType { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Rank { get; set; } = "";
    public int LeaguePoints { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
}

public sealed class RiotMatchDto
{
    public MatchMetadataDto Metadata { get; set; } = new();
    public MatchInfoDto Info { get; set; } = new();
}

public sealed class MatchMetadataDto
{
    public string MatchId { get; set; } = "";
}

public sealed class MatchInfoDto
{
    public long GameCreation { get; set; }
    public long? GameEndTimestamp { get; set; }
    public double GameDuration { get; set; }
    public int QueueId { get; set; }
    public string GameMode { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public List<MatchParticipantDto> Participants { get; set; } = [];

    /// Old matches report gameDuration in ms; newer ones (carrying a
    /// gameEndTimestamp) report it in seconds. Normalise to seconds.
    public double DurationSeconds => GameEndTimestamp is not null ? GameDuration : GameDuration / 1000;

    public DateTime GameCreationUtc => DateTimeOffset.FromUnixTimeMilliseconds(GameCreation).UtcDateTime;

    public DateTime GameEndUtc => GameEndTimestamp is long end
        ? DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime
        : GameCreationUtc.AddSeconds(DurationSeconds);
}

public sealed class MatchParticipantDto
{
    public int ParticipantId { get; set; }
    public string Puuid { get; set; } = "";
    public string? RiotIdGameName { get; set; }
    public string? RiotIdTagline { get; set; }
    public string? SummonerName { get; set; }
    public string ChampionName { get; set; } = "";
    public int TeamId { get; set; }
    public string TeamPosition { get; set; } = "";
    public bool Win { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int TotalMinionsKilled { get; set; }
    public int NeutralMinionsKilled { get; set; }
    public int GoldEarned { get; set; }
    public int TotalDamageDealtToChampions { get; set; }
    public int VisionScore { get; set; }
    public int ChampLevel { get; set; }
    public int Summoner1Id { get; set; }
    public int Summoner2Id { get; set; }
    public int Item0 { get; set; }
    public int Item1 { get; set; }
    public int Item2 { get; set; }
    public int Item3 { get; set; }
    public int Item4 { get; set; }
    public int Item5 { get; set; }
    public int Item6 { get; set; }
    public PerksDto? Perks { get; set; }
    public ChallengesDto? Challenges { get; set; }

    public string RiotId => string.IsNullOrEmpty(RiotIdGameName) ? SummonerName ?? "" : $"{RiotIdGameName}#{RiotIdTagline}";

    public string ItemsCsv => string.Join(',', new[] { Item0, Item1, Item2, Item3, Item4, Item5, Item6 });

    public int PrimaryStyleId => Perks?.Styles.FirstOrDefault(s => s.Description == "primaryStyle")?.Style ?? 0;
    public int SubStyleId => Perks?.Styles.FirstOrDefault(s => s.Description == "subStyle")?.Style ?? 0;
    public int KeystoneId => Perks?.Styles.FirstOrDefault(s => s.Description == "primaryStyle")?.Selections.FirstOrDefault()?.Perk ?? 0;
}

public sealed class PerksDto
{
    public List<PerkStyleDto> Styles { get; set; } = [];
}

public sealed class PerkStyleDto
{
    public string Description { get; set; } = "";
    public int Style { get; set; }
    public List<PerkSelectionDto> Selections { get; set; } = [];
}

public sealed class PerkSelectionDto
{
    public int Perk { get; set; }
}

/// Riot's per-game challenge counters - totals only, there are no per-event
/// skillshot records anywhere in the API.
public sealed class ChallengesDto
{
    public int? SkillshotsHit { get; set; }
    public int? SkillshotsDodged { get; set; }
    public int? DodgeSkillShotsSmallWindow { get; set; }
    public double? KillParticipation { get; set; }
}
