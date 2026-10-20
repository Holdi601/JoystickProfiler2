using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Watch;

namespace JoyPro.Gui;

// The GUI's lifetime around tray mode (docs/sync-watcher.md#tray-and-autostart). The GUI always ends when its window
// closes; with "Close to tray" on, the tray host (JoyPro.Tray.exe) keeps watching the games and is started here if it
// does not run yet. A second GUI start brings this one to the front and hands over its --review list.
[SupportedOSPlatform("windows")]
internal sealed class AppShell
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly MainWindowViewModel vm;
    private readonly SingleInstanceGuard? instanceGuard;
    private readonly InstanceNames names;
    private MainWindow? window;
    private bool updating;

    public AppShell(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel vm, SingleInstanceGuard? instanceGuard, InstanceNames names)
    {
        this.desktop = desktop;
        this.vm = vm;
        this.instanceGuard = instanceGuard;
        this.names = names;
    }

    public void Start(StartupOptions options)
    {
        vm.EnsureTrayHost = () => TrayHostLauncher.EnsureRunning(names);
        vm.ShutdownRequested += () => updating = true;
        vm.ApplyAutostartAtStartup();
        if (instanceGuard != null)
            instanceGuard.ActivationRequested += () => Dispatcher.UIThread.Post(OnActivationRequested);
        desktop.Exit += OnExit;

        MainWindow main = new() { DataContext = vm };
        window = main;
        desktop.MainWindow = main;
        if (options.ReviewKeys.Count > 0)
            main.Opened += ReviewOnOpen;

        void ReviewOnOpen(object? sender, EventArgs e)
        {
            main.Opened -= ReviewOnOpen;
            _ = vm.ReviewGameChangesAsync(options.ReviewKeys);
        }
    }

    // Another start of JoyPro2 (a click on the tray icon, the tray's Review, a shortcut): to the front, and its review
    // list (if any) checked now.
    private void OnActivationRequested()
    {
        if (window != null)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
        List<string> keys = TakeForwardedReviewKeys(names);
        if (keys.Count > 0)
            _ = vm.ReviewGameChangesAsync(keys);
    }

    // The GUI ends (its process too: the memory goes back to Windows). With "Close to tray" on the tray host goes on
    // watching; not while an update replaces the files.
    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        desktop.Exit -= OnExit;
        vm.StopLlmServerOnExit();
        if (vm.CloseToTray && !updating)
            vm.EnsureTrayHost();
    }

    // A second start leaves its --review list here (one watch key per line) before it signals this one.
    public static void ForwardReviewKeys(InstanceNames names, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return;
        try
        {
            Directory.CreateDirectory(names.SettingsFolder);
            File.AppendAllLines(names.GuiRequestFile, keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The running JoyPro2 still comes to the front; its own watcher offers the changes.
        }
    }

    public static List<string> TakeForwardedReviewKeys(InstanceNames names)
    {
        try
        {
            if (!File.Exists(names.GuiRequestFile))
                return [];
            List<string> keys = File.ReadAllLines(names.GuiRequestFile).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            File.Delete(names.GuiRequestFile);
            return keys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
