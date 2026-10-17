using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.ViewModels;

// A relation dropped on (or cleared from) a row of a callout table: bind it to that input, with the modifiers of the
// column (0 = none, 1.. = the sheet's modifier combinations, CalloutSheet.ComboOfColumn). Relation null = free the input.
public sealed record CalloutBindRequest(string Input, bool Modifier, string? Relation, string Label, int Column = -1)
{
    public int ColumnIndex => Column >= 0 ? Column : Modifier ? 1 : 0;
}

// The callout sheet of the Layout page (docs/device-reference.md, "Tables and lines"): the device picture with a table
// per control group and orthogonal leader lines, as JoyPro 1's cheat sheets. It is arranged automatically
// (CalloutLayouter, the same code as the engine's "layout callouts" and cheat sheets) and every part can be edited:
// tables moved (then pinned), resized, rows and the modifier column changed, lines bent, removed, re-routed or added by
// dragging from a table to a spot on the picture. Every edit can be undone. Rows show what is bound (TextOf); a relation
// dropped on a row asks the host to bind it (BindRequested).
public sealed partial class CalloutSheetViewModel : ObservableObject
{
    private const int UndoLimit = 200;
    private readonly List<(string Description, string Json)> undo = [];
    private readonly List<(string Description, string Json)> redo = [];
    private DeviceSchematic? schematic;
    private Dictionary<string, double[]> known = new(StringComparer.OrdinalIgnoreCase);

    public CalloutSheet? Sheet { get; private set; }

    // What a row shows: (input, modifier column) -> text; "" = nothing bound.
    public Func<string, bool, string>? TextOf { get; set; }

    // What a row shows per column (0 = no modifier, 1.. = the modifier columns); wins over TextOf.
    public Func<string, int, string>? TextOfColumn { get; set; }

    [ObservableProperty] private CalloutTable? selectedTable;
    [ObservableProperty] private CalloutLine? selectedLine;
    [ObservableProperty] private int selectedRow = -1;
    // The selected cell's column: 0 = the main column, 1.. = the modifier columns.
    [ObservableProperty] private int selectedColumn;
    [ObservableProperty] private bool hasUnsavedChanges;
    [ObservableProperty] private string status = string.Empty;
    // Snap moved tables to a grid of this many sheet units (0 = off).
    [ObservableProperty] private double gridSize = 10;
    [ObservableProperty] private bool snapToGrid = true;

    // Raised whenever the sheet or the texts changed (the canvas redraws).
    public event EventHandler? Changed;

    // A relation dropped on a row, or a row cleared.
    public event EventHandler<CalloutBindRequest>? BindRequested;

    // A line was dragged to a spot for a part without a known position: the host places the part there (0..1 of the
    // picture) like the marker assistant.
    public event EventHandler<(string Part, double X, double Y)>? PartPlaced;

    public bool HasSheet => Sheet != null;

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public string UndoTip => undo.Count > 0 ? T("Undo: {0} (Ctrl+Z)", undo[^1].Description) : T("Nothing to undo");

    public string RedoTip => redo.Count > 0 ? T("Redo: {0} (Ctrl+Y)", redo[^1].Description) : T("Nothing to redo");

    public bool HasSelectedTable => SelectedTable != null;

    public bool HasSelectedLine => SelectedLine != null;

    public bool SelectedTableHasModifier => SelectedTable?.ShowModifier == true;

    public bool SelectedRowModifier => SelectedColumn > 0;

    public bool SelectedTableIsAxis => SelectedTable?.Rows.Any(r => r.Icon == "axis") == true;

    public bool SelectedTableIsPinned => SelectedTable?.Pinned == true;

    // A grid of keys or the shared table of parts on one spot: it can be split into one table per part.
    public bool SelectedTableIsGroup => SelectedTable?.Parts.Count > 1;

    public bool HasSplitGroups => Sheet?.Ungrouped.Count > 0;

