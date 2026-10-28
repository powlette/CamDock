using System.Runtime.InteropServices;

namespace CamDock.Core;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
internal sealed class Hotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public Hotkeys()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
    }

    /// <summary>Parses "Win+Alt+C", "Ctrl+Shift+F9", … Returns false for empty or invalid text.</summary>
    public static bool TryParse(string? text, out uint mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None && mods != 0;
    }

    /// <summary>Registers a hotkey; returns an error message, or null on success (or when text is empty).</summary>
    public string? Register(string? text, Action action)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, out var mods, out var key)) return $"\"{text}\" isn't a valid shortcut";
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, (uint)key))
            return $"{text} is already used by another app";
        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var a))
        {
            try { a(); } catch (Exception ex) { Log.Warn("Hotkey action failed", ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
