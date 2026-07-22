using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// Records live games, Ascent-style: when the local player enters a real game
/// (LCU gameflow "InProgress" - replay renders are "WatchInProgress" and never
/// trigger this), the desktop is captured cropped to the game window and
/// encoded on the GPU (NVENC), so playing cost is a video encode the graphics
/// card does on the side. Recording runs to a fragmented .part.mp4 - playable
/// even if the agent dies mid-game - and is remuxed to a faststart .mp4 when
/// the game ends. A sidecar .json carries what the review UI needs: the match
/// id, queue, who played, and a video-time -> game-clock map sampled from the
/// Live Client API while recording.
public sealed class GameRecorder(AgentConfig config, string ffmpeg, string leagueRoot)
{
    private const string GameProcessName = "League of Legends";

    /// Phases where a game is imminent - poll fast so recording starts with
    /// the loading screen, not a minute into laning.
    private static readonly string[] NearGamePhases =
        ["Lobby", "Matchmaking", "ReadyCheck", "ChampSelect", "GameStart"];

    private readonly List<TrackerClient> _trackers =
        [.. config.ServerUrls.Select(url => new TrackerClient(url, config))];

    private DateTime _lastSweep;

    private readonly HttpClient _liveClient = new(new HttpClientHandler
    {
        // Same self-signed local cert as the Replay API (same port, in fact).
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    public string RecordingsDir => config.RecordingsDir is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LeagueTracker");

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RecordingsDir);
        FinalizeOrphans();
        Log.Info($"Game recorder on - live games land in {RecordingsDir} ({config.RecordFramerate}fps, NVENC cq {config.RecordQuality})");
        _lastSweep = DateTime.UtcNow;
        try { await SweepUnuploadedAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"VOD upload sweep failed: {ex.Message}"); }

