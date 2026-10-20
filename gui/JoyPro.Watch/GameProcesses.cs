using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace JoyPro.Watch;

// A running process of a game and how it was recognised (for the log).
public sealed record RunningGame(Process Process, string How);

// Finds a running process of a game for the config watcher (docs/sync-watcher.md): by its process names first, then,
// when the engine lists image folders for the game (MSFS Microsoft Store: XboxGames\<game>\Content, WindowsApps
// \<family>_...), any process whose exe lies in one of them, whatever it is called (the Store build may start as
// gamelaunchhelper.exe or under another name). Only runs when a watched config changed, never on a timer; every Process
// object it does not return is disposed.
[SupportedOSPlatform("windows")]
public static class GameProcesses
{
    public static RunningGame? Find(IReadOnlyList<string> names, IReadOnlyList<string>? folders)
    {
        foreach (string name in names ?? [])
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }
            Process? first = processes.FirstOrDefault();
            foreach (Process process in processes.Skip(1))
                process.Dispose();
            if (first != null)
                return new RunningGame(first, "process name " + name);
        }

        List<string> prefixes = (folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (prefixes.Count == 0)
            return null;
        RunningGame? found = null;
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception)
        {
            return null;
        }
        foreach (Process process in all)
        {
            if (found == null)
            {
                string image = ImagePath(process.Id);
                string? prefix = image.Length == 0 ? null : prefixes.FirstOrDefault(p => image.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (prefix != null)
                {
                    found = new RunningGame(process, "exe in the game's folder: " + image);
                    continue;
                }
            }
            process.Dispose();
        }
        return found;
    }

    // The full exe path of a process (limited query rights are enough, also for an elevated one); empty when unknown.
    public static string ImagePath(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return string.Empty;
        try
        {
            StringBuilder text = new(1024);
            int size = text.Capacity;
            return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString(0, size) : string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
