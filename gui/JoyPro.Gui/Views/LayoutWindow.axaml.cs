using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The layout window (ViewModels/LayoutWindowViewModel.cs): a window of its own, not owned by the main window, so it can
// stay on a second monitor. It comes back where it was (monitor, position, size, maximized) when that monitor is still
// there, else on the main window's monitor. The live input runs only while it is shown and not minimized.
public partial class LayoutWindow : Window
{
    private readonly DispatcherTimer saveTimer;
    private bool restored;

    public LayoutWindow()
    {
        InitializeComponent();
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            Remember();
            SettingsSaved?.Invoke();
        };
        PositionChanged += (_, _) => RememberLater();
        Opened += (_, _) => Model?.SetWindowShown(WindowState != WindowState.Minimized);
    }

    // The host saves the settings (ui-settings.json) when the window moved or was resized.
    public Action? SettingsSaved { get; set; }

    private LayoutWindowViewModel? Model => DataContext as LayoutWindowViewModel;

    private LayoutWindowSettings? Settings => Model?.Settings;

    // Before the first show: where the settings say, if that monitor is still there.
    public void Restore(Window? near)
    {
        LayoutWindowSettings? s = Settings;
        if (s == null)
            return;
        if (s.Width is double w && w >= MinWidth && s.Height is double h && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }
        Screen? screen = s.X is int x && s.Y is int y ? Screens.All.FirstOrDefault(sc => sc.WorkingArea.Contains(new PixelPoint(x + 40, y + 20))) : null;
        if (screen != null && s.X is int px && s.Y is int py)
        {
            Position = new PixelPoint(px, py);
        }
        else
        {
            Screen? target = near != null ? Screens.ScreenFromWindow(near) : Screens.Primary;
            if (target != null)
            {
                PixelRect area = target.WorkingArea;
                double scale = target.Scaling;
                int width = (int)Math.Min(Width * scale, area.Width), height = (int)Math.Min(Height * scale, area.Height);
                Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
            }
        }
        if (s.Maximized)
            WindowState = WindowState.Maximized;
        restored = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            Model?.SetWindowShown(IsVisible && WindowState != WindowState.Minimized);
            RememberLater();
        }
        else if (change.Property == ClientSizeProperty)
        {
            RememberLater();
        }
    }

    private void RememberLater()
    {
        if (!restored)
            return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    // Where the window is now (a minimized window keeps the place it had).
    public void Remember()
    {
        LayoutWindowSettings? s = Settings;
        if (s == null || WindowState == WindowState.Minimized)
            return;
        s.Maximized = WindowState == WindowState.Maximized;
        if (!s.Maximized)
        {
            s.X = Position.X;
            s.Y = Position.Y;
            s.Width = Math.Round(Width);
            s.Height = Math.Round(Height);
        }
        Screen? screen = Screens.ScreenFromWindow(this);
        s.Screen = screen == null ? string.Empty : string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        saveTimer.Stop();
        Remember();
        Model?.SetWindowShown(false);
        base.OnClosing(e);
    }

    private void CloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
