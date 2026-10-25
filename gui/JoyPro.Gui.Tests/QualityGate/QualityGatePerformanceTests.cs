using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I7 of the quality gate (QualityGate.cs, budgets in Budgets.cs): profile load, the first Lay out with AI preview right
// after it (rules only, the local AI off: German words, the load corrections of an older profile), a relation with 200 actions, 200 more
// into it, save, suggestions of the profile's biggest relations, and an export per game: registry games as a dry run on
// a copy of their config, DCS / IL-2 / Star Citizen into a copy of their config folder (they have no dry run). The real
// config files are fingerprinted before and after: an export that touches them is a finding of its own.
// (Catalog, search, relation open and the games list are timed by I1-I6.)
[SupportedOSPlatform("windows")]
public sealed class QualityGatePerformanceTests
{
    [Fact]
    public void I7_OperationsStayWithinBudget()
    {
        if (!Gate.Enabled)
            return;
        string? profile = Gate.CopyProfile("performance");
        if (profile == null)
        {
            Gate.Note("I7: no profile (JOYPRO_QUALITY_GATE_PROFILE): profile timings skipped");
            Gate.Finish("I7");
            return;
        }
        GateSession.Run(profile, async s =>
        {
            MainWindowViewModel vm = s.Vm;
            await s.MeasureAsync("profile load", Path.GetFileName(Gate.SourceProfile!), () => vm.LoadProfileCommand.ExecuteAsync(null));
            vm.CurrentStage = UiStage.Relations;
            await s.SettleAsync(() => true);

            // The first Lay out with AI preview after opening the profile (Binds page; rules only, no local AI model).
            vm.LlmEnabled = false;
            vm.LayoutRequest = "Gas und linker Stick, Trimmung auf den Hat, weichere Kurven für Helis";
            await s.MeasureAsync("lay out with AI preview", Path.GetFileName(Gate.SourceProfile!) + ": " + vm.LayoutRequest, () => vm.AskLayoutCommand.ExecuteAsync(null));
            if (vm.LayoutPreferences.Count < 3 || vm.LayoutNotUnderstood.Count > 0)
                Gate.Fail("I7", "Lay out with AI preview did not understand the request", "Lay out", vm.LayoutRequest,
                    vm.LayoutPreferences.Count + " preferences, not understood: " + string.Join(" / ", vm.LayoutNotUnderstood) + "; " + Gate.Short(vm.LayoutStatus, 160));
            vm.CurrentStage = UiStage.Relations;
            await s.SettleAsync(() => true);

            // Suggestions of the five relations with the most games (the slow case: much to compare).
            foreach (RelationListItem relation in vm.Relations.OrderByDescending(r => (r.Games ?? string.Empty).Split(',').Length).ThenBy(r => r.Name).Take(5).ToList())
            {
                vm.SelectedRelation = relation;
                await Task.Delay(50);
                await s.SettleAsync(() => true);
                await s.MeasureAsync("suggestions", relation.Name + " (" + relation.Games + ")", () => vm.LoadBuilderSuggestionsAsync(relation.Name));
            }

            // A new relation with 200 button actions from the catalog, then 200 more into it.
            await AddTwoHundredAsync(s);

            await s.MeasureAsync("save", Path.GetFileName(Gate.SourceProfile!) + " + 400 actions", () => vm.SaveProfileCommand.ExecuteAsync(null));

            await UsageStatsAsync(s);

            await ExportAllAsync(s);
        });
        Gate.Finish("I7");
    }

