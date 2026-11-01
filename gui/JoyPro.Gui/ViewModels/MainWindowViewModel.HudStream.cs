using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The HUD's outputs (docs/hud.md "Streamer mode") and the aircraft widget ("Aircraft stick and throttle"):
//   - On my screen: the transparent HUD over the game (HudWindow, on top, click-through).
//   - Browser Source: the local page for OBS (HudStreamServer on 127.0.0.1, real transparency).
//   - Stream window: a window of its own for OBS's Window Capture, not on top, transparent or a chroma key colour.
// The outputs are independent: the player may have no HUD on his screen while the stream shows it. All of them run
// while the HUD is on (Start HUD); each draws only what changed, at most HudMaxFps times a second.
public sealed partial class MainWindowViewModel
{
    public const string HudTransparent = "Transparent";

    // Set by the main window (the stream window); null in tests.
    public IOverlayPresenter? HudStreamPresenter { get; set; }

    private bool loadingHudOutputs;
    private HudStreamServer? hudStreamServer;
    private HudStreamHub? hudStreamHub;

    [ObservableProperty]
    private bool hudOnScreen = true;

    [ObservableProperty]
    private bool hudStreamBrowser;

    [ObservableProperty]
    private decimal hudStreamPort = 8765;

    [ObservableProperty]
    private bool hudStreamWindow;

    [ObservableProperty]
    private string hudStreamBackground = HudTransparent;

    [ObservableProperty]
    private int hudMaxFps = 30;

    // The designer's preview shows the widgets ticked for the stream instead of those for the screen.
    [ObservableProperty]
    private bool hudPreviewStream;

    [ObservableProperty]
    private string hudStreamStatus = string.Empty;

    public string HudStreamUrl => "http://127.0.0.1:" + (int)HudStreamPort + "/hud";

    public IReadOnlyList<int> HudFpsChoices { get; } = [15, 30, 60];

    public IReadOnlyList<HudChoice> HudStreamBackgrounds { get; } =
    [
        new(HudTransparent, N("Transparent (per-pixel alpha)")),
        new("#00FF00", N("Green screen (#00FF00)")),
        new("#FF00FF", N("Magenta (#FF00FF)")),
        new("#0000FF", N("Blue (#0000FF)"))
    ];

    public HudChoice? SelectedHudStreamBackground
    {
        get => HudStreamBackgrounds.FirstOrDefault(b => string.Equals(b.Id, HudStreamBackground, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null)
                HudStreamBackground = value.Id;
        }
    }

    public bool HudStreamIsChroma => !string.Equals(HudStreamBackground, HudTransparent, StringComparison.OrdinalIgnoreCase);

    public int HudStreamClients => hudStreamServer?.ClientCount ?? 0;

    internal HudStreamHub? HudStreamHubForTests => hudStreamHub;

    internal HudStreamServer? HudStreamServerForTests => hudStreamServer;

    private void LoadHudOutputs()
    {
        loadingHudOutputs = true;
        try
        {
            HudOnScreen = hudLayout.OnScreen;
            HudStreamBrowser = hudLayout.StreamBrowser;
            HudStreamPort = hudLayout.StreamPort is > 1023 and < 65536 ? hudLayout.StreamPort : 8765;
            HudStreamWindow = hudLayout.StreamWindow;
            HudStreamBackground = string.IsNullOrWhiteSpace(hudLayout.StreamBackground) ? HudTransparent : hudLayout.StreamBackground;
            HudMaxFps = HudFpsChoices.Contains(hudLayout.MaxFps) ? hudLayout.MaxFps : 30;
            HudPreviewStream = hudLayout.PreviewStream;
        }
        finally
        {
            loadingHudOutputs = false;
        }
    }

    // Kept in hud.json for the next start.
    partial void OnHudPreviewStreamChanged(bool value)
    {
        if (loadingHudOutputs)
            return;
        hudLayout.PreviewStream = value;
        ScheduleHudSave();
    }

    partial void OnHudOnScreenChanged(bool value)
    {
        if (loadingHudOutputs)
            return;
        hudLayout.OnScreen = value;
        ScheduleHudSave();
        ApplyHudOutputs();
    }

    partial void OnHudStreamBrowserChanged(bool value)
    {
        if (loadingHudOutputs)
            return;
        hudLayout.StreamBrowser = value;
        ScheduleHudSave();
        ApplyHudOutputs();
    }

