using LibVLCSharp.Shared;

namespace CamDock.Core;

/// <summary>Single shared LibVLC instance (one per process) used by every camera window.</summary>
internal static class VlcHost
{
    private static LibVLC? _vlc;
    private static readonly object Gate = new();
    private static DateTime _lastLog = DateTime.MinValue;

    public static LibVLC Instance
    {
        get
        {
            lock (Gate)
            {
                if (_vlc != null) return _vlc;
                // Single-file publish extracts libvlc\win-x64 next to the app (AppContext.BaseDirectory).
                var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
                if (Directory.Exists(dir)) LibVLCSharp.Shared.Core.Initialize(dir);
                else LibVLCSharp.Shared.Core.Initialize();
                _vlc = new LibVLC(
                    "--no-osd",
                    "--no-video-title-show",
                    "--no-snapshot-preview",
                    "--no-sub-autodetect-file",
                    "--no-spu",
                    "--avcodec-hw=any",        // hardware decoding (D3D11VA / DXVA2)
                    "--drop-late-frames",
                    "--skip-frames",
                    "--no-lua",
                    "--no-media-library",
                    "--ignore-config",
                    "--quiet");
                _vlc.Log += OnLog;
                Log.Info($"LibVLC {_vlc.Version} initialised.");
                return _vlc;
            }
        }
    }

    private static void OnLog(object? sender, LogEventArgs e)
    {
        if (e.Level != LogLevel.Error) return;
        // Throttle: VLC can be chatty when a stream drops.
        var now = DateTime.UtcNow;
        if (now - _lastLog < TimeSpan.FromSeconds(2)) return;
        _lastLog = now;
        Log.Warn($"VLC [{e.Module}] {e.Message}");
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _vlc?.Dispose(); } catch { /* ignore */ }
            _vlc = null;
        }
    }
}
