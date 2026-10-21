using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The page of a device layout with several views (docs/device-reference.md "Several views on one page"): the page
// (its format) fitted into the control, each view (its picture with its tables and lines, rendered by the engine) at
// its place and scale. A click selects a view, dragging moves it, the corner handle (or the mouse wheel over the
// selected view) scales it; the views stay on the page. ItemChanged: a view was moved or scaled (the host saves).
public sealed class MultiViewCanvas : Control
{
    public static readonly StyledProperty<IList<MultiViewItemModel>?> ItemsProperty =
        AvaloniaProperty.Register<MultiViewCanvas, IList<MultiViewItemModel>?>(nameof(Items));

    public static readonly StyledProperty<double> PageWidthProperty = AvaloniaProperty.Register<MultiViewCanvas, double>(nameof(PageWidth), 3508);

    public static readonly StyledProperty<double> PageHeightProperty = AvaloniaProperty.Register<MultiViewCanvas, double>(nameof(PageHeight), 2480);

    public static readonly StyledProperty<MultiViewItemModel?> SelectedItemProperty =
        AvaloniaProperty.Register<MultiViewCanvas, MultiViewItemModel?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> DarkPageProperty = AvaloniaProperty.Register<MultiViewCanvas, bool>(nameof(DarkPage), true);

    public IList<MultiViewItemModel>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public double PageWidth { get => GetValue(PageWidthProperty); set => SetValue(PageWidthProperty, value); }

    public double PageHeight { get => GetValue(PageHeightProperty); set => SetValue(PageHeightProperty, value); }

    public MultiViewItemModel? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    public bool DarkPage { get => GetValue(DarkPageProperty); set => SetValue(DarkPageProperty, value); }

    public event EventHandler<MultiViewItemModel>? ItemChanged;

    private enum Drag { None, Move, Scale }

    private Drag drag;
    private MultiViewItemModel? dragItem;
    private Point dragStart;
    private double startX, startY, startScale;

    static MultiViewCanvas()
    {
        AffectsRender<MultiViewCanvas>(ItemsProperty, PageWidthProperty, PageHeightProperty, SelectedItemProperty, DarkPageProperty);
        FocusableProperty.OverrideDefaultValue<MultiViewCanvas>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old)
                old.CollectionChanged -= OnItemsChanged;
            if (change.NewValue is INotifyCollectionChanged now)
                now.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    // The page in view coordinates: centred, as big as fits, a small margin.
    private (double Zoom, double Left, double Top) Frame()
    {
        double w = Math.Max(1, PageWidth), h = Math.Max(1, PageHeight);
        double zoom = Math.Min((Bounds.Width - 24) / w, (Bounds.Height - 24) / h);
        zoom = Math.Max(0.01, zoom);
        return (zoom, (Bounds.Width - w * zoom) / 2, (Bounds.Height - h * zoom) / 2);
    }

    private Rect ViewRect(MultiViewItemModel item)
    {
        (double zoom, double left, double top) = Frame();
        return new Rect(left + item.X * zoom, top + item.Y * zoom, Math.Max(1, item.Width * item.Scale * zoom), Math.Max(1, item.Height * item.Scale * zoom));
    }

