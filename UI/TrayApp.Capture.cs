using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CamDock.Core;

namespace CamDock.UI;

/// <summary>
/// Documentation screenshot mode. Put a file named <c>capture-screenshots.txt</c> next to CamDock.exe
/// (optionally containing an output folder) and start CamDock — or start it again while it's running.
/// CamDock captures its own windows to PNG files, writes <c>capture-done.txt</c> and deletes the trigger.
/// Only CamDock's own windows are captured; nothing else on the screen is recorded.
/// </summary>
internal sealed partial class TrayApp
{
    private const string CaptureTrigger = "capture-screenshots.txt";
    private bool _capturing;

    private static string TriggerPath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath)!, CaptureTrigger);

    private bool TryStartCapture()
    {
        if (_capturing || !File.Exists(TriggerPath)) return false;
        string outDir;
        try
        {
            var text = File.ReadAllText(TriggerPath).Trim();
            // Default: <repo>\docs\screenshots when the exe is in the repo's PUBLISH folder, else next to the exe.
            var exeDir = Path.GetDirectoryName(TriggerPath)!;
            var repo = Directory.GetParent(exeDir)?.FullName;
            outDir = text.Length > 0 ? text
                : repo != null && (Directory.Exists(Path.Combine(repo, ".git")) || File.Exists(Path.Combine(repo, "CamDock.csproj")))
                    ? Path.Combine(repo, "docs", "screenshots")
                    : Path.Combine(exeDir, "screenshots");
            File.Delete(TriggerPath);
        }
        catch (Exception ex) { Log.Warn($"Capture trigger unreadable: {ex.Message}"); return false; }
        _capturing = true;
        _ = RunCaptureAsync(outDir);
        return true;
    }

    private async Task RunCaptureAsync(string outDir)
    {
        var done = new List<string>();
        void Note(string s) { done.Add(s); Log.Info("[capture] " + s); }
        var openedHere = new List<CameraWindow>();
        try
        {
            Directory.CreateDirectory(outDir);
            Note($"Output: {outDir}");
            if (_cameras.Count == 0) await RefreshCamerasAsync(force: true);

            // 1. camera windows: top up to three, side by side, for the desktop shot (extra ones close afterwards)
            var needed = 3 - _windows.Count(w => !w.Value.IsPopup && w.Value.Visible);
            if (needed > 0)
            {
                var wa = Screen.PrimaryScreen!.WorkingArea;
                var w = Math.Min(640, (wa.Width - 80 - 24) / 3);
                var x = wa.Left + 40;
                foreach (var cam in _cameras.Where(c => c.IsOnline && !IsDoorbell(c.Id) && !_windows.ContainsKey(c.Id)).Take(needed))
                {
                    var st = Config.WindowState(cam.Id);
                    var saved = st.HasBounds ? st.Bounds : (Rectangle?)null;
                    st.Bounds = new Rectangle(x, wa.Top + 40, w, (int)(w / Math.Max(0.5, st.Aspect)) + 10);
                    OpenCamera(cam.Id, bringForward: true);
                    if (_windows.TryGetValue(cam.Id, out var win)) openedHere.Add(win);
                    if (saved is { } b) st.Bounds = b; else st.HasBounds = false; // don't change the user's layout
                    x += w + 12;
                }
            }
            var cams = _windows.Values.Where(w => !w.IsPopup && w.Visible).ToList();
            foreach (var w in cams) { w.SetHidden(false); w.BringForward(); }
            await WaitUntil(() => cams.All(w => w.IsPlaying), 25);
            await Task.Delay(2500);
            var shots = new List<(Rectangle, Bitmap)>();
            foreach (var w in cams)
            {
                w.TopMost = true;
                await Task.Delay(300);
                var bmp = Grab(w.Bounds);
                shots.Add((w.Bounds, bmp));
                Save(new Bitmap(bmp), outDir, $"camera-{Slug(w.CameraName)}.png");
                w.TopMost = Config.WindowState(w.CameraId).TopMost;
                Note($"camera window: {w.CameraName}");
            }
            if (shots.Count > 0)
            {
                Save(Composite(shots), outDir, "desktop.png");
                foreach (var (_, b) in shots) b.Dispose();
                Note("desktop composite");
            }

            // 2. camera right-click menu
            if (cams.FirstOrDefault() is { } first)
            {
                var p = new Point(first.Left + first.Width / 2, first.Top + 40);
                first.Menu.Show(p);
                await Task.Delay(500);
                Save(Grab(first.Menu.Bounds), outDir, "camera-menu.png");
                first.Menu.Close();
                Note("camera menu");
            }

            // 3. tray menu, with the Alerts submenu open
            BuildMenu();
            var area = Screen.PrimaryScreen!.WorkingArea;
            _menu.Show(new Point(area.Right - _menu.PreferredSize.Width - 420, area.Bottom - _menu.PreferredSize.Height - 20));
            await Task.Delay(500);
            Save(Grab(_menu.Bounds), outDir, "tray-menu.png");
            var alerts = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text?.StartsWith("Alerts") == true);
            if (alerts != null)
            {
                alerts.ShowDropDown();
                await Task.Delay(500);
                Save(Grab(Rectangle.Union(_menu.Bounds, alerts.DropDown.Bounds)), outDir, "tray-alerts.png");
            }
            _menu.Close();
            Note("tray menu");

            // 4. doorbell pop-up
            var bell = _cameras.FirstOrDefault(c => IsDoorbell(c.Id) && c.IsOnline);
            if (bell != null && !_windows.ContainsKey(bell.Id))
            {
                OpenCamera(bell.Id, bringForward: true, popup: true);
                if (_windows.TryGetValue(bell.Id, out var pop))
                {
                    await WaitUntil(() => pop.IsPlaying, 25);
                    await Task.Delay(2500);
                    Save(Grab(pop.Bounds), outDir, "doorbell-popup.png");
                    pop.Menu.Show(new Point(pop.Left + 30, pop.Top + 30));
                    await Task.Delay(500);
                    Save(Grab(Rectangle.Union(pop.Bounds, pop.Menu.Bounds)), outDir, "doorbell-menu.png");
                    pop.Menu.Close();
                    pop.Close();
                    Note("doorbell pop-up");
                }
            }

            // 5. settings tabs
            ShowSettings();
            if (_settings is { } sf)
            {
                sf.TopMost = true;
                for (var i = 0; i < sf.Tabs.TabPages.Count; i++)
                {
                    sf.Tabs.SelectedIndex = i;
                    await Task.Delay(600);
                    Save(Grab(sf.Bounds), outDir, $"settings-{i + 1}-{Slug(sf.Tabs.TabPages[i].Text)}.png");
                }
                sf.Close();
                Note("settings tabs");
            }

            // 6. alert history
            if (History.Items.Count > 0) ShowHistory();
            if (History.Items.Count > 0 && _historyForm is { } hf)
            {
                hf.TopMost = true;
                await Task.Delay(1200);
                Save(Grab(hf.Bounds), outDir, "alert-history.png");
                hf.Close();
                Note("alert history");
            }

            // 7. first-run setup (on a throwaway copy of the settings; nothing is saved)
            var setup = new SetupForm(Config.Clone()) { TopMost = true };
            setup.Show();
            await Task.Delay(1000);
            Save(Grab(setup.Bounds), outDir, "setup.png");
            setup.Close();
            Note("setup");
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot capture failed", ex);
            done.Add("ERROR: " + ex.Message);
        }
        finally
        {
            foreach (var w in openedHere) if (!w.IsDisposed) w.Close();
            _capturing = false;
            try { File.WriteAllLines(Path.Combine(outDir, "capture-done.txt"), done); } catch { /* ignore */ }
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int seconds)
    {
        for (var i = 0; i < seconds * 4 && !cond(); i++) await Task.Delay(250);
    }

    private static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Camera windows at their real positions on a neutral background (no desktop or other apps).</summary>
    private static Bitmap Composite(List<(Rectangle Bounds, Bitmap Image)> shots)
    {
        var union = shots.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        union.Inflate(40, 40);
        var bmp = new Bitmap(union.Width, union.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, union.Width, union.Height),
                   Color.FromArgb(32, 44, 64), Color.FromArgb(14, 18, 28), 45f))
            g.FillRectangle(bg, 0, 0, union.Width, union.Height);
        foreach (var (b, img) in shots)
        {
            using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            g.FillRectangle(shadow, b.X - union.X + 6, b.Y - union.Y + 8, b.Width, b.Height);
            g.DrawImage(img, b.X - union.X, b.Y - union.Y);
        }
        return bmp;
    }

    private static void Save(Bitmap bmp, string dir, string name)
    {
        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
        bmp.Dispose();
    }

    private static string Slug(string s) =>
        string.Concat(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-').Replace("--", "-");
}
