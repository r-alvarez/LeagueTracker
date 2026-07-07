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
}
