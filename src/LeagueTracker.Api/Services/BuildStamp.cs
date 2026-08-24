namespace LeagueTracker.Api.Services;

// When this image was built and when this process started. The Dockerfile
// writes build-info.txt in the runtime stage, so the stamp changes exactly
// when the image content does - a cached rebuild that produces the same image
// keeps the old stamp, which is the honest answer. Absent on a host run.
public static class BuildStamp
{
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    public static DateTime? BuiltUtc { get; } = Read();

    private static DateTime? Read()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "build-info.txt");
            if (!File.Exists(path)) return null;
            return DateTime.TryParse(File.ReadAllText(path).Trim(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
