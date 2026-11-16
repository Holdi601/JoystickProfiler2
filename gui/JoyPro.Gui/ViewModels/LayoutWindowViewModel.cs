using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The layout window (docs/setup-wizard.md#layout-window): a window of its own, for a second monitor too. Pick a game and
// an aircraft (or let it follow the running game) and see every device with the function on each control - JoyPro's
// drawings with their tables and lines (the Layout page's callout sheets, drawn by CalloutSheetCanvas). Pressing a
// button, moving an axis or a hat lights that control's row up and names its function in big letters at the top.
// - Source: the profile's binds on the connected devices, or a setup plan before it is applied (engine setup preview
//   --live-data: per device its drawing, its sheet and the row texts).
// - Live input: its own DirectInput monitor, only while the window is open and not minimized and "Light up" is on;
//   after 1.5 s without input it reads the devices every 250 ms only (MonitorAsync's idle delay).
// - Everything chosen (game, aircraft, device, options) and the window's place are kept in LayoutWindowSettings.
public sealed partial class LayoutWindowViewModel : ObservableObject
{
    // How often the devices are read after 1.5 s without input (the first press then shows within this time).
    public static readonly TimeSpan LiveIdleDelay = TimeSpan.FromMilliseconds(250);

    public const string AllDevices = "";

    private readonly SetupWizardViewModel.EngineRunner run;
    private readonly Func<string, string> gameName;
    private readonly Action settingsChanged;
    private readonly IJoystickCaptureService? capture;
    private readonly Action<Action> post;
    private readonly string workFolder;
    private readonly Dictionary<string, List<string>> aircraftByGame = new(StringComparer.OrdinalIgnoreCase);
    private string? planFile;
    private bool light;
    private int renderVersion;
    private bool loading;
    private CancellationTokenSource? liveCts;
    private bool windowShown;

    public LayoutWindowViewModel(SetupWizardViewModel.EngineRunner run, string? planFile, Func<string, string> gameName, LayoutWindowSettings settings,
        Action settingsChanged, bool light, IJoystickCaptureService? capture = null, Action<Action>? post = null, string? workFolder = null)
    {
        this.run = run;
        this.planFile = planFile;
        this.gameName = gameName;
        Settings = settings;
        this.settingsChanged = settingsChanged;
        this.light = light;
        this.capture = capture;
        this.post = post ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        this.workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "JoyPro2", "layout-window", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        lightUp = settings.LightUp;
        followGame = settings.FollowGame;
    }

    public LayoutWindowSettings Settings { get; }

    public bool FromPlan => planFile != null;

    public string SourceText => FromPlan
        ? T("Your setup plan, before it is applied: nothing in your profile has changed yet.")
        : T("Your profile: the binds on your connected devices.");

    public ObservableCollection<LayoutPreviewChoice> Games { get; } = [];

    public ObservableCollection<string> Aircraft { get; } = [];

    // Every device of the setup (with its sheet), and the ones shown (all, or the one picked).
    public ObservableCollection<LayoutWindowDevice> Devices { get; } = [];

    public ObservableCollection<LayoutWindowDevice> ShownDevices { get; } = [];

    public ObservableCollection<LayoutPreviewChoice> DeviceChoices { get; } = [];

    [ObservableProperty]
    private LayoutPreviewChoice? selectedGame;

    [ObservableProperty]
    private string? selectedAircraft;

    [ObservableProperty]
    private LayoutPreviewChoice? selectedDevice;

    [ObservableProperty]
    private bool lightUp;

    [ObservableProperty]
    private bool followGame;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private bool isWorking;

    // The function of the control pressed last, in big letters ("Weapon release"), and where it is.
    [ObservableProperty]
    private string lastFunction = string.Empty;

    [ObservableProperty]
    private string lastControl = string.Empty;

    // What the running game reported last (follow the game), for the status line.
    [ObservableProperty]
    private string followText = string.Empty;

    public bool HasLastFunction => LastFunction.Length > 0;

    public bool HasDevices => Devices.Count > 0;

    public bool IsEmpty => !IsWorking && Games.Count == 0;

    public bool ShowsAircraft => Aircraft.Count > 1 || (Aircraft.Count == 1 && !string.Equals(Aircraft[0], SelectedGame?.Id, StringComparison.OrdinalIgnoreCase));

    // The live monitor runs (tests).
    internal bool IsLive => liveCts != null;

    partial void OnLastFunctionChanged(string value) => OnPropertyChanged(nameof(HasLastFunction));

    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnLightUpChanged(bool value)
    {
        Settings.LightUp = value;
        settingsChanged();
        UpdateLive();
        if (!value)
            ClearLive();
    }

    partial void OnFollowGameChanged(bool value)
    {
        Settings.FollowGame = value;
        settingsChanged();
        FollowGameChanged?.Invoke(this, value);
    }

    // The host starts or stops listening to the running game.
    public event EventHandler<bool>? FollowGameChanged;

    // A new source (the wizard's plan, or the profile after Apply): read again.
    public void UseSource(string? plan, bool lightSheets)
    {
        planFile = plan;
        light = lightSheets;
        OnPropertyChanged(nameof(FromPlan));
        OnPropertyChanged(nameof(SourceText));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsWorking = true;
        loading = true;
        try
        {
            EngineCommandResult? result = await run(SourceArgs(), !FromPlan, false, T("Reading where your functions are..."), true);
            aircraftByGame.Clear();
            Games.Clear();
            if (result != null && result.ExitCode == 0)
            {
                using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement game in games.EnumerateArray())
                    {
                        string id = Text(game, "Game");
                        List<string> planes = game.TryGetProperty("Aircraft", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                            ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                            : [];
                        if (id.Length == 0 || planes.Count == 0)
                            continue;
                        aircraftByGame[id] = planes;
                        Games.Add(new LayoutPreviewChoice(id, gameName(id)));
                    }
                }
            }
            Status = Games.Count == 0
                ? (FromPlan ? T("The plan has no function on a device yet.") : T("No binds on your connected devices yet: set up your devices first."))
                : string.Empty;
            SelectedGame = Games.FirstOrDefault(g => string.Equals(g.Id, Settings.Game, StringComparison.OrdinalIgnoreCase)) ?? Games.FirstOrDefault();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Status = T("The layout could not be read: {0}", ex.Message);
        }
        finally
        {
            loading = false;
            IsWorking = false;
        }
        FillAircraft();
    }

    private List<string> SourceArgs()
    {
        List<string> args = ["setup", "preview"];
        if (planFile != null)
            args.AddRange(["--plan", planFile]);
        else
            args.Add("--from-profile");
        return args;
    }

    partial void OnSelectedGameChanged(LayoutPreviewChoice? value)
    {
        if (!loading)
            FillAircraft();
    }

    private void FillAircraft()
    {
        Aircraft.Clear();
        if (SelectedGame == null || !aircraftByGame.TryGetValue(SelectedGame.Id, out List<string>? planes))
        {
            OnPropertyChanged(nameof(ShowsAircraft));
            SelectedAircraft = null;
            SetDevices([]);
            return;
        }
        foreach (string plane in planes)
            Aircraft.Add(plane);
        OnPropertyChanged(nameof(ShowsAircraft));
        string? wanted = planes.FirstOrDefault(p => string.Equals(p, Settings.Aircraft, StringComparison.OrdinalIgnoreCase)) ?? planes.FirstOrDefault();
        if (SelectedAircraft == wanted)
            _ = RenderAsync();
        else
            SelectedAircraft = wanted;
    }

    partial void OnSelectedAircraftChanged(string? value)
    {
        if (value == null || SelectedGame == null)
            return;
        Settings.Game = SelectedGame.Id;
        Settings.Aircraft = value;
        settingsChanged();
        _ = RenderAsync();
    }

    partial void OnSelectedDeviceChanged(LayoutPreviewChoice? value)
    {
        if (value == null)
            return;
        if (!string.Equals(Settings.Device, value.Id, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Device = value.Id;
            settingsChanged();
        }
        FillShown();
    }

    // The sheets of every device for the chosen aircraft; an older request's answer is dropped.
    private async Task RenderAsync()
    {
        string? aircraft = SelectedAircraft;
        LayoutPreviewChoice? game = SelectedGame;
        if (aircraft == null || game == null)
            return;
        int version = ++renderVersion;
        IsWorking = true;
        Status = T("Drawing {0}...", aircraft);
        try
        {
            string folder = Path.Combine(workFolder, "sheets-" + version.ToString(CultureInfo.InvariantCulture));
            List<string> args = SourceArgs();
            args.AddRange(["--game", game.Id, "--aircraft", aircraft, "--out-dir", folder, "--live-data"]);
            if (light)
                args.Add("--light");
            args.AddRange(DeviceReferenceService.ExportDrawingArguments());
            EngineCommandResult? result = await run(args, !FromPlan, false, T("Drawing {0}...", aircraft), true);
            if (version != renderVersion)
                return;
            List<LayoutWindowDevice> list = [];
            if (result != null && result.ExitCode == 0)
            {
                using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in devices.EnumerateArray())
                    {
                        LayoutWindowDevice? device = LayoutWindowDevice.Read(item);
                        if (device != null)
                            list.Add(device);
                    }
                }
            }
            // The hands first: main stick, throttle, collective, left stick, then pedals and panels.
            string[] order = [JoyPro.Setup.SetupRoles.MainStick, JoyPro.Setup.SetupRoles.HeliStick, JoyPro.Setup.SetupRoles.Throttle, JoyPro.Setup.SetupRoles.Collective,
                JoyPro.Setup.SetupRoles.LeftStick, JoyPro.Setup.SetupRoles.Pedals, JoyPro.Setup.SetupRoles.Panel];
            list = list.OrderBy(d => Array.IndexOf(order, d.Role) is int i && i >= 0 ? i : order.Length).ToList();
            SetDevices(list);
            Status = list.Count == 0 ? T("No drawing for these devices: they are not in JoyPro's device catalog.") : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or ArgumentException)
        {
            Status = T("The layout could not be drawn: {0}", ex.Message);
        }
        finally
        {
            if (version == renderVersion)
                IsWorking = false;
        }
    }

    private void SetDevices(List<LayoutWindowDevice> list)
    {
        Devices.Clear();
        foreach (LayoutWindowDevice device in list)
            Devices.Add(device);
        DeviceChoices.Clear();
        DeviceChoices.Add(new LayoutPreviewChoice(AllDevices, T("Every device")));
        foreach (LayoutWindowDevice device in list)
            DeviceChoices.Add(new LayoutPreviewChoice(device.Key, device.Name));
        OnPropertyChanged(nameof(HasDevices));
        LayoutPreviewChoice choice = DeviceChoices.FirstOrDefault(c => string.Equals(c.Id, Settings.Device, StringComparison.OrdinalIgnoreCase)) ?? DeviceChoices[0];
        if (ReferenceEquals(SelectedDevice, choice) || SelectedDevice == choice)
            FillShown();
        else
            SelectedDevice = choice;
        LastFunction = string.Empty;
        LastControl = string.Empty;
    }

    private void FillShown()
    {
        ShownDevices.Clear();
        string wanted = SelectedDevice?.Id ?? AllDevices;
        foreach (LayoutWindowDevice device in Devices.Where(d => wanted.Length == 0 || string.Equals(d.Key, wanted, StringComparison.OrdinalIgnoreCase)))
            ShownDevices.Add(device);
    }

    // ---- live input ----

    // The window is on screen (not minimized): the monitor runs while "Light up" is on.
    public void SetWindowShown(bool shown)
    {
        windowShown = shown;
        UpdateLive();
    }

    private void UpdateLive()
    {
        bool wanted = windowShown && LightUp && capture != null;
        if (!wanted)
        {
            StopLive();
            return;
        }
        if (liveCts != null)
            return;
        liveCts = new CancellationTokenSource();
        CancellationToken token = liveCts.Token;
        // Only the devices the window shows are opened (taken now: the monitor runs on another thread).
        List<string> shown = Devices.Select(d => d.Device).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await capture!.MonitorAsync(
                    e => { if (!token.IsCancellationRequested) post(() => OnInput(e)); },
                    s => { if (!token.IsCancellationRequested) post(() => OnAxes(s)); },
                    LiveIdleDelay, name => FindDevice(shown, name) != null, token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                JoyPro.Logging.JoyProLog.Warn("LayoutWindow", "Live input stopped: " + ex.Message);
            }
        });
    }

    public void StopLive()
    {
        if (liveCts == null)
            return;
        liveCts.Cancel();
        liveCts.Dispose();
        liveCts = null;
        ClearLive();
    }

    private void ClearLive()
    {
        foreach (LayoutWindowDevice device in Devices)
            device.Live.Clear();
    }

    private LayoutWindowDevice? DeviceOf(string device)
    {
        string? name = FindDevice(Devices.Select(d => d.Device), device);
        return name == null ? null : Devices.FirstOrDefault(d => d.Device == name);
    }

    // The same instance (GUID); a name without an instance id (an added catalog device) by its name.
    internal static string? FindDevice(IEnumerable<string> shown, string device)
    {
        List<string> list = shown.ToList();
        return list.FirstOrDefault(d => OverlayResolver.SameDevice(d, device)) ??
               list.FirstOrDefault(d => (!d.Contains('{') || !device.Contains('{')) &&
                                        string.Equals(DeviceMarkerText.DeviceName(d), DeviceMarkerText.DeviceName(device), StringComparison.OrdinalIgnoreCase));
    }

    // A button or hat direction went down or up: its row lights up; a press names its function at the top.
    internal void OnInput(JoystickInputEvent input)
    {
        if (!LightUp)
            return;
        LayoutWindowDevice? device = DeviceOf(input.Device);
        if (device == null || !device.Live.SetButton(input.Input, input.Down) || !input.Down || input.FirstReport)
            return;
        Name(device, input.Input);
    }

    internal void OnAxes(JoystickAxisSnapshot snapshot)
    {
        if (!LightUp)
            return;
        LayoutWindowDevice? device = DeviceOf(snapshot.Device);
        if (device == null)
            return;
        IReadOnlyList<string> toggled = device.Live.SetAxes(snapshot.Axes, snapshot.FirstReport);
        string? moved = toggled.FirstOrDefault(a => device.Live.IsActive(a));
        if (moved != null && !snapshot.FirstReport)
            Name(device, moved);
    }

    private void Name(LayoutWindowDevice device, string input)
    {
        string text = device.TextOf(input, 0);
        string shifted = device.TextOf(input, 1);
        LastFunction = text.Length > 0 ? text : shifted.Length > 0 ? shifted : T("Nothing on it for this aircraft");
        LastControl = device.Name + " - " + DeviceMarkerText.ShortLabel(input) + (text.Length == 0 && shifted.Length > 0 ? " (" + T("with a modifier") + ")" : string.Empty);
    }

    // ---- follow the game ----

    // The running game said which aircraft it flies (DCS's hook) or which game runs: switch to it when "Follow the game"
    // is on and the window knows it; otherwise the manual choice stays.
    public void OnRunningGame(string game, string aircraft)
    {
        if (!FollowGame || string.IsNullOrWhiteSpace(game))
            return;
        LayoutPreviewChoice? choice = Games.FirstOrDefault(g => string.Equals(g.Id, game, StringComparison.OrdinalIgnoreCase));
        FollowText = aircraft.Length > 0 ? T("{0} reports {1}.", gameName(game), aircraft) : T("{0} is running.", gameName(game));
        if (choice == null)
            return;
        if (!ReferenceEquals(SelectedGame, choice))
            SelectedGame = choice;
        string? plane = Aircraft.FirstOrDefault(a => string.Equals(a, aircraft, StringComparison.OrdinalIgnoreCase));
        if (plane != null && plane != SelectedAircraft)
            SelectedAircraft = plane;
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}

