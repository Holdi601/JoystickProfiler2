using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The callout sheet of the Layout page (docs/ui/layout-redesign.md, "Tables and lines"): the device picture with a
// table per control group and orthogonal leader lines, drawn like JoyPro 1's cheat sheets (the engine's
// EngineCalloutRenderer draws the same sheet for the exports).
//   in:  Callouts (CalloutSheetViewModel: the sheet, the row texts, the edits), Source (the photo) or Schematic
//        (JoyPro's drawing), Mode, HeaderGame / HeaderPlane (the GAME / PLANE band of a preview)
//   out: RowClicked (an input picked), RowChangeRequested (right-click > Change...), the view model's edits.
// Mouse: the wheel zooms at the pointer, dragging the background (or the middle button) pans, a click on a row picks
// its input. Edit mode: drag a table (it is pinned), its right edge resizes it, drag a line's segment sideways to bend
// it, drag a spot to move it, drag the round handle of the selected table to a spot on the picture to add its line;
// Del removes the selected line. A relation dropped on a row binds it (any mode). Keys: + / - zoom, 0 or F fit.
public sealed class CalloutSheetCanvas : Control
{
    public const string RelationDragFormat = "joypro-relation";

    public static readonly StyledProperty<CalloutSheetViewModel?> CalloutsProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, CalloutSheetViewModel?>(nameof(Callouts));

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, IImage?>(nameof(Source));

    public static readonly StyledProperty<DeviceSchematic?> SchematicProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, DeviceSchematic?>(nameof(Schematic));

    public static readonly StyledProperty<DeviceCanvasMode> ModeProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, DeviceCanvasMode>(nameof(Mode));

    public static readonly StyledProperty<string?> HeaderGameProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, string?>(nameof(HeaderGame));

    public static readonly StyledProperty<string?> HeaderPlaneProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, string?>(nameof(HeaderPlane));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, bool>(nameof(ShowGrid));

    public CalloutSheetViewModel? Callouts { get => GetValue(CalloutsProperty); set => SetValue(CalloutsProperty, value); }
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public DeviceSchematic? Schematic { get => GetValue(SchematicProperty); set => SetValue(SchematicProperty, value); }
    public DeviceCanvasMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public string? HeaderGame { get => GetValue(HeaderGameProperty); set => SetValue(HeaderGameProperty, value); }
    public string? HeaderPlane { get => GetValue(HeaderPlaneProperty); set => SetValue(HeaderPlaneProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    // What the shown device is doing right now: the row of a held input (or a moving axis) and its table's line
    // light up, pulsing (docs/ui/live-input.md).
    public static readonly StyledProperty<LiveInputState?> LiveInputProperty =
        AvaloniaProperty.Register<CalloutSheetCanvas, LiveInputState?>(nameof(LiveInput));

    public LiveInputState? LiveInput { get => GetValue(LiveInputProperty); set => SetValue(LiveInputProperty, value); }

    // The pulse's phase in render tests (null: the clock).
    internal double? LivePulsePhase { get; set; }

    private Avalonia.Threading.DispatcherTimer? livePulseTimer;

    private bool LiveAny => LiveInput is { } live && (live.Pressed.Count > 0 || live.ActiveAxes().Any());

    private double LivePulse => LivePulsePhase ?? 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 900.0 * 2 * Math.PI);

    private void OnLiveInputChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (LiveAny && livePulseTimer == null && VisualRoot != null)
        {
            livePulseTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            livePulseTimer.Tick += (_, _) =>
            {
                InvalidateVisual();
                if (!LiveAny)
                {
                    livePulseTimer?.Stop();
                    livePulseTimer = null;
                }
            };
            livePulseTimer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        livePulseTimer?.Stop();
        livePulseTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    // The tables that hold an input in use right now.
    private HashSet<string> LiveTables(CalloutSheet sheet)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        if (LiveInput is not { } live || !LiveAny)
            return ids;
        foreach (CalloutTable table in sheet.Tables)
        {
            if (table.Rows.Any(r => r.Input.Length > 0 && live.IsActive(r.Input)))
                ids.Add(table.Id);
        }
        return ids;
    }

    // A row was clicked: (input, modifier column).
    public event EventHandler<(string Input, bool Modifier)>? RowClicked;

    // Right-click > Change...: the host opens the bind panel on that input.
    public event EventHandler<(string Input, bool Modifier)>? RowChangeRequested;

    // The sheet's font: Bahnschrift SemiCondensed, then the language's fallbacks (CJK of the language, symbols).
    private static FontFamily SheetFont => SheetFonts.Get("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI, ");
    private readonly SchematicPainter schematicPainter = new();
    private double zoom = 1;
    private Vector pan;
    private bool userAdjusted;
    private double fittedWidth, fittedHeight;

    private enum Drag { None, Pan, Table, Resize, Bend, Line, Spot }

    private Drag drag;
    private Point pressPoint;
    private Vector panAtPress;
    private bool moved;
    private CalloutTable? dragTable;
    private CalloutLine? dragLine;
    private int dragSegment;
    private Point dragSheetStart, dragSheetNow;
    private double dragTableX, dragTableY, dragTableWidth;
    // The row a relation is dragged over (highlighted).
    private (CalloutTable Table, int Row, int Column)? dropTarget;

    public CalloutSheetCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { dropTarget = null; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CalloutsProperty)
        {
            if (change.OldValue is CalloutSheetViewModel old)
                old.Changed -= OnCalloutsChanged;
            if (change.NewValue is CalloutSheetViewModel now)
                now.Changed += OnCalloutsChanged;
            userAdjusted = false;
            InvalidateVisual();
        }
        else if (change.Property == LiveInputProperty)
        {
            if (change.OldValue is LiveInputState oldLive)
                oldLive.Changed -= OnLiveInputChanged;
            if (change.NewValue is LiveInputState newLive)
                newLive.Changed += OnLiveInputChanged;
            OnLiveInputChanged(this, EventArgs.Empty);
        }
        else if (change.Property == SourceProperty || change.Property == SchematicProperty || change.Property == ModeProperty ||
                 change.Property == HeaderGameProperty || change.Property == HeaderPlaneProperty || change.Property == ShowGridProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            if (!userAdjusted)
                Fit();
        }
    }

