using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("JoyPro.Gui.Tests.RealFolderGuardLastOrderer", "JoyPro.Gui.Tests")]

namespace JoyPro.Gui.Tests;

// The end-of-run check of the test sandbox (docs/testing.md "Test sandbox"): a fingerprint (file list, size, time stamp)
// of the PC's real game and JoyPro folders, taken before the first test, compared with the folders after the last.
// Covered: %USERPROFILE%\Saved Games, the config locations of every game the engine finds on this PC ("games installs",
// files, folders and HKCU keys), %LOCALAPPDATA%\JoyPro2, %APPDATA%\JoyPro2 and %LOCALAPPDATA%\JoyPro. A game's own logs,
// tracks, screenshots and caches are left out (a game running beside the tests writes them); JoyPro's folders count
// only as a warning while a JoyPro of the user runs (it writes there itself).
[SupportedOSPlatform("windows")]
internal static class RealFolderGuard
{
    private static readonly string[] NoiseFolders = ["Logs", "Log", "Tracks", "Temp", "Screenshots", "Cache", "Caches", "CrashReports", "Crashes", "Replays"];
    private static readonly string[] NoiseExtensions = [".log", ".trk", ".tmp", ".dmp"];

    private static Dictionary<string, string>? before;
    private static readonly List<string> Roots = [];
    private static readonly List<string> JoyProRoots = [];
    private static readonly List<string> RegistryKeys = [];
    private static bool checkedAtEnd;

    public static string Description => string.Join(Environment.NewLine, Roots.Concat(RegistryKeys.Select(k => "HKCU\\" + k)));

    // Entries of the first fingerprint (files and registry values).
    public static int Count => before?.Count ?? 0;

    // Before the first test (TestEnvironment): the folders and their fingerprint.
    public static void Start(string? engine)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Add(Roots, Path.Combine(home, "Saved Games"));
        foreach (string joypro in new[] { Path.Combine(local, "JoyPro2"), Path.Combine(roaming, "JoyPro2"), Path.Combine(local, "JoyPro") })
        {
            Add(Roots, joypro);
            Add(JoyProRoots, joypro);
        }
        foreach (string location in GameConfigLocations(engine))
        {
            if (location.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            {
                if (!RegistryKeys.Contains(location[5..], StringComparer.OrdinalIgnoreCase))
                    RegistryKeys.Add(location[5..]);
            }
            else
                Add(Roots, location);
        }
        before = Fingerprint();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CheckAtExit();
    }

    // What changed since Start (empty: nothing). Warnings: changes a running JoyPro of the user may have made.
    public static List<string> Changes(out List<string> warnings)
    {
        warnings = [];
        checkedAtEnd = true;
        if (before == null)
            return ["the fingerprint was never taken (TestEnvironment did not run)"];
        Dictionary<string, string> after = Fingerprint();
        List<string> changes = [];
        foreach (KeyValuePair<string, string> entry in after)
        {
            if (!before.TryGetValue(entry.Key, out string? old))
                changes.Add("new: " + entry.Key);
            else if (old != entry.Value)
                changes.Add("changed: " + entry.Key + " (" + old + " -> " + entry.Value + ")");
        }
        changes.AddRange(before.Keys.Where(k => !after.ContainsKey(k)).Select(k => "deleted: " + k));
        if (UserJoyProRunning())
        {
            List<string> joypro = changes.Where(c => JoyProRoots.Any(r => c.Contains(r, StringComparison.OrdinalIgnoreCase))).ToList();
            warnings.AddRange(joypro);
            changes.RemoveAll(joypro.Contains);
        }
        return changes;
    }

    private static void CheckAtExit()
    {
        // A filtered run that left out the last test: the check still runs, its result goes to stderr and a file.
        if (checkedAtEnd || before == null)
            return;
        try
        {
            List<string> changes = Changes(out List<string> warnings);
            if (changes.Count == 0 && warnings.Count == 0)
                return;
            string report = "JoyPro2 GUI tests changed real game or JoyPro folders:" + Environment.NewLine +
                            string.Join(Environment.NewLine, changes.Concat(warnings.Select(w => "(warning, a JoyPro of the user runs) " + w)));
            Console.Error.WriteLine(report);
            string file = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "real-folder-guard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, report);
        }
        catch (Exception)
        {
        }
    }

    private static void Add(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        // A path inside a root already covered adds nothing; a root inside the new one is replaced by it.
        if (list.Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
            return;
        list.RemoveAll(r => r.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase));
        list.Add(full);
    }

