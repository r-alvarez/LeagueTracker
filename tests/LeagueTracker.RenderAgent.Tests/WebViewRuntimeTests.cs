using LeagueTracker.RenderAgent.Review;

namespace LeagueTracker.RenderAgent.Tests;

public sealed class WebViewRuntimeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("broken", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("152.0.4191.66", true)]
    public void Runtime_registration_requires_a_real_version(string? value, bool expected) =>
        Assert.Equal(expected, WebViewRuntime.ValidVersion(value));

    [Fact]
    public async Task Existing_runtime_skips_installation()
    {
        var result = await WebViewRuntime.EnsureAsync(() => true, _ => throw new InvalidOperationException("Must not run"), CancellationToken.None);
        Assert.True(result.Available);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(3010, true, true)]
    public async Task Registration_after_setup_determines_success(int exitCode, bool registered, bool expected)
    {
        var available = false;
        var result = await WebViewRuntime.EnsureAsync(() => available, _ =>
        {
            available = registered;
            return Task.FromResult(exitCode);
        }, CancellationToken.None);
        Assert.Equal(expected, result.Available);
        Assert.Equal(expected, result.Error is null);
    }

    [Fact]
    public async Task Failed_launch_can_be_retried()
    {
        var failure = await WebViewRuntime.EnsureAsync(() => false, _ => throw new IOException("Unavailable"), CancellationToken.None);
        Assert.False(failure.Available);
        var available = false;
        var retry = await WebViewRuntime.EnsureAsync(() => available, _ => { available = true; return Task.FromResult(0); }, CancellationToken.None);
        Assert.True(retry.Available);
    }

    [Fact]
    public async Task Closing_review_cancels_the_wait()
    {
        using var cancel = new CancellationTokenSource();
        var task = WebViewRuntime.EnsureAsync(() => false, async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Installer_timeout_reports_a_recoverable_failure()
    {
        var result = await WebViewRuntime.EnsureAsync(() => false, _ => throw new OperationCanceledException(), CancellationToken.None);
        Assert.False(result.Available);
        Assert.NotNull(result.Error);
    }
}
