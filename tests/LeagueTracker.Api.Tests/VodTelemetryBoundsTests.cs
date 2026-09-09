using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// Audit N16: any enrolled recorder key can upload telemetry, and one event
// stamped ten days in used to allocate 86k buckets.
public class VodTelemetryBoundsTests : IDisposable
{
    private const string MatchId = "EUW1_1";
    private const double GameSec = 1800;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private VodService Vods()
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "Me", TagLine = "EUW", Platform = "euw1", Region = "europe", DataDir = _root });
        return new VodService(new DataPaths(context));
    }

    private static MemoryStream Gzip(IEnumerable<string> lines, bool header = true)
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            if (header) writer.WriteLine("t_ms,event_type,input_name,value_a,value_b");
            foreach (var line in lines) writer.WriteLine(line);
        }
        compressed.Position = 0;
        return compressed;
    }

    private static IEnumerable<string> Actions(int count, long spacingMs = 100) =>
        Enumerable.Range(0, count).Select(i => $"{i * spacingMs},key_down,Q,0,0");

    private Task<VodService.TelemetryRejection?> Store(Stream body) =>
        Vods().StoreTelemetryAsync(MatchId, body, GameSec, CancellationToken.None);

    [Fact]
    public async Task A_games_worth_of_telemetry_is_stored_with_its_apm_series()
    {
        var vods = Vods();
        Assert.Null(await vods.StoreTelemetryAsync(MatchId, Gzip(Actions(600)), GameSec, CancellationToken.None));

        Assert.NotNull(vods.EventsPath(MatchId));
        var apm = (JsonElement)vods.ApmSeries(MatchId)!;
        Assert.Equal(6, apm.GetProperty("apm").GetArrayLength());
        Assert.Equal(600, apm.GetProperty("averageApm").GetInt32());
    }

    [Fact]
    public async Task A_timestamp_past_the_recording_is_rejected()
    {
        var tenDaysMs = 10L * 24 * 3600 * 1000;
        var rejection = await Store(Gzip([$"{tenDaysMs},key_down,Q,0,0"]));
        Assert.Contains("outside the recording", rejection!.Error);
        Assert.Null(Vods().EventsPath(MatchId));
    }

    [Fact]
    public async Task A_negative_timestamp_is_rejected()
    {
        var rejection = await Store(Gzip(["-5,key_down,Q,0,0"]));
        Assert.Contains("outside the recording", rejection!.Error);
    }

    [Fact]
    public async Task A_line_without_a_timestamp_is_rejected()
    {
        var rejection = await Store(Gzip(["soon,key_down,Q,0,0"]));
        Assert.Contains("no timestamp", rejection!.Error);
    }

    [Fact]
    public async Task Telemetry_without_the_header_is_rejected()
    {
        var rejection = await Store(Gzip(["100,key_down,Q,0,0"], header: false));
        Assert.Contains("t_ms header", rejection!.Error);
    }

    [Fact]
    public async Task More_lines_than_a_recording_can_hold_are_rejected()
    {
        var limit = (int)((GameSec + VodService.TelemetrySlackSec) * 1000);
        var rejection = await Store(Gzip(Enumerable.Range(0, limit + 1).Select(i => $"{i % 1000},mouse_move,,{i},0")));
        Assert.Contains("more than", rejection!.Error);
        Assert.Contains("lines", rejection.Error);
    }

    [Fact]
    public async Task More_actions_than_hands_can_make_are_rejected()
    {
        var limit = (int)((GameSec + VodService.TelemetrySlackSec) * 20);
        var rejection = await Store(Gzip(Actions(limit + 1, spacingMs: 1)));
        Assert.Contains("actions", rejection!.Error);
    }

    [Fact]
    public async Task An_upload_over_the_compressed_cap_is_cut_off()
    {
        var oversized = new byte[VodService.MaxTelemetryCompressedBytes + 1];
        Random.Shared.NextBytes(oversized);
        var rejection = await Store(new MemoryStream(oversized));
        Assert.Contains("exceeds", rejection!.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "vods", MatchId)));
    }

    [Fact]
    public async Task A_gzip_bomb_is_cut_off_at_the_expanded_cap()
    {
        // Long valid lines: short ones would trip the line-count bound first.
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.ASCII.GetBytes("t_ms,event_type,input_name,value_a,value_b\n"));
            var line = Encoding.ASCII.GetBytes($"0,mouse_move,{new string('x', 180)},0,0\n");
            var chunk = Enumerable.Repeat(line, 4096).SelectMany(b => b).ToArray();
            for (long written = 0; written <= VodService.MaxTelemetryExpandedBytes; written += chunk.Length) gzip.Write(chunk);
        }
        compressed.Position = 0;
        var rejection = await Store(compressed);
        Assert.Contains("exceeds", rejection!.Error);
    }

    [Fact]
    public async Task A_newline_free_bomb_is_cut_off_at_the_line_length()
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(new byte[1024 * 1024]);
        }
        compressed.Position = 0;
        var rejection = await Store(compressed);
        Assert.Contains("line longer", rejection!.Error);
    }

    [Fact]
    public async Task Bytes_that_are_not_gzip_are_rejected()
    {
        var rejection = await Store(new MemoryStream(Encoding.ASCII.GetBytes("t_ms,event_type\n100,key_down\n")));
        Assert.Contains("gzip", rejection!.Error);
    }

    [Fact]
    public async Task A_bad_match_id_never_touches_the_disk()
    {
        var rejection = await Vods().StoreTelemetryAsync("../etc", Gzip(Actions(1)), GameSec, CancellationToken.None);
        Assert.NotNull(rejection);
        Assert.False(Directory.Exists(Path.Combine(_root, "vods")));
    }
}
