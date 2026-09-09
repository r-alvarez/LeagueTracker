using System.Drawing;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// The only questions a fresh install has to answer, as a window instead of
/// a JSON file: which tracker, the join code that makes the machine its
/// owner's, what this machine does, where recordings go. Writes appsettings.json
/// (only these keys - everything else comes from the tracker's profile or
/// stays at its default) and can prove the tracker answers before saving.
public sealed class SetupForm : Form
{
    private readonly TextBox _join = new() { Width = ContentWidth };
    private readonly TextBox _server = new() { Width = ContentWidth };
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _recordings = new();
    private readonly TextBox _prefix = new() { Width = ContentWidth };
    private readonly CheckBox _review = new() { Text = "Automatically open replays in the League client", AutoSize = true };
    private readonly CheckBox _notifyReview = new() { Text = "Notify me when a recording is ready", AutoSize = true };
    private readonly Label _verdict = new() { AutoSize = true };
    private readonly Button _test = new() { Text = "Test connection" };
    private readonly Button _save = new() { Text = "Save" };

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
        _join.Text = current.JoinCode;
        foreach (var r in Roles) _role.Items.Add(r.Label);
        // A fresh install is almost always a player's PC; the owner's machines
        // already have a config that says otherwise.
        _role.SelectedIndex = NeedsSetup(current) ? 0
            : Array.FindIndex(Roles, r => r.Record == current.RecordGames && r.Render == current.RenderReplays) is >= 0 and var i ? i : 0;
        _recordings.Text = current.RecordingsDir;
        _prefix.Text = current.RecordNamePrefix;
        _review.Checked = current.PostGameReview;
        _notifyReview.Checked = current.NotifyRecordingReady;
        _notifyReview.ForeColor = Ink;
        _notifyReview.Margin = new Padding(0, 6, 0, 0);
        _review.ForeColor = Ink;
        _review.Margin = new Padding(0, 6, 0, 0);

        foreach (var box in new[] { _join, _server, _recordings, _prefix }) StyleField(box);
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
        root.Controls.Add(Card("Join",
            Fields(("Join code", _join, "The code from the tracker's Data page (\"Add a machine\") - eight letters like K7Q2-9DFM, or the one-line paste that also fills in the address. It makes this machine yours when it enrols (Save does that)."))));
        _join.TextChanged += (_, _) => ApplyJoinCode(_join.Text);
        root.Controls.Add(Card("Tracker",
            Fields(
                ("Tracker URL", _server, "Your tracker's https address, e.g. https://league.rjav-tech.co.uk (several: comma-separated). Plain http is only accepted for localhost: this machine's key and its YouTube credentials travel on every call."))));
        root.Controls.Add(Card("This machine",
            Fields(
                ("Role", _role, "Recorder for a player's PC; Renderer for the box that cuts replay clips; Both for one machine doing everything."),
                ("After each game", _review, "On: about 30 seconds after a game ends (unless you have queued again), the agent opens the replay through your League client, takes the screen for a few minutes, follows your champion through the moments that mattered (F8-F12 to skip or pause) and closes it. Off: nothing opens unless the tracker's owner turned it on for this machine - the review is on your match page either way."),
                ("Notifications", _notifyReview, "Show a tray notification after recording a game. Click it to open gameplay review. Off by default."))));
        root.Controls.Add(Card("Recordings",
            Fields(
                ("Recordings folder", RecordingsRow(), "Blank = Videos\\LeagueTracker. Games are 1.5-3 GB each at 1440p60 - pick a drive with room. Work in progress is kept on the system drive automatically."),
                ("Video title prefix", _prefix, "Recordings and YouTube titles: \"<prefix> - 15 Aug 2026 - Game 2\". Blank = the tracker's default."))));
        root.Controls.Add(Footer());
        Controls.Add(root);
        AcceptButton = _save;

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += async (_, _) => await SaveAndEnrolAsync();
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
            if (dialog.ShowDialog(this) == DialogResult.OK) _recordings.Text = RecordingLedger.OwnedFolder(dialog.SelectedPath);
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

    private void ApplyJoinCode(string text)
    {
        JoinPaste? paste;
        try
        {
            paste = SetupInput.ParsePaste(text);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _verdict.ForeColor = Bad;
            _verdict.Text = "That join code is not readable - ask for it again.";
            return;
        }
        if (paste is null) return;

        var serverApplied = paste.Server is { } server && ApplyPastedServer(server);
        if (paste.Role is { } role)
        {
            var index = role.ToLowerInvariant() switch { "recorder" => 0, "renderer" => 1, "both" or "full" => 2, _ => -1 };
            if (index >= 0) _role.SelectedIndex = index;
        }
        if (paste.Prefix is { } prefix) _prefix.Text = prefix;
        if (paste.Recordings is { } rec) _recordings.Text = rec;
        // Leave the bare code in the box (TextChanged re-enters and returns
        // at once - a bare code is not a paste). The box visibly shrinking
        // to eight letters reads as a half-finished paste, so the verdict
        // says out loud that the rest was unpacked, not lost.
        if (paste.Code is { } kept) _join.Text = kept;
        _verdict.ForeColor = Good;
        var address = serverApplied ? $"the tracker address ({SetupInput.Host(_server.Text)})" : "the tracker address was left as it is";
        _verdict.Text = paste.Code is { } code
            ? $"Join code {SetupInput.Pretty(code)} came out of that paste and {address} - Save enrols this machine."
            : $"That paste carried no code, {address} - type the join code as well, then Save.";
    }

