using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The HUD (HUD page): the user's devices live on screen like a streamer's controller overlay. Every connected stick,
// throttle and pedal set is a widget with its official picture (or the user's layout picture with the button
// positions), tilting with the stick, levers and axis bars moving, pressed buttons and hats lighting up. Next to them
// the binds ticker (what the pressed button does) and the game / aircraft header. The designer places and styles the
// widgets; the layout is saved in %APPDATA%\JoyPro2\hud.json.
public sealed partial class MainWindowViewModel
{
    // Per view model, with the JOYPRO_DATA_DIR in force when it is made (see UiSettingsPath).
    private readonly string HudLayoutPath = Path.Combine(AppInfo.SettingsFolder, "hud.json");
    private static readonly JsonSerializerOptions HudJsonOptions = new() { WriteIndented = true };

    private HudLayout hudLayout = new();
    private HudContext? hudContext;
    private CancellationTokenSource? inputCts;
    private readonly Dictionary<string, HudDevice?> hudDeviceByInputName = new(StringComparer.OrdinalIgnoreCase);
    private int hudSaveVersion;
    private bool hudLoaded;

    public ObservableCollection<HudWidget> HudWidgets { get; } = [];

    // The pictures of the last device read (for the HUD preview renderer test).
    internal Task HudPicturesTask { get; private set; } = Task.CompletedTask;

    public HudContext HudContext => hudContext ??= new HudContext(OverlayLines);

    public IReadOnlyList<string> HudAccents { get; } = ["#38BDF8", "#22D3EE", "#A3E635", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#F8FAFC"];

    public IReadOnlyList<HudAccentOption> HudAccentOptions => hudAccentOptions ??= HudAccents.Select(a => new HudAccentOption(a, new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(a)))).ToList();

    private List<HudAccentOption>? hudAccentOptions;

    public IReadOnlyList<string> HudStyles { get; } = ["Glass", "Neon", "Minimal"];

    public IReadOnlyList<string> HudPictureSources { get; } = ["Auto", "Photo", "Layout"];

    [ObservableProperty]
    private HudWidget? selectedHudWidget;

    [ObservableProperty]
    private string hudAccent = "#38BDF8";

    [ObservableProperty]
    private string hudStyle = "Glass";

    [ObservableProperty]
    private double hudOpacity = 1;

    [ObservableProperty]
    private bool hudStreamWindow;

    [ObservableProperty]
    private string hudStreamBackground = "#00FF00";

    [ObservableProperty]
    private string hudStatus = "Open the HUD page to see your devices.";

    [ObservableProperty]
    private Bitmap? hudPreviewBackground;

    public bool HasSelectedHudWidget => SelectedHudWidget != null;

    public bool SelectedHudWidgetIsDevice => SelectedHudWidget?.IsDevice == true;

    // The grip picked for a base that takes several (saved in the widget; the devices are read again with it).
    public HudChoice? SelectedHudGrip
    {
        get => SelectedHudWidget?.Device?.GripChoices.FirstOrDefault(g => g.Id == SelectedHudWidget.Grip);
        set
        {
            if (SelectedHudWidget != null && value != null && value.Id != SelectedHudWidget.Grip)
                SelectedHudWidget.Grip = value.Id;
        }
    }

    partial void OnSelectedHudWidgetChanged(HudWidget? oldValue, HudWidget? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        OnPropertyChanged(nameof(HasSelectedHudWidget));
        OnPropertyChanged(nameof(SelectedHudWidgetIsDevice));
        OnPropertyChanged(nameof(SelectedHudGrip));
        NotifyHudAxisSelection();
    }

    partial void OnHudAccentChanged(string value) => ApplyHudLook();
    partial void OnHudStyleChanged(string value) => ApplyHudLook();
    partial void OnHudOpacityChanged(double value) => ApplyHudLook();

    partial void OnHudStreamWindowChanged(bool value)
    {
        hudLayout.StreamWindow = value;
        ScheduleHudSave();
        // A running HUD switches between the transparent on-screen window and the stream window.
        if (IsOverlayRunning)
        {
            OverlayPresenter?.Hide();
            OverlayPresenter?.Show(this);
        }
    }

