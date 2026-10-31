using Avalonia;
using System;
using System.Runtime.Versioning;

namespace JoyPro.Gui;

[SupportedOSPlatform("windows")]
sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Background log (docs/logging.md): <data>\Logs\gui-<date>.log; the engine and JoyPro.Ffb it starts log into
        // the same folder with this session's id.
        JoyPro.Logging.JoyProLog.Start("gui", Services.AppInfo.Version, args);
        JoyPro.Logging.JoyProLog.Info("App", "Install folder: " + Services.AppInfo.InstallFolder + " | data: " + Services.AppInfo.DataFolder + " | settings: " + Services.AppInfo.SettingsFolder);
        Services.CrashReporter.Install();
        Watch.InstanceNames names = new(Services.AppInfo.SettingsFolder);
        Services.StartupOptions options = Services.StartupOptions.Parse(args);
        // --tray (the Run entry of the first tray mode): the tray host watches, not the GUI.
        if (options.StartInTray)
        {
            Services.TrayHostLauncher.EnsureRunning(names);
            return;
        }
        // One JoyPro2 per user: a second start brings the running one to the front and hands over its --review list.
        using Watch.SingleInstanceGuard? guard = Watch.SingleInstanceGuard.TryAcquire(names.Gui);
        if (guard == null)
        {
            AppShell.ForwardReviewKeys(names, options.ReviewKeys);
            Watch.SingleInstanceGuard.SignalExisting(names.Gui);
            return;
        }
        // The tray host hands the watching over to this GUI until it ends (no double prompts).
        Watch.GuiPresence.Announce(names);
        App.InstanceGuard = guard;
        App.Names = names;
        // Problem reports of 2.4.x move from Documents\JoyPro2 Problem Reports to Documents\JoyPro2\Problem Reports (once,
        // in the background: the window does not wait for it).
        System.Threading.Tasks.Task.Run(Services.ProblemReporter.MigrateOldFolder);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            JoyPro.Logging.JoyProLog.Info("App", "JoyPro2 closed normally");
        }
        catch (Exception ex)
        {
            Services.CrashReporter.Write(ex, "startup / main loop");
            throw;
        }
        finally
        {
            App.InstanceGuard = null;
            App.Names = null;
            JoyPro.Logging.JoyProLog.Flush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
