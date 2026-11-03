using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Add actions list after the 2.6.1 tester reports (docs/relation-builder.md "Same function and Related"): an opened
// relation starts with an empty box; "Same meaning" is split into "Same function" (open) and "Related" (folded, with its
// count); the "Same meaning" switch (remembered, also in Settings > Relations) leaves only the name matches; every row
// says in words why it was found; "brakes" name-matches "Wheel Brake"; the F-4E's sim-pit switch axes say what they are.
// Real engine and the development game lists: "brakes" with Axes in DCS, the relation "Wheel Brakes (analog)" holding the
// Spitfire's wheel brakes axis, the aircraft of the tester's list (F-4E pilot and WSO, Spitfire).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class SameMeaningRealEngineTests
{
    internal const string SpitfireWheelBrakes = "a3004cd1";
    internal static readonly string[] TesterPlanes = ["F-4E-45MC", "F-4E-45MC_WSO", "SpitfireLFMkIX"];

    internal static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")) ? engine : null;
    }

    internal static async Task WaitLongAsync(Func<bool> condition, int seconds = 90)
    {
        for (int i = 0; i < seconds * 50 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    // The profile with "Wheel Brakes (analog)" and the view model on it, the relation open in Add actions.
    internal static async Task<(MainWindowViewModel Vm, string Folder)> OpenWheelBrakesAsync(string engine)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "same-meaning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "brakes.pr0file");
        EngineCommandResult made = await client.RunAsync(engine, ["--profile", profile, "--status", "false", "relations", "add", "Wheel Brakes (analog)", "--axis", "--node", "DCS:" + SpitfireWheelBrakes + ":SpitfireLFMkIX"]);
        Assert.True(made.ExitCode == 0, made.StandardError);
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        vm.BuilderSearchText = "gear";
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Wheel Brakes (analog)");
        await WaitLongAsync(() => vm.BuilderRelationName == "Wheel Brakes (analog)" && !vm.IsBusy);
        await WaitLongAsync(() => vm.RelationNodes.Count == 1 && !vm.IsBuilderSuggestLoading);
        vm.ShowBuilderFindCommand.Execute(null);
        return (vm, folder);
    }

    // The query typed, then the DCS chip ticked (every DCS row, as in the tester's "22 actions in 1 game"; without a
    // ticked game the best 12 rows of each game are listed).
    internal static async Task SearchAsync(MainWindowViewModel vm, string query)
    {
        vm.BuilderSearchText = query;
        await WaitLongAsync(() => !vm.IsBuilderSearching && vm.BuilderLastSearchedQueryForTests == query && vm.BuilderSearchStatus.Length > 0);
        BuilderGameChip? dcs = vm.BuilderSearchGames.FirstOrDefault(c => c.GameId == "DCS");
        if (dcs != null && !dcs.IsSelected)
        {
            dcs.IsSelected = true;
            await Task.Delay(50);
            await WaitLongAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchStatus.Length > 0);
        }
    }

    [Fact]
    public async Task Brakes_InDcsAxes_NameMatchesFirst_CategoryHitsRelatedAndFolded_EveryRowSaysWhy()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenWheelBrakesAsync(engine);
        try
        {
            // Opened: the box is empty (the last relation's "gear" is gone), the start hint points to the suggestions.
            Assert.Equal(string.Empty, vm.BuilderSearchText);
            Assert.True(vm.ShowBuilderSearchStart);
            Assert.Contains("'Wheel Brakes (analog)'", vm.BuilderSearchStartText);
            Assert.Equal("axis", vm.BuilderEffectiveKind);
            foreach (string plane in TesterPlanes)
                await vm.AddBuilderSearchPlaneCommand.ExecuteAsync(plane);
            Assert.True(vm.BuilderShowSameMeaning);

            await SearchAsync(vm, "brakes");
            Assert.Contains("understood as Wheel brakes", vm.BuilderSearchStatus);
            Assert.Contains("+ 1 already in this relation", vm.BuilderSearchStatus);
            List<BuilderSearchResultItem> all = vm.BuilderSearchResults.Where(r => !r.IsInOpenRelation).ToList();
            // "brakes" is "brake": the F-4E's wheel brake axes (pilot and WSO, both and left / right) have every word.
            List<string> wheel = ["Wheel Brake", "Wheel Brake Left", "Wheel Brake Right", "Wheel Brake (Both)", "Wheel Brake (Left)", "Wheel Brake (Right)"];
            foreach (string title in wheel)
            {
                BuilderSearchResultItem row = Assert.Single(all, r => r.Title == title);
                Assert.True(row.IsNameMatch, title);
                Assert.Equal("name", row.SectionKind);
                Assert.Contains(row.MatchLabel, new[] { "same function", "main control" });
                Assert.StartsWith("The title has every word you typed; its function is 'Wheel brakes", row.ReasonText);
                Assert.NotEmpty(HighlightTextBlock.FindHighlightSegments(row.DisplayTitle, "brakes"));
            }
            // The category hits ("Gears, Brakes, Hook": landing gear, hook, anti-skid) are related, not "same meaning".
            List<BuilderSearchResultItem> related = all.Where(r => r.IsRelated).ToList();
            Assert.Equal(7, related.Count);
            Assert.All(related, r =>
            {
                Assert.Equal("related", r.MatchLabel);
                Assert.Equal("category", r.RelatedBy);
                Assert.Contains("its category '", r.ReasonText);
                Assert.Contains("has the word 'brakes'", r.ReasonText);
                Assert.DoesNotContain("Title match", r.WhyText);
                Assert.Equal("axis (sim-pit switch)", r.KindLabel);
                Assert.Contains("home cockpit", r.KindTip);
            });
            Assert.Contains(related, r => r.Title == "[SimPit] Landing Gear (Switch)");
            Assert.Contains(related, r => r.Title == "[SimPit] Arresting Hook Control Handle (Switch)");
            Assert.DoesNotContain(all, r => r.IsSameFunction);

            // Sections: the name matches, then "Related (7)" folded: its rows are not in the list until opened.
            List<BuilderSearchSectionItem> sections = vm.BuilderSearchRows.OfType<BuilderSearchSectionItem>().ToList();
            Assert.Equal(["name", "related"], sections.Select(s => s.Kind).ToArray());
            Assert.Equal("Name has every word (" + all.Count(r => r.IsNameMatch) + ")", sections[0].Label);
            Assert.Equal("Related (7)", sections[1].Label);
            Assert.False(sections[1].IsExpanded);
            Assert.DoesNotContain(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>(), r => r.IsRelated);
            vm.ToggleBuilderRelatedCommand.Execute(null);
            Assert.True(vm.BuilderSearchRows.OfType<BuilderSearchSectionItem>().Single(s => s.IsRelated).IsExpanded);
            List<object> rows = vm.BuilderSearchRows.ToList();
            int relatedAt = rows.IndexOf(rows.OfType<BuilderSearchSectionItem>().Single(s => s.IsRelated));
            Assert.All(rows.Take(relatedAt).OfType<BuilderSearchResultItem>(), r => Assert.True(r.IsNameMatch));
            Assert.Equal(7, rows.Skip(relatedAt).OfType<BuilderSearchResultItem>().Count(r => r.IsRelated));
            // The relation's own action stays greyed at the bottom.
            Assert.True(Assert.IsType<BuilderSearchResultItem>(rows[^1]).IsInOpenRelation);

            // Same meaning off: only the name matches; the status says how many it hides.
            vm.BuilderShowSameMeaning = false;
            Assert.Equal(["name"], vm.BuilderSearchRows.OfType<BuilderSearchSectionItem>().Select(s => s.Kind).ToArray());
            Assert.All(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>(), r => Assert.True(r.IsNameMatch));
            Assert.Contains("7 with the same meaning hidden", vm.BuilderSearchStatus);
            Assert.False(vm.ShowBuilderNoNameMatches);
            vm.BuilderShowSameMeaning = true;
            Assert.DoesNotContain("hidden", vm.BuilderSearchStatus);
        }
        finally
        {
            vm.StopGameConfigWatcher();
            EngineServerPool.Recycle("same meaning test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // "thrust": the throttles are "Same function" (a synonym), each says so; Same meaning off leaves no name match, which
    // is said plainly with one click to switch it on again.
    [Fact]
    public async Task Thrust_SameFunctionBySynonym_SwitchOffSaysThereIsNoNameMatch()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenWheelBrakesAsync(engine);
        try
        {
            foreach (string plane in TesterPlanes)
                await vm.AddBuilderSearchPlaneCommand.ExecuteAsync(plane);
            await SearchAsync(vm, "thrust");
            List<BuilderSearchResultItem> all = vm.BuilderSearchResults.Where(r => !r.IsInOpenRelation).ToList();
            Assert.NotEmpty(all);
            Assert.All(all, r => Assert.True(r.IsSameFunction, r.Title));
            BuilderSearchResultItem left = Assert.Single(all, r => r.Title == "Throttle Left");
            Assert.Equal("synonym", left.MatchLabel);
            Assert.Equal("'thrust' is a synonym of 'Throttle'; this action's function is 'Throttle (left) axis'.", left.ReasonText);
            BuilderSearchSectionItem section = Assert.Single(vm.BuilderSearchRows.OfType<BuilderSearchSectionItem>());
            Assert.Equal("Same function (" + all.Count + ")", section.Label);

            vm.BuilderShowSameMeaning = false;
            Assert.True(vm.ShowBuilderNoNameMatches);
            Assert.Empty(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>());
            Assert.Equal("No action's name has every word of \"thrust\". The actions found by what they do are hidden because Same meaning is off (" + all.Count + ").", vm.BuilderNoNameMatchesText);
            vm.ShowBuilderSameMeaningCommand.Execute(null);
            Assert.True(vm.BuilderShowSameMeaning);
            Assert.False(vm.ShowBuilderNoNameMatches);
            Assert.Equal(all.Count, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count(r => !r.IsInOpenRelation));
        }
        finally
        {
            vm.StopGameConfigWatcher();
            EngineServerPool.Recycle("same meaning test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The Add actions tab for "brakes" in DCS, light and dark: the sections (Related folded, then open), the switch, the
    // empty start and a row's tooltip drawn over it (JOYPRO_SAMEMEANING_RENDER_OUT=<folder>; nothing without it).
    [Fact]
    public void RenderBrakes()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_SAMEMEANING_RENDER_OUT");
        string? engine = Engine();
        if (string.IsNullOrWhiteSpace(outDir) || engine == null)
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, string folder) = AvaloniaRenderThread.Wait(OpenWheelBrakesAsync(engine));
            try
            {
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    UiDesignRenderTests.RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "start-empty-" + theme + ".png"));
                }
                foreach (string plane in TesterPlanes)
                    AvaloniaRenderThread.Wait(vm.AddBuilderSearchPlaneCommand.ExecuteAsync(plane));
                AvaloniaRenderThread.Wait(SearchAsync(vm, "brakes"));
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    UiDesignRenderTests.RenderWindow(vm, 1600, 2000, Path.Combine(outDir, "brakes-related-folded-" + theme + ".png"));
                }
                vm.ToggleBuilderRelatedCommand.Execute(null);
                BuilderSearchResultItem gear = vm.BuilderSearchResults.First(r => r.Title == "[SimPit] Landing Gear (Switch)");
                BuilderSearchResultItem wheel = vm.BuilderSearchResults.First(r => r.Title == "Wheel Brake (Left)");
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    RenderWithTooltip(vm, 1600, 2000, gear, Path.Combine(outDir, "brakes-related-open-tooltip-" + theme + ".png"));
                    RenderWithTooltip(vm, 1600, 2000, wheel, Path.Combine(outDir, "brakes-name-tooltip-" + theme + ".png"));
                }
                vm.BuilderShowSameMeaning = false;
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    UiDesignRenderTests.RenderWindow(vm, 1600, 2000, Path.Combine(outDir, "brakes-same-meaning-off-" + theme + ".png"));
                }
                AvaloniaRenderThread.Wait(SearchAsync(vm, "thrust"));
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    UiDesignRenderTests.RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "thrust-off-no-name-match-" + theme + ".png"));
                }
                vm.BuilderShowSameMeaning = true;
                BuilderSearchResultItem throttle = vm.BuilderSearchResults.First(r => r.Title == "Throttle Left");
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    RenderWithTooltip(vm, 1600, 2000, throttle, Path.Combine(outDir, "thrust-same-function-tooltip-" + theme + ".png"));
                }
                vm.CurrentStage = UiStage.Settings;
                vm.SettingsSearchText = "same meaning";
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in Themes)
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    UiDesignRenderTests.RenderWindow(vm, 1600, 1000, Path.Combine(outDir, "settings-relations-" + theme + ".png"));
                }
                vm.SettingsSearchText = string.Empty;
                vm.CurrentStage = UiStage.Relations;
            }
            finally
            {
                vm.StopGameConfigWatcher();
                EngineServerPool.Recycle("same meaning render done");
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }

    private static readonly (string, Avalonia.Styling.ThemeVariant)[] Themes = [("dark", Avalonia.Styling.ThemeVariant.Dark), ("light", Avalonia.Styling.ThemeVariant.Light)];

    // The window as RenderWindow draws it, with the row's chip tooltip (its ReasonText) drawn below the chip the way the
    // tooltip shows it on hover (a tooltip is a popup of its own, not part of the window's picture offscreen).
    private static void RenderWithTooltip(MainWindowViewModel vm, int width, int height, BuilderSearchResultItem row, string file)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        RenderPanels.UnvirtualizeItemsControls(main);
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        Border? chip = page.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, row) && b.Classes.Contains("chip") && b.IsEffectivelyVisible && b.Bounds.Width > 0);
        Assert.NotNull(chip);
        Point at = chip!.TranslatePoint(new Point(0, chip.Bounds.Height + 4), page) ?? new Point(0, 0);
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? found) && found is IBrush own
            ? own
            : new SolidColorBrush(main.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        bool light = main.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
        Border tip = new()
        {
            Background = new SolidColorBrush(light ? Color.Parse("#FFFFFF") : Color.Parse("#2B2B2B")),
            BorderBrush = new SolidColorBrush(light ? Color.Parse("#C8C8C8") : Color.Parse("#505050")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5),
            MaxWidth = 420,
            Child = new TextBlock { Text = row.ReasonText, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(light ? Color.Parse("#1B1B1B") : Color.Parse("#F0F0F0")), FontSize = 12 }
        };
        tip.Measure(new Size(420, 400));
        tip.Arrange(new Rect(tip.DesiredSize));
        double x = Math.Min(at.X, width - tip.DesiredSize.Width - 8);
        double y = Math.Min(at.Y, height - tip.DesiredSize.Height - 8);
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        using RenderTargetBitmap tipImage = new(new PixelSize((int)Math.Ceiling(tip.DesiredSize.Width), (int)Math.Ceiling(tip.DesiredSize.Height)));
        tipImage.Render(tip);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
            Rect tipRect = new(0, 0, tipImage.PixelSize.Width, tipImage.PixelSize.Height);
            context.DrawImage(tipImage, tipRect, new Rect(x, y, tipRect.Width, tipRect.Height));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }
}

