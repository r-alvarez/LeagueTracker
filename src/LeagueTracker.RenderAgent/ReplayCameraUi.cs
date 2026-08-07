namespace LeagueTracker.RenderAgent;

/// The replay HUD's camera and fog-of-war dropdowns, driven by clicking.
///
/// Clicking is not a shortcut here - it is the only route. The Replay API has
/// no champion follow-cam: "tps" crashes the game, cameraMode writes wipe the
/// selection, and cameraPosition writes are ignored while a lock is active
/// (established 2026-07-19). Everything that needs the camera to FOLLOW a
/// champion goes through these coordinates.
public static class ReplayCameraUi
{
    public const string GameWindowTitle = "League of Legends (TM) Client";

    // Replay UI geometry as ratios of the client area, calibrated at 2560x1440
    // with default HUD scale (GlobalScaleReplay=1). The camera dropdown lists
    // 13 entries (FPS, Directed, Manual, then the 10 champions in player-list
    // order) stacked upward from the box; the fog dropdown lists Blue/Red/All.
    public const double PanelX = 0.0703;
    public const double CameraBoxY = 0.9167;
    public const double CameraListBottomY = 0.90625;
    public const double DropdownRowH = 0.021806;
    public const double FogX = 0.114;
    public const double FogBoxY = 0.948;
    public const double FogBlueY = 0.8813;
    public const double FogRedY = 0.9035;

    /// The game persists EnableDirectedCamera in game.cfg's [Replay] section
    /// and reads it at launch. Directed camera off matters to both callers:
    /// the render path needs it so a moving camera can only mean the lock
    /// engaged, and the review needs it so the game stops steering away from
    /// the champion being reviewed. Idempotent; call while no game runs.
    public static void EnsureDirectedCameraDisabled(string leagueRoot)
    {
        var cfg = Path.Combine(leagueRoot, "Config", "game.cfg");
        if (!File.Exists(cfg)) return;

        var lines = File.ReadAllLines(cfg).ToList();
        var existing = lines.FindIndex(l => l.Trim().StartsWith("EnableDirectedCamera", StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (lines[existing].Trim().EndsWith("=0")) return;
            lines[existing] = "EnableDirectedCamera=0";
        }
        else
        {
            var replay = lines.FindIndex(l => l.Trim().Equals("[Replay]", StringComparison.OrdinalIgnoreCase));
            if (replay < 0) { lines.Add("[Replay]"); replay = lines.Count - 1; }
            lines.Insert(replay + 1, "EnableDirectedCamera=0");
        }
        File.WriteAllLines(cfg, lines);
        Log.Info("Disabled the directed replay camera in game.cfg");
    }

    /// Where the given player-list slot (0-9) sits in the open camera dropdown.
    public static double CameraRowY(int slotIndex) =>
        CameraListBottomY - (10 - slotIndex) * DropdownRowH + DropdownRowH / 2;

    /// Fog side first, then the champion - the same order the render path
    /// settled on (2026-08-05): the camera clicks are the last thing to touch
    /// the UI, so a fog mis-click's stray open list gets closed by them
    /// instead of showing through. False = the game window could not be
    /// focused, so no click landed anywhere.
    public static async Task<bool> SelectAsync(int slotIndex, bool blue, CancellationToken ct)
    {
        // A freshly-initialized replay UI eats the first clicks; give it a beat.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        if (!GameWindow.TryClickAt(GameWindowTitle, FogX, FogBoxY)) return false;
        await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
        // The dropdown defaults to All (no fog); pick the filmed side.
        // Deterministic click, idempotent when already set.
        GameWindow.TryClickAt(GameWindowTitle, FogX, blue ? FogBlueY : FogRedY);
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);

        GameWindow.TryClickAt(GameWindowTitle, PanelX, CameraBoxY);
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
        GameWindow.TryClickAt(GameWindowTitle, PanelX, CameraRowY(slotIndex));
        // The game hit-tests against the live cursor on the next frame, so
        // moving the cursor in the same instant as the click voids it.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        GameWindow.TryMoveCursor(GameWindowTitle, 0.5, 0.35);
        return true;
    }
}
