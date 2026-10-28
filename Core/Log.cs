using System.Text;

namespace CamDock.Core;

/// <summary>Application paths under %LOCALAPPDATA%\CamDock.</summary>
internal static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamDock");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string LogFolder => Path.Combine(Root, "logs");
    public static string SnapshotFolder => Path.Combine(Root, "snapshots");

    /// <summary>Data folder used before the app was renamed to CamDock.</summary>
    public static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniFiProtectViewer");

    /// <summary>One-time move of settings, history, snapshots and logs from the old data folder.</summary>
    public static string? MigrateLegacy()
    {
        try
        {
            if (!Directory.Exists(LegacyRoot) || Directory.Exists(Root)) return null;
            Directory.Move(LegacyRoot, Root);
            // Drop the old WebView2 profile from early builds if it came along.
            var wv = Path.Combine(Root, "WebView2");
            if (Directory.Exists(wv)) try { Directory.Delete(wv, true); } catch { /* in use */ }
            return $"Moved data from {LegacyRoot} to {Root}";
        }
        catch (Exception ex)
        {
            return $"Couldn't move data from {LegacyRoot}: {ex.Message}";
        }
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(SnapshotFolder);
    }
}

/// <summary>Minimal thread-safe rolling file logger (one file per day, 14 days kept).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static bool _initialised;

    public static string CurrentFile => Path.Combine(AppPaths.LogFolder, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Init()
    {
        try
        {
            AppPaths.Ensure();
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogFolder, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
            }
        }
        catch { /* logging must never crash the app */ }
        _initialised = true;
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (!_initialised) Init();
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ")
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (ex != null) sb.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));
        sb.AppendLine();
        lock (Gate)
        {
            try { File.AppendAllText(CurrentFile, sb.ToString()); }
            catch { /* ignore */ }
        }
        System.Diagnostics.Debug.Write(sb.ToString());
    }
}
