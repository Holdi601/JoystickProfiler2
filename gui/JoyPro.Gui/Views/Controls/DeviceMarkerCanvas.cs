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
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The device picture with its input markers: the canvas at the centre of the Layout page (View / Edit markers / Bind)
// and of "Bind on the picture" in Bind Studio. Contract (docs/ui/layout-redesign.md):
//   in:  Source (+ SourceWidth/SourceHeight = the picture's pixel size, the marker coordinate space), Markers
//        (IDeviceMarker), SelectedMarker (two-way), Mode, Zoom (two-way), ShowCommands, SnapEnabled, ShowGrid,
//        Overlay (extra content in picture coordinates, e.g. legend tables and connector lines)
//   out: MarkerClicked / MarkerClickedCommand (IDeviceMarker), MarkerMoved / MarkerMovedCommand (DeviceMarkerMove, at
//        the end of a drag or an arrow-key nudge; the canvas never moves a marker itself), CanvasClicked /
//        CanvasClickedCommand (DeviceCanvasClick, a click on the picture outside the markers; the selection is
//        cleared first).
// Mouse: wheel zooms at the pointer, dragging the background (or the middle button, or Space + drag) pans, a click
// selects a marker; in Edit mode a marker drags, snapping to the edges of the other markers (and to the grid when it
// is shown); Alt drags without snapping. Keys (when the canvas has the focus): + / - zoom, 0 or F fit, Esc clears the
// selection, arrows nudge the selected marker in Edit mode (Shift: 10 px).
public sealed class DeviceMarkerCanvas : Control
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 4.0;

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> SourceWidthProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, double>(nameof(SourceWidth));

    public static readonly StyledProperty<double> SourceHeightProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, double>(nameof(SourceHeight));

    public static readonly StyledProperty<IEnumerable?> MarkersProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, IEnumerable?>(nameof(Markers));

    public static readonly StyledProperty<object?> SelectedMarkerProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, object?>(nameof(SelectedMarker), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<DeviceCanvasMode> ModeProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, DeviceCanvasMode>(nameof(Mode));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, double>(nameof(Zoom), 1.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowCommandsProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowCommands), true);

    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(SnapEnabled), true);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowGrid));

    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, double>(nameof(GridSize), 10);

    public static readonly StyledProperty<Control?> OverlayProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, Control?>(nameof(Overlay));

    public static readonly StyledProperty<bool> ShowOverlayProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowOverlay), true);

    public static readonly StyledProperty<string?> HintTextProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, string?>(nameof(HintText));

    public static readonly StyledProperty<ICommand?> MarkerClickedCommandProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, ICommand?>(nameof(MarkerClickedCommand));

    public static readonly StyledProperty<ICommand?> MarkerMovedCommandProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, ICommand?>(nameof(MarkerMovedCommand));

    public static readonly StyledProperty<ICommand?> CanvasClickedCommandProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, ICommand?>(nameof(CanvasClickedCommand));

    // JoyPro's vector drawing of the device (device reference), drawn when there is no Source picture.
    public static readonly StyledProperty<DeviceSchematic?> SchematicProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, DeviceSchematic?>(nameof(Schematic));

    // The numbered badges of part markers (IDevicePartMarker).
    public static readonly StyledProperty<bool> ShowBadgesProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowBadges), true);

    // The zoom bar and the hint (off for small or exported pictures).
    public static readonly StyledProperty<bool> ShowChromeProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowChrome), true);

    // The dotted canvas behind the picture and the picture's shadow (off: only the picture, e.g. for exports).
    public static readonly StyledProperty<bool> ShowBackdropProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(ShowBackdrop), true);

    // The paper under JoyPro's drawing; null = the theme's (JP.SurfaceAltBrush with a border).
    public static readonly StyledProperty<IBrush?> PictureBackgroundProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, IBrush?>(nameof(PictureBackground));

    // Label markers drawn as JoyPro 1 did on the user's own layout: the bound command as plain text at the marker's
    // point, in LabelFontFamily / LabelFontSize (points of the picture) / LabelForeground.
    public static readonly StyledProperty<bool> PlainLabelsProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, bool>(nameof(PlainLabels));

    public static readonly StyledProperty<FontFamily?> LabelFontFamilyProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, FontFamily?>(nameof(LabelFontFamily));

    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, double>(nameof(LabelFontSize), 14);

    public static readonly StyledProperty<IBrush?> LabelForegroundProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, IBrush?>(nameof(LabelForeground));

    // The marker just bound (Bind mode): ringed in green for a moment so the user sees where the bind went.
    public static readonly StyledProperty<object?> FlashMarkerProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, object?>(nameof(FlashMarker));

    public object? FlashMarker { get => GetValue(FlashMarkerProperty); set => SetValue(FlashMarkerProperty, value); }

    // What the shown device is doing right now: a held input's marker pulses (with its leader line), a moving axis
    // shows a small live bar (docs/ui/live-input.md).
    public static readonly StyledProperty<LiveInputState?> LiveInputProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, LiveInputState?>(nameof(LiveInput));

    public LiveInputState? LiveInput { get => GetValue(LiveInputProperty); set => SetValue(LiveInputProperty, value); }

    // The pulse's phase in render tests (null: the clock).
    internal double? LivePulsePhase { get; set; }

    private Avalonia.Threading.DispatcherTimer? livePulseTimer;

    private void OnLiveInputChanged(object? sender, EventArgs e)
    {
        markerLayer.InvalidateVisual();
        if (LiveInput?.AnyActive == true && livePulseTimer == null && VisualRoot != null)
        {
            livePulseTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            livePulseTimer.Tick += (_, _) =>
            {
                markerLayer.InvalidateVisual();
                if (LiveInput?.AnyActive != true)
                    StopLivePulse();
            };
            livePulseTimer.Start();
        }
    }

    private void StopLivePulse()
    {
        livePulseTimer?.Stop();
        livePulseTimer = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopLivePulse();
        base.OnDetachedFromVisualTree(e);
    }

    private static IEnumerable<string> InputsOf(IDeviceMarker marker) =>
        marker is ReferenceCanvasMarker part ? part.Part.Inputs.Select(i => i.Input).Where(i => i.Length > 0) : [marker.Input];

    private static readonly Color LiveColor = Color.Parse("#F59E0B");
    private static readonly IBrush LiveBarBack = new SolidColorBrush(Color.Parse("#E60F172A"));
    private static readonly IBrush LiveBarFill = new SolidColorBrush(LiveColor);

    // A held input: a pulsing amber ring around its dot or label, its leader line and label outlined in amber; an
    // axis in use: a small bar with where it is (0..1; a centre tick for a stick axis).
    private void DrawLive(DrawingContext context, Badge badge, LiveInputState live, double pulse)
    {
        List<string> inputs = InputsOf(badge.Marker).ToList();
        if (!live.IsActive(inputs))
            return;
        byte glow = (byte)(70 + 110 * pulse);
        IPen ring = new Pen(new SolidColorBrush(LiveColor), 2.5 + 1.5 * pulse);
        IBrush glowBrush = new SolidColorBrush(Color.FromArgb(glow, LiveColor.R, LiveColor.G, LiveColor.B));
        Point anchor;
        double below;
        if (badge.IsPart)
        {
            Point center = badge.PartCenter;
            double r = Math.Max(badge.PartRadius, 6) + 5 + 4 * pulse;
            context.DrawEllipse(glowBrush, ring, center, r, r);
            if (badge.CommandRect.Width > 0)
            {
                Point edge = ClosestEdgePoint(badge.CommandRect, center);
                context.DrawLine(new Pen(new SolidColorBrush(LiveColor), 2.4), center, edge);
                context.DrawRectangle(null, new Pen(new SolidColorBrush(LiveColor), 2 + pulse), badge.CommandRect.Inflate(1.5), 4, 4);
            }
            anchor = center;
            below = r + 4;
        }
        else
        {
            Rect rect = badge.Rect.Inflate(3 + 3 * pulse);
            context.DrawRectangle(glowBrush, ring, rect, 6, 6);
            anchor = new Point(badge.Rect.Center.X, badge.Rect.Center.Y);
            below = rect.Height / 2 + 4;
        }

        double y = anchor.Y + below;
        foreach (string input in inputs.Where(i => LiveInputState.AxisIndex(i) >= 0))
        {
            double value = live.AxisValue(input);
            if (double.IsNaN(value))
                continue;
            Rect bar = new(anchor.X - 24, y, 48, 7);
            context.DrawRectangle(LiveBarBack, new Pen(Brushes.White, 1), bar, 3, 3);
            context.DrawRectangle(LiveBarFill, null, new Rect(bar.X + 1, bar.Y + 1, Math.Max(0, (bar.Width - 2) * Math.Clamp(value, 0, 1)), bar.Height - 2), 2, 2);
            context.DrawLine(new Pen(Brushes.White, 1), new Point(bar.Center.X, bar.Y - 1), new Point(bar.Center.X, bar.Bottom + 1));
            y += 10;
        }
    }

    public bool PlainLabels { get => GetValue(PlainLabelsProperty); set => SetValue(PlainLabelsProperty, value); }

    public FontFamily? LabelFontFamily { get => GetValue(LabelFontFamilyProperty); set => SetValue(LabelFontFamilyProperty, value); }

    public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }

    public IBrush? LabelForeground { get => GetValue(LabelForegroundProperty); set => SetValue(LabelForegroundProperty, value); }

    // A picture file dropped on the canvas (the path; e.g. the user's own photo of the device).
    public static readonly StyledProperty<ICommand?> PictureFileDroppedCommandProperty =
        AvaloniaProperty.Register<DeviceMarkerCanvas, ICommand?>(nameof(PictureFileDroppedCommand));

    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#EB0F172A"));
    private static readonly IBrush BadgeFreeBrush = new SolidColorBrush(Color.Parse("#B30F172A"));
    private static readonly IBrush BadgeSelectedBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush GlowBrush = new SolidColorBrush(Color.Parse("#552563EB"));
    private static readonly IPen BadgePen = new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 1);
    private static readonly IPen BadgeFreePen = new Pen(new SolidColorBrush(Color.Parse("#99FFFFFF")), 1, new DashStyle([3, 2], 0));
    private static readonly IPen BadgeHoverPen = new Pen(new SolidColorBrush(Color.Parse("#93C5FD")), 1.5);
    private static readonly IPen BadgeSelectedPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 1, new DashStyle([5, 3], 0));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#F8FAFC"));
    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));
    private static readonly IBrush ModifierBrush = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.Parse("#33000000"));

    private readonly Decorator overlayHost = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    private readonly MarkerLayer markerLayer;
    private readonly Border zoomBar;
    private readonly TextBlock zoomLabel = new() { MinWidth = 46, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly Border hintBar;
    private readonly TextBlock hintLabel = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly List<Badge> badges = [];
    private readonly List<INotifyPropertyChanged> observedMarkers = [];
    private INotifyCollectionChanged? observedCollection;
    private bool badgesDirty = true;

    private Vector pan;
    private bool needsFit = true;
    private bool userAdjusted;
    private Size lastArrangeSize;
    private bool settingZoom;

    private IDeviceMarker? dragMarker;
    private Point dragStartPointer;
    private Point dragStartMarker;
    private Point dragPreview;
    private bool dragMoved;
    private double? guideX;
    private double? guideY;

    private bool pressOnBackground;
    private bool panning;
    private Point pressPoint;
    private Vector panAtPress;
    private bool spaceHeld;
    private bool selectingFromCanvas;
    private IDeviceMarker? hovered;

    public DeviceMarkerCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        markerLayer = new MarkerLayer(this) { IsHitTestVisible = false };
        zoomBar = BuildZoomBar();
        hintBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = hintLabel,
            IsHitTestVisible = false
        };
        hintBar.Bind(Border.BackgroundProperty, hintBar.GetResourceObservable("JP.SurfaceBrush"));
        hintBar.Bind(Border.BorderBrushProperty, hintBar.GetResourceObservable("JP.BorderBrush"));
        hintBar.BorderThickness = new Thickness(1);
        hintLabel.Bind(TextBlock.ForegroundProperty, hintLabel.GetResourceObservable("JP.TextMutedBrush"));
        hintBar.Opacity = 0.92;

        foreach (Control child in new Control[] { overlayHost, markerLayer, hintBar, zoomBar })
        {
            VisualChildren.Add(child);
            LogicalChildren.Add(child);
        }
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        UpdateHint();
        UpdateZoomLabel();
    }

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public double SourceWidth { get => GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }

    public double SourceHeight { get => GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }

    public IEnumerable? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    // An IDeviceMarker (object so view models can bind their own marker type two-way).
    public object? SelectedMarker { get => GetValue(SelectedMarkerProperty); set => SetValue(SelectedMarkerProperty, value); }

    public DeviceCanvasMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    public bool ShowCommands { get => GetValue(ShowCommandsProperty); set => SetValue(ShowCommandsProperty, value); }

    public bool SnapEnabled { get => GetValue(SnapEnabledProperty); set => SetValue(SnapEnabledProperty, value); }

    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public double GridSize { get => GetValue(GridSizeProperty); set => SetValue(GridSizeProperty, value); }

    public Control? Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }

    public bool ShowOverlay { get => GetValue(ShowOverlayProperty); set => SetValue(ShowOverlayProperty, value); }

    // The hint in the lower left corner; empty = the mode's own hint.
    public string? HintText { get => GetValue(HintTextProperty); set => SetValue(HintTextProperty, value); }

    public ICommand? MarkerClickedCommand { get => GetValue(MarkerClickedCommandProperty); set => SetValue(MarkerClickedCommandProperty, value); }

    public ICommand? MarkerMovedCommand { get => GetValue(MarkerMovedCommandProperty); set => SetValue(MarkerMovedCommandProperty, value); }

    public ICommand? CanvasClickedCommand { get => GetValue(CanvasClickedCommandProperty); set => SetValue(CanvasClickedCommandProperty, value); }

    public DeviceSchematic? Schematic { get => GetValue(SchematicProperty); set => SetValue(SchematicProperty, value); }

    public bool ShowBadges { get => GetValue(ShowBadgesProperty); set => SetValue(ShowBadgesProperty, value); }

    public bool ShowChrome { get => GetValue(ShowChromeProperty); set => SetValue(ShowChromeProperty, value); }

    public bool ShowBackdrop { get => GetValue(ShowBackdropProperty); set => SetValue(ShowBackdropProperty, value); }

    public IBrush? PictureBackground { get => GetValue(PictureBackgroundProperty); set => SetValue(PictureBackgroundProperty, value); }

    public ICommand? PictureFileDroppedCommand { get => GetValue(PictureFileDroppedCommandProperty); set => SetValue(PictureFileDroppedCommandProperty, value); }

    public event EventHandler<string>? PictureFileDropped;

    public event EventHandler<IDeviceMarker>? MarkerClicked;

    public event EventHandler<DeviceMarkerMove>? MarkerMoved;

    public event EventHandler<DeviceCanvasClick>? CanvasClicked;

    // The picture size in picture pixels (the marker coordinate space).
    public Size PictureSize
    {
        get
        {
            double width = SourceWidth > 0 ? SourceWidth : Source?.Size.Width ?? Schematic?.Width ?? 0;
            double height = SourceHeight > 0 ? SourceHeight : Source?.Size.Height ?? Schematic?.Height ?? 0;
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }
    }

    public Point ToCanvasPoint(Point viewPoint)
    {
        double zoom = EffectiveZoom;
        return new Point((viewPoint.X - pan.X) / zoom, (viewPoint.Y - pan.Y) / zoom);
    }

    public Point ToViewPoint(Point canvasPoint)
    {
        double zoom = EffectiveZoom;
        return new Point(canvasPoint.X * zoom + pan.X, canvasPoint.Y * zoom + pan.Y);
    }

    public void FitToView()
    {
        Size picture = PictureSize;
        Size view = Bounds.Size;
        if (picture.Width <= 0 || picture.Height <= 0 || view.Width <= 0 || view.Height <= 0)
        {
            needsFit = true;
            return;
        }
        FitToViewFor(view);
        needsFit = false;
        userAdjusted = false;
        ViewChanged();
    }

    public void ZoomIn()
    {
        userAdjusted = true;
        ZoomAround(EffectiveZoom * 1.25, Center);
    }

    public void ZoomOut()
    {
        userAdjusted = true;
        ZoomAround(EffectiveZoom / 1.25, Center);
    }

    private double EffectiveZoom => Math.Clamp(double.IsFinite(Zoom) && Zoom > 0 ? Zoom : 1, MinZoom, MaxZoom);

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    private IEnumerable<IDeviceMarker> MarkerItems => Markers?.OfType<IDeviceMarker>() ?? [];

    private IDeviceMarker? Selected => SelectedMarker as IDeviceMarker;

    private void ZoomAround(double zoom, Point viewPoint)
    {
        double old = EffectiveZoom;
        double next = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(next - old) < 0.0001)
            return;
        pan = new Vector(viewPoint.X - (viewPoint.X - pan.X) * next / old, viewPoint.Y - (viewPoint.Y - pan.Y) * next / old);
        SetZoomInternal(next);
        ViewChanged();
    }

    private void SetZoomInternal(double zoom)
    {
        settingZoom = true;
        try
        {
            SetCurrentValue(ZoomProperty, Math.Round(zoom, 4));
        }
        finally
        {
            settingZoom = false;
        }
        UpdateZoomLabel();
    }

    private void ViewChanged()
    {
        badgesDirty = true;
        UpdateOverlayTransform();
        InvalidateVisual();
        markerLayer.InvalidateVisual();
    }

    private void MarkersChanged()
    {
        badgesDirty = true;
        markerLayer.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == SourceWidthProperty || change.Property == SourceHeightProperty || change.Property == SchematicProperty)
        {
            needsFit = true;
            userAdjusted = false;
            InvalidateArrange();
            ViewChanged();
        }
        else if (change.Property == MarkersProperty)
        {
            ObserveMarkers(change.NewValue as IEnumerable);
            MarkersChanged();
        }
        else if (change.Property == SelectedMarkerProperty)
        {
            MarkersChanged();
            if (!selectingFromCanvas && change.NewValue is IDeviceMarker marker)
                BringIntoView(marker);
        }
        else if (change.Property == ShowChromeProperty)
        {
            zoomBar.IsVisible = ShowChrome;
            hintBar.IsVisible = ShowChrome;
            needsFit = !userAdjusted;
            InvalidateArrange();
        }
        else if (change.Property == ModeProperty || change.Property == ShowCommandsProperty || change.Property == ShowBadgesProperty ||
                 change.Property == PlainLabelsProperty || change.Property == LabelFontFamilyProperty || change.Property == LabelFontSizeProperty ||
                 change.Property == LabelForegroundProperty || change.Property == FlashMarkerProperty)
        {
            UpdateHint();
            MarkersChanged();
        }
        else if (change.Property == HintTextProperty)
        {
            UpdateHint();
        }
        else if (change.Property == LiveInputProperty)
        {
            if (change.OldValue is LiveInputState old)
                old.Changed -= OnLiveInputChanged;
            if (change.NewValue is LiveInputState state)
                state.Changed += OnLiveInputChanged;
            OnLiveInputChanged(this, EventArgs.Empty);
        }
        else if (change.Property == ShowGridProperty || change.Property == GridSizeProperty || change.Property == ShowBackdropProperty || change.Property == PictureBackgroundProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == ZoomProperty && !settingZoom)
        {
            double old = change.OldValue is double o && o > 0 ? Math.Clamp(o, MinZoom, MaxZoom) : 1;
            double next = EffectiveZoom;
            Point c = Center;
            pan = new Vector(c.X - (c.X - pan.X) * next / old, c.Y - (c.Y - pan.Y) * next / old);
            UpdateZoomLabel();
            ViewChanged();
        }
        else if (change.Property == OverlayProperty)
        {
            overlayHost.Child = change.NewValue as Control;
            InvalidateMeasure();
        }
        else if (change.Property == ShowOverlayProperty)
        {
            overlayHost.IsVisible = ShowOverlay;
        }
        else if (change.Property.Name == "ActualThemeVariant")
        {
            InvalidateVisual();
        }
    }

    private void ObserveMarkers(IEnumerable? markers)
    {
        if (observedCollection != null)
            observedCollection.CollectionChanged -= OnMarkersCollectionChanged;
        observedCollection = markers as INotifyCollectionChanged;
        if (observedCollection != null)
            observedCollection.CollectionChanged += OnMarkersCollectionChanged;
        ObserveMarkerItems();
    }

    private void ObserveMarkerItems()
    {
        foreach (INotifyPropertyChanged item in observedMarkers)
            item.PropertyChanged -= OnMarkerPropertyChanged;
        observedMarkers.Clear();
        foreach (INotifyPropertyChanged item in MarkerItems.OfType<INotifyPropertyChanged>())
        {
            item.PropertyChanged += OnMarkerPropertyChanged;
            observedMarkers.Add(item);
        }
    }

    private void OnMarkersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveMarkerItems();
        if (dragMarker != null && !MarkerItems.Contains(dragMarker))
            EndDrag(commit: false);
        MarkersChanged();
        InvalidateVisual();
    }

    private void OnMarkerPropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkersChanged();

    protected override Size MeasureOverride(Size availableSize)
    {
        Size picture = PictureSize;
        overlayHost.Measure(new Size(Math.Max(1, picture.Width), Math.Max(1, picture.Height)));
        markerLayer.Measure(availableSize);
        zoomBar.Measure(availableSize);
        hintBar.Measure(new Size(Math.Max(0, availableSize.Width - zoomBar.DesiredSize.Width - 36), availableSize.Height));
        double width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size picture = PictureSize;
        overlayHost.Arrange(new Rect(0, 0, Math.Max(1, picture.Width), Math.Max(1, picture.Height)));
        markerLayer.Arrange(new Rect(finalSize));
        Size bar = zoomBar.DesiredSize;
        zoomBar.Arrange(new Rect(finalSize.Width - bar.Width - 12, finalSize.Height - bar.Height - 12, bar.Width, bar.Height));
        Size hint = hintBar.DesiredSize;
        hintBar.Arrange(new Rect(12, finalSize.Height - hint.Height - 12 - Math.Max(0, (bar.Height - hint.Height) / 2), Math.Min(hint.Width, Math.Max(0, finalSize.Width - bar.Width - 36)), hint.Height));
        // Until the user zooms or pans, the picture stays fitted when the canvas changes its size.
        if (!userAdjusted && finalSize != lastArrangeSize)
            needsFit = true;
        lastArrangeSize = finalSize;
        if (needsFit && finalSize.Width > 0 && finalSize.Height > 0 && picture.Width > 0 && picture.Height > 0)
        {
            // After the layout pass: the bounds are known then.
            needsFit = false;
            FitToViewFor(finalSize);
        }
        UpdateOverlayTransform();
        return finalSize;
    }

    private void FitToViewFor(Size view)
    {
        Size picture = PictureSize;
        // Room for the zoom bar and the hint below the picture when they are shown.
        double side = ShowChrome ? 24 : 6, top = ShowChrome ? 16 : 6, bottom = ShowChrome ? 48 : 6;
        double zoom = Math.Clamp(Math.Min((view.Width - 2 * side) / picture.Width, (view.Height - top - bottom) / picture.Height), MinZoom, MaxZoom);
        pan = new Vector((view.Width - picture.Width * zoom) / 2, Math.Max(top, (view.Height - bottom + top - picture.Height * zoom) / 2));
        SetZoomInternal(zoom);
        badgesDirty = true;
    }

    private void UpdateOverlayTransform()
    {
        double zoom = EffectiveZoom;
        overlayHost.RenderTransform = new MatrixTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(pan.X, pan.Y));
    }

    private void UpdateZoomLabel()
    {
        zoomLabel.Text = Math.Round(EffectiveZoom * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private void UpdateHint()
    {
        string text = !string.IsNullOrWhiteSpace(HintText)
            ? HintText!
            : Mode switch
            {
                DeviceCanvasMode.EditMarkers => T("Drag a marker to move it  ·  Alt: no snapping  ·  Arrows nudge (Shift: 10 px)"),
                DeviceCanvasMode.Bind => T("Click an input, then pick what it does on the right"),
                _ => T("Wheel: zoom  ·  Drag the background: pan  ·  Click a marker: details")
            };
        hintLabel.Text = text;
    }

    private Border BuildZoomBar()
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(ZoomButton("Icon.Minus", T("Zoom out (-)"), (_, _) => ZoomOut()));
        row.Children.Add(zoomLabel);
        row.Children.Add(ZoomButton("Icon.Plus", T("Zoom in (+)"), (_, _) => ZoomIn()));
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 6), Background = Brushes.Gray, Opacity = 0.35 });
        row.Children.Add(ZoomButton("Icon.FitView", T("Fit the picture (0)"), (_, _) => FitToView()));
        Border border = new()
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = row,
            BoxShadow = BoxShadows.Parse("0 4 14 0 #33000000")
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("JP.SurfaceBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("JP.BorderStrongBrush"));
        return border;
    }

    private static Button ZoomButton(string icon, string tip, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        PathIcon glyph = new() { Width = 14, Height = 14 };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        Button button = new()
        {
            Content = glyph,
            Padding = new Thickness(7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Focusable = false
        };
        ToolTip.SetTip(button, tip);
        button.Click += click;
        return button;
    }

    // Picture, background and grid (the markers are drawn by the marker layer above the overlay).
    public override void Render(DrawingContext context)
    {
        Rect view = new(Bounds.Size);
        IBrush background = Resource("JP.SurfaceAltBrush") ?? new SolidColorBrush(Color.Parse("#0D1526"));
        if (ShowBackdrop)
            context.FillRectangle(background, view);
        if (ShowBackdrop && Resource("JP.BorderBrush") is ISolidColorBrush dot)
        {
            IBrush dots = new SolidColorBrush(dot.Color, 0.9);
            for (double y = 12; y < view.Height; y += 24)
            {
                for (double x = 12; x < view.Width; x += 24)
                    context.FillRectangle(dots, new Rect(x, y, 1.6, 1.6));
            }
        }

        Size picture = PictureSize;
        // Nothing to show (no picture, no markers): the page's empty state sits on the plain canvas.
        if (picture.Width <= 0 || picture.Height <= 0 || (Source == null && Schematic == null && !MarkerItems.Any()))
            return;
        double zoom = EffectiveZoom;
        Rect target = new(pan.X, pan.Y, picture.Width * zoom, picture.Height * zoom);
        if (ShowBackdrop)
            context.DrawRectangle(ShadowBrush, null, target.Translate(new Vector(0, 6)).Inflate(3), 10, 10);
        if (Source is IImage image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            using (context.PushClip(new RoundedRect(target, 6)))
                context.DrawImage(image, new Rect(image.Size), target);
        }
        else if (Schematic is DeviceSchematic schematic && schematic.Width > 0)
        {
            if (PictureBackground != null)
                context.DrawRectangle(PictureBackground, null, target, 10, 10);
            else
                context.DrawRectangle(Resource("JP.SurfaceAltBrush") ?? Brushes.White, new Pen(Resource("JP.BorderBrush") ?? Brushes.Gray, 1), target, 10, 10);
            using (context.PushClip(new RoundedRect(target, 10)))
                DrawSchematic(context, schematic, target);
        }
        else
        {
            context.DrawRectangle(Resource("JP.SurfaceRaisedBrush") ?? Brushes.DimGray, null, target, 6, 6);
        }

        if (ShowGrid && GridSize > 0)
        {
            double step = GridSize;
            while (step * zoom < 8)
                step *= 2;
            IPen gridPen = new Pen(new SolidColorBrush(Color.Parse("#2E93C5FD")), 1);
            using (context.PushClip(target))
            {
                for (double x = 0; x <= picture.Width; x += step)
                    context.DrawLine(gridPen, new Point(pan.X + x * zoom, target.Top), new Point(pan.X + x * zoom, target.Bottom));
                for (double y = 0; y <= picture.Height; y += step)
                    context.DrawLine(gridPen, new Point(target.Left, pan.Y + y * zoom), new Point(target.Right, pan.Y + y * zoom));
            }
        }
    }

    // A theme brush of Design.axaml; through the application when the canvas is not in a window (offscreen renders).
    private IBrush? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush
        : Application.Current != null && Application.Current.TryFindResource(key, ActualThemeVariant, out object? app) && app is IBrush appBrush ? appBrush
        : null;

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ||
                           (ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light && Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

    private readonly SchematicPainter schematicPainter = new();

    // JoyPro's drawing of the device, in the theme's colors.
    private void DrawSchematic(DrawingContext context, DeviceSchematic schematic, Rect target) => schematicPainter.Draw(context, schematic, target, IsDark);

    // Where the picture is drawn in the control (view coordinates).
    public Rect PictureRect()
    {
        Size picture = PictureSize;
        double zoom = EffectiveZoom;
        return new Rect(pan.X, pan.Y, picture.Width * zoom, picture.Height * zoom);
    }

    private static readonly IBrush PartLabelFill = new SolidColorBrush(Color.Parse("#F2FFFFFF"));
    private static readonly IBrush PartLabelText = new SolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IBrush PartBadgeFill = new SolidColorBrush(Color.Parse("#E6E2E8F0"));
    private static readonly IBrush PartBadgeText = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IPen PartLabelPen = new Pen(new SolidColorBrush(Color.Parse("#990F172A")), 1);
    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.Parse("#F59E0B"));
    // Bind mode: a bound input's dot is green, a free one hollow grey; the input just bound gets a green ring.
    private static readonly IBrush BoundDotFill = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush FreeDotFill = new SolidColorBrush(Color.Parse("#E6334155"));
    private static readonly IPen FreeDotPen = new Pen(new SolidColorBrush(Color.Parse("#CBD5E1")), 1.5, new DashStyle([2.5, 2], 0));
    private static readonly IPen BoundLabelPen = new Pen(new SolidColorBrush(Color.Parse("#16A34A")), 1.6);
    private static readonly IBrush BadgeBoundBrush = new SolidColorBrush(Color.Parse("#EB14532D"));
    private static readonly IBrush FlashGlow = new SolidColorBrush(Color.Parse("#5522C55E"));
    private static readonly IPen FlashPen = new Pen(new SolidColorBrush(Color.Parse("#4ADE80")), 3);

    private bool IsFlashed(IDeviceMarker marker) => FlashMarker != null && ReferenceEquals(FlashMarker, marker);

    // The green ring of the input just bound.
    private void DrawFlash(DrawingContext context, Point center, double radius)
    {
        context.DrawEllipse(null, new Pen(FlashGlow, 9), center, radius + 11, radius + 11);
        context.DrawEllipse(null, FlashPen, center, radius + 11, radius + 11);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.2), center, radius + 14, radius + 14);
    }

    // A part of a device reference, like JoyPro 1's pictures: a small dot on the part and next to it a compact label
    // with what the part does (the bound command), joined by a thin leader line; without a command a small tag with
    // the part's short name. Where no label fits without covering another one, only the numbered dot is drawn (the
    // legend and the tooltip have the text). On JoyPro's drawing the part itself is drawn, so there is no dot.
    private void DrawPart(DrawingContext context, Badge badge, bool selected)
    {
        IDevicePartMarker part = (IDevicePartMarker)badge.Marker;
        Point center = badge.PartCenter;
        double r = badge.PartRadius;
        IBrush accent = Resource("JP.AccentBrush") ?? BadgeSelectedBrush;
        bool onPhoto = Source != null;
        bool dragged = ReferenceEquals(part, dragMarker) && dragMoved;
        IPen accentPen = new Pen(accent, 1.4);

        if (badge.CommandRect.Width > 0)
        {
            Point anchor = ClosestEdgePoint(badge.CommandRect, center);
            double dx = anchor.X - center.X, dy = anchor.Y - center.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length > r + 2)
                context.DrawLine(accentPen, new Point(center.X + dx / length * r, center.Y + dy / length * r), anchor);
        }

        bool bindMode = Mode == DeviceCanvasMode.Bind;
        bool partBound = !string.IsNullOrWhiteSpace(part.Command);
        if (part.IsHighlighted)
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 245, 158, 11)), new Pen(HighlightFill, 2.5), center, r + 5, r + 5);
        else if (selected)
        {
            context.DrawEllipse(GlowBrush, null, center, r + 7, r + 7);
            context.DrawEllipse(null, new Pen(accent, 2.2), center, r + 4, r + 4);
        }
        else if (ReferenceEquals(part, hovered))
            context.DrawEllipse(null, BadgeHoverPen, center, r + 3, r + 3);

        if (badge.Collapsed)
        {
            // No room for a label: the number at the part.
            Rect rect = badge.BadgeRect;
            IBrush numberFill = part.IsHighlighted ? HighlightFill : selected ? accent : bindMode ? (partBound ? new SolidColorBrush(Color.Parse("#15803D")) : FreeDotFill) : new SolidColorBrush(Color.Parse("#1D4ED8"));
            context.DrawRectangle(numberFill, bindMode && !partBound && !selected ? FreeDotPen : new Pen(Brushes.White, 1.5), rect, rect.Height / 2, rect.Height / 2);
            badge.Label.Draw(context, new Point(rect.Center.X - badge.Label.WidthIncludingTrailingWhitespace / 2, rect.Center.Y - badge.Label.Height / 2));
            return;
        }
        if (bindMode && !part.IsHighlighted)
        {
            // Bind mode: green = bound, hollow grey = free (also on JoyPro's drawing, where the part is drawn).
            double dot = onPhoto || dragged ? r : Math.Max(4, Math.Min(r * 0.45, 7));
            if (selected)
                context.DrawEllipse(accent, new Pen(Brushes.White, 1.5), center, dot, dot);
            else if (partBound)
                context.DrawEllipse(BoundDotFill, new Pen(Brushes.White, 1.5), center, dot, dot);
            else
                context.DrawEllipse(FreeDotFill, FreeDotPen, center, dot, dot);
        }
        else if (onPhoto || dragged || badge.CommandRect.Width > 0)
            context.DrawEllipse(part.IsHighlighted ? HighlightFill : accent, new Pen(Brushes.White, 1.5), center, onPhoto || dragged ? r : 3, onPhoto || dragged ? r : 3);

        if (badge.CommandRect.Width > 0 && badge.Command != null)
        {
            Rect rect = badge.CommandRect;
            bool bound = badge.IsBound;
            IBrush fill = part.IsHighlighted ? HighlightFill : bound ? PartLabelFill : PartBadgeFill;
            IPen pen = selected ? new Pen(accent, 2) : bindMode && bound ? BoundLabelPen : PartLabelPen;
            context.DrawRectangle(fill, pen, rect, 3, 3);
            badge.Command.Draw(context, new Point(rect.X + badge.PadX, rect.Y + (rect.Height - badge.Command.Height) / 2));
        }
    }

    private static Point ClosestEdgePoint(Rect rect, Point p) =>
        new(Math.Clamp(p.X, rect.Left, rect.Right), Math.Clamp(p.Y, rect.Top, rect.Bottom));

    // The dot, the text and the size of a part's label; where the label goes is decided for all parts together
    // (PlacePartLabels).
    private Badge BuildPartBadge(IDevicePartMarker part, double mx, double my, double zoom, Typeface bold, bool showCommands)
    {
        Point center = new(mx * zoom + pan.X, my * zoom + pan.Y);
        bool onPicture = Source != null;
        // A small dot on a photo; on the drawing the part is drawn, the ring follows its size when selected.
        double r = onPicture ? 5 : Math.Max(6, part.Radius * zoom);
        TextLayout number = new(part.Badge, bold, 10.5, Brushes.White);
        double numberHeight = 18, numberWidth = Math.Max(numberHeight, number.WidthIncludingTrailingWhitespace + 9);
        Rect badgeRect = new(center.X - numberWidth / 2, center.Y - numberHeight / 2, numberWidth, numberHeight);
        string command = (part.Command ?? string.Empty).Trim();
        bool bound = showCommands && command.Length > 0;
        TextLayout? text = null;
        if (bound)
            text = new TextLayout(command, bold, 12, PartLabelText, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: 230);
        else if (ShowBadges)
            text = new TextLayout(part.Badge, bold, 10.5, PartBadgeText);
        Size labelSize = text == null ? default : new Size(text.WidthIncludingTrailingWhitespace + 12, text.Height + 4);
        Rect hit = new(center.X - Math.Max(r, 7), center.Y - Math.Max(r, 7), 2 * Math.Max(r, 7), 2 * Math.Max(r, 7));
        return new Badge(part, hit, number, text, null, 1, 6, 0, 0)
        {
            IsPart = true,
            IsBound = bound,
            PartCenter = center,
            PartRadius = r,
            BadgeRect = badgeRect,
            LabelSize = labelSize
        };
    }

    // Puts every part's label where it covers no other label and no other part: right of the part, else left, above,
    // below, then a step further out (with a leader line). A label that fits nowhere is left out: the part shows its
    // number instead. The selected part, then bound parts, then the rest, top to bottom, get the first choice.
    private void PlacePartLabels(List<Badge> parts)
    {
        List<Rect> taken = [];
        foreach (Badge part in parts)
            taken.Add(new Rect(part.PartCenter.X - part.PartRadius - 1, part.PartCenter.Y - part.PartRadius - 1, 2 * part.PartRadius + 2, 2 * part.PartRadius + 2));
        Rect bounds = new(Bounds.Size);
        IDeviceMarker? selected = Selected;
        foreach (Badge part in parts.OrderByDescending(p => ReferenceEquals(p.Marker, selected) || p.Marker.IsSelected)
                     .ThenByDescending(p => p.IsBound).ThenBy(p => p.PartCenter.Y).ThenBy(p => p.PartCenter.X))
        {
            if (part.Command == null || part.LabelSize.Width <= 0)
                continue;
            Rect? spot = null;
            foreach (Rect candidate in LabelCandidates(part))
            {
                if (!bounds.Contains(candidate))
                    continue;
                Rect padded = candidate.Inflate(1.5);
                bool free = true;
                for (int i = 0; i < taken.Count && free; i++)
                {
                    if (taken[i].Intersects(padded) && !IsOwnDot(part, taken[i]))
                        free = false;
                }
                if (free)
                {
                    spot = candidate;
                    break;
                }
            }
            if (spot is Rect rect)
            {
                part.CommandRect = rect;
                taken.Add(rect);
                part.Hit = part.Hit.Union(rect);
            }
            else
            {
                part.Collapsed = true;
                part.Hit = part.Hit.Union(part.BadgeRect);
            }
        }
    }

    private static bool IsOwnDot(Badge part, Rect rect) =>
        Math.Abs(rect.Center.X - part.PartCenter.X) < 0.5 && Math.Abs(rect.Center.Y - part.PartCenter.Y) < 0.5;

    private static IEnumerable<Rect> LabelCandidates(Badge part)
    {
        double w = part.LabelSize.Width, h = part.LabelSize.Height, r = part.PartRadius, gap = 7;
        double cx = part.PartCenter.X, cy = part.PartCenter.Y;
        yield return new Rect(cx + r + gap, cy - h / 2, w, h);
        yield return new Rect(cx - r - gap - w, cy - h / 2, w, h);
        yield return new Rect(cx - w / 2, cy - r - gap - h, w, h);
        yield return new Rect(cx - w / 2, cy + r + gap, w, h);
        for (int step = 1; step <= 4; step++)
        {
            double dy = step * (h + 3);
            double far = r + gap + step * 10;
            yield return new Rect(cx + far, cy - h / 2 - dy, w, h);
            yield return new Rect(cx + far, cy - h / 2 + dy, w, h);
            yield return new Rect(cx - far - w, cy - h / 2 - dy, w, h);
            yield return new Rect(cx - far - w, cy - h / 2 + dy, w, h);
        }
        for (int step = 1; step <= 3; step++)
        {
            yield return new Rect(cx + r + gap + step * 40, cy - h / 2, w, h);
            yield return new Rect(cx - r - gap - step * 40 - w, cy - h / 2, w, h);
        }
    }

    // A label marker of the user's own JoyPro 1 layout: the bound command as plain text at its position, in the
    // layout's font, size and color (the boxes are part of his picture). Unbound inputs show nothing when looking;
    // in Edit and Bind mode a small tag shows where they are.
    private void DrawPlainLabel(DrawingContext context, Badge badge, bool selected)
    {
        IBrush accent = Resource("JP.AccentBrush") ?? BadgeSelectedBrush;
        if (badge.Command != null)
        {
            badge.Command.Draw(context, badge.Rect.TopLeft);
            if (selected || ReferenceEquals(badge.Marker, hovered))
                context.DrawRectangle(null, new Pen(accent, selected ? 2 : 1), badge.Rect.Inflate(3), 3, 3);
            return;
        }
        if (badge.Compact && !selected && !ReferenceEquals(badge.Marker, hovered))
            return;
        Rect rect = badge.Rect;
        context.DrawRectangle(selected ? accent : BadgeFreeBrush, selected ? BadgeSelectedPen : BadgeFreePen, rect, 4, 4);
        badge.Label.Draw(context, new Point(rect.X + 5, rect.Y + (rect.Height - badge.Label.Height) / 2));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DataFormats.Files / GetFiles: the Avalonia 11 drag data API
        e.DragEffects = e.Data.Contains(DataFormats.Files) && (PictureFileDropped != null || PictureFileDroppedCommand != null) ? DragDropEffects.Copy : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        string? file = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && IsPicture(p));