// The switch survives a restart (project rule: every toggle does) and is the same setting in Settings > Relations; the
// reasons in words without an engine; the highlight marks "Brake" for "brakes".
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class SameMeaningSettingTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public SameMeaningSettingTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose() => File.Delete(uiSettings);

    private static MainWindowViewModel Vm(bool persist) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void SameMeaning_StartsOn_SurvivesARestart_AndIsInSettings()
    {
        MainWindowViewModel first = Vm(persist: true);
        Assert.True(first.BuilderShowSameMeaning);
        first.BuilderShowSameMeaning = false;
        Assert.Contains("\"addActionsSameMeaning\": false", File.ReadAllText(uiSettings));
        MainWindowViewModel second = Vm(persist: true);
        Assert.False(second.BuilderShowSameMeaning);
        second.BuilderShowSameMeaning = true;
        Assert.True(Vm(persist: true).BuilderShowSameMeaning);
        // Settings > Relations, found by its words.
        SettingsSectionItem section = Assert.Single(second.SettingsSections, s => s.Id == "relations");
        Assert.Equal("Relations", section.Title);
        Assert.True(section.Matches(["same", "meaning"]));
    }

    [Fact]
    public void ReasonTexts_SayInWordsWhyARowWasFound()
    {
        BuilderSearchResultItem Row(string title, string match, bool literal) =>
            new("DCS", "x", title, "Gears, Brakes, Hook", true, ["F-4E-45MC"], [], string.Empty) { Match = match, IsNameMatch = literal, FunctionName = "Wheel brakes" };
        BuilderSearchResultItem understood = Row("Wheel Brake", "function", false);
        understood.Section = "function";
        understood.Query = "brakes";
        understood.Understood = "Wheel brakes";
        understood.UnderstoodBy = "name";
        understood.ActionFunction = "Wheel brakes axis";
        Assert.Equal("'brakes' is understood as 'Wheel brakes'; this action's function is 'Wheel brakes axis'.", understood.ReasonText);
        Assert.Equal("same function", understood.MatchLabel);

        BuilderSearchResultItem synonym = Row("Collective", "synonym", false);
        synonym.Section = "function";
        synonym.Query = "thrust";
        synonym.Understood = "Throttle";
        synonym.UnderstoodBy = "synonym";
        synonym.QueryTerm = "thrust";
        synonym.ActionFunction = "Throttle axis";
        synonym.GameTerm = "collective";
        Assert.Equal("'thrust' is a synonym of 'Throttle'; this action's function is 'Throttle axis'. The game calls it 'collective'.", synonym.ReasonText);

        BuilderSearchResultItem category = Row("[SimPit] Landing Gear (Switch)", "words", false);
        category.Section = "related";
        category.RelatedBy = "category";
        category.CategoryWords = ["brakes"];
        Assert.Equal("Related: its category 'Gears, Brakes, Hook' has the word 'brakes'; its title has none of the words you typed.", category.ReasonText);
        Assert.Equal(category.ReasonText, category.WhyText);
        Assert.Equal("related", category.MatchLabel);
        Assert.Equal("axis (sim-pit switch)", category.KindLabel);

        BuilderSearchResultItem some = Row("Landing Light", "words", false);
        some.Section = "related";
        some.RelatedBy = "some-words";
        some.TitleWords = ["landing"];
        Assert.Equal("Related: its title has only some of the words you typed ('landing').", some.ReasonText);

        // An older engine without "Section": the kind from Literal and Match.
        Assert.Equal("name", Row("Wheel Brake", "words", true).SectionKind);
        Assert.Equal("function", Row("Throttle", "synonym", false).SectionKind);
        Assert.Equal("related", Row("Landing Gear", "words", false).SectionKind);
    }

    [Theory]
    [InlineData("Wheel Brake (Left)", "brakes", "Brake")]
    [InlineData("Flap Up", "flaps", "Flap")]
    [InlineData("Landing Light", "lights", "Light")]
    [InlineData("Master Arm Switch", "switches", "Switch")]
    [InlineData("Wheel Brakes", "brake", "Brake")]
    public void Highlight_MarksTheSingularOfAPlural(string title, string query, string marked)
    {
        (int start, int length) = Assert.Single(HighlightTextBlock.FindHighlightSegments(title, query));
        Assert.Equal(marked, title.Substring(start, length)[..marked.Length]);
    }
}
