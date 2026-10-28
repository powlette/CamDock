namespace CamDock.UI;

/// <summary>Minimal single-line text prompt.</summary>
internal static class InputDialog
{
    public static string? Ask(string title, string prompt, int maxLength, string initial = "")
    {
        using var f = new Form
        {
            Text = title, Icon = TrayApp.AppIcon, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(420, 130),
            ShowInTaskbar = true, TopMost = true, Padding = new Padding(12),
        };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        var box = new TextBox { Dock = DockStyle.Top, MaxLength = maxLength, Text = initial };
        var count = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = SystemColors.GrayText };
        void Update() => count.Text = $"{box.TextLength}/{maxLength}";
        box.TextChanged += (_, _) => Update();
        Update();
        var ok = new Button { Text = "Send", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        f.Controls.Add(count);
        f.Controls.Add(box);
        f.Controls.Add(label);
        f.Controls.Add(buttons);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => { f.Activate(); box.Focus(); };
        return f.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
