using CamDock.Core;

namespace CamDock.UI;

internal sealed record ConnectionResult(string NvrUrl, string ApiKey, MetaInfo Meta, List<CameraInfo> Cameras);

/// <summary>NVR address + API key editor with validation, shared by Setup and Settings.</summary>
internal sealed class ConnectionPanel : UserControl
{
    private readonly TextBox _url = new() { Dock = DockStyle.Fill, PlaceholderText = "192.168.1.1  or  https://unvr.local" };
    private readonly TextBox _key = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "Paste the API key from UniFi Protect" };
    private readonly CheckBox _showKey = new() { Text = "Show", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, Height = 54 };

    private string _origUrl = "", _origKey = "";

    public string UrlText => _url.Text.Trim();
    public string KeyText => _key.Text.Trim();
    public bool ConnectionChanged => !string.Equals(UrlText, _origUrl, StringComparison.OrdinalIgnoreCase) || KeyText != _origKey;

    public ConnectionPanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Padding = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(Lbl("NVR address:"), 0, 0);
        grid.Controls.Add(_url, 1, 0);
        grid.SetColumnSpan(_url, 2);

        grid.Controls.Add(Lbl("API key:"), 0, 1);
        grid.Controls.Add(_key, 1, 1);
        grid.Controls.Add(_showKey, 2, 1);
        _showKey.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_showKey.Checked;

        var help = new LinkLabel { Text = "How do I create an API key?", AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
        help.LinkClicked += (_, _) => MessageBox.Show(FindForm(),
            "In the UniFi Protect web app on your console:\n\n" +
            "  1. Open Settings › Control Plane › Integrations (the location can vary slightly by version).\n" +
            "  2. Choose \"Create API Key\", give it a name such as \"CamDock\" and copy the key.\n\n" +
            "The key is used to list cameras, open their video streams, fetch alert snapshots and receive camera events. " +
            "It is stored encrypted for your Windows account only.",
            "Creating an API key", MessageBoxButtons.OK, MessageBoxIcon.Information);
        grid.Controls.Add(help, 1, 2);

        var note = new Label
        {
            Text = "Live video uses each camera's RTSP stream from Protect, so no web sign-in is needed. For cameras whose stream " +
                   "isn't turned on yet, the viewer can turn it on if the key has write (full management) access.",
            AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4),
        };
        grid.Controls.Add(note, 0, 3);
        grid.SetColumnSpan(note, 3);

        var testRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        testRow.Controls.Add(_test);
        grid.Controls.Add(testRow, 0, 4);
        grid.SetColumnSpan(testRow, 3);
        grid.Controls.Add(_status, 0, 5);
        grid.SetColumnSpan(_status, 3);

        _test.Click += async (_, _) => await ValidateAsync();
        Controls.Add(grid);
    }

    private static Label Lbl(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) };

    public void LoadFrom(AppConfig cfg)
    {
        _url.Text = _origUrl = cfg.NvrUrl;
        _key.Text = _origKey = cfg.ApiKey;
    }

    public void SaveTo(AppConfig cfg, ConnectionResult? validated)
    {
        if (validated != null)
        {
            cfg.NvrUrl = validated.NvrUrl;
            cfg.ApiKey = validated.ApiKey;
            if (!string.IsNullOrEmpty(validated.Meta.ApplicationVersion)) cfg.ProtectVersion = validated.Meta.ApplicationVersion;
            foreach (var c in validated.Cameras) cfg.NotifySettings(c.Id, c.Name);
        }
    }

    public async Task<ConnectionResult?> ValidateAsync()
    {
        if (!UrlHelper.TryNormalize(UrlText, out var url, out var err))
        {
            SetStatus(err, false);
            _url.Focus();
            return null;
        }
        if (KeyText.Length < 8)
        {
            SetStatus("Enter the API key created in UniFi Protect.", false);
            _key.Focus();
            return null;
        }
        _test.Enabled = false;
        UseWaitCursor = true;
        SetStatus($"Connecting to {url}…", null);
        try
        {
            using var api = new ProtectApi(url, KeyText);
            var meta = await api.GetMetaAsync();
            var cams = await api.GetCamerasAsync();
            _url.Text = url;
            var online = cams.Count(c => c.IsOnline);
            SetStatus($"✔ Connected to UniFi Protect {meta.ApplicationVersion} — {cams.Count} camera(s), {online} online.", true);
            Log.Info($"Connection test OK: {url}, Protect {meta.ApplicationVersion}, {cams.Count} cameras");
            return new ConnectionResult(url, KeyText, meta, cams);
        }
        catch (ProtectApiException ex)
        {
            Log.Warn($"Connection test failed for {url}: {ex.Kind} {ex.Message}");
            SetStatus("✖ " + ex.Message, false);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Connection test failed for {url}", ex);
            SetStatus("✖ " + ex.Message, false);
            return null;
        }
        finally
        {
            _test.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private void SetStatus(string text, bool? ok)
    {
        _status.Text = text;
        _status.ForeColor = ok switch { true => Color.ForestGreen, false => Color.Firebrick, _ => SystemColors.ControlText };
    }
}
