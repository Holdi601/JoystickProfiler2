using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.6.2 tester feedback (Speed of heat):
// - Relations page: with the Games filter on one aircraft ("DCS World: F-100D") the open relation's actions for it come
//   first, marked (accent edge, "Filter: F-100D", also on their aircraft line), the others folded in "Other aircraft (n)";
//   the relation's order stays and clearing the filter brings the plain list back.
// - Binds page, axis response: the user curve's 11 points are number boxes in sync with the graph's points; the number
//   boxes take typed values, Up / Down / Enter, and keep the range; Copy to another axis sends the values shown to the
//   other axis in one engine run (invert only when ticked); the Modifiers panel has a close button; the section opens by
//   itself for an axis and remembers the user's close.
// Pictures with JOYPRO_262_RENDER_OUT=<folder>: relations-filter-<width>-<theme>.png, axis-response-<width>-<theme>.png.
[SupportedOSPlatform("windows")]
public sealed class TesterBatch262Tests
{
    // ---- Relations page: the Games filter in the relation editor ----------------------------------------------------

    // "Button CP2 B3": five DCS actions, one aircraft each (F-5E-3, AJS37, SpitfireLFMkIX, FA-18C_hornet, AH-64D_BLK_II);
    // the F-5E-3's action is also in the F-5E (2 of 2 aircraft), the others 1 of 1.
    internal static BuilderFakeEngine FiveAircraftEngine()
    {
        BuilderFakeEngine fake = new();
        fake.AddFiveActionRelation();
        static string Node(string id, string title, params (string Plane, string State)[] planes) =>
            "{\"Relation\":\"Button CP2 B3\",\"Game\":\"DCS\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"Cockpit\",\"Active\":" + planes.Count(p => p.State == "on") +
            ",\"Available\":" + planes.Length + ",\"Differs\":0,\"Excluded\":0,\"Mixed\":false,\"Aircraft\":[" +
            string.Join(",", planes.Select(p => "{\"Plane\":\"" + p.Plane + "\",\"Title\":\"" + title + "\",\"Category\":\"Cockpit\",\"State\":\"" + p.State + "\",\"Match\":\"same\",\"Note\":\"\"}")) + "]}";
        fake.AircraftJson = "{\"Relation\":\"Button CP2 B3\",\"Nodes\":[" + string.Join(",",
            Node("d3001pnilu3001", "Gun Arm Switch", ("F-5E-3", "on"), ("F-5E", "on")),
            Node("d3002pnilu3002", "Master Mode Selector", ("AJS37", "on")),
            Node("d3003pnilu3003", "Gun Safety", ("SpitfireLFMkIX", "on")),
            Node("d3004pnilu3004", "Master Arm Switch", ("FA-18C_hornet", "on")),
            Node("d3005pnilu3005", "Weapons Action Switch", ("AH-64D_BLK_II", "on"))) + "]}";
        return fake;
    }

    [Fact]
    public async Task GamesFilter_PutsTheFilteredAircraftFirst_AndFoldsTheOthers()
    {
        try
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(FiveAircraftEngine(), "filter-262.pr0file");
            await RelationBuilderTests.OpenAsync(vm, "Button CP2 B3");
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count(n => n.HasAircraft) == 5);
            List<string> order = vm.RelationNodes.Select(n => n.Id).ToList();

            // No filter: the list as always.
            Assert.False(vm.IsRelationNodeListGrouped);
            Assert.Same(vm.RelationNodes, vm.RelationNodeListItems);
            Assert.DoesNotContain(vm.RelationNodes, n => n.IsFilterMatch);

