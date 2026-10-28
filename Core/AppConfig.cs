using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamDock.Core;

/// <summary>Detection categories that can raise desktop notifications.</summary>
internal static class AlertTypes
{
    public const string Motion = "motion";
    public const string Person = "person";
    public const string Vehicle = "vehicle";
    public const string Animal = "animal";
    public const string Package = "package";
    public const string LicensePlate = "licensePlate";
    public const string Face = "face";
    public const string Doorbell = "ring";
    public const string Audio = "audio";

    public static readonly (string Key, string Label)[] All =
    {
        (Motion, "Motion"), (Person, "Person"), (Vehicle, "Vehicle"), (Animal, "Animal"),
        (Package, "Package"), (LicensePlate, "Plate"), (Face, "Face"), (Doorbell, "Doorbell"), (Audio, "Audio"),
    };

    public static readonly string[] Defaults = { Person, Doorbell, Package };

    public static string Label(string key) => All.FirstOrDefault(a => a.Key == key).Label ?? key;
}

internal sealed class CameraNotifySettings
{
    public string Name { get; set; } = "";
    public List<string> Types { get; set; } = new(AlertTypes.Defaults);
    /// <summary>Pop the camera's live window up when one of its alerts fires.</summary>
    public bool Popup { get; set; }
}

internal sealed class CameraWindowState
{
    public string Name { get; set; } = "";
    public bool Open { get; set; }
    public bool HasBounds { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Aspect { get; set; } = 16.0 / 9.0;
    public bool TopMost { get; set; }
    public bool Locked { get; set; }
    public bool Muted { get; set; } = true;
    /// <summary>Stream quality for this window (high / medium / low); null = app default.</summary>
    public string? Quality { get; set; }

    [JsonIgnore] public Rectangle Bounds
    {
        get => new(X, Y, Width, Height);
        set { X = value.X; Y = value.Y; Width = value.Width; Height = value.Height; HasBounds = true; }
    }
}

internal sealed class AppConfig
{
    public static readonly string[] Qualities = { "high", "medium", "low" };