#pragma warning restore CS0618
        if (file == null)
            return;
        PictureFileDropped?.Invoke(this, file);
        if (PictureFileDroppedCommand?.CanExecute(file) == true)
            PictureFileDroppedCommand.Execute(file);
    }

    public static bool IsPicture(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
    }

    private void RenderMarkers(DrawingContext context)
    {
        IReadOnlyList<Badge> current = GetBadges();
        IDeviceMarker? selected = Selected;
        foreach (Badge badge in current)
        {
            bool isSelected = ReferenceEquals(badge.Marker, selected) || badge.Marker.IsSelected;
            if (!isSelected)
                DrawAny(context, badge, selected: false);
        }
        foreach (Badge badge in current)
        {
            bool isSelected = ReferenceEquals(badge.Marker, selected) || badge.Marker.IsSelected;
            if (isSelected)
                DrawAny(context, badge, selected: true);
        }

        // The input just bound: a green ring on top of everything.
        foreach (Badge badge in current)
        {
            if (IsFlashed(badge.Marker))
                DrawFlash(context, badge.IsPart ? badge.PartCenter : badge.Rect.Center, badge.IsPart ? Math.Max(badge.PartRadius, 6) : Math.Max(badge.Rect.Width, badge.Rect.Height) / 2);
        }

        // The inputs in use right now, pulsing on top.
        if (LiveInput is { } live && (live.Pressed.Count > 0 || live.ActiveAxes().Any()))
        {
            double pulse = LivePulsePhase ?? 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 900.0 * 2 * Math.PI);
            foreach (Badge badge in current)
                DrawLive(context, badge, live, pulse);
        }

        Size picture = PictureSize;
        double zoom = EffectiveZoom;
        if (guideX is double gx)
        {
            double x = pan.X + gx * zoom;
            context.DrawLine(GuidePen, new Point(x, pan.Y), new Point(x, pan.Y + picture.Height * zoom));
        }
        if (guideY is double gy)
        {
            double y = pan.Y + gy * zoom;
            context.DrawLine(GuidePen, new Point(pan.X, y), new Point(pan.X + picture.Width * zoom, y));
        }
    }

    private void DrawAny(DrawingContext context, Badge badge, bool selected)
    {
        if (badge.IsPart)
            DrawPart(context, badge, selected);
        else if (badge.Plain)
            DrawPlainLabel(context, badge, selected);
        else
            DrawBadge(context, badge, selected);
    }

    private void DrawBadge(DrawingContext context, Badge badge, bool selected)
    {
        Rect rect = badge.Rect;
        double radius = Math.Min(rect.Height / 2, 7 * badge.Scale);
        bool free = Mode == DeviceCanvasMode.Bind && string.IsNullOrWhiteSpace(badge.Marker.Command);
        bool boundInBind = Mode == DeviceCanvasMode.Bind && !free;
        if (badge.Compact)
        {
            context.DrawEllipse(free ? BadgeFreeBrush : boundInBind ? BadgeBoundBrush : BadgeBrush, free ? BadgeFreePen : BadgePen, rect.Center, rect.Width / 2, rect.Height / 2);
            context.DrawEllipse(KindBrush(badge.Marker.Kind), null, rect.Center, badge.DotSize / 2, badge.DotSize / 2);
            return;
        }
        using (context.PushOpacity(ReferenceEquals(badge.Marker, dragMarker) && dragMoved ? 0.9 : 1))
        {
            if (selected)
            {
                context.DrawRectangle(GlowBrush, null, rect.Inflate(3.5), radius + 3, radius + 3);
                context.DrawRectangle(BadgeSelectedBrush, BadgeSelectedPen, rect, radius, radius);
            }
            else
            {
                IPen pen = ReferenceEquals(badge.Marker, hovered) ? BadgeHoverPen : free ? BadgeFreePen : boundInBind ? BoundLabelPen : BadgePen;
                context.DrawRectangle(free ? BadgeFreeBrush : boundInBind ? BadgeBoundBrush : BadgeBrush, pen, rect, radius, radius);
            }

            double dot = badge.DotSize;
            Point dotCenter = new(rect.X + badge.PadX + dot / 2, rect.Y + rect.Height / 2);
            context.DrawEllipse(selected ? Brushes.White : boundInBind ? BoundDotFill : KindBrush(badge.Marker.Kind), null, dotCenter, dot / 2, dot / 2);

            double x = rect.X + badge.PadX + dot + badge.Gap;
            if (badge.Modifier != null)
            {
                badge.Modifier.Draw(context, new Point(x, rect.Y + (rect.Height - badge.Modifier.Height) / 2));
                x += badge.Modifier.WidthIncludingTrailingWhitespace + badge.Gap / 2;
            }
            badge.Label.Draw(context, new Point(x, rect.Y + (rect.Height - badge.Label.Height) / 2));
            x += badge.Label.WidthIncludingTrailingWhitespace;
            if (badge.Command != null)
            {
                x += badge.Gap;
                badge.Command.Draw(context, new Point(x, rect.Y + (rect.Height - badge.Command.Height) / 2));
            }
        }
    }

    private static IBrush KindBrush(string? kind) =>
        (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "axis" => new SolidColorBrush(Color.Parse("#C084FC")),
            "pov" => new SolidColorBrush(Color.Parse("#2DD4BF")),
            "slider" => new SolidColorBrush(Color.Parse("#FBBF24")),
            _ => new SolidColorBrush(Color.Parse("#60A5FA"))
        };

    private IReadOnlyList<Badge> GetBadges()
    {
        if (!badgesDirty)
            return badges;
        foreach (Badge old in badges)
            old.Dispose();
        badges.Clear();

        double zoom = EffectiveZoom;
        double scale = Math.Clamp(zoom * 2.2, 0.62, 1.15);
        double maxWidth = Math.Max(110, 380 * zoom);
        FontFamily family = GetValue(TextElement.FontFamilyProperty);
        Typeface bold = new(family, FontStyle.Normal, FontWeight.SemiBold);
        Typeface regular = new(family, FontStyle.Normal, FontWeight.Normal);
        bool showCommands = ShowCommands && Mode != DeviceCanvasMode.EditMarkers;
        double padX = 7 * scale;
        double padY = 3.5 * scale;
        double dotSize = 7 * scale;
        double gap = 6 * scale;
        // Far out the labels would cover each other: dots only, the selected and the hovered marker keep their label.
        bool compact = zoom < 0.16;
        IDeviceMarker? selected = Selected;
        List<Badge> parts = [];

        foreach (IDeviceMarker marker in MarkerItems)
        {
            double mx = marker.X;
            double my = marker.Y;
            if (ReferenceEquals(marker, dragMarker) && dragMoved)
            {
                mx = dragPreview.X;
                my = dragPreview.Y;
            }
            if (marker is IDevicePartMarker part)
            {
                Badge partBadge = BuildPartBadge(part, mx, my, zoom, bold, showCommands);
                parts.Add(partBadge);
                badges.Add(partBadge);
                continue;
            }
            if (PlainLabels)
            {
                badges.Add(BuildPlainLabel(marker, mx, my, zoom, bold));
                continue;
            }
            TextLayout label = new(DeviceMarkerText.ShortLabel(marker.Input), bold, 11.5 * scale, LabelBrush);
            TextLayout? modifier = string.IsNullOrWhiteSpace(marker.Modifier)
                ? null
                : new TextLayout(marker.Modifier.Trim() + " +", bold, 10.5 * scale, ModifierBrush);
            double used = padX * 2 + dotSize + gap + label.WidthIncludingTrailingWhitespace + (modifier == null ? 0 : modifier.WidthIncludingTrailingWhitespace + gap / 2);
            TextLayout? command = null;
            string text = (marker.Command ?? string.Empty).Trim();
            if (showCommands && text.Length > 0 && maxWidth - used - gap > 24)
                command = new TextLayout(text, regular, 11 * scale, CommandBrush, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: maxWidth - used - gap);
            double width = used + (command == null ? 0 : gap + command.WidthIncludingTrailingWhitespace);
            double height = Math.Max(label.Height, command?.Height ?? 0) + padY * 2;
            Point origin = new(mx * zoom + pan.X, my * zoom + pan.Y);
            bool small = compact && !ReferenceEquals(marker, selected) && !marker.IsSelected && !ReferenceEquals(marker, hovered) && !ReferenceEquals(marker, dragMarker);
            Rect rect = small ? new Rect(origin, new Size(dotSize + padX * 1.4, dotSize + padX * 1.4)) : new Rect(origin, new Size(width, height));
            badges.Add(new Badge(marker, rect, label, command, modifier, scale, padX, dotSize, gap) { Compact = small });
        }
        PlacePartLabels(parts);
        badgesDirty = false;
        return badges;
    }

    // JoyPro 1's text: GDI+ DrawString with the layout's font size on the layout's picture (72 dpi pictures: the size
    // is in picture pixels, which is what JoyPro 1's cheat sheets show).
    private Badge BuildPlainLabel(IDeviceMarker marker, double mx, double my, double zoom, Typeface bold)
    {
        Point origin = new(mx * zoom + pan.X, my * zoom + pan.Y);
        string text = (marker.Command ?? string.Empty).Trim();
        bool look = Mode == DeviceCanvasMode.View;
        TextLayout tag = new(DeviceMarkerText.ShortLabel(marker.Input) + (string.IsNullOrWhiteSpace(marker.Modifier) ? string.Empty : " · " + marker.Modifier.Trim()), bold, 10.5, LabelBrush);
        if (text.Length > 0)
        {
            double size = Math.Max(1, LabelFontSize * zoom);
            Typeface face = new(LabelFontFamily ?? FontFamily.Default);
            TextLayout command = new(text, face, size, LabelForeground ?? Brushes.Black);
            // GDI+ starts the text a little right of and below the point.
            Point at = new(origin.X + size * 0.12, origin.Y + size * 0.05);
            return new Badge(marker, new Rect(at, new Size(command.WidthIncludingTrailingWhitespace, command.Height)), tag, command, null, 1, 5, 0, 0) { Plain = true };
        }
        Rect rect = look ? new Rect(origin, new Size(14, 14)) : new Rect(origin, new Size(tag.WidthIncludingTrailingWhitespace + 10, tag.Height + 4));
        return new Badge(marker, rect, tag, null, null, 1, 5, 0, 0) { Plain = true, Compact = look };
    }

    private IDeviceMarker? HitTest(Point viewPoint)
    {
        IReadOnlyList<Badge> current = GetBadges();
        IDeviceMarker? selected = Selected;
        Badge? selectedBadge = current.FirstOrDefault(b => ReferenceEquals(b.Marker, selected));
        if (selectedBadge != null && selectedBadge.Rect.Inflate(2).Contains(viewPoint))
            return selectedBadge.Marker;
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (current[i].Rect.Inflate(2).Contains(viewPoint))
                return current[i].Marker;
        }
        return null;
    }

    private void BringIntoView(IDeviceMarker marker)
    {
        Size view = Bounds.Size;
        if (view.Width <= 0 || view.Height <= 0)
            return;
        Point p = ToViewPoint(new Point(marker.X, marker.Y));
        if (p.X >= 24 && p.Y >= 24 && p.X <= view.Width - 160 && p.Y <= view.Height - 72)
            return;
        pan += new Vector(view.Width / 2 - p.X - 60, view.Height / 2 - p.Y);
        ViewChanged();
    }

    private void Select(IDeviceMarker? marker)
    {
        selectingFromCanvas = true;
        try
        {
            SetCurrentValue(SelectedMarkerProperty, marker);
        }
        finally
        {
            selectingFromCanvas = false;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point p = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && spaceHeld))
        {
            StartPanAt(p, e);
            panning = true;
            return;
        }

        IDeviceMarker? hit = HitTest(p);
        if (props.IsRightButtonPressed)
        {
            if (hit == null)
            {
                Select(null);
                RaiseCanvasClicked(ToCanvasPoint(p), right: true);
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        if (hit != null)
        {
            Select(hit);
            MarkerClicked?.Invoke(this, hit);
            if (MarkerClickedCommand?.CanExecute(hit) == true)
                MarkerClickedCommand.Execute(hit);
            if (Mode == DeviceCanvasMode.EditMarkers)
            {
                dragMarker = hit;
                dragStartPointer = p;
                dragStartMarker = new Point(hit.X, hit.Y);
                dragPreview = dragStartMarker;
                dragMoved = false;
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }

        StartPanAt(p, e);
        pressOnBackground = true;
    }

    private void StartPanAt(Point p, PointerPressedEventArgs e)
    {
        pressPoint = p;
        panAtPress = pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);
        if (dragMarker != null)
        {
            if (!dragMoved && Distance(p, dragStartPointer) < 3)
                return;
            dragMoved = true;
            double zoom = EffectiveZoom;
            Point candidate = new(dragStartMarker.X + (p.X - dragStartPointer.X) / zoom, dragStartMarker.Y + (p.Y - dragStartPointer.Y) / zoom);
            dragPreview = Snap(candidate, dragMarker, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            MarkersChanged();
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }
        if (pressOnBackground || panning)
        {
            if (!panning && Distance(p, pressPoint) < 4)
                return;
            panning = true;
            userAdjusted = true;
            pan = panAtPress + (p - pressPoint);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            ViewChanged();
            e.Handled = true;
            return;
        }

        IDeviceMarker? hit = HitTest(p);
        if (!ReferenceEquals(hit, hovered))
        {
            hovered = hit;
            Cursor = hit == null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            string? tip = hit switch
            {
                IDevicePartMarker part => part.Details,
                not null => hit.Input + (string.IsNullOrWhiteSpace(hit.Modifier) ? string.Empty : T(" with {0}", hit.Modifier)) + (string.IsNullOrWhiteSpace(hit.Command) ? string.Empty : "\n" + hit.Command),
                _ => null
            };
            ToolTip.SetTip(this, tip);
            MarkersChanged();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point p = e.GetPosition(this);
        if (dragMarker != null)
        {
            EndDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (pressOnBackground && !panning && e.InitialPressMouseButton == MouseButton.Left)
        {
            Select(null);
            RaiseCanvasClicked(ToCanvasPoint(p), right: false);
        }
        if (pressOnBackground || panning)
        {
            pressOnBackground = false;
            panning = false;
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (dragMarker != null)
            EndDrag(commit: true);
        pressOnBackground = false;
        panning = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hovered != null)
        {
            hovered = null;
            MarkersChanged();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (PictureSize.Width <= 0)
            return;
        userAdjusted = true;
        double factor = Math.Pow(1.15, e.Delta.Y);
        ZoomAround(EffectiveZoom * factor, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Space:
                spaceHeld = true;
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                FitToView();
                e.Handled = true;
                break;
            case Key.Escape:
                Select(null);
                e.Handled = true;
                break;
            case Key.Left:
                e.Handled = Nudge(-step, 0);
                break;
            case Key.Right:
                e.Handled = Nudge(step, 0);
                break;
            case Key.Up:
                e.Handled = Nudge(0, -step);
                break;
            case Key.Down:
                e.Handled = Nudge(0, step);
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
            spaceHeld = false;
    }

    private bool Nudge(double dx, double dy)
    {
        if (Mode != DeviceCanvasMode.EditMarkers || Selected is not IDeviceMarker marker)
            return false;
        RaiseMoved(new DeviceMarkerMove(marker, marker.X, marker.Y, marker.X + dx, marker.Y + dy));
        return true;
    }

    private void EndDrag(bool commit)
    {
        IDeviceMarker? marker = dragMarker;
        bool moved = dragMoved;
        Point preview = dragPreview;
        dragMarker = null;
        dragMoved = false;
        guideX = null;
        guideY = null;
        Cursor = Cursor.Default;
        MarkersChanged();
        if (commit && marker != null && moved && (Math.Abs(preview.X - dragStartMarker.X) > 0.01 || Math.Abs(preview.Y - dragStartMarker.Y) > 0.01))
            RaiseMoved(new DeviceMarkerMove(marker, dragStartMarker.X, dragStartMarker.Y, Math.Round(preview.X, 2), Math.Round(preview.Y, 2)));
    }

    private void RaiseMoved(DeviceMarkerMove move)
    {
        MarkerMoved?.Invoke(this, move);
        if (MarkerMovedCommand?.CanExecute(move) == true)
            MarkerMovedCommand.Execute(move);
    }

    private void RaiseCanvasClicked(Point canvasPoint, bool right)
    {
        DeviceCanvasClick click = new(Math.Round(canvasPoint.X, 2), Math.Round(canvasPoint.Y, 2), right);
        CanvasClicked?.Invoke(this, click);
        if (CanvasClickedCommand?.CanExecute(click) == true)
            CanvasClickedCommand.Execute(click);
    }

    // Snaps the dragged marker's corner to the left edge / top edge of another marker (alignment guide), else to the
    // grid when it is shown.
    internal Point Snap(Point candidate, IDeviceMarker dragged, bool disable)
    {
        guideX = null;
        guideY = null;
        if (disable || !SnapEnabled)
            return candidate;
        double threshold = 8 / EffectiveZoom;
        double bestX = threshold;
        double bestY = threshold;
        double x = candidate.X;
        double y = candidate.Y;
        foreach (IDeviceMarker other in MarkerItems)
        {
            if (ReferenceEquals(other, dragged))
                continue;
            double dx = Math.Abs(other.X - candidate.X);
            if (dx < bestX)
            {
                bestX = dx;
                x = other.X;
                guideX = other.X;
            }
            double dy = Math.Abs(other.Y - candidate.Y);
            if (dy < bestY)
            {
                bestY = dy;
                y = other.Y;
                guideY = other.Y;
            }
        }
        if (ShowGrid && GridSize > 0)
        {
            if (guideX == null)
                x = Math.Round(candidate.X / GridSize) * GridSize;
            if (guideY == null)
                y = Math.Round(candidate.Y / GridSize) * GridSize;
        }
        return new Point(x, y);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private sealed class Badge(IDeviceMarker marker, Rect rect, TextLayout label, TextLayout? command, TextLayout? modifier, double scale, double padX, double dotSize, double gap) : IDisposable
    {
        public IDeviceMarker Marker { get; } = marker;
        // The hit area (a part: its dot, plus its label or number once placed).
        public Rect Rect { get; set; } = rect;
        public Rect Hit { get => Rect; set => Rect = value; }
        public bool IsBound { get; init; }
        public bool Collapsed { get; set; }
        public Size LabelSize { get; init; }
        public bool Plain { get; init; }
        public TextLayout Label { get; } = label;
        public TextLayout? Command { get; } = command;
        public TextLayout? Modifier { get; } = modifier;
        public double Scale { get; } = scale;
        public double PadX { get; } = padX;
        public double DotSize { get; } = dotSize;
        public double Gap { get; } = gap;
        public bool Compact { get; init; }
        public bool IsPart { get; init; }
        public Point PartCenter { get; init; }
        public double PartRadius { get; init; }
        public Rect BadgeRect { get; init; }
        public Rect CommandRect { get; set; }

        public void Dispose()
        {
            Label.Dispose();
            Command?.Dispose();
            Modifier?.Dispose();
        }
    }

    // The markers and the snap guides, above the overlay (tables, lines); not hit-testable (the canvas hit-tests).
    private sealed class MarkerLayer(DeviceMarkerCanvas owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderMarkers(context);
    }
}
