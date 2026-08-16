using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// Mirrors the newest agent build from GitHub Releases into ReleaseDir, so
/// nobody copies zips to the NAS by hand: the agent-release workflow
/// publishes `agent-<version>` with a LeagueTracker.RenderAgent-<version>.zip
/// asset, this pulls it, the agents pull it from here. Only the tracker with
/// Agent:SyncReleases on does it (the folder is shared - one writer).
public sealed class AgentReleaseSyncService(
    IOptions<AgentOptions> options, AgentRegistry registry, IHttpClientFactory http, ILogger<AgentReleaseSyncService> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private const int Keep = 3;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.SyncReleases) return;
        if (options.Value.GitHubRepo is not { Length: > 0 } repo)
        {
            log.LogWarning("Agent:SyncReleases is on but Agent:GitHubRepo is blank - not syncing");
            return;
        }
        log.LogInformation("Agent release sync on: {Repo} -> {Dir} every {Min} min", repo, registry.ReleaseDir, Every.TotalMinutes);
        while (!ct.IsCancellationRequested)
        {
            try { await SyncOnceAsync(repo, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Agent release sync failed: {Message}", ex.Message); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncOnceAsync(string repo, CancellationToken ct)
    {
        var client = http.CreateClient("github");
        // Unauthenticated: 60 requests/hour per IP, this uses 4. A public repo
        // needs no token for the API or the asset download.
        using var resp = await client.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=10", ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("GitHub releases lookup returned {Status}", (int)resp.StatusCode);
            return;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        (Version Version, string Name, string Url, long Size)? best = null;
        var installers = new Dictionary<Version, (string Name, string Url, long Size)>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("LeagueTracker.Agent-Setup-") && name.EndsWith(".exe") && Version.TryParse(name["LeagueTracker.Agent-Setup-".Length..^4], out var iv))
                {
                    installers[iv] = (name, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
                    continue;
                }
                if (!name.StartsWith("LeagueTracker.RenderAgent-") || !name.EndsWith(".zip")) continue;
                if (!Version.TryParse(name["LeagueTracker.RenderAgent-".Length..^4], out var version)) continue;
                if (best is null || version > best.Value.Version)
                {
                    best = (version, name, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
                }
            }
        }
        if (best is null) return;

        Directory.CreateDirectory(registry.ReleaseDir);
        var target = Path.Combine(registry.ReleaseDir, best.Value.Name);
        if (File.Exists(target) && new FileInfo(target).Length == best.Value.Size) return;
        if (registry.Latest() is { } local && Version.Parse(local.Version) >= best.Value.Version) return;

        log.LogInformation("Agent {Version} published on GitHub - downloading {Name} ({Mb} MB)", best.Value.Version, best.Value.Name, best.Value.Size / 1_000_000);
        // .partial then rename: the trackers only ever see whole zips.
        var partial = target + ".partial";
        using (var download = await client.GetAsync(best.Value.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            download.EnsureSuccessStatusCode();
            await using var source = await download.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, ct);
        }
        if (new FileInfo(partial).Length != best.Value.Size)
        {
            File.Delete(partial);
            throw new InvalidOperationException($"downloaded size differs from GitHub's for {best.Value.Name}");
        }
        File.Move(partial, target, overwrite: true);
        log.LogInformation("Agent {Version} is now in {Dir}; agents update when idle", best.Value.Version, registry.ReleaseDir);

        // The installer beside it, best-effort: new machines download it from
        // the Data page; installed agents never need it.
        if (installers.TryGetValue(best.Value.Version, out var setup))
        {
            var setupTarget = Path.Combine(registry.ReleaseDir, setup.Name);
            if (!File.Exists(setupTarget) || new FileInfo(setupTarget).Length != setup.Size)
            {
                try
                {
                    var setupPartial = setupTarget + ".partial";
                    using var download = await client.GetAsync(setup.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    download.EnsureSuccessStatusCode();
                    await using (var source = await download.Content.ReadAsStreamAsync(ct))
                    await using (var file = File.Create(setupPartial))
                    {
                        await source.CopyToAsync(file, ct);
                    }
                    File.Move(setupPartial, setupTarget, overwrite: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Installer for {Version} not mirrored: {Message}", best.Value.Version, ex.Message); }
            }
        }

        foreach (var oldSetup in Directory.EnumerateFiles(registry.ReleaseDir, "LeagueTracker.Agent-Setup-*.exe")
                     .Select(f => (Path: f, Version: ParseVersion(f)))
                     .Where(f => f.Version is not null && f.Version < best.Value.Version))
        {
            try { File.Delete(oldSetup.Path); } catch { /* next time */ }
        }
        foreach (var old in Directory.EnumerateFiles(registry.ReleaseDir, "LeagueTracker.RenderAgent-*.zip")
                     .Select(f => (Path: f, Version: ParseVersion(f)))
                     .Where(f => f.Version is not null)
                     .OrderByDescending(f => f.Version)
                     .Skip(Keep))
        {
            try { File.Delete(old.Path); log.LogInformation("Pruned old agent build {Name}", Path.GetFileName(old.Path)); }
            catch (Exception ex) { log.LogWarning("Could not prune {Name}: {Message}", Path.GetFileName(old.Path), ex.Message); }
        }
    }

    private static Version? ParseVersion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > 0 && Version.TryParse(name[(dash + 1)..], out var v) ? v : null;
    }
}
