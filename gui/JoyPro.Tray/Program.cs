using System;
using System.IO;
using System.Windows.Forms;
using JoyPro.Watch;

namespace JoyPro.Tray;

// JoyPro.Tray [--tray] [--gui <JoyPro.Gui.exe>] [--engine <engine.exe>]
// One per user and settings folder; a second start ends at once. The GUI and the engine are found next to the tray
// folder (<install>\tray\JoyPro.Tray.exe -> <install>\JoyPro.Gui.exe, <install>\engine\engine.exe).
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The background log (docs/logging.md): tray-<date>.log in <data>\Logs, what the watcher decided.
        JoyPro.Logging.JoyProLog.Start("tray", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?", args);
        InstanceNames names = InstanceNames.Current;
        using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(names.Tray);
        if (guard == null)
        {
            JoyPro.Logging.JoyProLog.Info("Tray", "Another tray host runs for these settings; this one ends");
            return 0;
        }

        // The menu and notifications in the GUI's language (the GUI writes tray-strings.json).
        WatchText.Load(names.SettingsFolder);
        string install = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string guiExe = Option(args, "--gui") ?? Path.Combine(install, JoyProPaths.GuiExeName);
        string engineExe = Option(args, "--engine") ?? Path.Combine(install, "engine", "engine.exe");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using TrayApplication app = new(names, guiExe, engineExe);
        Application.Run(app);
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
                return Path.GetFullPath(args[i + 1]);
        }
        return null;
    }
}