    private void OnCalloutsChanged(object? sender, EventArgs e)
    {
        CalloutSheet? sheet = Sheet;
        if (sheet != null && !userAdjusted && (Math.Abs(sheet.Width - fittedWidth) > 0.5 || Math.Abs(SheetHeight - fittedHeight) > 0.5))
            Fit();
        InvalidateVisual();
    }

    private CalloutSheet? Sheet => Callouts?.Sheet;

    private bool HasHeader => !string.IsNullOrWhiteSpace(HeaderGame) || !string.IsNullOrWhiteSpace(HeaderPlane);

    private double HeaderHeight => Sheet == null || !HasHeader ? 0 : Math.Round(Sheet.RowHeight * 1.6);

    private double SheetHeight => (Sheet?.Height ?? 0) + HeaderHeight;

    public static readonly StyledProperty<double> TopInsetProperty = AvaloniaProperty.Register<CalloutSheetCanvas, double>(nameof(TopInset));

    public double TopInset { get => GetValue(TopInsetProperty); set => SetValue(TopInsetProperty, value); }

    public void Fit()
    {
        CalloutSheet? sheet = Sheet;
        if (sheet == null || Bounds.Width <= 0 || Bounds.Height <= 0 || sheet.Width <= 0)
            return;
        // The space at the top that floating cards cover (the device card of the Layout page): the sheet fits below.
        double top = Math.Min(TopInset, Bounds.Height * 0.25);
        double z = Math.Min((Bounds.Width - 24) / sheet.Width, (Bounds.Height - 24 - top) / SheetHeight);
        zoom = Math.Clamp(z, 0.03, 4);
        pan = new Vector((Bounds.Width - sheet.Width * zoom) / 2, top + (Bounds.Height - top - SheetHeight * zoom) / 2);
        fittedWidth = sheet.Width;
        fittedHeight = SheetHeight;
        userAdjusted = false;
        InvalidateVisual();
    }

    private Point ToSheet(Point view) => new((view.X - pan.X) / zoom, (view.Y - pan.Y) / zoom);

    private Point ToView(double x, double y) => new(x * zoom + pan.X, y * zoom + pan.Y);

