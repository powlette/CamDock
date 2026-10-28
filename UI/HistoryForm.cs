using CamDock.Core;

namespace CamDock.UI;

/// <summary>Alerts seen in the last 24 hours with snapshots; double-click opens the camera live.</summary>
internal sealed class HistoryForm : Form
{
    private readonly TrayApp _app;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly ImageList _thumbs = new() { ImageSize = new Size(128, 72), ColorDepth = ColorDepth.Depth24Bit };
    private readonly ComboBox _camera = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox _missedOnly = new() { Text = "Only alerts I wasn't notified about", AutoSize = true, Margin = new Padding(12, 6, 3, 0) };
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<string, int> _imageIndex = new();

    public HistoryForm(TrayApp app)
    {
        _app = app;
        Text = "Alert history — last 24 hours";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 620);
        MinimumSize = new Size(560, 360);
        ShowInTaskbar = true;

        _list.SmallImageList = _thumbs;
        _list.Columns.Add("Snapshot / time", 230);
        _list.Columns.Add("Camera", 170);
        _list.Columns.Add("Event", 170);
        _list.Columns.Add("Status", 200);
        _list.DoubleClick += (_, _) => OpenLive();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) OpenLive();
            if (e.Control && e.KeyCode == Keys.C) CopyPicture();
        };
        _list.ContextMenuStrip = _menu;
        _menu.Opening += (_, e) =>
        {
            _menu.Items.Clear();
            var sel = Selected();
            if (sel == null) { e.Cancel = true; return; }
            _menu.Items.Add("Watch live", null, (_, _) => OpenLive());
            var hasPic = sel.Snapshot != null && File.Exists(sel.Snapshot);
            _menu.Items.Add(new ToolStripMenuItem("Open picture", null, (_, _) => OpenPicture()) { Enabled = hasPic });
            _menu.Items.Add(new ToolStripMenuItem("Copy picture", null, (_, _) => CopyPicture()) { Enabled = hasPic, ShortcutKeyDisplayString = "Ctrl+C" });
            _menu.Items.Add(new ToolStripMenuItem("Save picture as…", null, (_, _) => SavePicture()) { Enabled = hasPic });
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6), WrapContents = false };
        bar.Controls.Add(new Label { Text = "Camera:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        bar.Controls.Add(_camera);
        bar.Controls.Add(new Label { Text = "Event:", AutoSize = true, Margin = new Padding(12, 6, 3, 0) });
        bar.Controls.Add(_type);
        bar.Controls.Add(_missedOnly);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 6), FlowDirection = FlowDirection.RightToLeft };
        var clear = new Button { Text = "Clear history", AutoSize = true };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete all saved alerts and their pictures?", "Clear history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _app.History.Clear();
        };
        bottom.Controls.Add(clear);
        bottom.Controls.Add(new Label
        {
            Text = "Only alerts received while the viewer was running are listed. Double-click to watch live.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 12, 0),
        });

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(bottom);

        _type.Items.Add("All events");
        foreach (var (_, label) in AlertTypes.All) _type.Items.Add(label);
        _type.SelectedIndex = 0;
        _camera.SelectedIndexChanged += (_, _) => Reload();
        _type.SelectedIndexChanged += (_, _) => Reload();
        _missedOnly.CheckedChanged += (_, _) => Reload();
        _app.History.Changed += OnHistoryChanged;
        FillCameras();
        Reload();
    }

    private void OnHistoryChanged()
    {
        if (IsDisposed) return;
        FillCameras();
        Reload();
    }

    private void FillCameras()
    {
        var current = _camera.SelectedItem as string;
        var names = _app.History.Items.Select(e => e.CameraName).Distinct().OrderBy(n => n).ToList();
        _camera.BeginUpdate();
        _camera.Items.Clear();
        _camera.Items.Add("All cameras");
        foreach (var n in names) _camera.Items.Add(n);
        _camera.SelectedItem = current != null && _camera.Items.Contains(current) ? current : "All cameras";
        _camera.EndUpdate();
    }

    private void Reload()
    {
        var cam = _camera.SelectedIndex > 0 ? _camera.SelectedItem as string : null;
        var type = _type.SelectedIndex > 0 ? AlertTypes.All[_type.SelectedIndex - 1].Key : null;
        var items = _app.History.Items.Where(e =>
            (cam == null || e.CameraName == cam) &&
            (type == null || e.Types.Contains(type)) &&
            (!_missedOnly.Checked || e.Status != "Notified")).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in items)
        {
            var row = new ListViewItem(e.TimeUtc.ToLocalTime().ToString(e.TimeUtc.ToLocalTime().Date == DateTime.Today ? "T" : "ddd T"))
            {
                Tag = e, ImageIndex = Thumb(e.Snapshot),
            };
            row.SubItems.Add(e.CameraName);
            row.SubItems.Add(string.Join(", ", e.Types.Select(AlertTypes.Label)));
            row.SubItems.Add(e.Status);
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        Text = $"Alert history — last 24 hours ({items.Count})";
    }

    private int Thumb(string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        if (_imageIndex.TryGetValue(path, out var i)) return i;
        try
        {
            using var src = Image.FromFile(path);
            var bmp = new Bitmap(_thumbs.ImageSize.Width, _thumbs.ImageSize.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                var scale = Math.Min((double)bmp.Width / src.Width, (double)bmp.Height / src.Height);
                int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, (bmp.Width - w) / 2, (bmp.Height - h) / 2, w, h);
            }
            _thumbs.Images.Add(bmp);
            return _imageIndex[path] = _thumbs.Images.Count - 1;
        }
        catch { return -1; }
    }

    private AlertEntry? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as AlertEntry : null;

    private void OpenLive()
    {
        if (Selected() is { } e) _app.OpenCamera(e.CameraId, bringForward: true);
    }

    private void OpenPicture()
    {
        if (Selected()?.Snapshot is { } p && File.Exists(p))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    private void CopyPicture()
    {
        if (Selected()?.Snapshot is not { } p || !File.Exists(p)) return;
        using var img = Image.FromFile(p);
        Clipboard.SetImage(img);
    }

    private void SavePicture()
    {
        if (Selected() is not { Snapshot: { } p } e || !File.Exists(p)) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "JPEG image|*.jpg", InitialDirectory = _app.Config.SnapshotFolder,
            FileName = $"{e.CameraName} {e.TimeUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}.jpg",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) File.Copy(p, dlg.FileName, overwrite: true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _app.History.Changed -= OnHistoryChanged;
        _thumbs.Dispose();
        _menu.Dispose();
        base.OnFormClosed(e);
    }
}
