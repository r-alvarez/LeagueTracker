using System.Diagnostics;
using System.Text.Json;
using LeagueTracker.RenderAgent;
using LeagueTracker.RenderAgent.Review;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        Application.EnableVisualStyles();
        using var form = new ReviewForm(new AgentConfig { ServerUrl = "", RecordingsDir = root }, false, "ReviewSmoke." + Guid.NewGuid().ToString("N"));
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        Exception? failure = null;
        var ids = new List<int>();
        var timer = Stopwatch.StartNew();
        form.Shown += async (_, _) =>
        {
            try
            {
                var view = form.Controls.OfType<WebView2>().Single();
                while (view.CoreWebView2 is null && timer.Elapsed.TotalSeconds < 30) await Task.Delay(100);
                var core = view.CoreWebView2 ?? throw new Exception("WebView did not start");
                async Task<string> Js(string script) => JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(script)) ?? "";
                async Task Wait(string script)
                {
                    var start = Stopwatch.StartNew();
                    while (await core.ExecuteScriptAsync(script) != "true")
                    {
                        if (start.Elapsed.TotalSeconds > 30) throw new Exception("Timed out: " + script + " DOM=" + await Js("document.body.innerText"));
                        await Task.Delay(100);
                    }
                }
                await Wait("!!document.querySelector('.recording-open')");
                Console.WriteLine("Library ready ms=" + timer.ElapsedMilliseconds);
                Console.WriteLine("Process creation to library ready ms=" + Math.Round((DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds));
                await using (var screenshot = File.Create(Path.Combine(root, "library.png"))) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                await core.ExecuteScriptAsync("document.querySelector('.recording-open').click()");
                await Wait("!!document.querySelector('video') && document.querySelector('video').readyState >= 1");
                Console.WriteLine("Metadata ready ms=" + timer.ElapsedMilliseconds);
                await core.ExecuteScriptAsync("document.querySelector('video').muted=true; document.querySelector('video').play()");
                await Wait("document.querySelector('video').currentTime > 0.2");
                var seek = Stopwatch.StartNew();
                await core.ExecuteScriptAsync("document.querySelector('video').currentTime=12");
                await Wait("document.querySelector('video').currentTime >= 12 && !document.querySelector('video').seeking");
                Console.WriteLine("Seek ms=" + seek.ElapsedMilliseconds);
                await Task.Delay(1000);
                await using (var screenshot = File.Create(Path.Combine(root, "player.png"))) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                ids = core.Environment.GetProcessInfos().Select(p => p.ProcessId).ToList();
                long working = Process.GetCurrentProcess().WorkingSet64;
                foreach (var id in ids) { using var process = Process.GetProcessById(id); working += process.WorkingSet64; }
                Console.WriteLine("Host+WebView working set MiB=" + Math.Round(working / 1048576d, 1) + " Browser processes=" + ids.Count);
                Console.WriteLine("Player=" + await Js("JSON.stringify({duration:document.querySelector('video').duration,time:document.querySelector('video').currentTime,error:document.querySelector('video').error})"));
                Console.WriteLine("Brand=" + await Js("JSON.stringify({text:document.querySelector('.desktop-brand').textContent,color:getComputedStyle(document.querySelector('.desktop-brand')).color})"));
            }
            catch (Exception e) { failure = e; Console.WriteLine(e); }
            finally { form.Close(); }
        };
        Application.Run(form);
        form.Dispose();
        var stop = Stopwatch.StartNew();
        bool Alive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch { return false; } }
        while (ids.Any(Alive) && stop.Elapsed.TotalSeconds < 10) Thread.Sleep(100);
        Console.WriteLine("Remaining WebView processes=" + ids.Count(Alive));
        return failure is null && !ids.Any(Alive) ? 0 : 1;
    }
}
