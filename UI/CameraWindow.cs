using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using CamDock.Core;

namespace CamDock.UI;

/// <summary>Borderless, resizable, snapping window playing one camera's RTSP(S) stream with LibVLC.</summary>
internal sealed class CameraWindow : Form
{
    private readonly TrayApp _app;
    private readonly CameraWindowState _state;
    private readonly VideoView _video;
    private readonly Panel _overlay;
    private readonly Label _overlayText;
    private readonly ProgressBar _spinner;
    private readonly FlowLayoutPanel _overlayButtons;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _retryTimer;
    private readonly System.Windows.Forms.Timer _watchdog;

    private MediaPlayer? _player;
    private Media? _media;
    private CancellationTokenSource? _startCts;
    private string? _quality;            // quality actually playing
    private string? _requestedQuality;   // quality we asked for
    private string? _qualityNote;        // why the playing quality differs from the requested one
    private readonly System.Windows.Forms.Timer _qualityTimer = new() { Interval = 1200 };

    // idle / hidden
    private bool _idleLow, _idlePaused, _hidden;
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public bool IsIdle => _idleLow || _idlePaused;
    public bool IsHiddenByUser => _hidden;
    /// <summary>Pop-ups, full-size windows and the camera you're listening to never go idle.</summary>
    public bool CanIdle => !_popup && !_fullSize && _state.Muted && !_hidden && Visible;
    private bool _playing;
    private long _lastFrames = -1;
    private int _frozenTicks;
    private int _retrySeconds = 5;

    private bool _fullSize;
    private Rectangle _preFullBounds;

    // digital zoom (crop) state, in video pixels
    private uint _vw, _vh;
    private double _zoom = 1, _cropX, _cropY;
    private Point? _panStart;
    private double _panCropX, _panCropY;
    private DateTime _lastCropApply;

    // move tracking (snap without sticking)
    private Point _moveStartCursor;
    private Point _moveStartLocation;
    private bool _moving;

    // pop-up mode: opened by an alert, closes itself unless the user interacts with it
    private bool _popup;
    private readonly System.Windows.Forms.Timer _popupTimer = new();

    // our own double-click detection (the drag loop swallows the system one)
    private DateTime _lastDown;
    private Point _lastDownPos;

    public string CameraId { get; }
    public string CameraName { get; private set; }
    public bool IsPopup => _popup;
    public bool IsPlaying => _playing;
    public ContextMenuStrip Menu => _menu;

    private int BorderPx => _fullSize ? 0 : LogicalToDeviceUnits(5);