    // The Statistics page's document (engine usage stats with the profile: binds per device, growth from its history,
    // the oldest unused bind) on a store harvested from the usage fixtures. The first read builds the growth cache.
    private static async Task UsageStatsAsync(GateSession s)
    {
        MainWindowViewModel vm = s.Vm;
        string root = Path.Combine(Gate.OutDir, "usage-root");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        UsageStoreTests.CopyFixtures(root);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_USAGE_ROOT");
        Environment.SetEnvironmentVariable("JOYPRO_USAGE_ROOT", root);
        try
        {
            await vm.HarvestUsageAsync(null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_USAGE_ROOT", previous);
        }
        await s.MeasureAsync("usage stats first", "Statistics page, " + Path.GetFileName(Gate.SourceProfile!), () => vm.Statistics.ReloadAsync());
        await s.MeasureAsync("usage stats", "Statistics page, " + Path.GetFileName(Gate.SourceProfile!), () => vm.Statistics.ReloadAsync());
        if (!vm.Statistics.HasData)
            Gate.Fail("I7", "the Statistics page has no data after a harvest", string.Empty, "usage stats", vm.Statistics.StatusText);
    }

    private static async Task AddTwoHundredAsync(GateSession s)
    {
        MainWindowViewModel vm = s.Vm;
        vm.StartNewBuilderRelationCommand.Execute(null);
        await s.SettleAsync(() => true);
        await vm.OpenCatalogCommand.ExecuteAsync(null);
        await vm.SelectCatalogAsync("DCS", "F-16C_50");
        List<BuilderSearchResultItem> rows = vm.CatalogRows().Where(r => !r.IsAxis && r.CanCheck).Take(200).ToList();
        if (rows.Count < 200)
        {
            Gate.Note("I7: the F-16C_50 catalog has only " + rows.Count + " free buttons; 200-action timing on fewer");
        }
        foreach (BuilderSearchResultItem row in rows)
            row.IsChecked = true;
        vm.RelationNameInput = "QG 200 actions";
        await s.MeasureAsync("relation add 200 nodes", "new relation, " + rows.Count + " DCS F-16C_50 buttons", () => vm.AddCheckedCatalogAsync());
        if (vm.SelectedRelation?.Name != "QG 200 actions")
        {
            Gate.Fail("I7", "a relation with 200 actions is not created/opened", "DCS", "QG 200 actions", "open: " + (vm.SelectedRelation?.Name ?? "(none)") + "; " + Gate.Short(vm.CatalogStatus, 120));
            return;
        }
        int before = vm.RelationNodes.Count;
        await vm.SelectCatalogAsync("DCS", "FA-18C_hornet");
        await s.SettleAsync(() => true);
        List<BuilderSearchResultItem> more = vm.CatalogRows().Where(r => !r.IsAxis && r.CanCheck).Take(200).ToList();
        foreach (BuilderSearchResultItem row in more)
            row.IsChecked = true;
        await s.MeasureAsync("relation add 200 nodes to existing", "QG 200 actions + " + more.Count + " DCS FA-18C_hornet buttons", () => vm.AddCheckedCatalogAsync());
        if (vm.RelationNodes.Count <= before)
            Gate.Fail("I7", "adding 200 actions to an existing relation added nothing", "DCS", "QG 200 actions", before + " -> " + vm.RelationNodes.Count + "; " + Gate.Short(vm.CatalogStatus, 120));
    }

    // ------------------------------------------------------------------ export per game

    private sealed record Install(string GameId, bool Installed, List<string> ConfigPaths, string InstallPath);

    private static async Task ExportAllAsync(GateSession s)
    {
        MainWindowViewModel vm = s.Vm;
        EngineCommandResult installsResult = await s.Engine.RunAsync(Gate.EnginePath, ["--status", "false", "games", "installs"]);
        List<Install> installs = [];
        using (JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(installsResult.StandardOutput)))
        {
            foreach (JsonElement g in doc.RootElement.GetProperty("Games").EnumerateArray())
            {
                List<string> paths = g.TryGetProperty("ConfigPaths", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(p => p.GetString() ?? string.Empty).Where(p => p.Length > 0).ToList()
                    : [];
                installs.Add(new Install(g.GetProperty("GameId").GetString() ?? string.Empty, g.TryGetProperty("Installed", out JsonElement i) && i.ValueKind == JsonValueKind.True,
                    paths, g.TryGetProperty("InstallPath", out JsonElement ip) ? ip.GetString() ?? string.Empty : string.Empty));
            }
        }
        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        string root = Path.Combine(Gate.OutDir, "export-copies");
        foreach (Install game in installs.Where(g => g.Installed))
        {
            string configPath = game.ConfigPaths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p)) ?? game.ConfigPaths.FirstOrDefault() ?? string.Empty;
            bool registryStorage = configPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || configPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase);
            if (configPath.Length == 0)
            {
                Gate.Note("I7 export: " + game.GameId + " has no config yet (never started): not timed");
                continue;
            }
            // What the export writes (DCS: Config\Input of Saved Games, not its logs and tracks).
            string watched = game.GameId == "DCS" ? Path.Combine(configPath, "Config", "Input") : configPath;
            Dictionary<string, (long, DateTime)> fingerprint = registryStorage ? [] : Fingerprint(watched);
            string copyDir = Path.Combine(root, game.GameId);
            if (Directory.Exists(copyDir))
                Directory.Delete(copyDir, recursive: true);
            Directory.CreateDirectory(copyDir);
            // A snapshot of the real config before anything runs: should an export write there after all, the gate puts
            // every file back (and reports it).
            string snapshot = Path.Combine(copyDir, "_snapshot");
            if (!registryStorage && !Snapshot(watched, snapshot))
            {
                Gate.Note("I7 export: " + game.GameId + " config too big to snapshot (" + watched + "): not exported");
                continue;
            }
            EngineCommandResult? result = null;
            string how;
            switch (game.GameId)
            {
                case "DCS":
                    {
                        // Only Config\Input of Saved Games\DCS: what the export writes.
                        string target = Path.Combine(copyDir, "DCS");
                        string input = Path.Combine(configPath, "Config", "Input");
                        if (Directory.Exists(input))
                            Gate.CopyFolder(input, Path.Combine(target, "Config", "Input"));
                        vm.ExportDcsPath = target;
                        how = "into a copy of Config\\Input";
                        await s.MeasureAsync("export dry-run", "DCS (" + how + ")", async () => result = await s.InvokePrivateAsync<EngineCommandResult?>("RunDcsExportAsync", [null]));
                        break;
                    }
                case "IL2Game":
                    {
                        string target = Path.Combine(copyDir, "IL-2");
                        string input = Directory.Exists(configPath) ? configPath : Path.GetDirectoryName(configPath)!;
                        Gate.CopyFolder(input, Path.Combine(target, "data", "input"));
                        vm.ExportIl2Path = target;
                        how = "into a copy of data\\input";
                        await s.MeasureAsync("export dry-run", "IL-2 (" + how + ")", async () => result = await s.InvokePrivateAsync<EngineCommandResult?>("RunIl2ExportAsync"));
                        break;
                    }
                case "StarCitizen":
                    {
                        string target = Path.Combine(copyDir, "default");
                        Gate.CopyFolder(configPath, target);
                        vm.ExportStarCitizenPath = target;
                        // The export also writes into --source (the Profiles folder): that is the copy too.
                        vm.StarCitizenSourcePath = copyDir;
                        how = "into a copy of the profile folder";
                        await s.MeasureAsync("export dry-run", "Star Citizen (" + how + ")", async () => result = await s.InvokePrivateAsync<EngineCommandResult?>("RunStarCitizenExportAsync"));
                        break;
                    }
                default:
                    {
                        RegistryGameItem? registry = vm.RegistryGames.FirstOrDefault(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
                        if (registry == null)
                        {
                            Gate.Note("I7 export: " + game.GameId + " is installed but has no export integration");
                            continue;
                        }
                        vm.SelectedRegistryGame = registry;
                        string copy = string.Empty;
                        if (!registryStorage)
                        {
                            if (File.Exists(configPath))
                            {
                                copy = Path.Combine(copyDir, Path.GetFileName(configPath));
                                File.Copy(configPath, copy, overwrite: true);
                            }
                            else if (Directory.Exists(configPath) && FolderSize(configPath) < 200L * 1024 * 1024)
                            {
                                copy = Path.Combine(copyDir, Path.GetFileName(configPath.TrimEnd('\\')));
                                Gate.CopyFolder(configPath, copy);
                            }
                        }
                        vm.RegistryGamePath = copy;
                        vm.RegistryGameDryRun = true;
                        vm.MsfsWriteUserProfiles = false;
                        how = "dry run" + (copy.Length > 0 ? " on a copy" : registryStorage ? " (registry storage)" : string.Empty);
                        await s.MeasureAsync("export dry-run", game.GameId + " (" + how + ")", async () => result = await s.InvokePrivateAsync<EngineCommandResult?>("RunRegistryGameExportAsync"));
                        vm.RegistryGameDryRun = false;
                        break;
                    }
            }
            if (result == null)
                Gate.Fail("I7", "export did not run", game.GameId, how, Gate.Short(vm.StatusText, 140));
            else if (result.ExitCode != 0)
                Gate.Fail("I7", "export (" + (how.StartsWith("dry", StringComparison.Ordinal) ? "dry run" : "into a copy") + ") fails", game.GameId, how,
                    "exit " + result.ExitCode + ": " + Gate.Short(FirstError(result), 160));
            if (!registryStorage)
            {
                List<string> changed = Changed(fingerprint, Fingerprint(watched));
                if (changed.Count > 0)
                {
                    string restored = Restore(watched, snapshot, fingerprint);
                    Gate.Fail("I7", "export touched the real game config", game.GameId, how + ": " + watched, string.Join(", ", changed.Take(5)) + "; " + restored);
                }
            }
        }
    }

    // A copy of the file or folder the export could write (folders up to 300 MB).
    private static bool Snapshot(string path, string snapshot)
    {
        Directory.CreateDirectory(snapshot);
        if (File.Exists(path))
        {
            foreach (FileInfo f in Siblings(path))
                File.Copy(f.FullName, Path.Combine(snapshot, f.Name), overwrite: true);
            return true;
        }
        if (!Directory.Exists(path))
            return true;
        if (FolderSize(path) > 300L * 1024 * 1024)
            return false;
        Gate.CopyFolder(path, snapshot);
        return true;
    }

    // Back to the snapshot: files the export added are deleted, changed ones copied back with their old time stamp.
    private static string Restore(string path, string snapshot, Dictionary<string, (long, DateTime)> before)
    {
        int deleted = 0, copied = 0;
        try
        {
            bool single = File.Exists(path);
            string folder = single ? Path.GetDirectoryName(path)! : path;
            Dictionary<string, (long, DateTime)> now = Fingerprint(path);
            foreach (string file in now.Keys.Where(k => !before.ContainsKey(k)))
            {
                File.Delete(Path.Combine(folder, file));
                deleted++;
            }
            foreach ((string file, (long, DateTime) value) in before)
            {
                string target = Path.Combine(folder, file);
                if (!now.TryGetValue(file, out (long, DateTime) current) || current != value)
                {
                    File.Copy(Path.Combine(snapshot, file), target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, value.Item2);
                    copied++;
                }
            }
            return "restored from the gate's snapshot (" + copied + " copied back, " + deleted + " deleted)";
        }
        catch (Exception ex)
        {
            return "RESTORE FAILED (" + ex.Message + "): put back by hand from " + snapshot;
        }
    }

    private static string FirstError(EngineCommandResult result)
    {
        string text = result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(text));
            if (doc.RootElement.TryGetProperty("Errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return errors[0].GetString() ?? text;
        }
        catch (JsonException)
        {
        }
        return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
    }

    private static IEnumerable<FileInfo> Siblings(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        return new DirectoryInfo(Path.GetDirectoryName(file)!).EnumerateFiles(name + "*", SearchOption.TopDirectoryOnly).Take(200);
    }

    private static long FolderSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception)
        {
            return long.MaxValue;
        }
    }

    private static Dictionary<string, (long, DateTime)> Fingerprint(string path)
    {
        Dictionary<string, (long, DateTime)> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                // The file and its siblings of the same name (backups, metadata the export may add next to it).
                foreach (FileInfo f in Siblings(path))
                    map[f.Name] = (f.Length, f.LastWriteTimeUtc);
            }
            else if (Directory.Exists(path))
            {
                foreach (FileInfo f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Take(50000))
                    map[Path.GetRelativePath(path, f.FullName)] = (f.Length, f.LastWriteTimeUtc);
            }
        }
        catch (Exception)
        {
        }
        return map;
    }

    private static List<string> Changed(Dictionary<string, (long, DateTime)> before, Dictionary<string, (long, DateTime)> after)
    {
        List<string> changed = [];
        foreach ((string file, (long, DateTime) value) in after)
        {
            if (!before.TryGetValue(file, out (long, DateTime) old))
                changed.Add("new " + file);
            else if (old != value)
                changed.Add("changed " + file);
        }
        changed.AddRange(before.Keys.Where(k => !after.ContainsKey(k)).Select(k => "deleted " + k));
        return changed;
    }
}
