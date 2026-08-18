using System.Diagnostics;
using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class UploadThrottleTests
{
    private static string ScratchMemory() => Path.Combine(Path.GetTempPath(), $"lt-throttle-{Guid.NewGuid():n}.json");

    [Fact]
    public void Out_of_game_is_unlimited()
    {
        var throttle = new UploadThrottle(new AgentConfig(), inGame: () => false, ScratchMemory());
        Assert.Null(throttle.CapBytesPerSecond);
    }

    [Fact]
    public void In_game_with_no_measurement_uses_the_default_pace()
    {
        var throttle = new UploadThrottle(new AgentConfig(), inGame: () => true, ScratchMemory());
        Assert.Equal(6_000_000 / 8, throttle.CapBytesPerSecond);
    }

    [Fact]
    public void A_configured_in_game_rate_wins_over_the_measurement()
    {
        var throttle = new UploadThrottle(new AgentConfig { UploadInGameMbps = 12 }, inGame: () => true, ScratchMemory());
        Assert.Equal(12_000_000 / 8, throttle.CapBytesPerSecond);
    }

    [Fact]
    public async Task An_unthrottled_transfer_teaches_the_idle_rate_and_in_game_takes_half_of_it()
    {
        var memory = ScratchMemory();
        var inGame = false;
        var throttle = new UploadThrottle(new AgentConfig(), () => inGame, memory);
        var sink = new PacedSink(bytesPerSecond: 40_000_000);
        await throttle.WriteAsync(sink, new byte[8 * 1024 * 1024], CancellationToken.None);
        Assert.NotNull(throttle.IdleMbps);
        Assert.InRange(throttle.IdleMbps!.Value, 200, 400);   // 40 MB/s = 320 Mbps, give or take the timer

        inGame = true;
        var cap = throttle.CapBytesPerSecond!.Value;
        Assert.InRange(cap, 12_000_000, 25_000_000);           // about half of ~40 MB/s
        Assert.True(File.Exists(memory), "the idle rate is remembered on disk");
    }

    [Fact]
    public async Task In_game_writes_are_paced_to_the_cap()
    {
        var throttle = new UploadThrottle(new AgentConfig { UploadInGameMbps = 16 }, inGame: () => true, ScratchMemory());
        var clock = Stopwatch.StartNew();
        await throttle.WriteAsync(Stream.Null, new byte[1024 * 1024], CancellationToken.None); // 1 MB at 2 MB/s = 0.5 s
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.4, 2.0);
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1", "dQw4w9WgXcQ")]
    [InlineData("https://example.com/video", null)]
    public void Video_ids_are_read_from_both_link_shapes(string url, string? expected)
        => Assert.Equal(expected, YouTubeUploader.VideoIdOf(url));

    // Stands in for a socket whose send buffer fills at the line rate - the
    // only way the throttle can measure anything.
    private sealed class PacedSink(long bytesPerSecond) : Stream
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _accepted;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _accepted += buffer.Length;
            var due = TimeSpan.FromSeconds((double)_accepted / bytesPerSecond) - _clock.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due, ct);
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _accepted;
        public override long Position { get => _accepted; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
