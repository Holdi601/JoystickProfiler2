using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The axis band editor of a virtual function (axis bands, axis ends to buttons): the axis travel as a bar split into
// bands, each band with its virtual output (lit while it fires), the edges as handles the user drags, the hysteresis
// around each edge, and the input value (preview or a running forwarder) as a marker. Same dark style as the curve
// and force feedback editors. Draws only when Avalonia asks (the editor's changes invalidate it).
public sealed class AxisBandEditor : Control
{
    public static readonly StyledProperty<VirtualFunctionEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<AxisBandEditor, VirtualFunctionEditorViewModel?>(nameof(Editor));

    private const double Pad = 16;
    private const double BarTop = 38;
    private const double BarHeight = 46;
    private const double GrabDistance = 10;

    private static readonly IBrush PanelBrush = new ImmutableSolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IPen FramePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly IBrush BandBrushA = new ImmutableSolidColorBrush(Color.Parse("#1E293B"));
    private static readonly IBrush BandBrushB = new ImmutableSolidColorBrush(Color.Parse("#243247"));
    private static readonly IBrush CurrentBrush = new ImmutableSolidColorBrush(Color.Parse("#0C4A6E"));
    private static readonly IBrush OnBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush HysteresisBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 245, 158, 11));
    private static readonly IPen EdgePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#F8FAFC")), 2);
    private static readonly IBrush HandleBrush = new ImmutableSolidColorBrush(Color.Parse("#F8FAFC"));
    private static readonly IBrush HandleActiveBrush = new ImmutableSolidColorBrush(Color.Parse("#FDE68A"));
    private static readonly IPen HandlePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0EA5E9")), 1.6);
    private static readonly IPen MarkerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#F43F5E")), 2);
    private static readonly IBrush MarkerBrush = new ImmutableSolidColorBrush(Color.Parse("#F43F5E"));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private VirtualFunctionEditorViewModel? editor;
    private int dragEdge = -1;
    private int hoverEdge = -1;

    static AxisBandEditor()
    {
        AffectsRender<AxisBandEditor>(EditorProperty);
        ClipToBoundsProperty.OverrideDefaultValue<AxisBandEditor>(true);
    }

    public AxisBandEditor()
    {
        MinHeight = 170;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public VirtualFunctionEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty)
            return;
        if (editor != null)
            editor.PropertyChanged -= OnEditorChanged;
        editor = change.GetNewValue<VirtualFunctionEditorViewModel?>();
        if (editor != null)
            editor.PropertyChanged += OnEditorChanged;
        InvalidateVisual();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 170);

    private double X(double unit) => Pad + Math.Clamp(unit, 0, 1) * Math.Max(1, Bounds.Width - 2 * Pad);

    private double Unit(double x) => Math.Clamp((x - Pad) / Math.Max(1, Bounds.Width - 2 * Pad), 0, 1);

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(PanelBrush, FramePen, bounds, 8, 8);
        VirtualFunctionEditorViewModel? model = Editor;
        if (model == null)
            return;
        IReadOnlyList<double> edges = model.EdgeValues;
        IReadOnlyList<VfBandInfo> bands = model.Bands;
        double hysteresis = model.Hysteresis;
        Text(context, "0 %", Pad, 8, LabelBrush, 11);
        Text(context, "100 %", Bounds.Width - Pad - 30, 8, LabelBrush, 11);

        // Bands.
        int count = edges.Count + 1;
        for (int b = 0; b < count; b++)
        {
            double from = b == 0 ? 0 : edges[b - 1];
            double to = b == count - 1 ? 1 : edges[b];
            Rect rect = new(X(from), BarTop, Math.Max(0, X(to) - X(from)), BarHeight);
            bool current = b == model.CurrentBand;
            context.DrawRectangle(current ? CurrentBrush : b % 2 == 0 ? BandBrushA : BandBrushB, null, rect);
            VfBandInfo? info = b < bands.Count ? bands[b] : null;
            if (info == null)
                continue;
            double width = rect.Width;
            // The output lamp and texts, centred in the band (shortened in narrow bands).
            double cx = rect.X + width / 2;
            context.DrawEllipse(info.On ? OnBrush : OffBrush, null, new Point(cx, BarTop + 13), 6, 6);
            Text(context, Fit(info.Label, width), cx, BarTop + 22, current ? TextBrush : LabelBrush, 11, centre: true, bold: current);
            Text(context, Fit(info.Output, width), cx, BarTop + BarHeight + 26, info.On ? OnBrush : LabelBrush, 11, centre: true);
        }

        context.DrawRectangle(null, FramePen, new Rect(X(0), BarTop, X(1) - X(0), BarHeight));

        // Hysteresis and edges with handles.
        for (int i = 0; i < edges.Count; i++)
        {
            double x = X(edges[i]);
            if (hysteresis > 0)
                context.DrawRectangle(HysteresisBrush, null, new Rect(X(edges[i] - hysteresis), BarTop, Math.Max(1, X(edges[i] + hysteresis) - X(edges[i] - hysteresis)), BarHeight));
            context.DrawLine(EdgePen, new Point(x, BarTop - 4), new Point(x, BarTop + BarHeight + 4));
            context.DrawEllipse(i == dragEdge || i == hoverEdge ? HandleActiveBrush : HandleBrush, HandlePen, new Point(x, BarTop - 8), 6.5, 6.5);
            Text(context, Math.Round(edges[i] * 100, 1).ToString(CultureInfo.InvariantCulture) + " %", x, BarTop + BarHeight + 8, TextBrush, 11, centre: true);
        }

        // The input.
        double input = model.DisplayInput;
        if (!double.IsNaN(input))
        {
            double x = X(input);
            context.DrawLine(MarkerPen, new Point(x, BarTop - 2), new Point(x, BarTop + BarHeight + 2));
            StreamGeometry triangle = new();
            using (StreamGeometryContext g = triangle.Open())
            {
                g.BeginFigure(new Point(x - 6, BarTop + BarHeight + 12), true);
                g.LineTo(new Point(x + 6, BarTop + BarHeight + 12));
                g.LineTo(new Point(x, BarTop + BarHeight + 3));
                g.EndFigure(true);
            }

            context.DrawGeometry(MarkerBrush, null, triangle);
        }

        string footer = (model.LiveActive ? "LIVE  " : T("Preview  ")) + model.StateText;
        Text(context, footer, Pad, Bounds.Height - 22, model.LiveActive ? OnBrush : TextBrush, 12, bold: true);
        Text(context, T("drag the white handles to move the edges; amber = hysteresis"), Bounds.Width - Pad - 330, Bounds.Height - 21, LabelBrush, 11);
    }

    private static string Fit(string text, double width)
    {
        int max = Math.Max(3, (int)(width / 6.2));
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    private static void Text(DrawingContext context, string text, double x, double y, IBrush brush, double size, bool centre = false, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
            return;
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush);
        context.DrawText(formatted, new Point(centre ? x - formatted.Width / 2 : x, y));
    }

    private int EdgeAt(Point point)
    {
        VirtualFunctionEditorViewModel? model = Editor;
        if (model == null)
            return -1;
        IReadOnlyList<double> edges = model.EdgeValues;
        int best = -1;
        double bestDistance = GrabDistance;
        for (int i = 0; i < edges.Count; i++)
        {
            double distance = Math.Abs(point.X - X(edges[i]));
            if (distance <= bestDistance && point.Y >= BarTop - 18 && point.Y <= BarTop + BarHeight + 6)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        dragEdge = EdgeAt(e.GetPosition(this));
        if (dragEdge >= 0)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (dragEdge >= 0)
        {
            Editor?.MoveEdge(dragEdge, Unit(point.X));
            InvalidateVisual();
            return;
        }

        int hover = EdgeAt(point);
        if (hover != hoverEdge)
        {
            hoverEdge = hover;
            Cursor = new Cursor(hover >= 0 ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragEdge < 0)
            return;
        dragEdge = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }
}