    partial void OnHudStreamPortChanged(decimal value)
    {
        OnPropertyChanged(nameof(HudStreamUrl));
        if (loadingHudOutputs)
            return;
        hudLayout.StreamPort = (int)value;
        ScheduleHudSave();
        // A running page server moves to the new port.
        if (hudStreamServer != null)
        {
            StopHudStreamServer();
            ApplyHudOutputs();
        }
    }

    partial void OnHudStreamWindowChanged(bool value)
    {
        if (loadingHudOutputs)
            return;
        hudLayout.StreamWindow = value;
        ScheduleHudSave();
        ApplyHudOutputs();
    }

    partial void OnHudStreamBackgroundChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedHudStreamBackground));
        OnPropertyChanged(nameof(HudStreamIsChroma));
        if (loadingHudOutputs)
            return;
        hudLayout.StreamBackground = value;
        ScheduleHudSave();
        // The stream window takes its new background when it opens again.
        if (IsOverlayRunning && HudStreamWindow)
        {
            HudStreamPresenter?.Hide();
            HudStreamPresenter?.Show(this);
        }
    }

    partial void OnHudMaxFpsChanged(int value)
    {
        HudPump.MaxFps = value;
        if (loadingHudOutputs)
            return;
        hudLayout.MaxFps = value;
        ScheduleHudSave();
    }

    // Shows and hides the outputs as ticked (only while the HUD is on).
    private void ApplyHudOutputs()
    {
        if (!IsOverlayRunning)
            return;
        if (HudOnScreen)
            OverlayPresenter?.Show(this);
        else
            OverlayPresenter?.Hide();
        if (HudStreamWindow)
            HudStreamPresenter?.Show(this);
        else
            HudStreamPresenter?.Hide();
        if (HudStreamBrowser)
            StartHudStreamServer();
        else
            StopHudStreamServer();
        UpdateHudStreamStatus();
    }

    private void StopHudOutputs()
    {
        OverlayPresenter?.Hide();
        HudStreamPresenter?.Hide();
        StopHudStreamServer();
        UpdateHudStreamStatus();
    }

    internal void StartHudStreamServer()
    {
        if (hudStreamServer != null)
            return;
        hudStreamHub = new HudStreamHub(HudWidgets, HudContext, () => hudAircraft);
        HudStreamServer server = new((int)HudStreamPort, hudStreamHub);
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            hudStreamHub.Dispose();
            hudStreamHub = null;
            HudStreamStatus = T("The page for OBS cannot use port {0}: {1}. Pick another port.", (int)HudStreamPort, ex.Message);
            return;
        }
        server.ClientsChanged += () => Dispatcher.UIThread.Post(UpdateHudStreamStatus);
        hudStreamHub.Server = server;
        hudStreamHub.UpdateRequested = () => Dispatcher.UIThread.Post(HudPump.Kick);
        hudStreamServer = server;
        HudContext.Lines.CollectionChanged += OnHudLinesChanged;
        HudContext.PropertyChanged += OnHudHeaderChanged;
        UpdateHudStreamStatus();
    }

    internal void StopHudStreamServer()
    {
        if (hudStreamServer == null)
            return;
        HudContext.Lines.CollectionChanged -= OnHudLinesChanged;
        HudContext.PropertyChanged -= OnHudHeaderChanged;
        hudStreamServer.Stop();
        hudStreamServer = null;
        hudStreamHub?.Dispose();
        hudStreamHub = null;
        UpdateHudStreamStatus();
    }

    private void OnHudLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HudPump.Kick();

    private void OnHudHeaderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => HudPump.Kick();

    // The widgets, their places or styles changed: the stream page builds them again (with the next update).
    private void HudStreamLayoutChanged()
    {
        if (hudStreamHub == null)
            return;
        hudStreamHub.LayoutChanged();
        HudPump.Kick();
    }

    private void UpdateHudStreamStatus()
    {
        OnPropertyChanged(nameof(HudStreamClients));
        if (hudStreamServer == null)
        {
            if (!HudStreamBrowser || !IsOverlayRunning)
                HudStreamStatus = HudStreamBrowser ? T("The page for OBS starts with the HUD (Start HUD).") : string.Empty;
            return;
        }
        int clients = hudStreamServer.ClientCount;
        HudStreamStatus = clients == 0
            ? T("Waiting for OBS: add a Browser Source with {0} (1920 x 1080).", hudStreamServer.Url)
            : Plural("{0} page connected (OBS): it shows the HUD.", "{0} pages connected (OBS): they show the HUD.", clients);
    }

    // Copy (next to the address): the main window puts it on the clipboard.
    public event EventHandler<string>? CopyTextRequested;

    [RelayCommand]
    private void CopyHudStreamUrl()
    {
        CopyTextRequested?.Invoke(this, HudStreamUrl);
        HudStreamStatus = T("Copied {0}. In OBS: Sources > + > Browser, paste it as URL, width 1920, height 1080.", HudStreamUrl);
    }

    // ---- The aircraft widget ----------------------------------------------------------------------------------------

    private HudAircraft? hudAircraft;

    // Where the cockpit pictures are (null = <data>GameDataCockpitPictures; tests and renders set another folder).
    internal string? HudAircraftRoot { get; set; }
    private string hudAircraftKey = string.Empty;
    private int hudAircraftVersion;

    public ObservableCollection<HudChoice> HudAircraftChoices { get; } = [];

    public ObservableCollection<HudChoice> HudStickDeviceChoices { get; } = [];

    public ObservableCollection<HudChoice> HudThrottleDeviceChoices { get; } = [];

    public HudAircraft? CurrentHudAircraft => hudAircraft;

    public bool SelectedHudWidgetIsAircraft => SelectedHudWidget?.IsAircraft == true;

    public bool SelectedHudWidgetIsHeader => SelectedHudWidget?.IsHeader == true;

    public HudChoice? SelectedHudAircraftChoice
    {
        get => SelectedHudWidget is { IsAircraft: true } w ? HudAircraftChoices.FirstOrDefault(c => c.Id == w.AircraftChoice) ?? HudAircraftChoices.FirstOrDefault() : null;
        set
        {
            if (SelectedHudWidget is { IsAircraft: true } w && value != null && value.Id != w.AircraftChoice)
                w.AircraftChoice = value.Id;
        }
    }

    public HudChoice? SelectedHudStickDevice
    {
        get => SelectedHudWidget is { IsAircraft: true } w ? HudStickDeviceChoices.FirstOrDefault(c => c.Id == w.StickDevice) ?? HudStickDeviceChoices.FirstOrDefault() : null;
        set
        {
            if (SelectedHudWidget is { IsAircraft: true } w && value != null && value.Id != w.StickDevice)
                w.StickDevice = value.Id;
        }
    }

    public HudChoice? SelectedHudThrottleDevice
    {
        get => SelectedHudWidget is { IsAircraft: true } w ? HudThrottleDeviceChoices.FirstOrDefault(c => c.Id == w.ThrottleDevice) ?? HudThrottleDeviceChoices.FirstOrDefault() : null;
        set
        {
            if (SelectedHudWidget is { IsAircraft: true } w && value != null && value.Id != w.ThrottleDevice)
                w.ThrottleDevice = value.Id;
        }
    }

    // What the aircraft widget shows now, in words (under its settings).
    public string HudAircraftStatus => hudAircraft == null
        ? T("No aircraft: pick one here or under DCS aircraft, or install the DCS hook. The pictures come from Cockpits > Build pictures from my games (or a picture pack).")
        : T("{0}: {1}. Moves with {2} and {3}.", hudAircraft.Title, hudAircraft.Credit,
            hudAircraft.StickSource?.ShortTitle ?? T("no stick"), hudAircraft.ThrottleSource?.ShortTitle ?? T("no throttle"));

    private HudWidget? HudAircraftWidget => HudWidgets.FirstOrDefault(w => w.IsAircraft);

    internal void RefreshHudAircraftChoices()
    {
        string? keep = SelectedHudWidget is { IsAircraft: true } w ? w.AircraftChoice : null;
        HudAircraftChoices.Clear();
        HudAircraftChoices.Add(new HudChoice(string.Empty, T("Automatic (the aircraft you fly)")));
        try
        {
            foreach (HudChoice choice in HudAircraftLoader.Choices(HudAircraftRoot))
                HudAircraftChoices.Add(choice);
        }
        catch (Exception)
        {
            // No pictures folder yet: Automatic only.
        }
        if (keep != null && HudAircraftChoices.All(c => c.Id != keep))
            HudAircraftChoices.Add(new HudChoice(keep, keep));
        OnPropertyChanged(nameof(SelectedHudAircraftChoice));
    }

    private void RefreshHudDeviceChoices()
    {
        List<HudDevice> devices = HudWidgets.Where(x => x.Device != null).Select(x => x.Device!).ToList();
        HudStickDeviceChoices.Clear();
        HudStickDeviceChoices.Add(new HudChoice(string.Empty, T("Automatic (your stick)")));
        foreach (HudDevice device in devices.Where(d => d.HasGimbal))
            HudStickDeviceChoices.Add(new HudChoice(device.Device, device.ShortTitle));
        HudThrottleDeviceChoices.Clear();
        HudThrottleDeviceChoices.Add(new HudChoice(string.Empty, T("Automatic (your throttle)")));
        foreach (HudDevice device in devices.Where(d => d.Levers.Count > 0))
            HudThrottleDeviceChoices.Add(new HudChoice(device.Device, device.ShortTitle));
        OnPropertyChanged(nameof(SelectedHudStickDevice));
        OnPropertyChanged(nameof(SelectedHudThrottleDevice));
    }

    // The aircraft the widget shows: its own pick, else the DCS aircraft choice, else the aircraft DCS reported.
    internal string CurrentHudPlane()
    {
        if (!string.IsNullOrWhiteSpace(OverlayPlaneChoice) && OverlayPlaneChoice != OverlayAutomaticPlane)
            return OverlayPlaneChoice;
        // JoyPro's name of the aircraft DCS reported (engine overlay map), else the DCS unit type itself.
        if (overlayPlane.Length > 0 && (overlayGame == "DCS" || overlayGame.Length == 0))
            return overlayPlane;
        return overlayUnit;
    }

    // Loads the aircraft's grips when the aircraft (or the widget's pick) changed; the old one stays until the new one
    // is there. Pictures are read off the UI thread.
    [RelayCommand]
    internal async Task RefreshHudAircraftAsync()
    {
        HudWidget? widget = HudAircraftWidget;
        if (widget == null)
            return;
        string choice = widget.AircraftChoice;
        string plane = CurrentHudPlane();
        string key = choice.Length > 0 ? "choice:" + choice : "plane:" + plane;
        if (key == hudAircraftKey && hudAircraft != null)
        {
            RebindHudAircraft();
            return;
        }
        int version = ++hudAircraftVersion;
        HudAircraftLoader.Found? found = choice.Length == 0 && plane.Length == 0 ? null : await Task.Run(() => HudAircraftLoader.Find(choice, plane, HudAircraftRoot));
        if (version != hudAircraftVersion)
            return;
        hudAircraft?.Detach();
        hudAircraft = found == null ? null : HudAircraftLoader.Build(found);
        hudAircraftKey = key;
        foreach (HudWidget w in HudWidgets.Where(x => x.IsAircraft))
            w.Aircraft = hudAircraft;
        RebindHudAircraft();
        HudStreamLayoutChanged();
        OnPropertyChanged(nameof(CurrentHudAircraft));
    }

    // Which devices move the aircraft's grips and which inputs light its controls.
    internal void RebindHudAircraft()
    {
        if (hudAircraft == null)
        {
            OnPropertyChanged(nameof(HudAircraftStatus));
            return;
        }
        HudWidget? widget = HudAircraftWidget;
        List<HudDevice> devices = HudWidgets.Where(x => x.Device != null).Select(x => x.Device!).ToList();
        HudDevice? stick = devices.FirstOrDefault(d => widget != null && widget.StickDevice.Length > 0 && OverlayResolver.SameDevice(d.Device, widget.StickDevice))
                           ?? devices.Where(d => d.HasGimbal && !d.IsVirtual && d.DeviceKind is "stick" or "base" or "hotas" or "yoke").OrderBy(d => d.Hand == "right" ? 0 : d.Hand == "left" ? 2 : 1).FirstOrDefault()
                           ?? devices.FirstOrDefault(d => d.HasGimbal && !d.IsVirtual)
                           ?? devices.FirstOrDefault(d => d.HasGimbal);
        HudDevice? throttle = devices.FirstOrDefault(d => widget != null && widget.ThrottleDevice.Length > 0 && OverlayResolver.SameDevice(d.Device, widget.ThrottleDevice))
                              ?? devices.FirstOrDefault(d => d.Levers.Count > 0 && !d.IsVirtual && d.DeviceKind is "throttle" or "collective" or "quadrant" or "hotas")
                              ?? devices.FirstOrDefault(d => d.Levers.Count > 0 && !d.IsVirtual)
                              ?? devices.FirstOrDefault(d => d.Levers.Count > 0);
        hudAircraft.SetSources(stick, throttle);
        hudAircraft.BindDevices(devices);
        foreach (HudWidget w in HudWidgets.Where(x => x.IsAircraft))
            w.RefreshAircraft();
        OnPropertyChanged(nameof(HudAircraftStatus));
    }
}
