using CamDock.Core;

namespace CamDock.UI;

/// <summary>First-run configuration: NVR address and API key, validated before continuing.</summary>
internal sealed class SetupForm : Form
{
    private readonly AppConfig _config;
    private readonly ConnectionPanel _panel = new() { Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "Connect", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

    public SetupForm(AppConfig config)
    {
        _config = config;
        Text = "CamDock — Setup";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 470);
        Padding = new Padding(16);
        ShowInTaskbar = true;

        var heading = new Label
        {
            Text = "Connect to your UniFi Protect NVR",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4),
        };
        var sub = new Label
        {
            Text = "Enter the console's IP address or host name and a Protect API key. Both are checked before continuing.",
            AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(560, 0), Padding = new Padding(0, 0, 0, 12),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);

        Controls.Add(_panel);
        Controls.Add(buttons);
        Controls.Add(sub);
        Controls.Add(heading);
        AcceptButton = _ok;
        CancelButton = _cancel;

        _panel.LoadFrom(config);
        _ok.Click += async (_, _) => await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        _ok.Enabled = false;
        try
        {
            var result = await _panel.ValidateAsync();
            if (result == null) return;
            _panel.SaveTo(_config, result);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed) _ok.Enabled = true;
        }
    }
}