public sealed record LayoutPreviewChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

// One device of the layout window: its drawing, its callout sheet with the functions of the aircraft, and what it does
// right now (the canvas lights the row of a held input up).
public sealed class LayoutWindowDevice
{
    private readonly Dictionary<string, string[]> texts;

    public LayoutWindowDevice(string device, string name, string role, CalloutSheet sheet, Bitmap? picture, Dictionary<string, string[]> texts)
    {
        Device = device;
        Name = name;
        Role = role;
        Picture = picture;
        this.texts = new Dictionary<string, string[]>(texts, StringComparer.OrdinalIgnoreCase);
        Callouts = new CalloutSheetViewModel { TextOfColumn = TextOf };
        Callouts.ShowFixed(sheet);
    }

    public string Device { get; }

    // The name without the instance id: the device choice that survives a restart.
    public string Key => DeviceMarkerText.DeviceName(Device);

    public string Name { get; }

    public string Role { get; }

    public string Title => Role.Length > 0 ? Name + " - " + EngineText.Tr(JoyPro.Setup.SetupRoles.Title(Role)) : Name;

    public Bitmap? Picture { get; }

    public CalloutSheetViewModel Callouts { get; }

    public LiveInputState Live { get; } = new();

    // Width / height of the sheet: the window gives it the height that fits its width.
    public double Aspect => Callouts.Sheet is { } s && s.Height > 0 ? s.Width / s.Height : 1.6;

    public string TextOf(string input, int column)
    {
        return texts.TryGetValue(input, out string[]? row) && column >= 0 && column < row.Length ? row[column] : string.Empty;
    }

    internal static LayoutWindowDevice? Read(JsonElement item)
    {
        string sheetFile = item.TryGetProperty("Sheet", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
        if (!File.Exists(sheetFile))
            return null;
        CalloutSheet? sheet = CalloutSheet.Parse(File.ReadAllText(sheetFile));
        if (sheet == null)
            return null;
        string pictureFile = item.TryGetProperty("Picture", out JsonElement p) ? p.GetString() ?? string.Empty : string.Empty;
        Bitmap? picture = File.Exists(pictureFile) ? new Bitmap(pictureFile) : null;
        Dictionary<string, string[]> texts = new(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("Texts", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty row in t.EnumerateObject())
                texts[row.Name] = row.Value.ValueKind == JsonValueKind.Array ? row.Value.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray() : [];
        }
        string Get(string key) => item.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        return new LayoutWindowDevice(Get("Device"), Get("Name"), Get("Role"), sheet, picture, texts);
    }
}
