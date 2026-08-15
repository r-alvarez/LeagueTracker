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
    private readonly TextBox _server = new() { Width = ContentWidth };
    private readonly TextBox _cfId = new() { Width = ContentWidth };
    private readonly TextBox _cfSecret = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _recordings = new();
    private readonly TextBox _prefix = new() { Width = ContentWidth };
    private readonly Label _verdict = new() { AutoSize = true };
    private readonly Button _test = new() { Text = "Test connection" };
    private readonly Button _save = new() { Text = "Save", DialogResult = DialogResult.OK };

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

    // The site's palette (leaguetracker-web tokens) so the window reads as
    // part of the same product, not a stock dialog.
    private static readonly Color Page = ColorTranslator.FromHtml("#0b0e15");
    private static readonly Color Surface = ColorTranslator.FromHtml("#141926");
    private static readonly Color Field = ColorTranslator.FromHtml("#0f131d");
    private static readonly Color Ink = ColorTranslator.FromHtml("#edf1f8");
    private static readonly Color Muted = ColorTranslator.FromHtml("#8792a5");
    private static readonly Color Grid = ColorTranslator.FromHtml("#222a3b");
    private static readonly Color Accent = ColorTranslator.FromHtml("#4f9cf9");
    private static readonly Color Good = ColorTranslator.FromHtml("#3fb950");
    private static readonly Color Bad = ColorTranslator.FromHtml("#f0556a");
    private const int ContentWidth = 560;

    public SetupForm(AgentConfig current)
    {
        Text = $"LeagueTracker agent {AgentConfig.Version} - setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Page;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9.75f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { /* generic icon then */ }
        HandleCreated += (_, _) =>
        {
            // Dark title bar to match the page (Windows 10 20H1+; older builds ignore it).
            var dark = 1;
            _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        };

        _server.Text = current.ServerUrl.Contains("localhost") ? "" : current.ServerUrl;
        _cfId.Text = current.CfAccessClientId;
        _cfSecret.Text = current.CfAccessClientSecret;
        foreach (var r in Roles) _role.Items.Add(r.Label);
        // A fresh install is almost always a player's PC; the owner's machines
        // already have a config that says otherwise.
        _role.SelectedIndex = NeedsSetup(current) ? 0
            : Array.FindIndex(Roles, r => r.Record == current.RecordGames && r.Render == current.RenderReplays) is >= 0 and var i ? i : 0;
        _recordings.Text = current.RecordingsDir;
        _prefix.Text = current.RecordNamePrefix;

        foreach (var box in new[] { _server, _cfId, _cfSecret, _recordings, _prefix }) StyleField(box);
        _role.FlatStyle = FlatStyle.Flat;
        _role.BackColor = Field;
        _role.ForeColor = Ink;
        _role.Font = Font;
        _role.Width = ContentWidth;
        _role.Margin = new Padding(0, 4, 0, 0);
        _role.DrawMode = DrawMode.OwnerDrawFixed;
        _role.ItemHeight = 22;
        _role.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(selected ? Grid : Field);
            e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _role.Items[e.Index]!.ToString(), Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };

        var root = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 20, 24, 20), BackColor = Page };
        root.Controls.Add(Header());
        root.Controls.Add(Card("Tracker",
            Fields(
                ("Tracker URL", _server, "Your tracker's address, e.g. https://league-ben.rjav-tech.co.uk (several: comma-separated)."),
                ("Access token ID", _cfId, "The Cloudflare Access service token you were given for this machine."),
                ("Access token secret", SecretRow(), null))));
        root.Controls.Add(Card("This machine",
            Fields(("Role", _role, "Recorder for a player's PC; Renderer for the box that cuts replay clips; Both for one machine doing everything."))));
        root.Controls.Add(Card("Recordings",
            Fields(
                ("Recordings folder", RecordingsRow(), "Blank = Videos\\LeagueTracker. Games are 1.5-3 GB each at 1440p60 - pick a drive with room. Work in progress is kept on the system drive automatically."),
                ("Video title prefix", _prefix, "Recordings and YouTube titles: \"<prefix> - 15 Aug 2026 - Game 2\". Blank = the tracker's default."))));
        root.Controls.Add(Footer());
        Controls.Add(root);
        AcceptButton = _save;

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, e) =>
        {
            if (Validate(out var problem)) Save();
            else { _verdict.ForeColor = Bad; _verdict.Text = problem; DialogResult = DialogResult.None; }
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void StyleField(TextBox box)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Field;
        box.ForeColor = Ink;
        box.Font = Font;
        box.Margin = new Padding(0, 4, 0, 0);
    }

    private Control Header()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 16) };
        var logo = new PictureBox { Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 14, 0) };
        try
        {
            using var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("logo.png");
            if (stream is not null) logo.Image = Image.FromStream(stream);
        }
        catch { /* header without the mark */ }
        var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        text.Controls.Add(new Label { Text = "LeagueTracker agent", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Ink, Margin = Padding.Empty });
        text.Controls.Add(new Label { Text = $"Version {AgentConfig.Version} · records your games, publishes them, cuts replay clips", AutoSize = true, ForeColor = Muted, Margin = new Padding(1, 0, 0, 0) });
        panel.Controls.Add(logo);
        panel.Controls.Add(text);
        return panel;
    }

    private Control Card(string title, Control body)
    {
        var card = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Surface, Padding = new Padding(18, 14, 18, 16), Margin = new Padding(0, 0, 0, 12), MinimumSize = new Size(ContentWidth + 36, 0) };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Grid);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new Point(18, 14), Margin = Padding.Empty };
        stack.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Accent, Margin = new Padding(0, 0, 0, 8) });
        stack.Controls.Add(body);
        card.Controls.Add(stack);
        return card;
    }

    private Control Fields(params (string Label, Control Control, string? Hint)[] rows)
    {
        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, control, hint) = rows[i];
            stack.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Ink, Margin = new Padding(0, i == 0 ? 0 : 12, 0, 0) });
            stack.Controls.Add(control);
            if (hint is not null) stack.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 8.75f), MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 4, 0, 0) });
        }
        return stack;
    }

    private Control SecretRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _cfSecret.Width = ContentWidth - 70;
        var show = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Muted, Margin = new Padding(8, 8, 0, 0) };
        show.CheckedChanged += (_, _) => _cfSecret.UseSystemPasswordChar = !show.Checked;
        row.Controls.Add(_cfSecret);
        row.Controls.Add(show);
        return row;
    }

    private Control RecordingsRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _recordings.Width = ContentWidth - 96;
        var browse = GhostButton("Browse…", 88);
        browse.Margin = new Padding(8, 4, 0, 0);
        browse.Height = _recordings.PreferredHeight;
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Where finished recordings go", SelectedPath = _recordings.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _recordings.Text = dialog.SelectedPath;
        };
        row.Controls.Add(_recordings);
        row.Controls.Add(browse);
        return row;
    }

    private Control Footer()
    {
        var footer = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        _verdict.MaximumSize = new Size(ContentWidth + 36, 0);
        _verdict.ForeColor = Muted;
        _verdict.Margin = new Padding(0, 0, 0, 10);
        footer.Controls.Add(_verdict);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Width = ContentWidth + 36, Margin = Padding.Empty };
        var cancel = GhostButton("Cancel", 96);
        cancel.DialogResult = DialogResult.Cancel;
        StyleGhost(_test, 140);
        StylePrimary(_save, 110);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_test);
        footer.Controls.Add(buttons);
        return footer;
    }

    private Button GhostButton(string text, int width)
    {
        var button = new Button { Text = text };
        StyleGhost(button, width);
        return button;
    }

    private void StyleGhost(Button button, int width)
    {
        button.Width = width;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Surface;
        button.ForeColor = Ink;
        button.Font = Font;
        button.FlatAppearance.BorderColor = Grid;
        button.FlatAppearance.MouseOverBackColor = Grid;
        button.Margin = new Padding(8, 0, 0, 0);
    }

    private void StylePrimary(Button button, int width)
    {
        button.Width = width;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9.75f);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#6aacfb");
        button.Margin = new Padding(8, 0, 0, 0);
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
            RecordNamePrefix = _prefix.Text.Trim(),
        };
    }

    private async Task TestAsync()
    {
        if (!Validate(out var problem)) { _verdict.ForeColor = Bad; _verdict.Text = problem; return; }
        _test.Enabled = false;
        _verdict.ForeColor = Muted;
        _verdict.Text = "Contacting the tracker…";
        var draft = Draft();
        var results = new List<string>();
        foreach (var url in draft.ServerUrls)
        {
            var client = TrackerClient.ForServer(url, draft);
            var ok = await client.PingAsync(CancellationToken.None);
            var profile = ok ? await client.GetProfileAsync(CancellationToken.None) : null;
            var accounts = ok ? await client.GetAccountsAsync(CancellationToken.None) : null;
            results.Add(ok
                ? $"{url}: OK{(accounts is { Count: > 0 } ? $" - {accounts.Count} account(s): {string.Join(", ", accounts.Select(a => a.RiotId))}" : "")}{(profile is { Count: > 0 } ? $" (YouTube {(profile.ContainsKey("YouTubeRefreshToken") ? "ready" : "not configured")})" : "")}"
                : $"{url}: no answer - wrong address, or the Access token is not allowed here");
        }
        var allOk = results.All(r => r.Contains(": OK"));
        _verdict.ForeColor = allOk ? Good : Bad;
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
        // Only when given: an empty prefix written locally would win over
        // the tracker's default (a written key beats the profile).
        if (draft.RecordNamePrefix is { Length: > 0 }) Set("RecordNamePrefix", draft.RecordNamePrefix);
        else settings.Remove("RecordNamePrefix");

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath,
            "// Written by the setup window (run the agent with --install to change it).\n" +
            $"// {label}. Everything not listed here comes from the tracker's agent profile.\n" + json + "\n");
        Log.Info($"Settings saved to {Path.GetFileName(ConfigPath)}: {draft.Role} for {draft.ServerUrl}");
    }
}
