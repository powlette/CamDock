using System.Media;

namespace CamDock.Core;

/// <summary>Plays the configured sound when a camera reports motion (per-camera toggle, with cooldown).</summary>
internal sealed class MotionSound : IDisposable
{
    public static readonly (string Key, string Label)[] SystemSoundChoices =
    {
        ("system:Asterisk", "Windows: Asterisk"), ("system:Beep", "Windows: Default beep"),
        ("system:Exclamation", "Windows: Exclamation"), ("system:Hand", "Windows: Critical stop"),
        ("system:Question", "Windows: Question"),
    };

    private readonly Dictionary<string, DateTime> _lastPlayed = new();
    private readonly HashSet<string> _seenEvents = new();
    private readonly Queue<string> _seenOrder = new();
    private SoundPlayer? _player;
    private string? _loadedPath;

    /// <summary>Motion = plain motion or any smart detection (person, vehicle, …) starting.</summary>
    public static bool IsMotion(ProtectEvent ev) =>
        ev.Type is "motion" or "smartDetectZone" or "smartDetectLine" or "smartDetectLoiterZone";

    public void Handle(ProtectEvent ev, AppConfig cfg)
    {
        if (!IsMotion(ev) || ev.DeviceId == null) return;
        if (!cfg.Windows.TryGetValue(ev.DeviceId, out var st) || !st.MotionSound) return;
        // one sound per event (Protect sends "add" then "update"s for the same event)
        if (!_seenEvents.Add(ev.Id)) return;
        _seenOrder.Enqueue(ev.Id);
        while (_seenOrder.Count > 300) _seenEvents.Remove(_seenOrder.Dequeue());

        if (cfg.MotionSoundRespectsQuiet && AlertGate.HoldReason(cfg) != null) return;
        var now = DateTime.UtcNow;
        if (_lastPlayed.TryGetValue(ev.DeviceId, out var last) && now - last < TimeSpan.FromSeconds(Math.Max(0, cfg.MotionSoundCooldownSeconds))) return;
        _lastPlayed[ev.DeviceId] = now;
        Play(cfg.MotionSoundFile);
    }

    /// <summary>Plays a sound choice; returns an error message or null.</summary>
    public string? Play(string? choice)
    {
        try
        {
            switch (choice)
            {
                case "system:Asterisk": SystemSounds.Asterisk.Play(); return null;
                case "system:Beep": SystemSounds.Beep.Play(); return null;
                case "system:Exclamation": SystemSounds.Exclamation.Play(); return null;
                case "system:Hand": SystemSounds.Hand.Play(); return null;
                case "system:Question": SystemSounds.Question.Play(); return null;
            }
            if (string.IsNullOrWhiteSpace(choice) || !File.Exists(choice))
            {
                SystemSounds.Asterisk.Play();
                return $"Sound file not found: {choice}";
            }
            if (_player == null || _loadedPath != choice)
            {
                _player?.Dispose();
                _player = new SoundPlayer(choice);
                _player.Load();
                _loadedPath = choice;
            }
            _player.Play(); // async, doesn't block the UI
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Motion sound failed ({choice}): {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>.wav files in C:\Windows\Media, for the settings list.</summary>
    public static IEnumerable<string> WindowsMediaSounds()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    public void Dispose() => _player?.Dispose();
}