    public int Version { get; set; } = 1;
    public string NvrUrl { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public string? ProtectVersion { get; set; }

    public bool RunAtStartup { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotificationSnapshots { get; set; } = true;
    public int NotificationCooldownSeconds { get; set; } = 30;
    /// <summary>Default stream quality for camera windows.</summary>
    public string DefaultQuality { get; set; } = "medium";
    /// <summary>Use encrypted RTSPS (port 7441) instead of plain RTSP (port 7447).</summary>
    public bool EncryptedStreams { get; set; }
    /// <summary>Network buffer in milliseconds (lower = less delay, higher = smoother on poor networks).</summary>
    public int StreamBufferMs { get; set; } = 500;

    // ---- pop-up windows
    public bool PopupOnDoorbell { get; set; } = true;
    public int PopupSeconds { get; set; } = 60;

    // ---- offline notices
    public bool OfflineNotices { get; set; } = true;

    // ---- snooze / quiet hours
    public DateTime? SnoozeUntilUtc { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public string QuietStart { get; set; } = "22:00";
    public string QuietEnd { get; set; } = "07:00";
    public List<DayOfWeek> QuietDays { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public bool DoorbellBreaksThrough { get; set; } = true;

    // ---- doorbell quick replies
    public List<string> DoorbellReplies { get; set; } = new() { "Be right there", "One moment please" };
    public int DoorbellMessageMinutes { get; set; } = 2;
    /// <summary>Cameras that have sent a ring event (doorbell detection fallback).</summary>
    public List<string> DoorbellIds { get; set; } = new();

    // ---- global hotkeys
    public bool HotkeysEnabled { get; set; } = true;
    // Ctrl+Alt+Shift combos: Win+Alt+… collide with Windows 11 (Win+Alt+D = clock/calendar) and Game Bar.
    public string HotkeyToggleAll { get; set; } = "Ctrl+Alt+Shift+C";
    public string HotkeyDoorbell { get; set; } = "Ctrl+Alt+Shift+D";
    public string HotkeySnooze { get; set; } = "Ctrl+Alt+Shift+S";
    public string HotkeyHistory { get; set; } = "Ctrl+Alt+Shift+H";
    public string HotkeySnapshot { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>Move shortcuts still set to the old Win+Alt defaults onto the new defaults.</summary>
    public void MigrateHotkeys()
    {
        static string M(string v, string oldDefault, string newDefault) =>
            string.Equals(v, oldDefault, StringComparison.OrdinalIgnoreCase) ? newDefault : v;
        HotkeyToggleAll = M(HotkeyToggleAll, "Win+Alt+C", "Ctrl+Alt+Shift+C");
        HotkeyDoorbell = M(HotkeyDoorbell, "Win+Alt+D", "Ctrl+Alt+Shift+D");
        HotkeySnooze = M(HotkeySnooze, "Win+Alt+S", "Ctrl+Alt+Shift+S");
        HotkeyHistory = M(HotkeyHistory, "Win+Alt+H", "Ctrl+Alt+Shift+H");
        HotkeySnapshot = M(HotkeySnapshot, "Win+Alt+P", "Ctrl+Alt+Shift+P");
    }

    // ---- quality by window size / idle
    public bool AutoQuality { get; set; } = true;
    /// <summary>Minutes without any event before a camera is turned down; 0 = never.</summary>
    public int IdleMinutes { get; set; } = 10;
    /// <summary>"low" = switch to the low stream, "pause" = stop the stream and show a still.</summary>
    public string IdleMode { get; set; } = "low";

    // ---- snapshots
    public string SnapshotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CamDock");

    public Dictionary<string, CameraNotifySettings> Notifications { get; set; } = new();
    public Dictionary<string, CameraWindowState> Windows { get; set; } = new();

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(NvrUrl) && !string.IsNullOrWhiteSpace(ApiKeyProtected);

    [JsonIgnore] public string ApiKey
    {
        get => Secret.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Secret.Protect(value);
    }

    public CameraWindowState WindowState(string cameraId)
    {
        if (!Windows.TryGetValue(cameraId, out var s)) Windows[cameraId] = s = new CameraWindowState();
        return s;
    }

    public CameraNotifySettings NotifySettings(string cameraId, string name)
    {
        if (!Notifications.TryGetValue(cameraId, out var s)) Notifications[cameraId] = s = new CameraNotifySettings();
        if (!string.IsNullOrEmpty(name)) s.Name = name;
        return s;
    }

    /// <summary>The quality picked for this window: its own choice, else by window size (auto), else the default.</summary>
    public string QualityFor(string cameraId, int logicalWidth = 0, bool fullSize = false)
    {
        var q = Windows.GetValueOrDefault(cameraId)?.Quality;
        if (q != null && Qualities.Contains(q)) return q;
        if (AutoQuality && (logicalWidth > 0 || fullSize)) return AutoQualityFor(logicalWidth, fullSize);
        return Qualities.Contains(DefaultQuality) ? DefaultQuality : "medium";
    }

    /// <summary>Protect's low stream is ~640×360, medium ~1280×720, high is full resolution.</summary>
    public static string AutoQualityFor(int logicalWidth, bool fullSize) =>
        fullSize || logicalWidth > 1300 ? "high" : logicalWidth > 700 ? "medium" : "low";

    public bool IsAutoQuality(string cameraId) => AutoQuality && Windows.GetValueOrDefault(cameraId)?.Quality == null;

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;
}

/// <summary>DPAPI (current user) protection for secrets in config.json.</summary>
internal static class Secret
{
    // Kept from the app's original name so secrets saved before the rename still decrypt.
    private const string LegacyEntropy = "UniFiProtectViewer.v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(LegacyEntropy);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not decrypt a stored secret (config copied from another user/PC?)", ex);
            return "";
        }
    }
}

internal static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();

    public static AppConfig Load(out string? warning)
    {
        warning = null;
        var path = AppPaths.ConfigFile;
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
            cfg.Notifications ??= new();
            cfg.Windows ??= new();
            cfg.MigrateHotkeys();
            // Snapshot folder default from before the rename
            var oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "UniFi Protect");
            if (string.Equals(cfg.SnapshotFolder, oldDefault, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(oldDefault))
                cfg.SnapshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CamDock");
            return cfg;
        }
        catch (Exception ex)
        {
            var backup = Path.Combine(AppPaths.Root, $"config.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Move(path, backup); } catch { /* ignore */ }
            Log.Error($"config.json could not be read; moved to {backup}", ex);
            warning = $"Your settings file could not be read and has been reset.\nA copy was saved as:\n{backup}";
            return new AppConfig();
        }
    }

    public static void Save(AppConfig cfg)
    {
        lock (Gate)
        {
            try
            {
                AppPaths.Ensure();
                var tmp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
                File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save config.json", ex);
            }
        }
    }
}