            // The Games filter on one aircraft (the tester's "DCS World: F-100D"): its action first, the fold after it.
            vm.RelationFilter.SelectOnlyPlanes("DCS", "AJS37");
            Assert.True(vm.IsRelationNodeListGrouped);
            Assert.Same(vm.RelationNodesShown, vm.RelationNodeListItems);
            RelationNodeItem viggen = Assert.IsType<RelationNodeItem>(vm.RelationNodesShown[0]);
            Assert.Equal("d3002pnilu3002", viggen.Id);
            Assert.True(viggen.IsFilterMatch);
            Assert.Equal("Filter: AJS37", viggen.FilterMatchText);
            // An action with an aircraft line shows the mark there and the aircraft ticked in its list.
            Assert.True(viggen.ShowFilterChipOnAircraftLine);
            Assert.False(viggen.ShowFilterChipInHeader);
            Assert.True(viggen.Aircraft!.Items.Single(i => i.Plane == "AJS37").IsFilterMatch);
            RelationNodeOthersHeader fold = Assert.IsType<RelationNodeOthersHeader>(vm.RelationNodesShown[1]);
            Assert.Equal(2, vm.RelationNodesShown.Count);
            Assert.Equal(4, fold.Count);
            Assert.Equal("Other aircraft (4)", fold.Label);
            Assert.False(fold.IsExpanded);
            Assert.Equal(1, vm.RelationNodeFilterMatchCount);
            Assert.All(vm.RelationNodes.Where(n => n != viggen), n => Assert.False(n.IsFilterMatch));
            // The relation itself is complete and in its order.
            Assert.Equal(order, vm.RelationNodes.Select(n => n.Id).ToList());

            // The fold opens: the others below it, in the relation's order.
            vm.ToggleRelationNodeOthersCommand.Execute(null);
            Assert.True(fold.IsExpanded);
            Assert.Equal(6, vm.RelationNodesShown.Count);
            Assert.Equal(order.Where(id => id != viggen.Id), vm.RelationNodesShown.Skip(2).Cast<RelationNodeItem>().Select(n => n.Id));
            vm.ToggleRelationNodeOthersCommand.Execute(null);
            Assert.Equal(2, vm.RelationNodesShown.Count);

            // Several aircraft ticked: every action for one of them comes first.
            vm.RelationFilter.SelectOnlyPlanes("DCS", "F-5E-3", "F-5E", "FA-18C_hornet");
            RelationNodeItem tiger = vm.RelationNodes.Single(n => n.Id == "d3001pnilu3001");
            Assert.True(tiger.IsFilterMatch);
            Assert.Equal(["d3001pnilu3001", "d3004pnilu3004"], vm.RelationNodesShown.OfType<RelationNodeItem>().Select(n => n.Id).ToList());
            Assert.Equal(3, vm.RelationNodeOthers.Count);

            // An action of the fold picked elsewhere (a search match line) opens the fold.
            vm.SelectedRelationNode = vm.RelationNodes.Single(n => n.Id == "d3005pnilu3005");
            Assert.True(vm.RelationNodeOthers.IsExpanded);
            Assert.Contains(vm.SelectedRelationNode, vm.RelationNodesShown);

            // A whole game ticked: every action is for it, the plain list, all marked.
            vm.RelationFilter.ClearAllCommand.Execute(null);
            vm.RelationFilter.SelectGame("DCS");
            Assert.False(vm.IsRelationNodeListGrouped);
            Assert.All(vm.RelationNodes, n => Assert.True(n.IsFilterMatch));

