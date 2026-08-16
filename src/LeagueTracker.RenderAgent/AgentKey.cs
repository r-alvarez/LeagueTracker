using System.Security.Cryptography;

namespace LeagueTracker.RenderAgent;

/// This machine's identity towards the trackers: a random key made once and
/// kept next to the exe (agent.key). The server stores only its hash; the
/// owner approves the machine on the Data page and from then on every
/// request carries it. Nothing to type, nothing to hand out.
public static class AgentKey
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "agent.key");

    public static string Load()
    {
        try
        {
            if (File.ReadAllText(Path).Trim() is { Length: >= 32 } existing) return existing;
        }
        catch (IOException) { /* first run */ }
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        File.WriteAllText(Path, key);
        Log.Info("Generated this machine's agent key (agent.key)");
        return key;
    }
}
