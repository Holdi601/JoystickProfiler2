using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Services;
using JoyPro.Watch;

namespace JoyPro.Gui.ViewModels;

// Tray mode (docs/sync-watcher.md#tray-and-autostart). The game config watcher keeps running after the GUI closes in
// a separate small process, the tray host (JoyPro.Tray.exe): "Start with Windows (in the tray)" writes the per-user Run
// entry for it, "Close to tray" starts it when the GUI closes. The GUI itself always ends when its window closes, so
// its memory goes back to Windows.
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private bool closeToTray;

    [ObservableProperty]
    private string autostartStatus = string.Empty;

    private AutostartService? autostart;

    // Starts the tray host (it ends a second copy of itself at once); the tests replace it.
    internal Func<bool> EnsureTrayHost { get; set; } = () => false;

    // The Run key behind "Start with Windows". Only the real app (persisted settings) gets the registry; the tests
    // replace AutostartService.StoreFactory anyway.
    internal AutostartService Autostart
    {
        get => autostart ??= new AutostartService(persistUiFiles ? AutostartService.StoreFactory() : new InMemoryRunKeyStore());
        set
        {
            autostart = value;
            UpdateAutostartStatus();
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (loadingUiSettings)
            return;
        try
        {
            if (value)
            {
                Autostart.Enable();
                // Started with Windows, JoyPro2 lives in the tray: closing the window should hand over to it too.
                if (!CloseToTray)
                    CloseToTray = true;
            }
            else
            {
                Autostart.Disable();
            }
            UpdateAutostartStatus();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            AutostartStatus = T("Windows did not let JoyPro2 change its startup entry: {0}", ex.Message);
        }
        SaveUiSettings();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (loadingUiSettings)
            return;
        SaveUiSettings();
        if (value)
            EnsureTrayHost();
    }

    // At startup: with "Start with Windows" on, the entry is written again when it is missing or starts a copy of
    // JoyPro2 that no longer exists (a moved portable folder). With "Close to tray" on the tray host is started (it
    // pauses while the GUI runs, so its icon is there). Nothing is written while the setting is off.
    public void ApplyAutostartAtStartup()
    {
        if (StartWithWindows)
        {
            try
            {
                Autostart.Repair();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                AutostartStatus = T("Windows did not let JoyPro2 write its startup entry: {0}", ex.Message);
                return;
            }
        }
        UpdateAutostartStatus();
        if (CloseToTray)
            EnsureTrayHost();
    }

    private void UpdateAutostartStatus()
    {
        if (!StartWithWindows)
        {
            AutostartStatus = T("Off: JoyPro2 only runs when you start it.");
            return;
        }
        AutostartService service = Autostart;
        if (!service.IsRegistered)
            AutostartStatus = T("On, but the startup entry is missing: untick and tick the box again.");
        else if (service.ApprovedByWindows == false)
            AutostartStatus = T("Windows has JoyPro2 switched off in its startup apps: turn it on in Windows Settings > Apps > Startup.");
        else if (!service.PointsHere)
            AutostartStatus = T("On: Windows starts the JoyPro2 at {0} (tick the box again to start this one).", AutostartService.ExecutableOf(service.RegisteredCommand));
        else
            AutostartStatus = T("On: JoyPro2's tray icon starts when you sign in to Windows.");
    }

    // --review <watch key> (from the tray host's notification): the configs are checked now and their changes shown.
    // Without a loaded profile the last one is opened first (the changes need a profile to go into).
    public async Task ReviewGameChangesAsync(IEnumerable<string> watchKeys)
    {
        List<string> keys = watchKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
            return;
        if ((string.IsNullOrWhiteSpace(ProfilePath) || !File.Exists(ProfilePath)) && CanOpenLastProfile)
            await OpenLastProfileInternalAsync(showMissingStatus: true);
        foreach (string key in keys)
            QueueGameSyncCheck(key);
    }
}
