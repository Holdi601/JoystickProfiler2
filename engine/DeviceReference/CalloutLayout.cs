#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // Callout sheets (docs/device-reference.md, "Tables and lines"): the device picture with a table per control group
    // around it and thin orthogonal leader lines from each control's spot to its table, like JoyPro 1's cheat sheets.
    // Shared by the engine (layout render, layout callouts) and the GUI (Layout page), so both compute the same sheet.
    // Plain C# 7.3.

    // One row of a table: an input of the device (or an axis zone), its direction label and icon.
    public sealed class CalloutRow
    {
        // The DirectInput input ("JOY_BTN5", "JOY_BTN_POV1_U", "JOY_X"); empty for an axis zone row.
        public string Input = string.Empty;
        // "Press", "Up", "B3", "0-20 %".
        public string Label = string.Empty;
        // button, up, down, left, right, upleft, upright, downleft, downright, mid, axis, pos, zone.
        public string Icon = "button";
        // The part the row belongs to (a bank table has several parts).
        public string Part = string.Empty;
        // In a grid table: the cell (row and column, as the keys sit on the device) and the line in the cell (a switch's
        // up / down). -1 = no cell (a list table, or an old grid laid out row by row).
        public int GridRow = -1, GridCol = -1, Sub;

        public CalloutRow Clone()
        {
            return (CalloutRow)MemberwiseClone();
        }
    }

    // One table: a control group (a hat's directions, a switch's positions, an encoder, an axis, a button bank).
    public sealed class CalloutTable
    {
        // The part id ("stick.hat.tms"), or "bank:<prefix>" for a button bank.
        public string Id = string.Empty;
        public string Title = string.Empty;
        public List<string> Parts = new List<string>();
        public List<CalloutRow> Rows = new List<CalloutRow>();
        // Sheet coordinates (top-left) and width; the height follows from the rows.
        public double X, Y, Width;
        // left, right, top, bottom (around the picture) or other (no known spot: "Other controls").
        public string Side = "other";
        // Grid columns of a button bank (1 = a list).
        public int Columns = 1;
        // Zone rows of an axis (0-20 %, 20-40 % ...): 0 = none.
        public int Zones;
        // The modifier columns (one per modifier combination of the sheet, CalloutSheet.Combos) next to the main one.
        public bool ShowModifier = true;
        // Placed by the user: "Auto arrange again" keeps it where it is.
        public bool Pinned;
        // Resized by the user: keeps its width.
        public bool Resized;
        // The user removed the leader line.
        public bool NoLine;
        // The spot the line leads to, 0..1 of the picture (the part's position, a bank's centre); Known = false: no spot.
        public bool Known;
        public double SpotX, SpotY;
        // Rows the user hid (by input, or "#label" for rows without an input).
        public List<string> Hidden = new List<string>();

        // A grid of keys or switches (one cell per part, as they sit on the device); also a single column or row.
        public bool Grid;

        public bool IsGrid { get { return Grid || Columns > 1; } }

        public string KeyOf(CalloutRow row)
        {
            return row.Input.Length > 0 ? row.Input : "#" + row.Label;
        }

        // The rows shown: the inputs not hidden, then the axis zones.
        public List<CalloutRow> VisibleRows()
        {
            List<CalloutRow> rows = Rows.Where(r => !Hidden.Contains(KeyOf(r), StringComparer.OrdinalIgnoreCase)).ToList();
            for (int i = 0; i < Zones; i++)
            {
                int from = (int)Math.Round(100.0 * i / Zones), to = (int)Math.Round(100.0 * (i + 1) / Zones);
                rows.Add(new CalloutRow { Label = from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture) + " %", Icon = "zone", Part = Parts.Count > 0 ? Parts[0] : string.Empty });
            }
            return rows;
        }

        public CalloutTable Clone()
        {
            CalloutTable copy = (CalloutTable)MemberwiseClone();
            copy.Parts = new List<string>(Parts);
            copy.Rows = Rows.Select(r => r.Clone()).ToList();
            copy.Hidden = new List<string>(Hidden);
            return copy;
        }
    }

    // A leader line: an orthogonal polyline from the spot on the picture to the table's edge (sheet coordinates).
    public sealed class CalloutLine
    {
        public string Table = string.Empty;
        public List<double[]> Points = new List<double[]>();
        // Bent by the user: re-arranging keeps its inner corners (moved with the picture).
        public bool Manual;

        public CalloutLine Clone()
        {
            CalloutLine copy = (CalloutLine)MemberwiseClone();
            copy.Points = Points.Select(p => new[] { p[0], p[1] }).ToList();
            return copy;
        }
    }

    // Modifier combinations (JoyPro's modifiers are "reformers"): with n modifiers an input can do 2^n things (none,
    // mod 1, mod 2, mod 1 + 2 ...). A combination is written as its key: the modifier names sorted, joined by "+"
    // ("" = no modifier, "Shift A+Shift B"). The tables of a callout sheet show one column per chosen combination.
    public static class CalloutCombos
    {
        // Columns for the combinations the device's binds use (the default) / for every combination of the modifiers.
        public const string Used = "used";
        public const string All = "all";
        // Columns of a table at most by default, the unmodified one included (3 modifiers = 8 combinations).
        public const int DefaultMaxColumns = 8;

        // "Shift B, Shift A" / "Shift B+Shift A" -> ["Shift A", "Shift B"].
        public static List<string> Split(string combo)
        {
            return (combo ?? string.Empty)
                .Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim())
                .Where(m => m.Length > 0 && m != "-" && !m.StartsWith("<", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string Key(IEnumerable<string> modifiers)
        {
            return string.Join("+", Split(string.Join("+", (modifiers ?? Enumerable.Empty<string>()).Where(m => m != null))));
        }

        // Exactly the same modifiers (order and case do not matter).
        public static bool Same(string a, string b)
        {
            return string.Equals(Key(Split(a)), Key(Split(b)), StringComparison.OrdinalIgnoreCase);
        }

        // The heading of a column: "—" (no modifier), "Shift A", "Shift A + Shift B".
        public static string Label(string combo)
        {
            List<string> parts = Split(combo);
            return parts.Count == 0 ? "—" : string.Join(" + ", parts);
        }

        // Fewer modifiers first, then by name: "A", "B", "A+B".
        public static int Compare(string a, string b)
        {
            int na = Split(a).Count, nb = Split(b).Count;
            return na != nb ? na.CompareTo(nb) : string.Compare(Key(Split(a)), Key(Split(b)), StringComparison.OrdinalIgnoreCase);
        }

        // Every combination of the modifiers with at least one modifier (2^n - 1), fewest modifiers first; at most max.
        public static List<string> AllOf(IEnumerable<string> modifiers, int max)
        {
            List<string> names = Split(string.Join("+", modifiers ?? Enumerable.Empty<string>()));
            List<string> result = new List<string>();
            int n = Math.Min(names.Count, 12);
            for (int mask = 1; mask < (1 << n); mask++)
            {
                List<string> set = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        set.Add(names[i]);
                }
                result.Add(Key(set));
            }
            result.Sort(Compare);
            return max > 0 && result.Count > max ? result.Take(max).ToList() : result;
        }

        // The modifier columns of a sheet (without the unmodified one, which is always there): mode All = every
        // combination of the profile's modifiers, else (Used) the combinations the device's binds use (one entry per
        // bind: its modifiers; the most used ones win when there are more than fit). maxColumns counts the unmodified
        // column too (0 = DefaultMaxColumns). picked: the sheet's own modifier (combination) shows that one alone.
        public static List<string> Choose(string mode, IEnumerable<string> profileModifiers, IEnumerable<string> bindCombos, int maxColumns, string picked)
        {
            int max = Math.Max(1, (maxColumns > 0 ? maxColumns : DefaultMaxColumns) - 1);
            if (Split(picked).Count > 0)
                return new List<string> { Key(Split(picked)) };
            if (string.Equals(mode, All, StringComparison.OrdinalIgnoreCase))
                return AllOf(profileModifiers, max);
            List<string> used = (bindCombos ?? Enumerable.Empty<string>())
                .Select(c => Key(Split(c)))
                .Where(k => k.Length > 0)
                .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(g => g.Key)
                .ToList();
            used.Sort(Compare);
            return used;
        }
    }

    public sealed class CalloutCheck
    {
        public int Tables, Lines, Positioned, Linked, Other;
        public int Overlaps, NonOrthogonal, Outside, Crossings, LinesThroughTables;
        // Segments of two lines lying on top of each other.
        public int Shared;
        public List<string> Problems = new List<string>();

        public bool Ok { get { return Overlaps == 0 && NonOrthogonal == 0 && Outside == 0 && LinesThroughTables == 0 && Linked == Positioned; } }

        public override string ToString()
        {
            return "tables " + Tables + ", lines " + Lines + ", positioned " + Positioned + ", linked " + Linked + ", other " + Other +
                   ", overlaps " + Overlaps + ", non-orthogonal " + NonOrthogonal + ", outside " + Outside + ", crossings " + Crossings +
                   ", shared tracks " + Shared + ", lines through tables " + LinesThroughTables;
        }
    }

    // The whole sheet: the picture's place and size in sheet coordinates (picture pixels), the tables and the lines.
    public sealed class CalloutSheet
    {
        public const int CurrentVersion = 1;

        public double Width, Height;
        public double PictureX, PictureY, PictureWidth, PictureHeight;
        // The look in sheet units: row height, the main text column, the modifier column.
        public double RowHeight = 30, MainWidth = 255, ModWidth = 165;
        // The modifier the second column shows ("" = the profile's most used modifier of the device). Set: the sheet
        // shows that modifier (combination) alone, whatever ComboMode says.
        public string Modifier = string.Empty;
        // Which modifier combinations get a column (CalloutCombos.Used / All; "" = Used) and how many columns a table
        // has at most, the unmodified one included (0 = CalloutCombos.DefaultMaxColumns).
        public string ComboMode = string.Empty;
        public int MaxColumns;
        // The modifier columns shown after the main (unmodified) one: each a combination key ("Shift", "Alt+Shift",
        // CalloutCombos.Key). Empty = the old single modifier column (Modifier, else the device's most used modifier).
        public List<string> Combos = new List<string>();
        // The top of the "Other controls" area (its heading); -1 = none.
        public double OtherY = -1;
        public List<CalloutTable> Tables = new List<CalloutTable>();
        public List<CalloutLine> Lines = new List<CalloutLine>();
        // Parts the user split out of their grid (each gets its own table again; arranging keeps that).
        public List<string> Ungrouped = new List<string>();

        public double TitleHeight { get { return Math.Round(RowHeight * 0.72); } }

        public double GridCellWidth { get { return Math.Round(RowHeight * 4.6); } }

        public double DefaultWidth(CalloutTable table)
        {
            if (table.IsGrid)
                return table.Columns * GridCellWidth;
            return RowHeight + MainWidth + ModColumnCount(table) * ComboColumnWidth;
        }

        // How many modifier columns a list table shows: one per combination (at least the old single one), none for a
        // grid or a table with its modifier columns off.
        public int ModColumnCount(CalloutTable table)
        {
            if (table.IsGrid || !table.ShowModifier)
                return 0;
            return Math.Max(1, Combos.Count);
        }

        // The default width of one modifier column: narrower when there are several, so 8 columns stay readable.
        public double ComboColumnWidth
        {
            get
            {
                int n = Math.Max(1, Combos.Count);
                double f = n <= 1 ? 1 : n == 2 ? 0.85 : n <= 4 ? 0.72 : 0.62;
                return Math.Round(ModWidth * f);
            }
        }

        // The combination a column of a list table shows: 0 = no modifier (""); 1.. = Combos. null = the old single
        // modifier column without a picked modifier (the reader resolves the device's most used modifier).
        public string ComboOfColumn(int column)
        {
            if (column <= 0)
                return string.Empty;
            if (Combos.Count == 0)
                return Modifier.Length > 0 ? CalloutCombos.Key(CalloutCombos.Split(Modifier)) : null;
            return column <= Combos.Count ? Combos[column - 1] : string.Empty;
        }

        public double WidthOf(CalloutTable table)
        {
            return table.Width > 0 ? table.Width : DefaultWidth(table);
        }

        public int BodyRows(CalloutTable table)
        {
            if (table.IsGrid)
            {
                int[] offsets;
                return Math.Max(1, GridLayout(table, table.VisibleRows(), out offsets));
            }
            return Math.Max(1, table.VisibleRows().Count);
        }

        // Where the visible rows of a grid table sit: per row its line (in row heights below the title) and column.
        // A grid row is as high as its fullest cell (a switch's up and down are two lines); empty grid rows take no
        // room. Rows without a cell (an old bank) go row by row. Returns the number of lines.
        private static int GridLayout(CalloutTable table, List<CalloutRow> rows, out int[] lines)
        {
            int columns = Math.Max(1, table.Columns);
            lines = new int[rows.Count];
            int[] gridRow = new int[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                gridRow[i] = rows[i].GridRow >= 0 ? rows[i].GridRow : i / columns;
            List<int> used = gridRow.Distinct().OrderBy(r => r).ToList();
            Dictionary<int, int> top = new Dictionary<int, int>();
            int line = 0;
            foreach (int r in used)
            {
                top[r] = line;
                int height = 1;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (gridRow[i] == r)
                        height = Math.Max(height, (rows[i].GridRow >= 0 ? rows[i].Sub : 0) + 1);
                }
                line += height;
            }
            for (int i = 0; i < rows.Count; i++)
                lines[i] = top[gridRow[i]] + (rows[i].GridRow >= 0 ? rows[i].Sub : 0);
            return line;
        }

        private static int GridColumn(CalloutTable table, CalloutRow row, int index)
        {
            int columns = Math.Max(1, table.Columns);
            return Math.Min(columns - 1, row.GridCol >= 0 ? row.GridCol : index % columns);
        }

        public double HeightOf(CalloutTable table)
        {
            return TitleHeight + BodyRows(table) * RowHeight;
        }

        public double[] RectOf(CalloutTable table)
        {
            return new[] { table.X, table.Y, WidthOf(table), HeightOf(table) };
        }

        // The width of all modifier columns together inside a list table (0 when it has none).
        public double ModColumnWidth(CalloutTable table)
        {
            int n = ModColumnCount(table);
            if (n == 0)
                return 0;
            double text = WidthOf(table) - RowHeight;
            double mods = n * ComboColumnWidth;
            return Math.Max(RowHeight * n, Math.Round(text * mods / (MainWidth + mods)));
        }

        // The cell of a visible row: x, y, w, h (sheet coordinates); modifier = the (first) modifier column.
        public double[] CellOf(CalloutTable table, int row, bool modifier)
        {
            return CellOf(table, row, modifier ? 1 : 0);
        }

        // The cell of a visible row in a column: 0 = the main (unmodified) column, 1.. = the modifier columns.
        public double[] CellOf(CalloutTable table, int row, int column)
        {
            double top = table.Y + TitleHeight;
            double w = WidthOf(table);
            if (table.IsGrid)
            {
                List<CalloutRow> rows = table.VisibleRows();
                int[] lines;
                GridLayout(table, rows, out lines);
                double cw = w / Math.Max(1, table.Columns);
                if (row < 0 || row >= rows.Count)
                    return new[] { table.X, top, cw, RowHeight };
                return new[] { table.X + GridColumn(table, rows[row], row) * cw, top + lines[row] * RowHeight, cw, RowHeight };
            }
            double mod = ModColumnWidth(table);
            double y = top + row * RowHeight;
            int n = ModColumnCount(table);
            if (column > 0 && n > 0)
            {
                int k = Math.Min(column, n) - 1;
                double each = mod / n;
                return new[] { table.X + w - mod + k * each, y, each, RowHeight };
            }
            return new[] { table.X, y, w - mod, RowHeight };
        }

        // In a grid: whether visible row i starts a key's cell (the key's lines follow each other).
        public bool FirstLineOfCell(CalloutTable table, List<CalloutRow> rows, int i)
        {
            return i == 0 || rows[i - 1].Part != rows[i].Part || rows[i].Part.Length == 0 || rows[i - 1].GridRow != rows[i].GridRow || rows[i - 1].GridCol != rows[i].GridCol;
        }

        // The whole cell of the key of visible row i in a grid (all its lines): x, y, w, h.
        public double[] KeyCellOf(CalloutTable table, int i)
        {
            List<CalloutRow> rows = table.VisibleRows();
            double[] first = CellOf(table, i, false);
            double bottom = first[1] + first[3];
            for (int k = i + 1; k < rows.Count && !FirstLineOfCell(table, rows, k); k++)
            {
                double[] c = CellOf(table, k, false);
                bottom = c[1] + c[3];
            }
            return new[] { first[0], first[1], first[2], bottom - first[1] };
        }

        // Which visible row and column a sheet point is on: false when it is not on a row of this table.
        public bool HitRow(CalloutTable table, double x, double y, out int row, out bool modifier)
        {
            int column;
            bool hit = HitRow(table, x, y, out row, out column);
            modifier = column > 0;
            return hit;
        }

        // Which visible row and column (0 = main, 1.. = the modifier columns) a sheet point is on.
        public bool HitRow(CalloutTable table, double x, double y, out int row, out int column)
        {
            row = -1;
            column = 0;
            double[] r = RectOf(table);
            if (x < r[0] || x > r[0] + r[2] || y < r[1] + TitleHeight || y > r[1] + r[3])
                return false;
            int count = table.VisibleRows().Count;
            if (table.IsGrid)
            {
                for (int k = 0; k < count; k++)
                {
                    double[] c = CellOf(table, k, false);
                    if (x >= c[0] && x <= c[0] + c[2] && y >= c[1] && y <= c[1] + c[3])
                    {
                        row = k;
                        return true;
                    }
                }
                return false;
            }
            int i = (int)((y - r[1] - TitleHeight) / RowHeight);
            if (i < 0 || i >= count)
                return false;
            row = i;
            double mod = ModColumnWidth(table);
            int n = ModColumnCount(table);
            if (mod > 0 && n > 0 && x >= r[0] + r[2] - mod)
                column = Math.Min(n, 1 + (int)((x - (r[0] + r[2] - mod)) / (mod / n)));
            return true;
        }

        // The spot on the picture in sheet coordinates.
        public double[] SpotOf(CalloutTable table)
        {
            return new[] { PictureX + table.SpotX * PictureWidth, PictureY + table.SpotY * PictureHeight };
        }

        public CalloutTable Table(string id)
        {
            return Tables.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public CalloutLine LineOf(string table)
        {
            return Lines.FirstOrDefault(l => string.Equals(l.Table, table, StringComparison.OrdinalIgnoreCase));
        }

        public CalloutTable TableOfInput(string input)
        {
            return Tables.FirstOrDefault(t => t.Rows.Any(r => string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase)));
        }

        public CalloutSheet Clone()
        {
            CalloutSheet copy = (CalloutSheet)MemberwiseClone();
            copy.Tables = Tables.Select(t => t.Clone()).ToList();
            copy.Lines = Lines.Select(l => l.Clone()).ToList();
            copy.Ungrouped = new List<string>(Ungrouped);
            copy.Combos = new List<string>(Combos);
            return copy;
        }

        // The same sheet for the same picture at another size (a photo decoded smaller): every place and size times f.
        public CalloutSheet Scaled(double f)
        {
            CalloutSheet copy = Clone();
            if (f <= 0 || Math.Abs(f - 1) < 1e-9)
                return copy;
            copy.Width = Math.Ceiling(Width * f);
            copy.Height = Math.Ceiling(Height * f);
            copy.PictureX = PictureX * f;
            copy.PictureY = PictureY * f;
            copy.PictureWidth = PictureWidth * f;
            copy.PictureHeight = PictureHeight * f;
            copy.RowHeight = RowHeight * f;
            copy.MainWidth = MainWidth * f;
            copy.ModWidth = ModWidth * f;
            copy.OtherY = OtherY < 0 ? OtherY : OtherY * f;
            foreach (CalloutTable t in copy.Tables)
            {
                t.X *= f;
                t.Y *= f;
                t.Width *= f;
            }
            foreach (CalloutLine l in copy.Lines)
            {
                foreach (double[] p in l.Points)
                {
                    p[0] *= f;
                    p[1] *= f;
                }
            }
            return copy;
        }

        // ------------------------------------------------------------------ JSON ("callouts" of a joypro-device-layout)

        public Dictionary<string, object> ToJsonObject()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["version"] = CurrentVersion;
            root["width"] = R(Width);
            root["height"] = R(Height);
            root["picture"] = new List<object> { R(PictureX), R(PictureY), R(PictureWidth), R(PictureHeight) };
            root["rowHeight"] = R(RowHeight);
            root["mainWidth"] = R(MainWidth);
            root["modWidth"] = R(ModWidth);
            if (Modifier.Length > 0)
                root["modifier"] = Modifier;
            if (ComboMode.Length > 0)
                root["comboMode"] = ComboMode;
            if (MaxColumns > 0)
                root["maxColumns"] = MaxColumns;
            if (Combos.Count > 0)
                root["combos"] = Combos.Cast<object>().ToList();
            root["otherY"] = R(OtherY);
            if (Ungrouped.Count > 0)
                root["ungrouped"] = Ungrouped.Cast<object>().ToList();
            List<object> tables = new List<object>();
            foreach (CalloutTable t in Tables)
            {
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["id"] = t.Id;
                item["title"] = t.Title;
                item["parts"] = t.Parts.Cast<object>().ToList();
                item["side"] = t.Side;
                item["x"] = R(t.X);
                item["y"] = R(t.Y);
                item["width"] = R(t.Width);
                if (t.Grid)
                    item["grid"] = true;
                if (t.Columns > 1)
                    item["columns"] = t.Columns;
                if (t.Zones > 0)
                    item["zones"] = t.Zones;
                item["modifier"] = t.ShowModifier;
                if (t.Pinned)
                    item["pinned"] = true;
                if (t.Resized)
                    item["resized"] = true;
                if (t.NoLine)
                    item["noLine"] = true;
                if (t.Known)
                    item["spot"] = new List<object> { Math.Round(t.SpotX, 4), Math.Round(t.SpotY, 4) };
                if (t.Hidden.Count > 0)
                    item["hidden"] = t.Hidden.Cast<object>().ToList();
                List<object> rows = new List<object>();
                foreach (CalloutRow r in t.Rows)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal);
                    row["input"] = r.Input;
                    row["label"] = r.Label;
                    row["icon"] = r.Icon;
                    if (r.Part.Length > 0)
                        row["part"] = r.Part;
                    if (r.GridRow >= 0)
                        row["cell"] = new List<object> { r.GridRow, Math.Max(0, r.GridCol), r.Sub };
                    rows.Add(row);
                }
                item["rows"] = rows;
                tables.Add(item);
            }
            root["tables"] = tables;
            List<object> lines = new List<object>();
            foreach (CalloutLine l in Lines)
            {
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["table"] = l.Table;
                item["points"] = l.Points.Select(p => (object)new List<object> { R(p[0]), R(p[1]) }).ToList();
                if (l.Manual)
                    item["manual"] = true;
                lines.Add(item);
            }
            root["lines"] = lines;
            return root;
        }

        public string ToJson()
        {
            return MiniJson.Write(ToJsonObject(), indented: true);
        }

        public static CalloutSheet FromJsonObject(Dictionary<string, object> root)
        {
            if (root == null)
                return null;
            CalloutSheet sheet = new CalloutSheet
            {
                Width = Num(root, "width", 0),
                Height = Num(root, "height", 0),
                RowHeight = Math.Max(8, Num(root, "rowHeight", 30)),
                MainWidth = Math.Max(20, Num(root, "mainWidth", 255)),
                ModWidth = Math.Max(10, Num(root, "modWidth", 165)),
                Modifier = DeviceCatalog.Text(root, "modifier"),
                ComboMode = DeviceCatalog.Text(root, "comboMode"),
                MaxColumns = Math.Max(0, (int)Num(root, "maxColumns", 0)),
                Combos = DeviceCatalog.Strings(root, "combos").ToList(),
                OtherY = Num(root, "otherY", -1),
                Ungrouped = DeviceCatalog.Strings(root, "ungrouped").ToList()
            };
            List<object> picture = root.ContainsKey("picture") ? root["picture"] as List<object> : null;
            if (picture != null && picture.Count >= 4)
            {
                sheet.PictureX = D(picture[0]);
                sheet.PictureY = D(picture[1]);
                sheet.PictureWidth = D(picture[2]);
                sheet.PictureHeight = D(picture[3]);
            }
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "tables"))
            {
                CalloutTable t = new CalloutTable
                {
                    Id = DeviceCatalog.Text(item, "id"),
                    Title = DeviceCatalog.Text(item, "title"),
                    Parts = DeviceCatalog.Strings(item, "parts").ToList(),
                    Side = DeviceCatalog.Text(item, "side").Length > 0 ? DeviceCatalog.Text(item, "side") : "other",
                    X = Num(item, "x", 0),
                    Y = Num(item, "y", 0),
                    Width = Num(item, "width", 0),
                    Columns = Math.Max(1, (int)Num(item, "columns", 1)),
                    Grid = Bool(item, "grid", false),
                    Zones = Math.Max(0, Math.Min(10, (int)Num(item, "zones", 0))),
                    ShowModifier = Bool(item, "modifier", true),
                    Pinned = Bool(item, "pinned", false),
                    Resized = Bool(item, "resized", false),
                    NoLine = Bool(item, "noLine", false),
                    Hidden = DeviceCatalog.Strings(item, "hidden").ToList()
                };
                List<object> spot = item.ContainsKey("spot") ? item["spot"] as List<object> : null;
                if (spot != null && spot.Count >= 2)
                {
                    t.Known = true;
                    t.SpotX = Clamp01(D(spot[0]));
                    t.SpotY = Clamp01(D(spot[1]));
                }
                foreach (Dictionary<string, object> row in DeviceCatalog.Objects(item, "rows"))
                {
                    CalloutRow r = new CalloutRow
                    {
                        Input = DeviceCatalog.Text(row, "input"),
                        Label = DeviceCatalog.Text(row, "label"),
                        Icon = DeviceCatalog.Text(row, "icon").Length > 0 ? DeviceCatalog.Text(row, "icon") : "button",
                        Part = DeviceCatalog.Text(row, "part")
                    };
                    List<object> cell = row.ContainsKey("cell") ? row["cell"] as List<object> : null;
                    if (cell != null && cell.Count >= 3)
                    {
                        r.GridRow = Math.Max(0, (int)D(cell[0]));
                        r.GridCol = Math.Max(0, (int)D(cell[1]));
                        r.Sub = Math.Max(0, (int)D(cell[2]));
                    }
                    t.Rows.Add(r);
                }
                if (t.Id.Length > 0)
                    sheet.Tables.Add(t);
            }
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "lines"))
            {
                CalloutLine l = new CalloutLine { Table = DeviceCatalog.Text(item, "table"), Manual = Bool(item, "manual", false) };
                List<object> points = item.ContainsKey("points") ? item["points"] as List<object> : null;
                foreach (object p in points ?? new List<object>())
                {
                    List<object> xy = p as List<object>;
                    if (xy != null && xy.Count >= 2)
                        l.Points.Add(new[] { D(xy[0]), D(xy[1]) });
                }
                if (l.Table.Length > 0 && l.Points.Count >= 2)
                    sheet.Lines.Add(l);
            }
            return sheet;
        }

        public static CalloutSheet Parse(string json)
        {
            return FromJsonObject(MiniJson.Parse(json) as Dictionary<string, object>);
        }

        private static object R(double v)
        {
            return Math.Round(v, 1);
        }

        private static double D(object value)
        {
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static double Num(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null && !(value is string) && !(value is bool) ? D(value) : fallback;
        }

        private static bool Bool(Dictionary<string, object> item, string key, bool fallback)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }

        internal static double Clamp01(double v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }

    // Builds the tables of a device from JoyPro's description of its parts and places them around the picture
    // (deterministic, no LLM):
    //   1. Groups: one table per part (a hat's press and four directions, a switch's positions, an encoder's turns and
    //      press, an axis), and a grid table for a bank of numbered buttons that sit close together (B1..B8).
    //   2. Sides: each table with a known spot goes to the nearest margin (left/right preferred over top/bottom), then
    //      the sides are balanced: a side over its capacity hands the table that costs least to move to another side.
    //   3. Stacking: a side's tables are sorted by their spot along the side and packed without overlap so each table's
    //      first row sits as close as possible to its spot's height (clusters merged by their mean, order kept).
    //   4. Lines: spot -> lane in the gutter between picture and tables -> along the lane -> table edge. Lanes are given
    //      per side in order (lines whose table lies above the spot inner-first from the top, the others inner-first from
    //      the bottom); with the order kept by stacking, lines of one side never cross each other or a table.
    //   5. Canvas: the gutters and the columns are sized from the lanes and the widest table, the picture is placed in
    //      the middle, the canvas grows to hold everything; tables without a spot go into "Other controls" below.
    // Pinned tables stay where the user put them (moved with the picture); manual lines keep their corners.
    public static class CalloutLayouter
    {
        private sealed class Spot
        {
            public CalloutTable Table;
            public double X, Y; // picture pixels
            public double W, H;
            public double Top, Left; // picture-relative position of the table
            public int Lane;
            public bool LRoute;
            // Spots in one row: the row (group) and the spot's rank by distance to the side (0 = nearest).
            public int Group = -1, Depth;
            // A lane line of a row leaves its spot on its own track: this far along the side first.
            public double Track;
        }

        // ------------------------------------------------------------------ 1. groups

        // The tables of a device: parts with inputs, their spots where known (part id -> [x, y], 0..1 of the picture).
        // The picture's size tells the distances on it (the drawing's size when omitted); ungrouped: parts the user split
        // out of their grid.
        public static List<CalloutTable> BuildTables(DeviceSchematic schematic, IDictionary<string, double[]> known)
        {
            return BuildTables(schematic, known, schematic == null ? 1 : schematic.Width, schematic == null ? 1 : schematic.Height, null);
        }

        public static List<CalloutTable> BuildTables(DeviceSchematic schematic, IDictionary<string, double[]> known, double pictureWidth, double pictureHeight,
            ICollection<string> ungrouped)
        {
            List<CalloutTable> tables = new List<CalloutTable>();
            if (schematic == null)
                return tables;
            List<SchematicMarker> parts = schematic.Markers.Where(m => m.HasInputs).ToList();
            HashSet<string> banked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CalloutTable grid in KeyGrids.Find(parts, known, Math.Max(1, pictureWidth), Math.Max(1, pictureHeight), ungrouped))
            {
                foreach (string part in grid.Parts)
                    banked.Add(part);
                tables.Add(grid);
            }
            // Parts on (nearly) one spot (a knob's push, pull, turn and axis) share one table and one line.
            foreach (List<SchematicMarker> stack in Stacks(parts.Where(m => !banked.Contains(m.Id)).ToList(), known, pictureWidth, pictureHeight, ungrouped))
            {
                CalloutTable table = new CalloutTable
                {
                    Id = "stack:" + stack[0].Id,
                    Title = string.Join(" / ", stack.Select(PartTitle).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Known = true,
                    SpotX = CalloutSheet.Clamp01(stack.Average(m => known[m.Id][0])),
                    SpotY = CalloutSheet.Clamp01(stack.Average(m => known[m.Id][1]))
                };
                foreach (SchematicMarker m in stack)
                {
                    banked.Add(m.Id);
                    table.Parts.Add(m.Id);
                    foreach (SchematicInput input in OrderedInputs(m))
                        table.Rows.Add(new CalloutRow { Input = input.Input, Label = PartTitle(m) + " " + RowLabel(m, input), Icon = RowIcon(input), Part = m.Id });
                }
                tables.Add(table);
            }
            foreach (SchematicMarker m in parts)
            {
                if (banked.Contains(m.Id))
                    continue;
                CalloutTable table = new CalloutTable { Id = m.Id, Title = PartTitle(m) };
                table.Parts.Add(m.Id);
                double[] xy;
                if (known != null && known.TryGetValue(m.Id, out xy) && xy != null && xy.Length >= 2)
                {
                    table.Known = true;
                    table.SpotX = CalloutSheet.Clamp01(xy[0]);
                    table.SpotY = CalloutSheet.Clamp01(xy[1]);
                }
                foreach (SchematicInput input in OrderedInputs(m))
                    table.Rows.Add(new CalloutRow { Input = input.Input, Label = RowLabel(m, input), Icon = RowIcon(input), Part = m.Id });
                tables.Add(table);
            }
            return tables;
        }

        private static IEnumerable<SchematicInput> OrderedInputs(SchematicMarker m)
        {
            return m.Inputs.Where(i => i.Input.Length > 0)
                .GroupBy(i => i.Input, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(i => DirectionOrder(i)).ThenBy(i => DeviceVocabulary.NaturalKey(i.Input), StringComparer.Ordinal);
        }

        // Parts whose spots are within 1.5 % of the picture's diagonal of each other (at most 4 parts), in the drawing's
        // order; parts the user split out stay alone.
        private static List<List<SchematicMarker>> Stacks(List<SchematicMarker> parts, IDictionary<string, double[]> known, double pw, double ph, ICollection<string> ungrouped)
        {
            List<List<SchematicMarker>> stacks = new List<List<SchematicMarker>>();
            if (known == null)
                return stacks;
            HashSet<string> split = new HashSet<string>(ungrouped ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            double reach = Math.Sqrt(pw * pw + ph * ph) * 0.015;
            List<SchematicMarker> placed = parts.Where(m => !split.Contains(m.Id) && known.ContainsKey(m.Id) && known[m.Id] != null && known[m.Id].Length >= 2).ToList();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SchematicMarker m in placed)
            {
                if (used.Contains(m.Id))
                    continue;
                List<SchematicMarker> stack = new List<SchematicMarker> { m };
                for (int i = 0; i < stack.Count && stack.Count < 4; i++)
                {
                    foreach (SchematicMarker o in placed)
                    {
                        if (stack.Count >= 4 || used.Contains(o.Id) || stack.Contains(o))
                            continue;
                        double dx = (known[o.Id][0] - known[stack[i].Id][0]) * pw, dy = (known[o.Id][1] - known[stack[i].Id][1]) * ph;
                        if (Math.Sqrt(dx * dx + dy * dy) <= reach)
                            stack.Add(o);
                    }
                }
                if (stack.Count < 2)
                    continue;
                foreach (SchematicMarker s in stack)
                    used.Add(s.Id);
                stacks.Add(stack);
            }
            return stacks;
        }

        internal static string PartTitle(SchematicMarker m)
        {
            string name = (m.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                name = DeviceVocabulary.ElementName(m.Id);
            // A raw vocabulary id ("throttle.axis"): its words.
            if (name.IndexOf('.') > 0 && name.IndexOf(' ') < 0)
            {
                string[] words = name.Split('.').Where(w => w.Length > 0).Select(w => w.Replace('_', ' ')).ToArray();
                name = string.Join(" ", words);
                name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
            return name;
        }

        internal static string DirectionOf(SchematicInput input)
        {
            string d = (input.Direction ?? string.Empty).ToLowerInvariant();
            if (d.Length == 0 && DeviceVocabulary.InputKind(input.Input) == "hat")
                d = DeviceVocabulary.HatDirection(input.Input);
            return d;
        }

        // JoyPro 1's order: press, up, right, left, down, then the diagonals; positions in their order.
        internal static int DirectionOrder(SchematicInput input)
        {
            string d = DirectionOf(input);
            switch (d)
            {
                case "": case "press": case "push": return DeviceVocabulary.InputKind(input.Input) == "axis" ? 20 : 0;
                case "stage1": case "x": return 1;
                case "stage2": case "y": return 2;
                case "up": case "fwd": case "pull": case "out": return 1;
                case "right": case "cw": return 2;
                case "center": case "mid": return 3;
                case "left": case "ccw": return 4;
                case "down": case "aft": case "in": return 5;
                case "upright": case "ur": return 6;
                case "upleft": case "ul": return 7;
                case "downright": case "dr": return 8;
                case "downleft": case "dl": return 9;
            }
            if (d.StartsWith("pos", StringComparison.Ordinal))
            {
                int n;
                return 10 + (int.TryParse(d.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0);
            }
            return 50;
        }

        internal static string RowIcon(SchematicInput input)
        {
            if (DeviceVocabulary.InputKind(input.Input) == "axis")
                return "axis";
            switch (DirectionOf(input))
            {
                case "up": case "fwd": case "pull": case "out": return "up";
                case "down": case "aft": case "in": return "down";
                case "left": case "ccw": return "left";
                case "right": case "cw": return "right";
                case "upright": case "ur": return "upright";
                case "upleft": case "ul": return "upleft";
                case "downright": case "dr": return "downright";
                case "downleft": case "dl": return "downleft";
                case "center": case "mid": return "mid";
            }
            return DirectionOf(input).StartsWith("pos", StringComparison.Ordinal) ? "pos" : "button";
        }

        internal static string RowLabel(SchematicMarker part, SchematicInput input)
        {
            string d = DirectionOf(input);
            if (DeviceVocabulary.InputKind(input.Input) == "axis")
            {
                string axis = input.Input.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) ? input.Input.Substring(4) : input.Input;
                return d.Length > 0 ? DeviceVocabulary.DirectionName(d) + " (" + axis + ")" : axis;
            }
            if (d.StartsWith("pos", StringComparison.Ordinal))
                return d.Substring(3);
            if (d.Length == 0)
                return "Press";
            string name = DeviceVocabulary.DirectionName(d);
            return name.Length == 0 ? "Press" : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        // ------------------------------------------------------------------ 2.-5. arrange

        // The look of a sheet for a picture: row height from the picture's size, the two text columns from the row.
        public static void ApplyDefaultStyle(CalloutSheet sheet, double pictureWidth, double pictureHeight)
        {
            double longest = Math.Max(pictureWidth, pictureHeight);
            sheet.RowHeight = Math.Max(20, Math.Min(56, Math.Round(longest * 0.024)));
            sheet.MainWidth = Math.Round(sheet.RowHeight * 8.5);
            sheet.ModWidth = Math.Round(sheet.RowHeight * 5.5);
        }

        // A new sheet for a device picture (pictureWidth x pictureHeight pixels). previous (optional): its look, modifier,
        // the user's table settings (modifier column, zones, hidden rows, width), pinned tables and manual lines are kept.
        public static CalloutSheet Arrange(DeviceSchematic schematic, IDictionary<string, double[]> known, double pictureWidth, double pictureHeight, CalloutSheet previous)
        {
            return Arrange(BuildTables(schematic, known, pictureWidth, pictureHeight, previous == null ? null : previous.Ungrouped), pictureWidth, pictureHeight, previous);
        }

        public static CalloutSheet Arrange(List<CalloutTable> tables, double pictureWidth, double pictureHeight, CalloutSheet previous)
        {
            CalloutSheet sheet = new CalloutSheet
            {
                PictureWidth = Math.Max(1, pictureWidth),
                PictureHeight = Math.Max(1, pictureHeight)
            };
            if (previous != null && previous.RowHeight > 0)
            {
                sheet.RowHeight = previous.RowHeight;
                sheet.MainWidth = previous.MainWidth;
                sheet.ModWidth = previous.ModWidth;
                sheet.Modifier = previous.Modifier;
            }
            if (previous != null)
            {
                sheet.ComboMode = previous.ComboMode;
                sheet.MaxColumns = previous.MaxColumns;
                sheet.Combos = new List<string>(previous.Combos);
            }
            else
            {
                ApplyDefaultStyle(sheet, sheet.PictureWidth, sheet.PictureHeight);
            }
            if (previous != null)
                sheet.Ungrouped = new List<string>(previous.Ungrouped);
            foreach (CalloutTable fresh in tables)
            {
                CalloutTable t = fresh.Clone();
                CalloutTable old = previous == null ? null : previous.Table(t.Id);
                if (old != null)
                {
                    t.ShowModifier = old.ShowModifier;
                    t.Zones = old.Zones;
                    t.Hidden = new List<string>(old.Hidden);
                    t.NoLine = old.NoLine;
                    if (!t.Grid)
                        t.Columns = old.Columns;
                    t.Title = old.Title.Length > 0 ? old.Title : t.Title;
                    if (old.Resized)
                    {
                        t.Resized = true;
                        t.Width = old.Width;
                    }
                    // A spot the user moved the line to (a bank, or a part placed by dropping its line).
                    if (old.Known && (!t.Known || t.Parts.Count > 1))
                    {
                        t.Known = true;
                        t.SpotX = old.SpotX;
                        t.SpotY = old.SpotY;
                    }
                    if (old.Pinned)
                    {
                        t.Pinned = true;
                        t.Side = old.Side;
                        t.X = old.X - previous.PictureX; // picture-relative until the picture is placed
                        t.Y = old.Y - previous.PictureY;
                    }
                }
                if (!t.Resized)
                    t.Width = sheet.DefaultWidth(t);
                sheet.Tables.Add(t);
            }

            double pw = sheet.PictureWidth, ph = sheet.PictureHeight, rh = sheet.RowHeight;
            double gap = Math.Round(rh * 0.3), laneGap = Math.Max(6, Math.Round(rh * 0.3)), pad = Math.Round(rh * 0.6), margin = Math.Round(rh * 0.8);

            List<Spot> spots = new List<Spot>();
            foreach (CalloutTable t in sheet.Tables.Where(t => t.Known && !t.Pinned))
                spots.Add(new Spot { Table = t, X = t.SpotX * pw, Y = t.SpotY * ph, W = sheet.WidthOf(t), H = sheet.HeightOf(t) });

            AssignSides(spots, pw, ph, gap);

            double anchorOffset = sheet.TitleHeight + rh / 2;
            double row = rh * 0.5;
            List<Spot> left = Side(spots, "left", s => s.Y, s => s.X, row), right = Side(spots, "right", s => s.Y, s => pw - s.X, row);
            List<Spot> top = Side(spots, "top", s => s.X, s => s.Y, row), bottom = Side(spots, "bottom", s => s.X, s => ph - s.Y, row);
            PackColumn(left, anchorOffset, gap, Obstacles(sheet, "left"));
            PackColumn(right, anchorOffset, gap, Obstacles(sheet, "right"));
            PackBand(top, gap, pw);
            PackBand(bottom, gap, pw);

            // An L-shaped line runs along its table's row across the picture: only while that row is beside the picture
            // (above or below it, the band tables are in the way), else it takes a lane like the others.
            foreach (Spot s in left.Concat(right))
            {
                if (s.Top + anchorOffset < 0 || s.Top + anchorOffset > ph)
                    s.LRoute = false;
            }
            // Nor where it would run through another part's spot.
            double hit = rh * 0.45;
            foreach (Spot s in spots.Where(s => s.LRoute))
            {
                bool column = s.Table.Side == "left" || s.Table.Side == "right";
                double ax = column ? s.X : s.Left + s.W / 2, ay = column ? s.Top + anchorOffset : s.Y;
                double edge = s.Table.Side == "left" ? 0 : s.Table.Side == "right" ? pw : s.Table.Side == "top" ? 0 : ph;
                foreach (Spot o in spots)
                {
                    if (ReferenceEquals(o, s))
                        continue;
                    bool onCorner = column
                        ? Math.Abs(o.X - s.X) < hit && Between(o.Y, s.Y, ay) || Math.Abs(o.Y - ay) < hit && Between(o.X, s.X, edge)
                        : Math.Abs(o.Y - s.Y) < hit && Between(o.X, s.X, ax) || Math.Abs(o.X - ax) < hit && Between(o.Y, s.Y, edge);
                    if (onCorner)
                    {
                        s.LRoute = false;
                        break;
                    }
                }
            }
            double track = Math.Max(4, Math.Round(rh * 0.3));
            AssignTracks(left, s => s.Y, s => s.Top + anchorOffset, track);
            AssignTracks(right, s => s.Y, s => s.Top + anchorOffset, track);
            AssignTracks(top, s => s.X, s => s.Left + s.W / 2, track);
            AssignTracks(bottom, s => s.X, s => s.Left + s.W / 2, track);

            // Lanes: the anchor of a column table is its first row; of a band table its middle.
            int leftLanes = AssignLanes(left, s => s.Y + s.Track, s => s.Top + anchorOffset);
            int rightLanes = AssignLanes(right, s => s.Y + s.Track, s => s.Top + anchorOffset);
            int topLanes = AssignLanes(top, s => s.X + s.Track, s => s.Left + s.W / 2);
            int bottomLanes = AssignLanes(bottom, s => s.X + s.Track, s => s.Left + s.W / 2);
            double gl = left.Count == 0 ? 0 : pad * 2 + Math.Max(0, leftLanes - 1) * laneGap;
            double gr = right.Count == 0 ? 0 : pad * 2 + Math.Max(0, rightLanes - 1) * laneGap;
            double gt = top.Count == 0 ? 0 : pad * 2 + Math.Max(0, topLanes - 1) * laneGap;
            double gb = bottom.Count == 0 ? 0 : pad * 2 + Math.Max(0, bottomLanes - 1) * laneGap;
            double wl = left.Count == 0 ? 0 : left.Max(s => s.W);
            double wr = right.Count == 0 ? 0 : right.Max(s => s.W);
            double ht = top.Count == 0 ? 0 : top.Max(s => s.H);
            double minColumnTop = left.Concat(right).Select(s => s.Top).DefaultIfEmpty(0).Min();

            List<CalloutTable> pinned = sheet.Tables.Where(t => t.Pinned).ToList();
            double pinnedMinX = pinned.Select(t => t.X).DefaultIfEmpty(0).Min();
            double pinnedMinY = pinned.Select(t => t.Y).DefaultIfEmpty(0).Min();

            sheet.PictureX = margin + Math.Max(wl + gl, Math.Max(0, -pinnedMinX));
            sheet.PictureY = margin + Math.Max(Math.Max(ht + gt, -minColumnTop), Math.Max(0, -pinnedMinY));
            double px = sheet.PictureX, py = sheet.PictureY;

            foreach (Spot s in left)
            {
                s.Table.X = px - gl - s.W;
                s.Table.Y = py + s.Top;
            }
            foreach (Spot s in right)
            {
                s.Table.X = px + pw + gr;
                s.Table.Y = py + s.Top;
            }
            foreach (Spot s in top)
            {
                s.Table.X = px + s.Left;
                s.Table.Y = py - gt - s.H;
            }
            foreach (Spot s in bottom)
            {
                s.Table.X = px + s.Left;
                s.Table.Y = py + ph + gb;
            }
            foreach (CalloutTable t in pinned)
            {
                t.X += px;
                t.Y += py;
            }

            // Lines: spot -> lane -> along the lane -> table edge.
            Dictionary<string, CalloutLine> manual = new Dictionary<string, CalloutLine>(StringComparer.OrdinalIgnoreCase);
            if (previous != null)
            {
                foreach (CalloutLine l in previous.Lines.Where(l => l.Manual))
                {
                    CalloutLine moved = l.Clone();
                    foreach (double[] p in moved.Points)
                    {
                        p[0] += px - previous.PictureX;
                        p[1] += py - previous.PictureY;
                    }
                    manual[l.Table] = moved;
                }
            }
            foreach (Spot s in left)
                AddLaneLine(sheet, s.Table, px - pad - s.Lane * laneGap, true, anchorOffset, s.LRoute, s.Track);
            foreach (Spot s in right)
                AddLaneLine(sheet, s.Table, px + pw + pad + s.Lane * laneGap, true, anchorOffset, s.LRoute, s.Track);
            foreach (Spot s in top)
                AddLaneLine(sheet, s.Table, py - pad - s.Lane * laneGap, false, anchorOffset, s.LRoute, s.Track);
            foreach (Spot s in bottom)
                AddLaneLine(sheet, s.Table, py + ph + pad + s.Lane * laneGap, false, anchorOffset, s.LRoute, s.Track);
            foreach (CalloutTable t in pinned.Where(t => t.Known))
                sheet.Lines.Add(new CalloutLine { Table = t.Id, Points = RouteDirect(sheet, t) });
            for (int i = 0; i < sheet.Lines.Count; i++)
            {
                CalloutLine kept;
                if (manual.TryGetValue(sheet.Lines[i].Table, out kept))
                {
                    sheet.Lines[i] = kept;
                    FixEnds(sheet, kept);
                }
            }
            sheet.Lines.RemoveAll(l => { CalloutTable t = sheet.Table(l.Table); return t == null || t.NoLine; });

            // The canvas: everything placed so far, then "Other controls" below it.
            double maxRight = px + pw, maxBottom = py + ph;
            foreach (CalloutTable t in sheet.Tables.Where(t => t.Known || t.Pinned))
            {
                maxRight = Math.Max(maxRight, t.X + sheet.WidthOf(t));
                maxBottom = Math.Max(maxBottom, t.Y + sheet.HeightOf(t));
            }
            foreach (CalloutLine l in sheet.Lines)
            {
                foreach (double[] p in l.Points)
                {
                    maxRight = Math.Max(maxRight, p[0]);
                    maxBottom = Math.Max(maxBottom, p[1]);
                }
            }
            sheet.Width = Math.Ceiling(maxRight + margin);
            List<CalloutTable> other = sheet.Tables.Where(t => !t.Known && !t.Pinned).ToList();
            sheet.OtherY = -1;
            if (other.Count > 0)
            {
                sheet.OtherY = Math.Ceiling(maxBottom + margin);
                double x = margin, y = sheet.OtherY + sheet.TitleHeight + gap, rowBottom = y;
                double available = Math.Max(sheet.Width - margin, margin + other.Max(t => sheet.WidthOf(t)));
                foreach (CalloutTable t in other)
                {
                    t.Side = "other";
                    double w = sheet.WidthOf(t), h = sheet.HeightOf(t);
                    if (x > margin && x + w > available)
                    {
                        x = margin;
                        y = rowBottom + gap;
                    }
                    t.X = x;
                    t.Y = y;
                    x += w + gap;
                    rowBottom = Math.Max(rowBottom, y + h);
                }
                maxBottom = rowBottom;
                sheet.Width = Math.Max(sheet.Width, Math.Ceiling(other.Max(t => t.X + sheet.WidthOf(t)) + margin));
            }
            sheet.Height = Math.Ceiling(maxBottom + margin);
            return sheet;
        }

        // A side's tables in stacking order: by their spot along the side. Spots in one row (along the side within
        // `tolerance`) would send their lines along the same track, through each other's spots: they get L-shaped lines
        // instead (straight to their table's row, then along it), nested so they do not cross: the spot nearest to
        // the side in the middle of the row's tables, the farther ones outwards.
        private static List<Spot> Side(List<Spot> spots, string side, Func<Spot, double> along, Func<Spot, double> depth, double tolerance)
        {
            List<Spot> sorted = spots.Where(s => s.Table.Side == side).OrderBy(along).ThenBy(depth).ThenBy(s => s.Table.Id, StringComparer.Ordinal).ToList();
            List<Spot> result = new List<Spot>();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i + 1;
                while (j < sorted.Count && along(sorted[j]) - along(sorted[i]) < tolerance)
                    j++;
                List<Spot> row = sorted.Skip(i).Take(j - i).OrderBy(depth).ThenBy(s => s.Table.Id, StringComparer.Ordinal).ToList();
                LinkedList<Spot> nested = new LinkedList<Spot>();
                for (int k = 0; k < row.Count; k++)
                {
                    row[k].LRoute = row.Count > 1;
                    row[k].Group = row.Count > 1 ? i : -1;
                    row[k].Depth = k;
                    if (k % 2 == 1 || k == 0)
                        nested.AddLast(row[k]);
                    else
                        nested.AddFirst(row[k]);
                }
                result.AddRange(nested);
                i = j;
            }
            return result;
        }

        // Nearest margin first (left/right preferred: a column reads better and its lines do not cross), then the
        // sides are balanced against their capacity.
        private static void AssignSides(List<Spot> spots, double pw, double ph, double gap)
        {
            string[] sides = { "left", "right", "top", "bottom" };
            Func<Spot, string, double> cost = (s, side) =>
            {
                switch (side)
                {
                    case "left": return s.X;
                    case "right": return pw - s.X;
                    case "top": return s.W > pw ? double.MaxValue / 4 : s.Y * 1.7 + pw * 0.06;
                    default: return s.W > pw ? double.MaxValue / 4 : (ph - s.Y) * 1.7 + pw * 0.06;
                }
            };
            foreach (Spot s in spots)
                s.Table.Side = sides.OrderBy(side => cost(s, side)).First();

            double columnCapacity = Math.Max(ph * 1.15, ph + 6 * gap);
            double bandCapacity = pw;
            Func<string, double> load = side =>
            {
                List<Spot> on = spots.Where(s => s.Table.Side == side).ToList();
                if (on.Count == 0)
                    return 0;
                return side == "left" || side == "right" ? on.Sum(s => s.H) + gap * (on.Count - 1) : on.Sum(s => s.W) + gap * (on.Count - 1);
            };
            Func<string, double> capacity = side => side == "left" || side == "right" ? columnCapacity : bandCapacity;

            for (int guard = 0; guard < spots.Count * 4; guard++)
            {
                bool moved = false;
                foreach (string over in sides.Where(side => load(side) > capacity(side) + 0.5).OrderByDescending(side => load(side) / capacity(side)).ToList())
                {
                    bool overBand = over == "top" || over == "bottom";
                    Spot best = null;
                    string target = null;
                    double bestCost = double.MaxValue;
                    foreach (Spot s in spots.Where(s => s.Table.Side == over))
                    {
                        foreach (string side in sides.Where(side => side != over))
                        {
                            double extra = side == "left" || side == "right" ? s.H + gap : s.W + gap;
                            bool full = load(side) + extra > capacity(side) + 0.5;
                            // A band is a single row: never over its capacity. A full column only takes what an
                            // overfull band must give away, or what an overfull column hands over while it stays shorter.
                            if (full && (side == "top" || side == "bottom"))
                                continue;
                            if (full && !overBand && load(side) + extra >= load(over) - extra)
                                continue;
                            double c = cost(s, side) - cost(s, over) + (full ? pw : 0);
                            if (c < bestCost - 1e-9)
                            {
                                bestCost = c;
                                best = s;
                                target = side;
                            }
                        }
                    }
                    if (best == null)
                        continue;
                    best.Table.Side = target;
                    moved = true;
                    break;
                }
                if (!moved)
                    break;
            }
        }

        // Ordered 1D packing: each table wants its first row at its spot's height; overlapping neighbours merge into a
        // cluster placed at the mean of their wishes (the least squared displacement that keeps the order).
        private static void PackColumn(List<Spot> column, double anchorOffset, double gap, List<double[]> obstacles)
        {
            List<double> wish = column.Select(s => s.Y - anchorOffset).ToList();
            List<double> size = column.Select(s => s.H).ToList();
            List<double> placed = Pack(wish, size, gap, double.NegativeInfinity, double.PositiveInfinity);
            // Pinned tables of this column stay where they are: a table that would overlap one goes below it, the
            // tables after it follow (the order stays).
            if (obstacles != null && obstacles.Count > 0)
            {
                for (int i = 0; i < placed.Count; i++)
                {
                    if (i > 0)
                        placed[i] = Math.Max(placed[i], placed[i - 1] + size[i - 1] + gap);
                    for (int guard = 0; guard < obstacles.Count + 1; guard++)
                    {
                        double[] hit = obstacles.FirstOrDefault(o => placed[i] < o[1] + gap && placed[i] + size[i] + gap > o[0]);
                        if (hit == null)
                            break;
                        placed[i] = hit[1] + gap;
                    }
                }
            }
            for (int i = 0; i < column.Count; i++)
                column[i].Top = placed[i];
        }

        // The pinned tables of a column: their top and bottom, relative to the picture (as during arranging).
        private static List<double[]> Obstacles(CalloutSheet sheet, string side)
        {
            return sheet.Tables.Where(t => t.Pinned && t.Side == side)
                .Select(t => new[] { t.Y, t.Y + sheet.HeightOf(t) })
                .OrderBy(o => o[0]).ToList();
        }

        // A band is one row within the picture's width: middle of the table over its spot.
        private static void PackBand(List<Spot> band, double gap, double pw)
        {
            List<double> wish = band.Select(s => s.X - s.W / 2).ToList();
            List<double> size = band.Select(s => s.W).ToList();
            List<double> placed = Pack(wish, size, gap, 0, pw);
            for (int i = 0; i < band.Count; i++)
                band[i].Left = placed[i];
        }

        internal static List<double> Pack(List<double> wish, List<double> size, double gap, double min, double max)
        {
            int n = wish.Count;
            List<double> result = new List<double>(new double[n]);
            if (n == 0)
                return result;
            // Offset of each item from its cluster's start.
            List<int> start = new List<int>();
            List<double> sum = new List<double>();
            List<int> count = new List<int>();
            List<double> length = new List<double>();
            for (int i = 0; i < n; i++)
            {
                start.Add(i);
                sum.Add(wish[i]);
                count.Add(1);
                length.Add(size[i]);
                while (start.Count > 1)
                {
                    int c = start.Count - 1;
                    double prevTop = sum[c - 1] / count[c - 1];
                    double curTop = sum[c] / count[c];
                    if (prevTop + length[c - 1] + gap <= curTop + 1e-9)
                        break;
                    // Merge: the items of c get the offset of the previous cluster's length.
                    double offset = length[c - 1] + gap;
                    sum[c - 1] += sum[c] - offset * count[c];
                    count[c - 1] += count[c];
                    length[c - 1] += gap + length[c];
                    start.RemoveAt(c);
                    sum.RemoveAt(c);
                    count.RemoveAt(c);
                    length.RemoveAt(c);
                }
            }
            for (int c = 0; c < start.Count; c++)
            {
                double top = sum[c] / count[c];
                if (!double.IsInfinity(min) && !double.IsInfinity(max) && length[c] <= max - min)
                    top = Math.Max(min, Math.Min(max - length[c], top));
                int end = c + 1 < start.Count ? start[c + 1] : n;
                double y = top;
                for (int i = start[c]; i < end; i++)
                {
                    result[i] = y;
                    y += size[i] + gap;
                }
            }
            // Clamping can push clusters into each other: keep the order and the gaps, then pull back from the end
            // (when everything fits between min and max, both hold afterwards).
            for (int i = 1; i < n; i++)
                result[i] = Math.Max(result[i], result[i - 1] + size[i - 1] + gap);
            if (!double.IsInfinity(max))
            {
                result[n - 1] = Math.Min(result[n - 1], max - size[n - 1]);
                for (int i = n - 2; i >= 0; i--)
                    result[i] = Math.Min(result[i], result[i + 1] - gap - size[i]);
            }
            if (!double.IsInfinity(min) && result[0] < min)
            {
                result[0] = min;
                for (int i = 1; i < n; i++)
                    result[i] = Math.Max(result[i], result[i - 1] + size[i - 1] + gap);
            }
            return result;
        }

        // Lane lines of spots in one row would run on the same track through each other's spots: the nearest keeps the
        // row's track, the others step off it towards their table, farther spots farther (their tables are stacked
        // outwards in the same order, so the tracks keep the stacking order and do not cross).
        private static void AssignTracks(List<Spot> spots, Func<Spot, double> spotAlong, Func<Spot, double> anchorAlong, double step)
        {
            foreach (IGrouping<int, Spot> group in spots.Where(s => s.Group >= 0 && !s.LRoute).GroupBy(s => s.Group))
            {
                int before = 0, after = 0;
                foreach (Spot s in group.OrderBy(s => s.Depth))
                {
                    double rel = anchorAlong(s) - spotAlong(s);
                    // The spot nearest to the side keeps the row's own track: nothing of the row is in its way.
                    if (s.Depth == 0)
                        continue;
                    double offset = rel < 0 ? -(++before) * step : (++after) * step;
                    s.Track = Math.Abs(offset) > Math.Abs(rel) ? rel : offset;
                }
            }
        }

        private static bool Between(double v, double a, double b)
        {
            return v > Math.Min(a, b) && v < Math.Max(a, b);
        }

        // Lane numbers (0 = next to the picture). Lines whose anchor lies before the spot (above / left of it) are
        // given lanes from the first spot on, the others from the last spot on; a line gets the lane after the highest
        // lane of the earlier lines whose run along the lane overlaps its own. Returns the number of lanes.
        private static int AssignLanes(List<Spot> spots, Func<Spot, double> spotAlong, Func<Spot, double> anchorAlong)
        {
            int lanes = 0;
            List<Spot> before = spots.Where(s => !s.LRoute && anchorAlong(s) <= spotAlong(s) + 0.5).OrderBy(spotAlong).ToList();
            List<Spot> after = spots.Where(s => !s.LRoute && anchorAlong(s) > spotAlong(s) + 0.5).OrderByDescending(spotAlong).ToList();
            foreach (List<Spot> group in new[] { before, after })
            {
                List<Spot> done = new List<Spot>();
                foreach (Spot s in group)
                {
                    double lo = Math.Min(spotAlong(s), anchorAlong(s)), hi = Math.Max(spotAlong(s), anchorAlong(s));
                    int lane = 0;
                    foreach (Spot d in done)
                    {
                        double dlo = Math.Min(spotAlong(d), anchorAlong(d)), dhi = Math.Max(spotAlong(d), anchorAlong(d));
                        if (dlo <= hi + 1 && lo <= dhi + 1)
                            lane = Math.Max(lane, d.Lane + 1);
                    }
                    s.Lane = lane;
                    done.Add(s);
                    lanes = Math.Max(lanes, lane + 1);
                }
            }
            return lanes;
        }

        private static void AddLaneLine(CalloutSheet sheet, CalloutTable t, double lane, bool column, double anchorOffset, bool lRoute, double track)
        {
            double[] spot = sheet.SpotOf(t);
            double w = sheet.WidthOf(t), h = sheet.HeightOf(t);
            List<double[]> points = new List<double[]>();
            points.Add(spot);
            if (column)
            {
                double edge = t.Side == "left" ? t.X + w : t.X;
                double ay = t.Y + anchorOffset;
                if (!lRoute && track != 0)
                    points.Add(new[] { spot[0], spot[1] + track });
                if (!lRoute)
                    points.Add(new[] { lane, spot[1] + track });
                points.Add(new[] { lRoute ? spot[0] : lane, ay });
                points.Add(new[] { edge, ay });
            }
            else
            {
                double edge = t.Side == "top" ? t.Y + h : t.Y;
                double ax = t.X + w / 2;
                if (!lRoute && track != 0)
                    points.Add(new[] { spot[0] + track, spot[1] });
                if (!lRoute)
                    points.Add(new[] { spot[0] + track, lane });
                points.Add(new[] { ax, lRoute ? spot[1] : lane });
                points.Add(new[] { ax, edge });
            }
            sheet.Lines.Add(new CalloutLine { Table = t.Id, Points = Simplify(points) });
        }

        // A line to a table the user placed: an orthogonal Z from the spot to the table edge facing it.
        public static List<double[]> RouteDirect(CalloutSheet sheet, CalloutTable t)
        {
            double[] spot = sheet.SpotOf(t);
            double[] r = sheet.RectOf(t);
            double anchorY = r[1] + sheet.TitleHeight + sheet.RowHeight / 2;
            List<double[]> points = new List<double[]> { spot };
            bool sideways = spot[0] < r[0] || spot[0] > r[0] + r[2];
            if (sideways)
            {
                double edge = spot[0] < r[0] ? r[0] : r[0] + r[2];
                double mid = Math.Round((spot[0] + edge) / 2);
                points.Add(new[] { mid, spot[1] });
                points.Add(new[] { mid, anchorY });
                points.Add(new[] { edge, anchorY });
            }
            else
            {
                double edge = spot[1] < r[1] ? r[1] : r[1] + r[3];
                double ax = r[0] + r[2] / 2;
                double mid = Math.Round((spot[1] + edge) / 2);
                points.Add(new[] { spot[0], mid });
                points.Add(new[] { ax, mid });
                points.Add(new[] { ax, edge });
            }
            return Simplify(points);
        }

        // Re-routes one table's line (after the table or its spot moved): a manual line keeps its corners as far as it
        // can, else a direct Z route. Returns the line (null when the table has no spot or no line).
        public static CalloutLine Reroute(CalloutSheet sheet, CalloutTable t)
        {
            sheet.Lines.RemoveAll(l => string.Equals(l.Table, t.Id, StringComparison.OrdinalIgnoreCase) && !l.Manual);
            CalloutLine line = sheet.LineOf(t.Id);
            if (!t.Known || t.NoLine)
            {
                if (line != null)
                    sheet.Lines.Remove(line);
                return null;
            }
            if (line != null)
            {
                FixEnds(sheet, line);
                return line;
            }
            line = new CalloutLine { Table = t.Id, Points = RouteDirect(sheet, t) };
            sheet.Lines.Add(line);
            return line;
        }

        // Keeps a manual line attached: its first point on the spot, its last on the table edge, every segment
        // orthogonal (the neighbouring corner follows).
        public static void FixEnds(CalloutSheet sheet, CalloutLine line)
        {
            CalloutTable t = sheet.Table(line.Table);
            if (t == null || line.Points.Count < 2)
                return;
            double[] spot = sheet.SpotOf(t);
            double[] r = sheet.RectOf(t);
            if (line.Points.Count < 4)
            {
                line.Points = RouteDirect(sheet, t);
                line.Manual = false;
                return;
            }
            double[] first = line.Points[0], second = line.Points[1];
            bool firstHorizontal = Math.Abs(first[1] - second[1]) < 0.01;
            line.Points[0] = new[] { spot[0], spot[1] };
            if (firstHorizontal)
                second[1] = spot[1];
            else
                second[0] = spot[0];
            int n = line.Points.Count;
            double[] last = line.Points[n - 1], beforeLast = line.Points[n - 2];
            bool lastHorizontal = Math.Abs(last[1] - beforeLast[1]) < 0.01;
            double[] end;
            if (lastHorizontal)
            {
                double y = Math.Max(r[1] + 2, Math.Min(r[1] + r[3] - 2, last[1]));
                end = new[] { beforeLast[0] <= r[0] + r[2] / 2 ? r[0] : r[0] + r[2], y };
                beforeLast[1] = y;
            }
            else
            {
                double x = Math.Max(r[0] + 2, Math.Min(r[0] + r[2] - 2, last[0]));
                end = new[] { x, beforeLast[1] <= r[1] + r[3] / 2 ? r[1] : r[1] + r[3] };
                beforeLast[0] = x;
            }
            line.Points[n - 1] = end;
            line.Points = Simplify(line.Points);
        }

        // Moves segment `segment` (between point segment and segment+1) of a line sideways by delta (perpendicular to
        // the segment), adding corners at the ends when they are the line's ends: the line stays orthogonal.
        public static void Bend(CalloutSheet sheet, CalloutLine line, int segment, double delta)
        {
            if (line == null || segment < 0 || segment >= line.Points.Count - 1 || Math.Abs(delta) < 0.01)
                return;
            List<double[]> p = line.Points.Select(q => new[] { q[0], q[1] }).ToList();
            bool horizontal = Math.Abs(p[segment][1] - p[segment + 1][1]) < 0.01;
            int a = segment, b = segment + 1;
            if (b == p.Count - 1)
            {
                p.Insert(b, new[] { p[b][0], p[b][1] });
            }
            if (a == 0)
            {
                p.Insert(1, new[] { p[0][0], p[0][1] });
                a = 1;
                b = a + 1;
            }
            if (horizontal)
            {
                p[a][1] += delta;
                p[b][1] += delta;
            }
            else
            {
                p[a][0] += delta;
                p[b][0] += delta;
            }
            line.Points = Simplify(p);
            line.Manual = true;
            FixEnds(sheet, line);
            line.Manual = true;
        }

        public static List<double[]> Simplify(List<double[]> points)
        {
            List<double[]> result = new List<double[]>();
            foreach (double[] p in points)
            {
                double[] q = new[] { Math.Round(p[0], 1), Math.Round(p[1], 1) };
                if (result.Count > 0 && Math.Abs(result[result.Count - 1][0] - q[0]) < 0.05 && Math.Abs(result[result.Count - 1][1] - q[1]) < 0.05)
                    continue;
                if (result.Count >= 2)
                {
                    double[] a = result[result.Count - 2], b = result[result.Count - 1];
                    bool collinear = (Math.Abs(a[0] - b[0]) < 0.05 && Math.Abs(b[0] - q[0]) < 0.05) || (Math.Abs(a[1] - b[1]) < 0.05 && Math.Abs(b[1] - q[1]) < 0.05);
                    if (collinear)
                    {
                        result[result.Count - 1] = q;
                        continue;
                    }
                }
                result.Add(q);
            }
            return result;
        }

        // ------------------------------------------------------------------ the checks of the tests and the CLI

        public static CalloutCheck Check(CalloutSheet sheet)
        {
            CalloutCheck check = new CalloutCheck { Tables = sheet.Tables.Count, Lines = sheet.Lines.Count };
            check.Positioned = sheet.Tables.Count(t => t.Known && !t.NoLine);
            check.Other = sheet.Tables.Count(t => !t.Known);
            check.Linked = sheet.Tables.Count(t => t.Known && !t.NoLine && sheet.LineOf(t.Id) != null);
            List<double[]> rects = sheet.Tables.Select(t => sheet.RectOf(t)).ToList();
            for (int i = 0; i < rects.Count; i++)
            {
                double[] a = rects[i];
                if (a[0] < -0.5 || a[1] < -0.5 || a[0] + a[2] > sheet.Width + 0.5 || a[1] + a[3] > sheet.Height + 0.5)
                {
                    check.Outside++;
                    check.Problems.Add("table " + sheet.Tables[i].Id + " is outside the canvas");
                }
                for (int j = i + 1; j < rects.Count; j++)
                {
                    double[] b = rects[j];
                    if (a[0] < b[0] + b[2] - 0.5 && b[0] < a[0] + a[2] - 0.5 && a[1] < b[1] + b[3] - 0.5 && b[1] < a[1] + a[3] - 0.5)
                    {
                        check.Overlaps++;
                        check.Problems.Add("tables " + sheet.Tables[i].Id + " and " + sheet.Tables[j].Id + " overlap");
                    }
                }
            }
            double[] pic = { sheet.PictureX, sheet.PictureY, sheet.PictureWidth, sheet.PictureHeight };
            for (int i = 0; i < rects.Count; i++)
            {
                double[] a = rects[i];
                if (a[0] < pic[0] + pic[2] - 0.5 && pic[0] < a[0] + a[2] - 0.5 && a[1] < pic[1] + pic[3] - 0.5 && pic[1] < a[1] + a[3] - 0.5)
                {
                    check.Overlaps++;
                    check.Problems.Add("table " + sheet.Tables[i].Id + " covers the picture");
                }
            }
            foreach (CalloutLine line in sheet.Lines)
            {
                for (int k = 0; k + 1 < line.Points.Count; k++)
                {
                    double[] p = line.Points[k], q = line.Points[k + 1];
                    if (Math.Abs(p[0] - q[0]) > 0.05 && Math.Abs(p[1] - q[1]) > 0.05)
                    {
                        check.NonOrthogonal++;
                        check.Problems.Add("line of " + line.Table + " has a slanted segment");
                    }
                    foreach (double[] pt in new[] { p, q })
                    {
                        if (pt[0] < -0.5 || pt[1] < -0.5 || pt[0] > sheet.Width + 0.5 || pt[1] > sheet.Height + 0.5)
                        {
                            check.Outside++;
                            check.Problems.Add("line of " + line.Table + " leaves the canvas");
                        }
                    }
                    for (int i = 0; i < rects.Count; i++)
                    {
                        if (SegmentCrossesRect(p, q, rects[i]))
                        {
                            check.LinesThroughTables++;
                            check.Problems.Add("line of " + line.Table + " runs through table " + sheet.Tables[i].Id);
                        }
                    }
                }
            }
            for (int i = 0; i < sheet.Lines.Count; i++)
            {
                for (int j = i + 1; j < sheet.Lines.Count; j++)
                {
                    check.Crossings += Crossings(sheet.Lines[i], sheet.Lines[j]);
                    check.Shared += Shared(sheet.Lines[i], sheet.Lines[j]);
                }
            }
            return check;
        }

        private static bool SegmentCrossesRect(double[] p, double[] q, double[] r)
        {
            // Strictly inside: touching the edge (where a line ends at its table) is fine.
            double x0 = Math.Min(p[0], q[0]), x1 = Math.Max(p[0], q[0]), y0 = Math.Min(p[1], q[1]), y1 = Math.Max(p[1], q[1]);
            double inset = 1.0;
            return x0 < r[0] + r[2] - inset && x1 > r[0] + inset && y0 < r[1] + r[3] - inset && y1 > r[1] + inset;
        }

        private static int Crossings(CalloutLine a, CalloutLine b)
        {
            int n = 0;
            for (int i = 0; i + 1 < a.Points.Count; i++)
            {
                for (int j = 0; j + 1 < b.Points.Count; j++)
                {
                    if (ProperCross(a.Points[i], a.Points[i + 1], b.Points[j], b.Points[j + 1]))
                        n++;
                }
            }
            return n;
        }

        // Segments of two lines on top of each other (one line hiding the other).
        private static int Shared(CalloutLine a, CalloutLine b)
        {
            int n = 0;
            for (int i = 0; i + 1 < a.Points.Count; i++)
            {
                for (int j = 0; j + 1 < b.Points.Count; j++)
                {
                    double[] p1 = a.Points[i], p2 = a.Points[i + 1], q1 = b.Points[j], q2 = b.Points[j + 1];
                    bool ph = Math.Abs(p1[1] - p2[1]) < 0.05, qh = Math.Abs(q1[1] - q2[1]) < 0.05;
                    if (ph != qh)
                        continue;
                    int along = ph ? 0 : 1, across = ph ? 1 : 0;
                    if (Math.Abs(p1[across] - q1[across]) > 1.5)
                        continue;
                    double lo = Math.Max(Math.Min(p1[along], p2[along]), Math.Min(q1[along], q2[along]));
                    double hi = Math.Min(Math.Max(p1[along], p2[along]), Math.Max(q1[along], q2[along]));
                    if (hi - lo > 2)
                        n++;
                }
            }
            return n;
        }

        // A horizontal and a vertical segment that cross in their interiors.
        private static bool ProperCross(double[] p1, double[] p2, double[] q1, double[] q2)
        {
            bool ph = Math.Abs(p1[1] - p2[1]) < 0.05, qh = Math.Abs(q1[1] - q2[1]) < 0.05;
            if (ph == qh)
                return false;
            double[] h1 = ph ? p1 : q1, h2 = ph ? p2 : q2, v1 = ph ? q1 : p1, v2 = ph ? q2 : p2;
            double x = v1[0], y = h1[1];
            double hx0 = Math.Min(h1[0], h2[0]), hx1 = Math.Max(h1[0], h2[0]);
            double vy0 = Math.Min(v1[1], v2[1]), vy1 = Math.Max(v1[1], v2[1]);
            return x > hx0 + 0.5 && x < hx1 - 0.5 && y > vy0 + 0.5 && y < vy1 - 0.5;
        }
    }
}
