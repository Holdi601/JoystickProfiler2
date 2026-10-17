using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace JoyPro.Gui.Services;

// The per-user autostart entry: one value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin rights).
// Abstracted so the tests never touch the real key (TestEnvironment replaces StoreFactory with an in-memory store).
public interface IRunKeyStore
{
    string? Get(string name);

    void Set(string name, string command);

    void Remove(string name);

    // Windows' own switch (Settings > Apps > Startup, StartupApproved\Run): false when the user turned the entry off
    // there, null when Windows has no opinion (never toggled).
    bool? IsApprovedByWindows(string name);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyStore : IRunKeyStore
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? Get(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool? IsApprovedByWindows(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, false);
        // 12 bytes; the first one is 2 (or 6) when enabled and 3 (or 7) when switched off.
        if (key?.GetValue(name) is not byte[] { Length: > 0 } data)
            return null;
        return (data[0] & 1) == 0;
    }
}

// Not the registry: what the tests (and a VM without persisted settings) use.
public sealed class InMemoryRunKeyStore : IRunKeyStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => values;

    public string? Get(string name) => values.TryGetValue(name, out string? value) ? value : null;

    public void Set(string name, string command) => values[name] = command;

    public void Remove(string name) => values.Remove(name);

    public bool? IsApprovedByWindows(string name) => Approved.TryGetValue(name, out bool approved) ? approved : null;
}

// "Start with Windows (tray)": the tray host (JoyPro.Tray.exe, not the GUI) starts when the user signs in. The command
// is the tray host next to the running GUI (portable installs start from where they are), quoted, plus --tray.
[SupportedOSPlatform("windows")]
public sealed class AutostartService
{
    public const string ValueName = "JoyPro2";
    public const string TrayArgument = "--tray";

    // The real Run key; the tests replace it before any view model exists.
    public static Func<IRunKeyStore> StoreFactory { get; set; } = () => new WindowsRunKeyStore();

    private readonly IRunKeyStore store;
    private readonly string command;
    private readonly Func<string, bool> fileExists;

    public AutostartService(IRunKeyStore store, string? command = null, Func<string, bool>? fileExists = null)
    {
        this.store = store;
        this.command = string.IsNullOrWhiteSpace(command) ? CurrentLaunchCommand() : command;
        this.fileExists = fileExists ?? File.Exists;
    }

    public string Command => command;

    public string? RegisteredCommand => store.Get(ValueName);

    public bool IsRegistered => !string.IsNullOrWhiteSpace(RegisteredCommand);

    // The entry starts this very copy of JoyPro2 (and not another install).
    public bool PointsHere => SameCommand(RegisteredCommand, command);

    public bool? ApprovedByWindows => store.IsApprovedByWindows(ValueName);

    public void Enable() => store.Set(ValueName, command);

    public void Disable() => store.Remove(ValueName);

    // At startup, with the setting on: writes the entry when it is missing, starts a JoyPro2 that no longer exists
    // (a portable copy that was moved) or starts the full GUI (the first tray mode did). An entry for another existing
    // install's tray host is left alone. True when it wrote.
    public bool Repair()
    {
        string? registered = RegisteredCommand;
        if (string.IsNullOrWhiteSpace(registered))
        {
            Enable();
            return true;
        }
        if (PointsHere)
            return false;
        string exe = ExecutableOf(registered);
        if (exe.Length > 0 && fileExists(exe) &&
            !string.Equals(Path.GetFileName(exe), JoyPro.Watch.JoyProPaths.GuiExeName, StringComparison.OrdinalIgnoreCase))
            return false;
        Enable();
        return true;
    }

    // "<exe>" --tray, or "<dotnet>" "<dll>" --tray.
    public static string BuildCommand(string exePath, string? dllPath = null)
    {
        string exe = Clean(exePath);
        if (exe.Length == 0)
            throw new ArgumentException("No executable path.", nameof(exePath));
        string result = Quote(exe);
        string dll = Clean(dllPath);
        if (dll.Length > 0)
            result += " " + Quote(dll);
        return result + " " + TrayArgument;
    }

    // The tray host of this install: <GUI folder>\tray\JoyPro.Tray.exe.
    public static string CurrentLaunchCommand() => BuildCommand(TrayHostLauncher.TrayExePath);

    // The program a Run command starts: the quoted part, or (unquoted, as Windows reads it) up to the first ".exe".
    public static string ExecutableOf(string? runCommand)
    {
        string text = (runCommand ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end < 0 ? text[1..] : text[1..end];
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return text[..(exe + 4)];
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    private static bool SameCommand(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    // Windows paths cannot contain quotes; a stray one would break the command line.
    private static string Clean(string? path) => (path ?? string.Empty).Replace("\"", string.Empty).Trim();

    private static string Quote(string path) => "\"" + path + "\"";
}
