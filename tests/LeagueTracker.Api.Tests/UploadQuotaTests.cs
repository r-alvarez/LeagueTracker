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

    private UploadQuota Quota(double maxMediaGb, double minFreeGb = 0, string? dataDir = null, Func<string, long>? freeSpace = null)
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "A", TagLine = "B", DataDir = dataDir ?? _root });
        return new UploadQuota(new DataPaths(context), Options.Create(new UploadsOptions { MaxMediaGbPerAccount = maxMediaGb, MinFreeGb = minFreeGb }), freeSpace);
    }

    // Review of D1, second pass: in-flight reservations were netted off the
    // disk per account, so two accounts could each take the same last bytes.
    [Fact]
    public void Two_accounts_cannot_both_reserve_the_same_disk_headroom()
    {
        const double oneGb = 1;
        var oneKilobyteAboveTheFloor = (long)(oneGb * 1024 * 1024 * 1024) + 1024;
        var first = Quota(maxMediaGb: 1000, minFreeGb: oneGb, dataDir: Path.Combine(_root, "a"), freeSpace: _ => oneKilobyteAboveTheFloor);
        var second = Quota(maxMediaGb: 1000, minFreeGb: oneGb, dataDir: Path.Combine(_root, "b"), freeSpace: _ => oneKilobyteAboveTheFloor);

        using (var held = first.Reserve(declaredBytes: null, fileCap: 4096))
        {
            Assert.Equal(1024, held.Budget);
            using var refused = second.Reserve(declaredBytes: 512, fileCap: 4096);
            Assert.Equal(UploadRefusal.Disk, refused.Refusal);
        }
        using var afterRelease = second.Reserve(declaredBytes: 512, fileCap: 4096);
        Assert.Null(afterRelease.Refusal);
    }

    private const double OneKilobyteInGb = 1024.0 / (1024 * 1024 * 1024);

    [Fact]
    public void Media_counts_everything_but_the_raw_games()
    {
        Directory.CreateDirectory(Path.Combine(_root, "vods", "EUW1_1"));
        Directory.CreateDirectory(Path.Combine(_root, "games"));
        File.WriteAllBytes(Path.Combine(_root, "vods", "EUW1_1", "vod.mp4"), new byte[600]);
        File.WriteAllBytes(Path.Combine(_root, "games", "EUW1_1.json"), new byte[1_000_000]);

        var quota = Quota(maxMediaGb: OneKilobyteInGb);
        using var refused = quota.Reserve(declaredBytes: 500, fileCap: 4096);
        Assert.Equal(UploadRefusal.Allowance, refused.Refusal);
        using var accepted = quota.Reserve(declaredBytes: 400, fileCap: 4096);
        Assert.Null(accepted.Refusal);
    }

    [Fact]
    public void A_nearly_full_disk_refuses_before_a_byte_is_written()
    {
        using var reservation = Quota(maxMediaGb: 1000, minFreeGb: 1e9).Reserve(0, 4096);
        Assert.Equal(UploadRefusal.Disk, reservation.Refusal);
    }

    // Review of D1: a body with no Content-Length passed the allowance check
    // as zero bytes and was then bounded by the file cap alone.
    [Fact]
    public async Task An_unknown_length_body_is_bounded_by_the_allowance_not_only_the_file_cap()
    {
        var quota = Quota(maxMediaGb: OneKilobyteInGb);
        using var reservation = quota.Reserve(declaredBytes: null, fileCap: 4096);
        Assert.Null(reservation.Refusal);
        Assert.Equal(1024, reservation.Budget);

        using var body = new MemoryStream(new byte[2048]);
        using var file = new MemoryStream();
        Assert.False(await UploadQuota.CopyWithinAsync(body, file, reservation.Budget, CancellationToken.None));
        Assert.Equal(UploadRefusal.Allowance, reservation.Exceeded);
    }

    [Fact]
    public void Concurrent_uploads_cannot_each_take_the_same_remaining_allowance()
    {
        var quota = Quota(maxMediaGb: OneKilobyteInGb);
        using (var first = quota.Reserve(declaredBytes: null, fileCap: 4096))
        {
            Assert.Equal(1024, first.Budget);
            using var second = quota.Reserve(declaredBytes: null, fileCap: 4096);
            Assert.Equal(UploadRefusal.Allowance, second.Refusal);
        }
        using var afterRelease = quota.Reserve(declaredBytes: null, fileCap: 4096);
        Assert.Null(afterRelease.Refusal);
    }

    [Fact]
    public async Task A_budgeted_stream_throws_past_its_budget()
    {
        using var inner = new MemoryStream(new byte[3000]);
        var stream = new BudgetedStream(inner, 2000);
        await Assert.ThrowsAsync<UploadBudgetExceededException>(() => stream.CopyToAsync(Stream.Null));
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
