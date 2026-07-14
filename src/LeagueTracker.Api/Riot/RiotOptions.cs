namespace LeagueTracker.Api.Riot;

public sealed class RiotOptions
{
    public string GameName { get; set; } = "";
    public string TagLine { get; set; } = "";
    /// Regional routing (account-v1, match-v5): americas | europe | asia | sea
    public string Region { get; set; } = "europe";
    /// Platform routing (league-v4): euw1, eun1, na1, ...
    public string Platform { get; set; } = "euw1";
    /// Key resolution order: this value, RIOT_API_KEY env var, first line of ApiKeyFile.
    public string? ApiKey { get; set; }
    public string? ApiKeyFile { get; set; }
    public int PollSeconds { get; set; } = 120;
    /// Root for the SQLite db and raw per-game JSON. Relative paths resolve against content root.
    public string DataDir { get; set; } = "Data";
    public double RateSafetyMargin { get; set; } = 0.05;
    /// Full-game renders are big (~500MB); auto-delete this many days after
    /// rendering unless marked keep. Clips are small and live forever.
    public int FullGameRetentionDays { get; set; } = 60;
    /// Hide all rank/LP output (API responses, exports, SPA). Snapshots and
    /// per-game LP attribution keep accruing - LP isn't reconstructable later,
    /// so this suppresses display only.
    public bool HideLp { get; set; }
}