            // The filter cleared: as always.
            vm.RelationFilter.ClearAllCommand.Execute(null);
            Assert.False(vm.IsRelationNodeListGrouped);
            Assert.Same(vm.RelationNodes, vm.RelationNodeListItems);
            Assert.DoesNotContain(vm.RelationNodes, n => n.IsFilterMatch);
            Assert.DoesNotContain(vm.RelationNodes.SelectMany(n => n.Aircraft!.Items), i => i.IsFilterMatch);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // The real page: the filtered card has the accent edge, the fold is a header row (no card) whose click shows the
    // others; no horizontal scroll bar at 1280 and 1920, light and dark (pictures with JOYPRO_262_RENDER_OUT).
    [Fact]
    public async Task GamesFilter_OnThePage_MarksTheCardAndFoldsTheOthers()
    {
        string? outDir = RenderOut();
        try
        {
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(FiveAircraftEngine(), "filter-262-page.pr0file");
            await RelationBuilderTests.OpenAsync(vm, "Button CP2 B3");
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count(n => n.HasAircraft) == 5);
            vm.RelationFilter.SelectOnlyPlanes("DCS", "AJS37");
            vm.StatusText = "Ready.";
            AvaloniaRenderThread.Run(() =>
            {
                using (HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800)))
                {
                    ListBox list = main.Page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RelationNodeList");
                    List<ListBoxItem> rows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible).ToList();
                    Assert.Equal(2, rows.Count);
                    Border card = rows[0].GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RelationNodeRow");
                    Assert.Contains("filterMatch", card.Classes);
                    Assert.True(card.BorderThickness.Left >= 3);
                    Assert.True(rows[0].GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RelationNodeAircraftFilterChip").IsEffectivelyVisible);
                    Assert.Contains("groupHeader", rows[1].Classes);
                    Button toggle = rows[1].GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RelationNodeOthersToggle");
                    Assert.True(toggle.IsEffectivelyVisible);
                    toggle.Command!.Execute(toggle.CommandParameter);
                    main.Settle();
                    Assert.Equal(6, vm.RelationNodesShown.Count);
                    Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.IsEffectivelyVisible) > 2, "the opened fold shows no other action");
                    vm.ToggleRelationNodeOthersCommand.Execute(null);
                    main.Settle();
                }
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1280, 800), (1920, 1080) })
                        RenderRelationsPage(vm, width, height, outDir, "relations-filter-" + width + "-" + theme + ".png");
                }
                // The fold open, the others' aircraft open, dark.
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.ToggleRelationNodeOthersCommand.Execute(null);
                vm.RelationNodes.Single(n => n.Id == "d3002pnilu3002").Aircraft!.IsExpanded = true;
                RenderRelationsPage(vm, 1920, 1080, outDir, "relations-filter-open-1920-dark.png");
            });
        }
        finally
        {
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // The Relations page in the hidden root (its virtualizing lists realize their rows as on screen): no horizontal scroll
    // bar, every button inside the window, the relation editor's rows visible; saved as a picture when asked.
    private static void RenderRelationsPage(MainWindowViewModel vm, int width, int height, string? outDir, string file)
    {
        using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
        main.Settle();
        Control relations = main.Page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RelationsPaneGrid");
        Assert.True(relations.IsEffectivelyVisible);
        List<Avalonia.Controls.Primitives.ScrollBar> horizontal = relations.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Where(s => s.Orientation == Avalonia.Layout.Orientation.Horizontal && s.IsEffectivelyVisible && s.Bounds.Width > 0 && s.Bounds.Height > 0)
            .ToList();
        Assert.True(horizontal.Count == 0, file + ": a horizontal scroll bar is visible");
        foreach (Button button in relations.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
        {
            Point? right = button.TranslatePoint(new Point(button.Bounds.Width, 0), main.Page);
            Assert.True(right == null || right.Value.X <= width + 1, file + ": the button '" + (button.Content as string ?? button.Name ?? "?") + "' ends outside the window");
        }
        ListBox list = main.Page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RelationNodeList");
        Assert.Contains(list.GetVisualDescendants().OfType<Button>(), b => b.Name == "RelationNodeOthersToggle" && b.IsEffectivelyVisible);
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        using RenderTargetBitmap bitmap = main.Render();
        bitmap.Save(Path.Combine(outDir, file));
    }

    // ---- Binds page: the axis response -----------------------------------------------------------------------------

    private static AxisParameterEditor UserCurve(MainWindowViewModel vm) => vm.AxisTuningEditors.Single(e => e.IsUserCurve);

    [Fact]
    public async Task UserCurvePoints_AreNumbers_InSyncWithTheGraphPoints()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake);
        await AxisTuningTests.SelectScopeAsync(vm, "game:DCS");
        AxisParameterEditor curve = UserCurve(vm);
        curve.IsOn = true;
        Assert.Equal(11, curve.Points.Count);
        Assert.Equal(["0%", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%"], curve.Points.Select(p => p.PositionLabel).ToArray());
        int calls = fake.Calls.Count;

        // Typed: the point moves on the graph (the white handle at 50%).
        AxisCurvePointEditor half = curve.Points[5];
        double before = vm.AxisTuningHandles![5].Y;
        half.Text = "80";
        Assert.Equal(0.8, half.Value, 9);
        Assert.NotEqual(before, vm.AxisTuningHandles![5].Y);
        Assert.Equal(0.8, Math.Abs(vm.AxisTuningHandles[5].Y), 2);
        Assert.True(vm.AxisTuningHasEdits);

        // Dragged on the graph: the box shows the new number.
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(3, vm.AxisTuningHandles[3].X, 0.25));
        Assert.Equal(0.25, curve.Points[3].Value, 3);
        Assert.Equal("25", curve.Points[3].Text);

        // A decimal comma counts; no number / out of range: a message, nothing sent, the curve kept.
        curve.Points[2].Text = "40,5";
        Assert.Equal(0.405, curve.Points[2].Value, 9);
        IReadOnlyList<Point> kept = vm.AxisTuningCurve;
        curve.Points[7].Text = "x";
        Assert.False(curve.Points[7].IsValid);
        Assert.Contains("70%", vm.AxisTuningValidation);
        Assert.False(vm.CanSaveAxisTuning);
        Assert.Same(kept, vm.AxisTuningCurve);
        curve.Points[7].Text = "150";
        Assert.Contains("must be 0 .. 100", vm.AxisTuningValidation);
        curve.Points[7].Text = "90";
        Assert.Equal(string.Empty, vm.AxisTuningValidation);
        Assert.True(vm.CanSaveAxisTuning);
        // Editing is all in the GUI: the engine is asked only on Save.
        Assert.Equal(calls, fake.Calls.Count);

        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set"));
        string sent = set[set.ToList().IndexOf("--curvature") + 1];
        string[] points = sent.Split(',');
        Assert.Equal(11, points.Length);
        Assert.Equal("0.405", points[2]);
        Assert.Equal("0.25", points[3]);
        Assert.Equal("0.8", points[5]);
        Assert.Equal("0.9", points[7]);
    }

    // The real number boxes on the page: 11 boxes in the user curve; typing, Up / Down (Shift: ten steps), Enter puts
    // a number outside the range back to its end; the dead zone box the same; nothing is cut off at 1280 and 1920.
    [Fact]
    public void NumberBoxes_OnThePage_TypeStepAndClamp_AndFit()
    {
        string? outDir = RenderOut();
        AvaloniaRenderThread.Run(() =>
        {
            AxisTuningFakeEngine fake = new();
            fake.OtherAxes.Add(("Roll", "JOY_X"));
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake));
            try
            {
                vm.CurrentStage = UiStage.Binds;
                AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50"));
                AxisParameterEditor curve = UserCurve(vm);
                curve.IsOn = true;
                vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "4";
                using (HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 2400)))
                {
                    main.Settle();
                    Control panel = main.Page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "AxisResponsePanel");
                    Assert.True(panel.IsEffectivelyVisible);
                    List<TextBox> boxes = panel.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name == "AxisUserCurvePoint").ToList();
                    Assert.Equal(11, boxes.Count);
                    Assert.All(boxes, b => Assert.True(b.IsEffectivelyVisible && b.Bounds.Width >= 40, "a curve point box is cut: " + b.Bounds));
                    Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Slider>(), s => s.Orientation == Avalonia.Layout.Orientation.Vertical && s.IsEffectivelyVisible);

                    // Typed into the box: the point and the graph follow.
                    boxes[6].Text = "70";
                    Assert.Equal(0.7, curve.Points[6].Value, 9);
                    // Up: one step (0.01), Shift+Up: ten, Down back.
                    Key(boxes[6], Avalonia.Input.Key.Up);
                    Assert.Equal(0.71, curve.Points[6].Value, 9);
                    Key(boxes[6], Avalonia.Input.Key.Up, KeyModifiers.Shift);
                    Assert.Equal(0.81, curve.Points[6].Value, 9);
                    Key(boxes[6], Avalonia.Input.Key.Down);
                    Assert.Equal(0.8, curve.Points[6].Value, 9);
                    // At the end of the range it stays there.
                    boxes[10].Text = "100";
                    Key(boxes[10], Avalonia.Input.Key.Up);
                    Assert.Equal("100", boxes[10].Text);
                    // Out of range: a message while typed, Enter puts it back to the nearest end.
                    boxes[9].Text = "170";
                    Assert.False(curve.Points[9].IsValid);
                    Assert.Contains("90%", vm.AxisTuningValidation);
                    Key(boxes[9], Avalonia.Input.Key.Enter);
                    Assert.Equal("100", boxes[9].Text);
                    Assert.True(curve.Points[9].IsValid);
                    Assert.Equal(string.Empty, vm.AxisTuningValidation);
                    // The graph's drag shows in the box.
                    vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(2, vm.AxisTuningHandles![2].X, 0.15));
                    main.Settle();
                    Assert.Equal("15", boxes[2].Text);

                    // The dead zone's box: steps of its own, the range shown next to it, kept in the range.
                    TextBox deadzone = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "AxisParameterNumber" && t.DataContext is AxisParameterEditor { Key: "deadzone" });
                    Assert.Equal("4", deadzone.Text);
                    Key(deadzone, Avalonia.Input.Key.Up);
                    Assert.Equal(0.05, vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Value, 9);
                    deadzone.Text = "-30";
                    Key(deadzone, Avalonia.Input.Key.Enter);
                    Assert.Equal("0", deadzone.Text);
                    Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0 .. 100" && t.IsEffectivelyVisible);
                    deadzone.Text = "4";
                    main.Settle();
                }
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 1920 })
                    {
                        // A soft user curve as a pilot would set it (the fake engine forgets saved values, so set each time).
                        AvaloniaRenderThread.Wait(AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50"));
                        UserCurve(vm).IsOn = true;
                        double[] soft = [0, 0.04, 0.09, 0.15, 0.23, 0.33, 0.44, 0.57, 0.7, 0.85, 1];
                        for (int i = 0; i < soft.Length; i++)
                            UserCurve(vm).Points[i].Text = (soft[i] * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "4";
                        RenderAxisPanel(vm, width, outDir, "axis-response-" + width + "-" + theme + ".png");
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                vm.CurrentStage = UiStage.Start;
            }
        });
    }

    private static void Key(TextBox box, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = box });

    // The axis response section of the Binds page at the width: every number box and button inside the panel (nothing
    // cut off on the right), saved as a picture when asked.
    private static void RenderAxisPanel(MainWindowViewModel vm, int width, string? outDir, string file)
    {
        using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, 3000));
        main.Settle();
        Control panel = main.Page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "AxisResponsePanel");
        Assert.True(panel.IsEffectivelyVisible, file + ": the axis response is not shown");
        foreach (Control box in panel.GetVisualDescendants().OfType<Control>().Where(c => (c is TextBox or Button) && c.IsEffectivelyVisible && c.Bounds.Width > 0))
        {
            Point? right = box.TranslatePoint(new Point(box.Bounds.Width, 0), panel);
            Assert.True(right == null || right.Value.X <= panel.Bounds.Width + 1, file + ": '" + (box.Name ?? box.GetType().Name) + "' ends outside the panel (" + right?.X + " > " + panel.Bounds.Width + ")");
        }
        SaveCrop(main, panel, width, 3000, outDir, file);
    }

    // The whole page as on screen, cut to the control (with a margin of the page around it), when asked.
    private static void SaveCrop(HiddenWindow main, Control panel, int width, int height, string? outDir, string file)
    {
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Point? at = panel.TranslatePoint(new Point(0, 0), main.Page);
        Assert.NotNull(at);
        Rect cut = new Rect(at!.Value, panel.Bounds.Size).Inflate(12).Intersect(new Rect(0, 0, width, height));
        using RenderTargetBitmap page = main.Render();
        PixelSize pixels = new((int)Math.Ceiling(cut.Width), (int)Math.Ceiling(cut.Height));
        using RenderTargetBitmap bitmap = new(pixels);
        using (DrawingContext context = bitmap.CreateDrawingContext())
            context.DrawImage(page, cut, new Rect(0, 0, pixels.Width, pixels.Height));
        bitmap.Save(Path.Combine(outDir, file));
    }

    [Fact]
    public async Task CopyToAnotherAxis_SendsTheValuesShown_InOneRun_InvertOnlyWhenTicked()
    {
        AxisTuningFakeEngine fake = new();
        fake.OtherAxes.Add(("Roll", "JOY_X"));
        fake.OtherAxes.Add(("Yaw", "JOY_RZ"));
        MainWindowViewModel vm = await AxisTuningTests.CreateLoadedVmAsync(fake);
        await AxisTuningTests.SelectScopeAsync(vm, "plane:DCS:F-16C_50");
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "12";
        vm.AxisTuningEditors.Single(e => e.Key == "saturationY").Text = "90";
        AxisParameterEditor curve = UserCurve(vm);
        curve.IsOn = true;
        curve.Points[5].Text = "30";
        vm.AxisTuningEditors.Single(e => e.Key == "invert").IsOn = true;

        vm.PrepareAxisTuningCopyCommand.Execute(null);
        Assert.Equal(["Roll", "Yaw"], vm.AxisTuningCopyTargets.Select(t => t.Relation).ToArray());
        Assert.DoesNotContain(vm.AxisTuningCopyTargets, t => t.Relation == "Pitch");
        Assert.StartsWith("Roll · ", vm.AxisTuningCopyTargets[0].Label);
        Assert.Contains("DCS / F-16C_50", vm.AxisTuningCopyText);
        vm.AxisTuningCopyTarget = vm.AxisTuningCopyTargets.Single(t => t.Relation == "Yaw");
        Assert.False(vm.AxisTuningCopyInvert);
        Assert.True(vm.CanCopyAxisTuning);

        fake.Calls.Clear();
        await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
        // One engine run (one profile version, one Ctrl+Z), for the other axis and the same scope, the unsaved values.
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set", "Yaw"));
        Assert.True(AxisTuningTests.Seq(set, "binds", "tuning", "set", "Yaw", "--scope", "plane:DCS:F-16C_50"));
        Assert.Equal("0.12", After(set, "--deadzone"));
        Assert.Equal("0.9", After(set, "--saturation-y"));
        Assert.Equal("false", After(set, "--slider"));
        Assert.Equal(11, After(set, "--curvature").Split(',').Length);
        Assert.Equal("0.3", After(set, "--curvature").Split(',')[5]);
        // Invert not ticked: the other axis keeps its own.
        Assert.DoesNotContain("--inverted", set);
        Assert.Contains("Ctrl+Z", vm.StatusText);
        Assert.Contains("'Yaw'", vm.StatusText);
        // This axis's values stay unsaved here (the copy saves nothing of its own).
        Assert.True(vm.AxisTuningHasEdits);

        vm.AxisTuningCopyInvert = true;
        fake.Calls.Clear();
        await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
        set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set", "Yaw"));
        Assert.Equal("true", After(set, "--inverted"));
        Assert.Contains("with invert", vm.StatusText);

        // A value that is no number: nothing is copied.
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "abc";
        Assert.False(vm.CanCopyAxisTuning);
        fake.Calls.Clear();
        await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "tuning", "set"));
    }

    [Theory]
    [InlineData("Thrust Left", "Thrust Right")]
    [InlineData("Thrust Right", "Thrust Left")]
    [InlineData("Brake L", "Brake R")]
    [InlineData("toe brake right", "toe brake left")]
    [InlineData("Pitch", "Brake R")]
    public void CopyTarget_IsTheOtherSideFirst(string current, string expected)
    {
        List<AxisTuningCopyTarget> targets = new[] { "Brake R", "Thrust Left", "Thrust Right", "toe brake left", "toe brake right" }
            .Where(t => t != current).Select(t => new AxisTuningCopyTarget(t, t)).ToList();
        Assert.Equal(expected, MainWindowViewModel.BestCopyTarget(current, targets)?.Relation);
    }

    [Fact]
    public async Task AxisSection_OpensByItselfForAnAxis_AndKeepsTheUsersClose()
    {
        AxisTuningFakeEngine fake = new();
        fake.OtherAxes.Add(("Roll", "JOY_X"));
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "axis-open-262.pr0file");
        Directory.CreateDirectory(Path.GetDirectoryName(vm.ProfilePath)!);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        Assert.False(vm.IsAxisTuningExpanded);

        // An axis: open at once, and loaded.
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        Assert.True(vm.IsAxisTuningExpanded);
        await WaitForAsync(() => vm.AxisTuningLoaded);
        Assert.True(vm.ListPrefs.AxisResponseOpen);

        // The user closes it: the next axis stays closed (and the next start: list-preferences.json).
        vm.IsAxisTuningExpanded = false;
        Assert.False(vm.ListPrefs.AxisResponseOpen);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Roll");
        Assert.False(vm.IsAxisTuningExpanded);
        // Opened again by the user: open for the next axis.
        vm.IsAxisTuningExpanded = true;
        Assert.True(vm.ListPrefs.AxisResponseOpen);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        Assert.True(vm.IsAxisTuningExpanded);
        await WaitForAsync(() => vm.AxisTuningLoaded && vm.SelectedAxisTuningScope != null);
    }

    [Fact]
    public void BindPagePanels_AreKeptOverARestart()
    {
        string file = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "list-preferences-262-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ListPreferences fresh = ListPreferences.Load(file);
            Assert.True(fresh.AxisResponseOpen);
            Assert.True(fresh.BindModifiersOpen);
            fresh.AxisResponseOpen = false;
            fresh.BindModifiersOpen = false;
            fresh.Save(file);
            ListPreferences again = ListPreferences.Load(file);
            Assert.False(again.AxisResponseOpen);
            Assert.False(again.BindModifiersOpen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // The "Modifiers on this bind" panel's close button: only its title line stays (with Show), remembered.
    [Fact]
    public void ModifiersPanel_HasACloseButton()
    {
        AvaloniaRenderThread.Run(() =>
        {
            AxisTuningFakeEngine fake = new();
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(AxisTuningTests.CreateLoadedVmAsync(fake));
            try
            {
                vm.CurrentStage = UiStage.Binds;
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 2000));
                Control Named(string name) => main.Page.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                Assert.True(Named("BindModifiersPanel").IsEffectivelyVisible);
                Button close = (Button)Named("BindModifiersClose");
                Assert.True(close.IsEffectivelyVisible);
                Assert.True(Named("BindModifiersBody").IsEffectivelyVisible);
                Assert.False(Named("BindModifiersShow").IsEffectivelyVisible);
                double open = Named("BindModifiersPanel").Bounds.Height;
                SaveCrop(main, Named("BindModifiersPanel"), 1280, 2000, RenderOut(), "modifiers-open-1280.png");

                close.Command!.Execute(close.CommandParameter);
                main.Settle();
                Assert.False(vm.IsBindModifiersOpen);
                Assert.False(vm.ListPrefs.BindModifiersOpen);
                Assert.False(Named("BindModifiersBody").IsEffectivelyVisible);
                Assert.False(close.IsEffectivelyVisible);
                Button show = (Button)Named("BindModifiersShow");
                Assert.True(show.IsEffectivelyVisible);
                Assert.True(Named("BindModifiersPanel").Bounds.Height < open / 2, "closed, the panel still takes " + Named("BindModifiersPanel").Bounds.Height);
                SaveCrop(main, Named("BindModifiersPanel"), 1280, 2000, RenderOut(), "modifiers-closed-1280.png");

                show.Command!.Execute(show.CommandParameter);
                main.Settle();
                Assert.True(vm.IsBindModifiersOpen);
                Assert.True(Named("BindModifiersBody").IsEffectivelyVisible);
            }
            finally
            {
                vm.CurrentStage = UiStage.Start;
            }
        });
    }

    private static string After(IReadOnlyList<string> args, string option)
    {
        int at = args.ToList().IndexOf(option);
        Assert.True(at >= 0 && at + 1 < args.Count, option + " is missing in " + string.Join(" ", args));
        return args[at + 1];
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static string? RenderOut()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_262_RENDER_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        return string.IsNullOrWhiteSpace(outDir) ? null : outDir;
    }
}

