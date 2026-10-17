using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace JoyPro.Gui.Services;

// The virtual joystick page's ties to the PC, replaceable like FfbTelemetryServices: the tests swap in fakes, so no
// test starts JoyPro.VJoy (which would write to the user's vJoy device), looks at game processes or writes into the
// user's data folder. HidHide and vJoyConfig are only reached through the engine (engine vjoy ...), which the tests
// fake or run with JOYPRO_VJOY_FAKE.
public static class VirtualJoystickServices
{
    // JoyPro.VJoy.exe speaks the same line protocol as JoyPro.Ffb.exe (stdout lines, "stop" on stdin), so the
    // FFB process host starts it.
    public static IFfbHostLauncher Launcher { get; set; } = new FfbHostLauncher();

    // The pid of the first running process with one of the names (without ".exe"), or null.
    public static Func<IReadOnlyList<string>, int?> FindGameProcess { get; set; } = GameProcessLookup.FindFirstPid;

    // The executable of a running process (the game JoyPro hides the devices from), or null.
    public static Func<int, string?> ProcessPath { get; set; } = ReadProcessPath;

    // <GUI folder>\vjoy\JoyPro.VJoy.exe (bundled by the BuildAndBundleVJoyProcess target).
    public static Func<string> ExePath { get; set; } = () => Path.Combine(AppContext.BaseDirectory, "vjoy", "JoyPro.VJoy.exe");

    // Where the forwarding table goes (JOYPRO_DATA_DIR like the engine, else %LOCALAPPDATA%\JoyPro).
    public static Func<string> DataFolder { get; set; } = () =>
    {
        string? root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
        return Path.Combine(root, "VirtualJoystick");
    };

    private static string? ReadProcessPath(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
