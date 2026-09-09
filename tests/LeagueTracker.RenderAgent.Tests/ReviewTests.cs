using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueTracker.RenderAgent.Review;

namespace LeagueTracker.RenderAgent.Tests;

public sealed class ReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-review-" + Guid.NewGuid().ToString("n"));
    private readonly RecordingLibrary _library;
    public ReviewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "metadata"));
        _library = new(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private LocalRecording Add(string name, int ageDays = 1, bool published = false)
    {
        var date = DateTime.UtcNow.AddDays(-ageDays);
        File.WriteAllBytes(Path.Combine(_root, name + ".mp4"), Enumerable.Range(0, 100).Select(n => (byte)n).ToArray());
        File.SetLastWriteTimeUtc(Path.Combine(_root, name + ".mp4"), date);
        File.WriteAllText(Path.Combine(_root, "metadata", name + ".json"), JsonSerializer.Serialize(new
        {
            videoFile = name + ".mp4", matchId = "EUW1_123", activePlayer = "Player#EUW",
            recordingStartUtc = date, recordingEndUtc = date.AddMinutes(30), clockMap = new[] { new { videoSec = 0, gameSec = 15 } },
        }));
        if (published) File.WriteAllText(Path.Combine(_root, "metadata", name + ".review-published"), "true");
        return _library.List().Single(r => r.Name == name);
    }

    [Fact]
    public void Sidecar_upload_is_not_a_backup_of_the_video()
    {
        var r = Add("game");
        File.WriteAllText(Path.Combine(_root, "metadata", "game.uploaded"), "sidecars sent");
        Assert.False(_library.Find(r.Id).Published);
        Assert.Throws<InvalidOperationException>(() => _library.Delete(r.Id, false));
        Assert.True(File.Exists(_library.VideoPath(r)));
    }

    [Fact]
    public void Pins_and_unpublished_upload_backlogs_survive_pressure()
    {
        var pinned = Add("pinned", 4, true);
        _library.Pin(pinned.Id, true);
        var pending = Add("pending", 3);
        var old = Add("published", 2, true);
        Assert.Equal(1, _library.Prune(new(1, 1, 10), true, () => 0));
        Assert.True(File.Exists(_library.VideoPath(pinned)));
        Assert.True(File.Exists(_library.VideoPath(pending)));
        Assert.False(File.Exists(_library.VideoPath(old)));
        Assert.Throws<InvalidOperationException>(() => _library.Delete(pinned.Id, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_upload_settings_cannot_reinterpret_a_legacy_sidecar_receipt(bool uploadVods)
    {
        var r = Add("legacy");
        File.WriteAllText(Path.Combine(_root, "metadata", "legacy.uploaded"), "sidecars sent");
        var config = new AgentConfig { RecordingsDir = _root, UploadVods = uploadVods, YouTubeUpload = false };
        var recorder = new GameRecorder(config, "", "", []);
        await recorder.MarkPublishedIfSafeAsync("legacy", CancellationToken.None);
        Assert.False(_library.Find(r.Id).Published);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_receipts_become_published_only_after_a_matching_server_copy_is_confirmed(bool matches)
    {
        var r = Add("legacy-backed-up");
        var metadataPath = Path.Combine(_root, "metadata", r.Name + ".json");
        File.WriteAllText(Path.Combine(_root, "metadata", r.Name + ".uploaded"), "legacy receipt");
        var config = new AgentConfig { RecordingsDir = _root, UploadVods = true, YouTubeUpload = false };
        using var handler = new Handler
        {
            Reply = _ =>
            {
                Assert.Throws<IOException>(() => File.Delete(_library.VideoPath(r)));
                Assert.Throws<IOException>(() => File.WriteAllText(metadataPath, "{}"));
                var status = new JsonObject { ["exists"] = true, ["sizeBytes"] = matches ? r.SizeBytes : r.SizeBytes - 1, ["meta"] = JsonNode.Parse(File.ReadAllText(metadataPath)) };
                return new(HttpStatusCode.OK) { Content = new StringContent(status.ToJsonString()) };
            },
        };
        var tracker = new TrackerClient("https://tracker.test", "https://tracker.test/api", null, config, true, handler);
        var recorder = new GameRecorder(config, "", "", [tracker]);
        await recorder.MarkPublishedIfSafeAsync(r.Name, CancellationToken.None);
        Assert.Equal(matches, _library.Find(r.Id).Published);
        Assert.True(File.Exists(_library.VideoPath(r)));
    }

    [Fact]
    public void Local_only_recordings_rotate_and_foreign_videos_survive()
    {
        var older = Add("older", 4);
        var newer = Add("newer", 2);
        File.WriteAllText(Path.Combine(_root, "holiday.mp4"), "not ours");
        Assert.Equal(1, _library.Prune(new(1, 1, 10), false, () => 100));
        Assert.False(File.Exists(_library.VideoPath(older)));
        Assert.True(File.Exists(_library.VideoPath(newer)));
        Assert.True(File.Exists(Path.Combine(_root, "holiday.mp4")));
        Assert.True(File.Exists(Path.Combine(_root, "metadata", "older.json")));
    }

    [Fact]
    public void Playback_lease_prevents_delete_and_pruning()
    {
        var r = Add("playing", 4, true);
        using (var stream = _library.OpenVideo(r.Id))
        {
            Assert.Throws<IOException>(() => _library.Delete(r.Id, true));
            Assert.Equal(0, _library.Prune(new(1, 1, 10), false, () => 0));
        }
        _library.Delete(r.Id, true);
        Assert.False(File.Exists(_library.VideoPath(r)));
    }

    [Fact]
    public void Finalizing_files_are_not_playable_or_deletable()
    {
        var r = Add("finalizing");
        File.WriteAllText(Path.Combine(_root, "metadata", "finalizing.inflight.json"), "{}");
        Assert.False(_library.Find(r.Id).Available);
        Assert.Throws<FileNotFoundException>(() => _library.OpenVideo(r.Id));
        Assert.Throws<IOException>(() => _library.Delete(r.Id, true));
    }

    [Fact]
    public void Keep_all_preserves_unpinned_games_and_preferences_survive_profile_changes()
    {
        Add("kept");
        _library.SaveSettings(new(12, 30, 8, true));
        var settings = _library.Settings(new AgentConfig { MaxRecordingsGb = 2, KeepRecordingsAfterPublish = false });
        Assert.Equal(30, settings.MaxGb);
        Assert.True(settings.KeepAll);
        Assert.Equal(0, _library.Prune(settings, false, () => 0));
    }

    [Fact]
    public void Paths_and_malformed_sidecars_cannot_escape_the_library()
    {
        Assert.Throws<ArgumentException>(() => _library.SafePath("../outside.mp4"));
        Assert.Throws<FileNotFoundException>(() => _library.Find("../outside"));
        File.WriteAllText(Path.Combine(_root, "metadata", "bad.json"), "{ broken");
        File.WriteAllText(Path.Combine(_root, "metadata", "escape.json"), "{\"videoFile\":\"../outside.mp4\"}");
        Assert.Empty(_library.List());
    }

    [Fact]
    public void Apm_is_derived_locally_and_reused()
    {
        var r = Add("inputs");
        using (var file = File.Create(Path.Combine(_root, "metadata", "inputs.events.csv.gz")))
        using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip))
        {
            writer.WriteLine("t_ms,event_type,input_name,value_a,value_b");
            writer.WriteLine("100,key_down,Q,0,0"); writer.WriteLine("150,mouse_move,,0,0");
            writer.WriteLine("10100,mouse_down,Left,0,0"); writer.WriteLine("10200,wheel,,0,0");
            writer.WriteLine("9223372036854775807,key_down,Q,0,0");
        }
        var result = _library.Apm(r)!;
        Assert.Equal(6, result["apm"]![0]!.GetValue<int>());
        Assert.Equal(12, result["apm"]![1]!.GetValue<int>());
        Assert.Equal(9, result["averageApm"]!.GetValue<int>());
        Assert.Equal(result.ToJsonString(), _library.Apm(r)!.ToJsonString());
    }

    [Theory]
    [InlineData("/matches/EUW1_123/track", true)]
    [InlineData("/matches?page=2&pageSize=20", true)]
    [InlineData("/matches?page=25&pageSize=200", true)]
    [InlineData("https://example.com/matches/123", false)]
    [InlineData("/matches/../admin", false)]
    [InlineData("/matches/%2e%2e/admin", false)]
    [InlineData("/matches/EUW1_123?url=https://example.com", false)]
    [InlineData("/matches?page=1&pageSize=999999", false)]
    [InlineData("/matches/EUW1_123/vod", false)]
    [InlineData("/matches/EUW1_123\n", false)]
    [InlineData("/matches?page=1&pageSize=20\n", false)]
    public void Only_review_routes_are_proxied(string path, bool allowed) => Assert.Equal(allowed, ReviewApi.IsReviewPath(path));

    [Theory]
    [InlineData("https://ddragon.leagueoflegends.com/cdn/16.1/img/map/map11.png", true)]
    [InlineData("https://raw.communitydragon.org/latest/a.json", true)]
    [InlineData("https://ddragon.leagueoflegends.com.evil.example/a.png", false)]
    [InlineData("https://user@ddragon.leagueoflegends.com/a.png", false)]
    [InlineData("http://127.0.0.1/secret.json", false)]
    [InlineData("https://ddragon.leagueoflegends.com/a.js", false)]
    public void Artwork_proxy_has_no_arbitrary_URL_capability(string url, bool allowed) => Assert.Equal(allowed, ReviewMediaServer.AllowedArt(url));

    [Theory]
    [InlineData("Lobby", false)]
    [InlineData("EndOfGame", false)]
    [InlineData("None", false)]
    [InlineData("ChampSelect", true)]
    [InlineData("InProgress", true)]
    public void Review_remains_available_between_games(string phase, bool blocked) => Assert.Equal(blocked, ReviewForm.GamePhaseBlocksReview(phase));

    [Theory]
    [InlineData("https://app.leaguetracker.invalid/desktop.html", true)]
    [InlineData("https://app.leaguetracker.invalid.evil.test/desktop.html", false)]
    [InlineData("https://app.leaguetracker.invalid/other.html", false)]
    public void Bridge_is_bound_to_the_installed_page(string url, bool allowed) => Assert.Equal(allowed, ReviewForm.TrustedPage(url));

    [Fact]
    public async Task Local_media_supports_seek_head_and_rejects_untrusted_requests()
    {
        var r = Add("media");
        using var api = new ReviewApi(new AgentConfig { ServerUrl = "" }, Path.Combine(_root, "cache"), key: "test-key");
        await using var server = new ReviewMediaServer(_library, api, Path.Combine(_root, "cache"));
        await server.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Local(r.Id));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(20, 29);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 20-29/100", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(Enumerable.Range(20, 10).Select(n => (byte)n), await response.Content.ReadAsByteArrayAsync());
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Local(r.Id)));
        Assert.Equal(100, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, server.Local(r.Id));
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(200, 300);
        using var outOfRange = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outOfRange.StatusCode);
        using var noToken = await client.GetAsync(server.Address + "/local/" + r.Id);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, server.Local(r.Id));
        foreign.Headers.Add("Origin", "https://evil.example");
        using var denied = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => throw new HttpRequestException();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Reply(request));
    }

    [Fact]
    public async Task Desktop_account_discovery_uses_the_personal_endpoint_and_does_not_fall_back_to_renderer_scope()
    {
        var paths = new List<string>();
        var handler = new Handler
        {
            Reply = request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath == "/api/agent/accounts"
                    ? new(HttpStatusCode.OK) { Content = new StringContent("{\"accounts\":[{\"id\":\"somebody-else\",\"region\":\"euw\",\"slug\":\"Other-EUW\"}]}", Encoding.UTF8, "application/json") }
                    : new(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            },
        };
        using var api = new ReviewApi(new AgentConfig { ServerUrl = "https://tracker.example" }, Path.Combine(_root, "cache"), handler, "private-test-key");

        var result = JsonSerializer.SerializeToNode(await api.DiscoverAsync(true, CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Empty(result["accounts"]!.AsArray());
        Assert.Equal(["/api/agent/review/accounts"], paths);
    }

    [Fact]
    public async Task Remote_head_handles_GET_only_trackers_and_keeps_authentication_native()
    {
        var handler = new Handler();
        handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"accounts\":[{\"id\":\"owner\",\"region\":\"euw\",\"slug\":\"Player-EUW\"}]}", Encoding.UTF8, "application/json") };
        using var api = new ReviewApi(new AgentConfig { ServerUrl = "https://tracker.example" }, Path.Combine(_root, "cache"), handler, "private-test-key");
        var accounts = JsonSerializer.SerializeToNode(await api.DiscoverAsync(true, CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var account = accounts["accounts"]![0]!["id"]!.GetValue<string>();
        var methods = new List<HttpMethod>();
        handler.Reply = request =>
        {
            methods.Add(request.Method);
            Assert.Equal("private-test-key", request.Headers.GetValues("X-Agent-Key").Single());
            Assert.DoesNotContain("private-test-key", request.RequestUri!.AbsoluteUri);
            Assert.Equal("bytes=10-19", request.Headers.Range!.ToString());
            return request.Method == HttpMethod.Head
                ? new(HttpStatusCode.MethodNotAllowed)
                : new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };
        };
        using var response = await api.OpenMediaAsync(account, "/matches/EUW1_123/vod", "bytes=10-19", true, CancellationToken.None);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, methods);
    }

    [Fact]
    public async Task Cached_accounts_and_analysis_work_offline_but_a_live_denial_wins()
    {
        var handler = new Handler();
        handler.Reply = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath.EndsWith("/accounts")
                ? "{\"accounts\":[{\"id\":\"owner\",\"region\":\"euw\",\"slug\":\"Player-EUW\",\"riotId\":\"Player#EUW\"}]}" : "{\"summary\":{\"id\":\"EUW1_123\"}}", Encoding.UTF8, "application/json") };
        using var api = new ReviewApi(new AgentConfig { ServerUrl = "https://tracker.example" }, Path.Combine(_root, "cache"), handler, "private-test-key");
        var accounts = JsonSerializer.SerializeToNode(await api.DiscoverAsync(true, CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var account = accounts["accounts"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(200, (await api.GetAsync(account, "/matches/EUW1_123", true, CancellationToken.None)).Status);
        handler.Reply = _ => throw new HttpRequestException("offline");
        Assert.True((await api.GetAsync(account, "/matches/EUW1_123", true, CancellationToken.None)).Cached);
        Assert.Throws<UnauthorizedAccessException>(() => api.Account("someone-else"));
        handler.Reply = _ => new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        Assert.Equal(403, (await api.GetAsync(account, "/matches/EUW1_123", true, CancellationToken.None)).Status);
        handler.Reply = _ => throw new HttpRequestException("offline again");
        Assert.Equal(503, (await api.GetAsync(account, "/matches/EUW1_123", true, CancellationToken.None)).Status);
    }
}