// Copy to another axis with the real engine: one engine run is one profile version, so one Ctrl+Z takes it back; the
// source axis keeps its values. Sets JOYPRO_DATA_DIR for the process (the profile history lives there): runs alone.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AxisTuningCopyRealEngineTests
{
    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private const string Device = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";

    [Fact]
    public async Task CopyToTheOtherThrottle_IsOneUndoStep()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "axiscopy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string data = Path.Combine(folder, "data");
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        try
        {
            DataDirEngineClient client = new(data);
            string profile = Path.Combine(folder, "throttles.pr0file");
            foreach (string[] command in new[]
                     {
                         new[] { "relations", "add", "Thrust Left", "--axis", "--node", "DCS:a2004cdnil:F-16C_50" },
                         new[] { "relations", "add", "Thrust Right", "--axis", "--node", "DCS:a2003cdnil:F-16C_50" },
                         new[] { "binds", "set", "Thrust Left", "--device", Device, "--input", "JOY_Z" },
                         new[] { "binds", "set", "Thrust Right", "--device", Device, "--input", "JOY_RZ" },
                         new[] { "binds", "tuning", "set", "Thrust Left", "--scope", "base", "--deadzone", "0.1", "--curvature", "0.25" }
                     })
            {
                EngineCommandResult run = await client.RunAsync(engine, ["--profile", profile, .. command]);
                Assert.True(run.ExitCode == 0, string.Join(" ", command) + ": " + run.StandardError + run.StandardOutput);
            }

            MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Thrust Left");
            await WaitLongAsync(() => vm.AxisTuningLoaded && vm.SelectedAxisTuningScope?.IsBase == true && !vm.IsBusy);
            Assert.True(vm.IsAxisTuningExpanded);
            Assert.Equal(0.1, vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Value, 6);
            // An edit goes along (the curve's edits are saved by themselves, too).
            vm.AxisTuningEditors.Single(e => e.Key == "saturationY").Text = "80";
            vm.PrepareAxisTuningCopyCommand.Execute(null);
            Assert.Equal("Thrust Right", vm.AxisTuningCopyTarget?.Relation);
            await vm.FlushAutosaveAsync();

            // Saves within two seconds of each other are one version (ProfileHistory.CoalesceSeconds): the last one
            // before the copy is older than that.
            await Task.Delay(2500);
            int versions = JoyPro.History.ProfileHistory.Read(profile).Versions.Count;
            await vm.CopyAxisTuningToAxisCommand.ExecuteAsync(null);
            Assert.Contains("Ctrl+Z", vm.StatusText);
            var history = JoyPro.History.ProfileHistory.Read(profile).Versions;
            Assert.True(versions + 1 == history.Count, "versions: " + string.Join(" | ", history.Select(v => v.Id + " " + v.Label)));
            Assert.Equal("Copying the axis response", history[^1].Label);
            AxisTuning right = await BaseAsync(client, engine, profile, "Thrust Right");
            Assert.Equal(0.1, right.Deadzone, 6);
            Assert.Equal(0.8, right.SaturationY, 6);
            Assert.Equal(0.25, right.Curvature[0], 6);
            AxisTuning left = await BaseAsync(client, engine, profile, "Thrust Left");
            Assert.Equal(0.1, left.Deadzone, 6);

            // One Ctrl+Z: the other throttle is as before; this one keeps its own values.
            await vm.UndoLastChangeCommand.ExecuteAsync(null);
            right = await BaseAsync(client, engine, profile, "Thrust Right");
            Assert.Equal(0.0, right.Deadzone, 6);
            Assert.Equal(1.0, right.SaturationY, 6);
            Assert.Equal(0.0, right.Curvature.Count == 0 ? 0.0 : right.Curvature[0], 6);
            left = await BaseAsync(client, engine, profile, "Thrust Left");
            Assert.Equal(0.1, left.Deadzone, 6);
            Assert.Equal(0.8, left.SaturationY, 6);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            await RelationBuilderTests.SettleAllAsync();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // The relation's base tuning as the engine has it in the profile file.
    private static async Task<AxisTuning> BaseAsync(IEngineCliClient client, string engine, string profile, string relation)
    {
        EngineCommandResult show = await client.RunAsync(engine, ["--profile", profile, "binds", "tuning", "show", relation]);
        Assert.True(show.ExitCode == 0, show.StandardError);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(show.StandardOutput[show.StandardOutput.IndexOf('{')..]);
        return AxisTuning.Parse(doc.RootElement.GetProperty("Scopes").GetProperty("base").GetString() ?? string.Empty);
    }

    private static async Task WaitLongAsync(Func<bool> condition)
    {
        for (int i = 0; i < 600 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
