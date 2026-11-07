using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.6.4 tester feedback (Speed of heat): Relations page, Games filter "DCS World: F-16C_50". The relation
// "THROTTLE - THRUST SINGLE ENGINE - F-100D" has one DCS action "Thrust" (a2004cdnil) for the F-100D and the F-16C_50;
// the filter found it, but every place named the F-100D first ("Thrust · DCS World · F-100D +1 more", "F-100D,
// F-16C_50", the F-100D's icon, the F-100D first in the aircraft list): "the order!". Now the filter's aircraft come
// first wherever the page names an action's aircraft; display only, nothing hidden, the profile untouched; without a
// filter everything as before.
// Pictures with JOYPRO_265_RENDER_OUT=<folder>: relations-filter-order-<theme>.png.
[SupportedOSPlatform("windows")]
public sealed class TesterBatch265Tests
{
    internal const string Thrust = "THROTTLE - THRUST SINGLE ENGINE - F-100D";

    // The tester's relation: one axis action "Thrust" for the F-100D and the F-16C_50 (in this order), on the throttle.
    internal static BuilderFakeEngine ThrustEngine()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation(Thrust, true, BuilderFakeEngine.Stick + "::JOY_Z", ("DCS", "a2004cdnil", "F-100D,F-16C_50"));
        fake.NodeTitles["a2004cdnil"] = "Thrust";
        fake.AircraftJson = "{\"Relation\":\"" + Thrust + "\",\"Nodes\":[{\"Relation\":\"" + Thrust + "\",\"Game\":\"DCS\",\"Id\":\"a2004cdnil\",\"Title\":\"Thrust\"," +
                            "\"Category\":\"Flight Control\",\"Active\":2,\"Available\":2,\"Differs\":0,\"Excluded\":0,\"Mixed\":false,\"Aircraft\":[" +
                            "{\"Plane\":\"F-100D\",\"Title\":\"Thrust\",\"Category\":\"Flight Control\",\"State\":\"on\",\"Match\":\"same\",\"Note\":\"\"}," +
                            "{\"Plane\":\"F-16C_50\",\"Title\":\"Thrust\",\"Category\":\"Flight Control\",\"State\":\"on\",\"Match\":\"same\",\"Note\":\"\"}]}]}";
        return fake;
    }

    // The same relation with a module icon for each aircraft (the F-100D's orange, the F-16C_50's blue), as "media"
    // names them.
    internal static BuilderFakeEngine ThrustEngineWithIcons()
    {
        BuilderFakeEngine fake = ThrustEngine();
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "icons-265-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string hun = Path.Combine(folder, "F-100D.png");
        string viper = Path.Combine(folder, "F-16C_50.png");
        AvaloniaRenderThread.Run(() =>
        {
            SaveSquare(hun, 0xFFE07020);
            SaveSquare(viper, 0xFF2060E0);
        });
        fake.MediaJson = "{\"Games\":[],\"Aircraft\":[{\"Plane\":\"F-100D\",\"Icon\":" + System.Text.Json.JsonSerializer.Serialize(hun) + "}," +
                         "{\"Plane\":\"F-16C_50\",\"Icon\":" + System.Text.Json.JsonSerializer.Serialize(viper) + "}]}";
        return fake;
    }

    private static void SaveSquare(string path, uint argb)
    {
        using WriteableBitmap bitmap = new(new PixelSize(64, 64), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (Avalonia.Platform.ILockedFramebuffer frame = bitmap.Lock())
        {
            int[] row = Enumerable.Repeat(unchecked((int)argb), 64).ToArray();
            for (int y = 0; y < 64; y++)
                System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, 64);
        }
        bitmap.Save(path);
    }

    private static Bitmap? PictureOf(RelationNodeItem node, string plane) => node.PlanePictures.Single(p => p.Name == plane).Image;

    private static List<string> Shown(RelationNodeItem node) => node.Aircraft!.Visible.Select(i => i.Plane).ToList();

    private static bool Writes(IReadOnlyList<string> call) =>
        call.Any(a => a is "set" or "apply" or "add" or "remove" or "move" or "rename" or "order" or "export" or "save");

    // The relation editor: the action's row, its picture and its aircraft list.
    [Fact]
    public async Task GamesFilter_TheOpenRelationNamesTheFilteredAircraftFirst()
    {
        try
        {
            BuilderFakeEngine fake = ThrustEngineWithIcons();
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "thrust-265.pr0file");
            await RelationBuilderTests.OpenAsync(vm, Thrust);
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count == 1 && vm.RelationNodes[0].HasAircraft);
            RelationNodeItem thrust = vm.RelationNodes[0];
            await RelationBuilderTests.WaitForAsync(() => thrust.GameCover != null && thrust.PlanePictures.All(p => p.Image != null));
            Bitmap hunIcon = PictureOf(thrust, "F-100D")!;
            Bitmap viperIcon = PictureOf(thrust, "F-16C_50")!;
            Assert.NotSame(hunIcon, viperIcon);

            // No filter: as always (the relation's order, the F-100D's icon).
            Assert.Equal("DCS World · F-100D, F-16C_50 · axis", thrust.DetailsText);
            Assert.Equal("F-100D" + Environment.NewLine + "F-16C_50", thrust.PlanesDisplay);
            Assert.Equal("F-100D", thrust.CoverPlane);
            Assert.Null(thrust.LeadPlane);
            Assert.Same(hunIcon, thrust.Cover);
            Assert.Equal(["F-100D", "F-16C_50"], Shown(thrust));
            Assert.DoesNotContain(thrust.Aircraft!.Visible, i => i.IsFilterMatch);
            Assert.StartsWith("Same meaning", thrust.Aircraft.Visible[0].GroupHeader);
            Assert.False(thrust.Aircraft.HasFilterLead);
            int calls = fake.Calls.Count;

            // The tester's filter "DCS World: F-16C_50": the F-16C_50 first everywhere in the row.
            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50");
            Assert.True(thrust.IsFilterMatch);
            Assert.Equal("DCS World · F-16C_50, F-100D · axis", thrust.DetailsText);
            Assert.Equal("F-16C_50" + Environment.NewLine + "F-100D", thrust.PlanesDisplay);
            // The picture is the F-16C_50's.
            Assert.Equal("F-16C_50", thrust.LeadPlane);
            Assert.Equal("F-16C_50", thrust.CoverPlane);
            await RelationBuilderTests.WaitForAsync(() => thrust.LeadCover != null);
            Assert.Same(viperIcon, thrust.Cover);
            // The aircraft list: the filter's aircraft lead (marked, under "Filter: F-16C_50"), then the rest as always.
            Assert.Equal(["F-16C_50", "F-100D"], Shown(thrust));
            Assert.True(thrust.Aircraft.Visible[0].IsFilterMatch);
            Assert.False(thrust.Aircraft.Visible[1].IsFilterMatch);
            Assert.Equal("Filter: F-16C_50", thrust.Aircraft.Visible[0].GroupHeader);
            Assert.StartsWith("Same meaning", thrust.Aircraft.Visible[1].GroupHeader);
            Assert.EndsWith("(1)", thrust.Aircraft.Visible[1].GroupHeader);
            Assert.True(thrust.Aircraft.HasFilterLead);
            // Nothing hidden, nothing in the profile changed: the action's aircraft, its ticks and their order stay.
            Assert.Equal(2, thrust.Aircraft.Total);
            Assert.Equal("F-100D,F-16C_50", thrust.ActivePlanesText);
            Assert.Equal(["F-100D", "F-16C_50"], thrust.Planes);
            Assert.Equal(["F-100D", "F-16C_50"], thrust.Aircraft.Items.Select(i => i.Plane).ToList());
            Assert.Equal(["F-100D", "F-16C_50"], thrust.Aircraft.CheckedPlanes);
            Assert.False(thrust.Aircraft.HasChanges);
            Assert.DoesNotContain(fake.Calls.Skip(calls), Writes);

            // The list's own filter box keeps the lead.
            thrust.Aircraft.FilterText = "F-";
            Assert.Equal(["F-16C_50", "F-100D"], Shown(thrust));
            thrust.Aircraft.FilterText = string.Empty;

            // Both aircraft in the filter (or the whole game): the relation's order, no extra heading.
            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50", "F-100D");
            Assert.Equal("DCS World · F-100D, F-16C_50 · axis", thrust.DetailsText);
            Assert.Equal(["F-100D", "F-16C_50"], Shown(thrust));
            Assert.Null(thrust.LeadPlane);
            vm.RelationFilter.ClearAllCommand.Execute(null);
            vm.RelationFilter.SelectGame("DCS");
            Assert.Equal("DCS World · F-100D, F-16C_50 · axis", thrust.DetailsText);
            Assert.Equal(["F-100D", "F-16C_50"], Shown(thrust));
            Assert.StartsWith("Same meaning", thrust.Aircraft.Visible[0].GroupHeader);

            // The filter cleared: exactly as before.
            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50");
            vm.RelationFilter.ClearAllCommand.Execute(null);
            Assert.False(thrust.IsFilterMatch);
            Assert.Equal("DCS World · F-100D, F-16C_50 · axis", thrust.DetailsText);
            Assert.Equal("F-100D" + Environment.NewLine + "F-16C_50", thrust.PlanesDisplay);
            Assert.Null(thrust.LeadPlane);
            Assert.Equal("F-100D", thrust.CoverPlane);
            Assert.Same(hunIcon, thrust.Cover);
            Assert.Equal(["F-100D", "F-16C_50"], Shown(thrust));
            Assert.DoesNotContain(thrust.Aircraft.Visible, i => i.IsFilterMatch);
            Assert.StartsWith("Same meaning", thrust.Aircraft.Visible[0].GroupHeader);
            Assert.EndsWith("(2)", thrust.Aircraft.Visible[0].GroupHeader);
            Assert.DoesNotContain(fake.Calls.Skip(calls), Writes);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // The card's picture follows the lead aircraft: its own picture while the filter leads, the usual one after.
    [Fact]
    public void Cover_IsTheLeadAircraftsPicture_WhileTheFilterLeads()
    {
        AvaloniaRenderThread.Run(() =>
        {
            using WriteableBitmap viper = new(new PixelSize(2, 2), new Vector(96, 96));
            using WriteableBitmap hun = new(new PixelSize(2, 2), new Vector(96, 96));
            RelationNodeItem node = new() { Game = "DCS", Planes = ["F-100D", "F-16C_50"], GameCover = hun };
            List<string> changed = [];
            node.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
            Assert.Same(hun, node.Cover);
            node.LeadPlane = "F-16C_50";
            Assert.Null(node.Cover);
            node.LeadCover = viper;
            Assert.Same(viper, node.Cover);
            Assert.Contains(nameof(RelationNodeItem.Cover), changed);
            node.LeadPlane = null;
            Assert.Same(hun, node.Cover);
        });
    }

    // The relation list's card: the search's match line names the filter's aircraft first.
    [Fact]
    public async Task GamesFilter_TheCardsMatchLineNamesTheFilteredAircraftFirst()
    {
        try
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(ThrustEngine(), "thrust-265-card.pr0file");
            vm.RelationSearchText = "thrust";
            RelationListItem card = vm.Relations.Single(r => r.Name == Thrust);
            SearchMatchLine line = Assert.Single(card.SearchHits.Lines);
            Assert.Equal("Thrust", line.Hit);
            Assert.Equal("DCS World  ·  F-100D +1 more", line.Where);

            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50");
            card = vm.Relations.Single(r => r.Name == Thrust);
            line = Assert.Single(card.SearchHits.Lines);
            Assert.StartsWith("DCS World  ·  F-16C", line.Where);
            Assert.EndsWith(" +1 more", line.Where);
            Assert.DoesNotContain("F-100D", line.Where);
            // The line still opens the action (the relation's own node).
            Assert.Equal("a2004cdnil", line.Id);
            Assert.Equal(0, line.Node);

            // The Games filter cleared (the search stays): as before.
            vm.RelationFilter.ClearGamesCommand.Execute(null);
            line = Assert.Single(vm.Relations.Single(r => r.Name == Thrust).SearchHits.Lines);
            Assert.Equal("DCS World  ·  F-100D +1 more", line.Where);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // The Binds page: its search's match line and the peek's aircraft follow the Binds page's own Games filter.
    [Fact]
    public async Task GamesFilter_BindsPage_MatchLineAndPeekNameTheFilteredAircraftFirst()
    {
        try
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(ThrustEngine(), "thrust-265-binds.pr0file");
            vm.CurrentStage = UiStage.Binds;
            await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy && vm.Binds.Any(b => b.RelationName == Thrust));
            // The peek of the relation (the page loads it for the selected row; read here directly, as BindPeekTests do).
            vm.IsBindPeekOpen = true;
            await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy);
            await vm.LoadBindPeekAsync(Thrust);
            Assert.True(vm.HasBindPeekActions);
            BindPeekAction action = vm.BindPeekGroups.SelectMany(g => g.Actions).Single();
            Assert.Equal("F-100D, F-16C_50", action.PlaneNames);
            vm.BindSearchText = "thrust";
            Assert.Equal("DCS World  ·  F-100D +1 more", vm.Binds.First(b => b.RelationName == Thrust).SearchHits.Lines.Single().Where);

            vm.BindFilter.SelectOnlyPlanes("DCS", "F-16C_50");
            Assert.Equal("F-16C_50, F-100D", action.PlaneNames);
            Assert.Equal(["F-100D", "F-16C_50"], action.Planes);
            string where = vm.Binds.First(b => b.RelationName == Thrust).SearchHits.Lines.Single().Where;
            Assert.StartsWith("DCS World  ·  F-16C", where);
            Assert.DoesNotContain("F-100D", where);

            // A new peek under the filter starts in the filter's order.
            await vm.LoadBindPeekAsync(Thrust);
            Assert.Equal("F-16C_50, F-100D", vm.BindPeekGroups.SelectMany(g => g.Actions).Single().PlaneNames);

            vm.BindFilter.ClearGamesCommand.Execute(null);
            Assert.Equal("F-100D, F-16C_50", vm.BindPeekGroups.SelectMany(g => g.Actions).Single().PlaneNames);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // "Open catalog" under the Games filter: the filter's aircraft when the open relation has it (the catalog lists the
    // A-10C and the F-16C_50; the relation "Gear" is for both, the A-10C first, plus an IL-2 action). No filter: the
    // relation's first DCS aircraft as always. (A relation without any filtered game or aircraft is not in the list.)
    [Fact]
    public async Task GamesFilter_OpenCatalog_OpensTheFilteredAircraft()
    {
        static BuilderFakeEngine GearEngine()
        {
            BuilderFakeEngine fake = new();
            fake.AddRelation("Gear", false, string.Empty, ("DCS", "d68", "A-10C,F-16C_50"), ("IL2Game", "gear_toggle", "IL2Game"));
            return fake;
        }

        async Task<MainWindowViewModel> OpenGearAsync(BuilderFakeEngine fake, Action<ListFilterViewModel>? filter)
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "gear-265-" + Guid.NewGuid().ToString("N") + ".pr0file");
            await RelationBuilderTests.OpenAsync(vm, "Gear");
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count == 2);
            filter?.Invoke(vm.RelationFilter);
            Assert.Equal("Gear", vm.SelectedRelation?.Name);
            fake.Calls.Clear();
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            Assert.True(vm.IsBuilderCatalogPanel);
            return vm;
        }

        try
        {
            // No filter: the relation's first DCS aircraft.
            BuilderFakeEngine plain = GearEngine();
            MainWindowViewModel vm = await OpenGearAsync(plain, null);
            Assert.Equal("DCS", vm.CatalogGame?.Game);
            Assert.Equal("A-10C", vm.CatalogPlane?.Plane);
            Assert.Contains(plain.Calls, c => AxisTuningTests.Seq(c, "relations", "browse", "--game", "DCS", "--plane", "A-10C"));

            // "DCS World: F-16C_50": the F-16C_50, although the relation names the A-10C first.
            BuilderFakeEngine viper = GearEngine();
            vm = await OpenGearAsync(viper, f => f.SelectOnlyPlanes("DCS", "F-16C_50"));
            Assert.Equal("DCS", vm.CatalogGame?.Game);
            Assert.Equal("F-16C_50", vm.CatalogPlane?.Plane);
            Assert.Contains(viper.Calls, c => AxisTuningTests.Seq(c, "relations", "browse", "--game", "DCS", "--plane", "F-16C_50"));
            Assert.DoesNotContain(viper.Calls, c => AxisTuningTests.Seq(c, "relations", "browse", "--plane", "A-10C"));
            Assert.DoesNotContain(viper.Calls, Writes);

            // A filtered game: that game's list.
            BuilderFakeEngine il2 = GearEngine();
            vm = await OpenGearAsync(il2, f => f.SelectGame("IL2Game"));
            Assert.Equal("IL2Game", vm.CatalogGame?.Game);
            Assert.Null(vm.CatalogPlane);

            // A DCS aircraft the relation does not have and a game it has: that game.
            BuilderFakeEngine other = GearEngine();
            vm = await OpenGearAsync(other, f =>
            {
                f.SelectGame("IL2Game");
                f.SelectOnlyPlanes("DCS", "FA-18C_hornet");
            });
            Assert.Equal("IL2Game", vm.CatalogGame?.Game);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // "a link to take us from the relation to the binds as well as the bind to the relation": the bound relation's bind
    // chip under its name opens the Binds page with that bind selected (an unbound one has "Not bound yet: Binds page");
    // Back returns to the relation; "Open in Relations" on the Binds page goes the other way. Pictures with
    // JOYPRO_265_RENDER_OUT: relation-bind-chip-<theme>.png.
    [Fact]
    public async Task BoundRelation_BindChip_OpensItsBind_AndBackReturns()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_265_RENDER_OUT") is { Length: > 0 } o ? o : null;
        if (outDir != null)
            Directory.CreateDirectory(outDir);
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "bind-chip-265.pr0file");
        try
        {
            vm.CurrentStage = UiStage.Relations;
            await RelationBuilderTests.OpenAsync(vm, "Flare");
            Assert.True(vm.SelectedRelation!.IsBound, "bind: '" + vm.SelectedRelation.Bind + "' of " + vm.SelectedRelation.Name);
            AvaloniaRenderThread.Run(() =>
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                    main.Settle();
                    Button chip = main.Page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RelationBindChip");
                    Assert.True(chip.IsEffectivelyVisible);
                    Assert.Same(vm.BindSelectedRelationCommand, chip.Command);
                    // The tooltip names the bind and says what a click does.
                    StackPanel tip = Assert.IsType<StackPanel>(ToolTip.GetTip(chip));
                    ToolTip.SetIsOpen(chip, true);
                    main.Settle();
                    List<string> lines = tip.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                    Assert.Equal(vm.SelectedRelation!.BindTooltip, lines[0]);
                    Assert.Equal("Open this bind on the Binds page", lines[1]);
                    ToolTip.SetIsOpen(chip, false);
                    // The chip shows the input with an arrow; the unbound link is not there.
                    List<string> texts = chip.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
                    Assert.Contains(vm.SelectedRelation.BindInputPart, texts);
                    Assert.Single(chip.GetVisualDescendants().OfType<PathIcon>());
                    Assert.DoesNotContain(main.Page.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Not bound yet: Binds page") && b.IsEffectivelyVisible);
                    if (outDir != null)
                    {
                        using RenderTargetBitmap bitmap = main.Render();
                        bitmap.Save(Path.Combine(outDir, "relation-bind-chip-" + theme + ".png"));
                    }
                }
            });

            // A click: the Binds page with Flare's bind selected.
            vm.BindSelectedRelationCommand.Execute(null);
            Assert.Equal(UiStage.Binds, vm.CurrentStage);
            await RelationBuilderTests.WaitForAsync(() => vm.SelectedBind?.RelationName == "Flare");
            Assert.Equal("Flare", vm.SelectedBind?.RelationName);
            Assert.DoesNotContain(fake.Calls, Writes);

            // Back: the relation again.
            Assert.True(vm.CanNavigateBack);
            vm.NavigateBackCommand.Execute(null);
            Assert.Equal(UiStage.Relations, vm.CurrentStage);
            await RelationBuilderTests.WaitForAsync(() => vm.SelectedRelation?.Name == "Flare");
            Assert.Equal("Flare", vm.SelectedRelation?.Name);
        }
        finally
        {
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // The page as the tester sees it: the filter, the search, the open relation with its aircraft list open; the row
    // and the list name the F-16C_50 first. Light and dark (pictures with JOYPRO_265_RENDER_OUT).
    [Fact]
    public async Task GamesFilter_OnThePage_TheRowAndTheListLeadWithTheFilteredAircraft()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_265_RENDER_OUT") is { Length: > 0 } o ? o : null;
        if (outDir != null)
            Directory.CreateDirectory(outDir);
        try
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(ThrustEngineWithIcons(), "thrust-265-page.pr0file");
            await RelationBuilderTests.OpenAsync(vm, Thrust);
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count == 1 && vm.RelationNodes[0].HasAircraft);
            RelationNodeItem thrust = vm.RelationNodes[0];
            await RelationBuilderTests.WaitForAsync(() => thrust.PlanePictures.All(p => p.Image != null));
            vm.RelationSearchText = "thrust";
            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50");
            await RelationBuilderTests.WaitForAsync(() => thrust.LeadCover != null);
            Bitmap viperIcon = PictureOf(thrust, "F-16C_50")!;
            thrust.Aircraft!.IsExpanded = true;
            vm.StatusText = "Ready.";
            AvaloniaRenderThread.Run(() =>
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1600, 900));
                    main.Settle();
                    ListBox list = main.Page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RelationNodeList");
                    List<string> texts = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                    Assert.Contains("DCS World · F-16C_50, F-100D · axis", texts);
                    Assert.Contains("Filter: F-16C_50", texts);
                    int viper = texts.IndexOf("F-16C_50");
                    int hun = texts.IndexOf("F-100D");
                    Assert.True(viper >= 0 && hun > viper, "the aircraft list does not lead with the F-16C_50: " + string.Join(" | ", texts));
                    // The row's picture is the F-16C_50's icon.
                    Image cover = list.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "RelationNodeCover");
                    Assert.True(cover.IsEffectivelyVisible);
                    Assert.Same(viperIcon, cover.Source);
                    // The card's match line names the F-16C_50 first.
                    SearchMatchLine shownLine = Assert.Single(main.Page.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.Classes.Contains("searchHit") && b.IsEffectivelyVisible).Select(b => b.DataContext).OfType<SearchMatchLine>());
                    Assert.StartsWith("DCS World  ·  F-16C", shownLine.Where);
                    if (outDir != null)
                    {
                        using RenderTargetBitmap bitmap = main.Render();
                        bitmap.Save(Path.Combine(outDir, "relations-filter-order-" + theme + ".png"));
                    }
                }
            });
        }
        finally
        {
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
            await RelationBuilderTests.SettleAllAsync();
        }
    }
}
