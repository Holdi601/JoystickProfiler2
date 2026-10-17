using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using JoyPro.Watch;

namespace JoyPro.Gui.Services;

// The tray host (<install>\tray\JoyPro.Tray.exe, docs/sync-watcher.md#tray-host): started when tray mode is switched
// on, when the GUI starts or closes with tray mode on. It ends a second copy of itself at once and pauses while the
// GUI runs. Started through ExternalProcess, so the tests never start it.
[SupportedOSPlatform("windows")]
public static class TrayHostLauncher
{
    public static string TrayExePath => Path.Combine(AppContext.BaseDirectory, JoyProPaths.TrayFolderName, JoyProPaths.TrayExeName);

    public static bool IsRunning(InstanceNames names)
    {
        if (!Mutex.TryOpenExisting(SingleInstanceGuard.MutexName(names.Tray), out Mutex? mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    // True when it runs (or was started now).
    public static bool EnsureRunning(InstanceNames names)
    {
        if (IsRunning(names))
            return true;
        string exe = TrayExePath;
        if (!File.Exists(exe))
            return false;
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
        };
        start.ArgumentList.Add(AutostartService.TrayArgument);
        try
        {
            using Process? process = ExternalProcess.Start(start);
            return process != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
