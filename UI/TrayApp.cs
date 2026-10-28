using System.Reflection;
using Microsoft.Win32;
using CamDock.Core;

namespace CamDock.UI;

/// <summary>The application: tray icon + camera windows. There is no main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private static Icon? _icon;
    public static Icon AppIcon => _icon ??= LoadIcon();

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<string, CameraWindow> _windows = new();
    private readonly System.Windows.Forms.Timer _saveTimer;
    private readonly SynchronizationContext _ui;
    private readonly Notifier _notifier;

    private ProtectApi? _api;
    private EventListener? _events;
    private EventListener? _devices;
    private readonly System.Windows.Forms.Timer _offlineTimer = new() { Interval = 30_000 };
    private readonly Dictionary<string, DateTime> _offlineSince = new();   // camera id -> first seen offline (UTC)
    private readonly HashSet<string> _offlineNotified = new();
    private DateTime? _nvrDownSince;
    private bool _nvrNotified;
    private bool _everConnected;
    private readonly Hotkeys _hotkeys = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };
    private HistoryForm? _historyForm;
    private bool _allHidden;

    public AlertHistory History { get; } = new();
    public List<string> HotkeyErrors { get; } = new();
    private List<CameraInfo> _cameras = new();
    private DateTime _camerasFetched = DateTime.MinValue;
    private string? _cameraError;
    private bool _refreshing;
    private SettingsForm? _settings;

    public AppConfig Config { get; private set; }
    public bool IsExiting { get; private set; }
    public IReadOnlyList<CameraInfo> Cameras => _cameras;
    public ProtectApi? Api => _api;
    public int OpenWindowCount => _windows.Count;

    public TrayApp(AppConfig config, bool showWelcome)
    {
        Config = config;
        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, e) => { e.Cancel = false; BuildMenu(); _ = RefreshCamerasAsync(force: false); };
        _menu.Closing += (_, e) =>
        {
            // Keep the menu open while ticking several cameras.
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _keepMenuOpen) e.Cancel = true;
            _keepMenuOpen = false;
        };
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "CamDock",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_windows.Count > 0) BringAllForward();
            else ShowTrayMenu();
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

        _saveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(Config); };

        _notifier = new Notifier(() => Config, () => _api, CameraName);
        _notifier.CameraActivated += id => _ui.Post(_ => OpenCamera(id, bringForward: true), null);
        _notifier.ReplyRequested += (id, text) => _ui.Post(async _ => await SendDoorbellMessageAsync(id, "CUSTOM_MESSAGE", text), null);
        _notifier.AlertRaised += OnAlertRaised;
        _notifier.RingSeen += id =>
        {
            if (Config.DoorbellIds.Contains(id)) return;
            Config.DoorbellIds.Add(id);
            SaveSoon();
        };
        _offlineTimer.Tick += (_, _) => CheckOffline();

        History.Load();
        _notifier.History = History;
        RegisterHotkeys();
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _ui.Post(async _ => await StartAsync(showWelcome), null);
    }

    private bool _keepMenuOpen;

    private static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    // ------------------------------------------------------------------ startup / connection

    private async Task StartAsync(bool showWelcome)
    {
        Connect();
        foreach (var (id, st) in Config.Windows.Where(w => w.Value.Open).ToList())
            OpenCamera(id, bringForward: false, nameHint: st.Name);

        await RefreshCamerasAsync(force: true);

        if (showWelcome)
            _tray.ShowBalloonTip(8000, "CamDock is running",
                "Click the tray icon to choose which cameras to show. Settings are in the same menu.", ToolTipIcon.Info);
    }

    private void Connect()
    {
        _events?.Dispose();
        _events = null;
        _devices?.Dispose();
        _devices = null;
        _offlineSince.Clear();
        _nvrDownSince = null;
        _api?.Dispose();
        _api = null;
        if (!Config.IsConfigured) return;

        var key = Config.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            _cameraError = "The saved API key couldn't be read. Enter it again in Settings.";
            UpdateTrayText();
            return;
        }
        _api = new ProtectApi(Config.NvrUrl, key);
        // Events always flow: they feed notifications, alert history and idle/activity tracking.
        _events = new EventListener(_api.EventsWebSocketUri, key, _ui);
        _events.EventReceived += ev =>
        {
            if (ev.DeviceId != null && _windows.TryGetValue(ev.DeviceId, out var w)) w.NotifyActivity();
            _notifier.Handle(ev);
        };
        _events.ConnectionChanged += _ => OnStreamConnectionChanged();
        _events.Start();
        {
            if (Config.NotificationsEnabled && Config.OfflineNotices)
            {
                _devices = new EventListener(_api.DevicesWebSocketUri, key, _ui);
                _devices.DeviceReceived += OnDeviceUpdate;
                _devices.ConnectionChanged += _ => OnStreamConnectionChanged();
                _devices.Start();
            }
        }
        UpdateTrayText();
    }

    public async Task RefreshCamerasAsync(bool force)
    {
        if (_api == null || _refreshing) return;
        if (!force && DateTime.UtcNow - _camerasFetched < TimeSpan.FromSeconds(30)) return;
        _refreshing = true;
        try
        {
            var cams = await _api.GetCamerasAsync();
            _cameras = cams;
            _camerasFetched = DateTime.UtcNow;
            _cameraError = null;
            foreach (var c in cams)
            {
                Config.NotifySettings(c.Id, c.Name);
                if (Config.Windows.TryGetValue(c.Id, out var st)) st.Name = c.Name;
                if (_windows.TryGetValue(c.Id, out var w) && w.CameraName != c.Name) w.Rename(c.Name);
            }
            SaveSoon();
        }
        catch (ProtectApiException ex)
        {
            _cameraError = ex.Message;
            Log.Warn($"Camera list refresh failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _cameraError = ex.Message;
            Log.Error("Camera list refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
            UpdateTrayText();
            if (_menu.Visible) BuildMenu();
        }
    }

    private void UpdateTrayText()
    {
        var host = UrlHelper.HostOf(Config.NvrUrl);
        var status = _cameraError != null ? "NVR unreachable"
            : _events is { IsConnected: false } ? "connecting…"
            : $"{_windows.Count} camera(s) shown";
        var hold = AlertGate.HoldReason(Config);
        var text = $"CamDock — {host}\n{status}" + (hold != null ? $"\n🔕 {hold}" : "");
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    public string CameraName(string id) =>
        _cameras.FirstOrDefault(c => c.Id == id)?.Name
        ?? Config.Windows.GetValueOrDefault(id)?.Name
        ?? Config.Notifications.GetValueOrDefault(id)?.Name
        ?? "Camera";

    // ------------------------------------------------------------------ tray menu

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"UniFi Protect — {UrlHelper.HostOf(Config.NvrUrl)}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        if (_cameraError != null)
        {
            _menu.Items.Add(new ToolStripMenuItem("⚠ " + Shorten(_cameraError, 70)) { Enabled = false });
            var retry = new ToolStripMenuItem("Retry connection");
            retry.Click += async (_, _) => { _keepMenuOpen = true; await RefreshCamerasAsync(force: true); };
            _menu.Items.Add(retry);
        }

        var ids = new HashSet<string>();
        foreach (var cam in _cameras)
        {
            ids.Add(cam.Id);
            AddCameraItem(cam.Id, cam.IsOnline ? cam.Name : $"{cam.Name}  (offline)");
        }
        // Windows restored from config for cameras not (yet) in the list
        foreach (var (id, w) in _windows.Where(w => !ids.Contains(w.Key)))
            AddCameraItem(id, w.CameraName);
        if (_cameras.Count == 0 && _windows.Count == 0 && _cameraError == null)
            _menu.Items.Add(new ToolStripMenuItem(_refreshing ? "Loading cameras…" : "No cameras found") { Enabled = false });

        _menu.Items.Add(new ToolStripSeparator());
        foreach (var bell in _cameras.Where(c => IsDoorbell(c.Id)))
            _menu.Items.Add(BuildDoorbellMenu(bell.Id, $"Doorbell message — {bell.Name}"));
        _menu.Items.Add(BuildAlertsMenu());
        _menu.Items.Add(new ToolStripMenuItem($"Alert history ({History.Items.Count})…", null, (_, _) => ShowHistory()) { ShortcutKeyDisplayString = Config.HotkeysEnabled ? Config.HotkeyHistory : null });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Bring all cameras forward", null, (_, _) => BringAllForward()) { Enabled = _windows.Count > 0 });
        _menu.Items.Add(new ToolStripMenuItem(_allHidden ? "Show all cameras" : "Hide all cameras", null, (_, _) => ToggleAllHidden())
            { Enabled = _windows.Count > 0, ShortcutKeyDisplayString = Config.HotkeysEnabled ? Config.HotkeyToggleAll : null });
        _menu.Items.Add(new ToolStripMenuItem("Reconnect all cameras", null, (_, _) => { foreach (var w in _windows.Values) w.Restart(); }) { Enabled = _windows.Count > 0 });
        _menu.Items.Add(new ToolStripMenuItem("Close all cameras", null, (_, _) => { foreach (var w in _windows.Values.ToList()) w.Close(); }) { Enabled = _windows.Count > 0 });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(AppPaths.LogFolder)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
        _menu.ResumeLayout();
    }

    private void AddCameraItem(string id, string label)
    {
        var item = new ToolStripMenuItem(label) { Checked = _windows.ContainsKey(id) };
        item.Click += (_, _) =>
        {
            _keepMenuOpen = true;
            if (_windows.TryGetValue(id, out var w)) w.Close();
            else OpenCamera(id, bringForward: true);
            item.Checked = _windows.ContainsKey(id);
        };
        _menu.Items.Add(item);
    }

    private void ShowTrayMenu()
    {
        // NotifyIcon only exposes the menu on right-click; invoke the same internal method for left-click.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
    }

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    // ------------------------------------------------------------------ windows

    public void OpenCamera(string id, bool bringForward, string? nameHint = null, bool popup = false)
    {
        if (_windows.TryGetValue(id, out var existing))
        {
            if (popup && existing.IsPopup) existing.ExtendPopup(Config.PopupSeconds);
            if (!popup && existing.IsPopup) existing.Pin(); // opened on purpose: keep it
            if (bringForward || popup) existing.BringForward();
            return;
        }
        var state = Config.WindowState(id);
        var name = _cameras.FirstOrDefault(c => c.Id == id)?.Name ?? nameHint ?? CameraName(id);
        state.Name = name;
        if (!popup) state.Open = true;
        var w = new CameraWindow(this, id, name, state, popup);
        _windows[id] = w;
        w.Show();
        if (bringForward) w.BringForward();
        SaveSoon();
        UpdateTrayText();
    }

    public void OnWindowClosed(CameraWindow w)
    {
        if (_windows.TryGetValue(w.CameraId, out var cur) && cur == w) _windows.Remove(w.CameraId);
        UpdateTrayText();
    }

    public IEnumerable<Rectangle> OtherWindowBounds(CameraWindow self) =>
        _windows.Values.Where(w => w != self && w.Visible).Select(w => w.Bounds);

    public void BringAllForward()
    {
        foreach (var w in _windows.Values) w.BringForward();
    }

    /// <summary>Only one camera plays audio at a time.</summary>
    public void OnCameraUnmuted(CameraWindow source)
    {
        foreach (var w in _windows.Values.Where(w => w != source)) w.SetMuted(true);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ui.Post(_ => { foreach (var w in _windows.Values) w.EnsureVisibleAfterDisplayChange(); }, null);
    }

    // ------------------------------------------------------------------ alert pop-ups

    private void OnAlertRaised(string cameraId, IReadOnlyList<string> categories)
    {
        var notify = Config.Notifications.GetValueOrDefault(cameraId);
        var wanted = (notify?.Popup ?? false) || (Config.PopupOnDoorbell && categories.Contains(AlertTypes.Doorbell));
        if (!wanted) return;
        Log.Info($"Pop-up for {CameraName(cameraId)} ({string.Join(",", categories)})");
        OpenCamera(cameraId, bringForward: true, popup: true);
    }

    // ------------------------------------------------------------------ doorbell quick replies

    public bool IsDoorbell(string cameraId) =>
        (_cameras.FirstOrDefault(c => c.Id == cameraId)?.IsDoorbell ?? false) || Config.DoorbellIds.Contains(cameraId);

    public ToolStripMenuItem BuildDoorbellMenu(string cameraId, string title = "Doorbell message")
    {
        var root = new ToolStripMenuItem(title);
        foreach (var r in Config.DoorbellReplies.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var text = r.Trim();
            root.DropDownItems.Add(text, null, async (_, _) => await SendDoorbellMessageAsync(cameraId, "CUSTOM_MESSAGE", text));
        }
        root.DropDownItems.Add("Leave package at door", null, async (_, _) => await SendDoorbellMessageAsync(cameraId, "LEAVE_PACKAGE_AT_DOOR", null));
        root.DropDownItems.Add("Do not disturb", null, async (_, _) => await SendDoorbellMessageAsync(cameraId, "DO_NOT_DISTURB", null));
        root.DropDownItems.Add("Custom message…", null, async (_, _) =>
        {
            var text = InputDialog.Ask("Doorbell message", "Message to show on the doorbell screen (up to 30 characters):", 30);
            if (!string.IsNullOrWhiteSpace(text)) await SendDoorbellMessageAsync(cameraId, "CUSTOM_MESSAGE", text.Trim());
        });
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add("Clear message", null, async (_, _) => await SendDoorbellMessageAsync(cameraId, null, null));
        return root;
    }

    public async Task SendDoorbellMessageAsync(string cameraId, string? type, string? text)
    {
        if (_api == null) return;
        var name = CameraName(cameraId);
        try
        {
            await _api.SetDoorbellMessageAsync(cameraId, type, text, TimeSpan.FromMinutes(Math.Clamp(Config.DoorbellMessageMinutes, 1, 60)));
            var shown = type == null ? "cleared" : $"showing \"{text ?? type.Replace('_', ' ').ToLowerInvariant()}\"";
            Log.Info($"Doorbell {name}: message {shown}");
            _tray.ShowBalloonTip(3000, name, type == null ? "Doorbell message cleared." : $"Doorbell is {shown}.", ToolTipIcon.Info);
        }
        catch (ProtectApiException ex)
        {
            Log.Warn($"Doorbell message for {name} failed: {ex.Message}");
            Dialogs.Error("Couldn't set the doorbell message", ex.Message);
        }
    }

    // ------------------------------------------------------------------ snooze / quiet hours

    private ToolStripMenuItem BuildAlertsMenu()
    {
        var hold = AlertGate.HoldReason(Config);
        var root = new ToolStripMenuItem(hold != null ? $"Alerts: {hold}" : "Alerts: on");
        root.DropDownItems.Add(new ToolStripMenuItem(hold ?? "Alerts are on") { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());
        foreach (var min in new[] { 15, 30, 60 })
            root.DropDownItems.Add($"Snooze {min} minutes", null, (_, _) => Snooze(DateTime.UtcNow.AddMinutes(min)));
        root.DropDownItems.Add("Snooze until tomorrow morning", null, (_, _) => Snooze(AlertGate.NextMorningUtc(Config)));
        root.DropDownItems.Add(new ToolStripMenuItem("Resume alerts now", null, (_, _) => Snooze(null)) { Enabled = AlertGate.IsSnoozed(Config, DateTime.UtcNow) });
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripMenuItem($"Quiet hours ({Config.QuietStart}–{Config.QuietEnd})", null, (_, _) =>
        {
            Config.QuietHoursEnabled = !Config.QuietHoursEnabled;
            SaveSoon();
            UpdateTrayText();
        }) { Checked = Config.QuietHoursEnabled });
        root.DropDownItems.Add(new ToolStripMenuItem("Doorbell rings break through", null, (_, _) =>
        {
            Config.DoorbellBreaksThrough = !Config.DoorbellBreaksThrough;
            SaveSoon();
        }) { Checked = Config.DoorbellBreaksThrough });
        return root;
    }

    private void Snooze(DateTime? untilUtc)
    {
        Config.SnoozeUntilUtc = untilUtc;
        SaveSoon();
        UpdateTrayText();
        Log.Info(untilUtc is { } u ? $"Alerts snoozed until {u.ToLocalTime():g}" : "Alerts resumed");
    }

    // ------------------------------------------------------------------ offline notices

    private void OnDeviceUpdate(DeviceUpdate u)
    {
        if (u.ModelKey != "camera" || u.State == null) return;
        var idx = _cameras.FindIndex(c => c.Id == u.Id);
        if (idx >= 0 && _cameras[idx].State != u.State) _cameras[idx] = _cameras[idx] with { State = u.State };
        var online = string.Equals(u.State, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        if (online)
        {
            _offlineSince.Remove(u.Id);
            if (_offlineNotified.Remove(u.Id))
                _notifier.ShowStatus(u.Id, $"{CameraName(u.Id)} is back online", $"Reconnected at {DateTime.Now:t}");
        }
        else if (!_offlineSince.ContainsKey(u.Id) && !_offlineNotified.Contains(u.Id))
        {
            _offlineSince[u.Id] = DateTime.UtcNow;
            _offlineTimer.Start();
        }
    }

    private void OnStreamConnectionChanged()
    {
        var anyListener = _events != null || _devices != null;
        var connected = (_events?.IsConnected ?? false) || (_devices?.IsConnected ?? false);
        if (connected)
        {
            _everConnected = true;
            _nvrDownSince = null;
            if (_nvrNotified)
            {
                _nvrNotified = false;
                _notifier.ShowStatus("", "NVR is back online", $"{UrlHelper.HostOf(Config.NvrUrl)} reconnected at {DateTime.Now:t}");
            }
        }
        else if (anyListener && _everConnected && _nvrDownSince == null)
        {
            _nvrDownSince = DateTime.UtcNow;
            _offlineTimer.Start();
        }
        UpdateTrayText();
    }

    /// <summary>Only report outages that last 2 minutes, so reboots and Wi-Fi blips don't nag.</summary>
    private void CheckOffline()
    {
        var limit = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;
        foreach (var (id, since) in _offlineSince.ToList())
        {
            if (now - since < limit) continue;
            _offlineSince.Remove(id);
            _offlineNotified.Add(id);
            _notifier.ShowStatus(id, $"{CameraName(id)} is offline", $"Offline since {since.ToLocalTime():t}");
        }
        if (_nvrDownSince is { } down && now - down >= limit && !_nvrNotified)
        {
            _nvrNotified = true;
            _notifier.ShowStatus("", "NVR unreachable", $"{UrlHelper.HostOf(Config.NvrUrl)} hasn't responded since {down.ToLocalTime():t}");
        }
        if (_offlineSince.Count == 0 && (_nvrDownSince == null || _nvrNotified)) _offlineTimer.Stop();
    }

    // ------------------------------------------------------------------ hotkeys

    public void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        HotkeyErrors.Clear();
        if (!Config.HotkeysEnabled) return;
        void Reg(string label, string key, Action a)
        {
            var err = _hotkeys.Register(key, a);
            if (err != null) { HotkeyErrors.Add($"{label}: {err}"); Log.Warn($"Hotkey {label}: {err}"); }
        }
        Reg("Show/hide all cameras", Config.HotkeyToggleAll, ToggleAllHidden);
        Reg("Doorbell", Config.HotkeyDoorbell, DoorbellHotkey);
        Reg("Snooze", Config.HotkeySnooze, SnoozeHotkey);
        Reg("Alert history", Config.HotkeyHistory, ShowHistory);
        Reg("Snapshot", Config.HotkeySnapshot, SnapshotHotkey);
    }

    public void ToggleAllHidden()
    {
        if (_windows.Count == 0) return;
        _allHidden = !_allHidden;
        foreach (var w in _windows.Values.ToList())
        {
            if (_allHidden && w.IsPopup) { w.Close(); continue; }
            w.SetHidden(_allHidden);
        }
        if (!_allHidden) BringAllForward();
        Log.Info(_allHidden ? "All cameras hidden" : "All cameras shown");
    }

    private void DoorbellHotkey()
    {
        var bell = _cameras.FirstOrDefault(c => IsDoorbell(c.Id))?.Id ?? Config.DoorbellIds.FirstOrDefault();
        if (bell == null) { _tray.ShowBalloonTip(3000, "CamDock", "No doorbell found yet.", ToolTipIcon.Info); return; }
        if (_windows.TryGetValue(bell, out var w)) { w.BringForward(); w.NotifyActivity(); }
        else OpenCamera(bell, bringForward: true, popup: true);
    }

    private void SnoozeHotkey()
    {
        if (AlertGate.IsSnoozed(Config, DateTime.UtcNow)) Snooze(null);
        else Snooze(DateTime.UtcNow.AddMinutes(30));
        _tray.ShowBalloonTip(2500, "CamDock", AlertGate.HoldReason(Config) ?? "Alerts resumed", ToolTipIcon.Info);
    }

    /// <summary>Snapshot of the camera under the mouse, else the active one, else the only one open.</summary>
    private void SnapshotHotkey()
    {
        var target = _windows.Values.FirstOrDefault(w => w.Visible && w.Bounds.Contains(Cursor.Position))
                     ?? _windows.Values.FirstOrDefault(w => w.ContainsFocus)
                     ?? (_windows.Count == 1 ? _windows.Values.First() : null);
        if (target == null) { _tray.ShowBalloonTip(3000, "CamDock", "Point at a camera window, then press the snapshot shortcut.", ToolTipIcon.Info); return; }
        target.SaveSnapshot(false);
    }

    // ------------------------------------------------------------------ snapshots / history

    public async Task SaveSnapshotAsync(string cameraId, bool copyToClipboard)
    {
        var name = CameraName(cameraId);
        var bytes = _api != null ? await _api.GetSnapshotAsync(cameraId, default, highQuality: true) : null;
        if (bytes is not { Length: > 0 })
        {
            _tray.ShowBalloonTip(3000, name, "Couldn't get a snapshot from the NVR.", ToolTipIcon.Warning);
            return;
        }
        try
        {
            if (copyToClipboard)
            {
                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                Clipboard.SetImage(img);
                _tray.ShowBalloonTip(2500, name, "Snapshot copied to the clipboard.", ToolTipIcon.Info);
                return;
            }
            var folder = string.IsNullOrWhiteSpace(Config.SnapshotFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CamDock")
                : Config.SnapshotFolder;
            Directory.CreateDirectory(folder);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(folder, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            Log.Info($"Snapshot saved: {path}");
            _tray.ShowBalloonTip(2500, name, $"Snapshot saved to {folder}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving snapshot failed: {ex.Message}");
            Dialogs.Error("Couldn't save the snapshot", ex.Message);
        }
    }

    public void ShowHistory()
    {
        if (_historyForm is { IsDisposed: false }) { _historyForm.Activate(); return; }
        _historyForm = new HistoryForm(this);
        _historyForm.FormClosed += (_, _) => _historyForm = null;
        _historyForm.Show();
        _historyForm.Activate();
    }

    // ------------------------------------------------------------------ idle

    private void CheckIdle()
    {
        if (Config.IdleMinutes <= 0) return;
        var limit = TimeSpan.FromMinutes(Config.IdleMinutes);
        var now = DateTime.UtcNow;
        foreach (var w in _windows.Values.ToList())
            if (!w.IsIdle && w.CanIdle && now - w.LastActivityUtc >= limit) w.EnterIdle(Config.IdleMode);
    }

    // ------------------------------------------------------------------ settings

    public void ShowSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(this);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void ApplySettings(AppConfig updated)
    {
        var old = Config;
        // Window state is owned live by the windows; carry it across.
        updated.Windows = old.Windows;
        Config = updated;

        var connectionChanged = !string.Equals(old.NvrUrl, updated.NvrUrl, StringComparison.OrdinalIgnoreCase)
                                || old.ApiKeyProtected != updated.ApiKeyProtected;
        var nvrChanged = !string.Equals(old.NvrUrl, updated.NvrUrl, StringComparison.OrdinalIgnoreCase);

        try
        {
            if (old.RunAtStartup != updated.RunAtStartup || StartupManager.IsEnabled() != updated.RunAtStartup)
                StartupManager.Apply(updated.RunAtStartup);
        }
        catch (Exception ex)
        {
            Dialogs.Error("Couldn't change the run-at-startup setting", ex.Message, ex.ToString());
        }

        ConfigStore.Save(Config);
        Log.Info($"Settings saved (connectionChanged={connectionChanged}, notifications={updated.NotificationsEnabled}).");

        RegisterHotkeys();
        if (connectionChanged || old.NotificationsEnabled != updated.NotificationsEnabled || old.OfflineNotices != updated.OfflineNotices) Connect();
        if (old.IdleMinutes != updated.IdleMinutes || old.IdleMode != updated.IdleMode)
            foreach (var w in _windows.Values) w.NotifyActivity(); // re-evaluate with the new rule
        if (nvrChanged)
        {
            // Camera ids are per-NVR: close everything from the old console and ask for a web sign-in.
            foreach (var w in _windows.Values.ToList()) w.Close();
            _cameras.Clear();
            _ = RefreshCamerasAsync(force: true);
        }
        else
        {
            if (connectionChanged) _ = RefreshCamerasAsync(force: true);
            if (connectionChanged || old.DefaultQuality != updated.DefaultQuality || old.EncryptedStreams != updated.EncryptedStreams
                || old.StreamBufferMs != updated.StreamBufferMs || old.AutoQuality != updated.AutoQuality)
                foreach (var w in _windows.Values) w.Restart();
        }
        UpdateTrayText();
    }

    public void ResetWindowLayout()
    {
        foreach (var st in Config.Windows.Values) st.HasBounds = false;
        IsExiting = true;
        var open = _windows.Keys.ToList();
        foreach (var w in _windows.Values.ToList()) w.Close();
        IsExiting = false;
        foreach (var id in open) OpenCamera(id, bringForward: false);
        SaveSoon();
    }

    public void TestNotification() => _notifier.ShowTest();

    /// <summary>The exe was launched again while running: surface what we have instead of starting twice.</summary>
    public void OnSecondInstance()
    {
        if (_windows.Count > 0) BringAllForward();
        else ShowTrayMenu();
        _tray.ShowBalloonTip(4000, "CamDock", "Already running — use the tray icon to choose cameras.", ToolTipIcon.Info);
    }

    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Could not open {path}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ exit

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        IsExiting = true;
        ConfigStore.Save(Config);
    }

    public void ExitApp()
    {
        if (IsExiting && _tray.Visible == false) return;
        IsExiting = true;
        Log.Info("Exiting.");
        _saveTimer.Stop();
        ConfigStore.Save(Config);
        _events?.Dispose();
        _devices?.Dispose();
        _offlineTimer.Stop();
        _idleTimer.Stop();
        _hotkeys.Dispose();
        _historyForm?.Close();
        foreach (var w in _windows.Values.ToList()) w.Close();
        _settings?.Close();
        VlcHost.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _api?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _saveTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
