using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Views.Controls;

// The rotatable 3D view of a device (docs/device-reference.md, "3D view"): the pre-rendered frame of the current yaw
// and pitch, every part of the device at its spot on that frame (hollow when it faces away), the selected part ringed,
// held inputs lit. Drag rotates (sideways: around the vertical; up and down: the pitch), the wheel zooms, a double
// click resets the view; a click picks a part (SelectedPart, two-way), a relation dragged onto a part binds it there
// (RelationDroppedCommand, like the flat picture).
public sealed class TurntableView : Control
{
    public static readonly StyledProperty<DeviceTurntable?> TurntableProperty =
        AvaloniaProperty.Register<TurntableView, DeviceTurntable?>(nameof(Turntable));

    public static readonly StyledProperty<IEnumerable?> PartsProperty =
        AvaloniaProperty.Register<TurntableView, IEnumerable?>(nameof(Parts));

    public static readonly StyledProperty<ReferenceMarker?> SelectedPartProperty =
        AvaloniaProperty.Register<TurntableView, ReferenceMarker?>(nameof(SelectedPart), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<LiveInputState?> LiveInputProperty =
        AvaloniaProperty.Register<TurntableView, LiveInputState?>(nameof(LiveInput));

    public static readonly StyledProperty<ICommand?> RelationDroppedCommandProperty =
        AvaloniaProperty.Register<TurntableView, ICommand?>(nameof(RelationDroppedCommand));

    public DeviceTurntable? Turntable { get => GetValue(TurntableProperty); set => SetValue(TurntableProperty, value); }
    public IEnumerable? Parts { get => GetValue(PartsProperty); set => SetValue(PartsProperty, value); }
    public ReferenceMarker? SelectedPart { get => GetValue(SelectedPartProperty); set => SetValue(SelectedPartProperty, value); }
    public LiveInputState? LiveInput { get => GetValue(LiveInputProperty); set => SetValue(LiveInputProperty, value); }
    public ICommand? RelationDroppedCommand { get => GetValue(RelationDroppedCommandProperty); set => SetValue(RelationDroppedCommandProperty, value); }

    // The view: yaw step, pitch index, zoom (1 = the frame fits).
    public int YawIndex { get; private set; }
    public int PitchIndex { get; private set; } = 1;
    public double Zoom { get; private set; } = 1.25;

    private readonly Dictionary<string, Bitmap?> bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private Point? dragStart;
    private int dragYaw, dragPitch;
    private bool dragged;
    private ReferenceMarker? dropHover;

    static TurntableView()
    {
        AffectsRender<TurntableView>(TurntableProperty, PartsProperty, SelectedPartProperty);
    }

    public TurntableView()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TurntableProperty)
        {
            bitmaps.Clear();
            ResetView();
        }
        else if (change.Property == PartsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldC)
                oldC.CollectionChanged -= OnPartsChanged;
            if (change.NewValue is INotifyCollectionChanged newC)
                newC.CollectionChanged += OnPartsChanged;
            Watch(change.NewValue as IEnumerable);
        }
        else if (change.Property == SelectedPartProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == LiveInputProperty)
        {
            if (change.OldValue is LiveInputState oldL)
                oldL.Changed -= OnLive;
            if (change.NewValue is LiveInputState newL)
                newL.Changed += OnLive;
        }
    }

    private readonly HashSet<INotifyPropertyChanged> watched = [];

    // A part's command, selection or live state changed: draw again.
    private void Watch(IEnumerable? items)
    {
        foreach (INotifyPropertyChanged old in watched)
            old.PropertyChanged -= OnPartChanged;
        watched.Clear();
        foreach (INotifyPropertyChanged item in items?.OfType<INotifyPropertyChanged>() ?? [])
        {
            item.PropertyChanged += OnPartChanged;
            watched.Add(item);
        }
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnPartsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Watch(Parts);
        InvalidateVisual();
    }

    private void OnLive(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    public void ResetView()
    {
        YawIndex = 0;
        PitchIndex = Turntable == null ? 0 : Math.Min(1, Turntable.Pitches.Count - 1);
        Zoom = 1.25;
        InvalidateVisual();
    }

    // Turns the view (tests and keys): yaw steps around, pitch steps up or down.
    public void Rotate(int yawSteps, int pitchSteps)
    {
        if (Turntable == null)
            return;
        YawIndex = ((YawIndex + yawSteps) % Turntable.YawSteps + Turntable.YawSteps) % Turntable.YawSteps;
        PitchIndex = Math.Clamp(PitchIndex + pitchSteps, 0, Turntable.Pitches.Count - 1);
        InvalidateVisual();
    }

    private DeviceTurntable.Frame? CurrentFrame => Turntable?.FrameAt(PitchIndex, YawIndex);

    private Bitmap? BitmapOf(DeviceTurntable.Frame frame)
    {
        string path = Turntable!.PathOf(frame);
        if (!bitmaps.TryGetValue(path, out Bitmap? bmp))
        {
            try
            {
                bmp = System.IO.File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception)
            {
                bmp = null;
            }
            bitmaps[path] = bmp;
        }
        return bmp;
    }

    // Where the frame is drawn in the control.
    private Rect FrameRect()
    {
        double side = Math.Min(Bounds.Width, Bounds.Height) * Zoom;
        return new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
    }

    // The view point of a part on the current frame, and whether it faces the viewer; null when it is not in 3D.
    public (Point At, bool Seen)? PointOf(string partId)
    {
        DeviceTurntable.Frame? frame = CurrentFrame;
        if (frame == null || !frame.Points.TryGetValue(partId, out (double X, double Y, bool Seen) p))
            return null;
        Rect r = FrameRect();
        return (new Point(r.X + p.X * r.Width, r.Y + p.Y * r.Height), p.Seen);
    }

    private IEnumerable<ReferenceMarker> AllParts => Parts?.OfType<ReferenceMarker>() ?? [];

    // The part under a view point (facing parts first), within reach.
    public ReferenceMarker? PartAt(Point view, double reach = 16)
    {
        ReferenceMarker? best = null;
        double bestD = reach;
        foreach (ReferenceMarker m in AllParts)
        {
            if (PointOf(m.Id) is not { } p)
                continue;
            double d = Math.Sqrt(Math.Pow(p.At.X - view.X, 2) + Math.Pow(p.At.Y - view.Y, 2)) + (p.Seen ? 0 : reach * 0.6);
            if (d < bestD)
            {
                bestD = d;
                best = m;
            }
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        DeviceTurntable.Frame? frame = CurrentFrame;
        if (frame == null)
            return;
        Rect r = FrameRect();
        if (BitmapOf(frame) is { } bmp)
            context.DrawImage(bmp, new Rect(bmp.Size), r);
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF));
        IBrush live = new SolidColorBrush(Color.FromRgb(0xF2, 0xA2, 0x2E));
        Pen ring = new(accent, 3), hollow = new(new SolidColorBrush(Color.FromArgb(160, 180, 190, 200)), 1.5) { DashStyle = DashStyle.Dash }, white = new(Brushes.White, 1.5);
        HashSet<string> pressed = new(LiveInput?.Pressed ?? (IReadOnlyCollection<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Typeface face = new("Bahnschrift, Segoe UI");
        foreach (ReferenceMarker m in AllParts.OrderBy(m => m.IsSelected))
        {
            if (PointOf(m.Id) is not { } p)
                continue;
            bool lit = m.IsHighlighted || m.Inputs.Any(i => pressed.Contains(i.Input));
            double rad = 7;
            if (p.Seen)
                context.DrawEllipse(lit ? live : accent, white, p.At, rad, rad);
            else
                context.DrawEllipse(null, hollow, p.At, rad - 1, rad - 1);
            if (m.IsSelected || ReferenceEquals(m, SelectedPart) || ReferenceEquals(m, dropHover))
                context.DrawEllipse(null, ring, p.At, rad + 6, rad + 6);
            if (p.Seen && (m.IsSelected || m.Command.Length > 0 || ReferenceEquals(m, SelectedPart)))
            {
                string text = m.Command.Length > 0 ? m.Command : m.Name;
                FormattedText label = new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 12, Brushes.White);
                Rect box = new(p.At.X + 12, p.At.Y - label.Height / 2 - 2, label.Width + 8, label.Height + 4);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 24, 30, 40)), null, box, 4, 4);
                context.DrawText(label, new Point(box.X + 4, box.Y + 2));
            }
        }
        if (Turntable?.Source.Length > 0)
        {
            FormattedText credit = new(Turntable.Source, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 10.5, new SolidColorBrush(Color.FromArgb(170, 200, 205, 215)));
            context.DrawText(credit, new Point(8, Bounds.Height - credit.Height - 6));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        dragStart = e.GetPosition(this);
        dragYaw = YawIndex;
        dragPitch = PitchIndex;
        dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragStart is not { } start || Turntable == null)
            return;
        Point now = e.GetPosition(this);
        double dx = now.X - start.X, dy = now.Y - start.Y;
        if (!dragged && Math.Abs(dx) + Math.Abs(dy) < 5)
            return;
        dragged = true;
        // A full turn over the control's width; the pitches over its height.
        int yaw = dragYaw + (int)Math.Round(-dx / Math.Max(1, Bounds.Width) * Turntable.YawSteps);
        int pitch = dragPitch + (int)Math.Round(dy / Math.Max(1, Bounds.Height / Math.Max(1, Turntable.Pitches.Count)));
        YawIndex = ((yaw % Turntable.YawSteps) + Turntable.YawSteps) % Turntable.YawSteps;
        PitchIndex = Math.Clamp(pitch, 0, Turntable.Pitches.Count - 1);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragStart != null && !dragged && PartAt(e.GetPosition(this)) is { } part)
            SetCurrentValue(SelectedPartProperty, part);
        dragStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.6, 3.5);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Rotate(-1, 0); e.Handled = true; break;
            case Key.Right: Rotate(1, 0); e.Handled = true; break;
            case Key.Up: Rotate(0, -1); e.Handled = true; break;
            case Key.Down: Rotate(0, 1); e.Handled = true; break;
            case Key.Home: ResetView(); e.Handled = true; break;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (RelationDroppedCommand != null && CalloutSheetCanvas.RelationOf(e) != null)
        {
            dropHover = PartAt(e.GetPosition(this), 28);
            e.DragEffects = dropHover != null ? DragDropEffects.Link : DragDropEffects.None;
            InvalidateVisual();
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        string? relation = CalloutSheetCanvas.RelationOf(e);
        dropHover = null;
        if (relation != null)
            DropRelationAt(e.GetPosition(this), relation);
        InvalidateVisual();
    }

    // Drops a relation onto the part at a view point: picks it, then runs RelationDroppedCommand. False: no part there.
    public bool DropRelationAt(Point view, string relation)
    {
        ReferenceMarker? part = PartAt(view, 28);
        if (part == null)
            return false;
        SetCurrentValue(SelectedPartProperty, part);
        if (RelationDroppedCommand?.CanExecute(relation) == true)
            RelationDroppedCommand.Execute(relation);
        return true;
    }
}