    // The address in a paste is where this machine sends its key from then
    // on, and a paste is one line from wherever it was handed over - so it
    // never replaces an address already in the box without being asked.
    private bool ApplyPastedServer(string server)
    {
        var current = _server.Text.Trim();
        if (current is { Length: > 0 } && !string.Equals(current.TrimEnd('/'), server.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(this,
                $"This join code points at a different tracker:\n\n    {SetupInput.Host(server)}\n    ({server})\n\nThe box currently says {SetupInput.Host(current)}. Use the pasted address?",
                "Change the tracker address?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer is not DialogResult.Yes) return false;
        }
        _server.Text = server;
        return true;
    }

    private bool Validate(out string problem)
    {
        problem = SetupInput.ServerUrlProblem(_server.Text) ?? "";
        return problem is not { Length: > 0 };
    }

    private AgentConfig Draft()
    {
        var (_, record, render) = Roles[_role.SelectedIndex];
        return new AgentConfig
        {
            ServerUrl = string.Join(",", _server.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            JoinCode = SetupInput.IsPaste(_join.Text.Trim()) ? "" : _join.Text.Trim().Replace("-", ""),
            RecordGames = record,
            RenderReplays = render,
            RecordingsDir = _recordings.Text.Trim(),
            RecordNamePrefix = _prefix.Text.Trim(),
            PostGameReview = _review.Checked,
            NotifyRecordingReady = _notifyReview.Checked,
        };
    }

    // Test only asks whether the address answers. Enrolling here would hand
    // the machine's key to whatever the box says before the person has
    // decided to keep it (a wrong or pasted address) - that is Save's job.
    private async Task TestAsync()
    {
        if (!Validate(out var problem)) { _verdict.ForeColor = Bad; _verdict.Text = problem; return; }
        _test.Enabled = false;
        _verdict.ForeColor = Muted;
        _verdict.Text = "Contacting the tracker…";
        var draft = Draft();
        List<string> results = [];
        foreach (var url in draft.ServerUrls)
        {
            var client = TrackerClient.ForServer(url, draft);
            results.Add(await client.PingAnonymousAsync(CancellationToken.None)
                ? $"{url}: OK - a LeagueTracker server answers; Save enrols this machine there"
                : await client.PingAsync(CancellationToken.None)
                    ? $"{url}: OK - reachable, though it offers no agent enrolment; is the tracker up to date?"
                    : $"{url}: no answer - wrong address, or not reachable from here");
        }
        _verdict.ForeColor = results.All(r => r.Contains(": OK")) ? Good : Bad;
        _verdict.Text = string.Join("\n", results);
        _test.Enabled = true;
    }

    // Written first, then enrolled: the agent that starts afterwards re-announces
    // itself from the same file and key, so a failed or pending enrolment here
    // only costs the person the early "waiting for approval" line.
    private async Task SaveAndEnrolAsync()
    {
        if (!Validate(out var problem)) { _verdict.ForeColor = Bad; _verdict.Text = problem; return; }
        _save.Enabled = false;
        _test.Enabled = false;
        Save();
        _verdict.ForeColor = Muted;
        _verdict.Text = "Saved - enrolling this machine…";
        var draft = Draft();
        List<string> results = [];
        if (!Review.WebViewRuntime.IsInstalled())
        {
            _verdict.Text = "Preparing gameplay review… This may take a few minutes.";
            var runtime = await Review.WebViewRuntime.EnsureAsync(CancellationToken.None);
            if (!runtime.Available) results.Add(runtime.Error!);
            if (IsDisposed) return;
            _verdict.Text = "Saved - enrolling this machine…";
        }
        foreach (var url in draft.ServerUrls)
        {
            var client = TrackerClient.ForServer(url, draft);
            results.Add(await client.EnrollAsync(CancellationToken.None) switch
            {
                "approved" => $"{url}: this machine is approved",
                "pending" => $"{url}: this machine is waiting for approval on the site's Data page - it starts by itself once approved",
                "revoked" => $"{url}: this machine was revoked - ask the owner to re-approve it",
                { } refused when refused.StartsWith("refused:", StringComparison.Ordinal) => $"{url}: enrolment refused - {refused[8..]}",
                _ => $"{url}: no enrolment answer - the agent keeps trying once it runs",
            });
        }
        EnrolmentVerdict = string.Join("\n", results);
        Log.Info($"Enrolment on save: {EnrolmentVerdict.Replace('\n', ' ')}");
        // Cancel during the enrolment round-trip already closed the window.
        if (IsDisposed) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    // For the caller's summary: the window is gone by the time it reads this.
    public string? EnrolmentVerdict { get; private set; }

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
        Set("JoinCode", draft.JoinCode);
        Set("RecordGames", draft.RecordGames);
        Set("RenderReplays", draft.RenderReplays);
        Set("RecordingsDir", draft.RecordingsDir);
        // Ticked is an explicit yes. Unticked leaves it to the tracker's
        // profile, which is where an owner turns it on for a machine without
        // touching it - and this window runs in its own process and never
        // sees that profile, so a written "false" would silently beat it.
        if (draft.PostGameReview) Set("PostGameReview", true);
        else settings.Remove("PostGameReview");
        Set("NotifyRecordingReady", draft.NotifyRecordingReady);
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
