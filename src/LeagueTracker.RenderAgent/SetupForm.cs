using System.Drawing;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// The only questions a fresh install has to answer, as a window instead of
/// a JSON file: which tracker, the Access token that gets through its wall,
/// what this machine does, where recordings go. Writes appsettings.json
/// (only these keys - everything else comes from the tracker's profile or
/// stays at its default) and can prove the tracker answers before saving.
public sealed class SetupForm : Form
{
    private readonly TextBox _server = new() { Width = 420 };
    private readonly TextBox _cfId = new() { Width = 420 };
    private readonly TextBox _cfSecret = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly ComboBox _role = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _recordings = new() { Width = 340 };
    private readonly Label _verdict = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DimGray };
    private readonly Button _test = new() { Text = "Test connection", Width = 130 };
    private readonly Button _save = new() { Text = "Save", Width = 100, DialogResult = DialogResult.OK };

    private static readonly (string Label, bool Record, bool Render)[] Roles =
    [
        ("Recorder - record my games and publish them (a player's PC)", true, false),
        ("Renderer - cut replay clips for every tracker (dedicated box)", false, true),
        ("Both - record and render on this machine", true, true),
    ];

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// A config that says nothing useful yet: missing, or the template's
    /// localhost placeholder.
    public static bool NeedsSetup(AgentConfig config) =>
        !File.Exists(ConfigPath) || config.ServerUrls is not { Length: > 0 } || config.ServerUrl.Contains("localhost");

    public SetupForm(AgentConfig current)
    {
        Text = $"LeagueTracker agent {AgentConfig.Version} - setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { /* generic icon then */ }

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string label, Control control, string? hint = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) });
            var cell = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            cell.Controls.Add(control);
            if (hint is not null) cell.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 6) });
            grid.Controls.Add(cell);
        }

        _server.Text = current.ServerUrl.Contains("localhost") ? "" : current.ServerUrl;
        _cfId.Text = current.CfAccessClientId;
        _cfSecret.Text = current.CfAccessClientSecret;
        foreach (var r in Roles) _role.Items.Add(r.Label);
        // A fresh install is almost always a player's PC; the owner's machines
        // already have a config that says otherwise.
        _role.SelectedIndex = NeedsSetup(current) ? 0
            : Array.FindIndex(Roles, r => r.Record == current.RecordGames && r.Render == current.RenderReplays) is >= 0 and var i ? i : 0;
        _recordings.Text = current.RecordingsDir;

        Row("Tracker URL", _server, "Your tracker's address, e.g. https://league-ben.rjav-tech.co.uk (several: comma-separated).");
        Row("Access token ID", _cfId, "The Cloudflare Access service token you were given for this machine.");
        Row("Access token secret", _cfSecret);
        Row("This machine is", _role);

        var browse = new Button { Text = "Browse…", Width = 76 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Where finished recordings go", SelectedPath = _recordings.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _recordings.Text = dialog.SelectedPath;
        };
        var recRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        recRow.Controls.Add(_recordings);
        recRow.Controls.Add(browse);
        Row("Recordings folder", recRow, "Blank = Videos\\LeagueTracker. Games are 1.5-3 GB each at 1440p60 - pick a drive with room.");

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_test);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel });

        var root = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        root.Controls.Add(grid);
        root.Controls.Add(_verdict);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = _save;

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, e) =>
        {
            if (Validate(out var problem)) Save();
            else { _verdict.ForeColor = Color.Firebrick; _verdict.Text = problem; DialogResult = DialogResult.None; }
        };
    }

    private bool Validate(out string problem)
    {
        problem = "";
        if (_server.Text.Trim() is not { Length: > 0 } || !_server.Text.Split(',').All(u => Uri.TryCreate(u.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            problem = "Tracker URL must be one or more http(s) addresses.";
            return false;
        }
        if (_server.Text.Contains("https://") && (_cfId.Text.Trim() is not { Length: > 0 } || _cfSecret.Text.Trim() is not { Length: > 0 }))
        {
            problem = "An https tracker sits behind Cloudflare Access - both token fields are needed.";
            return false;
        }
        return true;
    }

    private AgentConfig Draft()
    {
        var (_, record, render) = Roles[_role.SelectedIndex];
        return new AgentConfig
        {
            ServerUrl = string.Join(",", _server.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            CfAccessClientId = _cfId.Text.Trim(),
            CfAccessClientSecret = _cfSecret.Text.Trim(),
            RecordGames = record,
            RenderReplays = render,
            RecordingsDir = _recordings.Text.Trim(),
        };
    }

    private async Task TestAsync()
    {
        if (!Validate(out var problem)) { _verdict.ForeColor = Color.Firebrick; _verdict.Text = problem; return; }
        _test.Enabled = false;
        _verdict.ForeColor = Color.DimGray;
        _verdict.Text = "Contacting the tracker…";
        var draft = Draft();
        var results = new List<string>();
        foreach (var url in draft.ServerUrls)
        {
            var client = new TrackerClient(url, draft);
            var ok = await client.PingAsync(CancellationToken.None);
            var profile = ok ? await client.GetProfileAsync(CancellationToken.None) : null;
            results.Add(ok
                ? $"{url}: OK{(profile is { Count: > 0 } ? $" (profile: {profile.Count} setting(s), YouTube {(profile.ContainsKey("YouTubeRefreshToken") ? "ready" : "not configured")})" : "")}"
                : $"{url}: no answer - wrong address, or the Access token is not allowed here");
        }
        var allOk = results.All(r => r.Contains(": OK"));
        _verdict.ForeColor = allOk ? Color.ForestGreen : Color.Firebrick;
        _verdict.Text = string.Join("\n", results);
        _test.Enabled = true;
    }

    /// Rewrites only its own keys; anything else already in the file (a
    /// hand-tuned install like the owner's) survives - the comments do not,
    /// which is the price of not owning a JSONC writer.
    private void Save()
    {
        var draft = Draft();
        var (label, _, _) = Roles[_role.SelectedIndex];
        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var property in doc.RootElement.EnumerateObject()) settings[property.Name] = property.Value.Clone();
            }
            catch (JsonException) { /* unreadable - start over with just ours */ }
        }
        void Set<T>(string key, T value) => settings[key] = JsonSerializer.SerializeToElement(value);
        Set("ServerUrl", draft.ServerUrl);
        Set("CfAccessClientId", draft.CfAccessClientId);
        Set("CfAccessClientSecret", draft.CfAccessClientSecret);
        Set("RecordGames", draft.RecordGames);
        Set("RenderReplays", draft.RenderReplays);
        Set("RecordingsDir", draft.RecordingsDir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath,
            "// Written by the setup window (run the agent with --install to change it).\n" +
            $"// {label}. Everything not listed here comes from the tracker's agent profile.\n" + json + "\n");
        Log.Info($"Settings saved to {Path.GetFileName(ConfigPath)}: {draft.Role} for {draft.ServerUrl}");
    }
}
