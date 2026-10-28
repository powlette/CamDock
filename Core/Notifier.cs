using Microsoft.Toolkit.Uwp.Notifications;

namespace CamDock.Core;

/// <summary>Turns Protect events into native Windows toast notifications.</summary>
internal sealed class Notifier
{
    private readonly Func<AppConfig> _config;
    private readonly Func<ProtectApi?> _api;
    private readonly Func<string, string> _cameraName;
    private readonly Dictionary<string, DateTime> _lastShown = new();
    private readonly HashSet<string> _notified = new();
    private readonly Queue<string> _notifiedOrder = new();

    /// <summary>Raised (on a background thread) when the user clicks a toast for a camera.</summary>
    public event Action<string>? CameraActivated;
    /// <summary>Raised (background thread) when a doorbell toast's quick-reply button is clicked: (cameraId, text).</summary>
    public event Action<string, string>? ReplyRequested;
    /// <summary>Raised on the UI thread when an alert passes all filters: (cameraId, categories).</summary>
    public event Action<string, IReadOnlyList<string>>? AlertRaised;
    /// <summary>Raised on the UI thread for every ring, even if notifications are off (doorbell detection).</summary>
    public event Action<string>? RingSeen;

    public Notifier(Func<AppConfig> config, Func<ProtectApi?> api, Func<string, string> cameraName)
    {
        _config = config;
        _api = api;
        _cameraName = cameraName;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }
        catch (Exception ex)
        {
            Log.Warn("Toast notifications unavailable", ex);
        }
    }

    /// <summary>Where alerts are recorded (set by the app).</summary>
    public AlertHistory? History { get; set; }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue("camera", out var cam) || string.IsNullOrEmpty(cam)) return;
            if (args.TryGetValue("reply", out var reply) && !string.IsNullOrEmpty(reply)) ReplyRequested?.Invoke(cam, reply);
            else CameraActivated?.Invoke(cam);
        }
        catch (Exception ex)
        {
            Log.Warn("Toast activation failed", ex);
        }
    }

    public static IEnumerable<string> Categorize(ProtectEvent ev)
    {
        switch (ev.Type)
        {
            case "motion":
                yield return AlertTypes.Motion;
                break;
            case "ring":
                yield return AlertTypes.Doorbell;
                break;
            case "smartAudioDetect":
                yield return AlertTypes.Audio;
                break;
            case "smartDetectZone":
            case "smartDetectLine":
            case "smartDetectLoiterZone":
                foreach (var t in ev.SmartTypes.Select(MapSmartType).Where(x => x != null).Distinct())
                    yield return t!;
                break;
        }
    }

    private static string? MapSmartType(string s) => s switch
    {
        "person" => AlertTypes.Person,
        "vehicle" or "car" => AlertTypes.Vehicle,
        "animal" or "pet" => AlertTypes.Animal,
        "package" => AlertTypes.Package,
        "licensePlate" => AlertTypes.LicensePlate,
        "face" => AlertTypes.Face,
        _ when s.StartsWith("alrm", StringComparison.Ordinal) => AlertTypes.Audio,
        _ => null,
    };

    public async void Handle(ProtectEvent ev)
    {
        try
        {
            var cfg = _config();
            if (string.IsNullOrEmpty(ev.DeviceId)) return;
            if (ev.Type == "ring") RingSeen?.Invoke(ev.DeviceId);
            var all = Categorize(ev).ToList();
            if (all.Count == 0) return;
            var cam = cfg.Notifications.GetValueOrDefault(ev.DeviceId);
            var enabled = new HashSet<string>(cam?.Types ?? new List<string>());

            // History: every smart detection / ring / audio alert once per event; plain motion only if enabled.
            var histCats = new List<string>();
            foreach (var c in all)
            {
                if (c == AlertTypes.Motion && !enabled.Contains(c)) continue;
                var key = "h|" + ev.Id + "|" + c;
                if (_notified.Contains(key)) continue;
                Remember(key);
                histCats.Add(c);
            }

            // Toasts: enabled types, once per event, with per-camera cooldown.
            var now = DateTime.UtcNow;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, cfg.NotificationCooldownSeconds));
            var cats = new List<string>();
            if (cfg.NotificationsEnabled && cam != null)
            {
                foreach (var c in all)
                {
                    if (!enabled.Contains(c)) continue;
                    var eventKey = ev.Id + "|" + c;
                    if (_notified.Contains(eventKey)) continue;
                    var coolKey = ev.DeviceId + "|" + c;
                    // Doorbell rings are never suppressed by the cooldown.
                    if (c != AlertTypes.Doorbell && _lastShown.TryGetValue(coolKey, out var last) && now - last < cooldown) continue;
                    _lastShown[coolKey] = now;
                    Remember(eventKey);
                    cats.Add(c);
                }
            }
            if (cats.Count == 0 && histCats.Count == 0) return;

            var isRing = cats.Contains(AlertTypes.Doorbell);
            var allowed = cats.Count > 0 && AlertGate.Allows(cfg, isRing);
            if (allowed) AlertRaised?.Invoke(ev.DeviceId, cats); // pop-up first: it shouldn't wait for the snapshot
            else if (cats.Count > 0) Log.Info($"Alert held ({AlertGate.HoldReason(cfg)}): {string.Join(",", cats)} on {ev.DeviceId}");

            var name = _cameraName(ev.DeviceId);
            string? image = null;
            if (_api() is { } api)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = await api.GetSnapshotAsync(ev.DeviceId, cts.Token);
                if (bytes is { Length: > 0 }) image = SaveSnapshot(ev.DeviceId, bytes);
            }

            if (histCats.Count > 0)
            {
                History?.Add(new AlertEntry
                {
                    Id = ev.Id, TimeUtc = ev.Start.ToUniversalTime(), CameraId = ev.DeviceId, CameraName = name,
                    Types = histCats, Snapshot = image,
                    Status = allowed ? "Notified" : cats.Count > 0 ? $"Held ({AlertGate.HoldReason(cfg)})" : "Not notified",
                });
            }

            if (!allowed) return;
            var title = isRing ? $"Doorbell — {name}" : $"{string.Join(", ", cats.Select(AlertTypes.Label))} — {name}";
            var replies = isRing ? cfg.DoorbellReplies.Where(r => !string.IsNullOrWhiteSpace(r)).Take(2).ToList() : new List<string>();
            Show(ev.DeviceId, title, $"Detected at {ev.Start:t}", cfg.NotificationSnapshots ? image : null, isRing, replies);
            Log.Info($"Notification: {title} (event {ev.Id})");
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to handle event", ex);
        }
    }

    public void ShowTest()
    {
        Show("", "CamDock", "Test notification — alerts are working.", null, false, new List<string>());
    }

    /// <summary>Camera / NVR offline or back-online notice (respects snooze and quiet hours).</summary>
    public void ShowStatus(string cameraId, string title, string body)
    {
        var cfg = _config();
        if (!cfg.NotificationsEnabled || !cfg.OfflineNotices) return;
        if (!AlertGate.Allows(cfg, false)) { Log.Info($"Status notice held: {title}"); return; }
        Show(cameraId, title, body, null, false, new List<string>());
        Log.Info($"Notification: {title}");
    }

    private static void Show(string cameraId, string title, string body, string? imagePath, bool important, List<string> replies)
    {
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("camera", cameraId)
                .AddText(title)
                .AddText(body);
            if (imagePath != null) b.AddInlineImage(new Uri(imagePath));
            if (!string.IsNullOrEmpty(cameraId))
            {
                foreach (var r in replies)
                    b.AddButton(new ToastButton().SetContent($"Reply: {r}").AddArgument("camera", cameraId).AddArgument("reply", r));
                b.AddButton(new ToastButton().SetContent("Watch live").AddArgument("camera", cameraId));
                b.AddButton(new ToastButtonDismiss());
            }
            if (important) b.SetToastScenario(ToastScenario.Reminder);
            b.Show();
        }
        catch (Exception ex)
        {
            Log.Warn("Toast could not be shown", ex);
        }
    }

    private void Remember(string key)
    {
        _notified.Add(key);
        _notifiedOrder.Enqueue(key);
        while (_notifiedOrder.Count > 500) _notified.Remove(_notifiedOrder.Dequeue());
    }

    private static string SaveSnapshot(string cameraId, byte[] bytes)
    {
        // Lifetime of these files is managed by AlertHistory (24 h).
        AppPaths.Ensure();
        var path = Path.Combine(AppPaths.SnapshotFolder, $"{cameraId}-{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
