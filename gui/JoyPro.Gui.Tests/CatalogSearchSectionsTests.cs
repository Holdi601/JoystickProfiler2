using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// A tester on 2.6.3, relation editor > Catalog, Falcon BMS, filter "RMFD OSB 20": seven rows, all open and equal, by
// category, the MFD power switch "Recommended" and RMFD neither first nor marked, while the engine's own search said
// name / Recommended for RMFD and related for the rest. The filter now takes the search's sections like Add actions:
// related rows are no hits, Same meaning decides about function rows, Recommended follows the words while a filter text
// is on. All with the real engine and the real lists (docs/relation-builder.md "Browse the catalog").
[SupportedOSPlatform("windows")]
public sealed class CatalogSearchSectionsTests
{
    private static async Task<(MainWindowViewModel Vm, string Folder)> OpenAsync(string engine, string game, string plane = "")
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "catalog-sections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
        vm.CurrentStage = UiStage.Relations;
        await vm.OpenCatalogCommand.ExecuteAsync(null);
        await vm.SelectCatalogAsync(game, plane);
        await SameMeaningRealEngineTests.WaitLongAsync(() => vm.CatalogRowItems.Count > 0 && !vm.IsCatalogLoading);
        return (vm, folder);
    }

    private static void Done(MainWindowViewModel vm, string folder)
    {
        vm.StopGameConfigWatcher();
        EngineServerPool.Recycle("catalog sections test done");
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // The text typed and the engine's answer applied.
    private static async Task FilterAsync(MainWindowViewModel vm, string text)
    {
        int delay = MainWindowViewModel.CatalogMeaningDelayMs;
        MainWindowViewModel.CatalogMeaningDelayMs = 0;
        try
        {
            vm.CatalogFilterText = text;
            await SameMeaningRealEngineTests.WaitLongAsync(() => vm.CatalogAnsweredText == text.Trim());
        }
        finally
        {
            MainWindowViewModel.CatalogMeaningDelayMs = delay;
        }
    }

    // What the list shows, in its order (a row in two categories twice).
    private static List<BuilderSearchResultItem> Shown(MainWindowViewModel vm) => vm.CatalogEntries.OfType<CatalogEntryRow>().Select(e => e.Item.Row).ToList();

    private static string[] Titles(MainWindowViewModel vm) => Shown(vm).Select(r => r.Title).ToArray();

    private static string[] RecommendedTitles(MainWindowViewModel vm) => Shown(vm).Where(r => r.ShownRecommended).Select(r => r.Title).ToArray();

    [Fact]
    public async Task FalconBms_RmfdOsb20_ListsOnlyThatButton_AndRecommendsIt()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "FalconBMS");
        try
        {
            // Without a filter text the browse's chips: the MFD power switch is marked for its own function.
            BuilderSearchResultItem off = vm.CatalogRows().Single(r => r.Title == "AVIONICS: MFD Switch - OFF");
            Assert.True(off.IsRecommended);
            Assert.True(off.ShownRecommended);

            await FilterAsync(vm, "RMFD OSB 20");
            Assert.Equal(["RMFD: OSB-20 Button - Push"], Titles(vm));
            Assert.Equal(["RMFD: OSB-20 Button - Push"], RecommendedTitles(vm));
            Assert.False(off.ShownRecommended);
            Assert.Equal("4.10 Right MFD", vm.CatalogEntries.OfType<CatalogCategoryItem>().Single().Name);

            // The words as one: the same row, also before the engine has answered.
            MainWindowViewModel.CatalogMeaningDelayMs = 60000;
            vm.CatalogFilterText = "RMFD OSB20";
            Assert.Equal(["RMFD: OSB-20 Button - Push"], Titles(vm));
            Assert.Empty(RecommendedTitles(vm));
            MainWindowViewModel.CatalogMeaningDelayMs = 300;
            await FilterAsync(vm, "RMFD  OSB-20 ");
            Assert.Equal(["RMFD: OSB-20 Button - Push"], Titles(vm));

            // Filter gone: every category and the browse's chips again.
            vm.CatalogFilterText = string.Empty;
            Assert.True(off.ShownRecommended);
            Assert.Equal(vm.CatalogRows().Count(r => r.IsRecommended), vm.CatalogRows().Count(r => r.ShownRecommended));
            Assert.All(vm.CatalogCategories, c => Assert.True(c.IsVisible));
        }
        finally
        {
            MainWindowViewModel.CatalogMeaningDelayMs = 300;
            Done(vm, folder);
        }
    }

    [Fact]
    public async Task FalconBms_Osb20_ListsTheFourDisplays_NotTheMfdSwitch()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "FalconBMS");
        try
        {
            string[] four = ["FMFD: OSB-20 Button - Push", "LMFD: OSB-20 Button - Push", "RMFD: OSB-20 Button - Push", "TMFD: OSB-20 Button - Push"];
            await FilterAsync(vm, "OSB 20");
            Assert.Equal(four, Titles(vm).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            Assert.Equal(four, RecommendedTitles(vm).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            await FilterAsync(vm, "OSB20");
            Assert.Equal(four, Titles(vm).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Done(vm, folder);
        }
    }

    // The word the title does not have: found by what it does, and that is what the switch is for.
    [Fact]
    public async Task FalconBms_Thrust_FindsTheThrottles_AndSameMeaningDecides()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "FalconBMS");
        try
        {
            await FilterAsync(vm, "thrust");
            Assert.Contains("Throttle", Titles(vm));
            Assert.Contains("FCTRL: Throttle Forward", Titles(vm));
            Assert.Contains("FCTRL: ENGINE - Togg. Thrust Reverser", Titles(vm));
            Assert.Equal(string.Empty, vm.CatalogFilterNote);

            vm.BuilderShowSameMeaning = false;
            Assert.Equal(["FCTRL: ENGINE - Togg. Thrust Reverser"], Titles(vm));
            Assert.Contains("7", vm.CatalogFilterNote);
            Assert.EndsWith(vm.CatalogFilterNote, vm.CatalogStatusLine, StringComparison.Ordinal);

            vm.BuilderShowSameMeaning = true;
            Assert.Contains("Throttle", Titles(vm));
            Assert.Equal(string.Empty, vm.CatalogFilterNote);
        }
        finally
        {
            vm.BuilderShowSameMeaning = true;
            Done(vm, folder);
        }
    }

    [Fact]
    public async Task DcsF16_OsbWords_SideNamed_AndNoHitByTheId()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "DCS", "F-16C_50");
        try
        {
            Assert.Equal("F-16C_50", vm.CatalogPlane?.Plane);
            await FilterAsync(vm, "OSB 20");
            Assert.Equal(["Left MFD OSB 20", "Right MFD OSB 20"], Titles(vm).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray());
            Assert.Equal(["Left MFD OSB 20", "Right MFD OSB 20"], RecommendedTitles(vm).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray());

            // RMFD is not in the title: DCS calls it Right MFD, the search knows (same function), Left is related.
            await FilterAsync(vm, "RMFD OSB 20");
            Assert.Equal(["Right MFD OSB 20"], Titles(vm).Distinct().ToArray());
            Assert.Equal(["Right MFD OSB 20"], RecommendedTitles(vm).Distinct().ToArray());
            vm.BuilderShowSameMeaning = false;
            Assert.Empty(Titles(vm));
            Assert.Contains("1", vm.CatalogFilterNote);
            vm.BuilderShowSameMeaning = true;

            // Every id of the F-16's OSBs has a 3 (d3001...): the id is no hit for one of the words.
            await FilterAsync(vm, "OSB 3");
            Assert.Contains("Left MFD OSB 3", Titles(vm));
            Assert.DoesNotContain("Left MFD OSB 1", Titles(vm));

            // The id or key typed as such still finds it.
            await FilterAsync(vm, "d3020pnilu3020cd25");
            Assert.Equal(["Right MFD OSB 20"], Titles(vm).Distinct().ToArray());

            await FilterAsync(vm, "thrust");
            Assert.Contains(Titles(vm), t => t.StartsWith("Throttle Smoothly", StringComparison.Ordinal));
            Assert.Equal("Thrust", Titles(vm)[0]);
        }
        finally
        {
            vm.BuilderShowSameMeaning = true;
            Done(vm, folder);
        }
    }

    // No hit at all: the related rows rather than an empty list (gate I11: "stop my ship completely right now" in X4, whose
    // Stop Engines the search files under related because of the word "right"), and the status line says so.
    [Fact]
    public async Task NoHitAtAll_ListsTheRelatedRows_AndSaysSo()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "X4");
        try
        {
            // "left" is a side the function's row does not have: Related, and nothing else.
            await FilterAsync(vm, "stop my ship completely on the left");
            Assert.Equal(["Stop Engines"], Titles(vm).Distinct().ToArray());
            Assert.Contains("related", vm.CatalogFilterNote, StringComparison.Ordinal);
            Assert.Empty(RecommendedTitles(vm));

            // Same meaning off: nothing listed, and the line says what the switch brings back.
            vm.BuilderShowSameMeaning = false;
            Assert.Empty(Titles(vm));
            Assert.Contains("hidden", vm.CatalogFilterNote, StringComparison.Ordinal);
            vm.BuilderShowSameMeaning = true;

            // A hit: no related rows, no note.
            await FilterAsync(vm, "space brake");
            Assert.Equal(["Stop Engines"], Titles(vm).Distinct().ToArray());
            Assert.Equal(string.Empty, vm.CatalogFilterNote);
        }
        finally
        {
            vm.BuilderShowSameMeaning = true;
            Done(vm, folder);
        }
    }

    // The answer belongs to the aircraft it was asked for: another aircraft asks again.
    [Fact]
    public async Task AnotherAircraft_AsksTheSearchAgain()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "DCS", "F-16C_50");
        try
        {
            await FilterAsync(vm, "OSB 20");
            Assert.Contains("Right MFD OSB 20", Titles(vm));
            int delay = MainWindowViewModel.CatalogMeaningDelayMs;
            MainWindowViewModel.CatalogMeaningDelayMs = 0;
            try
            {
                await vm.SelectCatalogAsync("DCS", "FA-18C_hornet");
                Assert.Equal("FA-18C_hornet", vm.CatalogPlane?.Plane);
                await SameMeaningRealEngineTests.WaitLongAsync(() => vm.CatalogAnsweredText == "OSB 20");
            }
            finally
            {
                MainWindowViewModel.CatalogMeaningDelayMs = delay;
            }
            Assert.DoesNotContain("Right MFD OSB 20", Titles(vm));
            Assert.All(Shown(vm), r => Assert.Contains("FA-18C_hornet", r.Planes));
        }
        finally
        {
            Done(vm, folder);
        }
    }

    // Without a sort level the search's best rows and their categories come first; a level the user set stays.
    [Fact]
    public async Task BestRowsFirst_UnlessASortLevelIsSet()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "FalconBMS");
        try
        {
            await FilterAsync(vm, "thrust");
            Assert.False(vm.CatalogSort.HasLevels);
            Assert.Equal("Axes", vm.CatalogEntries.OfType<CatalogCategoryItem>().First().Name);
            Assert.Equal("Throttle", Titles(vm)[0]);

            vm.CatalogSort.AddLevel();
            Assert.True(vm.CatalogSort.HasLevels);
            Assert.NotEqual("Axes", vm.CatalogEntries.OfType<CatalogCategoryItem>().First().Name);
            Assert.Contains("Throttle", Titles(vm));
        }
        finally
        {
            vm.CatalogSort.ResetCommand.Execute(null);
            Done(vm, folder);
        }
    }

    [Theory]
    [InlineData("osb20", "RMFD: OSB-20 Button - Push", true)]
    [InlineData("OSB-20", "Left MFD OSB 20", true)]
    [InlineData("osb20", "Left MFD OSB 2", false)]
    [InlineData("a10", "A-10C: Gear", true)]
    [InlineData("a1", "Data 1", false)]
    public void JoinedWord_FindsTheTitleWithItsSeparator(string word, string title, bool hit)
    {
        Assert.Equal(hit, MainWindowViewModel.CatalogJoinedWord(word.ToLowerInvariant())!.IsMatch(title));
    }

    [Fact]
    public void JoinedWord_OnePart_NeedsNoRegex()
    {
        Assert.Null(MainWindowViewModel.CatalogJoinedWord("gear"));
        Assert.Null(MainWindowViewModel.CatalogJoinedWord("20"));
    }

    // The Catalog tab, dark and light: opt-in, set JOYPRO_264B_RENDER_OUT=<folder> (needs the whole application).
    [Fact]
    public void Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_264B_RENDER_OUT");
        string? engine = SameMeaningRealEngineTests.Engine();
        if (string.IsNullOrWhiteSpace(outDir) || engine == null)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder texts = new();
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, string folder) = AvaloniaRenderThread.Wait(OpenAsync(engine, "FalconBMS"));
            try
            {
                void Shot(string name)
                {
                    AvaloniaRenderThread.Pump();
                    texts.AppendLine("--- " + name + " | status: " + vm.CatalogStatusLine);
                    foreach (object entry in vm.CatalogEntries)
                    {
                        if (entry is CatalogCategoryItem category)
                            texts.AppendLine("[" + category.Name + "]");
                        else if (entry is CatalogEntryRow row)
                            texts.AppendLine("  " + row.Item.Row.Title + (row.Item.Row.ShownRecommended ? " | " + row.Item.Row.ShownRecommendedLabel : string.Empty));
                    }
                    foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                    {
                        Application.Current!.RequestedThemeVariant = variant;
                        UiDesignRenderTests.RenderWindow(vm, 1600, 1000, Path.Combine(outDir, name + "-" + theme + ".png"));
                    }
                }

                MainWindowViewModel.CatalogMeaningDelayMs = 0;
                void Filter(string text)
                {
                    vm.CatalogFilterText = text;
                    AvaloniaRenderThread.Wait(SameMeaningRealEngineTests.WaitLongAsync(() => vm.CatalogAnsweredText == text));
                }

                Filter("RMFD OSB 20");
                Shot("catalog-bms-rmfd-osb-20");
                Filter("thrust");
                Shot("catalog-bms-thrust");
                vm.BuilderShowSameMeaning = false;
                Shot("catalog-bms-thrust-same-meaning-off");
                vm.BuilderShowSameMeaning = true;
                AvaloniaRenderThread.Wait(vm.SelectCatalogAsync("DCS", "F-16C_50"));
                Filter("RMFD OSB 20");
                Shot("catalog-f16-rmfd-osb-20");
                Filter("thrust");
                Shot("catalog-f16-thrust");
                AvaloniaRenderThread.Wait(vm.SelectCatalogAsync("X4", string.Empty));
                Filter("stop my ship completely right now");
                Shot("catalog-x4-related-only");
            }
            finally
            {
                MainWindowViewModel.CatalogMeaningDelayMs = 300;
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                Done(vm, folder);
                File.WriteAllText(Path.Combine(outDir, "texts.txt"), texts.ToString());
            }
        });
    }
}
