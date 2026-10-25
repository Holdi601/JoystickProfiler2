using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tester report (2.3.1): searching "throttle" on the Relations page listed everything bound on the device "VPC VMAX
// Prime Throttle". The search now looks at the relation's name and its actions' titles (the default), only the name,
// or everything (the scope box next to the search, remembered with the filters); devices and games are picked with
// the filter chips. The relation called as the search comes first. The Binds page uses the same filter and ranking.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RelationSearchScopeTests
{
    private const string ThrottleDevice = "VPC VMAX Prime Throttle {3A2B}";

    private static FilterSubject Subject(string name, string device, params string[] titles) =>
        new(name, [], ["DCS|F-16C_50"], device, device.Length > 0, false,
            string.Join("\u0001", name, "dcs", device.Length > 0 ? "VPC VMAX Prime Throttle" : string.Empty, "joy_btn5"), titles);

    private static List<FilterSubject> Sample() =>
    [
        Subject("Flaps Up", ThrottleDevice, "Flaps up"),
        Subject("Speed Brake", ThrottleDevice, "Speedbrake extend/retract"),
        Subject("Throttle Cutoff Release", ThrottleDevice, "Throttle cutoff release"),
        Subject("Engine Start", "", "Throttle to idle detent"),
        Subject("Throttle", ThrottleDevice, "Throttle (analog)"),
        Subject("Gear", "", "Landing gear lever")
    ];

    private static List<string> Shown(ListFilterViewModel filter, IEnumerable<FilterSubject> subjects) =>
        subjects.Where(filter.Matches).OrderBy(filter.SearchRank).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Name).ToList();

    [Fact]
    public void Default_MatchesNameAndActionTitles_NotTheDeviceName()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        Assert.Equal(ListSearchScope.NameAndActions, filter.SearchScope);

        filter.SearchText = "throttle";

        // The exact name first, then names starting with it, then a relation found by an action's title.
        Assert.Equal(["Throttle", "Throttle Cutoff Release", "Engine Start"], Shown(filter, subjects));
        Assert.DoesNotContain("Flaps Up", Shown(filter, subjects));
    }

    [Fact]
    public void NameScope_OnlyNames_EverythingScope_AlsoDevices()
    {
        ListFilterViewModel filter = new("binds");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        filter.SearchText = "throttle";

        filter.SearchScopeIndex = 0;
        Assert.Equal(ListSearchScope.Name, filter.SearchScope);
        Assert.Equal(["Throttle", "Throttle Cutoff Release"], Shown(filter, subjects));

        filter.SearchScopeIndex = 2;
        Assert.Equal(ListSearchScope.Everything, filter.SearchScope);
        Assert.Equal(["Throttle", "Throttle Cutoff Release", "Engine Start", "Flaps Up", "Speed Brake"], Shown(filter, subjects));

        // The device filter chip still picks the device's relations, whatever the scope.
        filter.SearchScopeIndex = 1;
        filter.SearchText = string.Empty;
        filter.SelectDevices(ThrottleDevice);
        Assert.Equal(4, subjects.Count(filter.Matches));
    }

    [Fact]
    public void Ranking_ExactNameFirst_ThenStartsWith_ThenAllWords_ThenActions()
    {
        ListFilterViewModel filter = new("relations");
        filter.SearchText = "gear";
        Assert.Equal(0, filter.SearchRank(Subject("Gear", "")));
        Assert.Equal(1, filter.SearchRank(Subject("Gear Up", "")));
        Assert.Equal(2, filter.SearchRank(Subject("Landing Gear", "")));
        Assert.Equal(3, filter.SearchRank(Subject("Wheels", "", "Landing gear lever")));
        filter.SearchText = string.Empty;
        Assert.Equal(0, filter.SearchRank(Subject("Wheels", "")));
    }

    [Fact]
    public void Scope_IsRemembered_WithTheFilters()
    {
        ListFilterViewModel filter = new("relations") { SearchScope = ListSearchScope.Everything };
        string path = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "scope-" + Guid.NewGuid().ToString("N"), "list-preferences.json");
        try
        {
            ListPreferences prefs = new();
            prefs.Filters[filter.Page] = filter.ToState();
            prefs.Save(path);

            ListFilterViewModel restored = new("relations");
            restored.LoadState(ListPreferences.Load(path).Filters[filter.Page]);
            Assert.Equal(ListSearchScope.Everything, restored.SearchScope);
            Assert.Equal(2, restored.SearchScopeIndex);

            // An unknown or missing value is the default.
            restored.LoadState(new ListFilterState { SearchScope = "nonsense" });
            Assert.Equal(ListSearchScope.NameAndActions, restored.SearchScope);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Opt-in, the user's own profile through the real engine: JOYPRO_SEARCH_PROFILE = a profile (a copy of it is
    // opened), JOYPRO_SEARCH_DATA = a data folder (JOYPRO_DATA_DIR; default a temp folder), JOYPRO_SEARCH_WORD (default
    // "throttle"). On the Relations and the Binds page no relation may be listed only because of its device's name.
    [Fact]
    public async Task RealProfile_ThrottleSearch_IgnoresDeviceNames_OnBothPages()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_PROFILE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        string word = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_WORD") is { Length: > 0 } w ? w : "throttle";
        string data = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_DATA") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        string profile = Path.Combine(data, "search-test.pr0file");
        File.Copy(source, profile, overwrite: true);
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        StringBuilder report = new();
        try
        {
            MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = FindEngine(),
                ProfilePath = profile
            };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            Assert.True(vm.Relations.Count > 50, "the profile did not load: " + vm.StatusText);
            Assert.Contains(vm.Relations, r => r.ActionTitles.Count > 0);
            int withDeviceWord = vm.Relations.Count(r => r.IsBound && r.BindDeviceFull.Contains(word, StringComparison.OrdinalIgnoreCase));
            report.AppendLine("relations " + vm.Relations.Count + ", bound on a device with '" + word + "': " + withDeviceWord);

            bool Fits(RelationListItem r) => r.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                                             r.ActionTitles.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));

            vm.RelationSearchText = word;
            List<RelationListItem> shown = vm.Relations.ToList();
            report.AppendLine("Relations page, '" + word + "': " + shown.Count + " shown: " + string.Join(", ", shown.Take(30).Select(r => r.Name)));
            Assert.NotEmpty(shown);
            Assert.All(shown, r => Assert.True(Fits(r), r.Name + " is listed only because of its device " + r.BindDeviceFull));
            // The ones called so come first.
            int firstOther = shown.FindIndex(r => !r.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (firstOther >= 0)
                Assert.DoesNotContain(shown.Skip(firstOther), r => r.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase));

            vm.BindSearchText = word;
            List<BindListItem> binds = vm.Binds.ToList();
            report.AppendLine("Binds page, '" + word + "': " + binds.Count + " shown");
            Assert.Equal(shown.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal), binds.Select(b => b.RelationName).Distinct().OrderBy(n => n, StringComparer.Ordinal));

            // JOYPRO_SEARCH_OUT=<folder>: both pages with the search and its scope box, rendered (never shown).
            if (Environment.GetEnvironmentVariable("JOYPRO_SEARCH_OUT") is { Length: > 0 } outDir)
            {
                Directory.CreateDirectory(outDir);
                AvaloniaRenderThread.Run(() =>
                {
                    foreach (UiStage stage in new[] { UiStage.Relations, UiStage.Binds })
                    {
                        vm.CurrentStage = stage;
                        foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
                        {
                            using HiddenWindow host = HiddenWindow.Create(new JoyPro.Gui.Views.MainWindow { DataContext = vm }, new Avalonia.Size(width, height));
                            host.Settle();
                            host.Settle();
                            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = host.Render();
                            bitmap.Save(Path.Combine(outDir, stage.ToString().ToLowerInvariant() + "-search-" + word + "-" + width + ".png"));
                        }
                    }
                });
            }

            // Everything: the device's relations come back.
            vm.RelationFilter.SearchScope = ListSearchScope.Everything;
            report.AppendLine("Everything: " + vm.Relations.Count + " shown");
            Assert.True(vm.Relations.Count >= shown.Count);
            if (withDeviceWord > 0)
                Assert.Contains(vm.Relations, r => !Fits(r));
            vm.RelationFilter.SearchScope = ListSearchScope.NameAndActions;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
            File.WriteAllText(Path.Combine(data, "search-report.txt"), report.ToString());
        }
    }

    private static string FindEngine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir == null ? "engine.exe" : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
    }
}
