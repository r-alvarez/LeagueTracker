namespace LeagueTracker.RenderAgent;

// What in the recordings folder is this agent's to prune, probe and publish.
// The folder is the person's choice and may be their own Videos; nothing in
// it is ours unless we made it (audit G-N1).
public static class RecordingLedger
{
    public const string MetaFolder = "metadata";

    // Every recording this agent made left at least one sidecar under its
    // name in metadata/ - .inflight.json while recording, telemetry, the
    // finalized .json, the delivery marks. ".orphan" is not proof: older
    // builds wrote it for any mp4 they could not place, theirs or not.
    public static bool IsOurs(string metaDir, string baseName) =>
        Directory.Exists(metaDir)
        && Directory.EnumerateFiles(metaDir, baseName + ".*").Any(sidecar => !sidecar.EndsWith(".orphan", StringComparison.OrdinalIgnoreCase));

    // Browse... to a folder that already holds videos and no ledger means
    // "put them next to my things", not "these are yours": record into a
    // subfolder so the budget and adoption passes never meet them.
    public static string OwnedFolder(string chosen)
    {
        if (!Directory.Exists(chosen) || Directory.Exists(Path.Combine(chosen, MetaFolder))) return chosen;
        return Directory.EnumerateFiles(chosen, "*.mp4").Any() ? Path.Combine(chosen, "LeagueTracker") : chosen;
    }
}
