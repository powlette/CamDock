using CamDock.Core;
using CamDock.UI;

namespace CamDock;

internal static class Program
{
    private const string MutexName = @"Local\CamDock.SingleInstance";
    private const string ActivateEventName = @"Local\CamDock.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        var migration = AppPaths.MigrateLegacy();
        Log.Init();
        if (migration != null) Log.Info(migration);
        Log.Info($"Starting CamDock {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion} (args: {string.Join(' ', args)})");

        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info("Another instance is running; asking it to come forward.");
            try { using var ev = EventWaitHandle.OpenExisting(ActivateEventName); ev.Set(); } catch { /* ignore */ }
            return;
        }
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            Dialogs.Error("Something went wrong", e.Exception.Message + "\n\nThe app will keep running. Details were written to the log.", e.Exception.ToString());
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"Fatal unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            try
            {
                MessageBox.Show($"CamDock hit an unexpected error and must close.\n\n{(e.ExceptionObject as Exception)?.Message}\n\nLog: {Log.CurrentFile}",
                    "CamDock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            var config = ConfigStore.Load(out var warning);
            if (warning != null) Dialogs.Warn("Settings were reset", warning);

            var firstRun = false;
            if (!config.IsConfigured || string.IsNullOrEmpty(config.ApiKey))
            {
                using var setup = new SetupForm(config);
                if (setup.ShowDialog() != DialogResult.OK)
                {
                    Log.Info("Setup cancelled.");
                    return;
                }
                ConfigStore.Save(config);
                Log.Info($"Initial configuration saved for {config.NvrUrl}.");
                firstRun = true;
            }

            // Replace the startup entry from before the rename, then keep it pointing at this exe.
            if (StartupManager.RemoveLegacy()) config.RunAtStartup = true;
            if (config.RunAtStartup)
            {
                try { StartupManager.Apply(true); } catch { /* logged */ }
            }

            var app = new TrayApp(config, firstRun);
            var ui = SynchronizationContext.Current!;
            var listener = new Thread(() =>
            {
                while (activate.WaitOne())
                {
                    if (app.IsExiting) break;
                    ui.Post(_ => app.OnSecondInstance(), null);
                }
            }) { IsBackground = true, Name = "SingleInstanceListener" };
            listener.Start();

            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup error", ex);
            Dialogs.Error("CamDock couldn't start", ex.Message, ex.ToString());
        }
        Log.Info("Stopped.");
    }
}
