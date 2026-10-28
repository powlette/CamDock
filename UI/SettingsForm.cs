using CamDock.Core;

namespace CamDock.UI;

internal sealed class SettingsForm : Form
{
    private readonly TrayApp _app;
    public TabControl Tabs { get; private set; } = null!;
    private readonly ConnectionPanel _conn = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly Label _current = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 10) };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _encrypted = new() { Text = "Use encrypted RTSPS (port 7441) instead of RTSP (port 7447)", AutoSize = true };
    private readonly NumericUpDown _buffer = new() { Minimum = 100, Maximum = 5000, Increment = 100, Width = 80 };

    private readonly CheckBox _notifyEnabled = new() { Text = "Show desktop notifications for camera events", AutoSize = true };
    private readonly CheckBox _notifySnap = new() { Text = "Include a camera snapshot in notifications", AutoSize = true };
    private readonly NumericUpDown _cooldown = new() { Minimum = 0, Maximum = 3600, Width = 70 };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, BackgroundColor = SystemColors.Window,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BorderStyle = BorderStyle.FixedSingle,
    };

    private readonly CheckBox _offline = new() { Text = "Notify when a camera or the NVR is offline for 2 minutes, and when it's back", AutoSize = true };

    private readonly CheckBox _popupDoorbell = new() { Text = "Pop up the doorbell's live video when it rings", AutoSize = true };
    private readonly NumericUpDown _popupSeconds = new() { Minimum = 10, Maximum = 600, Increment = 10, Width = 70 };
    private readonly CheckBox _quietEnabled = new() { Text = "Hold alerts during quiet hours", AutoSize = true };
    private readonly DateTimePicker _quietStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true, Width = 100 };
    private readonly DateTimePicker _quietEnd = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true, Width = 100 };
    private readonly CheckBox[] _quietDays = Enum.GetValues<DayOfWeek>()
        .Select(d => new CheckBox { Text = d.ToString()[..3], Tag = d, AutoSize = true }).ToArray();
    private readonly CheckBox _breakThrough = new() { Text = "Doorbell rings break through snooze and quiet hours", AutoSize = true };
    private readonly TextBox _replies = new() { Multiline = true, Height = 90, Width = 360, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly NumericUpDown _replyMinutes = new() { Minimum = 1, Maximum = 60, Width = 60 };

    private readonly CheckBox _hotkeysOn = new() { Text = "Enable global keyboard shortcuts", AutoSize = true };
    private readonly TextBox _hkToggle = new() { Width = 140 }, _hkDoorbell = new() { Width = 140 }, _hkSnooze = new() { Width = 140 },
        _hkHistory = new() { Width = 140 }, _hkSnapshot = new() { Width = 140 };
    private readonly Label _hkStatus = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(660, 0) };
    private readonly CheckBox _autoQuality = new() { Text = "Pick stream quality from window size (Low for small tiles, High when full size)", AutoSize = true };
    private readonly NumericUpDown _idleMinutes = new() { Minimum = 0, Maximum = 240, Width = 60 };
    private readonly ComboBox _idleMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _snapFolder = new() { Width = 420 };

    private readonly CheckBox _startup = new() { Text = "Run CamDock when I sign in to Windows", AutoSize = true };

    public SettingsForm(TrayApp app)
    {
        _app = app;
        Text = "CamDock — Settings";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 720);
        MinimumSize = new Size(760, 600);
        MaximizeBox = false;
        ShowInTaskbar = true;

        var tabs = Tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildNotificationsTab());
        tabs.TabPages.Add(BuildAlertsTab());
        tabs.TabPages.Add(BuildDesktopTab());
        tabs.TabPages.Add(BuildGeneralTab());

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        save.Click += async (_, _) => await SaveAsync(save);
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        LoadValues();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage BuildConnectionTab()
    {
        var page = new TabPage("Connection") { Padding = new Padding(12), AutoScroll = true };
        var adv = new GroupBox { Text = "Video streams", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var ag = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ag.Controls.Add(new Label { Text = "Default quality:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) }, 0, 0);
        _quality.Items.AddRange(new object[] { "High", "Medium", "Low" });
        ag.Controls.Add(_quality, 1, 0);
        ag.Controls.Add(new Label { Text = "Network buffer (ms):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) }, 0, 1);
        ag.Controls.Add(_buffer, 1, 1);
        ag.Controls.Add(_encrypted, 0, 2);
        ag.SetColumnSpan(_encrypted, 2);
        var hint = new Label
        {
            Text = "Each camera can override the quality from its right-click menu. If a camera's stream isn't turned on in Protect, " +
                   "the viewer turns it on through the API (this needs an API key with write access). Lower buffer = less delay; " +
                   "raise it if video stutters on Wi-Fi or VPN.",
            AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 0),
        };
        ag.Controls.Add(hint, 0, 3);
        ag.SetColumnSpan(hint, 2);
        adv.Controls.Add(ag);
        var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };

        page.Controls.Add(adv);
        page.Controls.Add(spacer);
        page.Controls.Add(_conn);
        page.Controls.Add(_current);
        return page;
    }

    private TabPage BuildNotificationsTab()
    {
        var page = new TabPage("Notifications") { Padding = new Padding(12) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        top.Controls.Add(_notifyEnabled);
        top.Controls.Add(_notifySnap);
        top.Controls.Add(_offline);
        var cd = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        cd.Controls.Add(new Label { Text = "Don't repeat the same alert from a camera within", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        cd.Controls.Add(_cooldown);
        cd.Controls.Add(new Label { Text = "seconds (doorbell rings always show)", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        top.Controls.Add(cd);
        top.Controls.Add(new Label
        {
            Text = "Choose which events show a notification for each camera, and whether that camera's live video pops up. " +
                   "Click a column header to toggle the whole column.",
            AutoSize = true, Margin = new Padding(3, 10, 3, 4),
        });

        var test = new Button { Text = "Send test notification", AutoSize = true };
        test.Click += (_, _) => _app.TestNotification();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.Add(test);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "camera", HeaderText = "Camera", ReadOnly = true, Width = 200, Frozen = true });
        foreach (var (key, label) in AlertTypes.All)
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = key, HeaderText = label, Width = 62, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "popup", HeaderText = "Pop up", Width = 62, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex <= 0) return;
            var allOn = _grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[e.ColumnIndex].Value is true);
            foreach (DataGridViewRow r in _grid.Rows) r.Cells[e.ColumnIndex].Value = !allOn;
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _notifyEnabled.CheckedChanged += (_, _) => { _grid.Enabled = _notifySnap.Enabled = _cooldown.Enabled = _notifyEnabled.Checked; };

        page.Controls.Add(_grid);
        page.Controls.Add(bottom);
        page.Controls.Add(top);
        return page;
    }

    private TabPage BuildAlertsTab()
    {
        var page = new TabPage("Pop-ups & quiet hours") { Padding = new Padding(12), AutoScroll = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var pop = new GroupBox { Text = "Pop-up windows", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var pf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        pf.Controls.Add(_popupDoorbell);
        var ps = new FlowLayoutPanel { AutoSize = true };
        ps.Controls.Add(new Label { Text = "Close a pop-up after", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        ps.Controls.Add(_popupSeconds);
        ps.Controls.Add(new Label { Text = "seconds unless I click, move or resize it", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        pf.Controls.Add(ps);
        pf.Controls.Add(new Label
        {
            Text = "Other cameras pop up when the \"Pop up\" box is ticked for them on the Notifications tab. Pop-ups have a red border; " +
                   "clicking one (or choosing \"Keep this window open\") turns it into a normal camera window.",
            AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText,
        });
        pop.Controls.Add(pf);
        flow.Controls.Add(pop);

        var quiet = new GroupBox { Text = "Quiet hours", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var qf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        qf.Controls.Add(_quietEnabled);
        var qt = new FlowLayoutPanel { AutoSize = true };
        qt.Controls.Add(new Label { Text = "From", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        qt.Controls.Add(_quietStart);
        qt.Controls.Add(new Label { Text = "to", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        qt.Controls.Add(_quietEnd);
        qf.Controls.Add(qt);
        var qd = new FlowLayoutPanel { AutoSize = true };
        foreach (var d in _quietDays) qd.Controls.Add(d);
        qf.Controls.Add(qd);
        qf.Controls.Add(_breakThrough);
        qf.Controls.Add(new Label
        {
            Text = "Snooze for 15/30/60 minutes or until morning from the tray icon › Alerts. Snooze and quiet hours hold back " +
                   "notifications, pop-ups and offline notices.",
            AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText,
        });
        quiet.Controls.Add(qf);
        flow.Controls.Add(quiet);

        var bell = new GroupBox { Text = "Doorbell quick replies", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var bf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        bf.Controls.Add(new Label { Text = "Messages (one per line, up to 30 characters). The first two also appear as buttons on doorbell notifications:", AutoSize = true, MaximumSize = new Size(660, 0) });
        bf.Controls.Add(_replies);
        var bm = new FlowLayoutPanel { AutoSize = true };
        bm.Controls.Add(new Label { Text = "Show messages on the doorbell for", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        bm.Controls.Add(_replyMinutes);
        bm.Controls.Add(new Label { Text = "minutes", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        bf.Controls.Add(bm);
        bf.Controls.Add(new Label
        {
            Text = "Send from a doorbell window's right-click menu, the tray menu, or the notification. Needs an API key with write access.",
            AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText,
        });
        bell.Controls.Add(bf);
        flow.Controls.Add(bell);

        _quietEnabled.CheckedChanged += (_, _) =>
        {
            _quietStart.Enabled = _quietEnd.Enabled = _quietEnabled.Checked;
            foreach (var d in _quietDays) d.Enabled = _quietEnabled.Checked;
        };
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildDesktopTab()
    {
        var page = new TabPage("Desktop") { Padding = new Padding(12), AutoScroll = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var hk = new GroupBox { Text = "Keyboard shortcuts (work from any app)", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var hg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        hg.Controls.Add(_hotkeysOn, 0, 0);
        hg.SetColumnSpan(_hotkeysOn, 2);
        var rows = new (string, TextBox)[]
        {
            ("Show / hide all cameras", _hkToggle), ("Pop up the doorbell", _hkDoorbell), ("Snooze alerts 30 min / resume", _hkSnooze),
            ("Open alert history", _hkHistory), ("Snapshot of camera under the mouse", _hkSnapshot),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            hg.Controls.Add(new Label { Text = rows[i].Item1 + ":", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) }, 0, i + 1);
            hg.Controls.Add(rows[i].Item2, 1, i + 1);
        }
        var hint = new Label
        {
            Text = "Write shortcuts like Ctrl+Alt+Shift+C or Ctrl+Shift+F9. Leave a box empty to turn that shortcut off.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(660, 0), Margin = new Padding(3, 6, 3, 0),
        };
        hg.Controls.Add(hint, 0, rows.Length + 1);
        hg.SetColumnSpan(hint, 2);
        hg.Controls.Add(_hkStatus, 0, rows.Length + 2);
        hg.SetColumnSpan(_hkStatus, 2);
        hk.Controls.Add(hg);
        flow.Controls.Add(hk);

        var perf = new GroupBox { Text = "Saving resources", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var pf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        pf.Controls.Add(_autoQuality);
        var idle = new FlowLayoutPanel { AutoSize = true };
        idle.Controls.Add(new Label { Text = "After", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        idle.Controls.Add(_idleMinutes);
        idle.Controls.Add(new Label { Text = "minutes with no camera activity,", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        _idleMode.Items.AddRange(new object[] { "switch to the low-quality stream", "pause and show a still picture" });
        idle.Controls.Add(_idleMode);
        pf.Controls.Add(idle);
        pf.Controls.Add(new Label
        {
            Text = "0 minutes = never. Any motion, detection or ring from the camera brings it back to live instantly (click a paused " +
                   "window to resume it yourself). Pop-ups, full-size windows and the camera you're listening to never go idle.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(660, 0),
        });
        perf.Controls.Add(pf);
        flow.Controls.Add(perf);

        var snap = new GroupBox { Text = "Snapshots", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(680, 0) };
        var sf = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        sf.Controls.Add(new Label { Text = "Save to:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        sf.Controls.Add(_snapFolder);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _snapFolder.Text, UseDescriptionForTitle = true, Description = "Folder for camera snapshots" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _snapFolder.Text = dlg.SelectedPath;
        };
        sf.Controls.Add(browse);
        snap.Controls.Add(sf);
        flow.Controls.Add(snap);

        _hotkeysOn.CheckedChanged += (_, _) =>
        {
            foreach (var (_, tb) in rows) tb.Enabled = _hotkeysOn.Checked;
        };
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General") { Padding = new Padding(12) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(_startup);

        var resetLayout = new Button { Text = "Reset camera window positions", AutoSize = true, Margin = new Padding(3, 16, 3, 3) };
        resetLayout.Click += (_, _) => _app.ResetWindowLayout();
        flow.Controls.Add(resetLayout);

        var logs = new Button { Text = "Open log folder", AutoSize = true };
        logs.Click += (_, _) => TrayApp.OpenFolder(AppPaths.LogFolder);
        flow.Controls.Add(logs);
        var data = new Button { Text = "Open data folder", AutoSize = true };
        data.Click += (_, _) => TrayApp.OpenFolder(AppPaths.Root);
        flow.Controls.Add(data);

        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        flow.Controls.Add(new Label
        {
            Text = $"CamDock {version}\nSettings: {AppPaths.ConfigFile}\nLogs: {AppPaths.LogFolder}",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 20, 3, 3),
        });
        page.Controls.Add(flow);
        return page;
    }

    // ------------------------------------------------------------------ load / save

    private void LoadValues()
    {
        var cfg = _app.Config;
        _current.Text = cfg.IsConfigured
            ? $"Currently connected to: {cfg.NvrUrl}" + (cfg.ProtectVersion != null ? $"   (UniFi Protect {cfg.ProtectVersion})" : "")
            : "Not connected.";
        _current.Font = new Font(Font, FontStyle.Bold);
        _conn.LoadFrom(cfg);
        _quality.SelectedIndex = Math.Max(0, Array.IndexOf(AppConfig.Qualities, cfg.DefaultQuality));
        _encrypted.Checked = cfg.EncryptedStreams;
        _buffer.Value = Math.Clamp(cfg.StreamBufferMs, 100, 5000);

        _notifyEnabled.Checked = cfg.NotificationsEnabled;
        _notifySnap.Checked = cfg.NotificationSnapshots;
        _cooldown.Value = Math.Clamp(cfg.NotificationCooldownSeconds, 0, 3600);
        _startup.Checked = cfg.RunAtStartup || StartupManager.IsEnabled();
        _offline.Checked = cfg.OfflineNotices;
        _popupDoorbell.Checked = cfg.PopupOnDoorbell;
        _popupSeconds.Value = Math.Clamp(cfg.PopupSeconds, 10, 600);
        _quietEnabled.Checked = cfg.QuietHoursEnabled;
        _quietStart.Value = DateTime.Today.Add(AlertGate.TryParseTime(cfg.QuietStart, out var qs) ? qs : TimeSpan.FromHours(22));
        _quietEnd.Value = DateTime.Today.Add(AlertGate.TryParseTime(cfg.QuietEnd, out var qe) ? qe : TimeSpan.FromHours(7));
        foreach (var d in _quietDays) d.Checked = cfg.QuietDays.Contains((DayOfWeek)d.Tag!);
        _quietStart.Enabled = _quietEnd.Enabled = cfg.QuietHoursEnabled;
        foreach (var d in _quietDays) d.Enabled = cfg.QuietHoursEnabled;
        _breakThrough.Checked = cfg.DoorbellBreaksThrough;
        _replies.Text = string.Join(Environment.NewLine, cfg.DoorbellReplies);
        _replyMinutes.Value = Math.Clamp(cfg.DoorbellMessageMinutes, 1, 60);
        _hotkeysOn.Checked = cfg.HotkeysEnabled;
        _hkToggle.Text = cfg.HotkeyToggleAll; _hkDoorbell.Text = cfg.HotkeyDoorbell; _hkSnooze.Text = cfg.HotkeySnooze;
        _hkHistory.Text = cfg.HotkeyHistory; _hkSnapshot.Text = cfg.HotkeySnapshot;
        foreach (var tb in new[] { _hkToggle, _hkDoorbell, _hkSnooze, _hkHistory, _hkSnapshot }) tb.Enabled = cfg.HotkeysEnabled;
        _hkStatus.Text = _app.HotkeyErrors.Count > 0 ? "⚠ " + string.Join("\n⚠ ", _app.HotkeyErrors) : "";
        _autoQuality.Checked = cfg.AutoQuality;
        _idleMinutes.Value = Math.Clamp(cfg.IdleMinutes, 0, 240);
        _idleMode.SelectedIndex = cfg.IdleMode == "pause" ? 1 : 0;
        _snapFolder.Text = cfg.SnapshotFolder;

        var cams = _app.Cameras.Count > 0
            ? _app.Cameras.Select(c => (Id: c.Id, Name: c.Name)).ToList()
            : cfg.Notifications.Select(kv => (Id: kv.Key, Name: kv.Value.Name)).OrderBy(x => x.Name).ToList();
        foreach (var (id, name) in cams)
        {
            var settings = cfg.Notifications.GetValueOrDefault(id);
            var enabled = settings?.Types ?? new List<string>(AlertTypes.Defaults);
            var values = new object[AlertTypes.All.Length + 2];
            values[^1] = settings?.Popup ?? false;
            values[0] = name;
            for (var i = 0; i < AlertTypes.All.Length; i++) values[i + 1] = enabled.Contains(AlertTypes.All[i].Key);
            var row = _grid.Rows.Add(values);
            _grid.Rows[row].Tag = id;
        }
        _grid.Enabled = _notifySnap.Enabled = _cooldown.Enabled = _notifyEnabled.Checked;
    }

    private async Task SaveAsync(Button save)
    {
        save.Enabled = false;
        try
        {
            var updated = _app.Config.Clone();
            ConnectionResult? validated = null;
            if (_conn.ConnectionChanged)
            {
                validated = await _conn.ValidateAsync();
                if (validated == null)
                {
                    Dialogs.Warn("Settings not saved", "The new NVR address or API key couldn't be verified. Fix the connection details (see the message under \"Test connection\") and try again.");
                    return;
                }
            }
            _conn.SaveTo(updated, validated);

            updated.DefaultQuality = AppConfig.Qualities[Math.Max(0, _quality.SelectedIndex)];
            updated.EncryptedStreams = _encrypted.Checked;
            updated.StreamBufferMs = (int)_buffer.Value;
            updated.NotificationsEnabled = _notifyEnabled.Checked;
            updated.NotificationSnapshots = _notifySnap.Checked;
            updated.NotificationCooldownSeconds = (int)_cooldown.Value;
            updated.RunAtStartup = _startup.Checked;
            updated.OfflineNotices = _offline.Checked;
            updated.PopupOnDoorbell = _popupDoorbell.Checked;
            updated.PopupSeconds = (int)_popupSeconds.Value;
            updated.QuietHoursEnabled = _quietEnabled.Checked;
            updated.QuietStart = _quietStart.Value.ToString("HH:mm");
            updated.QuietEnd = _quietEnd.Value.ToString("HH:mm");
            updated.QuietDays = _quietDays.Where(d => d.Checked).Select(d => (DayOfWeek)d.Tag!).ToList();
            updated.DoorbellBreaksThrough = _breakThrough.Checked;
            updated.DoorbellReplies = _replies.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => l.Length > 30 ? l[..30] : l).Distinct().ToList();
            updated.DoorbellMessageMinutes = (int)_replyMinutes.Value;

            foreach (var tb in new[] { _hkToggle, _hkDoorbell, _hkSnooze, _hkHistory, _hkSnapshot })
            {
                if (tb.Text.Trim().Length > 0 && !Hotkeys.TryParse(tb.Text, out _, out _))
                {
                    Dialogs.Warn("Settings not saved", $"\"{tb.Text}\" isn't a valid shortcut. Use a form like Ctrl+Alt+Shift+C, or leave it empty.");
                    return;
                }
            }
            updated.HotkeysEnabled = _hotkeysOn.Checked;
            updated.HotkeyToggleAll = _hkToggle.Text.Trim(); updated.HotkeyDoorbell = _hkDoorbell.Text.Trim();
            updated.HotkeySnooze = _hkSnooze.Text.Trim(); updated.HotkeyHistory = _hkHistory.Text.Trim();
            updated.HotkeySnapshot = _hkSnapshot.Text.Trim();
            updated.AutoQuality = _autoQuality.Checked;
            updated.IdleMinutes = (int)_idleMinutes.Value;
            updated.IdleMode = _idleMode.SelectedIndex == 1 ? "pause" : "low";
            updated.SnapshotFolder = _snapFolder.Text.Trim();

            _grid.EndEdit();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.Tag is not string id) continue;
                var s = updated.NotifySettings(id, r.Cells[0].Value as string ?? "");
                s.Types = AlertTypes.All.Where((a, i) => r.Cells[i + 1].Value is true).Select(a => a.Key).ToList();
                s.Popup = r.Cells["popup"].Value is true;
            }

            _app.ApplySettings(updated);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Dialogs.Error("Settings could not be saved", ex.Message, ex.ToString());
        }
        finally
        {
            if (!IsDisposed) save.Enabled = true;
        }
    }
}
