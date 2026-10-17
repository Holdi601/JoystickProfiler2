using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// A node view of one virtual function in the style of the force feedback graph editor (FfbGraphCanvas): the physical
// inputs as nodes on the left, the function in the middle (coloured header with its icon, the state below), the
// virtual outputs on the right; wires light up while an output fires (preview or a running forwarder). Read-only: the
// rows next to it edit the function. Draws only when Avalonia asks.
public sealed class VirtualFunctionFlowView : Control
{
    public static readonly StyledProperty<VirtualFunctionEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<VirtualFunctionFlowView, VirtualFunctionEditorViewModel?>(nameof(Editor));

    private const double NodeWidth = 190;
    private const double RowHeight = 30;
    private const double Gap = 8;

    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(29, 32, 38)), 1);
    private static readonly IBrush BodyBrush = new ImmutableSolidColorBrush(Color.FromRgb(31, 35, 42));
    private static readonly IPen OutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(80, 87, 100)), 1);
    private static readonly IBrush InputHeader = new ImmutableSolidColorBrush(Color.FromRgb(95, 163, 255));
    private static readonly IBrush FunctionHeader = new ImmutableSolidColorBrush(Color.FromRgb(245, 190, 75));
    private static readonly IBrush OutputHeader = new ImmutableSolidColorBrush(Color.FromRgb(108, 203, 95));
    private static readonly IBrush TitleBrush = Brushes.White;
    private static readonly IBrush DarkTitleBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 17, 21));
    private static readonly IBrush DetailBrush = new ImmutableSolidColorBrush(Color.FromRgb(185, 190, 200));
    private static readonly IBrush OnBrush = new ImmutableSolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.FromRgb(71, 85, 105));
    private static readonly IBrush BarBackBrush = new ImmutableSolidColorBrush(Color.FromRgb(20, 23, 28));
    private static readonly IBrush BarBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IPen WirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 95, 163, 255)), 2.2);
    private static readonly IPen WireOnPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(74, 222, 128)), 3);
    private static readonly IPen WireIdlePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 80, 87, 100)), 1.6);
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private VirtualFunctionEditorViewModel? editor;

    static VirtualFunctionFlowView()
    {
        AffectsRender<VirtualFunctionFlowView>(EditorProperty);
        ClipToBoundsProperty.OverrideDefaultValue<VirtualFunctionFlowView>(true);
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
        Detach();
        editor = change.GetNewValue<VirtualFunctionEditorViewModel?>();
        if (editor != null)
        {
            editor.PropertyChanged += OnChanged;
            editor.Inputs.CollectionChanged += OnCollectionChanged;
            editor.Outputs.CollectionChanged += OnCollectionChanged;
            Attach();
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Detach()
    {
        if (editor == null)
            return;
        editor.PropertyChanged -= OnChanged;
        editor.Inputs.CollectionChanged -= OnCollectionChanged;
        editor.Outputs.CollectionChanged -= OnCollectionChanged;
        foreach (VfInputRoleItem input in editor.Inputs)
            input.PropertyChanged -= OnChanged;
        foreach (VfOutputRoleItem output in editor.Outputs)
            output.PropertyChanged -= OnChanged;
    }

    private void Attach()
    {
        if (editor == null)
            return;
        foreach (VfInputRoleItem input in editor.Inputs)
            input.PropertyChanged += OnChanged;
        foreach (VfOutputRoleItem output in editor.Outputs)
            output.PropertyChanged += OnChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (editor == null)
            return;
        foreach (VfInputRoleItem input in editor.Inputs)
            input.PropertyChanged -= OnChanged;
        foreach (VfOutputRoleItem output in editor.Outputs)
            output.PropertyChanged -= OnChanged;
        Attach();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Max(Editor?.Inputs.Count ?? 1, Editor?.Outputs.Count ?? 1);
        double height = Math.Max(150, 44 + rows * (RowHeight + Gap) + 20);
        return new Size(double.IsInfinity(availableSize.Width) ? 760 : availableSize.Width, height);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, bounds, 8, 8);
        for (double x = 12; x < bounds.Width; x += 24)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        for (double y = 12; y < bounds.Height; y += 24)
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
        VirtualFunctionEditorViewModel? model = Editor;
        if (model == null)
            return;

        double left = 14;
        double right = Math.Max(left + NodeWidth * 2 + 60, bounds.Width - NodeWidth - 14);
        double centreWidth = Math.Min(230, Math.Max(150, right - left - NodeWidth - 60));
        double centreX = left + NodeWidth + (right - left - NodeWidth - centreWidth) / 2;
        double top = 34;
        Text(context, T("Physical inputs"), left, 10, DetailBrush, 11, bold: true);
        Text(context, T("Virtual outputs"), right, 10, DetailBrush, 11, bold: true);

        // The function node.
        double centreHeight = Math.Max(90, Math.Max(model.Inputs.Count, model.Outputs.Count) * (RowHeight + Gap) - Gap);
        Rect node = new(centreX, top, centreWidth, centreHeight);
        context.DrawRectangle(BodyBrush, OutlinePen, node, 6, 6);
        context.DrawRectangle(FunctionHeader, null, new Rect(node.X, node.Y, node.Width, 26), 6, 6);
        Text(context, model.Icon + "  " + model.TypeName, node.X + 8, node.Y + 5, DarkTitleBrush, 13, bold: true);
        Text(context, Fit(model.Name, centreWidth - 16), node.X + 8, node.Y + 34, TitleBrush, 12, bold: true);
        Text(context, Fit(model.StateText, centreWidth - 16), node.X + 8, node.Y + 54, model.LiveActive ? OnBrush : DetailBrush, 11);
        if (model.LiveActive)
            Text(context, "LIVE", node.Right - 36, node.Y + 34, OnBrush, 11, bold: true);

        // Inputs.
        for (int i = 0; i < model.Inputs.Count; i++)
        {
            VfInputRoleItem input = model.Inputs[i];
            Rect box = new(left, top + i * (RowHeight + Gap), NodeWidth, RowHeight);
            context.DrawRectangle(BodyBrush, OutlinePen, box, 5, 5);
            context.DrawRectangle(InputHeader, null, new Rect(box.X, box.Y, 4, box.Height), 2, 2);
            string detail = input.IsSet ? VfTokensDescribe(input.SelectedInput!) + " - " + input.SelectedDevice!.Display : input.Optional ? T("(optional, not set)") : T("(not set)");
            Text(context, input.Label, box.X + 10, box.Y + 2, TitleBrush, 11, bold: true);
            Text(context, Fit(detail, NodeWidth - 16), box.X + 10, box.Y + 15, DetailBrush, 10);
            Point from = new(box.Right, box.Y + box.Height / 2);
            Point to = new(node.X, node.Y + 30 + (i + 0.5) * (node.Height - 30) / Math.Max(1, model.Inputs.Count));
            Wire(context, from, to, input.IsSet ? WirePen : WireIdlePen);
        }

        // Outputs.
        for (int i = 0; i < model.Outputs.Count; i++)
        {
            VfOutputRoleItem output = model.Outputs[i];
            Rect box = new(right, top + i * (RowHeight + Gap), NodeWidth, RowHeight);
            context.DrawRectangle(BodyBrush, OutlinePen, box, 5, 5);
            context.DrawRectangle(output.IsOn ? OnBrush : OutputHeader, null, new Rect(box.Right - 4, box.Y, 4, box.Height), 2, 2);
            context.DrawEllipse(output.IsOn ? OnBrush : OffBrush, null, new Point(box.X + 12, box.Y + box.Height / 2), 5, 5);
            Text(context, output.Label, box.X + 24, box.Y + 2, TitleBrush, 11, bold: true);
            Text(context, Fit(output.Describe + (output.ValueText.Length > 0 ? "  " + output.ValueText : string.Empty), NodeWidth - 34), box.X + 24, box.Y + 15, output.IsOn ? OnBrush : DetailBrush, 10);
            Point from = new(node.Right, node.Y + 30 + (i + 0.5) * (node.Height - 30) / Math.Max(1, model.Outputs.Count));
            Point to = new(box.X, box.Y + box.Height / 2);
            Wire(context, from, to, output.IsOn ? WireOnPen : output.TargetToken.Length > 0 ? WirePen : WireIdlePen);
        }

        // The first input's value as a bar under the function (axes).
        if (!double.IsNaN(model.DisplayInput))
        {
            Rect bar = new(node.X + 8, node.Bottom - 16, node.Width - 16, 7);
            context.DrawRectangle(BarBackBrush, null, bar, 3, 3);
            context.DrawRectangle(BarBrush, null, new Rect(bar.X, bar.Y, bar.Width * Math.Clamp(model.DisplayInput, 0, 1), bar.Height), 3, 3);
        }
    }

    private static string VfTokensDescribe(string token) => JoyPro.Engine.VJoy.VfTokens.Describe(token);

    private static void Wire(DrawingContext context, Point from, Point to, IPen pen)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            double dx = Math.Max(30, (to.X - from.X) / 2);
            g.BeginFigure(from, false);
            g.CubicBezierTo(new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y), to);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static string Fit(string text, double width)
    {
        int max = Math.Max(4, (int)(width / 5.8));
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    private static void Text(DrawingContext context, string text, double x, double y, IBrush brush, double size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
            return;
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush);
        context.DrawText(formatted, new Point(x, y));
    }
}