    partial void OnHudStreamBackgroundChanged(string value)
    {
        hudLayout.StreamBackground = value;
        ScheduleHudSave();
    }

    [RelayCommand]
    private void SetHudAccent(string? accent)
    {
        if (!string.IsNullOrWhiteSpace(accent))
            HudAccent = accent;
    }

    private void ApplyHudLook()
    {
        HudContext.Apply(HudAccent, HudStyle, HudOpacity);
        hudLayout.Accent = HudAccent;
        hudLayout.Style = HudStyle;
        hudLayout.Opacity = HudOpacity;
        ScheduleHudSave();
    }

    [RelayCommand]
    private void GoHud() => CurrentStage = UiStage.Hud;

    // The HUD page shows live input in its preview while it is open.
    private void OnHudStageChanged(bool open)
    {
        if (open)
        {
            _ = OpenHudPageAsync();
        }
        else
        {
            StopInputMonitorIfIdle();
        }
    }

    private async Task OpenHudPageAsync()
    {
        if (!hudLoaded)
            await LoadHudDevicesAsync();
        StartInputMonitor();
        if (HudPreviewBackground == null)
            HudPreviewBackground = await LoadHudPreviewBackgroundAsync();
    }

    // The grips set for bases in the HUD designer (device -> catalog id), also before the HUD page was opened.
    private Dictionary<string, string> HudGrips()
    {
        HudLayout layout = hudLayout;
        if (!hudLoaded)
        {
            layout = new HudLayout();
            if (persistUiFiles)
            {
                try
                {
                    if (File.Exists(HudLayoutPath))
                        layout = JsonSerializer.Deserialize<HudLayout>(File.ReadAllText(HudLayoutPath), HudJsonOptions) ?? new HudLayout();
                }
                catch (Exception)
                {
                    layout = new HudLayout();
                }
            }
        }
        Dictionary<string, string> grips = new(StringComparer.OrdinalIgnoreCase);
        foreach (HudWidgetConfig config in layout.Widgets.Where(w => w.Kind == "device" && w.Device.Length > 0 && w.Grip.Length > 0))
            grips[config.Device] = config.Grip;
        return grips;
    }

    private void LoadHudLayout()
    {
        hudLayout = new HudLayout();
        if (persistUiFiles)
        {
            try
            {
                if (File.Exists(HudLayoutPath))
                    hudLayout = JsonSerializer.Deserialize<HudLayout>(File.ReadAllText(HudLayoutPath), HudJsonOptions) ?? new HudLayout();
            }
            catch (Exception)
            {
                hudLayout = new HudLayout();
            }
        }
        HudAccent = string.IsNullOrWhiteSpace(hudLayout.Accent) ? "#38BDF8" : hudLayout.Accent;
        HudStyle = HudStyles.Contains(hudLayout.Style) ? hudLayout.Style : T("Glass");
        HudOpacity = hudLayout.Opacity <= 0 ? 1 : hudLayout.Opacity;
        HudStreamWindow = hudLayout.StreamWindow;
        HudStreamBackground = string.IsNullOrWhiteSpace(hudLayout.StreamBackground) ? "#00FF00" : hudLayout.StreamBackground;
        ApplyHudLook();
    }

