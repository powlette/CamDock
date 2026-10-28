using CamDock.Core;

namespace CamDock.UI;

internal static class Dialogs
{
    public static void Error(string heading, string text, string? details = null) =>
        Show(TaskDialogIcon.Error, heading, text, details);

    public static void Warn(string heading, string text, string? details = null) =>
        Show(TaskDialogIcon.Warning, heading, text, details);

    private static void Show(TaskDialogIcon icon, string heading, string text, string? details)
    {
        try
        {
            var openLog = new TaskDialogButton("Open log folder");
            var page = new TaskDialogPage
            {
                Caption = "CamDock",
                Heading = heading,
                Text = text,
                Icon = icon,
                Buttons = { TaskDialogButton.OK, openLog },
                Footnote = new TaskDialogFootnote($"Log: {Log.CurrentFile}"),
            };
            if (!string.IsNullOrEmpty(details))
                page.Expander = new TaskDialogExpander(details) { CollapsedButtonText = "Show details", ExpandedButtonText = "Hide details" };
            if (TaskDialog.ShowDialog(page) == openLog) TrayApp.OpenFolder(AppPaths.LogFolder);
        }
        catch
        {
            MessageBox.Show(text + (details != null ? "\n\n" + details : ""), heading,
                MessageBoxButtons.OK, icon == TaskDialogIcon.Error ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
        }
    }
}
