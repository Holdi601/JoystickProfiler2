using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.DeviceReference;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Modifier combinations on the callout tables (docs/device-reference.md, "Modifier combinations"): with n modifiers an
// input can do 2^n things, the tables get one column per combination (the ones in use by default, all of them on
// request, capped), each cell shows the relation bound under exactly those modifiers, and a relation dropped on a cell
// binds it with exactly those modifiers.
[SupportedOSPlatform("windows")]
public sealed class ModifierCombinationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void AllCombinations_GiveTwoToTheNColumns(int modifiers, int columns)
    {
        string[] names = ["Shift A", "Shift B", "Shift C"];
        List<string> combos = CalloutCombos.Choose(CalloutCombos.All, names.Take(modifiers), [], 0, string.Empty);
        Assert.Equal(columns, combos.Count + 1);
        Assert.Equal(combos.Count, combos.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The sheet: one main column plus one per combination (the old single modifier column without modifiers).
        CalloutSheet sheet = new() { Combos = combos };
        CalloutTable table = new() { Id = "t", Rows = [new CalloutRow { Input = "JOY_BTN1" }] };
        table.Width = sheet.DefaultWidth(table);
        Assert.Equal(Math.Max(1, combos.Count), sheet.ModColumnCount(table));
        // Each column has its own cell side by side; the hit test finds it again.
        for (int c = 0; c <= sheet.ModColumnCount(table); c++)
        {
            double[] cell = sheet.CellOf(table, 0, c);
            Assert.True(cell[2] > 0);
            Assert.True(sheet.HitRow(table, cell[0] + cell[2] / 2, cell[1] + cell[3] / 2, out int row, out int column));
            Assert.Equal(0, row);
            Assert.Equal(c, column);
        }
    }

    [Fact]
    public void Combinations_AreOrderedFewestFirst_AndKeyedByName()
    {
        Assert.Equal(["Shift A", "Shift B", "Shift A+Shift B"], CalloutCombos.AllOf(["Shift B", "Shift A"], 0));
        Assert.Equal("shift a+Shift B", CalloutCombos.Key(["Shift B", "shift a"]));
        Assert.True(CalloutCombos.Same("Shift B,Shift A", "Shift A+Shift B"));
        Assert.True(CalloutCombos.Same("<none>", string.Empty));
        Assert.False(CalloutCombos.Same("Shift A", "Shift A+Shift B"));
        Assert.Equal("—", CalloutCombos.Label(string.Empty));
        Assert.Equal("Shift A + Shift B", CalloutCombos.Label("Shift A+Shift B"));
    }

    [Fact]
    public void UsedCombinations_AreTheBindsOnes_MostUsedWhenCapped_OrThePickedOne()
    {
        string[] binds = ["", "Shift A", "Shift A", "Shift B,Shift A", "Pinky", "", "Shift A"];
        Assert.Equal(["Pinky", "Shift A", "Shift A+Shift B"], CalloutCombos.Choose(CalloutCombos.Used, ["Shift A", "Shift B", "Pinky"], binds, 0, string.Empty));
        // At most 2 columns: the unmodified one and the most used combination.
        Assert.Equal(["Shift A"], CalloutCombos.Choose(CalloutCombos.Used, ["Shift A", "Shift B", "Pinky"], binds, 2, string.Empty));
        // No modifier binds: no combination column (the table looks as before).
        Assert.Empty(CalloutCombos.Choose(string.Empty, ["Shift A"], ["", ""], 0, string.Empty));
        // A picked combination alone.
        Assert.Equal(["Shift A+Shift B"], CalloutCombos.Choose(CalloutCombos.All, ["Shift A", "Shift B"], binds, 0, "Shift B+Shift A"));
        // All combinations of 4 modifiers capped at 8 columns.
        Assert.Equal(7, CalloutCombos.Choose(CalloutCombos.All, ["A", "B", "C", "D"], [], 0, string.Empty).Count);
    }

    [Fact]
    public void Sheet_KeepsItsCombinations_InTheFile_AndOldSheetsHaveOneModifierColumn()
    {
        CalloutSheet old = CalloutSheet.Parse("{\"version\":1,\"tables\":[{\"id\":\"t\",\"rows\":[{\"input\":\"JOY_BTN1\"}]}],\"lines\":[]}");
        Assert.Empty(old.Combos);
        CalloutTable t = old.Tables[0];
        Assert.Equal(1, old.ModColumnCount(t));
        Assert.Null(old.ComboOfColumn(1));
        Assert.Equal(string.Empty, old.ComboOfColumn(0));

        CalloutSheet sheet = new() { ComboMode = CalloutCombos.All, MaxColumns = 4, Combos = ["Shift A", "Shift B", "Shift A+Shift B"] };
        CalloutSheet back = CalloutSheet.Parse(sheet.ToJson());
        Assert.Equal(sheet.Combos, back.Combos);
        Assert.Equal(CalloutCombos.All, back.ComboMode);
        Assert.Equal(4, back.MaxColumns);
        Assert.Equal("Shift A+Shift B", back.ComboOfColumn(3));
    }

    // Widths: more combinations make wider tables (narrower columns each), the layouter arranges them without overlap.
    [Fact]
    public void CalloutSheetViewModel_SetCombos_WidensTheTables_AndArrangesAgain()
    {
        DeviceCatalog catalog = DeviceCatalog.Load(DeviceReferenceTests.ShippedCatalog);
        RefDevice model = catalog.Find("thrustmaster-warthog-stick")!;
        DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
        Dictionary<string, double[]> known = schematic.Markers.ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);
        CalloutSheetViewModel vm = new() { TextOfColumn = (input, column) => input == "JOY_BTN1" ? "col" + column : string.Empty };
        vm.Show(schematic, known, schematic.Width, schematic.Height, null);
        CalloutTable list = vm.Sheet!.Tables.First(t => !t.IsGrid && t.ShowModifier);
        double before = vm.Sheet.WidthOf(list);

        Assert.True(vm.SetCombos(CalloutCombos.AllOf(["Shift A", "Shift B", "Shift C"], 0)));
        CalloutTable after = vm.Sheet!.Table(list.Id)!;
        Assert.Equal(7, vm.Sheet.ModColumnCount(after));
        Assert.True(vm.Sheet.WidthOf(after) > before);
        CalloutCheck check = CalloutLayouter.Check(vm.Sheet);
        Assert.True(check.Overlaps == 0, string.Join("; ", check.Problems));
        Assert.False(vm.SetCombos(CalloutCombos.AllOf(["Shift A", "Shift B", "Shift C"], 0)));

        // The cell texts come per column; a drop binds with the column.
        CalloutTable trigger = vm.Sheet.TableOfInput("JOY_BTN1")!;
        int row = trigger.VisibleRows().FindIndex(r => r.Input == "JOY_BTN1");
        Assert.Equal("col3", vm.RowText(trigger, trigger.VisibleRows()[row], 3));
        List<CalloutBindRequest> requests = [];
        vm.BindRequested += (_, r) => requests.Add(r);
        Assert.True(vm.DropRelation(trigger, row, 4, "Gear"));
        Assert.Equal(4, Assert.Single(requests).ColumnIndex);
        Assert.Equal("Shift A+Shift B", vm.Sheet.ComboOfColumn(4));
    }
}