        while (!ct.IsCancellationRequested)
        {
            if (RenderAgent.StopRequested) { Log.Info("stop.requested found - recorder exiting"); return; }
            try
            {
                var phase = await PhaseAsync(ct);
                if (phase == "InProgress")
                {
                    var gaveUp = !await RecordGameAsync(ct);
                    if (gaveUp)
                    {
                        // Capture is deterministically broken for this game
                        // (encoder init, window on a display ddagrab can't
                        // reach) - retrying every pass would spam ffmpeg
                        // launches all game, so sit it out.
                        Log.Warn("Recording gave up on this game - waiting for it to end");
                        while (!RenderAgent.StopRequested && await PhaseAsync(ct) == "InProgress") await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    }
                    continue;
                }
                // Idle moments double as upload retry windows: a VOD recorded
                // before its match was imported (the poller lags the game by
                // minutes) gets delivered on one of these passes.
                if (!NearGamePhases.Contains(phase) && DateTime.UtcNow - _lastSweep > TimeSpan.FromMinutes(10))
                {
                    _lastSweep = DateTime.UtcNow;
                    await SweepUnuploadedAsync(ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(NearGamePhases.Contains(phase) ? 3 : 15), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"Recorder pass failed: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// One game, start to finish. False = capture could not be made to work
    /// for this game (as opposed to the game simply ending).
    private async Task<bool> RecordGameAsync(CancellationToken ct)
    {
        // Match identity first: the gameflow session knows the game id before
        // the game window even exists. Platform + id is the same match id the
        // trackers use, which is what ties the VOD to its match page later.
        LcuClient.GameSession? session = null;
        if (LcuClient.TryConnect(leagueRoot) is { } lcu)
        {
            using (lcu) session = await lcu.GetGameSessionAsync(ct);
        }

        if (!ShouldRecord(session, out var skipReason))
        {
            Log.Info($"Not recording this game: {skipReason}");
            // StopRequested too: a deploy must not wait for the game to end.
            while (!RenderAgent.StopRequested && await PhaseAsync(ct) == "InProgress") await Task.Delay(TimeSpan.FromSeconds(15), ct);
            return true;
        }

        var game = await WaitForGameWindowAsync(ct);
        if (game is not { } g) return true; // no window (yet) - not a capture defect, retry next pass

        using var process = g.Process;
        var matchId = session is { PlatformId.Length: > 0 } s ? $"{s.PlatformId}_{s.GameId}" : null;
        var baseName = $"{DateTime.Now:yyyy-MM-dd_HH-mm}{(matchId is null ? "" : $"_{matchId}")}";
        var partPath = Path.Combine(RecordingsDir, $"{baseName}.part.mp4");
        Log.Info($"Recording game {matchId ?? "(id unknown)"}: {g.Rect.Width}x{g.Rect.Height} -> {baseName}.mp4");

        // NVENC first; if ffmpeg dies straight away (driver/session limit),
        // fall back to CPU x264 rather than losing the VOD.
        var eventsPath = Path.Combine(RecordingsDir, $"{baseName}.events.csv.gz");
        var result = await CaptureAsync(partPath, eventsPath, g, nvenc: true, ct);
        if (result is { FfmpegFailedEarly: true })
        {
            Log.Warn("NVENC capture failed at startup - falling back to CPU encoding");
            result = await CaptureAsync(partPath, eventsPath, g, nvenc: false, ct);
        }
        if (result is { FfmpegFailedEarly: true })
        {
            Log.Error($"Capture would not start: {result.StderrTail}");
            try { if (File.Exists(eventsPath)) File.Delete(eventsPath); } catch { /* telemetry without video is noise */ }
            return false;
        }

        var finalPath = Path.Combine(RecordingsDir, $"{baseName}.mp4");
        await FinalizeAsync(partPath, finalPath, ct);
        WriteSidecar(Path.Combine(RecordingsDir, $"{baseName}.json"), baseName, session, g, result!);
        Log.Info($"Recording complete: {baseName}.mp4 ({result!.Duration.TotalMinutes:0} min)");
        if (matchId is not null) await TryUploadVodAsync(matchId, baseName, ct);
        return true;
    }

    /// Offers the VOD to each tracker until the one owning the match takes
    /// it (one agent, several account instances). A .uploaded stamp next to
    /// the mp4 keeps the startup sweep from re-sending gigabytes; failure
    /// just leaves the stamp missing, and the next agent start retries.
    private async Task TryUploadVodAsync(string matchId, string baseName, CancellationToken ct)
    {
        string P(string ext) => Path.Combine(RecordingsDir, baseName + ext);
        foreach (var tracker in _trackers)
        {
            try
            {
                if (!await tracker.UploadVodAsync(matchId, P(".mp4"),
                        File.Exists(P(".json")) ? P(".json") : null,
                        File.Exists(P(".events.csv.gz")) ? P(".events.csv.gz") : null,
                        File.Exists(P(".jpg")) ? P(".jpg") : null, ct))
                {
                    continue; // tracker doesn't know this match - not its account
                }
                File.WriteAllText(P(".uploaded"), tracker.ServerUrl);
                Log.Info($"VOD {matchId} uploaded to {tracker.ServerUrl}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"VOD upload to {tracker.ServerUrl} failed: {ex.Message} (retried at next agent start)");
            }
        }
        // No tracker knew the match: normal for a brand-new game - the
        // poller imports it within minutes, and the startup sweep (or the
        // post-game re-try below) delivers it then.
        Log.Info($"VOD {matchId}: no tracker accepted it yet (match not imported) - will retry");
    }

    /// Recordings whose upload never landed (tracker down, match not yet
    /// imported, agent killed): retried at startup, oldest first.
    private async Task SweepUnuploadedAsync(CancellationToken ct)
    {
        foreach (var sidecar in Directory.EnumerateFiles(RecordingsDir, "*.json").OrderBy(f => f))
        {
            var baseName = Path.GetFileNameWithoutExtension(sidecar);
            if (File.Exists(Path.Combine(RecordingsDir, baseName + ".uploaded"))) continue;
            if (!File.Exists(Path.Combine(RecordingsDir, baseName + ".mp4"))) continue;
            string? matchId = null;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, ct));
                matchId = doc.RootElement.TryGetProperty("matchId", out var id) ? id.GetString() : null;
            }
            catch
            {
                continue;
            }
            if (matchId is { Length: > 0 }) await TryUploadVodAsync(matchId, baseName, ct);
        }
    }

    private sealed record GameWindowInfo(Process Process, (int X, int Y, int Width, int Height) Rect);

    /// Riot queue ids -> the config categories of RecordQueues. Kept to the
    /// queues Riot actually runs; retired ids are harmless to keep.
    private static readonly Dictionary<long, string> QueueCategories = new()
    {
        [420] = "ranked-solo",
        [440] = "ranked-flex",
        [400] = "normal", [430] = "normal", [480] = "normal", [490] = "normal",
        [450] = "aram",
        [700] = "clash", [720] = "clash",
        [830] = "coop-ai", [840] = "coop-ai", [850] = "coop-ai",
        [870] = "coop-ai", [880] = "coop-ai", [890] = "coop-ai",
        [900] = "urf", [1900] = "urf",
        [1300] = "nexus-blitz",
        [1700] = "arena", [1710] = "arena",
        [2300] = "brawl",
        [950] = "doom-bots", [960] = "doom-bots",
        [0] = "custom",      // custom lobbies
        [3140] = "custom",   // Practice Tool (own queue id since ~2026, observed live)
    };

    private bool ShouldRecord(LcuClient.GameSession? session, out string skipReason)
    {
        skipReason = "";
        var enabled = config.RecordQueues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.ToLowerInvariant()).ToHashSet();
        if (enabled.Contains("all")) return true;
        // No session = nothing to classify by; record rather than risk losing
        // a game that was wanted (the sidecar just lacks a match id too).
        if (session is null) return true;
        var category = QueueCategories.GetValueOrDefault(session.QueueId, "other");
        // Unmapped queue but a mode name that identifies it: trust the mode.
        if (category is "other" && session.GameMode is "PRACTICETOOL") category = "custom";
        if (category is "other" && session.GameMode is "ARAM") category = "aram";
        if (category is "other" && session.GameMode is "URF" or "ARURF") category = "urf";
        if (enabled.Contains(category)) return true;
        skipReason = $"queue {session.QueueId} ({category}, {session.GameMode ?? "?"}) is not in RecordQueues ({config.RecordQueues})";
        return false;
    }

    private sealed record CaptureResult(
        bool FfmpegFailedEarly, string StderrTail, DateTime StartedUtc, TimeSpan Duration,
        string Encoder, List<(double VideoSec, double GameSec)> ClockMap, string? ActivePlayer);

    /// The client starts the game on its own schedule; the window exists from
    /// the loading screen on. When a replay render happens to overlap a live
    /// game there are two identically-named processes - the live game is the
    /// newer one, because renders never start while the player is in the flow.
    private async Task<GameWindowInfo?> WaitForGameWindowAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await PhaseAsync(ct) is not "InProgress") return null;

