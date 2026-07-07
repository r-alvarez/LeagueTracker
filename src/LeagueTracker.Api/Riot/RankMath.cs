namespace LeagueTracker.Api.Riot;

/// One absolute LP scale so ranks can be averaged and diffed across tiers:
/// Iron IV 0 LP = 0, each division is 100, each tier 400. Master, Grandmaster
/// and Challenger share one ladder above Diamond I, so they all map to 2800+LP.
public static class RankMath
{
    private static readonly string[] Tiers = ["IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND"];
    private static readonly string[] Divisions = ["IV", "III", "II", "I"];

    public const string SoloQueueType = "RANKED_SOLO_5x5";
    public const string FlexQueueType = "RANKED_FLEX_SR";
    public const int SoloQueueId = 420;
    public const int FlexQueueId = 440;
    public static readonly int[] RankedQueueIds = [SoloQueueId, FlexQueueId];

    public static int? ToValue(string? tier, string? division, int lp)
    {
        if (tier is null or "") return null;
        tier = tier.ToUpperInvariant();
        if (tier is "MASTER" or "GRANDMASTER" or "CHALLENGER") return 2800 + lp;

        var tierIdx = Array.IndexOf(Tiers, tier);
        var divIdx = Array.IndexOf(Divisions, division?.ToUpperInvariant());
        if (tierIdx < 0 || divIdx < 0) return null;
        return tierIdx * 400 + divIdx * 100 + lp;
    }

    public static string ToLabel(double value)
    {
        if (value >= 2800) return $"Master+ {(int)(value - 2800)} LP";
        if (value < 0) value = 0;
        var tierIdx = Math.Min((int)(value / 400), Tiers.Length - 1);
        var rem = value - tierIdx * 400;
        var divIdx = (int)(rem / 100);
        var lp = (int)(rem - divIdx * 100);
        var tier = char.ToUpperInvariant(Tiers[tierIdx][0]) + Tiers[tierIdx][1..].ToLowerInvariant();
        return $"{tier} {Divisions[divIdx]} {lp} LP";
    }

    public static string QueueLabel(string queueType) => queueType == FlexQueueType ? "Flex" : "Solo/Duo";

    public static string QueueLabelForQueueId(int queueId) => queueId == FlexQueueId ? "Flex" : "Solo/Duo";

    public static string QueueTypeForQueueId(int queueId) => queueId == FlexQueueId ? FlexQueueType : SoloQueueType;

    /// Rank entry matching the game's queue; falls back to the player's other
    /// ranked queue so a Flex-only player still contributes to a Solo game's average.
    public static LeagueEntryDto? SelectEntryForQueue(IEnumerable<LeagueEntryDto> entries, int queueId)
    {
        var list = entries.Where(e => e.QueueType is SoloQueueType or FlexQueueType).ToList();
        var preferred = QueueTypeForQueueId(queueId);
        return list.FirstOrDefault(e => e.QueueType == preferred) ?? list.FirstOrDefault();
    }

    public static string QueueName(int queueId) => queueId switch
    {
        400 => "Normal Draft", 420 => "Ranked Solo/Duo", 430 => "Normal Blind", 440 => "Ranked Flex",
        450 => "ARAM", 480 => "Swiftplay", 490 => "Quickplay", 700 => "Clash", 720 => "ARAM Clash",
        900 => "ARURF", 1700 => "Arena", 1900 => "URF",
        _ => $"Queue {queueId}",
    };
}
