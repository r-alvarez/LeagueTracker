using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class RecordingLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lt-ledger-{Guid.NewGuid():n}");
    private string MetaDir => Path.Combine(_root, RecordingLedger.MetaFolder);

    public RecordingLedgerTests() => Directory.CreateDirectory(MetaDir);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private void Sidecar(string name) => File.WriteAllText(Path.Combine(MetaDir, name), "");

    [Theory]
    [InlineData("Ruben - 26 Aug 2026 - Game 1.json")]
    [InlineData("Ruben - 26 Aug 2026 - Game 1.inflight.json")]
    [InlineData("Ruben - 26 Aug 2026 - Game 1.seg01.events.csv.gz")]
    [InlineData("Ruben - 26 Aug 2026 - Game 1.uploaded")]
    public void A_recording_with_any_sidecar_of_its_own_is_ours(string sidecar)
    {
        Sidecar(sidecar);
        Assert.True(RecordingLedger.IsOurs(MetaDir, "Ruben - 26 Aug 2026 - Game 1"));
    }

    [Fact]
    public void A_video_with_no_sidecar_is_not_ours()
    {
        Sidecar("Ruben - 26 Aug 2026 - Game 1.json");
        Assert.False(RecordingLedger.IsOurs(MetaDir, "2026-08-20 21-33-11"));   // an OBS file in the same folder
        Assert.False(RecordingLedger.IsOurs(MetaDir, "Ruben - 26 Aug 2026 - Game 10"));
        Assert.False(RecordingLedger.IsOurs(Path.Combine(_root, "missing"), "Ruben - 26 Aug 2026 - Game 1"));
    }

    [Fact]
    public void An_orphan_verdict_alone_does_not_make_a_video_ours()
    {
        Sidecar("2026-08-20 21-33-11.orphan");
        Assert.False(RecordingLedger.IsOurs(MetaDir, "2026-08-20 21-33-11"));
    }

    [Fact]
    public void A_folder_that_already_holds_videos_gets_a_subfolder()
    {
        var videos = Path.Combine(_root, "Videos");
        Directory.CreateDirectory(videos);
        File.WriteAllText(Path.Combine(videos, "holiday.mp4"), "");
        Assert.Equal(Path.Combine(videos, "LeagueTracker"), RecordingLedger.OwnedFolder(videos));
    }

    [Fact]
    public void An_empty_folder_or_one_with_our_ledger_is_used_as_is()
    {
        var empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(empty, RecordingLedger.OwnedFolder(empty));
        File.WriteAllText(Path.Combine(_root, "old.mp4"), "");
        Assert.Equal(_root, RecordingLedger.OwnedFolder(_root));   // has metadata/ already
        Assert.Equal(Path.Combine(_root, "new"), RecordingLedger.OwnedFolder(Path.Combine(_root, "new")));
    }
}
