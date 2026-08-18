using ScreenRecorderLib;

namespace LeagueTracker.RenderAgent;

/// Plan-B capture engine (CaptureBackend "wgc"): Windows Graphics Capture
/// through ScreenRecorderLib's Media Foundation pipeline - the same class of
/// capture OBS (and therefore Ascent) uses. WGC reads the DWM-composited
/// desktop, so the exclusive-fullscreen display mode switches that tear down
/// a Desktop Duplication session (and with it ffmpeg's ddagrab) do not
/// interrupt it. Records video ONLY: game-process audio keeps coming from
/// ProcessAudioCapture (game-only sound, which whole-desktop loopback can't
/// promise), written as paced PCM beside the segment and muxed at finalize.
public sealed class WgcRecorder : IDisposable
{
    /// Our ScreenRecorderLib build (deploy/screenrecorderlib) tone-maps HDR
    /// desktops to SDR unless this process variable says not to - the
    /// library has no API surface for it, the environment is the switch.
    public static void ConfigureHdrToneMap(bool enabled) =>
        Environment.SetEnvironmentVariable("SCREENRECORDERLIB_HDR_TONEMAP", enabled ? null : "0");

    private readonly Recorder _recorder;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// Set as soon as the engine reports failure - segment supervision polls
    /// this between clock samples.
    public string? Error { get; private set; }

    public bool HasEnded => _done.Task.IsCompleted;

    /// Frames rendered into the output so far - the segment supervisor's
    /// aliveness signal. File growth alone can't tell a dead engine from a
    /// visually quiet stretch (quality-mode H264 writes almost nothing when
    /// little moves on screen); the frame counter advances either way.
    public int CurrentFrameNumber => _recorder.CurrentFrameNumber;

    private WgcRecorder((int X, int Y, int Width, int Height)? rect, int framerate, int quality)
    {
        var source = DisplayRecordingSource.MainMonitor;
        source.RecorderApi = RecorderApi.WindowsGraphicsCapture;
        source.IsCursorCaptureEnabled = true;
        source.IsBorderRequired = false; // no yellow capture border over the game
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                SourceRect = rect is { } r ? new ScreenRect(r.X, r.Y, r.Width, r.Height) : null,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    EncoderProfile = H264Profile.High,
                },
                Quality = quality,
                Framerate = framerate,
                IsHardwareEncodingEnabled = true,
                // Fragmented for the same reason the ffmpeg path writes
                // +frag_keyframe: a crash costs seconds, not the game.
                // Finalize remuxes to faststart.
                IsFragmentedMp4Enabled = true,
                IsMp4FastStartEnabled = false,
            },
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
        };
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnStatusChanged += (_, e) =>
        {
            if (e.Status == RecorderStatus.Recording) _started.TrySetResult();
        };
        _recorder.OnRecordingFailed += (_, e) =>
        {
            Error = e.Error is { Length: > 0 } msg ? msg : "recording failed";
            _started.TrySetResult();
            _done.TrySetResult(Error);
        };
        _recorder.OnRecordingComplete += (_, _) => _done.TrySetResult(null);
    }

    /// Null when the engine won't construct at all (missing OS support) -
    /// the caller falls back to the ffmpeg capture path.
    public static WgcRecorder? TryStart(string outputPath, (int X, int Y, int Width, int Height)? rect, int framerate, int quality)
    {
        WgcRecorder? recorder = null;
        try
        {
            recorder = new WgcRecorder(rect, framerate, quality);
            recorder._recorder.Record(outputPath);
            return recorder;
        }
        catch (Exception ex)
        {
            Log.Warn($"WGC capture unavailable: {ex.Message}");
            recorder?.Dispose();
            return null;
        }
    }

    /// True once frames are actually flowing; false = failed or timed out.
    public async Task<bool> WaitForRecordingAsync(TimeSpan timeout)
    {
        await Task.WhenAny(_started.Task, Task.Delay(timeout));
        return _started.Task.IsCompleted && Error is null;
    }

    /// Graceful stop, waiting for the muxer to flush. Returns the engine's
    /// error if the recording ended in failure, null on a clean stop.
    public async Task<string?> StopAsync(TimeSpan timeout)
    {
        try { _recorder.Stop(); } catch { /* already stopped */ }
        await Task.WhenAny(_done.Task, Task.Delay(timeout));
        return _done.Task.IsCompleted ? _done.Task.Result : "recorder did not stop in time";
    }

    public void Dispose()
    {
        try { _recorder.Stop(); } catch { /* already stopped */ }
        _done.TrySetResult("disposed");
    }
}
