using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The HUD's widgets on the 1920 x 1080 reference screen. Used by the on-screen HUD window and the stream window
// (Designer off) and by the HUD page's preview (Designer on: outlines, click to select, drag to move).
public partial class HudSurface : UserControl
{
    public static readonly StyledProperty<IEnumerable<HudWidget>?> WidgetsProperty =
        AvaloniaProperty.Register<HudSurface, IEnumerable<HudWidget>?>(nameof(Widgets));

    public static readonly StyledProperty<HudContext?> ContextProperty =
        AvaloniaProperty.Register<HudSurface, HudContext?>(nameof(Context));

    public static readonly StyledProperty<bool> DesignerProperty =
        AvaloniaProperty.Register<HudSurface, bool>(nameof(Designer));

    public static readonly StyledProperty<bool> FixedProperty =
        AvaloniaProperty.Register<HudSurface, bool>(nameof(Fixed));

    public static readonly StyledProperty<HudWidget?> SelectedWidgetProperty =
        AvaloniaProperty.Register<HudSurface, HudWidget?>(nameof(SelectedWidget), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<Cursor?> DragCursorProperty =
        AvaloniaProperty.Register<HudSurface, Cursor?>(nameof(DragCursor));

    private HudWidget? dragging;
    private HudPanel? dragPanel;
    private Point dragOffset;
    private HudContext? observedContext;

    public HudSurface()
    {
        InitializeComponent();
    }

    public IEnumerable<HudWidget>? Widgets
    {
        get => GetValue(WidgetsProperty);
        set => SetValue(WidgetsProperty, value);
    }

    public HudContext? Context
    {
        get => GetValue(ContextProperty);
        set => SetValue(ContextProperty, value);
    }

    public bool Designer
    {
        get => GetValue(DesignerProperty);
        set => SetValue(DesignerProperty, value);
    }

    public bool Fixed
    {
        get => GetValue(FixedProperty);
        set => SetValue(FixedProperty, value);
    }

    public HudWidget? SelectedWidget
    {
        get => GetValue(SelectedWidgetProperty);
        set => SetValue(SelectedWidgetProperty, value);
    }

    public Cursor? DragCursor
    {
        get => GetValue(DragCursorProperty);
        set => SetValue(DragCursorProperty, value);
    }

    // The panel that places the widgets (its Origin is the top-left widget corner when not Fixed).
    public HudPanel? Panel => this.FindDescendantOfType<HudPanel>();

    // The visible widgets with their measured size in reference pixels (scale included).
    public Dictionary<HudWidget, Size> MeasuredWidgets()
    {
        Dictionary<HudWidget, Size> sizes = [];
        HudPanel? panel = Panel;
        if (panel == null)
            return sizes;
        foreach (Control child in panel.Children)
        {
            if (child.IsVisible && child.DataContext is HudWidget widget && child.DesiredSize.Width > 0)
                sizes[widget] = child.DesiredSize;
        }
        return sizes;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DesignerProperty)
            DragCursor = Designer ? new Cursor(StandardCursorType.SizeAll) : null;
        if (change.Property == ContextProperty)
        {
            if (observedContext != null)
                observedContext.PropertyChanged -= OnContextChanged;
            observedContext = Context;
            if (observedContext != null)
                observedContext.PropertyChanged += OnContextChanged;
            ApplyContext();
        }
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e) => ApplyContext();

    // The templates use dynamic resources for the colours, so a new accent or style shows at once.
    private void ApplyContext()
    {
        HudContext? context = observedContext;
        if (context == null)
            return;
        Color accent = context.AccentColor;
        Resources["HudAccent"] = new SolidColorBrush(accent);
        Resources["HudAccentSoft"] = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        Resources["HudCardBackground"] = context.CardBackground;
        Resources["HudCardBorder"] = context.CardBorder;
    }

    private void WidgetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Designer || sender is not Control control || control.DataContext is not HudWidget widget)
            return;
        SelectedWidget = widget;
        HudPanel? panel = control.FindAncestorOfType<HudPanel>();
        if (panel == null || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;
        Point position = e.GetPosition(panel);
        dragOffset = new Point(position.X + panel.Origin.X - widget.X, position.Y + panel.Origin.Y - widget.Y);
        dragging = widget;
        dragPanel = panel;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void WidgetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragging == null || dragPanel == null)
            return;
        Point position = e.GetPosition(dragPanel);
        dragging.X = Math.Round(Math.Clamp(position.X + dragPanel.Origin.X - dragOffset.X, -40, HudLayout.ReferenceWidth - 40));
        dragging.Y = Math.Round(Math.Clamp(position.Y + dragPanel.Origin.Y - dragOffset.Y, -40, HudLayout.ReferenceHeight - 40));
        e.Handled = true;
    }

    private void WidgetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragging == null)
            return;
        dragging = null;
        dragPanel = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
