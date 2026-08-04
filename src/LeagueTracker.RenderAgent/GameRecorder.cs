using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueTracker.RenderAgent;

/// Records live games, Ascent-style: when the local player enters a real game
/// (LCU gameflow "InProgress" - replay renders are "WatchInProgress" and never
/// trigger this), the desktop is captured cropped to the game window and
/// encoded on the GPU (NVENC), so playing cost is a video encode the graphics
/// card does on the side.
///
/// One GAME is one FILE, no matter how many times the capture dies underneath
/// it. Desktop Duplication does not survive the display mode switches an
/// exclusive-fullscreen game makes (alt-tab at game start killed a capture in
/// its first minute on four of the five days it ran), so each capture attempt
/// is a numbered segment ({name}.segNN.part.mp4) and the game's segments are
/// concatenated into a single mp4 when it ends. The game's name and number are
/// allocated once per match id and held in a {name}.inflight.json state file,
/// so capture restarts, agent restarts and deploys all append to the same
/// game rather than minting "Game N+1" for the tail of game N. Day numbers
/// come from a ledger as well as the folder, so files being renamed or
/// recycled can never make a later game overwrite an earlier one.
///
/// Recording runs to fragmented .part.mp4 segments - playable even if the
/// agent dies mid-game - and is remuxed/concatenated to a faststart .mp4 when
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

    private readonly YouTubeUploader _youtube = new(config);

    private readonly HttpClient _liveClient = new(new HttpClientHandler
    {
        // Same self-signed local cert as the Replay API (same port, in fact).
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    public string RecordingsDir => config.RecordingsDir is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LeagueTracker");

    /// Sidecars (telemetry, recording metadata, thumbnail, upload stamps)
    /// live one level down so the recordings folder itself stays a clean
    /// list of publishable mp4s.
    public string MetaDir => Path.Combine(RecordingsDir, "metadata");

    /// Everything the recorder must remember about a game while it is still
    /// being played: identity, the allocated name, and each capture segment
    /// so far. Persisted as {BaseName}.inflight.json after every segment -
    /// this is what lets a restarted agent keep appending to the same game.
    private sealed class RecordingState
    {
        public string BaseName { get; set; } = "";
        public string? MatchId { get; set; }
        public long? GameId { get; set; }
        public string? PlatformId { get; set; }
        public long? QueueId { get; set; }
        public string? GameMode { get; set; }
        public string? ActivePlayer { get; set; }
        public List<SegmentState> Segments { get; set; } = [];
    }

    private sealed class SegmentState
    {
        public string File { get; set; } = "";      // file name within RecordingsDir
        public string? EventsFile { get; set; }     // file name within MetaDir
        public DateTime StartedUtc { get; set; }
        public double WallSec { get; set; }
        public double VideoSec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasAudio { get; set; }
        public string Encoder { get; set; } = "";
        /// An already-finalized faststart mp4 (the game resumed after its
        /// first chunk was finalized - agent restart) rather than a raw
        /// fragmented .part segment.
        public bool Preexisting { get; set; }
        public List<double[]> ClockMap { get; set; } = []; // [videoSec, gameSec]
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(MetaDir);
        try { await FinalizeOrphansAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Orphan finalize failed: {ex.Message}"); }
        Log.Info($"Game recorder on - live games land in {RecordingsDir} ({config.RecordFramerate}fps, " +
                 (config.CaptureBackend.Trim().Equals("wgc", StringComparison.OrdinalIgnoreCase)
                     ? $"WGC + MF quality {Math.Clamp(96 - config.RecordQuality, 40, 95)})"
                     : $"ddagrab + NVENC cq {config.RecordQuality})"));
        _youtube.ValidateAtStartup();
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

    /// One game, start to finish, as many capture segments as it takes.
    /// False = capture could not be made to work for this game (as opposed
    /// to the game simply ending).
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

        var matchId = session is { PlatformId.Length: > 0 } s ? $"{s.PlatformId}_{s.GameId}" : null;
        var state = TryResumeState(matchId);
        if (state is null)
        {
            state = new RecordingState
            {
                BaseName = AllocateBaseName(matchId),
                MatchId = matchId,
                GameId = session?.GameId,
                PlatformId = session?.PlatformId,
                QueueId = session?.QueueId,
                GameMode = session?.GameMode,
            };
        }
        else
        {
            Log.Info($"Resuming recording {state.BaseName} at segment {state.Segments.Count + 1}");
        }

        var earlyFailures = 0;
        var gaveUp = false;
        while (true)
        {
            if (RenderAgent.StopRequested)
            {
                // Deploy stop mid-game: leave the segments and state on disk
                // for the next agent run to resume - finalizing here would
                // split the game across two numbers.
                if (state.Segments.Count > 0 && await PhaseSafeAsync() == "InProgress")
                {
                    SaveInflight(state);
                    Log.Info($"Stop requested mid-game - {state.BaseName} will resume after restart");
                    return true;
                }
                break;
            }
            if (await PhaseAsync(ct) is not "InProgress") break;

            var game = await WaitForGameWindowAsync(ct);
            if (game is not { } g) break; // game over, or the window never came - finalize what exists
            using var gameProcess = g.Process;

            // Wait out the loading screen before capturing. The game flips
            // display mode when it goes from load to live, and that switch
            // destroys the Desktop Duplication session mid-recording (every
            // game seamed at ~30s otherwise). liveclientdata/gamestats only
            // answers once the world is up - i.e. AFTER the switch - so it's
            // the "safe to capture" signal. On a mid-game segment restart it
            // answers immediately.
            await WaitForGameLiveAsync(ct);

            // The mode switch may have changed the client size since load -
            // read the rect now, post-switch, so the crop matches the screen.
            if (GameWindow.ClientRectOf(g.Process.MainWindowHandle) is { Width: >= 320, Height: >= 200 } liveRect)
            {
                g = g with { Rect = liveRect };
            }

            var segNo = state.Segments.Count + 1;
            var segBase = $"{state.BaseName}.seg{segNo:00}";
            var partPath = Path.Combine(RecordingsDir, $"{segBase}.part.mp4");
            var eventsPath = Path.Combine(MetaDir, $"{segBase}.events.csv.gz");
            Log.Info($"Recording {state.BaseName} segment {segNo} ({state.MatchId ?? "id unknown"}): {g.Rect.Width}x{g.Rect.Height}");

            // Backend order: wgc tries Windows Graphics Capture and falls
            // back to ffmpeg ddagrab+NVENC; ddagrab tries NVENC and falls
            // back to CPU x264 (driver/session limit). Either way a startup
            // failure gets one different-engine retry before counting.
            var useWgc = config.CaptureBackend.Trim().Equals("wgc", StringComparison.OrdinalIgnoreCase);
            var result = useWgc
                ? await CaptureWgcAsync(partPath, eventsPath, g, ct)
                : await CaptureAsync(partPath, eventsPath, g, nvenc: true, ct);
            if (result is { FfmpegFailedEarly: true })
            {
                Log.Warn(useWgc
                    ? "WGC capture failed at startup - falling back to ffmpeg ddagrab"
                    : "NVENC capture failed at startup - falling back to CPU encoding");
                result = await CaptureAsync(partPath, eventsPath, g, nvenc: useWgc, ct);
            }

            // "Failed early" (nonzero exit within seconds) and "ran but wrote
            // nothing" (a mode switch killing ddagrab before the first real
            // frames - ffmpeg exits CLEANLY, which is why these used to be
            // finalized as 0-minute games) are the same case here: no usable
            // video, so retry after a beat rather than minting a junk file.
            var partSize = new FileInfo(partPath) is { Exists: true } part ? part.Length : 0;
            if (result!.FfmpegFailedEarly || (result.VideoSec < 2 && result.Frames < 30 && partSize < 20_000_000))
            {
                TryDelete(partPath);
                TryDelete(eventsPath);
                TryDelete(Path.ChangeExtension(partPath, ".pcm"));
                if (++earlyFailures >= 4)
                {
                    Log.Error($"Capture keeps dying at startup ({earlyFailures} attempts) - giving up on this game: {result.StderrTail}");
                    gaveUp = true;
                    break;
                }
                Log.Warn($"Capture produced no usable video (attempt {earlyFailures}/4) - retrying: {result.StderrTail}");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }
            earlyFailures = 0;

            state.Segments.Add(new SegmentState
            {
                File = Path.GetFileName(partPath),
                EventsFile = File.Exists(eventsPath) ? Path.GetFileName(eventsPath) : null,
                StartedUtc = result.StartedUtc,
                WallSec = result.Duration.TotalSeconds,
                VideoSec = result.VideoSec,
                Width = g.Rect.Width & ~1,
                Height = g.Rect.Height & ~1,
                HasAudio = result.HasAudio,
                Encoder = result.Encoder,
                ClockMap = [.. result.ClockMap.Select(p => new[] { p.VideoSec, p.GameSec })],
            });
            state.ActivePlayer ??= result.ActivePlayer;
            SaveInflight(state);
            // Loop: if the game is still on, the next pass opens segment N+1
            // (a seam of a few seconds); if it ended, the phase check exits.
        }

        if (state.Segments.Count == 0)
        {
            CleanupInflight(state);
            ReleaseBaseName(state.BaseName);
            return !gaveUp;
        }

        if (state.Segments.Any(seg => !seg.Preexisting))
        {
            await FinalizeGameAsync(state, ct);
            Log.Info($"Recording complete: {state.BaseName}.mp4 ({state.Segments.Sum(seg => seg.VideoSec) / 60:0} min, {state.Segments.Count} segment(s))");
            // Customs/Practice Tool have no Riot match for a tracker to own -
            // those recordings are local-only, not eternal upload retries.
            // A deploy stop skips the deliveries; the startup sweep catches up.
            var deliverable = state.MatchId is not null && !RenderAgent.StopRequested
                && QueueCategories.GetValueOrDefault(state.QueueId ?? -1, "other") is not "custom";
            if (deliverable && (config.UploadVods || config.UploadVodSidecars))
            {
                await TryUploadVodAsync(state.MatchId!, state.BaseName, ct);
            }
            if (deliverable && _youtube.Enabled)
            {
                await TryPublishToYouTubeAsync(state.MatchId!, state.BaseName, ct);
            }
        }
        else
        {
            CleanupInflight(state); // resumed, but the game ended before any new footage
        }
        return !gaveUp;
    }

    /// The in-flight state for this match id, if this game was already being
    /// recorded (capture restart survives in-process; agent restart finds the
    /// .inflight.json; a game whose first chunk was already FINALIZED comes
    /// back as a Preexisting segment so the tail concatenates onto it).
    private RecordingState? TryResumeState(string? matchId)
    {
        if (matchId is not { Length: > 0 }) return null;
        foreach (var path in Directory.EnumerateFiles(MetaDir, "*.inflight.json"))
        {
            if (TryLoadInflight(path) is not { } state || state.MatchId != matchId) continue;
            state.Segments.RemoveAll(seg => !File.Exists(Path.Combine(RecordingsDir, seg.File)));
            return state;
        }
        foreach (var sidecar in Directory.EnumerateFiles(MetaDir, "*.json"))
        {
            if (sidecar.EndsWith(".inflight.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar));
                if (root?["matchId"]?.GetValue<string>() != matchId) continue;
                var baseName = Path.GetFileNameWithoutExtension(sidecar);
                if (!File.Exists(Path.Combine(RecordingsDir, baseName + ".mp4"))) continue;
                Log.Info($"Game {matchId} already has a finalized recording - appending to {baseName}.mp4");
                var startUtc = root["recordingStartUtc"]?.GetValue<DateTime>() ?? DateTime.UtcNow;
                var endUtc = root["recordingEndUtc"]?.GetValue<DateTime>() ?? startUtc;
                return new RecordingState
                {
                    BaseName = baseName,
                    MatchId = matchId,
                    GameId = root["gameId"]?.GetValue<long>(),
                    PlatformId = root["platformId"]?.GetValue<string>(),
                    QueueId = root["queueId"]?.GetValue<long>(),
                    GameMode = root["gameMode"]?.GetValue<string>(),
                    ActivePlayer = root["activePlayer"]?.GetValue<string>(),
                    Segments =
                    [
                        new SegmentState
                        {
                            File = baseName + ".mp4",
                            EventsFile = File.Exists(Path.Combine(MetaDir, baseName + ".events.csv.gz")) ? baseName + ".events.csv.gz" : null,
                            StartedUtc = startUtc,
                            WallSec = Math.Max(0, (endUtc - startUtc).TotalSeconds),
                            Width = (int)(root["width"]?.GetValue<long>() ?? 0),
                            Height = (int)(root["height"]?.GetValue<long>() ?? 0),
                            Preexisting = true,
                            ClockMap = [.. (root["clockMap"]?.AsArray() ?? []).OfType<System.Text.Json.Nodes.JsonNode>()
                                .Select(n => new[] { n["videoSec"]?.GetValue<double>() ?? 0, n["gameSec"]?.GetValue<double>() ?? 0 })],
                        },
                    ],
                };
            }
            catch
            {
                // not a sidecar (numbers ledger, corrupt file) - skip
            }
        }
        return null;
    }

    private string InflightPath(RecordingState state) => Path.Combine(MetaDir, state.BaseName + ".inflight.json");

    private void SaveInflight(RecordingState state)
    {
        try { File.WriteAllText(InflightPath(state), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Warn($"Could not save recording state: {ex.Message}"); }
    }

    private static RecordingState? TryLoadInflight(string path)
    {
        try { return JsonSerializer.Deserialize<RecordingState>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private void CleanupInflight(RecordingState state) => TryDelete(InflightPath(state));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private string NumbersPath => Path.Combine(MetaDir, "game-numbers.json");

    private Dictionary<string, int> LoadNumbers()
    {
        try
        {
            return File.Exists(NumbersPath)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(NumbersPath)) ?? []
                : [];
        }
        catch { return []; }
    }

    private void SaveNumbers(Dictionary<string, int> numbers)
    {
        try { File.WriteAllText(NumbersPath, JsonSerializer.Serialize(numbers, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Warn($"Could not save the game-number ledger: {ex.Message}"); }
    }

    /// "Road to Platinum - 22 Jul 2026 - Game 2": the day's next number. The
    /// number is the max of what's in the folder AND a ledger of what was
    /// ever handed out - the folder alone is not enough, because files get
    /// recycled/renamed between sessions, and a reused number OVERWRITES the
    /// game that had it (it happened twice, 25 and 27 Jul).
    /// Blank prefix keeps the sortable timestamp + match id scheme.
    private string AllocateBaseName(string? matchId)
    {
        if (config.RecordNamePrefix is not { Length: > 0 } rawPrefix)
        {
            return $"{DateTime.Now:yyyy-MM-dd_HH-mm}{(matchId is null ? "" : $"_{matchId}")}";
        }
        var prefix = string.Concat(rawPrefix.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim();
        var day = DateTime.Now.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var stem = $"{prefix} - {day}";
        var pattern = new Regex($@"^{Regex.Escape($"{stem} - Game ")}(\d+)");
        var next = 1;
        foreach (var file in Directory.EnumerateFiles(RecordingsDir, "*.mp4"))
        {
            if (pattern.Match(Path.GetFileName(file)) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n))
            {
                next = Math.Max(next, n + 1);
            }
        }
        var numbers = LoadNumbers();
        if (numbers.TryGetValue(stem, out var used)) next = Math.Max(next, used + 1);
        numbers[stem] = next;
        SaveNumbers(numbers);
        return $"{stem} - Game {next}";
    }

    /// A game that produced no footage gives its number back (if it is still
    /// the day's latest) so gapless Game 1, 2, 3... survives a false start.
    private void ReleaseBaseName(string baseName)
    {
        if (Regex.Match(baseName, @"^(.*) - Game (\d+)$") is not { Success: true } m) return;
        var numbers = LoadNumbers();
        if (numbers.TryGetValue(m.Groups[1].Value, out var used) && used == int.Parse(m.Groups[2].Value))
        {
            numbers[m.Groups[1].Value] = used - 1;
            SaveNumbers(numbers);
        }
    }

    /// Offers the VOD to each tracker until the one owning the match takes
    /// it (one agent, several account instances). A .uploaded stamp next to
    /// the mp4 keeps the startup sweep from re-sending gigabytes; failure
    /// just leaves the stamp missing, and the next agent start retries.
    private async Task TryUploadVodAsync(string matchId, string baseName, CancellationToken ct)
    {
        string M(string ext) => Path.Combine(MetaDir, baseName + ext);
        foreach (var tracker in _trackers)
        {
            try
            {
                if (!await tracker.UploadVodAsync(matchId, Path.Combine(RecordingsDir, baseName + ".mp4"),
                        File.Exists(M(".json")) ? M(".json") : null,
                        File.Exists(M(".events.csv.gz")) ? M(".events.csv.gz") : null,
                        File.Exists(M(".jpg")) ? M(".jpg") : null,
                        includeVideo: config.UploadVods, ct))
                {
                    continue; // tracker doesn't know this match - not its account
                }
                File.WriteAllText(M(".uploaded"), tracker.ServerUrl);
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

    /// Uploads the finished mp4 to YouTube (unless already published) and
    /// hands the link to the owning tracker. False = stop attempting further
    /// uploads this pass: a game wants the machine, or quota/network is the
    /// blocker - and both are shared, so the next file would only fail the
    /// same way.
    private async Task<bool> TryPublishToYouTubeAsync(string matchId, string baseName, CancellationToken ct)
    {
        string M(string ext) => Path.Combine(MetaDir, baseName + ext);
        if (!_youtube.Enabled) return false;
        if (File.Exists(M(".ytfailed.txt"))) return true; // deterministic reject - a human decides, not a retry loop
        if (!File.Exists(M(".youtube.txt")))
        {
            var mp4 = Path.Combine(RecordingsDir, baseName + ".mp4");
            if (!File.Exists(mp4)) return true; // recycled before it ever published - nothing to upload
            // The title is the file name minus its separators - "Road to
            // Platinum 03 Aug 2026 Game 2", the exact style the channel's
            // hand-made uploads already use.
            var result = await _youtube.UploadAsync(mp4, baseName.Replace(" - ", " "), $"Match {matchId}",
                M(".ytsession.json"), holdOff: () => GameProcessRunning, ct);
            switch (result.Outcome)
            {
                case UploadOutcome.Uploaded:
                    File.WriteAllText(M(".youtube.txt"), result.Url);
                    Log.Info($"Published to YouTube: {baseName} -> {result.Url}");
                    break;
                case UploadOutcome.Paused:
                    Log.Info($"YouTube upload paused ({result.Error}): {baseName} - resumes at the next idle sweep");
                    return false;
                case UploadOutcome.Postponed:
                    Log.Warn($"YouTube upload postponed ({result.Error}): {baseName}");
                    return false;
                case UploadOutcome.Failed:
                    File.WriteAllText(M(".ytfailed.txt"), result.Error);
                    Log.Error($"YouTube rejected {baseName}: {result.Error} - not retrying (delete {baseName}.ytfailed.txt to try again)");
                    return true;
            }
        }
        await TryPostVodLinkAsync(matchId, baseName, ct);
        return true;
    }

    /// The link goes to whichever tracker owns the match, the same routing
    /// rule as the VOD itself; a .linked stamp stops re-posting. No taker
    /// (poller lag on a fresh game) just means the next sweep retries.
    private async Task TryPostVodLinkAsync(string matchId, string baseName, CancellationToken ct)
    {
        string M(string ext) => Path.Combine(MetaDir, baseName + ext);
        if (File.Exists(M(".linked"))) return;
        string url;
        try { url = File.ReadAllText(M(".youtube.txt")).Trim(); }
        catch { return; }
        if (url.Length == 0) return;
        foreach (var tracker in _trackers)
        {
            try
            {
                if (!await tracker.SetVodLinkAsync(matchId, url, ct)) continue;
                File.WriteAllText(M(".linked"), tracker.ServerUrl);
                Log.Info($"VOD link for {matchId} registered on {tracker.ServerUrl}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"VOD link post to {tracker.ServerUrl} failed: {ex.Message} (retried at next sweep)");
            }
        }
        Log.Info($"VOD link for {matchId}: no tracker accepted it yet (match not imported) - will retry");
    }

    /// The between-chunks pause signal for YouTube uploads: a running game
    /// process means the player (and the recorder loop this runs on) needs
    /// the machine right now.
    private static bool GameProcessRunning
    {
        get
        {
            var procs = Process.GetProcessesByName(GameProcessName);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
    }

    /// Recordings with deliveries still owed (tracker down, match not yet
    /// imported, YouTube quota spent, agent killed): retried at startup and
    /// on idle passes, oldest first.
    private async Task SweepUnuploadedAsync(CancellationToken ct)
    {
        if (!config.UploadVods && !config.UploadVodSidecars && !_youtube.Enabled) return;
        var youtubeGo = _youtube.Enabled;
        string? resolvedPlatform = null;
        var platformProbed = false;
        foreach (var sidecar in Directory.EnumerateFiles(MetaDir, "*.json").OrderBy(f => f))
        {
            if (sidecar.EndsWith(".inflight.json", StringComparison.OrdinalIgnoreCase)
                || sidecar.EndsWith(".ytsession.json", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = Path.GetFileNameWithoutExtension(sidecar);
            var delivered = File.Exists(Path.Combine(MetaDir, baseName + ".uploaded"));
            if (delivered && !_youtube.Enabled) continue; // nothing left owed for this game
            string? matchId;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(sidecar, ct))!;
                matchId = root["matchId"]?.GetValue<string>();
                if (root["queueId"]?.GetValue<long>() is { } queueId
                    && QueueCategories.GetValueOrDefault(queueId, "other") is "custom")
                {
                    continue; // no Riot match exists for customs - nothing to deliver to
                }
                // Sidecars recorded before the platform fallback existed have
                // a gameId but no match id - repair them when the client can
                // say which platform this PC plays on (once per sweep).
                if (matchId is not { Length: > 0 } && root["gameId"]?.GetValue<long>() is > 0 and var gameId)
                {
                    if (!platformProbed)
                    {
                        platformProbed = true;
                        if (LcuClient.TryConnect(leagueRoot) is { } lcu)
                        {
                            using (lcu) resolvedPlatform = await lcu.GetPlatformIdAsync(ct);
                        }
                    }
                    if (resolvedPlatform is { Length: > 0 })
                    {
                        matchId = $"{resolvedPlatform}_{gameId}";
                        root["matchId"] = matchId;
                        root["platformId"] = resolvedPlatform;
                        await File.WriteAllTextAsync(sidecar, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
                        Log.Info($"Backfilled match id {matchId} into {baseName}.json");
                    }
                }
            }
            catch
            {
                continue;
            }
            if (matchId is not { Length: > 0 }) continue;
            // Sidecars upload even after the local mp4 is gone (published to
            // YouTube and cleaned up); a full upload obviously cannot.
            if (!delivered && (config.UploadVods || config.UploadVodSidecars)
                && (!config.UploadVods || File.Exists(Path.Combine(RecordingsDir, baseName + ".mp4"))))
            {
                await TryUploadVodAsync(matchId, baseName, ct);
            }
            if (youtubeGo) youtubeGo = await TryPublishToYouTubeAsync(matchId, baseName, ct);
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
        [2400] = "aram",     // ARAM: Mayhem (LCU reports gameMode "KIWI", Riot's internal codename - observed live 2026-07-22)
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
        if (category is "other" && session.GameMode is "ARAM" or "KIWI") category = "aram";
        if (category is "other" && session.GameMode is "URF" or "ARURF") category = "urf";
        if (enabled.Contains(category)) return true;
        skipReason = $"queue {session.QueueId} ({category}, {session.GameMode ?? "?"}) is not in RecordQueues ({config.RecordQueues})";
        return false;
    }

    private sealed record CaptureResult(
        bool FfmpegFailedEarly, string StderrTail, DateTime StartedUtc, TimeSpan Duration,
        double VideoSec, long Frames, bool HasAudio, string Encoder,
        List<(double VideoSec, double GameSec)> ClockMap, string? ActivePlayer);

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

    /// Blocks until the game world is live (the loading-screen display-mode
    /// switch is done) or a timeout. Signalled by liveclientdata/gamestats
    /// answering with a game time - it stays silent through the whole load.
    /// The timeout is a backstop: if the API never answers (disabled, odd
    /// mode) we record anyway rather than miss the game.
    private async Task WaitForGameLiveAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var everSaw = false;
        while (DateTime.UtcNow < deadline)
        {
            if (RenderAgent.StopRequested) return;
            if (await PhaseAsync(ct) is not "InProgress") return; // game vanished (dodge, crash)
            if (await GameTimeAsync(ct) is { } t)
            {
                everSaw = true;
                // Answering AND advancing = simulation running, mode settled.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (await GameTimeAsync(ct) is { } t2 && t2 >= t) { Log.Info($"Game world live at {t2:0}s - starting capture"); return; }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        Log.Warn($"Game-live signal not seen within the wait window ({(everSaw ? "clock stalled" : "no Live Client response")}) - capturing anyway");
    }

    private async Task<CaptureResult?> CaptureAsync(string partPath, string eventsPath, GameWindowInfo g, bool nvenc, CancellationToken ct)
    {
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        var width = g.Rect.Width & ~1;   // encoders need even dimensions
        var height = g.Rect.Height & ~1;
        // Game-only audio via process loopback; when it can't start, the
        // recording proceeds with no audio track rather than failing.
        using var audio = config.RecordAudio ? ProcessAudioCapture.TryStart(g.Process.Id) : null;
        var input = $"-y -f lavfi -i ddagrab=framerate={fps}:offset_x={Math.Max(0, g.Rect.X)}:offset_y={Math.Max(0, g.Rect.Y)}:video_size={width}x{height}"
            + (audio is null ? "" : $" {ProcessAudioCapture.FfmpegInputArgs}");
        // ddagrab hands out D3D11 frames; NVENC eats them on the GPU without a
        // round-trip through system memory - that is the whole low-overhead
        // trick. The CPU fallback has to download frames first.
        var encode = nvenc
            ? $"-c:v h264_nvenc -preset p4 -rc vbr -cq {config.RecordQuality} -b:v 0 -maxrate 25M -bufsize 50M -g {fps * 4}"
            : "-vf hwdownload,format=bgra -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p";
        // No -shortest here, deliberately: it made ffmpeg stop the moment
        // EITHER input ended, so a dying audio pipe cleanly killed the whole
        // video recording seconds into a game (the string of 0-minute "games"
        // of 24-27 Jul). A dead VIDEO stream is the monitor loop's job now:
        // -progress exposes the encoder's frame counter, and frames that stop
        // advancing end the capture within seconds so a fresh segment starts.
        var mapping = audio is null ? "" : "-map 0:v -map 1:a -c:a aac -b:a 160k";
        // Fragmented mp4: every fragment is self-contained, so a crash or
        // power cut costs seconds, not the whole game. Finalize remuxes to a
        // normal faststart mp4 for clean browser playback.
        var args = $"{input} {encode} {mapping} -nostats -progress pipe:1 -movflags +frag_keyframe+empty_moov -f mp4 \"{partPath}\"";

        using var proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,   // 'q' on stdin = ffmpeg's graceful stop, which flushes the muxer
            RedirectStandardOutput = true,  // -progress key=value stream
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");

        var stderrTail = DrainStderrAsync(proc.StandardError);

        // The -progress stream is the capture's pulse: "frame=" only advances
        // while ddagrab is really delivering (it duplicates frames on a static
        // screen, so a healthy capture advances even when nothing moves).
        var progressGate = new object();
        long frames = 0;
        var lastFrameAdvance = DateTime.UtcNow;
        double videoUs = 0; // out_time_ms is microseconds despite the name (ffmpeg quirk)
        var progressPump = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq];
                if (key is "frame" && long.TryParse(line[(eq + 1)..], out var f))
                {
                    lock (progressGate) { if (f > frames) { frames = f; lastFrameAdvance = DateTime.UtcNow; } }
                }
                else if (key is "out_time_us" or "out_time_ms" && long.TryParse(line[(eq + 1)..], out var us))
                {
                    lock (progressGate) videoUs = Math.Max(videoUs, us);
                }
            }
        });

        // Hooks live exactly as long as the capture, so event t_ms and video
        // time share a zero point (within ffmpeg's first-frame latency).
        using var inputLogger = config.RecordInputs ? InputLogger.TryStart(eventsPath, g.Process.MainWindowHandle) : null;
        var startedUtc = DateTime.UtcNow;
        var clockMap = new List<(double, double)>();
        string? activePlayer = null;
        var lastClockSample = DateTime.MinValue;

        var exitedAlone = false;
        try
        {
            while (!proc.HasExited)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; } // agent shutdown - stop cleanly below

                g.Process.Refresh();
                if (g.Process.HasExited) break;
                if (proc.HasExited) { exitedAlone = true; break; }

                // A deploy's stop request ends the recording cleanly (the VOD
                // up to here survives) rather than orphaning the capture.
                if (RenderAgent.StopRequested) break;

                // Frames stopped coming = the video stream is dead (a display
                // mode switch tearing down the Desktop Duplication session)
                // even though ffmpeg may sit there muxing audio under one
                // frozen frame. End this segment; the game is still on, so
                // the caller immediately opens the next one.
                DateTime frameAdvance;
                long framesSeen;
                lock (progressGate) { frameAdvance = lastFrameAdvance; framesSeen = frames; }
                if (DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(20) && DateTime.UtcNow - frameAdvance > TimeSpan.FromSeconds(12))
                {
                    Log.Warn($"Video frames stopped at {framesSeen} ({(DateTime.UtcNow - frameAdvance).TotalSeconds:0}s without progress - display mode switch likely); restarting the capture");
                    break;
                }

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

        await Task.WhenAny(progressPump, Task.Delay(3000));
        var duration = DateTime.UtcNow - startedUtc;
        double videoSec;
        long frameCount;
        lock (progressGate) { videoSec = videoUs / 1e6; frameCount = frames; }
        var failedEarly = duration < TimeSpan.FromSeconds(8) && proc.ExitCode != 0;
        if (!failedEarly && exitedAlone)
        {
            // ffmpeg ending while the game still runs is the interesting
            // failure (dead capture stream) - its stderr names the reason
            // (23 Jul: two games lost before this was logged).
            Log.Warn($"ffmpeg ended on its own after {duration.TotalSeconds:0}s: {Tail(await stderrTail)}");
        }
        return new CaptureResult(failedEarly, Tail(await stderrTail), startedUtc, duration,
            videoSec, frameCount, audio is not null, nvenc ? "h264_nvenc" : "libx264", clockMap, activePlayer);
    }

    /// One capture segment through Windows Graphics Capture instead of
    /// ffmpeg/ddagrab (CaptureBackend "wgc"). Same contract as CaptureAsync:
    /// runs until the game ends, the capture dies, or a stop is requested,
    /// and hands back the segment's vitals. Game audio lands beside the
    /// video as paced PCM ({part}.pcm) and is muxed in at finalize.
    private async Task<CaptureResult?> CaptureWgcAsync(string partPath, string eventsPath, GameWindowInfo g, CancellationToken ct)
    {
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        // Media Foundation's Quality knob is 1-100 (higher = better); NVENC
        // cq is inverted (lower = better). 96 - cq maps the default cq 26 to
        // 70 - adjust after comparing real game bitrates if needed.
        var quality = Math.Clamp(96 - config.RecordQuality, 40, 95);
        var rect = (Math.Max(0, g.Rect.X), Math.Max(0, g.Rect.Y), g.Rect.Width & ~1, g.Rect.Height & ~1);
        var startedUtc = DateTime.UtcNow;
        using var recorder = WgcRecorder.TryStart(partPath, rect, fps, quality);
        if (recorder is null)
        {
            return new CaptureResult(true, "WGC recorder would not construct", startedUtc, TimeSpan.Zero, 0, 0, false, "h264_mf_wgc", [], null);
        }
        if (!await recorder.WaitForRecordingAsync(TimeSpan.FromSeconds(10)))
        {
            var startError = await recorder.StopAsync(TimeSpan.FromSeconds(5));
            return new CaptureResult(true, recorder.Error ?? startError ?? "WGC recording did not start", startedUtc,
                DateTime.UtcNow - startedUtc, 0, 0, false, "h264_mf_wgc", [], null);
        }
        startedUtc = DateTime.UtcNow; // zero point = frames actually flowing
        using var audio = config.RecordAudio ? ProcessAudioCapture.TryStartToFile(g.Process.Id, Path.ChangeExtension(partPath, ".pcm")) : null;
        using var inputLogger = config.RecordInputs ? InputLogger.TryStart(eventsPath, g.Process.MainWindowHandle) : null;

        var clockMap = new List<(double, double)>();
        string? activePlayer = null;
        var lastClockSample = DateTime.MinValue;
        // WGC surviving mode switches makes the growth watchdog mostly
        // vestigial, but engines can still die quietly - same belt and
        // braces as the ffmpeg path, just measured on the output file.
        var lastGrowthCheck = DateTime.UtcNow;
        long lastPartSize = 0;

        while (!recorder.HasEnded)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; } // agent shutdown - stop cleanly below

            g.Process.Refresh();
            if (g.Process.HasExited) break;
            if (RenderAgent.StopRequested) break;

            if (DateTime.UtcNow - lastGrowthCheck > TimeSpan.FromSeconds(60))
            {
                lastGrowthCheck = DateTime.UtcNow;
                var size = new FileInfo(partPath) is { Exists: true } part ? part.Length : 0;
                var grewBytes = size - lastPartSize;
                lastPartSize = size;
                if (grewBytes < 5_000_000 && DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(90))
                {
                    Log.Warn($"WGC capture stalled ({grewBytes / 1024} KB in the last minute); restarting the capture");
                    break;
                }
            }

            if (DateTime.UtcNow - lastClockSample > TimeSpan.FromSeconds(30))
            {
                lastClockSample = DateTime.UtcNow;
                if (await GameTimeAsync(ct) is { } gameSec)
                {
                    clockMap.Add(((DateTime.UtcNow - startedUtc).TotalSeconds, gameSec));
                }
                activePlayer ??= await ActivePlayerAsync(ct);
                if (clockMap.Count % 3 == 0 && await PhaseAsync(CancellationToken.None) is not "InProgress" and not null) break;
            }
        }

        var endedAlone = recorder.HasEnded;
        var error = await recorder.StopAsync(TimeSpan.FromSeconds(15));
        var duration = DateTime.UtcNow - startedUtc;
        var failedEarly = duration < TimeSpan.FromSeconds(8) && error is not null;
        if (!failedEarly && endedAlone)
        {
            Log.Warn($"WGC capture ended on its own after {duration.TotalSeconds:0}s: {error ?? "no error reported"}");
        }
        // The muxer's exact video duration comes from the finalize probe;
        // WGC output is wall-continuous, so the wall clock is a fair stand-in
        // until then. Frames aren't observable here (-1): the junk check
        // falls back to duration + file size.
        return new CaptureResult(failedEarly, error ?? "", startedUtc, duration,
            duration.TotalSeconds, -1, audio is not null, "h264_mf_wgc", clockMap, activePlayer);
    }

    /// Turns a finished game's segments into the one file that game IS:
    /// each raw segment is remuxed to faststart (stream copy, milliseconds
    /// per gigabyte) with the BT.709 retag, multi-segment games are then
    /// concatenated (stream copy again), telemetry and clock maps are merged
    /// on the concatenated timeline, and the sidecar + thumbnail are written.
    private async Task FinalizeGameAsync(RecordingState state, CancellationToken ct)
    {
        var finalPath = Path.Combine(RecordingsDir, state.BaseName + ".mp4");
        var ready = new List<(SegmentState Seg, string Path)>();
        foreach (var seg in state.Segments)
        {
            var src = Path.Combine(RecordingsDir, seg.File);
            if (!File.Exists(src)) continue;
            var usable = src;
            if (!seg.Preexisting)
            {
                var tmp = Path.Combine(RecordingsDir, seg.File[..^".part.mp4".Length] + ".remux.mp4");
                try
                {
                    await RunFfmpegAsync(RemuxArgs(src, tmp), ct);
                    usable = tmp;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The fragmented original is still playable - use it as is.
                    Log.Warn($"Segment remux failed ({ex.Message}) - keeping the fragmented copy of {seg.File}");
                    TryDelete(tmp);
                }
            }
            // The muxer knows durations better than our wall clock does, and
            // the offsets that align telemetry across segments must be exact.
            var probe = await ProbeAsync(usable, ct);
            if (ParseDurationSec(probe) is { } sec) seg.VideoSec = sec;
            seg.HasAudio = probe.Contains("Audio:");
            ready.Add((seg, usable));
        }
        if (ready.Count == 0)
        {
            Log.Warn($"Nothing to finalize for {state.BaseName} - all segments are gone");
            CleanupInflight(state);
            return;
        }

        // The concat demuxer needs every piece to have the same streams; a
        // segment whose audio capture failed gets a silent track (audio-only
        // encode, video untouched) rather than breaking the whole join.
        if (ready.Any(r => r.Seg.HasAudio) && ready.Any(r => !r.Seg.HasAudio))
        {
            for (var i = 0; i < ready.Count; i++)
            {
                if (ready[i].Seg.HasAudio) continue;
                var padded = ready[i].Path + ".pad.mp4";
                try
                {
                    await RunFfmpegAsync($"-y -i \"{ready[i].Path}\" -f lavfi -i anullsrc=r={ProcessAudioCapture.SampleRate}:cl=stereo -map 0:v -map 1:a -c:v copy -c:a aac -b:a 160k -shortest -movflags +faststart \"{padded}\"", ct);
                    if (!ready[i].Path.Equals(Path.Combine(RecordingsDir, ready[i].Seg.File), StringComparison.OrdinalIgnoreCase)) TryDelete(ready[i].Path);
                    ready[i] = (ready[i].Seg, padded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Silent-track pad failed for {ready[i].Seg.File}: {ex.Message}");
                }
            }
        }

        var salvaged = false;
        if (ready.Count == 1)
        {
            if (!ready[0].Path.Equals(finalPath, StringComparison.OrdinalIgnoreCase)) File.Move(ready[0].Path, finalPath, overwrite: true);
        }
        else
        {
            var listPath = Path.Combine(MetaDir, state.BaseName + ".concat.txt");
            var concatTmp = Path.Combine(RecordingsDir, state.BaseName + ".concat.mp4");
            try
            {
                // Forward slashes: the concat demuxer treats backslashes as
                // escapes inside its list file.
                File.WriteAllLines(listPath, ready.Select(r => $"file '{Path.GetFullPath(r.Path).Replace('\\', '/').Replace("'", "'\\''")}'"));
                await RunFfmpegAsync($"-y -f concat -safe 0 -i \"{listPath}\" -c copy -movflags +faststart \"{concatTmp}\"", ct);
                File.Move(concatTmp, finalPath, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never lose footage to a failed join (codec-parameter drift
                // between segments, say): the pieces become their own files.
                Log.Warn($"Segment concat failed ({ex.Message}) - keeping the segments as separate files");
                salvaged = true;
                var n = 0;
                foreach (var (seg, path) in ready)
                {
                    n++;
                    var dst = n == 1 ? finalPath : Path.Combine(RecordingsDir, $"{state.BaseName} (part {n}).mp4");
                    if (!path.Equals(dst, StringComparison.OrdinalIgnoreCase)) File.Move(path, dst, overwrite: true);
                }
            }
            finally
            {
                TryDelete(listPath);
                TryDelete(concatTmp);
            }
        }

        // The final mp4 now holds everything - the raw segments, WGC audio
        // sidecars and remux intermediates are redundant.
        foreach (var (seg, path) in ready)
        {
            var orig = Path.Combine(RecordingsDir, seg.File);
            foreach (var leftover in new[] { orig, Path.ChangeExtension(orig, ".pcm"), path })
            {
                if (!leftover.Equals(finalPath, StringComparison.OrdinalIgnoreCase)) TryDelete(leftover);
            }
        }

        // Video offsets of each segment within the final file - the merge key
        // for telemetry and clock maps. Meaningless if the join fell apart
        // into separate files, so telemetry merging is skipped there (each
        // segment's events file stays behind for manual rescue).
        var offsets = new List<(SegmentState Seg, double OffsetSec)>();
        var cumulative = 0.0;
        foreach (var (seg, _) in ready)
        {
            offsets.Add((seg, cumulative));
            cumulative += seg.VideoSec;
        }
        if (!salvaged) MergeEvents(offsets, Path.Combine(MetaDir, state.BaseName + ".events.csv.gz"));

        await WriteThumbnailAsync(finalPath, Path.Combine(MetaDir, state.BaseName + ".jpg"), ct);
        WriteSidecar(state, offsets);
        // The content changed (or is brand new) - any earlier upload of this
        // game's first chunk is stale now.
        TryDelete(Path.Combine(MetaDir, state.BaseName + ".uploaded"));
        CleanupInflight(state);
    }

    /// One events.csv.gz for the whole game: each segment's telemetry with
    /// its video offset added, so t_ms lines up with the concatenated video
    /// exactly like a single uninterrupted recording's would.
    private void MergeEvents(List<(SegmentState Seg, double OffsetSec)> segments, string outPath)
    {
        var sources = segments.Where(s => s.Seg.EventsFile is { Length: > 0 } f && File.Exists(Path.Combine(MetaDir, f))).ToList();
        if (sources.Count == 0) return;
        try
        {
            // Read everything first: the resumed-game case has the merged
            // output file itself as segment 1's source.
            var merged = new List<string>();
            foreach (var (seg, offsetSec) in sources)
            {
                var offsetMs = (long)(offsetSec * 1000);
                using var file = File.OpenRead(Path.Combine(MetaDir, seg.EventsFile!));
                using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.ASCII);
                reader.ReadLine(); // header
                while (reader.ReadLine() is { Length: > 0 } line)
                {
                    var comma = line.IndexOf(',');
                    if (comma <= 0 || !long.TryParse(line[..comma], out var t)) continue;
                    merged.Add($"{t + offsetMs}{line[comma..]}");
                }
            }
            using (var file = File.Create(outPath))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip, Encoding.ASCII))
            {
                writer.WriteLine("t_ms,event_type,input_name,value_a,value_b");
                foreach (var line in merged) writer.WriteLine(line);
            }
            foreach (var (seg, _) in sources)
            {
                var path = Path.Combine(MetaDir, seg.EventsFile!);
                if (!path.Equals(outPath, StringComparison.OrdinalIgnoreCase)) TryDelete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Input telemetry merge failed: {ex.Message}");
        }
    }

    private async Task WriteThumbnailAsync(string videoPath, string thumbPath, CancellationToken ct)
    {
        foreach (var seek in new[] { 600, 60, 2 })  // mid-game if it lasted, else whatever exists
        {
            try
            {
                await RunFfmpegAsync($"-y -ss {seek} -i \"{videoPath}\" -frames:v 1 -vf scale=640:-1 \"{thumbPath}\"", ct);
                if (new FileInfo(thumbPath) is { Exists: true, Length: > 0 }) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
    }

    private void WriteSidecar(RecordingState state, List<(SegmentState Seg, double OffsetSec)> segments)
    {
        try
        {
            var last = segments[^1].Seg;
            var sized = segments.LastOrDefault(s => s.Seg.Width > 0).Seg ?? last;
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                videoFile = $"{state.BaseName}.mp4",
                matchId = state.MatchId,
                eventsFile = File.Exists(Path.Combine(MetaDir, $"{state.BaseName}.events.csv.gz")) ? $"{state.BaseName}.events.csv.gz" : null,
                gameId = state.GameId,
                platformId = state.PlatformId,
                queueId = state.QueueId,
                gameMode = state.GameMode,
                activePlayer = state.ActivePlayer,
                recordingStartUtc = segments[0].Seg.StartedUtc,
                recordingEndUtc = last.StartedUtc.AddSeconds(last.WallSec),
                width = sized.Width,
                height = sized.Height,
                fps = Math.Clamp(config.RecordFramerate, 15, 120),
                encoder = string.Join("+", segments.Select(s => s.Seg.Encoder).Where(e => e.Length > 0).Distinct()),
                // Capture seams (each entry is one uninterrupted capture) -
                // lets the review UI mark where footage gaps are.
                segments = segments.Select(s => new
                {
                    startUtc = s.Seg.StartedUtc,
                    videoOffsetSec = Math.Round(s.OffsetSec, 1),
                    videoSec = Math.Round(s.Seg.VideoSec, 1),
                }),
                // videoSec -> gameSec samples; the review UI maps timeline
                // events onto the video with these (one pair would do, but
                // samples over the whole game absorb any drift).
                clockMap = segments
                    .SelectMany(s => s.Seg.ClockMap.Select(p => new { videoSec = Math.Round(p[0] + s.OffsetSec, 1), gameSec = Math.Round(p[1], 1) })),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(MetaDir, state.BaseName + ".json"), json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write recording metadata: {ex.Message}");
        }
    }

    /// Remux a single fragmented recording into a normal faststart mp4
    /// (stream copy - milliseconds per gigabyte, no re-encode), plus a
    /// mid-game thumbnail. The legacy single-file path: the record test and
    /// stray .part.mp4 files from before segmented recording use it.
    private async Task FinalizeAsync(string partPath, string finalPath, CancellationToken ct)
    {
        try
        {
            await RunFfmpegAsync(RemuxArgs(partPath, finalPath), ct);
            File.Delete(partPath);
            TryDelete(Path.ChangeExtension(partPath, ".pcm"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The fragmented original is still playable - keep it, and say so.
            Log.Warn($"Remux failed ({ex.Message}) - keeping the fragmented recording at {partPath}");
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);
        }
        await WriteThumbnailAsync(finalPath, Path.Combine(MetaDir, Path.GetFileNameWithoutExtension(finalPath) + ".jpg"), ct);
    }

    /// Recordings interrupted by a crash/power cut/deploy leave segments and
    /// an .inflight.json behind. If their game is STILL being played (a
    /// deploy restarted the agent mid-game), leave everything for the
    /// recorder loop to resume; otherwise finalize into the game's one file.
    private async Task FinalizeOrphansAsync(CancellationToken ct)
    {
        string? liveMatchId = null;
        try
        {
            if (await PhaseAsync(ct) == "InProgress" && LcuClient.TryConnect(leagueRoot) is { } lcu)
            {
                using (lcu)
                {
                    liveMatchId = await lcu.GetGameSessionAsync(ct) is { PlatformId.Length: > 0 } s ? $"{s.PlatformId}_{s.GameId}" : null;
                }
            }
        }
        catch { /* no client, no live game */ }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(MetaDir, "*.inflight.json"))
        {
            if (TryLoadInflight(path) is not { } state) { TryDelete(path); continue; }
            foreach (var seg in state.Segments) claimed.Add(seg.File);
            if (state.MatchId is { Length: > 0 } && state.MatchId == liveMatchId)
            {
                Log.Info($"Recording {state.BaseName} belongs to the game still in progress - resuming it");
                continue;
            }
            state.Segments.RemoveAll(seg => !File.Exists(Path.Combine(RecordingsDir, seg.File)));
            if (state.Segments.Count == 0 || state.Segments.All(seg => seg.Preexisting)) { TryDelete(path); continue; }
            Log.Warn($"Finalizing interrupted recording: {state.BaseName} ({state.Segments.Count} segment(s))");
            try
            {
                await FinalizeGameAsync(state, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Could not finalize {state.BaseName}: {ex.Message}");
            }
        }

        // Stray parts with no state file (recordings from before segmented
        // capture, or a lost .inflight.json): finalized one-to-one so the
        // footage survives, even if under a .segNN name.
        foreach (var part in Directory.EnumerateFiles(RecordingsDir, "*.part.mp4"))
        {
            if (claimed.Contains(Path.GetFileName(part))) continue;
            var final = part[..^".part.mp4".Length] + ".mp4";
            Log.Warn($"Finalizing stray recording: {Path.GetFileName(part)}");
            try
            {
                await FinalizeAsync(part, final, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Could not finalize {Path.GetFileName(part)}: {ex.Message}");
            }
        }
    }

    /// Segmented-pipeline smoke test without a game (LT_RECORD_TEST=seg):
    /// two 6s desktop captures become segments of one RecordingState, and
    /// FinalizeGameAsync - the exact live path - must join them into a single
    /// mp4 with merged telemetry and a sidecar that knows both segments.
    public static async Task SegmentTestAsync(AgentConfig config, string ffmpeg, CancellationToken ct)
    {
        var recorder = new GameRecorder(config, ffmpeg, leagueRoot: "");
        Directory.CreateDirectory(recorder.RecordingsDir);
        Directory.CreateDirectory(recorder.MetaDir);
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        var state = new RecordingState { BaseName = $"segment-test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}" };
        Log.Info($"Segment test: 2x 6s of the primary desktop at {fps}fps, then the one-file finalize...");
        for (var i = 1; i <= 2; i++)
        {
            var segBase = $"{state.BaseName}.seg{i:00}";
            var part = Path.Combine(recorder.RecordingsDir, $"{segBase}.part.mp4");
            var events = Path.Combine(recorder.MetaDir, $"{segBase}.events.csv.gz");
            var startedUtc = DateTime.UtcNow;
            using (config.RecordInputs ? InputLogger.TryStart(events) : null)
            {
                await recorder.RunFfmpegAsync(
                    $"-y -f lavfi -i ddagrab=framerate={fps} -t 6 " +
                    $"-c:v h264_nvenc -preset p4 -rc vbr -cq {config.RecordQuality} -b:v 0 -maxrate 25M -bufsize 50M -g {fps * 4} " +
                    $"-movflags +frag_keyframe+empty_moov -f mp4 \"{part}\"", ct);
            }
            state.Segments.Add(new SegmentState
            {
                File = Path.GetFileName(part),
                EventsFile = File.Exists(events) ? Path.GetFileName(events) : null,
                StartedUtc = startedUtc,
                WallSec = (DateTime.UtcNow - startedUtc).TotalSeconds,
                Encoder = "h264_nvenc",
                ClockMap = [[1.0, 100.0 + i]],
            });
            recorder.SaveInflight(state);
        }
        await recorder.FinalizeGameAsync(state, ct);
        var final = Path.Combine(recorder.RecordingsDir, $"{state.BaseName}.mp4");
        var probe = await recorder.ProbeAsync(final, ct);
        var duration = ParseDurationSec(probe);
        var stray = Directory.EnumerateFiles(recorder.RecordingsDir, $"{state.BaseName}.seg*").Count();
        Log.Info($"Segment test: {final} duration {duration ?? -1:0.0}s (want ~12), leftover segment files {stray} (want 0), " +
                 $"merged telemetry {(File.Exists(Path.Combine(recorder.MetaDir, state.BaseName + ".events.csv.gz")) ? "present" : "MISSING")}, " +
                 $"inflight {(File.Exists(recorder.InflightPath(state)) ? "STILL PRESENT" : "cleaned")}");
        if (duration is null or < 11 or > 14 || stray != 0) throw new InvalidOperationException("segment test failed the checks above");
        Log.Info("Segment test complete");
    }

    /// WGC-pipeline smoke test (LT_RECORD_TEST=wgc): 10s of the primary
    /// desktop through WgcRecorder + file-target audio capture +
    /// FinalizeGameAsync - proves the plan-B engine loads, encodes and muxes
    /// before it is ever flipped on for real games.
    public static async Task WgcTestAsync(AgentConfig config, string ffmpeg, CancellationToken ct)
    {
        var recorder = new GameRecorder(config, ffmpeg, leagueRoot: "");
        Directory.CreateDirectory(recorder.RecordingsDir);
        Directory.CreateDirectory(recorder.MetaDir);
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        var quality = Math.Clamp(96 - config.RecordQuality, 40, 95);
        var state = new RecordingState { BaseName = $"wgc-test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}" };
        var part = Path.Combine(recorder.RecordingsDir, $"{state.BaseName}.seg01.part.mp4");
        var events = Path.Combine(recorder.MetaDir, $"{state.BaseName}.seg01.events.csv.gz");
        Log.Info($"WGC test: 10s of the primary desktop at {fps}fps (MF quality {quality})...");
        Process? noise = null;
        if (config.RecordAudio)
        {
            noise = Process.Start(new ProcessStartInfo("powershell",
                "-NoProfile -c \"$p = New-Object Media.SoundPlayer 'C:\\Windows\\Media\\Alarm01.wav'; $p.PlayLooping(); Start-Sleep 14\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        var startedUtc = DateTime.UtcNow;
        try
        {
            using var wgc = WgcRecorder.TryStart(part, null, fps, quality)
                ?? throw new InvalidOperationException("WGC recorder would not construct");
            if (!await wgc.WaitForRecordingAsync(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException($"WGC did not start: {wgc.Error ?? "timeout"}");
            }
            using (noise is null ? null : ProcessAudioCapture.TryStartToFile(noise.Id, Path.ChangeExtension(part, ".pcm")))
            using (config.RecordInputs ? InputLogger.TryStart(events) : null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            if (await wgc.StopAsync(TimeSpan.FromSeconds(15)) is { } err)
            {
                throw new InvalidOperationException($"WGC recording failed: {err}");
            }
        }
        finally
        {
            try { if (noise is { HasExited: false }) noise.Kill(); } catch { /* already gone */ }
            noise?.Dispose();
        }
        state.Segments.Add(new SegmentState
        {
            File = Path.GetFileName(part),
            EventsFile = File.Exists(events) ? Path.GetFileName(events) : null,
            StartedUtc = startedUtc,
            WallSec = (DateTime.UtcNow - startedUtc).TotalSeconds,
            Encoder = "h264_mf_wgc",
        });
        await recorder.FinalizeGameAsync(state, ct);
        var final = Path.Combine(recorder.RecordingsDir, state.BaseName + ".mp4");
        var probe = await recorder.ProbeAsync(final, ct);
        var duration = ParseDurationSec(probe);
        var hasAudio = probe.Contains("Audio:");
        Log.Info($"WGC test: {final} duration {duration ?? -1:0.0}s (want ~10), audio {(hasAudio ? "present" : "MISSING")}, " +
                 $"telemetry {(File.Exists(Path.Combine(recorder.MetaDir, state.BaseName + ".events.csv.gz")) ? "present" : "MISSING")}");
        if (duration is null or < 8 or > 14 || (config.RecordAudio && !hasAudio))
        {
            throw new InvalidOperationException("WGC test failed the checks above");
        }
        Log.Info("WGC test complete");
    }

    /// Pipeline smoke test without a game: record the primary desktop for 10s
    /// through the exact capture/encode/finalize path (LT_RECORD_TEST=1).
    public static async Task RecordTestAsync(AgentConfig config, string ffmpeg, CancellationToken ct)
    {
        var recorder = new GameRecorder(config, ffmpeg, leagueRoot: "");
        Directory.CreateDirectory(recorder.RecordingsDir);
        Directory.CreateDirectory(recorder.MetaDir);
        var baseName = $"record-test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var part = Path.Combine(recorder.RecordingsDir, $"{baseName}.part.mp4");
        var fps = Math.Clamp(config.RecordFramerate, 15, 120);
        Log.Info($"Record test: 10s of the primary desktop at {fps}fps...");
        var events = Path.Combine(recorder.MetaDir, $"{baseName}.events.csv.gz");
        // Audio path needs a process that actually plays sound - spawn one
        // looping a stock Windows wav and capture ITS process, exactly as a
        // game would be captured.
        Process? noise = null;
        if (config.RecordAudio)
        {
            noise = Process.Start(new ProcessStartInfo("powershell",
                "-NoProfile -c \"$p = New-Object Media.SoundPlayer 'C:\\Windows\\Media\\Alarm01.wav'; $p.PlayLooping(); Start-Sleep 14\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        try
        {
            using var audio = noise is null ? null : ProcessAudioCapture.TryStart(noise.Id);
            var audioIn = audio is null ? "" : $" {ProcessAudioCapture.FfmpegInputArgs}";
            var mapping = audio is null ? "" : "-map 0:v -map 1:a -c:a aac -b:a 160k ";
            using (config.RecordInputs ? InputLogger.TryStart(events) : null)
            {
                await recorder.RunFfmpegAsync(
                    $"-y -f lavfi -i ddagrab=framerate={fps}{audioIn} -t 10 " +
                    $"-c:v h264_nvenc -preset p4 -rc vbr -cq {config.RecordQuality} -b:v 0 -maxrate 25M -bufsize 50M -g {fps * 4} " +
                    $"{mapping}-movflags +frag_keyframe+empty_moov -f mp4 \"{part}\"", ct);
            }
        }
        finally
        {
            try { if (noise is { HasExited: false }) noise.Kill(); } catch { /* already gone */ }
            noise?.Dispose();
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

    /// Phase for decisions made while shutting down - must not throw.
    private async Task<string?> PhaseSafeAsync()
    {
        try { return await PhaseAsync(CancellationToken.None); }
        catch { return null; }
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

    /// Stream-copy remux of a raw segment to faststart, with the BT.709
    /// retag (NVENC's sRGB transfer tag made YouTube gamma-convert every
    /// upload ~20% darker - measured on Game 6 of 24 Jul; pixels untouched,
    /// the matrix tag stays as written since the encoders really do convert
    /// BT.601). A {segment}.pcm beside the video (the WGC path's game audio)
    /// becomes the AAC track here.
    private string RemuxArgs(string src, string dst)
    {
        var pcm = Path.ChangeExtension(src, ".pcm");
        const string bsf = "-bsf:v h264_metadata=colour_primaries=1:transfer_characteristics=1";
        return File.Exists(pcm)
            ? $"-y -i \"{src}\" -f s16le -ar {ProcessAudioCapture.SampleRate} -ch_layout stereo -i \"{pcm}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 160k -shortest {bsf} -movflags +faststart \"{dst}\""
            : $"-y -i \"{src}\" -c copy {bsf} -movflags +faststart \"{dst}\"";
    }

    /// ffmpeg -i with no output exits nonzero by design; the interesting
    /// bits (Duration, the stream list) are on stderr regardless.
    private async Task<string> ProbeAsync(string path, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(ffmpeg, $"-hide_banner -i \"{path}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stderr;
    }

    private static double? ParseDurationSec(string probe)
    {
        var m = Regex.Match(probe, @"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
            + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

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
