using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

public class UploadQuotaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private UploadQuota Quota(double maxMediaGb, double minFreeGb = 0)
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "A", TagLine = "B", DataDir = _root });
        return new UploadQuota(new DataPaths(context), Options.Create(new UploadsOptions { MaxMediaGbPerAccount = maxMediaGb, MinFreeGb = minFreeGb }));
    }

    [Fact]
    public void Media_counts_everything_but_the_raw_games()
    {
        Directory.CreateDirectory(Path.Combine(_root, "vods", "EUW1_1"));
        Directory.CreateDirectory(Path.Combine(_root, "games"));
        File.WriteAllBytes(Path.Combine(_root, "vods", "EUW1_1", "vod.mp4"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(_root, "games", "EUW1_1.json"), new byte[1_000_000]);

        var oneKilobyteAllowance = Quota(maxMediaGb: 3000.0 / (1024 * 1024 * 1024));
        Assert.Contains("allowance", oneKilobyteAllowance.Refusal(incomingBytes: 1500));
        Assert.Null(oneKilobyteAllowance.Refusal(incomingBytes: 500));
    }

    [Fact]
    public void A_nearly_full_disk_refuses_before_a_byte_is_written()
    {
        Assert.Contains("disk", Quota(maxMediaGb: 1000, minFreeGb: 1e9).Refusal(0));
    }

    [Fact]
    public async Task A_body_over_the_cap_is_reported_and_not_written_past_it()
    {
        using var body = new MemoryStream(new byte[300_000]);
        using var file = new MemoryStream();
        Assert.False(await UploadQuota.CopyWithinAsync(body, file, cap: 200_000, CancellationToken.None));
        Assert.True(file.Length <= 200_000);

        body.Position = 0;
        using var roomy = new MemoryStream();
        Assert.True(await UploadQuota.CopyWithinAsync(body, roomy, cap: 300_000, CancellationToken.None));
        Assert.Equal(300_000, roomy.Length);
    }
}