    private Rect ToView(double[] r) => new(r[0] * zoom + pan.X, r[1] * zoom + pan.Y, r[2] * zoom, r[3] * zoom);

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ||
                           (ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light && Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

    // ------------------------------------------------------------------ drawing (the engine's palette)

    private sealed record Palette(Color Page, Color Table, Color Title, Color Mod, Color Border, Color Grid, Color Text, Color Faint, Color Line,
        Color Header, Color HeaderText, Color HeaderBox, Color Accent, Color DropFill);

    private static readonly Palette DarkPalette = new(Color.Parse("#2B3038"), Color.Parse("#F7F7F7"), Color.Parse("#D5DAE1"), Color.Parse("#E9ECF0"),
        Color.Parse("#0E1116"), Color.Parse("#9AA1AB"), Color.Parse("#0E1116"), Color.Parse("#8A919B"), Color.Parse("#E6E8EB"),
        Color.Parse("#15181D"), Color.Parse("#F2F4F7"), Color.Parse("#F7F7F7"), Color.Parse("#3B82F6"), Color.Parse("#C7DBFF"));

    private static readonly Palette LightPalette = new(Color.Parse("#FFFFFF"), Color.Parse("#FFFFFF"), Color.Parse("#E3E7ED"), Color.Parse("#F3F5F8"),
        Color.Parse("#2F3640"), Color.Parse("#B7BDC6"), Color.Parse("#111418"), Color.Parse("#9AA1AB"), Color.Parse("#7C8794"),
        Color.Parse("#2F3640"), Color.Parse("#FFFFFF"), Color.Parse("#FFFFFF"), Color.Parse("#2563EB"), Color.Parse("#C7DBFF"));

    public override void Render(DrawingContext context)
    {
        Palette pal = IsDark ? DarkPalette : LightPalette;
        // On JoyPro's own drawing the leader lines take the drawing style's leader colour (the engine's sheets too).
        if (Source is LineArtImage art)
            pal = pal with { Line = Color.Parse(art.Colors(IsDark).Accent) };
        context.FillRectangle(new SolidColorBrush(IsDark ? Color.Parse("#1B1F25") : Color.Parse("#E9EDF2")), new Rect(Bounds.Size));
        CalloutSheet? sheet = Sheet;
        CalloutSheetViewModel? vm = Callouts;
        if (sheet == null || vm == null)
            return;
        if (fittedWidth <= 0 && !userAdjusted)
            Fit();
        Rect page = new(pan.X, pan.Y, sheet.Width * zoom, SheetHeight * zoom);
        context.DrawRectangle(new SolidColorBrush(pal.Page), null, page);

        Rect picture = ToView([sheet.PictureX, sheet.PictureY, sheet.PictureWidth, sheet.PictureHeight]);
        if (Source is { } image && image.Size.Width > 0)
            context.DrawImage(image, new Rect(image.Size), picture);
        else if (Schematic is { Width: > 0 } schematic)
        {
            context.FillRectangle(new SolidColorBrush(IsDark ? Color.Parse("#0B1220") : Color.Parse("#EEF2F7")), picture);
            using (context.PushClip(picture))
                schematicPainter.Draw(context, schematic, picture, IsDark);
        }

        if (ShowGrid && Mode == DeviceCanvasMode.EditMarkers && vm.GridSize > 1)
        {
            double step = vm.GridSize;
            while (step * zoom < 8)
                step *= 2;
            IPen gridPen = new Pen(new SolidColorBrush(Color.Parse("#2E93C5FD")), 1);
            for (double x = 0; x <= sheet.Width; x += step)
                context.DrawLine(gridPen, ToView(x, 0), ToView(x, sheet.Height));
            for (double y = 0; y <= sheet.Height; y += step)
                context.DrawLine(gridPen, ToView(0, y), ToView(sheet.Width, y));
        }

        double rh = sheet.RowHeight;
        Typeface regular = new(SheetFont), bold = new(SheetFont, FontStyle.Normal, FontWeight.Bold);
        DrawLines(context, sheet, vm, pal);
        foreach (CalloutTable table in sheet.Tables)
            DrawTable(context, sheet, vm, table, pal, regular, bold);
        if (sheet.OtherY >= 0)
            DrawText(context, T("Other controls (no spot on this picture yet: drag a table's handle to its spot, or use the marker assistant)"), bold, rh * 0.42,
                new SolidColorBrush(IsDark ? pal.HeaderText : pal.Text), new Rect(ToView(rh * 0.8, sheet.OtherY), new Size(sheet.Width * zoom, sheet.TitleHeight * zoom)));
        if (HasHeader)
            DrawHeader(context, sheet, pal, regular);

        // A line being dragged from a table's handle.
        if (drag == Drag.Line && dragTable != null)
        {
            Point from = ToView(HandleOf(sheet, dragTable).X, HandleOf(sheet, dragTable).Y);
            Point to = ToView(dragSheetNow.X, dragSheetNow.Y);
            IPen pen = new Pen(new SolidColorBrush(pal.Accent), 2, new DashStyle([4, 3], 0));
            context.DrawLine(pen, from, new Point(to.X, from.Y));
            context.DrawLine(pen, new Point(to.X, from.Y), to);
            context.DrawEllipse(new SolidColorBrush(pal.Accent), null, to, 5, 5);
        }
    }

    private void DrawLines(DrawingContext context, CalloutSheet sheet, CalloutSheetViewModel vm, Palette pal)
    {
        double width = Math.Max(1.2, sheet.RowHeight * 0.07 * zoom);
        IPen pen = new Pen(new SolidColorBrush(pal.Line), width, lineJoin: PenLineJoin.Miter);
        IPen glow = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), width * 3, lineJoin: PenLineJoin.Miter);
        IPen selected = new Pen(new SolidColorBrush(pal.Accent), width * 2.2, lineJoin: PenLineJoin.Miter);
        double r = Math.Max(2.5, sheet.RowHeight * 0.13 * zoom);
        HashSet<string> liveTables = LiveTables(sheet);
        double pulse = LivePulse;
        Color liveColor = Color.Parse("#F59E0B");
        IPen livePen = new Pen(new SolidColorBrush(liveColor), width * (2.2 + 1.2 * pulse), lineJoin: PenLineJoin.Miter);
        foreach (CalloutLine line in sheet.Lines)
        {
            List<Point> points = line.Points.Select(p => ToView(p[0], p[1])).ToList();
            if (points.Count < 2)
                continue;
            bool isSelected = ReferenceEquals(line, vm.SelectedLine) || (vm.SelectedTable != null && string.Equals(line.Table, vm.SelectedTable.Id, StringComparison.OrdinalIgnoreCase));
            bool isLive = liveTables.Contains(line.Table);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (IsDark)
                    context.DrawLine(glow, points[i], points[i + 1]);
                context.DrawLine(isLive ? livePen : isSelected ? selected : pen, points[i], points[i + 1]);
            }
            if (isLive)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(70 + 110 * pulse), liveColor.R, liveColor.G, liveColor.B)), null, points[0], r * (2.4 + pulse), r * (2.4 + pulse));
            context.DrawEllipse(new SolidColorBrush(isLive ? liveColor : isSelected ? pal.Accent : pal.Line), new Pen(new SolidColorBrush(pal.Border), Math.Max(1, width * 0.8)), points[0], r, r);
        }
        // Spots of tables without a line (removed by the user): a small ring, so the part stays visible.
        foreach (CalloutTable t in sheet.Tables.Where(t => t.Known && sheet.LineOf(t.Id) == null))
            context.DrawEllipse(null, new Pen(new SolidColorBrush(pal.Line), Math.Max(1, width)), ToView(sheet.SpotOf(t)[0], sheet.SpotOf(t)[1]), r, r);
    }

    private void DrawTable(DrawingContext context, CalloutSheet sheet, CalloutSheetViewModel vm, CalloutTable table, Palette pal, Typeface regular, Typeface bold)
    {
        double[] r = sheet.RectOf(table);
        if (drag == Drag.Table && ReferenceEquals(table, dragTable))
            r = [dragTableX, dragTableY, r[2], r[3]];
        if (drag == Drag.Resize && ReferenceEquals(table, dragTable))
            r = [r[0], r[1], dragTableWidth, r[3]];
        Rect rect = ToView(r);
        double rh = sheet.RowHeight * zoom, th = sheet.TitleHeight * zoom;
        IBrush text = new SolidColorBrush(pal.Text), faint = new SolidColorBrush(pal.Faint);
        IPen gridPen = new Pen(new SolidColorBrush(pal.Grid), Math.Max(0.6, rh * 0.03));
        bool isSelected = ReferenceEquals(table, vm.SelectedTable);
        context.FillRectangle(new SolidColorBrush(pal.Table), rect);
        context.FillRectangle(new SolidColorBrush(pal.Title), new Rect(rect.X, rect.Y, rect.Width, th));
        string title = table.Title + (table.Pinned && Mode == DeviceCanvasMode.EditMarkers ? "  (pinned)" : string.Empty);
        List<CalloutRow> rows = table.VisibleRows();
        double modWidth = sheet.ModColumnWidth(table) * zoom;
        int columns = sheet.ModColumnCount(table);
        double colWidth = columns > 0 ? modWidth / columns : 0;
        // Several modifier combinations: each column is headed with its combination ("Shift A + B"); the title keeps
        // the main column.
        bool headed = columns > 0 && sheet.Combos.Count > 0;
        DrawText(context, title, bold, sheet.RowHeight * 0.42, text, new Rect(rect.X + rh * 0.25, rect.Y, (headed ? rect.Width - modWidth : rect.Width) - rh * 0.5, th));
        if (headed)
        {
            for (int c = 1; c <= columns; c++)
            {
                Rect head = new(rect.Right - modWidth + (c - 1) * colWidth, rect.Y, colWidth, th);
                bool picked = isSelected && vm.SelectedColumn == c;
                context.FillRectangle(new SolidColorBrush(picked ? pal.DropFill : pal.Mod), head);
                context.DrawLine(gridPen, head.TopLeft, head.BottomLeft);
                DrawText(context, CalloutCombos.Label(sheet.ComboOfColumn(c)), bold, sheet.RowHeight * 0.34, faint, new Rect(head.X + rh * 0.12, head.Y, head.Width - rh * 0.2, th));
            }
        }
        context.DrawLine(gridPen, new Point(rect.X, rect.Y + th), new Point(rect.Right, rect.Y + th));
        for (int i = 0; i < rows.Count; i++)
        {
            CalloutRow row = rows[i];
            double[] c = sheet.CellOf(table, i, false);
            Rect cell = new(rect.X + (c[0] - table.X) * zoom, rect.Y + (c[1] - table.Y) * zoom, table.IsGrid ? c[2] * zoom : rect.Width - modWidth, rh);
            bool rowSelected = isSelected && vm.SelectedRow == i;
            if (dropTarget is { } drop && ReferenceEquals(drop.Table, table) && drop.Row == i)
                context.FillRectangle(new SolidColorBrush(pal.DropFill), drop.Column > 0 && colWidth > 0 ? ColumnCell(rect, modWidth, colWidth, drop.Column, cell.Y, rh) : cell);
            else if (rowSelected)
                context.FillRectangle(new SolidColorBrush(pal.DropFill, 0.6), vm.SelectedColumn > 0 && colWidth > 0 ? ColumnCell(rect, modWidth, colWidth, vm.SelectedColumn, cell.Y, rh) : cell);
            if (row.Input.Length > 0 && LiveInput is { } live && live.IsActive(row.Input))
            {
                // The input in use: the whole row (all modifier columns) pulses in amber.
                Rect full = new(rect.X, cell.Y, table.IsGrid ? cell.Width : rect.Width, rh);
                context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(80 + 90 * LivePulse), 245, 158, 11)), full);
                context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), Math.Max(1.5, rh * 0.06)), full);
            }
            string main = vm.RowText(table, row, 0);
            if (table.IsGrid)
            {
                // A key's cell holds its lines (a switch's up and down), parted faintly.
                if (sheet.FirstLineOfCell(table, rows, i))
                {
                    double[] box = sheet.KeyCellOf(table, i);
                    context.DrawRectangle(null, gridPen, new Rect(rect.X + (box[0] - table.X) * zoom, rect.Y + (box[1] - table.Y) * zoom, box[2] * zoom, box[3] * zoom));
                }
                else
                {
                    context.DrawLine(new Pen(new SolidColorBrush(pal.Grid, 0.35), Math.Max(0.5, rh * 0.02)), new Point(cell.X + rh * 0.3, cell.Y), new Point(cell.Right - rh * 0.3, cell.Y));
                }
                string label = row.Label + ":";
                double labelWidth = DrawText(context, label, bold, sheet.RowHeight * 0.36, faint, new Rect(cell.X + rh * 0.15, cell.Y, cell.Width * 0.4, rh));
                DrawText(context, main, regular, sheet.RowHeight * 0.5, text, new Rect(cell.X + rh * 0.2 + labelWidth, cell.Y, cell.Width - labelWidth - rh * 0.3, rh));
                continue;
            }
            if (i > 0)
                context.DrawLine(gridPen, new Point(rect.X, cell.Y), new Point(rect.Right, cell.Y));
            DrawIcon(context, row, new Rect(cell.X, cell.Y, rh, rh), pal, bold);
            Rect textRect = new(cell.X + rh * 1.08, cell.Y, cell.Width - rh * 1.2, rh);
            if (main.Length > 0)
                DrawText(context, main, regular, sheet.RowHeight * 0.5, text, textRect);
            else if (row.Input.Length == 0 && row.Icon != "zone")
                DrawText(context, T("not mapped: click to map it"), regular, sheet.RowHeight * 0.38, faint, textRect);
            else if (row.Icon == "zone" || Mode != DeviceCanvasMode.View)
                DrawText(context, row.Icon == "zone" ? row.Label : row.Label + (row.Input.Length > 0 ? " · " + DeviceMarkerText.ShortLabel(row.Input) : string.Empty),
                    regular, sheet.RowHeight * 0.42, faint, textRect);
            for (int col = 1; col <= columns; col++)
            {
                Rect modCell = ColumnCell(rect, modWidth, colWidth, col, cell.Y, rh);
                if (!(dropTarget is { } d && ReferenceEquals(d.Table, table) && d.Row == i && d.Column == col) && !(rowSelected && vm.SelectedColumn == col))
                    context.FillRectangle(new SolidColorBrush(pal.Mod), modCell);
                context.DrawLine(gridPen, modCell.TopLeft, modCell.BottomLeft);
                DrawText(context, vm.RowText(table, row, col), regular, sheet.RowHeight * (columns > 2 ? 0.44 : 0.5), text, new Rect(modCell.X + rh * 0.15, modCell.Y, modCell.Width - rh * 0.25, rh));
            }
        }
        if (!table.IsGrid)
            context.DrawLine(gridPen, new Point(rect.X + rh, rect.Y + th), new Point(rect.X + rh, rect.Bottom));
        context.DrawRectangle(new Pen(new SolidColorBrush(isSelected ? pal.Accent : pal.Border), Math.Max(1.2, sheet.RowHeight * 0.07 * zoom) * (isSelected ? 1.6 : 1)), rect);

        if (isSelected && Mode == DeviceCanvasMode.EditMarkers)
        {
            // The resize grip on the right edge and the line handle where the line meets the table.
            IBrush accent = new SolidColorBrush(pal.Accent);
            context.DrawRectangle(accent, new Pen(Brushes.White, 1.5), new Rect(rect.Right - 4, rect.Y + rect.Height / 2 - 9, 8, 18), 3, 3);
            Point handle = ToView(HandleOf(sheet, table).X, HandleOf(sheet, table).Y);
            context.DrawEllipse(accent, new Pen(Brushes.White, 2), handle, 7, 7);
        }
    }

    // Where a table's line handle is: the end of its line, else the middle of the edge nearest the picture.
    private static Point HandleOf(CalloutSheet sheet, CalloutTable table)
    {
        CalloutLine? line = sheet.LineOf(table.Id);
        if (line != null && line.Points.Count > 0)
            return new Point(line.Points[^1][0], line.Points[^1][1]);
        double[] r = sheet.RectOf(table);
        double cx = sheet.PictureX + sheet.PictureWidth / 2, cy = sheet.PictureY + sheet.PictureHeight / 2;
        double ay = r[1] + sheet.TitleHeight + sheet.RowHeight / 2;
        if (r[0] + r[2] < cx - sheet.PictureWidth / 2)
            return new Point(r[0] + r[2], ay);
        if (r[0] > cx + sheet.PictureWidth / 2)
            return new Point(r[0], ay);
        return new Point(r[0] + r[2] / 2, r[1] + r[3] < cy ? r[1] + r[3] : r[1]);
    }

    // Modifier column c (1..) of a row in view coordinates.
    private static Rect ColumnCell(Rect table, double modWidth, double colWidth, int column, double y, double rh) =>
        new(table.Right - modWidth + (column - 1) * colWidth, y, colWidth, rh);

    private void DrawIcon(DrawingContext context, CalloutRow row, Rect cell, Palette pal, Typeface bold)
    {
        double cx = cell.Center.X, cy = cell.Center.Y, r = cell.Height * 0.34;
        IBrush ink = new SolidColorBrush(pal.Text);
        IPen ring = new Pen(ink, Math.Max(1, cell.Height * 0.07));
        switch (row.Icon)
        {
            case "button":
                context.DrawEllipse(ink, null, new Point(cx, cy), r, r * 0.55);
                return;
            case "zone":
                context.FillRectangle(ink, new Rect(cx - r, cy - r * 0.18, r * 2, r * 0.36));
                return;
            case "mid":
                context.DrawEllipse(null, ring, new Point(cx, cy), r, r);
                context.DrawEllipse(ink, null, new Point(cx, cy), r * 0.35, r * 0.35);
                return;
            case "pos":
                context.DrawEllipse(null, ring, new Point(cx, cy), r, r);
                DrawText(context, row.Label, bold, cell.Height * 0.36 / Math.Max(zoom, 0.0001), ink, new Rect(cx - r, cy - r, r * 2, r * 2), center: true);
                return;
            case "axis":
                context.DrawEllipse(null, ring, new Point(cx, cy), r, r);
                foreach (double angle in new[] { 0.0, 90, 180, 270 })
                    context.DrawGeometry(ink, null, Arrow(cx, cy, r * 0.75, angle, r * 0.42));
                return;
        }
        double a = row.Icon switch
        {
            "right" => 0, "downright" => 45, "down" => 90, "downleft" => 135, "left" => 180, "upleft" => 225, "up" => 270, "upright" => 315, _ => double.NaN
        };
        if (double.IsNaN(a))
        {
            context.DrawEllipse(ink, null, new Point(cx, cy), r, r * 0.55);
            return;
        }
        context.DrawEllipse(null, ring, new Point(cx, cy), r, r);
        context.DrawGeometry(ink, null, Arrow(cx, cy, r * 0.72, a, r * 0.7));
        double rad = a * Math.PI / 180;
        context.DrawLine(new Pen(ink, Math.Max(1, cell.Height * 0.08)), new Point(cx - Math.Cos(rad) * r * 0.55, cy - Math.Sin(rad) * r * 0.55), new Point(cx + Math.Cos(rad) * r * 0.1, cy + Math.Sin(rad) * r * 0.1));
    }

    private static Geometry Arrow(double cx, double cy, double reach, double angle, double size)
    {
        double a = angle * Math.PI / 180;
        double tx = cx + Math.Cos(a) * reach, ty = cy + Math.Sin(a) * reach;
        double bx = cx + Math.Cos(a) * (reach - size), by = cy + Math.Sin(a) * (reach - size);
        double nx = -Math.Sin(a) * size * 0.6, ny = Math.Cos(a) * size * 0.6;
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(tx, ty), true);
            g.LineTo(new Point(bx + nx, by + ny));
            g.LineTo(new Point(bx - nx, by - ny));
            g.EndFigure(true);
        }
        return geometry;
    }

    private void DrawHeader(DrawingContext context, CalloutSheet sheet, Palette pal, Typeface regular)
    {
        double h = HeaderHeight, top = sheet.Height, pad = h * 0.18;
        context.FillRectangle(new SolidColorBrush(pal.Header), ToView([0, top, sheet.Width, h]));
        (string Label, string Value)[] fields = [("GAME:", HeaderGame ?? string.Empty), ("PLANE:", HeaderPlane ?? string.Empty)];
        double slot = sheet.Width / fields.Length, x = pad;
        foreach ((string label, string value) in fields)
        {
            double used = DrawText(context, label, regular, h * 0.56, new SolidColorBrush(pal.HeaderText), ToView([x, top, slot, h]));
            double labelWidth = used / zoom;
            Rect box = ToView([x + labelWidth + pad * 0.5, top + pad, Math.Max(10, slot - labelWidth - pad * 2.5), h - pad * 2]);
            context.FillRectangle(new SolidColorBrush(pal.HeaderBox), box);
            DrawText(context, value, regular, h * 0.42, new SolidColorBrush(pal.Text), box.Deflate(new Thickness(pad * 0.5 * zoom, 0)));
            x += slot;
        }
    }

    // Text in a box, vertically centred, cut with an ellipsis; size in sheet units. Returns the drawn width (view).
    private double DrawText(DrawingContext context, string text, Typeface typeface, double size, IBrush brush, Rect box, bool center = false)
    {
        if (string.IsNullOrEmpty(text) || box.Width <= 2 || box.Height <= 2)
            return 0;
        double viewSize = Math.Max(1, size * zoom);
        if (viewSize < 3)
            return 0;
        using TextLayout layout = new(text, typeface, viewSize, brush, center ? TextAlignment.Center : TextAlignment.Left,
            TextWrapping.NoWrap, TextTrimming.CharacterEllipsis, maxWidth: box.Width, maxHeight: box.Height);
        double y = box.Y + (box.Height - layout.Height) / 2;
        layout.Draw(context, new Point(box.X, y));
        return Math.Min(box.Width, layout.WidthIncludingTrailingWhitespace);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point p = e.GetPosition(this);
        double next = Math.Clamp(zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.03, 4);
        pan = new Vector(p.X - (p.X - pan.X) * next / zoom, p.Y - (p.Y - pan.Y) * next / zoom);
        zoom = next;
        userAdjusted = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        CalloutSheet? sheet = Sheet;
        CalloutSheetViewModel? vm = Callouts;
        Point p = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        pressPoint = p;
        moved = false;
        if (sheet == null || vm == null || props.IsMiddleButtonPressed)
        {
            StartPan(e);
            return;
        }
        Point s = ToSheet(p);
        double tolerance = 6 / zoom;
        if (props.IsRightButtonPressed)
        {
            if (vm.RowAt(s.X, s.Y, out CalloutTable? rt, out int rr, out int rm) && rt != null)
                OpenRowMenu(vm, rt, rr, rm);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        if (Mode == DeviceCanvasMode.EditMarkers)
        {
            CalloutTable? selected = vm.SelectedTable;
            if (selected != null)
            {
                Point handle = HandleOf(sheet, selected);
                if (Math.Abs(handle.X - s.X) <= 9 / zoom && Math.Abs(handle.Y - s.Y) <= 9 / zoom)
                {
                    Begin(Drag.Line, selected, s, e);
                    return;
                }
                double[] r = sheet.RectOf(selected);
                if (Math.Abs(s.X - (r[0] + r[2])) <= 6 / zoom && s.Y >= r[1] && s.Y <= r[1] + r[3])
                {
                    Begin(Drag.Resize, selected, s, e);
                    dragTableWidth = r[2];
                    return;
                }
            }
            CalloutTable? spotTable = sheet.Tables.FirstOrDefault(t => t.Known && Math.Abs(sheet.SpotOf(t)[0] - s.X) <= 8 / zoom && Math.Abs(sheet.SpotOf(t)[1] - s.Y) <= 8 / zoom);
            if (spotTable != null)
            {
                vm.SelectedTable = spotTable;
                Begin(Drag.Spot, spotTable, s, e);
                return;
            }
            CalloutTable? table = vm.TableAt(s.X, s.Y);
            if (table != null)
            {
                vm.SelectedLine = null;
                vm.SelectRow(table, -1, false);
                if (sheet.HitRow(table, s.X, s.Y, out int row, out int column))
                    vm.SelectRow(table, row, column);
                Begin(Drag.Table, table, s, e);
                dragTableX = table.X;
                dragTableY = table.Y;
                return;
            }
            CalloutLine? line = vm.LineAt(s.X, s.Y, tolerance, out int segment);
            if (line != null)
            {
                vm.SelectedLine = line;
                vm.SelectedTable = sheet.Table(line.Table);
                dragLine = line;
                dragSegment = segment;
                Begin(Drag.Bend, vm.SelectedTable, s, e);
                return;
            }
            vm.SelectedLine = null;
            vm.SelectRow(null, -1, false);
            StartPan(e);
            return;
        }

        if (vm.RowAt(s.X, s.Y, out CalloutTable? hit, out int hitRow, out int hitColumn) && hit != null)
        {
            vm.SelectRow(hit, hitRow, hitColumn);
            CalloutRow? row = vm.RowOf(hit, hitRow);
            // A row without an input (a hat's press that is not mapped) is clicked too: the host maps it.
            if (row != null && (row.Input.Length > 0 || row.Part.Length > 0))
                RowClicked?.Invoke(this, (row.Input, hitColumn > 0));
            e.Handled = true;
            return;
        }
        StartPan(e);
    }

    private void Begin(Drag kind, CalloutTable? table, Point sheetPoint, PointerPressedEventArgs e)
    {
        drag = kind;
        dragTable = table;
        dragSheetStart = sheetPoint;
        dragSheetNow = sheetPoint;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    private void StartPan(PointerPressedEventArgs e)
    {
        drag = Drag.Pan;
        panAtPress = pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag == Drag.None)
            return;
        Point p = e.GetPosition(this);
        if (!moved && Math.Abs(p.X - pressPoint.X) + Math.Abs(p.Y - pressPoint.Y) < 4)
            return;
        moved = true;
        Point s = ToSheet(p);
        dragSheetNow = s;
        CalloutSheetViewModel? vm = Callouts;
        switch (drag)
        {
            case Drag.Pan:
                pan = panAtPress + (p - pressPoint);
                userAdjusted = true;
                break;
            case Drag.Table when dragTable != null && vm != null:
                dragTableX = vm.Snap(dragTable.X + s.X - dragSheetStart.X);
                dragTableY = vm.Snap(dragTable.Y + s.Y - dragSheetStart.Y);
                break;
            case Drag.Resize when dragTable != null && Sheet != null:
                dragTableWidth = Math.Max(Sheet.RowHeight * 4, Sheet.WidthOf(dragTable) + s.X - dragSheetStart.X);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Drag kind = drag;
        drag = Drag.None;
        e.Pointer.Capture(null);
        CalloutSheetViewModel? vm = Callouts;
        CalloutSheet? sheet = Sheet;
        if (vm == null || sheet == null || !moved || dragTable == null && kind != Drag.Bend)
        {
            InvalidateVisual();
            return;
        }
        Point s = ToSheet(e.GetPosition(this));
        switch (kind)
        {
            case Drag.Table:
                vm.MoveTable(dragTable!, dragTableX, dragTableY);
                break;
            case Drag.Resize:
                vm.ResizeTable(dragTable!, dragTableWidth);
                break;
            case Drag.Line:
                vm.AddLine(dragTable!, s.X, s.Y);
                break;
            case Drag.Spot:
                vm.MoveSpot(dragTable!, s.X, s.Y);
                break;
            case Drag.Bend when dragLine != null && dragSegment >= 0 && dragSegment + 1 < dragLine.Points.Count:
                double[] a = dragLine.Points[dragSegment], b = dragLine.Points[dragSegment + 1];
                bool horizontal = Math.Abs(a[1] - b[1]) < 0.05;
                vm.BendLine(dragLine, dragSegment, horizontal ? s.Y - dragSheetStart.Y : s.X - dragSheetStart.X);
                break;
        }
        dragTable = null;
        dragLine = null;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        drag = Drag.None;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        CalloutSheetViewModel? vm = Callouts;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                zoom = Math.Clamp(zoom * 1.25, 0.03, 4);
                userAdjusted = true;
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                zoom = Math.Clamp(zoom / 1.25, 0.03, 4);
                userAdjusted = true;
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 or Key.F:
                Fit();
                e.Handled = true;
                break;
            case Key.Delete when vm?.SelectedLine != null && Mode == DeviceCanvasMode.EditMarkers:
                vm.DeleteLine(vm.SelectedLine);
                e.Handled = true;
                break;
            case Key.Escape when vm != null:
                vm.SelectedLine = null;
                vm.SelectRow(null, -1, false);
                e.Handled = true;
                break;
        }
    }

    private void OpenRowMenu(CalloutSheetViewModel vm, CalloutTable table, int row, int column)
    {
        CalloutRow? target = vm.RowOf(table, row);
        if (target == null || target.Input.Length == 0)
            return;
        vm.SelectRow(table, row, column);
        bool bound = vm.RowText(table, target, column).Length > 0;
        MenuItem change = new() { Header = T("Change...") };
        change.Click += (_, _) => RowChangeRequested?.Invoke(this, (target.Input, column > 0));
        MenuItem clear = new() { Header = column > 0 ? T("Clear the modifier bind") : T("Clear"), IsEnabled = bound };
        clear.Click += (_, _) => vm.ClearRow(table, row, column);
        ContextMenu menu = new() { ItemsSource = new[] { change, clear } };
        menu.Open(this);
    }

    // ------------------------------------------------------------------ dropping a relation on a row

    // The relation name of a drag (text "joypro-relation:<name>"), else null.
    public static string? RelationOf(DragEventArgs e)
    {
#pragma warning disable CS0618 // DataFormats.Text / Get: the Avalonia 11 drag data API
        string? text = e.Data.Contains(DataFormats.Text) ? e.Data.Get(DataFormats.Text) as string : null;
#pragma warning restore CS0618
        return text != null && text.StartsWith(RelationDragFormat + ":", StringComparison.Ordinal) ? text[(RelationDragFormat.Length + 1)..] : null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        CalloutSheetViewModel? vm = Callouts;
        dropTarget = null;
        e.DragEffects = DragDropEffects.None;
        if (vm != null && RelationOf(e) != null)
        {
            Point s = ToSheet(e.GetPosition(this));
            if (vm.RowAt(s.X, s.Y, out CalloutTable? table, out int row, out int column) && table != null && vm.RowOf(table, row)?.Input.Length > 0)
            {
                dropTarget = (table, row, column);
                e.DragEffects = DragDropEffects.Link;
            }
        }
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        dropTarget = null;
        CalloutSheetViewModel? vm = Callouts;
        string? relation = RelationOf(e);
        if (vm == null || relation == null)
            return;
        Point s = ToSheet(e.GetPosition(this));
        if (vm.RowAt(s.X, s.Y, out CalloutTable? table, out int row, out int column) && table != null)
            vm.DropRelation(table, row, column, relation);
        InvalidateVisual();
    }
}