    private static Rect Handle(Rect r) => new(r.Right - 9, r.Bottom - 9, 18, 18);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        (double zoom, double left, double top) = Frame();
        Rect page = new(left, top, PageWidth * zoom, PageHeight * zoom);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), page.Translate(new Vector(4, 4)));
        context.FillRectangle(new SolidColorBrush(DarkPage ? Color.Parse("#2B3038") : Colors.White), page);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#8A919B")), 1), page);
        IBrush accent = new SolidColorBrush(Color.Parse("#3B82F6"));
        foreach (MultiViewItemModel item in Items ?? [])
        {
            Rect r = ViewRect(item);
            using (context.PushClip(page))
            {
                if (item.Image != null)
                    context.DrawImage(item.Image, new Rect(item.Image.Size), r);
                else
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), r);
                    FormattedText wait = new(item.Title, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 14,
                        new SolidColorBrush(Color.Parse("#8A919B")));
                    context.DrawText(wait, new Point(r.X + 8, r.Y + 8));
                }
            }
            if (ReferenceEquals(item, SelectedItem))
            {
                context.DrawRectangle(new Pen(accent, 2.5, new DashStyle([4, 3], 0)), r.Inflate(2));
                context.DrawRectangle(accent, new Pen(Brushes.White, 1.5), Handle(r), 3, 3);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point p = e.GetPosition(this);
        Focus();
        if (SelectedItem != null && Handle(ViewRect(SelectedItem)).Contains(p))
        {
            Begin(Drag.Scale, SelectedItem, p, e);
            return;
        }
        // The topmost view under the pointer (the last drawn).
        MultiViewItemModel? hit = (Items ?? []).LastOrDefault(i => ViewRect(i).Contains(p));
        SelectedItem = hit;
        if (hit != null)
            Begin(Drag.Move, hit, p, e);
        InvalidateVisual();
    }

    private void Begin(Drag kind, MultiViewItemModel item, Point p, PointerPressedEventArgs e)
    {
        drag = kind;
        dragItem = item;
        dragStart = p;
        startX = item.X;
        startY = item.Y;
        startScale = item.Scale;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag == Drag.None || dragItem == null)
            return;
        (double zoom, _, _) = Frame();
        Point p = e.GetPosition(this);
        double dx = (p.X - dragStart.X) / zoom, dy = (p.Y - dragStart.Y) / zoom;
        if (drag == Drag.Move)
        {
            dragItem.X = Math.Clamp(startX + dx, -dragItem.Width * dragItem.Scale * 0.8, PageWidth - dragItem.Width * dragItem.Scale * 0.2);
            dragItem.Y = Math.Clamp(startY + dy, -dragItem.Height * dragItem.Scale * 0.8, PageHeight - dragItem.Height * dragItem.Scale * 0.2);
        }
        else
        {
            // The corner follows the pointer; the view keeps its aspect.
            double width = Math.Max(1, dragItem.Width * startScale);
            dragItem.Scale = Math.Clamp(startScale * (width + dx) / width, 0.02, 10);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (drag == Drag.None || dragItem == null)
            return;
        MultiViewItemModel item = dragItem;
        bool changed = Math.Abs(item.X - startX) > 0.5 || Math.Abs(item.Y - startY) > 0.5 || Math.Abs(item.Scale - startScale) > 1e-4;
        drag = Drag.None;
        dragItem = null;
        e.Pointer.Capture(null);
        if (changed)
            ItemChanged?.Invoke(this, item);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        MultiViewItemModel? item = SelectedItem;
        if (item == null || !ViewRect(item).Contains(e.GetPosition(this)))
            return;
        double factor = e.Delta.Y > 0 ? 1.08 : 1 / 1.08;
        // Scaled around the view's centre.
        double cx = item.X + item.Width * item.Scale / 2, cy = item.Y + item.Height * item.Scale / 2;
        item.Scale = Math.Clamp(item.Scale * factor, 0.02, 10);
        item.X = cx - item.Width * item.Scale / 2;
        item.Y = cy - item.Height * item.Scale / 2;
        InvalidateVisual();
        ItemChanged?.Invoke(this, item);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        MultiViewItemModel? item = SelectedItem;
        if (item == null)
            return;
        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 50 : 10;
        switch (e.Key)
        {
            case Key.Left: item.X -= step; break;
            case Key.Right: item.X += step; break;
            case Key.Up: item.Y -= step; break;
            case Key.Down: item.Y += step; break;
            default: return;
        }
        InvalidateVisual();
        ItemChanged?.Invoke(this, item);
        e.Handled = true;
    }
}
