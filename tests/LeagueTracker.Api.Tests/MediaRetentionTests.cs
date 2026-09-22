using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

public class MediaRetentionTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly string[] Patches = ["16.18", "16.17", "16.16", "16.15"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly RenderLeaseService _leases = new();
    private readonly ClipService _clips;
    private readonly ReplayArchiveService _replays;
    private readonly FullGameService _full;
    private readonly VodService _vods;
    private long _freeBytes = 500 * Gb;

    public MediaRetentionTests()
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "A", TagLine = "B", DataDir = _root });
        var paths = new DataPaths(context);
        _replays = new ReplayArchiveService(null!, null!, paths, NullLogger<ReplayArchiveService>.Instance);
        _vods = new VodService(paths);
        _clips = new ClipService(null!, _replays, _vods, paths);
        _full = new FullGameService(null!, _replays, paths);
        Paths = paths;
    }

    private DataPaths Paths { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private MediaRetentionService Service(MediaRetentionOptions? options = null, double allowanceGb = 60, double minFreeGb = 20)
    {
        var quota = new UploadQuota(Paths, Options.Create(new UploadsOptions { MaxMediaGbPerAccount = allowanceGb, MinFreeGb = minFreeGb }), _ => _freeBytes);
        return new MediaRetentionService(null!, _clips, _replays, _full, _vods, _leases, quota, null!,
            Options.Create(options ?? new MediaRetentionOptions()), Options.Create(new RiotOptions()), NullLogger<MediaRetentionService>.Instance);
    }

    private async Task<RetainedMatch> GameAsync(string id, string patch, DateTime endUtc, int fightClips = 2, int momentClips = 1, int clipMb = 1)
    {
        List<ClipWindow> windows = [];
        for (var i = 0; i < fightClips; i++) windows.Add(new ClipWindow(windows.Count, 0, 10, "fight", [], "fight", "Ally", "Ahri"));
        for (var i = 0; i < momentClips; i++) windows.Add(new ClipWindow(windows.Count, 0, 10, "kill", []));
        await _clips.SavePlanAsync(new ClipPlan(id, $"{patch}.712.1234", 1800, windows), CancellationToken.None);
        foreach (var window in windows) File.WriteAllBytes(_clips.ClipTargetPath(id, window.Index), new byte[clipMb * 1024 * 1024]);
        return new RetainedMatch(id, $"{patch}.712.1234", endUtc);
    }

    private void Replay(string id)
    {
        Directory.CreateDirectory(Path.Combine(_root, "replays"));
        File.WriteAllBytes(Path.Combine(_root, "replays", $"{id}.rofl"), new byte[1024]);
    }

    [Fact]
    public async Task Clips_expire_two_patches_back_and_the_plan_stays_to_explain_it()
    {
        var old = await GameAsync("EUW1_1", "16.16", new DateTime(2026, 8, 20));
        var previous = await GameAsync("EUW1_2", "16.17", new DateTime(2026, 9, 5));
        var current = await GameAsync("EUW1_3", "16.18", new DateTime(2026, 9, 20));

        var report = await Service().SweepAsync(Patches, [old, previous, current], CancellationToken.None);

        Assert.Equal(1, report.ClipMatchesExpired);
        Assert.False(_clips.HasClips(old.Id));
        Assert.True(_clips.HasClips(previous.Id));
        Assert.True(_clips.HasClips(current.Id));
        Assert.NotNull(await _clips.LoadPlanAsync(old.Id, CancellationToken.None));
        var expiry = _clips.Expiry(old.Id);
        Assert.Equal(("patch", "16.16", 3), (expiry!.Reason, expiry.Patch, expiry.Clips));
        Assert.Equal(3L * 1024 * 1024, report.BytesFreed);
    }

    [Fact]
    public async Task Kept_and_leased_matches_are_never_expired()
    {
        var kept = await GameAsync("EUW1_1", "16.15", new DateTime(2026, 8, 1));
        var leased = await GameAsync("EUW1_2", "16.15", new DateTime(2026, 8, 2));
        Assert.True(_clips.ToggleKeep(kept.Id, 10 * Gb).Kept);
        Assert.True(_leases.TryClaim($"clips:{leased.Id}", "agent"));

        var report = await Service().SweepAsync(Patches, [kept, leased], CancellationToken.None);

        Assert.Equal(0, report.ClipMatchesExpired);
        Assert.True(_clips.HasClips(kept.Id));
        Assert.True(_clips.HasClips(leased.Id));
    }

    [Fact]
    public async Task Replays_expire_as_soon_as_the_client_cannot_play_them()
    {
        var previous = await GameAsync("EUW1_1", "16.17", new DateTime(2026, 9, 5));
        var current = await GameAsync("EUW1_2", "16.18", new DateTime(2026, 9, 20));
        Replay(previous.Id);
        Replay(current.Id);

        var report = await Service().SweepAsync(Patches, [previous, current], CancellationToken.None);

        Assert.Equal(1, report.ReplaysExpired);
        Assert.Null(_replays.PathFor(previous.Id));
        Assert.NotNull(_replays.PathFor(current.Id));
        Assert.True(_clips.HasClips(previous.Id));
    }

    [Fact]
    public async Task Without_a_patch_list_nothing_expires_by_patch()
    {
        var old = await GameAsync("EUW1_1", "16.15", new DateTime(2026, 8, 1));
        Replay(old.Id);

        var report = await Service().SweepAsync(null, [old], CancellationToken.None);

        Assert.Equal((0, 0), (report.ClipMatchesExpired, report.ReplaysExpired));
        Assert.True(_clips.HasClips(old.Id));
    }

    [Fact]
    public async Task Personal_clips_are_reclaimed_once_footage_exists_and_fight_clips_stay()
    {
        var game = await GameAsync("EUW1_1", "16.18", new DateTime(2026, 9, 20), fightClips: 2, momentClips: 3);
        _vods.SaveLink(game.Id, "https://youtu.be/abc123");

        var report = await Service().SweepAsync(Patches, [game], CancellationToken.None);

        Assert.Equal(3, report.PersonalClipsReclaimed);
        Assert.NotNull(_clips.ClipPath(game.Id, 0));
        Assert.NotNull(_clips.ClipPath(game.Id, 1));
        Assert.Null(_clips.ClipPath(game.Id, 2));
        Assert.Null(_clips.Expiry(game.Id));
    }

    [Fact]
    public async Task Pressure_evicts_the_oldest_unkept_clips_until_the_disk_has_headroom_again()
    {
        var oldest = await GameAsync("EUW1_1", "16.18", new DateTime(2026, 9, 1), fightClips: 4, momentClips: 0, clipMb: 2);
        var kept = await GameAsync("EUW1_2", "16.18", new DateTime(2026, 9, 2), fightClips: 4, momentClips: 0, clipMb: 2);
        var newest = await GameAsync("EUW1_3", "16.18", new DateTime(2026, 9, 3), fightClips: 4, momentClips: 0, clipMb: 2);
        Assert.True(_clips.ToggleKeep(kept.Id, 10 * Gb).Kept);
        _freeBytes = 30 * Gb - 4 * 1024 * 1024;

        var report = await Service(new MediaRetentionOptions { PressureHeadroomGb = 10 }).SweepAsync(Patches, [oldest, kept, newest], CancellationToken.None);

        Assert.Equal(1, report.PressureEvictions);
        Assert.False(report.Starved);
        Assert.False(_clips.HasClips(oldest.Id));
        Assert.Equal("pressure", _clips.Expiry(oldest.Id)!.Reason);
        Assert.True(_clips.HasClips(kept.Id));
        Assert.True(_clips.HasClips(newest.Id));
    }

    [Fact]
    public async Task Pressure_relieves_the_account_allowance_too_and_reports_starvation()
    {
        var kept = await GameAsync("EUW1_1", "16.18", new DateTime(2026, 9, 1), fightClips: 4, momentClips: 0, clipMb: 2);
        Assert.True(_clips.ToggleKeep(kept.Id, 10 * Gb).Kept);
        var report = await Service(allowanceGb: 8.5 / 1024).SweepAsync(Patches, [kept], CancellationToken.None);

        Assert.True(report.Starved);
        Assert.Equal(0, report.PressureEvictions);
        Assert.True(_clips.HasClips(kept.Id));
    }

    [Fact]
    public async Task Pressure_takes_unkept_full_renders_before_any_clips()
    {
        var game = await GameAsync("EUW1_1", "16.18", new DateTime(2026, 9, 1), fightClips: 1, momentClips: 0, clipMb: 1);
        Directory.CreateDirectory(Path.Combine(_root, "fullgames"));
        File.WriteAllBytes(Path.Combine(_root, "fullgames", "EUW1_9.mp4"), new byte[8 * 1024 * 1024]);
        _freeBytes = 30 * Gb - 4 * 1024 * 1024;

        var report = await Service().SweepAsync(Patches, [game], CancellationToken.None);

        Assert.Equal(1, report.PressureEvictions);
        Assert.Null(_full.VideoPath("EUW1_9"));
        Assert.True(_clips.HasClips(game.Id));
    }

    [Fact]
    public async Task Keeping_is_bounded_by_the_kept_allowance()
    {
        var first = await GameAsync("EUW1_1", "16.18", new DateTime(2026, 9, 1), fightClips: 3, momentClips: 0, clipMb: 1);
        var second = await GameAsync("EUW1_2", "16.18", new DateTime(2026, 9, 2), fightClips: 3, momentClips: 0, clipMb: 1);

        Assert.True(_clips.ToggleKeep(first.Id, 4 * 1024 * 1024).Kept);
        var refused = _clips.ToggleKeep(second.Id, 4 * 1024 * 1024);

        Assert.False(refused.Kept);
        Assert.Contains("over the", refused.Error);
        Assert.Equal(3L * 1024 * 1024, _clips.KeptBytes());
    }

    [Fact]
    public async Task An_owner_retry_lifts_the_expiry()
    {
        var old = await GameAsync("EUW1_1", "16.15", new DateTime(2026, 8, 1));
        await Service().SweepAsync(Patches, [old], CancellationToken.None);
        Assert.NotNull(_clips.Expiry(old.Id));

        _clips.ClearExpiry(old.Id);
        _clips.DeleteClips(old.Id);

        Assert.Null(_clips.Expiry(old.Id));
        Assert.Null(await _clips.LoadPlanAsync(old.Id, CancellationToken.None));
    }
}

public class PatchReferenceTests
{
    [Fact]
    public void Versions_reduce_to_distinct_patches_newest_first()
    {
        var patches = PatchReference.Reduce(["16.18.1", "16.17.1", "16.17.0", "lolpatch_16.16", "16.16.1"]);
        Assert.Equal(["16.18", "16.17", "16.16"], patches);
    }

    [Fact]
    public void Game_versions_age_against_the_list_and_unknown_reads_as_current()
    {
        string[] patches = ["16.18", "16.17", "16.16"];
        Assert.Equal(0, PatchReference.PatchAge(patches, "16.18.712.1234"));
        Assert.Equal(1, PatchReference.PatchAge(patches, "16.17.700.1"));
        Assert.Equal(2, PatchReference.PatchAge(patches, "16.16.1"));
        Assert.Equal(0, PatchReference.PatchAge(patches, "16.19.5.1"));
        Assert.Equal(0, PatchReference.PatchAge(patches, ""));
    }
}