            var procs = Process.GetProcessesByName(GameProcessName);
            var newest = procs.OrderByDescending(SafeStartTime).FirstOrDefault();
            foreach (var other in procs.Where(p => !ReferenceEquals(p, newest))) other.Dispose();
            if (newest is not null)
            {
                newest.Refresh();
                if (newest.MainWindowHandle is not 0 &&
                    GameWindow.ClientRectOf(newest.MainWindowHandle) is { Width: >= 320, Height: >= 200 } rect)
                {
                    if (!GameWindow.IsOnPrimaryDisplay(rect))
                    {
                        Log.Warn("Game window is not on the primary display - ddagrab captures the primary, this may fail");
                    }
                    return new GameWindowInfo(newest, rect);
                }
                newest.Dispose();
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        Log.Warn("Game window did not appear within 3 minutes");
        return null;

        static DateTime SafeStartTime(Process p)
        {
            try { return p.StartTime; } catch { return DateTime.MinValue; }
        }
    }

    private async Task<CaptureResult?> CaptureAsync(string partPath, string eventsPath, GameWindowInfo g, bool nvenc, CancellationToken ct)
    {
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        var width = g.Rect.Width & ~1;   // encoders need even dimensions
        var height = g.Rect.Height & ~1;
        var input = $"-y -f lavfi -i ddagrab=framerate={fps}:offset_x={Math.Max(0, g.Rect.X)}:offset_y={Math.Max(0, g.Rect.Y)}:video_size={width}x{height}";
        // ddagrab hands out D3D11 frames; NVENC eats them on the GPU without a
        // round-trip through system memory - that is the whole low-overhead
        // trick. The CPU fallback has to download frames first.
        var encode = nvenc
            ? $"-c:v h264_nvenc -preset p4 -rc vbr -cq {config.RecordQuality} -b:v 0 -maxrate 25M -bufsize 50M -g {fps * 4}"
            : "-vf hwdownload,format=bgra -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p";
        // Fragmented mp4: every fragment is self-contained, so a crash or
        // power cut costs seconds, not the whole game. FinalizeAsync remuxes
        // to a normal faststart mp4 for clean browser playback.
        var args = $"{input} {encode} -movflags +frag_keyframe+empty_moov -f mp4 \"{partPath}\"";

        using var proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,   // 'q' on stdin = ffmpeg's graceful stop, which flushes the muxer
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");

        var stderrTail = DrainStderrAsync(proc.StandardError);
        // Hooks live exactly as long as the capture, so event t_ms and video
        // time share a zero point (within ffmpeg's first-frame latency).
        using var inputLogger = config.RecordInputs ? InputLogger.TryStart(eventsPath, g.Process.MainWindowHandle) : null;
        var startedUtc = DateTime.UtcNow;
        var clockMap = new List<(double, double)>();
        string? activePlayer = null;
        var lastClockSample = DateTime.MinValue;

        try
        {
            while (!proc.HasExited)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; } // agent shutdown - stop cleanly below

                g.Process.Refresh();
                if (g.Process.HasExited) break;

                // A deploy's stop request ends the recording cleanly (the VOD
                // up to here survives) rather than orphaning the capture.
                if (RenderAgent.StopRequested) break;

                // ffmpeg dying this early is an encoder/capture init problem,
                // not a game event - report it so the caller can fall back.
                if (proc.HasExited && DateTime.UtcNow - startedUtc < TimeSpan.FromSeconds(8)) break;

                if (DateTime.UtcNow - lastClockSample > TimeSpan.FromSeconds(30))
                {
                    lastClockSample = DateTime.UtcNow;
                    if (await GameTimeAsync(ct) is { } gameSec)
                    {
                        clockMap.Add(((DateTime.UtcNow - startedUtc).TotalSeconds, gameSec));
                    }
                    activePlayer ??= await ActivePlayerAsync(ct);
                    // Every 3rd sample (~90s) also re-check the phase: the
                    // post-game screen keeps the process alive briefly, and
                    // there is nothing worth recording past "InProgress".
                    if (clockMap.Count % 3 == 0 && await PhaseAsync(CancellationToken.None) is not "InProgress" and not null) break;
                }
            }
        }
        finally
        {
            if (!proc.HasExited)
            {
                try
                {
                    await proc.StandardInput.WriteAsync("q");
                    await proc.StandardInput.FlushAsync(CancellationToken.None);
                    if (!proc.WaitForExit(TimeSpan.FromSeconds(15))) proc.Kill();
                }
                catch { try { proc.Kill(); } catch { /* already gone */ } }
            }
        }

        var duration = DateTime.UtcNow - startedUtc;
        var failedEarly = duration < TimeSpan.FromSeconds(8) && proc.ExitCode != 0;
        return new CaptureResult(failedEarly, Tail(await stderrTail), startedUtc, duration,
            nvenc ? "h264_nvenc" : "libx264", clockMap, activePlayer);
    }

    /// Remux the fragmented recording into a normal faststart mp4 (stream
    /// copy - milliseconds per gigabyte, no re-encode), plus a mid-game
    /// thumbnail for the library view.
    private async Task FinalizeAsync(string partPath, string finalPath, CancellationToken ct)
    {
        try
        {
            await RunFfmpegAsync($"-y -i \"{partPath}\" -c copy -movflags +faststart \"{finalPath}\"", ct);
            File.Delete(partPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The fragmented original is still playable - keep it, and say so.
            Log.Warn($"Remux failed ({ex.Message}) - keeping the fragmented recording at {partPath}");
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);
        }

        var thumb = Path.ChangeExtension(finalPath, ".jpg");
        foreach (var seek in new[] { 600, 60, 2 })  // mid-game if it lasted, else whatever exists
        {
            try
            {
                await RunFfmpegAsync($"-y -ss {seek} -i \"{finalPath}\" -frames:v 1 -vf scale=640:-1 \"{thumb}\"", ct);
                if (new FileInfo(thumb) is { Exists: true, Length: > 0 }) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
    }

    private void WriteSidecar(string path, string baseName, LcuClient.GameSession? session, GameWindowInfo g, CaptureResult result)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                videoFile = $"{baseName}.mp4",
                matchId = session is { PlatformId.Length: > 0 } ? $"{session.PlatformId}_{session.GameId}" : null,
                eventsFile = File.Exists(Path.Combine(RecordingsDir, $"{baseName}.events.csv.gz")) ? $"{baseName}.events.csv.gz" : null,
                gameId = session?.GameId,
                platformId = session?.PlatformId,
                queueId = session?.QueueId,
                gameMode = session?.GameMode,
                activePlayer = result.ActivePlayer,
                recordingStartUtc = result.StartedUtc,
                recordingEndUtc = result.StartedUtc + result.Duration,
                width = g.Rect.Width & ~1,
                height = g.Rect.Height & ~1,
                fps = Math.Clamp(config.RecordFramerate, 15, 120),
                encoder = result.Encoder,
                // videoSec -> gameSec samples; the review UI maps timeline
                // events onto the video with these (one pair would do, but
                // samples over the whole game absorb any drift).
                clockMap = result.ClockMap.Select(p => new { videoSec = Math.Round(p.VideoSec, 1), gameSec = Math.Round(p.GameSec, 1) }),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write recording metadata: {ex.Message}");
        }
    }

    /// Recordings interrupted by a crash/power cut leave a .part.mp4 behind;
    /// fragments up to the cut are intact, so finalize them at startup.
    private void FinalizeOrphans()
    {
        foreach (var part in Directory.EnumerateFiles(RecordingsDir, "*.part.mp4"))
        {
            var final = part[..^".part.mp4".Length] + ".mp4";
            Log.Warn($"Finalizing interrupted recording: {Path.GetFileName(part)}");
            try
            {
                FinalizeAsync(part, final, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not finalize {Path.GetFileName(part)}: {ex.Message}");
            }
        }
    }

    /// Pipeline smoke test without a game: record the primary desktop for 10s
    /// through the exact capture/encode/finalize path (LT_RECORD_TEST=1).
    public static async Task RecordTestAsync(AgentConfig config, string ffmpeg, CancellationToken ct)
    {
        var recorder = new GameRecorder(config, ffmpeg, leagueRoot: "");
        Directory.CreateDirectory(recorder.RecordingsDir);
        var baseName = $"record-test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var part = Path.Combine(recorder.RecordingsDir, $"{baseName}.part.mp4");
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        Log.Info($"Record test: 10s of the primary desktop at {fps}fps...");
        var events = Path.Combine(recorder.RecordingsDir, $"{baseName}.events.csv.gz");
        using (config.RecordInputs ? InputLogger.TryStart(events) : null)
        {
            await recorder.RunFfmpegAsync(
                $"-y -f lavfi -i ddagrab=framerate={fps} -t 10 " +
                $"-c:v h264_nvenc -preset p4 -rc vbr -cq {config.RecordQuality} -b:v 0 -maxrate 25M -bufsize 50M -g {fps * 4} " +
                $"-movflags +frag_keyframe+empty_moov -f mp4 \"{part}\"", ct);
        }
        if (File.Exists(events)) Log.Info($"Record test: input telemetry at {events} ({new FileInfo(events).Length} bytes)");
        await recorder.FinalizeAsync(part, Path.Combine(recorder.RecordingsDir, $"{baseName}.mp4"), ct);
        Log.Info($"Record test complete: {Path.Combine(recorder.RecordingsDir, baseName + ".mp4")}");
    }

    private async Task<string?> PhaseAsync(CancellationToken ct)
    {
        if (leagueRoot is not { Length: > 0 } || LcuClient.TryConnect(leagueRoot) is not { } lcu) return null;
        using (lcu) return await lcu.GetGameflowPhaseAsync(ct);
    }

    /// In-game clock from the Live Client API (same 2999 endpoint family the
    /// Replay API uses; live games serve it without any game.cfg flag).
    private async Task<double?> GameTimeAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _liveClient.GetStringAsync("https://127.0.0.1:2999/liveclientdata/gamestats", ct));
            return doc.RootElement.GetProperty("gameTime").GetDouble();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// Which account is playing - this PC sees more than one.
    private async Task<string?> ActivePlayerAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _liveClient.GetStringAsync("https://127.0.0.1:2999/liveclientdata/activeplayername", ct);
            return JsonSerializer.Deserialize<string>(raw) is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// Keeps reading so ffmpeg never blocks on a full stderr pipe (hours-long
    /// runs would otherwise stall); only the tail is kept for diagnostics.
    private static Task<string> DrainStderrAsync(StreamReader reader) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        while (await reader.ReadAsync(buffer, 0, buffer.Length) is > 0 and var n)
        {
            sb.Append(buffer, 0, n);
            if (sb.Length > 8192) sb.Remove(0, sb.Length - 4096);
        }
        return sb.ToString();
    });

    private static string Tail(string s) => s.Length > 400 ? s[^400..] : s;

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {Tail(stderr)}");
    }
}
