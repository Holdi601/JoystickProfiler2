using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;
using Xunit.Abstractions;
using static JoyPro.Gui.Localization.Loc;

namespace JoyPro.Gui.Tests;

// F1's help mode (docs/ui/help-and-tours.md): every interactive control of every page has a help text, a click in help
// mode lands on the help layer (the control does not run), F1 / Esc switch it, and the popup sits next to its control.
// JOYPRO_HELP_RENDER_OUT=<folder> also renders the help popup and tour steps on several pages, light and dark.
[SupportedOSPlatform("windows")]
public sealed class HelpModeTests(ITestOutputHelper output)
{
    internal static readonly (string Page, UiStage Stage)[] Pages =
    [
        ("start", UiStage.Start), ("relations", UiStage.Relations), ("binds", UiStage.Binds), ("modifiers", UiStage.Modifiers),
        ("layout", UiStage.Layout), ("export", UiStage.Export), ("hud", UiStage.Hud), ("ffb", UiStage.FfbTelemetry),
        ("vjoy", UiStage.VirtualJoystick), ("cockpits", UiStage.Cockpits), ("statistics", UiStage.Statistics),
        ("settings", UiStage.Settings), ("advanced", UiStage.Advanced)
    ];

    [Fact]
    public void Catalog_HasEveryWikiControl_WithText()
    {
        IReadOnlyList<HelpEntry> entries = HelpCatalog.Entries;
        Assert.True(entries.Count >= 1000, "help-controls.json has " + entries.Count + " controls");
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.What), e.Page + " " + e.Key));
        Assert.All(entries, e => Assert.StartsWith(HelpCatalog.WikiBase + "gui/" + e.Page + ".html#", HelpCatalog.WikiUrl(e.Page, e.Anchor, "en")));
        // Every page of the bars and of the wiki has its plain-words texts.
        foreach ((string page, _) in Pages)
            Assert.Equal(page, HelpCatalog.Page(page).Slug);
        Assert.Contains(entries, e => e.Key == "GoRelationsCommand");
    }

    // "More in the wiki" opens the page in the GUI's language: /<code>/gui/... with the GUI's codes, English at the root.
    [Fact]
    public void WikiUrl_OpensTheUsersLanguage()
    {
        Assert.Equal(HelpCatalog.WikiBase + "gui/binds.html#record", HelpCatalog.WikiUrl("binds", "record", "en"));
        Assert.Equal(HelpCatalog.WikiBase + "de/gui/binds.html#record", HelpCatalog.WikiUrl("binds", "record", "de"));
        Assert.Equal(HelpCatalog.WikiBase + "zh-Hans/gui/start.html", HelpCatalog.WikiUrl("", null, "zh-hans"));
        Assert.Equal(HelpCatalog.WikiBase + "sr-Cyrl/gui/vjoy.html", HelpCatalog.WikiUrl("vjoy", null, "sr-Cyrl"));
        Assert.Equal(HelpCatalog.WikiBase + "gui/vjoy.html", HelpCatalog.WikiUrl("vjoy", null, "xx"));
        Assert.Equal(HelpCatalog.WikiBase + "gui/vjoy.html", HelpCatalog.WikiUrl("vjoy", null, null));
        // Without a language: the GUI's current one.
        Assert.Equal(HelpCatalog.WikiUrl("export", "check", Loc.Current.Code), HelpCatalog.WikiUrl("export", "check"));
        Assert.Equal("https://holdi601.github.io/JoystickProfiler2/", HelpCatalog.WikiBase);
    }

    // The walk over every page: every button, field, chip, list row, tab, menu button and picture that is shown has a
    // help text of its own (from the wiki or its own words); none falls back to nothing.
    [Fact]
    public void EveryInteractiveControl_OnEveryPage_HasAHelpText()
    {
        List<string> missing = [];
        int catalog = 0, derived = 0, total = 0;
        List<string> timings = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.ShowStatisticsPage = true;
                foreach (SettingsSectionItem section in vm.SettingsSections)
                    section.IsExpanded = true;
                foreach ((string page, UiStage stage) in Pages)
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    vm.CurrentStage = stage;
                    AvaloniaRenderThread.Pump();
                    // A real (hidden) window: the lists realize the rows a user sees, the sections are all open.
                    using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1600, 1400));
                    foreach (Expander expander in window.Page.GetVisualDescendants().OfType<Expander>().ToList())
                        expander.IsExpanded = true;
                    window.Settle();
                    Control root = window.Page;
                    int before = total;
                    HashSet<Control> seen = new(ReferenceEqualityComparer.Instance);
                    foreach (Control control in root.GetVisualDescendants().OfType<Control>().ToList())
                    {
                        if (!HelpResolver.IsStandaloneTarget(control) || control.FindAncestorOfType<HelpLayer>() != null || !seen.Add(control))
                            continue;
                        total++;
                        HelpTopic topic = HelpResolver.Resolve(control, HelpLayer.PageOf(control, page));
                        if (topic.Source == HelpSource.Catalog)
                            catalog++;
                        else if (topic.Source == HelpSource.Derived)
                            derived++;
                        else
                            missing.Add(page + ": " + Describe(control));
                        Assert.False(string.IsNullOrWhiteSpace(topic.What), page + ": " + Describe(control));
                    }
                    timings.Add($"{page}: {total - before} controls, {watch.ElapsedMilliseconds} ms");
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
        output.WriteLine($"{total} controls with help: {catalog} from the wiki, {derived} from their own words, {missing.Count} without.");
        foreach (string t in timings)
            output.WriteLine(t);
        foreach (string m in missing.Distinct().Take(200))
            output.WriteLine(m);
        Assert.True(missing.Count == 0, missing.Count + " controls without a help text:\n" + string.Join("\n", missing.Distinct().Take(80)));
        Assert.True(catalog > 200, "only " + catalog + " controls found their wiki entry");
    }

    [Fact]
    public void HelpMode_ClickLandsOnTheHelpLayer_ShowsTheControlsText_NotItsAction()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.CurrentStage = UiStage.Start;
                using HiddenWindow hidden = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                MainWindow main = (MainWindow)hidden.Window;
                Control root = hidden.Page;
                HelpLayer layer = root.GetVisualDescendants().OfType<HelpLayer>().Single();
                ToggleButton nav = root.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "Nav_relations");

                // Off: the catcher is not there, a click reaches the page.
                Border catcher = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HelpCatcher");
                Assert.False(catcher.IsVisible);

                // F1: help mode on; the help cursor's catcher covers the whole window, above the content (the layer is the
                // last child of the window's layers), so a click lands on it and not on the button.
                main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                Assert.True(vm.IsHelpModeOn);
                hidden.Settle();
                Assert.True(catcher.IsEffectivelyVisible);
                Assert.True(catcher.IsHitTestVisible);
                Assert.NotNull(catcher.Background);
                Assert.NotNull(catcher.Cursor);
                Assert.Equal(layer.Bounds.Size, catcher.Bounds.Size);
                Panel layers = (Panel)layer.GetVisualParent()!;
                Assert.Same(layer, layers.Children[^1]);
                Assert.True(layers.Bounds.Width >= nav.TranslatePoint(new Point(nav.Bounds.Width, 0), layers)!.Value.X);

                // The click's target is the Relations button; its help shows; the page did not change.
                // (the hit test of a hidden window can lag a frame behind: the control under the point is taken as the
                // visual inside the button, like a click on its text)
                Visual inside = nav.GetVisualDescendants().OfType<TextBlock>().First();
                Control? target = HelpResolver.FindTarget(inside);
                string page = HelpLayer.PageOf(target, vm.CurrentHelpPage);
                Assert.Same(nav, target);
                Assert.Equal("header", page);
                HelpTopic topic = layer.ShowHelpFor(target, page);
                Assert.Equal(HelpSource.Catalog, topic.Source);
                Assert.Equal(T("Relations"), topic.Title);
                Assert.Contains("relation", topic.Fits, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(HelpCatalog.WikiBase + "gui/header.html#", topic.WikiUrl);
                Assert.Equal(UiStage.Start, vm.CurrentStage);
                Assert.True(vm.IsHelpPopupOpen);

                // The popup stands next to the button, inside the window, not over it.
                layer.Place();
                hidden.Settle();
                Border card = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HelpCard");
                Rect cardRect = new(card.TranslatePoint(new Point(0, 0), layer)!.Value, card.Bounds.Size);
                Rect navRect = new(nav.TranslatePoint(new Point(0, 0), layer)!.Value, nav.Bounds.Size);
                Assert.False(cardRect.Intersects(navRect), "the help popup covers its button");
                Assert.True(new Rect(layer.Bounds.Size).Contains(cardRect), "the help popup is cut off: " + cardRect);

                // A click on an empty place explains the page.
                HelpTopic pageTopic = layer.ShowHelpFor(null, "binds");
                Assert.Equal(HelpSource.Page, pageTopic.Source);
                Assert.Equal(T("Binds"), pageTopic.Title);

                // The wiki link opens the control's page; Esc ends help mode.
                vm.ShowHelp(topic);
                vm.OpenHelpWikiCommand.Execute(null);
                Assert.Equal(topic.WikiUrl, vm.LastOpenedHelpUrl);
                main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(vm.IsHelpModeOn);
                Assert.False(vm.IsHelpPopupOpen);
                // F1 twice: on and off.
                main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                Assert.False(vm.IsHelpModeOn);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    [Fact]
    public void MenuButton_ListsItsEntries_FieldsAndRowsExplainThemselves()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.CurrentStage = UiStage.Relations;
                MainWindow main = Layout(vm, 1600, 1000);
                Control root = (Control)main.Content!;
                Button help = root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HelpButton");
                HelpTopic topic = HelpResolver.Resolve(help, "header");
                Assert.Equal(HelpSource.Catalog, topic.Source);
                Assert.Contains(topic.MenuEntries, m => m.Title == T("Help mode (F1)") && m.What.Length > 0);
                Assert.Contains(topic.MenuEntries, m => m.Title == T("Tour of this page"));

                TextBox search = root.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RelationSearchBox");
                HelpTopic field = HelpResolver.Resolve(search, "relations");
                Assert.NotEqual(HelpSource.None, field.Source);

                ListBoxItem row = root.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is RelationListItem);
                HelpTopic rowTopic = HelpResolver.Resolve(row, "relations");
                Assert.Equal(HelpSource.Derived, rowTopic.Source);
                Assert.Equal(T(HelpCatalog.RowTexts["RelationListItem"]), rowTopic.What);
                // JoyPro's own words in a text are explained in plain words.
                Assert.Contains(HelpCatalog.WordsIn("Exports the relation's binds."), w => w.Word == "Relation");
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    // The help popup and tour steps on several pages, light and dark, at two window sizes (looked at by eye).
    [Fact]
    public void RenderHelpPopupAndTourSteps()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_HELP_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    (UiStage Stage, string Page, string Target)[] shots =
                    [
                        (UiStage.Start, "start", "name:HelpButton"),
                        (UiStage.Relations, "relations", "name:RelationSearchBox"),
                        (UiStage.Binds, "binds", "cmd:RecordNextInputCommand"),
                        (UiStage.Export, "export", "nav:check"),
                        (UiStage.Settings, "settings", "cmd:RunGameSetupAgainCommand")
                    ];
                    foreach ((UiStage stage, string page, string target) in shots)
                    {
                        foreach ((int w, int h) in new[] { (1280, 800), (1920, 1080) })
                        {
                            vm.CurrentStage = stage;
                            vm.IsHelpModeOn = true;
                            Shoot(vm, w, h, Path.Combine(outDir, $"help-{page}-{w}-{theme}.png"), layer =>
                            {
                                Control? control = layer.FindTarget(target);
                                layer.ShowHelpFor(control, HelpLayer.PageOf(control, page));
                            });
                            vm.IsHelpModeOn = false;
                        }
                    }
                    foreach ((string tour, int step, int w, int h) in new[] { ("new", 1, 1280, 800), ("new", 4, 1280, 800), ("dcs", 7, 1920, 1080), ("pad", 4, 1280, 800), ("page:binds", 2, 1280, 800), ("jp1", 6, 1920, 1080), ("page:start", 0, 1280, 800) })
                    {
                        vm.StartTour(tour, everyFeature: true, step);
                        Shoot(vm, w, h, Path.Combine(outDir, $"tour-{tour.Replace(':', '-')}-{step}-{w}-{theme}.png"), layer => layer.FindTourTarget());
                        vm.EndTourCommand.Execute(null);
                    }
                    vm.InstalledGamesForWelcome = () => ["DCS", "IL2Game"];
                    foreach ((int w, int h) in new[] { (1280, 800), (1920, 1080) })
                    {
                        vm.OpenWelcome();
                        Shoot(vm, w, h, Path.Combine(outDir, $"welcome-{w}-{theme}.png"), _ => { });
                        vm.IsWelcomeOpen = false;
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.StopGameConfigWatcher();
            }
        });
    }

    // One picture in a hidden window (real layout passes): the layer placed, then the page onto its background.
    private static void Shoot(MainWindowViewModel vm, int w, int h, string file, Action<HelpLayer> prepare)
    {
        using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(w, h));
        HelpLayer layer = window.Page.GetVisualDescendants().OfType<HelpLayer>().Single();
        prepare(layer);
        for (int i = 0; i < 3; i++)
        {
            window.Settle();
            layer.Place();
        }
        window.Settle();
        using RenderTargetBitmap bitmap = window.Render();
        bitmap.Save(file);
    }

    // The window laid out offscreen like the render tests (lists without virtualization); expandAll opens every section.
    internal static MainWindow Layout(MainWindowViewModel vm, int width, int height, bool expandAll = false)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        RenderPanels.UnvirtualizeItemsControls(main);
        Relayout(main, width, height);
        if (expandAll)
        {
            foreach (Expander expander in ((Control)main.Content!).GetVisualDescendants().OfType<Expander>().ToList())
                expander.IsExpanded = true;
            Relayout(main, width, height);
        }
        return main;
    }

    internal static void Relayout(MainWindow main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
    }

    private static string Describe(Control control)
    {
        List<string> path = [];
        foreach (Visual v in control.GetSelfAndVisualAncestors().Take(8))
        {
            if (v is Control c)
                path.Add(c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? string.Empty : "#" + c.Name));
        }
        return string.Join(" < ", path) + " | text: " + HelpResolver.ShownText(control) + " | tip: " + HelpResolver.TooltipText(control) +
               " | ctx: " + control.DataContext?.GetType().Name;
    }
}
