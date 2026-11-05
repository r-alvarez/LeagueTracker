using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeagueTracker.Api.Tests;

public class ReplayUploadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly ReplayArchiveService _replays;

    public ReplayUploadTests()
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "A", TagLine = "B", DataDir = _root });
        _replays = new ReplayArchiveService(null!, null!, new DataPaths(context), NullLogger<ReplayArchiveService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private string Stage(string matchId, byte[] bytes)
    {
        var staging = _replays.UploadStagingPath(matchId)!;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, bytes);
        return staging;
    }

    [Fact]
    public async Task A_real_replay_lands_in_the_archive_and_clears_an_unavailable_mark()
    {
        await _replays.MarkUnavailableAsync("EUW1_1", "client stuck at checking", CancellationToken.None);
        var staging = Stage("EUW1_1", [.. "RIOT"u8, 0, 1, 2, 3]);

        Assert.True(await _replays.AcceptUploadAsync("EUW1_1", staging, CancellationToken.None));

        Assert.NotNull(_replays.PathFor("EUW1_1"));
        Assert.Contains("EUW1_1", _replays.ArchivedMatchIds());
        Assert.False(_replays.IsUnavailable("EUW1_1"));
        Assert.False(File.Exists(staging));
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 8, 0 })]
    [InlineData(new byte[] { (byte)'R', (byte)'I' })]
    [InlineData(new byte[0])]
    public async Task Anything_else_is_refused_and_leaves_no_file(byte[] bytes)
    {
        var staging = Stage("EUW1_2", bytes);

        Assert.False(await _replays.AcceptUploadAsync("EUW1_2", staging, CancellationToken.None));

        Assert.Null(_replays.PathFor("EUW1_2"));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task Ids_that_could_leave_the_folder_are_refused()
    {
        Assert.Null(_replays.UploadStagingPath("../EUW1_3"));
        Assert.False(await _replays.MarkUnavailableAsync("..\\x", "nope", CancellationToken.None));
        Assert.False(_replays.IsUnavailable("../EUW1_3"));
    }

    [Fact]
    public async Task An_unavailable_mark_is_not_counted_as_an_archived_replay()
    {
        await _replays.MarkUnavailableAsync("EUW1_4", "client says incompatible", CancellationToken.None);

        Assert.True(_replays.IsUnavailable("EUW1_4"));
        Assert.DoesNotContain("EUW1_4", _replays.ArchivedMatchIds());
    }
}
