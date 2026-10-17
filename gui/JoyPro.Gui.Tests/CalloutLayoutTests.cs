using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tables and lines (docs/device-reference.md, "Tables and lines"): the automatic callout layout (CalloutLayouter,
// shared with the engine) on JoyPro's drawings of every kind of device and on the curated photo position sets, the
// editing of a sheet (CalloutSheetViewModel) and the sheet on the Layout page with binding by drag and drop.
[SupportedOSPlatform("windows")]
public sealed class CalloutLayoutTests
{
    private static DeviceCatalog Catalog => DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);

    private static (DeviceSchematic Schematic, Dictionary<string, double[]> Known) Drawing(DeviceCatalog catalog, RefDevice model)
    {
        DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
        return (schematic, schematic.Markers.ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase));
    }

    private static void AssertClean(CalloutSheet sheet, string name)
    {
        CalloutCheck check = CalloutLayouter.Check(sheet);
        Assert.True(check.Overlaps == 0, name + ": " + string.Join("; ", check.Problems));
        Assert.True(check.NonOrthogonal == 0, name + ": slanted line");
        Assert.True(check.Outside == 0, name + ": outside the canvas");
        Assert.True(check.LinesThroughTables == 0, name + ": a line runs through a table");
        Assert.True(check.Linked == check.Positioned, name + ": a positioned part has no line");
        // Every segment of every line is horizontal or vertical (also checked by the check, here by hand).
        foreach (CalloutLine line in sheet.Lines)
        {
            for (int i = 0; i + 1 < line.Points.Count; i++)
                Assert.True(Math.Abs(line.Points[i][0] - line.Points[i + 1][0]) < 0.1 || Math.Abs(line.Points[i][1] - line.Points[i + 1][1]) < 0.1, name);
        }
    }

    // ------------------------------------------------------------------ the automatic layout

    // JoyPro's drawings of sticks, throttles, panels, pedals, a HOTAS, an MFD, a yoke and a generic button box.
    [Theory]
    [InlineData("thrustmaster-warthog-stick", 1)]
    [InlineData("thrustmaster-hotas-warthog-throttle", 8)]
    [InlineData("winwing-f18-takeoff-panel", 10)]
    [InlineData("logitech-x52-pro", 12)]
    public void Drawings_GetTablesWithoutOverlap_OrthogonalLines_FewCrossings(string modelId, int maxCrossings)
    {
        DeviceCatalog catalog = Catalog;
        RefDevice model = catalog.Find(modelId)!;
        (DeviceSchematic schematic, Dictionary<string, double[]> known) = Drawing(catalog, model);
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, known, schematic.Width, schematic.Height, null);

        AssertClean(sheet, modelId);
        CalloutCheck check = CalloutLayouter.Check(sheet);
        Assert.True(check.Crossings <= maxCrossings, modelId + ": " + check.Crossings + " crossings");
        // Every part with an input has its table, with a line from its spot.
        Assert.Equal(schematic.Markers.Count(m => m.HasInputs), sheet.Tables.Sum(t => t.Parts.Count));
        Assert.All(sheet.Tables, t => Assert.NotNull(sheet.LineOf(t.Id)));
        // The picture is not covered and sits inside the grown canvas.
        Assert.True(sheet.PictureX > 0 && sheet.PictureY > 0 && sheet.PictureX + sheet.PictureWidth < sheet.Width && sheet.PictureY + sheet.PictureHeight < sheet.Height);
    }

    // Every kind of device the catalog has (all models of the kinds) and a generic button box: clean sheets.
    [Theory]
    [InlineData("stick")]
    [InlineData("throttle")]
    [InlineData("panel")]
    [InlineData("pedals")]
    [InlineData("hotas")]
    [InlineData("mfd")]
    [InlineData("yoke")]
    [InlineData("quadrant")]
    public void EveryModelOfAKind_GetsACleanSheet(string kind)
    {
        DeviceCatalog catalog = Catalog;
        List<RefDevice> models = catalog.Devices.Where(d => d.Kind == kind && d.AllControls.Any(c => c.Input.Length > 0)).ToList();
        Assert.NotEmpty(models);
        foreach (RefDevice model in models)
        {
            (DeviceSchematic schematic, Dictionary<string, double[]> known) = Drawing(catalog, model);
            AssertClean(CalloutLayouter.Arrange(schematic, known, schematic.Width, schematic.Height, null), model.Id);
        }
    }

    [Fact]
    public void GenericButtonBox_GetsAButtonBankGrid()
    {
        RefDevice box = DeviceGeneric.Model(new DeviceProbe { InstanceName = "Home Cockpit Box", Buttons = 12, Hats = 1, Axes = ["JOY_X"] }, "buttonbox");
        DeviceSchematic schematic = SchematicBuilder.Build(box, null);
        Dictionary<string, double[]> known = schematic.Markers.ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, known, schematic.Width, schematic.Height, null);
        AssertClean(sheet, "button box");
        Assert.Equal(schematic.Markers.Count(m => m.HasInputs), sheet.Tables.Sum(t => t.Parts.Count));
    }

    // The two curated photo sets: their parts get tables with lines; the parts the set does not place get a table in
    // "Other controls" without a line.
    [Theory]
    [InlineData("winwing-f18-takeoff-panel.main.json", 1400, 933)]
    [InlineData("vkb-gunfighter-mcg-pro.main.json", 1400, 1400)]
    public void CuratedPhotos_LinkThePlacedParts_AndListTheOthers(string file, double width, double height)
    {
        DeviceCatalog catalog = Catalog;
        string folder = DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog);
        DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(Path.Combine(folder, file)), file);
        RefDevice model = catalog.Find(set.Model)!;
        DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, set.Markers, width, height, null);

        AssertClean(sheet, file);
        List<CalloutTable> placed = sheet.Tables.Where(t => t.Known).ToList();
        Assert.Equal(set.Markers.Keys.Count(k => schematic.Markers.Any(m => m.Id == k && m.HasInputs)), placed.Sum(t => t.Parts.Count));
        Assert.All(placed, t => Assert.NotNull(sheet.LineOf(t.Id)));
        List<CalloutTable> other = sheet.Tables.Where(t => !t.Known).ToList();
        Assert.All(other, t => Assert.Null(sheet.LineOf(t.Id)));
        Assert.All(other, t => Assert.Equal("other", t.Side));
        if (other.Count > 0)
            Assert.True(other.Min(t => t.Y) > sheet.OtherY && sheet.OtherY > sheet.PictureY + sheet.PictureHeight);
    }

    [Fact]
    public void Groups_HatHasFiveRowsInJoyPro1Order_AxesAndButtonsTheirOwn()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceSchematic schematic, Dictionary<string, double[]> known) = Drawing(catalog, catalog.Find("thrustmaster-warthog-stick")!);
        List<CalloutTable> tables = CalloutLayouter.BuildTables(schematic, known);
        CalloutTable cms = tables.Single(t => t.Title.Contains("CMS", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["button", "up", "right", "left", "down"], cms.Rows.Select(r => r.Icon).ToArray());
        CalloutTable stick = tables.Single(t => t.Rows.Any(r => r.Input == "JOY_X"));
        Assert.All(stick.Rows, r => Assert.Equal("axis", r.Icon));
        Assert.Contains(tables, t => t.Rows.Count == 1 && t.Rows[0].Input == "JOY_BTN2");
    }

    [Fact]
    public void Sheet_RoundTripsThroughTheShareableLayoutFormat()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceSchematic schematic, Dictionary<string, double[]> known) = Drawing(catalog, catalog.Find("thrustmaster-hotas-warthog-throttle")!);
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, known, schematic.Width, schematic.Height, null);
        sheet.Tables[0].Pinned = true;
        sheet.Tables[1].Zones = 3;
        sheet.Modifier = "Shift";
        DeviceLayoutSet set = new() { Model = "thrustmaster-hotas-warthog-throttle", PhotoKind = "drawing", Markers = known, Callouts = sheet, LocalPhotoFile = @"C:\x.png" };
        string shared = set.ToJson();
        Assert.DoesNotContain("x.png", shared, StringComparison.Ordinal);
        Assert.Contains("x.png", set.ToJson(includeLocal: true), StringComparison.Ordinal);
        DeviceLayoutSet back = DeviceLayoutSet.Parse(shared, "t.json");
        Assert.NotNull(back.Callouts);
        Assert.Equal(sheet.ToJson(), back.Callouts!.ToJson());
        Assert.True(back.Callouts.Tables[0].Pinned);
        Assert.Equal(3, back.Callouts.Tables[1].Zones);
        Assert.Equal("Shift", back.Callouts.Modifier);
    }

    // ------------------------------------------------------------------ key grids and shipped sheets

    private static (DeviceLayoutSet Set, DeviceSchematic Schematic) Shipped(DeviceCatalog catalog, string file)
    {
        string folder = DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog);
        DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(Path.Combine(folder, file)), file);
        RefDevice model = catalog.Find(set.Model)!;
        return (set, SchematicBuilder.Build(model, catalog.BaseOf(model)));
    }

    // Keypads get ONE grid table per block of keys, laid out like the keys, with ONE line each; names do not matter.
    [Theory]
    [InlineData("winwing-pfp-4.main.json", 60)]
    [InlineData("winwing-pfp-3n.main.json", 60)]
    [InlineData("winwing-pfp-7.main.json", 60)]
    [InlineData("winwing-mcdu-32.main.json", 55)]
    [InlineData("winwing-f16-icp.main.json", 15)]
    [InlineData("winwing-pdc-3m.main.json", 15)]
    [InlineData("winwing-pap3.main.json", 10)]
    [InlineData("virpil-sharka-50-control-panel.sheet.json", 40)]
    public void Keypads_GetGridTables_MirroringTheKeys(string file, int minGridded)
    {
        DeviceCatalog catalog = Catalog;
        (DeviceLayoutSet set, DeviceSchematic schematic) = Shipped(catalog, file);
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, set.Markers, set.Callouts!.PictureWidth, set.Callouts.PictureHeight, null);
        AssertClean(sheet, file);
        List<CalloutTable> grids = sheet.Tables.Where(t => t.IsGrid).ToList();
        Assert.True(grids.Sum(t => t.Parts.Count) >= minGridded, file + ": " + grids.Sum(t => t.Parts.Count) + " keys in grids");
        // JoyPro's shipped sheet of the keypad (arranged, then a few tables pinned by rule) is a good one.
        CalloutCheck check = CalloutLayouter.Check(set.Callouts);
        Assert.True(check.Ok && check.Crossings <= 4 && check.Shared == 0, file + ": " + check);
        Assert.Contains(set.Callouts.Tables, t => t.IsGrid);
        foreach (CalloutTable grid in grids)
        {
            Assert.NotNull(sheet.LineOf(grid.Id));
            // Every cell holds one key; a key's lines (a switch's up and down) share its cell.
            Assert.Equal(grid.Parts.Count, grid.Rows.Select(r => (r.GridRow, r.GridCol)).Distinct().Count());
            Assert.All(grid.Rows.GroupBy(r => r.Part), g => Assert.Single(g.Select(r => (r.GridRow, r.GridCol)).Distinct()));
            // Down a column the keys go down on the picture.
            foreach (IGrouping<int, CalloutRow> column in grid.Rows.Where(r => r.Sub == 0).GroupBy(r => r.GridCol))
            {
                List<double> ys = column.OrderBy(r => r.GridRow).Select(r => set.Markers[r.Part][1]).ToList();
                for (int i = 1; i < ys.Count; i++)
                    Assert.True(ys[i] > ys[i - 1] - 0.01, file + " " + grid.Title + ": column " + column.Key + " is not top to bottom");
            }
        }
    }

    [Fact]
    public void LineSelectKeys_AreOneColumnInTheirOrder()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceLayoutSet set, DeviceSchematic schematic) = Shipped(catalog, "winwing-pfp-4.main.json");
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, set.Markers, set.Callouts!.PictureWidth, set.Callouts.PictureHeight, null);
        CalloutTable left = sheet.Tables.Single(t => t.IsGrid && t.Title.Contains("1L", StringComparison.Ordinal));
        Assert.Equal(1, left.Columns);
        Assert.Equal(6, left.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 6), left.Rows.Select(r => r.GridRow));
        Assert.Equal(left.Rows.Select(r => r.Label), left.Rows.Select(r => r.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.Equal(sheet.TitleHeight + 6 * sheet.RowHeight, sheet.HeightOf(left), 3);
        Assert.True(left.Known);
    }

    // Parts on one spot (a knob's push, pull and turn) share one table and one line.
    [Fact]
    public void PartsOnOneSpot_ShareOneTable()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceLayoutSet set, DeviceSchematic schematic) = Shipped(catalog, "winwing-fcu.main.json");
        CalloutSheet sheet = CalloutLayouter.Arrange(schematic, set.Markers, set.Callouts!.PictureWidth, set.Callouts.PictureHeight, null);
        List<CalloutTable> stacks = sheet.Tables.Where(t => t.Id.StartsWith("stack:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(stacks);
        Assert.All(stacks, t => Assert.True(t.Parts.Count >= 2 && !t.IsGrid && sheet.LineOf(t.Id) != null));
        AssertClean(sheet, "fcu");
    }

    // The sheets JoyPro ships: clean, and no poor one (at most 12 crossings, no shared tracks beyond that).
    [Fact]
    public void ShippedSheets_AreCleanAndMostlyGood()
    {
        string folder = DevicePhotoDb.LayoutFolderOf(DeviceReferenceTests.ShippedCatalog);
        int good = 0, all = 0;
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(file), file);
            if (set.Callouts == null)
                continue;
            all++;
            CalloutCheck check = CalloutLayouter.Check(set.Callouts);
            Assert.True(check.Ok, Path.GetFileName(file) + ": " + check + " " + string.Join("; ", check.Problems));
            Assert.True(check.Crossings <= 12, Path.GetFileName(file) + ": " + check.Crossings + " crossings");
            if (check.Crossings <= 4 && check.Shared == 0)
                good++;
        }
        Assert.True(all >= 290, all + " shipped sheets");
        Assert.True(good >= all - 8, good + " of " + all + " shipped sheets are good");
    }

    [Fact]
    public void Grid_SplitsIntoSingleTables_AndGroupsAgain_DropOnACellBinds()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceLayoutSet set, DeviceSchematic schematic) = Shipped(catalog, "winwing-pfp-4.main.json");
        CalloutSheetViewModel vm = new();
        vm.Show(schematic, set.Markers, set.Callouts!.PictureWidth, set.Callouts.PictureHeight, null);
        CalloutTable grid = vm.Sheet!.Tables.First(t => t.IsGrid && t.Parts.Count > 10);
        int before = vm.Sheet.Tables.Count;

        // A relation dropped on a grid cell binds that key.
        List<CalloutBindRequest> requests = [];
        vm.BindRequested += (_, r) => requests.Add(r);
        double[] cell = vm.Sheet.CellOf(grid, 5, false);
        Assert.True(vm.RowAt(cell[0] + 4, cell[1] + 4, out CalloutTable? hit, out int row, out bool modifier));
        Assert.Same(grid, hit);
        Assert.Equal(5, row);
        Assert.False(modifier);
        Assert.True(vm.DropRelation(grid, row, false, "Trim Up"));
        Assert.Equal(grid.VisibleRows()[5].Input, Assert.Single(requests).Input);

        // Moved and pinned like any table.
        vm.MoveTable(grid, 20, 20);
        Assert.True(vm.Sheet!.Table(grid.Id)!.Pinned);

        vm.SplitGroup(vm.Sheet.Table(grid.Id)!);
        Assert.Null(vm.Sheet!.Table(grid.Id));
        Assert.Equal(before - 1 + grid.Parts.Count, vm.Sheet.Tables.Count);
        Assert.All(grid.Parts, p => Assert.Contains(vm.Sheet.Tables, t => t.Parts.Count == 1 && t.Parts[0] == p));
        Assert.True(vm.HasSplitGroups);
        vm.AutoArrange();
        Assert.Null(vm.Sheet!.Table(grid.Id));
        AssertClean(vm.Sheet, "split");

        vm.Regroup();
        Assert.NotNull(vm.Sheet!.Table(grid.Id));
        Assert.False(vm.HasSplitGroups);
        vm.Undo();
        Assert.Null(vm.Sheet!.Table(grid.Id));
    }

    // JoyPro's sheet of a full-size photo comes back for the photo decoded smaller: scaled, its pins kept.
    [Fact]
    public void ShippedSheet_IsScaledToTheShownPictureSize()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceLayoutSet set, DeviceSchematic schematic) = Shipped(catalog, "winwing-fcu.main.json");
        CalloutSheet shipped = set.Callouts!;
        double width = 1400, height = 1400 * shipped.PictureHeight / shipped.PictureWidth;
        CalloutSheetViewModel vm = new();
        vm.Show(schematic, set.Markers, width, height, shipped);
        Assert.Equal(width, vm.Sheet!.PictureWidth, 3);
        Assert.Equal(shipped.Tables.Select(t => t.Id), vm.Sheet.Tables.Select(t => t.Id));
        Assert.Equal(shipped.Tables.Count(t => t.Pinned), vm.Sheet.Tables.Count(t => t.Pinned));
        Assert.Equal(shipped.Tables[0].X * width / shipped.PictureWidth, vm.Sheet.Tables[0].X, 1);
        Assert.False(vm.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ editing

    private static CalloutSheetViewModel EditableStick(out DeviceSchematic schematic, out Dictionary<string, double[]> known)
    {
        DeviceCatalog catalog = Catalog;
        (schematic, known) = Drawing(catalog, catalog.Find("thrustmaster-warthog-stick")!);
        CalloutSheetViewModel vm = new();
        vm.Show(schematic, known, schematic.Width, schematic.Height, null);
        return vm;
    }

    [Fact]
    public void MovedTable_IsPinned_AndAutoArrangeKeepsIt_UndoRedo()
    {
        CalloutSheetViewModel vm = EditableStick(out _, out _);
        CalloutSheet sheet = vm.Sheet!;
        CalloutTable table = sheet.Tables[0];
        string before = sheet.ToJson();
        double relX = 40 - sheet.PictureX, relY = 900 - sheet.PictureY;

        vm.MoveTable(table, 40, 900);
        Assert.True(table.Pinned);
        Assert.True(vm.HasUnsavedChanges);
        Assert.NotNull(vm.Sheet!.LineOf(table.Id));

        vm.AutoArrange();
        CalloutTable kept = vm.Sheet!.Table(table.Id)!;
        Assert.True(kept.Pinned);
        // Kept where the user put it, relative to the picture.
        Assert.Equal(relX, kept.X - vm.Sheet.PictureX, 0);
        Assert.Equal(relY, kept.Y - vm.Sheet.PictureY, 0);
        Assert.All(vm.Sheet.Lines, l => Assert.True(l.Points.Count >= 2));

        vm.Undo();
        vm.Undo();
        Assert.Equal(before, vm.Sheet!.ToJson());
        Assert.False(vm.CanUndo);
        vm.Redo();
        Assert.True(vm.Sheet!.Table(table.Id)!.Pinned);
    }

    [Fact]
    public void Lines_Bend_Delete_Reroute_StayOrthogonal()
    {
        CalloutSheetViewModel vm = EditableStick(out _, out _);
        CalloutLine line = vm.Sheet!.Lines.First(l => l.Points.Count >= 3);
        string table = line.Table;
        vm.BendLine(line, 1, 30);
        CalloutLine bent = vm.Sheet!.LineOf(table)!;
        Assert.True(bent.Manual);
        Assert.Equal(0, CalloutLayouter.Check(vm.Sheet).NonOrthogonal);
        // The ends stay on the spot and on the table.
        Assert.Equal(vm.Sheet.SpotOf(vm.Sheet.Table(table)!)[0], bent.Points[0][0], 1);

        vm.DeleteLine(bent);
        Assert.Null(vm.Sheet.LineOf(table));
        Assert.True(vm.Sheet.Table(table)!.NoLine);
        vm.AutoArrange();
        Assert.Null(vm.Sheet!.LineOf(table));

        vm.RerouteLine(vm.Sheet.Table(table)!);
        Assert.NotNull(vm.Sheet.LineOf(table));
        Assert.False(vm.Sheet.Table(table)!.NoLine);
        Assert.Equal(0, CalloutLayouter.Check(vm.Sheet).NonOrthogonal);
    }

    [Fact]
    public void Rows_Columns_AndWidth_Change()
    {
        CalloutSheetViewModel vm = EditableStick(out _, out _);
        CalloutSheet sheet = vm.Sheet!;
        CalloutTable axis = sheet.Tables.First(t => t.Rows.Any(r => r.Icon == "axis"));
        int rows = axis.VisibleRows().Count;
        vm.ChangeRows(axis, 1);
        vm.ChangeRows(axis, 1);
        Assert.Equal(rows + 2, axis.VisibleRows().Count);
        Assert.Equal("zone", axis.VisibleRows().Last().Icon);
        Assert.Equal("50-100 %", axis.VisibleRows().Last().Label);

        CalloutTable hat = sheet.Tables.First(t => t.Rows.Count == 5);
        vm.ChangeRows(hat, -1);
        Assert.Equal(4, hat.VisibleRows().Count);
        vm.ChangeRows(hat, 1);
        Assert.Equal(5, hat.VisibleRows().Count);

        double width = sheet.WidthOf(hat);
        vm.SetModifierColumn(hat, false);
        Assert.Equal(0, sheet.ModColumnWidth(hat));
        Assert.True(sheet.WidthOf(hat) < width);
        vm.ResizeTable(hat, 700);
        Assert.Equal(700, sheet.WidthOf(hat), 0);
        vm.AutoArrange();
        Assert.Equal(700, vm.Sheet!.WidthOf(vm.Sheet.Table(hat.Id)!), 0);
    }

    // A part without a known position: its table is in "Other controls"; a line dragged to a spot places the part.
    [Fact]
    public void LineDraggedToASpot_PlacesAnUnknownPart()
    {
        DeviceCatalog catalog = Catalog;
        (DeviceSchematic schematic, Dictionary<string, double[]> known) = Drawing(catalog, catalog.Find("thrustmaster-warthog-stick")!);
        string missing = schematic.Markers.First(m => m.Inputs.Any(i => i.Input == "JOY_BTN2")).Id;
        known.Remove(missing);
        CalloutSheetViewModel vm = new();
        vm.Show(schematic, known, schematic.Width, schematic.Height, null);
        CalloutTable table = vm.Sheet!.Table(missing)!;
        Assert.False(table.Known);
        Assert.Equal("other", table.Side);
        Assert.Null(vm.Sheet.LineOf(missing));

        (string Part, double X, double Y)? placed = null;
        vm.PartPlaced += (_, p) => placed = p;
        double x = vm.Sheet.PictureX + vm.Sheet.PictureWidth * 0.3, y = vm.Sheet.PictureY + vm.Sheet.PictureHeight * 0.2;
        vm.AddLine(table, x, y);

        Assert.NotNull(placed);
        Assert.Equal(missing, placed!.Value.Part);
        Assert.Equal(0.3, placed.Value.X, 3);
        CalloutTable now = vm.Sheet!.Table(missing)!;
        Assert.True(now.Known);
        Assert.NotEqual("other", now.Side);
        Assert.NotNull(vm.Sheet.LineOf(missing));
        AssertClean(vm.Sheet, "after placing");
    }

    [Fact]
    public void DroppedRelation_AsksToBindThatInput_ModifierColumnToo()
    {
        CalloutSheetViewModel vm = EditableStick(out _, out _);
        List<CalloutBindRequest> requests = [];
        vm.BindRequested += (_, r) => requests.Add(r);
        CalloutTable hat = vm.Sheet!.Tables.First(t => t.Rows.Count == 5 && t.ShowModifier);
        Assert.True(vm.DropRelation(hat, 1, false, "Trim Up"));
        Assert.True(vm.DropRelation(hat, 1, true, "Radar Up"));
        vm.ClearRow(hat, 1, false);
        Assert.Equal(3, requests.Count);
        Assert.Equal(hat.VisibleRows()[1].Input, requests[0].Input);
        Assert.False(requests[0].Modifier);
        Assert.Equal("Trim Up", requests[0].Relation);
        Assert.True(requests[1].Modifier);
        Assert.Null(requests[2].Relation);
        // The canvas finds the row under the pointer.
        double[] cell = vm.Sheet.CellOf(hat, 1, true);
        Assert.True(vm.RowAt(cell[0] + 3, cell[1] + 3, out CalloutTable? hit, out int row, out bool modifier));
        Assert.Same(hat, hit);
        Assert.Equal(1, row);
        Assert.True(modifier);
    }

    // A saved sheet of the same picture comes back as it was; a part that got a position later leaves "Other".
    [Fact]
    public void SavedSheet_IsShownAgain_AndFollowsNewPositions()
    {
        CalloutSheetViewModel vm = EditableStick(out DeviceSchematic schematic, out Dictionary<string, double[]> known);
        vm.MoveTable(vm.Sheet!.Tables[2], 30, 30);
        CalloutSheet saved = vm.Sheet!.Clone();
        CalloutSheetViewModel again = new();
        again.Show(schematic, known, schematic.Width, schematic.Height, saved);
        Assert.Equal(saved.ToJson(), again.Sheet!.ToJson());
        Assert.False(again.HasUnsavedChanges);

        string part = again.Sheet.Tables[0].Parts[0];
        Dictionary<string, double[]> moved = new(known, StringComparer.OrdinalIgnoreCase) { [part] = [0.5, 0.5] };
        again.UpdatePositions(moved);
        Assert.Equal(0.5, again.Sheet!.Table(again.Sheet.Tables[0].Id)!.SpotX, 3);
        Assert.True(again.HasUnsavedChanges);
    }
}