    public string SelectedTableTitle => SelectedTable?.Title ?? string.Empty;

    // "12 tables · 10 lines · 2 other controls".
    public string Summary
    {
        get
        {
            if (Sheet == null)
                return T("No tables");
            int other = Sheet.Tables.Count(t => !t.Known);
            return T("{0} tables · {1} lines{2}", Sheet.Tables.Count, Sheet.Lines.Count, (other > 0 ? T(" · {0} without a spot", other) : string.Empty));
        }
    }

    // ------------------------------------------------------------------ building

    // Shows the sheet of a picture: the saved one when it belongs to this picture (same size), with its tables
    // matched to the parts again (new parts are added, the spots follow the known positions); else arranged anew.
    public void Show(DeviceSchematic? drawing, IDictionary<string, double[]> positions, double pictureWidth, double pictureHeight, CalloutSheet? saved)
    {
        schematic = drawing;
        known = new Dictionary<string, double[]>(positions, StringComparer.OrdinalIgnoreCase);
        undo.Clear();
        redo.Clear();
        SelectedTable = null;
        SelectedLine = null;
        SelectedRow = -1;
        if (drawing == null || pictureWidth <= 0 || pictureHeight <= 0)
        {
            Sheet = null;
            HasUnsavedChanges = false;
            Notify();
            return;
        }
        // The same picture decoded at another size (JoyPro's shipped sheet was made on the full-size photo): scaled.
        if (saved != null && saved.PictureWidth > 0 && saved.PictureHeight > 0 && Math.Abs(saved.PictureWidth - pictureWidth) >= 1 &&
            Math.Abs(saved.PictureWidth / saved.PictureHeight - pictureWidth / pictureHeight) < 0.01 * pictureWidth / pictureHeight)
            saved = saved.Scaled(pictureWidth / saved.PictureWidth);
        bool fits = saved != null && Math.Abs(saved.PictureWidth - pictureWidth) < 1 && Math.Abs(saved.PictureHeight - pictureHeight) < 1;
        if (fits && SameTables(saved!))
        {
            Sheet = saved!.Clone();
            SyncSpots(rearrangeWhenNew: false);
        }
        else
        {
            Sheet = CalloutLayouter.Arrange(drawing, known, pictureWidth, pictureHeight, fits || saved != null ? Rescaled(saved!, pictureWidth, pictureHeight) : null);
        }
        HasUnsavedChanges = false;
        Notify();
    }

    // A saved sheet keeps its settings for a picture of another size (the user's photo replaced): tables, pins and
    // lines are re-arranged, the look and the table settings stay.
    private static CalloutSheet Rescaled(CalloutSheet saved, double width, double height)
    {
        CalloutSheet copy = saved.Clone();
        if (Math.Abs(saved.PictureWidth - width) >= 1 || Math.Abs(saved.PictureHeight - height) >= 1)
        {
            foreach (CalloutTable t in copy.Tables)
                t.Pinned = false;
            copy.Lines.Clear();
            CalloutLayouter.ApplyDefaultStyle(copy, width, height);
        }
        return copy;
    }

    private bool SameTables(CalloutSheet saved)
    {
        HashSet<string> wanted = new(CalloutLayouter.BuildTables(schematic, known, saved.PictureWidth, saved.PictureHeight, saved.Ungrouped).Select(t => t.Id),
            StringComparer.OrdinalIgnoreCase);
        return wanted.SetEquals(saved.Tables.Select(t => t.Id));
    }

    // The known positions changed (a part dragged, placed by the assistant): spots follow, a part that got its first
    // spot leaves "Other controls" (the sheet is arranged again, pinned tables stay).
    public void UpdatePositions(IDictionary<string, double[]> positions)
    {
        known = new Dictionary<string, double[]>(positions, StringComparer.OrdinalIgnoreCase);
        if (Sheet == null)
            return;
        if (SyncSpots(rearrangeWhenNew: true))
            HasUnsavedChanges = true;
        Notify();
    }