    // The config locations the engine finds for the installed games of this PC (read only: a temp data folder and
    // the sandbox of this run).
    private static List<string> GameConfigLocations(string? engine)
    {
        List<string> locations = [];
        if (engine == null || !File.Exists(engine))
            return locations;
        try
        {
            ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
            info.ArgumentList.Add("games");
            info.ArgumentList.Add("installs");
            info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "guard-data-" + Guid.NewGuid().ToString("N"));
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            using JsonDocument json = JsonDocument.Parse(output);
            foreach (JsonElement game in json.RootElement.GetProperty("Games").EnumerateArray())
            {
                if (!game.TryGetProperty("ConfigPaths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement path in paths.EnumerateArray())
                {
                    string value = path.GetString() ?? string.Empty;
                    if (value.Length == 0)
                        continue;
                    // A config file (also one not written yet): its folder (the game writes its other files beside it).
                    locations.Add(Directory.Exists(value) ? value : Path.GetDirectoryName(value)!);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RealFolderGuard: the game config locations could not be read (" + ex.Message + "); only Saved Games and the JoyPro folders are checked.");
        }
        return locations;
    }

    private static Dictionary<string, string> Fingerprint()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in Roots)
        {
            if (File.Exists(root))
            {
                AddFile(result, new FileInfo(root));
                continue;
            }
            if (!Directory.Exists(root))
                continue;
            Walk(result, new DirectoryInfo(root));
        }
        foreach (string key in RegistryKeys)
        {
            try
            {
                using RegistryKey? registry = Registry.CurrentUser.OpenSubKey(key, false);
                if (registry == null)
                    continue;
                foreach (string name in registry.GetValueNames())
                {
                    object? value = registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    byte[] bytes = value switch
                    {
                        byte[] b => b,
                        null => [],
                        _ => Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                    };
                    result["HKCU\\" + key + "\\" + name] = Convert.ToHexString(SHA256.HashData(bytes))[..16];
                }
            }
            catch (Exception)
            {
            }
        }
        return result;
    }

    private static void Walk(Dictionary<string, string> result, DirectoryInfo dir)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = dir.GetFileSystemInfos();
        }
        catch (Exception)
        {
            return;
        }
        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo sub)
            {
                // Links are not followed; a game's own logs and caches are left out.
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || NoiseFolders.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                Walk(result, sub);
            }
            else if (entry is FileInfo file && !NoiseExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                AddFile(result, file);
        }
    }

    private static void AddFile(Dictionary<string, string> result, FileInfo file)
    {
        try
        {
            result[file.FullName] = file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
        }
    }

    // The user's own JoyPro (GUI, tray, watcher) runs beside the tests: it writes into the JoyPro folders itself.
    private static bool UserJoyProRunning()
    {
        string tests = AppContext.BaseDirectory;
        foreach (string name in new[] { "JoyPro.Gui", "JoyPro.Tray", "JoyPro.Watch", "JoyPro.Ffb", "JoyPro.VJoy" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path == null || !path.StartsWith(Path.GetFullPath(Path.Combine(tests, "..", "..", "..", "..", "..")), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (Exception)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}

// The guard's check runs after every other test (tests run one at a time, TestParallelization.cs).
public sealed class RealFolderGuardLastOrderer : ITestCollectionOrderer
{
    private readonly ITestCollectionOrderer inner;

    public RealFolderGuardLastOrderer(IMessageSink diagnosticMessageSink)
    {
        inner = new DefaultTestCollectionOrderer();
    }

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        List<ITestCollection> ordered = inner.OrderTestCollections(testCollections).ToList();
        return ordered.Where(c => c.DisplayName != RealFolderGuardTests.CollectionName)
            .Concat(ordered.Where(c => c.DisplayName == RealFolderGuardTests.CollectionName));
    }
}

[CollectionDefinition(RealFolderGuardTests.CollectionName)]
public sealed class RealFolderGuardCollection
{
}

[Collection(RealFolderGuardTests.CollectionName)]
[SupportedOSPlatform("windows")]
public sealed class RealFolderGuardTests
{
    public const string CollectionName = "ZZ Real folder guard (runs last)";

    // The last test of the run: no test changed a real game folder, Saved Games or JoyPro folder, and no engine a test
    // started wrote (or tried to write) outside the test sandbox.
    [Fact]
    public void NoTestChangedRealGameOrJoyProFolders()
    {
        Assert.True(RealFolderGuard.Count > 0 || !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games")),
            "the fingerprint of the real folders is empty");
        Console.WriteLine("RealFolderGuard: " + RealFolderGuard.Count + " entries in" + Environment.NewLine + RealFolderGuard.Description);
        List<string> changes = RealFolderGuard.Changes(out List<string> warnings);
        foreach (string warning in warnings)
            Console.Error.WriteLine("RealFolderGuard (a JoyPro of the user runs, not counted): " + warning);
        Assert.True(changes.Count == 0,
            "Tests changed real game or JoyPro folders of this PC (checked: " + Environment.NewLine + RealFolderGuard.Description + "):" +
            Environment.NewLine + string.Join(Environment.NewLine, changes.Take(200)));
        string violations = Path.Combine(TestEnvironment.SandboxRoot, "sandbox-violations.log");
        Assert.False(File.Exists(violations),
            "An engine started by a test tried to write outside the test sandbox (the writes were refused):" + Environment.NewLine +
            (File.Exists(violations) ? string.Join(Environment.NewLine, File.ReadLines(violations).Take(80)) : string.Empty));
    }
}
