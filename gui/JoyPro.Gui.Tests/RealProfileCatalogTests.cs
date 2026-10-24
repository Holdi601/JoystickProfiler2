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

// Opt-in: the catalog of every game with a real profile and the real engine (the report "MSFS 2024: 1787 actions, no
// rows"). JOYPRO_REAL_PROFILE = a COPY of a profile (it is copied again, the copy is opened), JOYPRO_REAL_DATA = a JoyPro
// data folder (JOYPRO_DATA_DIR, e.g. a copy of a PC's GameData under <folder>\GameData), JOYPRO_REAL_GAMES = comma list
// (default MSFS2024). Writes <data>\catalog-report.txt.
public sealed class RealProfileCatalogTests
{
    [Fact]
    public async Task CatalogListsTheRowsOfEveryGame()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_REAL_PROFILE");
        string? data = Environment.GetEnvironmentVariable("JOYPRO_REAL_DATA");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(data) || !File.Exists(source))
            return;
        string[] games = (Environment.GetEnvironmentVariable("JOYPRO_REAL_GAMES") is { Length: > 0 } g ? g : "MSFS2024")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string profile = Path.Combine(data, "catalog-test.pr0file");
        File.Copy(source, profile, overwrite: true);
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        StringBuilder report = new();
        List<string> failures = [];
        try
        {
            MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = FindEngine(),
            };
            vm.ProfilePath = profile;
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            report.AppendLine("relations: " + vm.ProfileRelationCount);
            string openName = Environment.GetEnvironmentVariable("JOYPRO_REAL_OPEN") ?? string.Empty;
            RelationListItem? open = vm.Relations.FirstOrDefault(r => r.Name == openName) ?? vm.Relations.FirstOrDefault(r => r.IsBound) ?? vm.Relations.FirstOrDefault();
            if (open != null)
            {
                vm.SelectedRelation = open;
                for (int i = 0; i < 3000 && (vm.RelationNodes.Count == 0 || vm.IsBuilderSuggestLoading); i++)
                    await Task.Delay(10);
                report.AppendLine("open relation: " + open.Name + " (" + vm.BuilderEffectiveKind + ")");
            }
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            foreach (string game in games)
            {
                // As the game picker does it (the selected item changes, the list loads on its own).
                vm.CatalogGame = vm.CatalogGames.FirstOrDefault(o => string.Equals(o.Game, game, StringComparison.OrdinalIgnoreCase));
                await Task.Delay(50);
                for (int i = 0; i < 6000 && vm.IsCatalogLoading; i++)
                    await Task.Delay(10);
                vm.ExpandAllCatalogCommand.Execute(null);
                int rows = vm.CatalogRowItems.Count;
                int visible = vm.CatalogCategories.Where(c => c.IsVisible).Sum(c => c.VisibleRows.Count);
                report.AppendLine(game + ": option '" + vm.CatalogGame?.Label + "', status '" + vm.CatalogStatus + "', rows " + rows + ", visible " + visible +
                                  ", categories " + string.Join(" | ", vm.CatalogCategories.Select(c => c.Name + " " + c.Rows.Count + "/" + c.VisibleRows.Count)));
                if (rows == 0 || visible == 0)
                    failures.Add(game + ": no rows (" + vm.CatalogStatus + ")");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
            File.WriteAllText(Path.Combine(data, "catalog-report.txt"), report.ToString());
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures) + Environment.NewLine + report);
    }

    private static string FindEngine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir == null ? "engine.exe" : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
    }
}
