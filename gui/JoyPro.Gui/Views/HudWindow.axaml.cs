using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;

namespace JoyPro.Gui.Views;

// The HUD on screen: always on top, never takes the focus (the game keeps keyboard and mouse), not in the task bar or
// Alt+Tab, optionally click-through, and only as large as the visible widgets (the HudPanel's bounding box), placed
// where the designer put them on the primary screen. Stream mode: a normal window with a plain background that OBS can
// capture and key out.
[SupportedOSPlatform("windows")]
public partial class HudWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x8000000;
    private const uint LwaAlpha = 0x2;

    private readonly bool stream;
    private readonly ScaleTransform screenScale = new(1, 1);
    private HudPanel? observedPanel;

    public HudWindow() : this(false)
    {
    }

    public HudWindow(bool stream)
    {
        this.stream = stream;
        InitializeComponent();
        this.FindControl<LayoutTransformControl>("ScreenHost")!.LayoutTransform = screenScale;
        if (stream)
        {
            Title = T("JoyPro HUD (stream)");
            SizeToContent = SizeToContent.Manual;
            CanResize = true;
            Width = 960;
            Height = 540;
            ShowInTaskbar = true;
            this.FindControl<Control>("ScreenHost")!.IsVisible = false;
            this.FindControl<Control>("StreamHost")!.IsVisible = true;
        }
        else
        {
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        Opened += (_, _) =>
        {
            if (stream)
            {
                string color = (DataContext as MainWindowViewModel)?.HudStreamBackground ?? "#00FF00";
                Background = new SolidColorBrush(Color.TryParse(color, out Color parsed) ? parsed : Colors.Lime);
                return;
            }
            ApplyWindowStyles();
            PlaceOnScreen();
        };
        LayoutUpdated += (_, _) => WatchPanel();
    }

    // The on-screen window covers the widgets' bounding box: scaled from the reference screen to the primary screen.
    public void PlaceOnScreen()
    {
        if (stream)
            return;
        Screen? screen = Screens.Primary;
        HudPanel? panel = this.FindControl<HudSurface>("ScreenSurface")?.Panel;
        if (screen == null || panel == null)
            return;
        PixelRect area = screen.Bounds;
        double dpi = screen.Scaling;
        double scale = Math.Min(area.Width / dpi / HudLayout.ReferenceWidth, area.Height / dpi / HudLayout.ReferenceHeight);
        if (Math.Abs(screenScale.ScaleX - scale) > 0.001)
        {
            screenScale.ScaleX = scale;
            screenScale.ScaleY = scale;
        }
        Position = new PixelPoint(area.X + (int)Math.Round(panel.Origin.X * scale * dpi), area.Y + (int)Math.Round(panel.Origin.Y * scale * dpi));
    }

    private void WatchPanel()
    {
        HudPanel? panel = this.FindControl<HudSurface>("ScreenSurface")?.Panel;
        if (panel == null || panel == observedPanel)
            return;
        if (observedPanel != null)
            observedPanel.OriginChanged -= OnOriginChanged;
        observedPanel = panel;
        panel.OriginChanged += OnOriginChanged;
        PlaceOnScreen();
    }

    private void OnOriginChanged(object? sender, EventArgs e) => PlaceOnScreen();

    private void ApplyWindowStyles()
    {
        IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;
        bool clickThrough = (DataContext as MainWindowViewModel)?.OverlayClickThrough ?? true;
        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow | WsExNoActivate;
        if (clickThrough)
            style |= WsExTransparent | WsExLayered;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        if (clickThrough)
            SetLayeredWindowAttributes(handle, 0, 255, LwaAlpha);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}

// Opens the HUD window (on screen or as stream window) on Show and closes it on Hide.
[SupportedOSPlatform("windows")]
public sealed class OverlayPresenter : IOverlayPresenter
{
    private HudWindow? window;

    public void Show(object dataContext)
    {
        bool stream = (dataContext as MainWindowViewModel)?.HudStreamWindow == true;
        if (window != null)
        {
            window.PlaceOnScreen();
            return;
        }
        window = new HudWindow(stream) { DataContext = dataContext };
        window.Closed += (_, _) => window = null;
        window.Show();
    }

    public void Hide()
    {
        window?.Close();
        window = null;
    }
}
