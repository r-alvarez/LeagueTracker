using System.Diagnostics;
using Microsoft.Win32;

namespace LeagueTracker.RenderAgent.Review;

internal sealed record RuntimeSetupResult(bool Available, string? Error = null);

internal static class WebViewRuntime
{
    private const string ClientKey = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string Failure = "Gameplay review could not be prepared. Check your internet connection and try again. Recording can continue.";

    internal static bool ValidVersion(string? value) => Version.TryParse(value, out var version) && version > new Version(0, 0, 0, 0);

    public static bool IsInstalled()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
                using var key = root.OpenSubKey(ClientKey);
                if (ValidVersion(key?.GetValue("pv") as string)) return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return false;
    }

    public static Task<RuntimeSetupResult> EnsureAsync(CancellationToken ct) => EnsureAsync(IsInstalled, InstallAsync, ct);

    internal static async Task<RuntimeSetupResult> EnsureAsync(Func<bool> installed, Func<CancellationToken, Task<int>> install, CancellationToken ct)
    {
        if (installed()) return new(true);
        try
        {
            ct.ThrowIfCancellationRequested();
            var code = await install(ct);
            if (installed()) return new(true);
            Log.Warn($"WebView2 preparation did not register a runtime (exit {code}).");
            return new(false, Failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            Log.Warn($"WebView2 preparation failed: {ex.Message}");
            return new(false, Failure);
        }
    }

    public static async Task<int> PrepareAsync()
    {
        var result = await EnsureAsync(CancellationToken.None);
        if (!result.Available) Log.Warn(result.Error!);
        return result.Available ? 0 : 1;
    }

    private static async Task<int> InstallAsync(CancellationToken ct)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueTracker", "setup");
        Directory.CreateDirectory(root);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await using var gate = await AcquireAsync(Path.Combine(root, "webview2.lock"), timeout.Token);
        if (IsInstalled()) return 0;
        using var resource = typeof(WebViewRuntime).Assembly.GetManifestResourceStream("webview2-bootstrapper.exe")
            ?? throw new IOException("This build is missing the WebView2 bootstrapper. Rebuild with deploy/get-webview-bootstrapper.ps1.");
        var folder = Path.Combine(root, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await resource.CopyToAsync(file, timeout.Token);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("/silent");
            start.ArgumentList.Add("/install");
            using var process = Process.Start(start) ?? throw new IOException("Could not start the WebView2 bootstrapper.");
            // Do not kill Microsoft's shared installer if the review window closes.
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        finally
        {
            try { File.Delete(path); Directory.Delete(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(500, ct); }
        }
    }
}