    public CameraWindow(TrayApp app, string cameraId, string cameraName, CameraWindowState state, bool popup = false)
    {
        _popup = popup;
        _app = app;
        CameraId = cameraId;
        CameraName = cameraName;
        _state = state;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = BorderColor(false);
        Text = cameraName;
        Icon = TrayApp.AppIcon;
        TopMost = state.TopMost || popup;
        MinimumSize = new Size(160, 100);
        DoubleBuffered = true;
        Padding = new Padding(BorderPx);
        KeyPreview = true;

        _video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
        WireMouse(_video);
        Controls.Add(_video);

        // ---- overlay (loading / error) ----
        _overlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 16), BackgroundImageLayout = ImageLayout.Zoom };
        _overlayText = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.5f), Padding = new Padding(12, 0, 12, 0),
        };
        _spinner = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Bottom, Height = 4 };
        _overlayButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(8, 4, 8, 4), Visible = false,
        };
        _overlay.Controls.Add(_overlayText);
        _overlay.Controls.Add(_overlayButtons);
        _overlay.Controls.Add(_spinner);
        WireMouse(_overlay);
        WireMouse(_overlayText);
        Controls.Add(_overlay);
        _overlay.BringToFront();

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, _) => { NotifyActivity(); PopulateMenu(_menu); };

        _retryTimer = new System.Windows.Forms.Timer();
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); Restart(); };
        // Frozen-stream watchdog: only runs while a stream is playing.
        _watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _watchdog.Tick += (_, _) => CheckFrozen();

        _popupTimer.Tick += (_, _) => { _popupTimer.Stop(); if (_popup && !IsDisposed) Close(); };
        _qualityTimer.Tick += (_, _) => { _qualityTimer.Stop(); if (WantedQuality() != _requestedQuality && !_idlePaused && !_hidden) Restart(); };

        RestoreSavedBounds();
        ShowLoading($"Connecting to {cameraName}…");
    }

    private Color BorderColor(bool active) =>
        _popup ? Color.FromArgb(active ? 230 : 200, 60, 60) : active ? Color.FromArgb(70, 70, 70) : Color.FromArgb(28, 28, 28);

    /// <summary>Restart the auto-close countdown (another alert from the same camera).</summary>
    public void ExtendPopup(int seconds)
    {
        if (!_popup) return;
        _popupTimer.Stop();
        _popupTimer.Interval = Math.Clamp(seconds, 5, 3600) * 1000;
        _popupTimer.Start();
    }

    /// <summary>Turn an alert pop-up into a normal, persistent camera window.</summary>
    public void Pin()
    {
        if (!_popup) return;
        _popup = false;
        _popupTimer.Stop();
        _state.Open = true;
        TopMost = _state.TopMost;
        BackColor = BorderColor(ContainsFocus);
        _app.SaveSoon();
        Log.Info($"[{CameraName}] pop-up kept open");
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.TryRoundCorners(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_popup) ExtendPopup(_app.Config.PopupSeconds);
        Restart();
    }

    // ------------------------------------------------------------------ playback

    private void EnsurePlayer()
    {
        if (_player != null) return;
        _player = new MediaPlayer(VlcHost.Instance)
        {
            EnableHardwareDecoding = true,
            EnableMouseInput = false, // let mouse/keyboard reach our window
            EnableKeyInput = false,
        };
        _player.Playing += (_, _) => Ui(OnPlaying);
        _player.EncounteredError += (_, _) => Ui(() => OnStreamFailed("The video stream couldn't be played."));
        _player.EndReached += (_, _) => Ui(() => OnStreamFailed("The video stream ended (the NVR or camera may have restarted)."));
        _player.Vout += (_, _) => Ui(UpdateVideoSize);
        _video.MediaPlayer = _player;
    }

    private void Ui(Action a)
    {
        // VLC raises events on its own threads; never call back into VLC from them.
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
        catch (InvalidOperationException) { /* closing */ }
    }

    public async void Restart()
    {
        _retryTimer.Stop();
        _watchdog.Stop();
        _startCts?.Cancel();
        var cts = _startCts = new CancellationTokenSource();
        _playing = false;
        _quality = null;
        ShowLoading($"Connecting to {CameraName}…");

        var api = _app.Api;
        if (api == null)
        {
            ShowError("Not connected to the NVR. Check the address and API key in Settings.", autoRetry: true);
            return;
        }
        try
        {
            EnsurePlayer();
            _requestedQuality = WantedQuality();
            var stream = await api.GetStreamAsync(CameraId, _requestedQuality, _app.Config.EncryptedStreams, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            _quality = stream.Quality;
            _qualityNote = stream.Quality == _requestedQuality ? null
                : $"{Cap(_requestedQuality)} isn't available for this camera, so {Cap(stream.Quality)} is playing. " +
                  $"Turn on its {Cap(_requestedQuality)} RTSPS stream in Protect (camera › Settings › Advanced), " +
                  "or use an API key with write access so CamDock can turn it on.";
            if (_qualityNote != null) Log.Warn($"[{CameraName}] {_qualityNote}");
            Log.Info($"[{CameraName}] playing {stream.Quality} stream {Mask(stream.PlayUrl)}");

            var options = new List<string>
            {
                $":network-caching={Math.Clamp(_app.Config.StreamBufferMs, 100, 5000)}",
                ":rtsp-tcp",                     // interleaved TCP: reliable through firewalls, no UDP loss
                ":rtsp-frame-buffer-size=1000000",
            };
            if (_state.Muted) options.Add(":no-audio"); // don't decode audio we aren't playing

            var old = _media;
            _media = new Media(VlcHost.Instance, new Uri(stream.PlayUrl), options.ToArray());
            _player!.Play(_media);
            old?.Dispose();
        }
        catch (ProtectApiException ex)
        {
            if (cts.IsCancellationRequested) return;
            Log.Warn($"[{CameraName}] stream lookup failed: {ex.Message}");
            var permanent = ex.Kind is ApiErrorKind.Unauthorized or ApiErrorKind.NotFound;
            ShowError(ex.Message, autoRetry: !permanent);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Log.Error($"[{CameraName}] could not start playback", ex);
            ShowError("The video player couldn't start:\n" + ex.Message, autoRetry: true);
        }
    }

    private int LogicalWidth => (int)Math.Round(Width * 96.0 / Math.Max(96, DeviceDpi));

    private static string Cap(string? q) => string.IsNullOrEmpty(q) ? "" : char.ToUpper(q[0]) + q[1..];

    private string WantedQuality() => _idleLow ? "low" : _app.Config.QualityFor(CameraId, LogicalWidth, _fullSize);

    /// <summary>After a resize: switch stream if auto quality now wants a different one (debounced).</summary>
    private void QueueQualityCheck()
    {
        if (!_app.Config.IsAutoQuality(CameraId) || _requestedQuality == null) return;
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    // ------------------------------------------------------------------ activity / idle / hide

    public void NotifyActivity()
    {
        LastActivityUtc = DateTime.UtcNow;
        if (IsIdle) ExitIdle();
    }

    public void EnterIdle(string mode)
    {
        if (IsIdle || !CanIdle) return;
        Log.Info($"[{CameraName}] idle → {mode}");
        if (mode == "pause")
        {
            _idlePaused = true;
            PausePlayback();
            ShowPausedAsync();
        }
        else
        {
            _idleLow = true;
            if (_requestedQuality != "low") Restart();
        }
    }

    private void ExitIdle()
    {
        var wasPaused = _idlePaused;
        var wasLow = _idleLow;
        _idlePaused = _idleLow = false;
        LastActivityUtc = DateTime.UtcNow;
        Log.Info($"[{CameraName}] active again");
        if (wasPaused || (wasLow && WantedQuality() != _requestedQuality)) Restart();
    }

    private async void ShowPausedAsync()
    {
        SetOverlay($"{CameraName} — paused (no activity since {LastActivityUtc.ToLocalTime():t}).\nClick to watch live.", spinner: false,
            buttons: Array.Empty<(string, Action)>(), keepImage: true);
        var bytes = _app.Api is { } api ? await api.GetSnapshotAsync(CameraId) : null;
        if (!_idlePaused || IsDisposed || bytes == null) return;
        try
        {
            using var ms = new MemoryStream(bytes);
            var img = Image.FromStream(ms);
            var old = _overlay.BackgroundImage;
            _overlay.BackgroundImage = new Bitmap(img);
            old?.Dispose();
            img.Dispose();
        }
        catch { /* keep the plain overlay */ }
    }

    /// <summary>Hide (stop streaming) or show again (hotkey "show/hide all").</summary>
    public void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        if (hidden)
        {
            PausePlayback();
            Hide();
        }
        else
        {
            Show();
            if (!_idlePaused) Restart();
        }
    }

    private void PausePlayback()
    {
        _startCts?.Cancel();
        _watchdog.Stop();
        _retryTimer.Stop();
        _playing = false;
        try { _player?.Stop(); } catch (Exception ex) { Log.Warn($"[{CameraName}] stop: {ex.Message}"); }
    }

    public async void SaveSnapshot(bool copyToClipboard) => await _app.SaveSnapshotAsync(CameraId, copyToClipboard);

    private static string Mask(string url)
    {
        // The stream alias grants access to the video, so keep it out of the log.
        var u = new Uri(url);
        return $"{u.Scheme}://{u.Authority}/***";
    }

    private void OnPlaying()
    {
        _playing = true;
        _retrySeconds = 5;
        _lastFrames = -1;
        _frozenTicks = 0;
        HideOverlay();
        UpdateVideoSize();
        ApplyCrop(force: true);
        if (_player != null) _player.Mute = _state.Muted;
        _watchdog.Start();
    }

    private void OnStreamFailed(string message)
    {
        if (IsDisposed) return;
        _playing = false;
        _watchdog.Stop();
        Log.Warn($"[{CameraName}] {message}");
        var host = UrlHelper.HostOf(_app.Config.NvrUrl);
        var hint = _app.Config.EncryptedStreams
            ? $"\nCheck that {host} is reachable on port 7441, or turn off encrypted streams in Settings."
            : $"\nCheck that {host} is reachable on port 7447 (Protect's RTSP port).";
        ShowError(message + hint, autoRetry: true);
    }

    private void CheckFrozen()
    {
        if (!_playing || _player == null) return;
        long frames;
        try { frames = _media?.Statistics.DecodedVideo ?? -1; }
        catch { return; }
        if (frames >= 0 && frames == _lastFrames)
        {
            if (++_frozenTicks >= 3) // ~15 s without a new frame
            {
                Log.Warn($"[{CameraName}] video frozen; reconnecting");
                Restart();
            }
        }
        else _frozenTicks = 0;
        _lastFrames = frames;
    }

    private void UpdateVideoSize()
    {
        if (_player == null) return;
        uint w = 0, h = 0;
        try { if (!_player.Size(0, ref w, ref h)) return; } catch { return; }
        if (w == 0 || h == 0) return;
        _vw = w; _vh = h;
        SetAspect((double)w / h);
    }

    private void StopPlayback()
    {
        _startCts?.Cancel();
        _watchdog.Stop();
        _retryTimer.Stop();
        var p = _player;
        _player = null;
        if (p != null)
        {
            try
            {
                _video.MediaPlayer = null;
                p.Stop();
                p.Dispose();
            }
            catch (Exception ex) { Log.Warn($"[{CameraName}] stopping player: {ex.Message}"); }
        }
        _media?.Dispose();
        _media = null;
    }

    // ------------------------------------------------------------------ digital zoom (crop)

    private void ZoomAt(Point p, double factor)
    {
        if (_vw == 0 || _vh == 0 || !_playing) return;
        var ns = Math.Clamp(_zoom * factor, 1, 8);
        if (Math.Abs(ns - _zoom) < 0.001) return;
        double px = Math.Clamp((double)p.X / Math.Max(1, _video.Width), 0, 1);
        double py = Math.Clamp((double)p.Y / Math.Max(1, _video.Height), 0, 1);
        double curW = _vw / _zoom, curH = _vh / _zoom;
        double pointX = _cropX + px * curW, pointY = _cropY + py * curH;
        _zoom = ns < 1.02 ? 1 : ns;
        double newW = _vw / _zoom, newH = _vh / _zoom;
        _cropX = pointX - px * newW;
        _cropY = pointY - py * newH;
        ApplyCrop(force: true);
    }

    private void ResetZoom()
    {
        _zoom = 1; _cropX = _cropY = 0;
        ApplyCrop(force: true);
    }

    private void ApplyCrop(bool force)
    {
        if (_player == null) return;
        if (!force && DateTime.UtcNow - _lastCropApply < TimeSpan.FromMilliseconds(60)) return;
        _lastCropApply = DateTime.UtcNow;
        if (_zoom <= 1 || _vw == 0)
        {
            _zoom = 1; _cropX = _cropY = 0;
            try { _player.CropGeometry = null; } catch { /* ignore */ }
            return;
        }
        double w = _vw / _zoom, h = _vh / _zoom;
        _cropX = Math.Clamp(_cropX, 0, _vw - w);
        _cropY = Math.Clamp(_cropY, 0, _vh - h);
        try { _player.CropGeometry = $"{(int)w}x{(int)h}+{(int)_cropX}+{(int)_cropY}"; }
        catch (Exception ex) { Log.Warn($"[{CameraName}] crop failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ mouse

    private void WireMouse(Control c)
    {
        c.MouseDown += OnVideoMouseDown;
        c.MouseMove += OnVideoMouseMove;
        c.MouseUp += OnVideoMouseUp;
        c.MouseWheel += (_, e) => NotifyActivity();
        c.MouseWheel += (_, e) => ZoomAt(PointToVideo(c, e.Location), e.Delta > 0 ? 1.25 : 0.8);
    }

    private Point PointToVideo(Control source, Point p) => source == _video ? p : _video.PointToClient(source.PointToScreen(p));

    private void OnVideoMouseDown(object? sender, MouseEventArgs e)
    {
        if (_popup && e.Button == MouseButtons.Left) Pin(); // interacting keeps it
        if (_idlePaused && e.Button == MouseButtons.Left) { NotifyActivity(); return; }
        NotifyActivity(); // using the window counts as activity: an idle window returns to its chosen quality
        if (e.Button == MouseButtons.Middle) { ResetZoom(); return; }
        if (e.Button != MouseButtons.Left) return;

        var now = DateTime.UtcNow;
        var screen = Cursor.Position;
        var isDouble = (now - _lastDown).TotalMilliseconds <= SystemInformation.DoubleClickTime
                       && Math.Abs(screen.X - _lastDownPos.X) <= SystemInformation.DoubleClickSize.Width
                       && Math.Abs(screen.Y - _lastDownPos.Y) <= SystemInformation.DoubleClickSize.Height;
        _lastDown = isDouble ? DateTime.MinValue : now;
        _lastDownPos = screen;
        if (isDouble) { ToggleFullSize(); return; }

        if (_zoom > 1 && (ModifierKeys & Keys.Control) == 0)
        {
            _panStart = screen;
            _panCropX = _cropX; _panCropY = _cropY;
            if (sender is Control sc) sc.Capture = true;
            return;
        }
        BeginWindowDrag();
    }

    private void OnVideoMouseMove(object? sender, MouseEventArgs e)
    {
        if (_panStart is not { } start || _vw == 0) return;
        var cur = Cursor.Position;
        double scaleX = (_vw / _zoom) / Math.Max(1, _video.Width);
        double scaleY = (_vh / _zoom) / Math.Max(1, _video.Height);
        _cropX = _panCropX - (cur.X - start.X) * scaleX;
        _cropY = _panCropY - (cur.Y - start.Y) * scaleY;
        ApplyCrop(force: false);
    }

    private void OnVideoMouseUp(object? sender, MouseEventArgs e)
    {
        if (_panStart != null) { _panStart = null; ApplyCrop(force: true); return; }
        if (e.Button == MouseButtons.Right) ShowMenu();
    }

    // ------------------------------------------------------------------ overlay

    private void ShowLoading(string text) => SetOverlay(text, spinner: true, buttons: Array.Empty<(string, Action)>());

    private void ShowError(string text, bool autoRetry)
    {
        var buttons = new List<(string, Action)> { ("Retry", Restart), ("Settings…", () => _app.ShowSettings()) };
        if (autoRetry)
        {
            text += $"\n\nRetrying in {_retrySeconds} s…";
            _retryTimer.Interval = _retrySeconds * 1000;
            _retryTimer.Start();
            _retrySeconds = Math.Min(60, _retrySeconds * 2);
        }
        SetOverlay(text, spinner: false, buttons: buttons.ToArray());
    }

    private void SetOverlay(string text, bool spinner, (string Text, Action Click)[] buttons, bool keepImage = false)
    {
        if (IsDisposed) return;
        if (!keepImage && _overlay.BackgroundImage != null)
        {
            var old = _overlay.BackgroundImage;
            _overlay.BackgroundImage = null;
            old.Dispose();
        }
        _overlayText.Text = text;
        _spinner.Visible = spinner;
        _overlayButtons.SuspendLayout();
        foreach (Control c in _overlayButtons.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (var (label, click) in buttons)
        {
            var b = new Button
            {
                Text = label, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = Color.FromArgb(48, 48, 48), Margin = new Padding(0, 0, 6, 0),
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            b.Click += (_, _) => click();
            _overlayButtons.Controls.Add(b);
        }
        _overlayButtons.Visible = buttons.Length > 0;
        _overlayButtons.ResumeLayout();
        _overlay.Visible = true;
        _overlay.BringToFront();
    }

    private void HideOverlay()
    {
        _retryTimer.Stop();
        _overlay.Visible = false;
    }

    // ------------------------------------------------------------------ menu

    private void PopulateMenu(ContextMenuStrip m)
    {
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem(CameraName) { Enabled = false, Font = new Font(m.Font, FontStyle.Bold) });
        if (_popup)
            m.Items.Add(new ToolStripMenuItem("Keep this window open", null, (_, _) => Pin()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripSeparator());
        if (_app.IsDoorbell(CameraId))
        {
            m.Items.Add(_app.BuildDoorbellMenu(CameraId));
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add(Check("Listen (audio)", !_state.Muted, () => SetMuted(!_state.Muted)));
        m.Items.Add(Check("Always on top", _state.TopMost, () => { _state.TopMost = !_state.TopMost; TopMost = _state.TopMost; _app.SaveSoon(); }));
        m.Items.Add(Check("Lock position", _state.Locked, () => { _state.Locked = !_state.Locked; _app.SaveSoon(); }));
        m.Items.Add(Check("Play sound on motion", _state.MotionSound, () =>
        {
            _state.MotionSound = !_state.MotionSound;
            _app.SaveSoon();
            Log.Info($"[{CameraName}] motion sound {(_state.MotionSound ? "on" : "off")}");
            if (_state.MotionSound) _app.MotionSound.Play(_app.Config.MotionSoundFile); // preview
        }));

        var chosen = _app.Config.QualityFor(CameraId, LogicalWidth, _fullSize);
        string qLabel, qNote;
        if (_idleLow)
        {
            qLabel = $"Quality: {Cap(chosen)} (Low while idle)";
            qNote = $"No activity for {_app.Config.IdleMinutes} min, so the Low stream is playing. It returns to {Cap(chosen)} on the next event, or when you click the window.";
        }
        else if (_quality == null)
        {
            qLabel = $"Quality: {Cap(chosen)} (connecting…)";
            qNote = "";
        }
        else if (_qualityNote != null)
        {
            qLabel = $"Quality: {Cap(_requestedQuality)} ({Cap(_quality)} in use)";
            qNote = _qualityNote;
        }
        else
        {
            qLabel = $"Quality: {Cap(_quality)}";
            qNote = "";
        }
        var quality = new ToolStripMenuItem(qLabel) { ToolTipText = qNote.Length > 0 ? qNote : null };
        if (qNote.Length > 0)
        {
            quality.DropDownItems.Add(new ToolStripMenuItem(qNote.Length > 90 ? qNote[..qNote.IndexOf(". ", StringComparison.Ordinal)] + "." : qNote)
                { Enabled = false, ToolTipText = qNote });
            quality.DropDownItems.Add(new ToolStripSeparator());
        }
        var fixedQ = _state.Quality;
        if (_app.Config.AutoQuality)
            quality.DropDownItems.Add(new ToolStripMenuItem("Auto (by window size)", null, (_, _) => SetQuality(null)) { Checked = fixedQ == null });
        foreach (var q in AppConfig.Qualities)
        {
            var isCurrent = fixedQ == q || (fixedQ == null && !_app.Config.AutoQuality && _app.Config.QualityFor(CameraId) == q);
            quality.DropDownItems.Add(new ToolStripMenuItem(char.ToUpper(q[0]) + q[1..], null, (_, _) => SetQuality(q)) { Checked = isCurrent });
        }
        m.Items.Add(quality);
        m.Items.Add(new ToolStripMenuItem("Save snapshot", null, (_, _) => SaveSnapshot(false)));
        m.Items.Add(new ToolStripMenuItem("Copy snapshot", null, (_, _) => SaveSnapshot(true)));

        var size = new ToolStripMenuItem("Size");
        foreach (var w in new[] { 320, 480, 640, 960 })
            size.DropDownItems.Add(new ToolStripMenuItem($"{w} wide", null, (_, _) => ApplySizePreset(w)) { Enabled = !_state.Locked });
        m.Items.Add(size);

        var snap = new ToolStripMenuItem("Snap to corner") { Enabled = !_state.Locked && !_fullSize };
        snap.DropDownItems.Add("Top left", null, (_, _) => SnapToCorner(ContentAlignment.TopLeft));
        snap.DropDownItems.Add("Top right", null, (_, _) => SnapToCorner(ContentAlignment.TopRight));
        snap.DropDownItems.Add("Bottom left", null, (_, _) => SnapToCorner(ContentAlignment.BottomLeft));
        snap.DropDownItems.Add("Bottom right", null, (_, _) => SnapToCorner(ContentAlignment.BottomRight));
        m.Items.Add(snap);

        m.Items.Add(Check("Full size", _fullSize, ToggleFullSize, "Double-click"));
        m.Items.Add(new ToolStripMenuItem("Reset zoom", null, (_, _) => ResetZoom()) { Enabled = _zoom > 1, ShortcutKeyDisplayString = "Middle-click" });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Reconnect", null, (_, _) => Restart()));
        m.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => _app.ShowSettings()));
        m.Items.Add(new ToolStripMenuItem("Close", null, (_, _) => Close()) { ShortcutKeyDisplayString = "Alt+F4" });
    }

    private static ToolStripMenuItem Check(string text, bool on, Action click, string? shortcut = null)
        => new(text, null, (_, _) => click()) { Checked = on, ShortcutKeyDisplayString = shortcut };

    private void ShowMenu() => _menu.Show(Cursor.Position);

    // ------------------------------------------------------------------ actions

    public void SetMuted(bool muted)
    {
        if (_state.Muted == muted) return;
        _state.Muted = muted;
        _app.SaveSoon();
        if (!muted) _app.OnCameraUnmuted(this);
        // Audio isn't decoded while muted, so turning it on reconnects with audio; turning it off just mutes.
        if (muted) { if (_player != null) _player.Mute = true; }
        else Restart();
    }

    private void SetQuality(string? q)
    {
        _state.Quality = q;
        _app.SaveSoon();
        ResetZoom();
        Restart();
    }

    public void Rename(string name)
    {
        CameraName = name;
        Text = name;
        _state.Name = name;
    }

    public void BringForward()
    {
        if (_hidden) SetHidden(false);
        if (!Visible) Show();
        if (!TopMost)
        {
            TopMost = true;   // raise without stealing focus
            TopMost = false;
        }
    }

    // ------------------------------------------------------------------ geometry

    private void RestoreSavedBounds()
    {
        if (_state.HasBounds && IsOnScreen(_state.Bounds))
        {
            Bounds = _state.Bounds;
            return;
        }
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var offset = 32 * (_app.OpenWindowCount % 8);
        var w = LogicalToDeviceUnits(480);
        var h = HeightForWidth(w);
        Bounds = new Rectangle(wa.Right - w - 16 - offset, wa.Top + 16 + offset, w, h);
    }

    public void EnsureVisibleAfterDisplayChange()
    {
        if (_fullSize) return;
        if (_state.HasBounds && IsOnScreen(_state.Bounds)) { Bounds = _state.Bounds; return; }
        if (!IsOnScreen(Bounds))
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? Bounds;
            Location = new Point(wa.Left + 16, wa.Top + 16);
        }
    }

    private static bool IsOnScreen(Rectangle r) => Screen.AllScreens.Any(s =>
    {
        var i = Rectangle.Intersect(s.WorkingArea, r);
        return i.Width >= 60 && i.Height >= 40;
    });

    private int HeightForWidth(int width) => (int)Math.Round((width - 2 * BorderPx) / Math.Max(0.2, _state.Aspect)) + 2 * BorderPx;

    private void SetAspect(double aspect)
    {
        if (aspect < 0.2 || aspect > 6 || Math.Abs(aspect - _state.Aspect) < 0.01) return;
        _state.Aspect = aspect;
        if (!_fullSize && !_state.Locked)
        {
            Height = HeightForWidth(Width);
            SaveBounds();
        }
    }

    private void ApplySizePreset(int logicalWidth)
    {
        if (_fullSize) ToggleFullSize();
        var w = LogicalToDeviceUnits(logicalWidth);
        var r = new Rectangle(Left, Top, w, HeightForWidth(w));
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        if (r.Right > wa.Right) r.X = Math.Max(wa.Left, wa.Right - r.Width);
        if (r.Bottom > wa.Bottom) r.Y = Math.Max(wa.Top, wa.Bottom - r.Height);
        Bounds = r;
        SaveBounds();
        QueueQualityCheck();
    }

    private void SnapToCorner(ContentAlignment corner)
    {
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        var x = corner is ContentAlignment.TopLeft or ContentAlignment.BottomLeft ? wa.Left : wa.Right - Width;
        var y = corner is ContentAlignment.TopLeft or ContentAlignment.TopRight ? wa.Top : wa.Bottom - Height;
        Location = new Point(x, y);
        SaveBounds();
    }

    private void ToggleFullSize()
    {
        if (!_fullSize)
        {
            _preFullBounds = Bounds;
            _fullSize = true;
            Padding = new Padding(0);
            Bounds = Screen.FromRectangle(Bounds).Bounds;
        }
        else
        {
            _fullSize = false;
            Padding = new Padding(BorderPx);
            Bounds = _preFullBounds;
        }
        if (_fullSize) NotifyActivity();
        QueueQualityCheck();
    }

    private void SaveBounds()
    {
        if (_fullSize) return;
        _state.Bounds = Bounds;
        _app.SaveSoon();
    }

    private void BeginWindowDrag()
    {
        if (_state.Locked || _fullSize) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); BackColor = BorderColor(true); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BackColor = BorderColor(false); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && _fullSize) ToggleFullSize();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCHITTEST:
                base.WndProc(ref m);
                if (m.Result == (IntPtr)Native.HTCLIENT && !_state.Locked && !_fullSize)
                {
                    var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                    var hit = HitTestEdges(p);
                    if (hit != Native.HTCLIENT) m.Result = (IntPtr)hit;
                }
                return;

            case Native.WM_ENTERSIZEMOVE:
                Pin(); // moving or resizing a pop-up keeps it
                _moveStartCursor = Cursor.Position;
                _moveStartLocation = Location;
                _moving = true;
                break;

            case Native.WM_MOVING:
            {
                var r = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                if (_moving)
                {
                    // Position always follows the cursor from where the drag started, so a snap
                    // only holds while the cursor is within the snap distance — never sticky.
                    var cur = Cursor.Position;
                    int w = r.Width, h = r.Height;
                    r.Left = _moveStartLocation.X + (cur.X - _moveStartCursor.X);
                    r.Top = _moveStartLocation.Y + (cur.Y - _moveStartCursor.Y);
                    r.Right = r.Left + w;
                    r.Bottom = r.Top + h;
                }
                if ((ModifierKeys & Keys.Alt) == 0) SnapMove(ref r);
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }

            case Native.WM_SIZING:
            {
                var r = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                AdjustSizing((int)m.WParam, ref r);
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }

            case Native.WM_EXITSIZEMOVE:
                _moving = false;
                SaveBounds();
                QueueQualityCheck();
                break;
        }
        base.WndProc(ref m);
    }

    private int HitTestEdges(Point p)
    {
        var b = Math.Max(BorderPx, 4);
        var corner = LogicalToDeviceUnits(16);
        bool left = p.X < b, right = p.X >= ClientSize.Width - b, top = p.Y < b, bottom = p.Y >= ClientSize.Height - b;
        bool nearLeft = p.X < corner, nearRight = p.X >= ClientSize.Width - corner;
        bool nearTop = p.Y < corner, nearBottom = p.Y >= ClientSize.Height - corner;
        if ((top && nearLeft) || (left && nearTop)) return Native.HTTOPLEFT;
        if ((top && nearRight) || (right && nearTop)) return Native.HTTOPRIGHT;
        if ((bottom && nearLeft) || (left && nearBottom)) return Native.HTBOTTOMLEFT;
        if ((bottom && nearRight) || (right && nearBottom)) return Native.HTBOTTOMRIGHT;
        if (left) return Native.HTLEFT;
        if (right) return Native.HTRIGHT;
        if (top) return Native.HTTOP;
        if (bottom) return Native.HTBOTTOM;
        return Native.HTCLIENT;
    }

    private int SnapDistance => LogicalToDeviceUnits(14);

    /// <summary>Snap a moving window to monitor work-area edges and other camera windows.</summary>
    private void SnapMove(ref Native.RECT r)
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        var T = SnapDistance;
        var others = _app.OtherWindowBounds(this).ToList();

        int bestDx = int.MaxValue, bestDy = int.MaxValue;
        void TryX(int d) { if (Math.Abs(d) <= T && Math.Abs(d) < Math.Abs(bestDx)) bestDx = d; }
        void TryY(int d) { if (Math.Abs(d) <= T && Math.Abs(d) < Math.Abs(bestDy)) bestDy = d; }

        TryX(wa.Left - r.Left); TryX(wa.Right - r.Right);
        TryY(wa.Top - r.Top); TryY(wa.Bottom - r.Bottom);
        foreach (var o in others)
        {
            bool vOverlap = r.Top < o.Bottom + T && r.Bottom > o.Top - T;
            bool hOverlap = r.Left < o.Right + T && r.Right > o.Left - T;
            if (vOverlap) { TryX(o.Right - r.Left); TryX(o.Left - r.Right); TryX(o.Left - r.Left); TryX(o.Right - r.Right); }
            if (hOverlap) { TryY(o.Bottom - r.Top); TryY(o.Top - r.Bottom); TryY(o.Top - r.Top); TryY(o.Bottom - r.Bottom); }
        }
        if (bestDx != int.MaxValue) { r.Left += bestDx; r.Right += bestDx; }
        if (bestDy != int.MaxValue) { r.Top += bestDy; r.Bottom += bestDy; }
    }

    /// <summary>Keep the video's aspect ratio while resizing (hold Shift for a free shape) and snap dragged edges.</summary>
    private void AdjustSizing(int edge, ref Native.RECT r)
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        var T = SnapDistance;
        bool movesLeft = edge is Native.WMSZ_LEFT or Native.WMSZ_TOPLEFT or Native.WMSZ_BOTTOMLEFT;
        bool movesRight = edge is Native.WMSZ_RIGHT or Native.WMSZ_TOPRIGHT or Native.WMSZ_BOTTOMRIGHT;
        bool movesTop = edge is Native.WMSZ_TOP or Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT;
        bool movesBottom = edge is Native.WMSZ_BOTTOM or Native.WMSZ_BOTTOMLEFT or Native.WMSZ_BOTTOMRIGHT;

        if (movesLeft && Math.Abs(r.Left - wa.Left) <= T) r.Left = wa.Left;
        if (movesRight && Math.Abs(r.Right - wa.Right) <= T) r.Right = wa.Right;
        if (movesTop && Math.Abs(r.Top - wa.Top) <= T) r.Top = wa.Top;
        if (movesBottom && Math.Abs(r.Bottom - wa.Bottom) <= T) r.Bottom = wa.Bottom;

        if ((ModifierKeys & Keys.Shift) != 0) return;

        var b2 = 2 * BorderPx;
        var aspect = Math.Max(0.2, _state.Aspect);
        if (edge is Native.WMSZ_TOP or Native.WMSZ_BOTTOM)
        {
            var w = (int)Math.Round((r.Height - b2) * aspect) + b2;
            r.Right = r.Left + Math.Max(MinimumSize.Width, w);
        }
        else
        {
            var h = (int)Math.Round((r.Width - b2) / aspect) + b2;
            h = Math.Max(MinimumSize.Height, h);
            if (movesTop) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
        }
    }

    // ------------------------------------------------------------------ lifetime

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_app.IsExiting && !_popup)
        {
            _state.Open = false;
            _app.SaveSoon();
        }
        StopPlayback();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _retryTimer.Dispose();
        _watchdog.Dispose();
        _popupTimer.Dispose();
        _qualityTimer.Dispose();
        _overlay.BackgroundImage?.Dispose();
        _menu.Dispose();
        _app.OnWindowClosed(this);
        base.OnFormClosed(e);
    }
}