    // True when something changed.
    private bool SyncSpots(bool rearrangeWhenNew)
    {
        if (Sheet == null)
            return false;
        bool changed = false, newSpot = false;
        foreach (CalloutTable t in Sheet.Tables)
        {
            List<double[]> spots = t.Parts.Select(p => known.TryGetValue(p, out double[]? xy) ? xy : null).Where(xy => xy != null).Select(xy => xy!).ToList();
            if (spots.Count == 0)
                continue;
            // A bank keeps a spot the user moved its line to; a part follows its position.
            if (t.Parts.Count > 1 && t.Known)
                continue;
            double x = spots.Average(s => s[0]), y = spots.Average(s => s[1]);
            if (!t.Known)
            {
                newSpot = true;
                changed = true;
                t.Known = true;
                t.SpotX = x;
                t.SpotY = y;
                continue;
            }
            if (Math.Abs(t.SpotX - x) > 1e-4 || Math.Abs(t.SpotY - y) > 1e-4)
            {
                t.SpotX = x;
                t.SpotY = y;
                CalloutLayouter.Reroute(Sheet, t);
                changed = true;
            }
        }
        if (newSpot && rearrangeWhenNew && schematic != null)
            Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
        return changed;
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Sheet));
        OnPropertyChanged(nameof(HasSheet));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        OnPropertyChanged(nameof(Summary));
        NotifySelection();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelectedTable));
        OnPropertyChanged(nameof(HasSelectedLine));
        OnPropertyChanged(nameof(SelectedTableHasModifier));
        OnPropertyChanged(nameof(SelectedTableIsAxis));
        OnPropertyChanged(nameof(SelectedTableIsPinned));
        OnPropertyChanged(nameof(SelectedTableIsGroup));
        OnPropertyChanged(nameof(HasSplitGroups));
        OnPropertyChanged(nameof(SelectedTableTitle));
    }

    // The texts changed (binds reloaded, another preview): redraw only.
    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedTableChanged(CalloutTable? value)
    {
        NotifySelection();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedLineChanged(CalloutLine? value)
    {
        NotifySelection();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string RowText(CalloutTable table, CalloutRow row, bool modifier) => RowText(table, row, modifier ? 1 : 0);

    public string RowText(CalloutTable table, CalloutRow row, int column)
    {
        if (row.Input.Length == 0)
            return string.Empty;
        if (TextOfColumn != null)
            return TextOfColumn(row.Input, column) ?? string.Empty;
        return TextOf == null ? string.Empty : TextOf(row.Input, column > 0) ?? string.Empty;
    }

    // The modifier combinations of the columns changed (binds, the sheet's choice): the tables get one column per
    // combination, their widths follow and the sheet is arranged again (pinned tables stay). True when it changed.
    public bool SetCombos(IReadOnlyList<string> combos)
    {
        if (Sheet == null)
            return false;
        List<string> next = combos.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (next.SequenceEqual(Sheet.Combos, StringComparer.OrdinalIgnoreCase))
            return false;
        Sheet.Combos = next;
        if (schematic != null)
            Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
        else
        {
            foreach (CalloutTable t in Sheet.Tables.Where(t => !t.Resized))
                t.Width = Sheet.DefaultWidth(t);
            Grow();
        }
        HasUnsavedChanges = true;
        Notify();
        return true;
    }

    public bool RowAt(double x, double y, out CalloutTable? table, out int row, out int column)
    {
        table = TableAt(x, y);
        row = -1;
        column = 0;
        return table != null && Sheet != null && Sheet.HitRow(table, x, y, out row, out column);
    }

    // ------------------------------------------------------------------ hit tests (sheet coordinates)

    public CalloutTable? TableAt(double x, double y)
    {
        if (Sheet == null)
            return null;
        for (int i = Sheet.Tables.Count - 1; i >= 0; i--)
        {
            double[] r = Sheet.RectOf(Sheet.Tables[i]);
            if (x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3])
                return Sheet.Tables[i];
        }
        return null;
    }

    public bool RowAt(double x, double y, out CalloutTable? table, out int row, out bool modifier)
    {
        table = TableAt(x, y);
        row = -1;
        modifier = false;
        return table != null && Sheet != null && Sheet.HitRow(table, x, y, out row, out modifier);
    }

    // The line and its segment within `tolerance` of a point.
    public CalloutLine? LineAt(double x, double y, double tolerance, out int segment)
    {
        segment = -1;
        if (Sheet == null)
            return null;
        foreach (CalloutLine line in Sheet.Lines)
        {
            for (int i = 0; i + 1 < line.Points.Count; i++)
            {
                double[] p = line.Points[i], q = line.Points[i + 1];
                double x0 = Math.Min(p[0], q[0]) - tolerance, x1 = Math.Max(p[0], q[0]) + tolerance;
                double y0 = Math.Min(p[1], q[1]) - tolerance, y1 = Math.Max(p[1], q[1]) + tolerance;
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                {
                    segment = i;
                    return line;
                }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ edits (each one can be undone)

    private void Push(string description)
    {
        if (Sheet == null)
            return;
        undo.Add((description, Sheet.ToJson()));
        if (undo.Count > UndoLimit)
            undo.RemoveAt(0);
        redo.Clear();
        HasUnsavedChanges = true;
    }

    private void Done(string status)
    {
        Status = status;
        Notify();
    }

    public double Snap(double value) => SnapToGrid && GridSize > 1 ? Math.Round(value / GridSize) * GridSize : Math.Round(value, 1);

    // A table dragged to a new place: pinned there (Auto arrange keeps it), its line re-routed.
    public void MoveTable(CalloutTable table, double x, double y)
    {
        if (Sheet == null)
            return;
        x = Snap(x);
        y = Snap(y);
        if (Math.Abs(table.X - x) < 0.5 && Math.Abs(table.Y - y) < 0.5)
            return;
        Push("move " + table.Title);
        table.X = Math.Max(0, x);
        table.Y = Math.Max(0, y);
        table.Pinned = true;
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(T("{0} moved and pinned; Auto arrange keeps it there.", table.Title));
    }

    public void ResizeTable(CalloutTable table, double width)
    {
        if (Sheet == null)
            return;
        double min = Sheet.RowHeight * (table.IsGrid ? 2 * table.Columns : 4);
        width = Math.Max(min, Snap(width));
        if (Math.Abs(Sheet.WidthOf(table) - width) < 0.5)
            return;
        Push("resize " + table.Title);
        table.Width = width;
        table.Resized = true;
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(T("{0} is {1} wide.", table.Title, Math.Round(width)));
    }

    public void SetModifierColumn(CalloutTable table, bool on)
    {
        if (Sheet == null || table.IsGrid || table.ShowModifier == on)
            return;
        Push(on ? "modifier column on" : "modifier column off");
        double before = Sheet.DefaultWidth(table);
        table.ShowModifier = on;
        if (!table.Resized || Math.Abs(table.Width - before) < 0.5)
            table.Width = Sheet.DefaultWidth(table);
        else
            table.Width += (on ? 1 : -1) * Math.Max(1, Sheet.Combos.Count) * Sheet.ComboColumnWidth;
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(table.Title + (on ? ": the modifier column is shown." : ": no modifier column."));
    }

    // More or fewer rows: an axis gets zone rows (0-20 %, 20-40 % ...), other tables hide or show their last rows.
    public void ChangeRows(CalloutTable table, int delta)
    {
        if (Sheet == null || delta == 0)
            return;
        bool axis = table.Rows.Any(r => r.Icon == "axis");
        if (axis)
        {
            int zones = Math.Clamp(table.Zones + delta, 0, 10);
            if (zones == table.Zones)
                return;
            Push(delta > 0 ? "add a zone row" : "remove a zone row");
            table.Zones = zones;
        }
        else if (delta < 0)
        {
            CalloutRow? last = table.Rows.LastOrDefault(r => !table.Hidden.Contains(table.KeyOf(r), StringComparer.OrdinalIgnoreCase));
            if (last == null || table.Rows.Count(r => !table.Hidden.Contains(table.KeyOf(r), StringComparer.OrdinalIgnoreCase)) <= 1)
                return;
            Push("hide a row");
            table.Hidden.Add(table.KeyOf(last));
        }
        else
        {
            CalloutRow? hidden = table.Rows.FirstOrDefault(r => table.Hidden.Contains(table.KeyOf(r), StringComparer.OrdinalIgnoreCase));
            if (hidden == null)
                return;
            Push("show a row");
            table.Hidden.RemoveAll(h => string.Equals(h, table.KeyOf(hidden), StringComparison.OrdinalIgnoreCase));
        }
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(table.Title + " has " + table.VisibleRows().Count + " rows.");
    }

    // A segment of a line dragged sideways: the line stays orthogonal and keeps the bend.
    public void BendLine(CalloutLine line, int segment, double delta)
    {
        if (Sheet == null || Math.Abs(delta) < 0.5)
            return;
        Push("bend a line");
        CalloutLayouter.Bend(Sheet, line, segment, Snap(delta));
        Grow();
        Done(T("Line bent; Re-route line puts it back."));
    }

    public void DeleteLine(CalloutLine line)
    {
        if (Sheet == null)
            return;
        CalloutTable? table = Sheet.Table(line.Table);
        Push("remove a line");
        Sheet.Lines.Remove(line);
        if (table != null)
            table.NoLine = true;
        SelectedLine = null;
        Done(T("Line removed. Drag from the table's handle to a spot to add one."));
    }

    public void RerouteLine(CalloutTable table)
    {
        if (Sheet == null || !table.Known)
            return;
        Push("re-route a line");
        table.NoLine = false;
        Sheet.Lines.RemoveAll(l => string.Equals(l.Table, table.Id, StringComparison.OrdinalIgnoreCase));
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(T("Line of {0} re-routed.", table.Title));
    }

    // A line dragged from a table to a spot on the picture (sheet coordinates). A part without a known position gets
    // that spot (the host saves it like a marker the assistant placed).
    public void AddLine(CalloutTable table, double x, double y)
    {
        if (Sheet == null)
            return;
        double fx = (x - Sheet.PictureX) / Sheet.PictureWidth, fy = (y - Sheet.PictureY) / Sheet.PictureHeight;
        if (fx < 0 || fx > 1 || fy < 0 || fy > 1)
        {
            Status = T("Drop the line on the picture.");
            return;
        }
        Push("add a line");
        bool wasKnown = table.Known;
        // A single part's spot is its position; a bank's spot is where its line ends.
        if (!wasKnown || table.Parts.Count > 1 || !known.ContainsKey(table.Parts.FirstOrDefault() ?? string.Empty))
        {
            table.Known = true;
            table.SpotX = fx;
            table.SpotY = fy;
        }
        table.NoLine = false;
        Sheet.Lines.RemoveAll(l => string.Equals(l.Table, table.Id, StringComparison.OrdinalIgnoreCase));
        if (!wasKnown && table.Parts.Count == 1)
        {
            known[table.Parts[0]] = [fx, fy];
            PartPlaced?.Invoke(this, (table.Parts[0], fx, fy));
            if (table.Side == "other" && schematic != null)
            {
                // It leaves "Other controls": the sheet is arranged again around it (pinned tables stay).
                Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
                Done(T("{0} is placed; its table joined the others.", table.Title));
                return;
            }
        }
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(T("Line of {0} added.", table.Title));
    }

    // A spot dragged on the picture: a part's position moves with it (the host keeps it like a dragged marker).
    public void MoveSpot(CalloutTable table, double x, double y)
    {
        if (Sheet == null || !table.Known)
            return;
        double fx = Math.Clamp((x - Sheet.PictureX) / Sheet.PictureWidth, 0, 1), fy = Math.Clamp((y - Sheet.PictureY) / Sheet.PictureHeight, 0, 1);
        Push("move a spot");
        table.SpotX = fx;
        table.SpotY = fy;
        if (table.Parts.Count == 1)
        {
            known[table.Parts[0]] = [fx, fy];
            PartPlaced?.Invoke(this, (table.Parts[0], fx, fy));
        }
        CalloutLayouter.Reroute(Sheet, table);
        Grow();
        Done(T("Spot of {0} moved.", table.Title));
    }

    // A grid of keys (or the table of parts on one spot) back into one table per part; arranging keeps them apart.
    public void SplitGroup(CalloutTable table)
    {
        if (Sheet == null || schematic == null || table.Parts.Count < 2)
            return;
        Push("split " + table.Title);
        foreach (string part in table.Parts.Where(p => !Sheet.Ungrouped.Contains(p, StringComparer.OrdinalIgnoreCase)))
            Sheet.Ungrouped.Add(part);
        Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
        SelectedTable = null;
        SelectedLine = null;
        Done(T("{0} is one table per part again; Group again puts it back together.", table.Title));
    }

    // The grids and shared tables JoyPro makes, also for the parts split out before.
    public void Regroup()
    {
        if (Sheet == null || schematic == null || Sheet.Ungrouped.Count == 0)
            return;
        Push("group again");
        Sheet.Ungrouped.Clear();
        Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
        SelectedTable = null;
        SelectedLine = null;
        Done(T("Keys and parts on one spot share their tables again."));
    }

    public void SetPinned(CalloutTable table, bool pinned)
    {
        if (table.Pinned == pinned)
            return;
        Push(pinned ? "pin" : "unpin");
        table.Pinned = pinned;
        Done(pinned ? T("{0} is pinned.", table.Title) : T("{0} moves with Auto arrange again.", table.Title));
    }

    // Arranges every table that is not pinned again (bent lines of pinned tables stay).
    public void AutoArrange()
    {
        if (Sheet == null || schematic == null)
            return;
        Push("auto arrange");
        Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, Sheet);
        SelectedLine = null;
        Done(T("Arranged again{0}", (Sheet.Tables.Any(t => t.Pinned) ? T("; pinned tables stayed.") : ".")));
    }

    // Everything back to JoyPro's arrangement (also the pins, widths, rows and lines).
    public void ResetLayout()
    {
        if (Sheet == null || schematic == null)
            return;
        Push("reset tables");
        CalloutSheet look = new() { Modifier = Sheet.Modifier, ComboMode = Sheet.ComboMode, MaxColumns = Sheet.MaxColumns, Combos = new List<string>(Sheet.Combos) };
        CalloutLayouter.ApplyDefaultStyle(look, Sheet.PictureWidth, Sheet.PictureHeight);
        look.RowHeight = Sheet.RowHeight;
        look.MainWidth = Sheet.MainWidth;
        look.ModWidth = Sheet.ModWidth;
        Sheet = CalloutLayouter.Arrange(schematic, known, Sheet.PictureWidth, Sheet.PictureHeight, look);
        SelectedTable = null;
        SelectedLine = null;
        Done(T("Tables and lines are JoyPro's again."));
    }

    // The canvas grows when a table or a line leaves it; nothing is cut off.
    private void Grow()
    {
        if (Sheet == null)
            return;
        double right = Sheet.PictureX + Sheet.PictureWidth, bottom = Sheet.PictureY + Sheet.PictureHeight;
        foreach (CalloutTable t in Sheet.Tables)
        {
            right = Math.Max(right, t.X + Sheet.WidthOf(t));
            bottom = Math.Max(bottom, t.Y + Sheet.HeightOf(t));
        }
        foreach (double[] p in Sheet.Lines.SelectMany(l => l.Points))
        {
            right = Math.Max(right, p[0]);
            bottom = Math.Max(bottom, p[1]);
        }
        double margin = Math.Round(Sheet.RowHeight * 0.8);
        Sheet.Width = Math.Max(Sheet.Width, Math.Ceiling(right + margin));
        Sheet.Height = Math.Max(Sheet.Height, Math.Ceiling(bottom + margin));
    }

    public void Undo()
    {
        if (Sheet == null || undo.Count == 0)
            return;
        (string description, string json) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((description, Sheet.ToJson()));
        Sheet = CalloutSheet.Parse(json);
        HasUnsavedChanges = true;
        SelectedLine = null;
        SelectedTable = Sheet.Table(SelectedTable?.Id ?? string.Empty);
        Done(T("Undone: {0}.", description));
    }

    public void Redo()
    {
        if (Sheet == null || redo.Count == 0)
            return;
        (string description, string json) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((description, Sheet.ToJson()));
        Sheet = CalloutSheet.Parse(json);
        HasUnsavedChanges = true;
        SelectedLine = null;
        SelectedTable = Sheet.Table(SelectedTable?.Id ?? string.Empty);
        Done(T("Redone: {0}.", description));
    }

    public void MarkSaved()
    {
        HasUnsavedChanges = false;
    }

    // ------------------------------------------------------------------ binding on the rows

    public CalloutRow? RowOf(CalloutTable table, int row)
    {
        List<CalloutRow> rows = table.VisibleRows();
        return row >= 0 && row < rows.Count ? rows[row] : null;
    }

    public void SelectRow(CalloutTable? table, int row, bool modifier) => SelectRow(table, row, modifier ? 1 : 0);

    public void SelectRow(CalloutTable? table, int row, int column)
    {
        SelectedTable = table;
        SelectedRow = table == null ? -1 : row;
        SelectedColumn = Math.Max(0, column);
        OnPropertyChanged(nameof(SelectedRowModifier));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The input picked elsewhere (legend, side panel): its row is the selected one (the modifier column stays as it is
    // when the same input is picked again).
    public void SelectInput(string? input)
    {
        if (Sheet == null || string.IsNullOrEmpty(input))
            return;
        if (SelectedTable != null && RowOf(SelectedTable, SelectedRow) is { } current && string.Equals(current.Input, input, StringComparison.OrdinalIgnoreCase))
            return;
        CalloutTable? table = Sheet.TableOfInput(input);
        if (table == null)
            return;
        int row = table.VisibleRows().FindIndex(r => string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase));
        SelectRow(table, row, SelectedColumn);
    }

    // A relation dropped on a row: bind it there (the modifier column binds it with the sheet's modifier).
    public bool DropRelation(CalloutTable table, int row, bool modifier, string relation) => DropRelation(table, row, modifier ? 1 : 0, relation);

    // A relation dropped on a cell: bound with the modifiers of its column.
    public bool DropRelation(CalloutTable table, int row, int column, string relation)
    {
        CalloutRow? target = RowOf(table, row);
        if (target == null || target.Input.Length == 0 || string.IsNullOrWhiteSpace(relation))
        {
            Status = target != null && target.Input.Length == 0 ? T("A zone row has no input of its own; drop it on the axis row.") : T("Drop the relation on a row of a table.");
            return false;
        }
        SelectRow(table, row, column);
        BindRequested?.Invoke(this, new CalloutBindRequest(target.Input, column > 0, relation.Trim(), table.Title + " " + target.Label, column));
        return true;
    }

    public void ClearRow(CalloutTable table, int row, bool modifier) => ClearRow(table, row, modifier ? 1 : 0);

    public void ClearRow(CalloutTable table, int row, int column)
    {
        CalloutRow? target = RowOf(table, row);
        if (target == null || target.Input.Length == 0)
            return;
        BindRequested?.Invoke(this, new CalloutBindRequest(target.Input, column > 0, null, table.Title + " " + target.Label, column));
    }
}