    // Saves a moment after the last change (dragging changes the position many times a second).
    private void ScheduleHudSave()
    {
        int version = ++hudSaveVersion;
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            if (version == hudSaveVersion)
                SaveHudLayout();
        }, TaskScheduler.Default);
    }

    private void SaveHudLayout()
    {
        if (!persistUiFiles)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HudLayoutPath)!);
            File.WriteAllText(HudLayoutPath, JsonSerializer.Serialize(hudLayout, HudJsonOptions));
        }
        catch (Exception)
        {
            // The layout is kept in memory; the next change tries again.
        }
    }

    [RelayCommand]
    private async Task LoadHudDevicesAsync()
    {
        if (!hudLoaded)
            LoadHudLayout();
        hudLoaded = true;
        List<string> args = HudDevicesArguments();
        foreach (HudWidgetConfig config in hudLayout.Widgets.Where(w => w.Kind == "device" && w.Grip.Length > 0))
            args.AddRange(["--grip", config.Device + "=" + config.Grip]);
        bool withProfile = !string.IsNullOrWhiteSpace(ProfilePath) && File.Exists(ProfilePath);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: withProfile, mutatesProfile: false, busyLabel: N("Reading your devices for the HUD..."), forceText: false);
        List<(HudDevice Device, string Image, string LayoutPicture, List<(string, double, double)> Markers)> found = [];
        if (result != null && result.ExitCode == 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in devices.EnumerateArray())
                        found.Add(ParseHudDevice(item));
                }
            }
            catch (JsonException)
            {
            }
        }
        RebuildHudWidgets(found.Select(f => f.Device).ToList());
        int shown = HudWidgets.Count(w => w.IsDevice && w.Visible);
        HudStatus = found.Count == 0
            ? T("No devices found. Connect your devices, then press Reload devices.")
            : T("{0} device(s), {1} shown. Drag the widgets in the preview; untick what you do not want to see.", found.Count, shown);
        HudPicturesTask = LoadHudPicturesAsync(found);
    }

    private (HudDevice, string, string, List<(string, double, double)>) ParseHudDevice(JsonElement item)
    {
        List<(string Input, string Control, string Label)> axes = [];
        if (item.TryGetProperty("Axes", out JsonElement axisList) && axisList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement axis in axisList.EnumerateArray())
                axes.Add((ReadJsonString(axis, "Input"), ReadJsonString(axis, "Control"), ReadJsonString(axis, "Label")));
        }
        Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("Labels", out JsonElement labelMap) && labelMap.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty label in labelMap.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? string.Empty;
        }
        List<(string, string)> grips = [];
        if (item.TryGetProperty("GripChoices", out JsonElement gripList) && gripList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement grip in gripList.EnumerateArray())
                grips.Add((ReadJsonString(grip, "Id"), ReadJsonString(grip, "Name")));
        }
        HudDevice device = new(ReadJsonString(item, "Device"), ReadJsonString(item, "Name"), ReadJsonString(item, "Model"), ReadJsonString(item, "Kind"),
            ReadJsonInt(item, "Buttons"), ReadJsonInt(item, "Hats"), axes, labels, grips, ReadJsonString(item, "Hand"));
        string layoutPicture = string.Empty;
        List<(string, double, double)> markers = [];
        if (item.TryGetProperty("Layout", out JsonElement layout) && layout.ValueKind == JsonValueKind.Object)
        {
            layoutPicture = ReadJsonString(layout, "Picture");
            if (layout.TryGetProperty("Markers", out JsonElement markerList) && markerList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement marker in markerList.EnumerateArray())
                {
                    double mx = marker.TryGetProperty("X", out JsonElement xv) && xv.ValueKind == JsonValueKind.Number ? xv.GetDouble() : -1;
                    double my = marker.TryGetProperty("Y", out JsonElement yv) && yv.ValueKind == JsonValueKind.Number ? yv.GetDouble() : -1;
                    if (mx >= 0 && my >= 0)
                        markers.Add((ReadJsonString(marker, "Input"), mx, my));
                }
            }
        }
        return (device, ReadJsonString(item, "Image"), layoutPicture, markers);
    }

    // One widget per device (kept from the saved layout, new devices get a place by their kind), plus header and ticker.
    private void RebuildHudWidgets(List<HudDevice> devices)
    {
        string? selectedId = SelectedHudWidget?.Config.Id;
        foreach (HudWidget old in HudWidgets)
            old.PropertyChanged -= OnHudWidgetChanged;
        HudWidgets.Clear();
        hudDeviceByInputName.Clear();

        HudWidgetConfig header = hudLayout.Widgets.FirstOrDefault(w => w.Kind == "header") ?? AddHudConfig(new HudWidgetConfig { Id = "header", Kind = "header", X = 40, Y = 36 });
        HudWidgetConfig binds = hudLayout.Widgets.FirstOrDefault(w => w.Kind == "binds") ?? AddHudConfig(new HudWidgetConfig { Id = "binds", Kind = "binds", X = 40, Y = 104 });
        AddHudWidget(new HudWidget(header, HudContext, null));
        AddHudWidget(new HudWidget(binds, HudContext, null));

        HashSet<string> usedSlots = new(hudLayout.Widgets.Where(w => w.Kind == "device").Select(w => SlotOf(w.X, w.Y)));
        // A HUD without placed devices yet is arranged once the pictures are in (sizes known).
        hudArrangePending = !hudLayout.Widgets.Any(w => w.Kind == "device");
        foreach (HudDevice device in devices)
        {
            HudWidgetConfig? config = hudLayout.Widgets.FirstOrDefault(w => w.Kind == "device" && OverlayResolver.SameDevice(w.Device, device.Device));
            if (config == null)
            {
                config = DefaultDeviceConfig(device, usedSlots);
                AddHudConfig(config);
            }
            config.Device = device.Device;
            AddHudWidget(new HudWidget(config, HudContext, device));
        }
        // Where each stick element's axes come from (a grip without X/Y takes them from its base).
        ApplyHudAxisSources();
        SelectedHudWidget =HudWidgets.FirstOrDefault(w => w.Config.Id == selectedId) ?? HudWidgets.FirstOrDefault(w => w.IsDevice && w.Visible) ?? HudWidgets.FirstOrDefault();
        ScheduleHudSave();
    }

    private bool hudArrangePending;

    // Raised when the view should measure the widgets and call ArrangeHudWidgets (the sizes depend on the pictures).
    public event EventHandler? HudArrangeRequested;

    [RelayCommand]
    private void ArrangeHud() => HudArrangeRequested?.Invoke(this, EventArgs.Empty);

    // Packs the visible device widgets along the bottom of the reference screen: throttle, collective and left-hand
    // devices from the left, right-hand sticks from the right, pedals in the middle; what does not fit goes a row up.
    // sizes: the widgets' measured sizes (with their scale) in reference pixels.
    internal void ArrangeHudWidgets(IReadOnlyDictionary<HudWidget, Avalonia.Size> sizes)
    {
        const double margin = 30, gap = 22;
        List<HudWidget> devices = HudWidgets.Where(w => w.IsDevice && w.Visible && sizes.ContainsKey(w)).ToList();
        bool LeftSide(HudWidget w) => w.Device!.DeviceKind is "throttle" or "collective" or "quadrant" || w.Device.Hand == "left";
        List<HudWidget> left = devices.Where(w => LeftSide(w) && w.Device!.DeviceKind != "pedals")
            .OrderBy(w => w.Device!.DeviceKind is "throttle" or "collective" or "quadrant" ? 0 : 1).ToList();
        List<HudWidget> pedals = devices.Where(w => w.Device!.DeviceKind == "pedals").ToList();
        List<HudWidget> right = devices.Except(left).Except(pedals).OrderBy(w => w.Device!.Hand == "right" ? 0 : 1).ToList();

        double rowBottom = HudLayout.ReferenceHeight - margin;
        while (left.Count + right.Count + pedals.Count > 0)
        {
            double x = margin;
            double rowHeight = 0;
            List<(HudWidget Widget, double X)> row = [];
            double rightEdge = HudLayout.ReferenceWidth - margin;
            // Right-hand devices first claim the right end, then the left group fills from the left, pedals in between.
            foreach (HudWidget widget in right.ToList())
            {
                double width = sizes[widget].Width;
                if (rightEdge - width < x && row.Count > 0)
                    break;
                rightEdge -= width;
                row.Add((widget, rightEdge));
                rightEdge -= gap;
                rowHeight = Math.Max(rowHeight, sizes[widget].Height);
                right.Remove(widget);
            }
            foreach (HudWidget widget in left.ToList())
            {
                double width = sizes[widget].Width;
                if (x + width > rightEdge && row.Count > 0)
                    break;
                row.Add((widget, x));
                x += width + gap;
                rowHeight = Math.Max(rowHeight, sizes[widget].Height);
                left.Remove(widget);
            }
            foreach (HudWidget widget in pedals.ToList())
            {
                double width = sizes[widget].Width;
                if (x + width > rightEdge && row.Count > 0)
                    break;
                double centred = Math.Max(x, Math.Min(rightEdge - width, (x + rightEdge - width) / 2));
                row.Add((widget, centred));
                x = centred + width + gap;
                rowHeight = Math.Max(rowHeight, sizes[widget].Height);
                pedals.Remove(widget);
            }
            foreach ((HudWidget widget, double wx) in row)
            {
                widget.X = Math.Round(wx);
                widget.Y = Math.Round(Math.Max(0, rowBottom - sizes[widget].Height));
            }
            rowBottom -= rowHeight + gap;
            if (row.Count == 0 || rowBottom < 200)
                break;
        }
        HudStatus = T("Arranged {0} device(s) along the bottom of the screen.", devices.Count);
    }

    private HudWidgetConfig AddHudConfig(HudWidgetConfig config)
    {
        hudLayout.Widgets.Add(config);
        return config;
    }

    private void AddHudWidget(HudWidget widget)
    {
        widget.PropertyChanged += OnHudWidgetChanged;
        HudWidgets.Add(widget);
    }

    private void OnHudWidgetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudWidget.IsSelected))
            return;
        ScheduleHudSave();
        if (e.PropertyName == nameof(HudWidget.Grip) && sender is HudWidget { IsDevice: true })
            _ = LoadHudDevicesAsync();
        else if (e.PropertyName is nameof(HudWidget.AxisX) or nameof(HudWidget.AxisY) or nameof(HudWidget.AxisTwist))
            ApplyHudAxisSources();
    }

    // Default places on the 1920 x 1080 screen: right-hand stick bottom right, left stick / throttle bottom left,
    // pedals bottom centre; panels, keyboards and others start hidden.
    private static HudWidgetConfig DefaultDeviceConfig(HudDevice device, HashSet<string> usedSlots)
    {
        // A virtual joystick (vJoy) mirrors the real devices: hidden at first like panels.
        bool isVirtual = device.Device.Contains("vJoy", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("vJoy", StringComparison.OrdinalIgnoreCase);
        bool visible = !isVirtual && device.DeviceKind is "stick" or "throttle" or "pedals" or "collective" or "yoke" or "hotas" or "base";
        string[] preferred = isVirtual ? ["spare1", "spare2", "spare3", "spare4"] : device.DeviceKind switch
        {
            "throttle" or "collective" or "quadrant" => ["left", "left2", "center"],
            "pedals" => ["center", "center2"],
            "stick" or "base" or "yoke" or "hotas" => device.Title.Contains("left", StringComparison.OrdinalIgnoreCase) || device.Name.StartsWith("L-", StringComparison.OrdinalIgnoreCase)
                ? ["left2", "left", "right2"]
                : ["right", "right2", "left2"],
            _ => ["spare1", "spare2", "spare3", "spare4"]
        };
        string slot = preferred.FirstOrDefault(s => !usedSlots.Contains(s)) ?? preferred[0];
        usedSlots.Add(slot);
        (double x, double y) = SlotPlace(slot);
        return new HudWidgetConfig { Id = Guid.NewGuid().ToString("N")[..8], Kind = "device", Device = device.Device, X = x, Y = y, Scale = 0.85, Visible = visible };
    }

    private static readonly string[] HudSlots = ["right", "right2", "left", "left2", "center", "center2", "spare1", "spare2", "spare3", "spare4"];

    // At 80 % a device card is about 430 x 370: stick bottom right, throttle bottom left, pedals bottom centre.
    private static (double X, double Y) SlotPlace(string slot) => slot switch
    {
        "right" => (1460, 680),
        "right2" => (1010, 680),
        "left" => (30, 680),
        "left2" => (480, 680),
        "center" => (800, 860),
        "center2" => (800, 460),
        "spare1" => (560, 120),
        "spare2" => (1000, 120),
        "spare3" => (1440, 120),
        _ => (1440, 300)
    };

    private static string SlotOf(double x, double y) => HudSlots.FirstOrDefault(s => SlotPlace(s) == (x, y)) ?? x + "," + y;

    // Product photo (downloaded once) and layout picture, both with the background cut away.
    private async Task LoadHudPicturesAsync(List<(HudDevice Device, string Image, string LayoutPicture, List<(string, double, double)> Markers)> devices)
    {
        foreach ((HudDevice device, string image, string layoutPicture, List<(string, double, double)> markers) in devices)
        {
            Bitmap? photo = await LoadHudPictureAsync(image);
            Bitmap? layout = await LoadHudPictureAsync(layoutPicture);
            HudWidget? widget = HudWidgets.FirstOrDefault(w => w.Device == device);
            device.SetPictures(photo, layout, markers, widget?.PictureSource ?? "Auto");
            device.Icon = DeviceIconFor(device.Device);
            widget?.RefreshVisibility();
        }
        if (hudArrangePending)
        {
            hudArrangePending = false;
            HudArrangeRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<Bitmap?> LoadHudPictureAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        try
        {
            string? file = await DeviceImageCache.LocalFileAsync(source);
            if (file == null)
                return null;
            string cut = await Task.Run(() => ImageCutout.CutoutFile(file));
            return await DeviceImageCache.LoadAsync(cut, 600);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The preview's backdrop: the DCS aircraft's own main menu picture, so the HUD is seen over a cockpit-like scene.
    private async Task<Bitmap?> LoadHudPreviewBackgroundAsync()
    {
        MediaIndex index = await GetMediaIndexAsync();
        string[] wanted = [overlayPlane, OverlayPlaneChoice, "F-16C_50", "FA-18C_hornet", T("A-10C II")];
        string? plane = wanted.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && index.AircraftBackgrounds.ContainsKey(p)) ?? index.AircraftBackgrounds.Keys.FirstOrDefault();
        return plane == null ? null : await DeviceImageCache.LoadAsync(index.AircraftBackgrounds[plane], 1280);
    }

    // One DirectInput monitor feeds the HUD widgets (live preview and on-screen HUD) and the binds ticker.
    private void StartInputMonitor()
    {
        if (inputCts != null)
            return;
        inputCts = new CancellationTokenSource();
        CancellationToken token = inputCts.Token;
        _ = joystickCaptureService.MonitorAsync(
            e => Dispatcher.UIThread.Post(() => OnHudInput(e)),
            s => Dispatcher.UIThread.Post(() => OnHudAxes(s)),
            token);
    }

    private void StopInputMonitorIfIdle()
    {
        if (IsOverlayRunning || IsHudStage || inputCts == null)
            return;
        inputCts.Cancel();
        inputCts = null;
        foreach (HudWidget widget in HudWidgets.Where(w => w.Device != null))
            widget.Device!.ReleaseAll();
    }

    internal void OnHudInput(JoystickInputEvent input)
    {
        HudDeviceFor(input.Device)?.SetInput(input.Input, input.Down);
        if (IsOverlayRunning)
            OnOverlayInput(input);
    }

    internal void OnHudAxes(JoystickAxisSnapshot snapshot) => HudDeviceFor(snapshot.Device)?.ApplyAxes(snapshot.Axes);

    private HudDevice? HudDeviceFor(string inputName)
    {
        if (!hudDeviceByInputName.TryGetValue(inputName, out HudDevice? device))
        {
            device = HudWidgets.Select(w => w.Device).FirstOrDefault(d => d != null && OverlayResolver.SameDevice(d.Device, inputName));
            hudDeviceByInputName[inputName] = device;
        }
        return device;
    }

    [RelayCommand]
    private void ResetHudLayout()
    {
        hudLayout.Widgets.Clear();
        RebuildHudWidgets(HudWidgets.Where(w => w.Device != null).Select(w => w.Device!).ToList());
        foreach (HudWidget widget in HudWidgets)
            widget.RefreshVisibility();
        HudStatus = T("Layout reset to the default places.");
    }

    [RelayCommand]
    private void ShowAllHudDevices()
    {
        foreach (HudWidget widget in HudWidgets)
            widget.Visible = true;
    }
}
