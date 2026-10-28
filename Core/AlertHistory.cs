using System.Text.Json;

namespace CamDock.Core;

internal sealed class AlertEntry
{
    public string Id { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public List<string> Types { get; set; } = new();
    /// <summary>Snapshot file in the app's snapshot folder, if one was fetched.</summary>
    public string? Snapshot { get; set; }
    /// <summary>"Notified", "Held (…)" or "Not notified".</summary>
    public string Status { get; set; } = "";
}

/// <summary>
/// Alerts the app has seen, kept for 24 hours across restarts (history.json + snapshot files).
/// The Integration API has no event history, so this only covers time the app was running.
/// </summary>
internal sealed class AlertHistory
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    private static string FilePath => Path.Combine(AppPaths.Root, "history.json");

    private readonly List<AlertEntry> _items = new();
    public event Action? Changed;

    public IReadOnlyList<AlertEntry> Items => _items;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _items.AddRange(JsonSerializer.Deserialize<List<AlertEntry>>(File.ReadAllText(FilePath), ConfigStore.JsonOptions) ?? new());
        }
        catch (Exception ex) { Log.Warn($"Alert history couldn't be read and was reset: {ex.Message}"); }
        // Snapshot paths saved before the rename point at the old data folder.
        foreach (var e in _items)
            if (e.Snapshot != null && e.Snapshot.StartsWith(AppPaths.LegacyRoot, StringComparison.OrdinalIgnoreCase))
                e.Snapshot = AppPaths.Root + e.Snapshot[AppPaths.LegacyRoot.Length..];
        Prune(save: false);
        DeleteOrphanSnapshots();
    }

    public void Add(AlertEntry e)
    {
        _items.Insert(0, e);
        Prune(save: true);
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (var e in _items) TryDelete(e.Snapshot);
        _items.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Prune(bool save)
    {
        var cutoff = DateTime.UtcNow - Keep;
        var removed = _items.Where((e, i) => e.TimeUtc < cutoff || i >= MaxEntries).ToList();
        foreach (var e in removed)
        {
            _items.Remove(e);
            TryDelete(e.Snapshot);
        }
        if (save) Save();
    }

    private void Save()
    {
        try
        {
            AppPaths.Ensure();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, ConfigStore.JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"Saving alert history failed: {ex.Message}"); }
    }

    private void DeleteOrphanSnapshots()
    {
        try
        {
            if (!Directory.Exists(AppPaths.SnapshotFolder)) return;
            var keep = new HashSet<string>(_items.Where(e => e.Snapshot != null).Select(e => e.Snapshot!), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppPaths.SnapshotFolder, "*.jpg"))
                if (!keep.Contains(f)) TryDelete(f);
        }
        catch { /* ignore */ }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { /* in use / gone */ }
    }
}
