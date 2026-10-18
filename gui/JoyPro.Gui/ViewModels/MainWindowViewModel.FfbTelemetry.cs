using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;
using JoyPro.Ffb.Core.Telemetry.Il2;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The "Force feedback (telemetry)" page: JoyPro drives a force feedback stick from the game's telemetry through a
// node graph (JoyPro.Ffb.exe, gui/JoyPro.Ffb). Settings go through `engine ffb telemetry config set/clear` (the engine
// owns telemetry-ffb.json) and a running JoyPro.Ffb is told to "reload". The page also hosts the graph editor and the
// telemetry inspector.
//
// Process manager (light, like the Forza app's "only while the game is running"): only while the feature is enabled,
// every 5 s the supported games are looked up by process name; when one runs, JoyPro.Ffb is started for it with the
// game's and the GUI's pid. It ends itself when the game exits (it waits on the game's handle) or the GUI goes away;
// the GUI only notices the exit. Disabling the feature or closing the GUI sends "stop" and kills it after 3 s.
// While the feature is disabled nothing polls at all. Cost while enabled and no game runs: one
// Process.GetProcessesByName per process name every 5 s - each walks the process table (a few ms), the Process objects
// are disposed right away; nothing is looked up while JoyPro.Ffb runs.
public sealed partial class MainWindowViewModel
{
    public const int FfbTelemetrySettingsTab = 0;
    public const int FfbTelemetryEditorTab = 1;
    public const int FfbTelemetryInspectorTab = 2;

    private const string FfbTelemetryOffText = "off - telemetry force feedback is switched off";
    private const int FfbTelemetryLogLines = 200;

    public ObservableCollection<FfbTelemetryGameItem> FfbTelemetryGames { get; } = [];

    public ObservableCollection<FfbTelemetryDeviceOption> FfbTelemetryDevices { get; } = [];

    // Graphs a game can use: built-in + the files of the Graphs folder.
    public ObservableCollection<FfbGraphOption> FfbTelemetryGraphOptions { get; } = [];

    // Graphs an aircraft can use: "" = the game's graph, + the files.
    public ObservableCollection<FfbGraphOption> FfbTelemetryAircraftGraphOptions { get; } = [];

    public ObservableCollection<FfbTelemetryFieldItem> FfbTelemetryFields { get; } = [];

    public ObservableCollection<string> FfbTelemetryLog { get; } = [];

    public FfbGraphEditorViewModel FfbGraphEditor { get; } = new();

    [ObservableProperty]
    private bool ffbTelemetryEnabled;

    [ObservableProperty]
    private FfbTelemetryDeviceOption? selectedFfbTelemetryDevice;

    [ObservableProperty]
    private double ffbTelemetryStrength = 100;

    [ObservableProperty]
    private double ffbTelemetryMaxForce = TelemetryFfbConfig.DefaultMaxForce * 100;

    [ObservableProperty]
    private bool ffbTelemetryInvertX;

    [ObservableProperty]
    private bool ffbTelemetryInvertY;

    // JoyPro provides the centring spring while it drives the stick (graph springs + a plain spring + held in pauses).
    [ObservableProperty]
    private bool ffbTelemetryCentringSpring = true;

    [ObservableProperty]
    private double ffbTelemetryCentringStiffness = TelemetryFfbConfig.DefaultCentringStiffness * 100;

    public string FfbTelemetryCentringStiffnessText => FfbTelemetryCentringStiffness.ToString("0", CultureInfo.InvariantCulture) + " %";

    // The picked device is a VPforce stick: its Configurator's own spring overrides JoyPro's spring and trim.
    public bool FfbTelemetryIsVpforce => SelectedFfbTelemetryDevice?.IsVpforce ?? FfbTelemetryDeviceOption.IsVpforceDevice(string.Empty, string.Empty, ffbTelemetryConfig.DeviceName);

    public static string FfbTelemetryVpforceHint => FfbDirectionWizard.VpforceHint;

    [ObservableProperty]
    private FfbTelemetryGameItem? selectedFfbTelemetryGame;

    [ObservableProperty]
    private string ffbTelemetryStatusText = FfbTelemetryOffText;

    [ObservableProperty]
    private string ffbTelemetryMessage = string.Empty;

    [ObservableProperty]
    private string ffbTelemetrySetupChanges = string.Empty;

    [ObservableProperty]
    private string ffbTelemetryDeviceInfo = "Press Refresh to list the force feedback devices.";

    [ObservableProperty]
    private string ffbTelemetryConfigPath = string.Empty;

    [ObservableProperty]
    private int ffbTelemetryTab;

    [ObservableProperty]
    private string ffbTelemetryFieldFilter = string.Empty;

    [ObservableProperty]
    private string ffbTelemetryFieldCount = "No telemetry yet: it shows while JoyPro.Ffb runs for a game.";

    [ObservableProperty]
    private string ffbTelemetryNewAircraft = string.Empty;

    [ObservableProperty]
    private string ffbTelemetryCurrentAircraft = string.Empty;

    [ObservableProperty]
    private bool ffbTelemetryProcessRunning;

    public bool IsFfbTelemetryStage => CurrentStage == UiStage.FfbTelemetry;

    public string FfbTelemetryStrengthText => FfbTelemetryStrength.ToString("0", CultureInfo.InvariantCulture) + " %";

    public string FfbTelemetryMaxForceText => T("{0} % (hard cap)", FfbTelemetryMaxForce.ToString("0", CultureInfo.InvariantCulture));

    public bool FfbTelemetryGameNeedsSetup => SelectedFfbTelemetryGame?.SetupNeeded ?? false;

    // Test seams (the defaults come from FfbTelemetryServices, which the tests replace).
    internal IFfbHostLauncher FfbHostLauncher { get; set; } = FfbTelemetryServices.Launcher;

    internal Func<IReadOnlyList<string>, int?> FfbFindGameProcess { get; set; } = FfbTelemetryServices.FindGameProcess;

    internal string FfbTelemetryExePath { get; set; } = FfbTelemetryServices.ExePath();

    internal Action<Action> FfbTelemetryDispatch { get; set; } = DispatchFfbToUi;

    internal TimeSpan FfbTelemetryPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan FfbTelemetrySaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    internal int FfbTelemetryGuiPid { get; set; } = Environment.ProcessId;

    internal bool FfbTelemetryPolling => ffbTelemetryPollCts != null;

    internal bool FfbGraphPreviewRunning => ffbPreviewCts != null;

    private readonly object ffbTelemetryPendingGate = new();
    private readonly List<FfbTelemetryScope> ffbTelemetryPendingOrder = [];
    private readonly Dictionary<FfbTelemetryScope, Dictionary<string, string>> ffbTelemetryPending = [];
    private readonly Dictionary<string, double> ffbLatestTelemetry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FfbTelemetryFieldItem> ffbFieldItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> ffbSeenKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> ffbNodeValues = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Name, string Title)> ffbTelemetryEffects = FfbEffectNames.All;
    private TelemetryFfbConfig ffbTelemetryConfig = new();
    private bool ffbTelemetryLoading;
    private bool ffbTelemetryStatusLoaded;
    private int ffbTelemetrySaveVersion;
    private CancellationTokenSource? ffbTelemetryPollCts;
    private CancellationTokenSource? ffbPreviewCts;
    private IFfbHostProcess? ffbHost;
    private string ffbHostGame = string.Empty;
    private int ffbHostGamePid;
    private string ffbHostLastReason = string.Empty;
    private string ffbHostLastState = string.Empty;
    private string ffbHostExitReason = string.Empty;
    private string ffbTelemetryLastFault = string.Empty;
    private int? ffbBlockedGamePid;
    private bool ffbWatchOn;
    private bool mainWindowMinimized;
    private DateTime ffbInspectorUpdated = DateTime.MinValue;
    private bool ffbKeyOptionsDirty = true;
    private string ffbKeyOptionsGame = string.Empty;
    private IReadOnlyList<TelemetryDescriptor> ffbDescriptors = AircraftTelemetry.Descriptors;

    // Called by the constructor: the games are known without the engine; with persisted settings (the real app, not the
    // tests) the config file is read directly so the process manager runs without the page ever being opened.
    private void InitializeFfbTelemetry(bool readConfigFile)
    {
        FfbGraphEditor.Saved += OnFfbGraphSaved;
        FfbGraphEditor.CaptureInput = CaptureFfbInputAsync;
        FfbGraphEditor.ForceFeedbackDevice = () => SelectedFfbTelemetryDevice?.InstanceGuid ?? ffbTelemetryConfig.Device;
        foreach (TelemetryGame game in TelemetryGame.All)
        {
            if (!FfbTelemetryGames.Any(g => string.Equals(g.Id, game.Id, StringComparison.OrdinalIgnoreCase)))
                FfbTelemetryGames.Add(CreateFfbTelemetryGame(game.Id, game.Title, game.ProcessNames));
        }
        SelectedFfbTelemetryGame ??= FfbTelemetryGames.FirstOrDefault();
        RebuildFfbGraphOptions();
        if (readConfigFile)
        {
            try
            {
                ApplyFfbTelemetryConfig(TelemetryFfbConfig.Load(FfbTelemetryServices.ConfigFile()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FfbTelemetryMessage = T("Cannot read the telemetry force feedback settings: {0}", ex.Message);
            }
        }
        else
        {
            ApplyFfbTelemetryConfig(new TelemetryFfbConfig());
        }
        RefreshFfbTelemetryKeyOptions();
    }

    // "Press the button now" for the graph editor's button and trim nodes: buttons and hats of every device (DirectInput,
    // in the background), no keyboard and no axes.
    private async Task<(string Device, string Input)?> CaptureFfbInputAsync(TimeSpan timeout, CancellationToken token)
    {
        JoystickCaptureService.CaptureResult? result = await joystickCaptureService.CaptureNextAsync(
            new JoystickCaptureService.CaptureOptions { AxisMode = false, IncludeKeyboard = false, TimeoutMs = (int)timeout.TotalMilliseconds },
            token);
        return result == null || string.IsNullOrWhiteSpace(result.Input) ? null : (result.Device, result.Input);
    }

    private FfbTelemetryGameItem CreateFfbTelemetryGame(string id, string title, IReadOnlyList<string> processNames) =>
        new(id, title, processNames, FfbTelemetryGraphOptions)
        {
            Changed = OnFfbTelemetryItemChanged,
            SetupNeeded = !string.Equals(id, TelemetryGame.WarThunder, StringComparison.OrdinalIgnoreCase)
        };

    private static void DispatchFfbToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ---- Page visibility ----

    [RelayCommand]
    private void GoFfbTelemetry() => CurrentStage = UiStage.FfbTelemetry;

    private void OnFfbTelemetryStageChanged(bool open)
    {
        if (open && !ffbTelemetryStatusLoaded)
        {
            ffbTelemetryStatusLoaded = true;
            RebuildFfbGraphOptions();
            FfbGraphEditor.RefreshGraphFilesCommand.Execute(null);
            _ = RefreshFfbTelemetryStatusAsync();
            // The force feedback devices at once (read-only), so a connected FFB stick is offered without Refresh.
            if (FfbTelemetryDevices.Count == 0)
                _ = RefreshFfbTelemetryDevicesAsync();
        }
        UpdateFfbTelemetryWatch();
    }

    // The main window was minimised or restored: no live view while it is minimised.
    public void SetMainWindowMinimized(bool minimized)
    {
        if (mainWindowMinimized == minimized)
            return;
        mainWindowMinimized = minimized;
        UpdateFfbTelemetryWatch();
        UpdateLiveInputMonitor();
    }

    partial void OnFfbTelemetryTabChanged(int value)
    {
        UpdateFfbTelemetryWatch();
        if (value == FfbTelemetryInspectorTab)
            UpdateFfbTelemetryFields(rebuild: true);
    }

    private bool FfbTelemetryPageVisible => IsFfbTelemetryStage && !mainWindowMinimized;

    // "watch on" while the editor or the inspector is visible and JoyPro.Ffb runs; the local preview otherwise.
    internal void UpdateFfbTelemetryWatch()
    {
        bool want = ffbHost != null && FfbTelemetryPageVisible && FfbTelemetryTab is FfbTelemetryEditorTab or FfbTelemetryInspectorTab;
        if (ffbHost == null)
        {
            ffbWatchOn = false;
        }
        else if (want != ffbWatchOn && SendFfbHostCommand(want ? HostProtocol.WatchOn : HostProtocol.WatchOff))
        {
            ffbWatchOn = want;
            if (!want)
                FfbGraphEditor.ClearLiveValues();
        }
        UpdateFfbGraphPreview();
    }

    private void UpdateFfbGraphPreview()
    {
        bool want = ffbHost == null && FfbTelemetryPageVisible && FfbTelemetryTab == FfbTelemetryEditorTab;
        if (want && ffbPreviewCts == null)
        {
            ffbPreviewCts = new CancellationTokenSource();
            _ = RunFfbGraphPreviewAsync(ffbPreviewCts.Token);
        }
        else if (!want && ffbPreviewCts != null)
        {
            ffbPreviewCts.Cancel();
            ffbPreviewCts.Dispose();
            ffbPreviewCts = null;
            if (ffbHost == null)
                FfbGraphEditor.ClearLiveValues();
        }
    }

    // Without JoyPro.Ffb: the edited graph evaluated here, at most 10 times a second and only while it is visible.
    private async Task RunFfbGraphPreviewAsync(CancellationToken token)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            try
            {
                FfbGraphEditor.EvaluateLocally(clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                // Only a preview: a graph the evaluator cannot handle must not take the page down.
                FfbGraphEditor.Message = T("Preview stopped: {0}", ex.Message);
                return;
            }
            try
            {
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- Loading (engine ffb telemetry status) ----

    [RelayCommand]
    private async Task RefreshFfbTelemetryStatusAsync()
    {
        // Changes still waiting for their save go first, so the answer includes them.
        await FlushFfbTelemetrySettingsAsync();
        EngineCommandResult? result = await RunEngineAsync(["ffb", "telemetry", "status"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Loading telemetry force feedback..."), forceText: false);
        if (result == null)
        {
            FfbTelemetryMessage = StatusText;
            return;
        }
        if (result.ExitCode != 0)
        {
            FfbTelemetryMessage = T("engine ffb telemetry status: {0}", BuildFailureStatus(result));
            return;
        }
        try
        {
            ApplyFfbTelemetryStatus(ExtractJsonPayload(result.StandardOutput));
            // Nothing to report when the settings are read (the file is named in the tooltip of the page title).
            FfbTelemetryMessage = string.Empty;
        }
        catch (JsonException ex)
        {
            FfbTelemetryMessage = T("The engine's telemetry force feedback status could not be read: {0}", ex.Message);
        }
    }

    internal void ApplyFfbTelemetryStatus(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        FfbTelemetryConfigPath = FfbJsonString(root, "ConfigPath");

        if (TryFfbJson(root, "Effects", out JsonElement effects) && effects.ValueKind == JsonValueKind.Array)
        {
            List<(string, string)> list = [];
            foreach (JsonElement effect in effects.EnumerateArray())
            {
                string name = FfbJsonString(effect, "Name");
                if (name.Length > 0)
                    list.Add((name, FfbJsonString(effect, "Title") is { Length: > 0 } title ? title : name));
            }
            if (list.Count > 0)
                ffbTelemetryEffects = list;
        }

        if (TryFfbJson(root, "Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement game in games.EnumerateArray())
            {
                string id = FfbJsonString(game, "Game");
                if (id.Length == 0)
                    continue;
                FfbTelemetryGameItem? item = FfbTelemetryGames.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    List<string> names = [];
                    if (TryFfbJson(game, "ProcessNames", out JsonElement processNames) && processNames.ValueKind == JsonValueKind.Array)
                        names.AddRange(processNames.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0));
                    item = CreateFfbTelemetryGame(id, FfbJsonString(game, "Title") is { Length: > 0 } title ? title : id, names);
                    FfbTelemetryGames.Add(item);
                }
                item.Running = FfbJsonBool(game, "Running") ?? item.Running;
                item.SetupNeeded = FfbJsonBool(game, "SetupNeeded") ?? item.SetupNeeded;
                item.SetupInstalled = FfbJsonBool(game, "SetupInstalled") ?? false;
                item.SetupDetail = FfbJsonString(game, "SetupDetail");
                item.Endpoint = FfbJsonString(game, "Endpoint");
            }
        }

        if (TryFfbJson(root, "Config", out JsonElement config) && config.ValueKind == JsonValueKind.Object)
            ApplyFfbTelemetryConfig(TelemetryFfbConfig.Parse(config.GetRawText()));

        // A JoyPro.Ffb this GUI did not start (a second GUI, a manual run): its last status, for information.
        if (ffbHost == null && TryFfbJson(root, "Process", out JsonElement process) && FfbJsonBool(process, "Running") == true &&
            TryFfbJson(root, "LastStatus", out JsonElement last) && last.ValueKind == JsonValueKind.Object &&
            FfbJsonString(last, "reason") is { Length: > 0 } reason)
            FfbTelemetryStatusText = T("{0} (JoyPro.Ffb started elsewhere)", reason);

        OnPropertyChanged(nameof(FfbTelemetryGameNeedsSetup));
    }

    // Puts a config into the page without sending anything back to the engine.
    private void ApplyFfbTelemetryConfig(TelemetryFfbConfig config)
    {
        ffbTelemetryLoading = true;
        try
        {
            ffbTelemetryConfig = config;
            FfbTelemetryEnabled = config.Enabled;
            FfbTelemetryStrength = Math.Round(config.Strength * 100);
            FfbTelemetryMaxForce = Math.Round(config.MaxForce * 100);
            (FfbTelemetryInvertX, FfbTelemetryInvertY) = config.InvertFor(config.Device);
            FfbTelemetryCentringSpring = config.CentringSpring;
            FfbTelemetryCentringStiffness = Math.Round(config.CentringStiffness * 100);
            if (config.Device.Length == 0)
            {
                SelectedFfbTelemetryDevice = null;
            }
            else
            {
                FfbTelemetryDeviceOption? device = FfbTelemetryDevices.FirstOrDefault(d => string.Equals(d.InstanceGuid, config.Device, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                {
                    device = new FfbTelemetryDeviceOption(config.Device, config.DeviceName.Length > 0 ? config.DeviceName : config.Device, T("saved; Refresh to check it is connected"), true);
                    FfbTelemetryDevices.Add(device);
                }
                SelectedFfbTelemetryDevice = device;
            }

            EnsureFfbGraphOptions(config);
            foreach (FfbTelemetryGameItem game in FfbTelemetryGames)
            {
                GameFfbSettings settings = config.Game(game.Id);
                game.Loading = true;
                game.Enabled = settings.Enabled;
                game.Strength = Math.Round(settings.Strength * 100);
                game.Graph = FfbTelemetryGraphOptions.FirstOrDefault(o => string.Equals(o.Value, settings.Graph, StringComparison.OrdinalIgnoreCase)) ?? FfbGraphOption.BuiltIn;
                game.Loading = false;

                game.Effects.Clear();
                foreach ((string name, string title) in ffbTelemetryEffects)
                {
                    FfbTelemetryEffectItem effect = new(game.Id, name, title) { Loading = true, Changed = OnFfbTelemetryItemChanged };
                    if (settings.Effects.TryGetValue(name, out EffectSetting? stored))
                    {
                        effect.Enabled = stored.Enabled;
                        effect.Strength = Math.Round(stored.Strength * 100);
                    }
                    effect.Loading = false;
                    game.Effects.Add(effect);
                }

                game.Aircraft.Clear();
                foreach (KeyValuePair<string, AircraftFfbSettings> plane in settings.Aircraft.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    game.Aircraft.Add(CreateFfbTelemetryAircraft(game.Id, plane.Key, plane.Value.Graph, plane.Value.Strength));
            }
        }
        finally
        {
            ffbTelemetryLoading = false;
        }
        UpdateFfbTelemetryPolling();
    }

    private FfbTelemetryAircraftItem CreateFfbTelemetryAircraft(string game, string name, string graph, double strength)
    {
        FfbTelemetryAircraftItem item = new(game, name, FfbTelemetryAircraftGraphOptions) { Loading = true, Changed = OnFfbTelemetryItemChanged };
        item.Graph = FfbTelemetryAircraftGraphOptions.FirstOrDefault(o => string.Equals(o.Value, graph, StringComparison.OrdinalIgnoreCase)) ?? FfbTelemetryAircraftGraphOptions.FirstOrDefault();
        item.Strength = Math.Round(strength * 100);
        item.Loading = false;
        return item;
    }

    // Graph files referenced by the config but missing from the folder stay pickable (marked).
    private void EnsureFfbGraphOptions(TelemetryFfbConfig config)
    {
        foreach (GameFfbSettings settings in config.Games.Values)
        {
            AddMissingFfbGraphOption(FfbTelemetryGraphOptions, settings.Graph);
            foreach (AircraftFfbSettings plane in settings.Aircraft.Values)
                AddMissingFfbGraphOption(FfbTelemetryAircraftGraphOptions, plane.Graph);
        }
    }

    private static void AddMissingFfbGraphOption(ObservableCollection<FfbGraphOption> options, string value)
    {
        if (value.Length > 0 && !options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)))
            options.Add(new FfbGraphOption(value, T("{0}  (file missing)", value)));
    }

    // The *.jpffb.json files of the Graphs folder, keeping what every game and aircraft has picked.
    internal void RebuildFfbGraphOptions()
    {
        List<string> files = [];
        string folder = FfbTelemetryServices.GraphFolder();
        try
        {
            if (Directory.Exists(folder))
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*" + TelemetryFfbPaths.GraphExtension)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FfbTelemetryMessage = T("Cannot read the Graphs folder: {0}", ex.Message);
        }

        List<(FfbTelemetrySettingItem Item, string Value)> picks = [];
        foreach (FfbTelemetryGameItem game in FfbTelemetryGames)
        {
            picks.Add((game, game.Graph?.Value ?? ffbTelemetryConfig.Game(game.Id).Graph));
            foreach (FfbTelemetryAircraftItem plane in game.Aircraft)
                picks.Add((plane, plane.Graph?.Value ?? string.Empty));
        }
        foreach ((FfbTelemetrySettingItem item, _) in picks)
            item.Loading = true;
        try
        {
            FfbTelemetryGraphOptions.Clear();
            FfbTelemetryGraphOptions.Add(FfbGraphOption.BuiltIn);
            FfbTelemetryAircraftGraphOptions.Clear();
            FfbTelemetryAircraftGraphOptions.Add(new FfbGraphOption(string.Empty, T("Same as the game")));
            foreach (string file in files)
            {
                FfbTelemetryGraphOptions.Add(new FfbGraphOption(file, file));
                FfbTelemetryAircraftGraphOptions.Add(new FfbGraphOption(file, file));
            }
            EnsureFfbGraphOptions(ffbTelemetryConfig);
            foreach ((FfbTelemetrySettingItem item, string value) in picks)
            {
                if (item is FfbTelemetryGameItem game)
                {
                    AddMissingFfbGraphOption(FfbTelemetryGraphOptions, value);
                    game.Graph = FfbTelemetryGraphOptions.First(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
                }
                else if (item is FfbTelemetryAircraftItem plane)
                {
                    AddMissingFfbGraphOption(FfbTelemetryAircraftGraphOptions, value);
                    plane.Graph = FfbTelemetryAircraftGraphOptions.First(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            foreach ((FfbTelemetrySettingItem item, _) in picks)
                item.Loading = false;
        }
    }

    // ---- Settings -> engine ----

    partial void OnFfbTelemetryEnabledChanged(bool value)
    {
        if (ffbTelemetryLoading)
            return;
        ffbTelemetryConfig.Enabled = value;
        ffbTelemetryLastFault = string.Empty;
        ffbBlockedGamePid = null;
        QueueFfbTelemetrySet(FfbTelemetryScope.Global, "enabled", FfbTelemetrySettingItem.Format(value));
        UpdateFfbTelemetryPolling();
    }

    partial void OnFfbTelemetryStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(FfbTelemetryStrengthText));
        if (!ffbTelemetryLoading)
            QueueFfbTelemetrySet(FfbTelemetryScope.Global, "strength", FfbTelemetrySettingItem.Format(Math.Round(value) / 100.0));
    }

    partial void OnFfbTelemetryMaxForceChanged(double value)
    {
        OnPropertyChanged(nameof(FfbTelemetryMaxForceText));
        if (!ffbTelemetryLoading)
            QueueFfbTelemetrySet(FfbTelemetryScope.Global, "max-force", FfbTelemetrySettingItem.Format(Math.Round(value) / 100.0));
    }

    partial void OnFfbTelemetryInvertXChanged(bool value)
    {
        if (!ffbTelemetryLoading)
            QueueFfbTelemetryInvert("invert-x", value);
    }

    partial void OnFfbTelemetryInvertYChanged(bool value)
    {
        if (!ffbTelemetryLoading)
            QueueFfbTelemetryInvert("invert-y", value);
    }

    // The directions belong to a device: saved for the picked device (its own entry) and globally (devices without one).
    private void QueueFfbTelemetryInvert(string key, bool value)
    {
        QueueFfbTelemetrySet(FfbTelemetryScope.Global, key, FfbTelemetrySettingItem.Format(value));
        string device = TelemetryFfbConfig.NormaliseDeviceKey(SelectedFfbTelemetryDevice?.InstanceGuid);
        if (device.Length > 0)
        {
            if (!ffbTelemetryConfig.Devices.TryGetValue(device, out DeviceFfbSettings? settings))
                ffbTelemetryConfig.Devices[device] = settings = new DeviceFfbSettings { InvertX = FfbTelemetryInvertX, InvertY = FfbTelemetryInvertY };
            if (key == "invert-x")
                settings.InvertX = value;
            else
                settings.InvertY = value;
            QueueFfbTelemetrySet(FfbTelemetryScope.ForDevice(device), key, FfbTelemetrySettingItem.Format(value));
        }
        ffbTelemetryConfig.InvertX = FfbTelemetryInvertX;
        ffbTelemetryConfig.InvertY = FfbTelemetryInvertY;
    }

    partial void OnFfbTelemetryCentringSpringChanged(bool value)
    {
        if (!ffbTelemetryLoading)
            QueueFfbTelemetrySet(FfbTelemetryScope.Global, "centring-spring", FfbTelemetrySettingItem.Format(value));
    }

    partial void OnFfbTelemetryCentringStiffnessChanged(double value)
    {
        OnPropertyChanged(nameof(FfbTelemetryCentringStiffnessText));
        if (!ffbTelemetryLoading)
            QueueFfbTelemetrySet(FfbTelemetryScope.Global, "centring-stiffness", FfbTelemetrySettingItem.Format(Math.Round(value) / 100.0));
    }

    partial void OnSelectedFfbTelemetryDeviceChanged(FfbTelemetryDeviceOption? value)
    {
        OnPropertyChanged(nameof(FfbTelemetryIsVpforce));
        if (value == null)
            return;
        // Each device keeps its own directions (found by the direction test).
        bool loading = ffbTelemetryLoading;
        ffbTelemetryLoading = true;
        try
        {
            (FfbTelemetryInvertX, FfbTelemetryInvertY) = ffbTelemetryConfig.InvertFor(value.InstanceGuid);
        }
        finally
        {
            ffbTelemetryLoading = loading;
        }
        if (loading)
            return;
        QueueFfbTelemetrySet(FfbTelemetryScope.Global, "device", value.InstanceGuid);
        QueueFfbTelemetrySet(FfbTelemetryScope.Global, "device-name", value.Name);
    }

    partial void OnSelectedFfbTelemetryGameChanged(FfbTelemetryGameItem? value)
    {
        FfbTelemetrySetupChanges = string.Empty;
        OnPropertyChanged(nameof(FfbTelemetryGameNeedsSetup));
        RefreshFfbTelemetryKeyOptions();
    }

    private void OnFfbTelemetryItemChanged(FfbTelemetryScope scope, string key, string value)
    {
        if (ffbTelemetryLoading)
            return;
        if (scope.Plane == null && scope.Effect == null && key == "enabled" && scope.Game != null)
        {
            ffbTelemetryConfig.Game(scope.Game).Enabled = value == "true";
            UpdateFfbTelemetryStatusLine();
        }
        QueueFfbTelemetrySet(scope, key, value);
    }

    // Changes are collected per scope and sent a moment after the last one (sliders change many times a second).
    private void QueueFfbTelemetrySet(FfbTelemetryScope scope, string key, string value)
    {
        lock (ffbTelemetryPendingGate)
        {
            if (!ffbTelemetryPending.TryGetValue(scope, out Dictionary<string, string>? values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                ffbTelemetryPending[scope] = values;
                ffbTelemetryPendingOrder.Add(scope);
            }
            values.Remove(key);
            values[key] = value;
        }
        int version = Interlocked.Increment(ref ffbTelemetrySaveVersion);
        _ = FlushFfbTelemetryLaterAsync(version);
    }

    private async Task FlushFfbTelemetryLaterAsync(int version)
    {
        if (FfbTelemetrySaveDelay > TimeSpan.Zero)
            await Task.Delay(FfbTelemetrySaveDelay);
        if (version == Volatile.Read(ref ffbTelemetrySaveVersion))
            await FlushFfbTelemetrySettingsAsync();
    }

    // `ffb telemetry config set` per changed scope, then "reload" to a running JoyPro.Ffb.
    internal async Task<bool> FlushFfbTelemetrySettingsAsync()
    {
        List<(FfbTelemetryScope Scope, Dictionary<string, string> Values)> batch;
        lock (ffbTelemetryPendingGate)
        {
            batch = ffbTelemetryPendingOrder.Select(s => (s, ffbTelemetryPending[s])).ToList();
            ffbTelemetryPending.Clear();
            ffbTelemetryPendingOrder.Clear();
        }
        if (batch.Count == 0)
            return true;
        bool ok = true;
        foreach ((FfbTelemetryScope scope, Dictionary<string, string> values) in batch)
        {
            List<string> args = ["ffb", "telemetry", "config", "set", .. scope.ToArguments()];
            foreach (KeyValuePair<string, string> pair in values)
                args.AddRange(["--param", pair.Key + "=" + pair.Value]);
            if (!await RunFfbTelemetryEngineAsync(args, "Saving telemetry force feedback..."))
                ok = false;
        }
        if (ok)
        {
            FfbTelemetryMessage = T("Saved.");
            ffbBlockedGamePid = null;
            SendFfbHostCommand(HostProtocol.Reload);
        }
        return ok;
    }

    private async Task<bool> RunFfbTelemetryEngineAsync(List<string> args, string busyLabel)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: busyLabel, forceText: false);
        if (result == null)
        {
            FfbTelemetryMessage = T("engine {0}: {1}", string.Join(" ", args), StatusText);
            return false;
        }
        if (result.ExitCode != 0)
        {
            FfbTelemetryMessage = T("engine {0}: {1}", string.Join(" ", args.Take(4)), BuildFailureStatus(result));
            return false;
        }
        return true;
    }

    // ---- Devices (JoyPro.Ffb devices --json, read-only, only on request) ----

    [RelayCommand]
    private async Task RefreshFfbTelemetryDevicesAsync()
    {
        FfbTelemetryDeviceInfo = T("Looking for force feedback devices...");
        FfbToolResult result;
        try
        {
            result = await FfbHostLauncher.RunToolAsync(FfbTelemetryExePath, ["devices", "--json"]);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            FfbTelemetryDeviceInfo = T("Cannot run JoyPro.Ffb: {0}", ex.Message);
            return;
        }
        if (result.ExitCode != 0)
        {
            string error = (result.StandardError + "\n" + result.StandardOutput).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            FfbTelemetryDeviceInfo = T("JoyPro.Ffb devices failed{0}", (error.Length > 0 ? ": " + error : "."));
            return;
        }
        List<string> warnings = [];
        List<FfbTelemetryDeviceOption> devices;
        try
        {
            devices = ParseFfbTelemetryDevices(result.StandardOutput, warnings);
        }
        catch (JsonException ex)
        {
            FfbTelemetryDeviceInfo = T("The device list could not be read: {0}", ex.Message);
            return;
        }
        ApplyFfbTelemetryDevices(devices);
        FfbTelemetryDeviceInfo = (devices.Count == 0 ? T("No force feedback device found.") : T("{0} force feedback device(s).", devices.Count.ToString(CultureInfo.InvariantCulture))) +
                                 (warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty);
    }

    internal static List<FfbTelemetryDeviceOption> ParseFfbTelemetryDevices(string output, List<string> warnings)
    {
        List<FfbTelemetryDeviceOption> devices = [];
        using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
        if (TryFfbJson(doc.RootElement, "devices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement device in list.EnumerateArray())
            {
                string guid = FfbJsonString(device, "instanceGuid");
                if (guid.Length == 0)
                    continue;
                List<string> axes = TryFfbJson(device, "forceAxes", out JsonElement forceAxes) && forceAxes.ValueKind == JsonValueKind.Array
                    ? forceAxes.EnumerateArray().Select(a => a.ToString()).ToList()
                    : [];
                string name = FfbJsonString(device, "name") is { Length: > 0 } n ? n : FfbJsonString(device, "productName");
                string vid = FfbJsonString(device, "vid");
                string pid = FfbJsonString(device, "pid");
                string detail = (vid.Length > 0 ? T("VID {0} PID {1}, ", vid, pid) : string.Empty) +
                                (axes.Count > 0 ? "force axes " + string.Join(" ", axes) : T("no force axes"));
                devices.Add(new FfbTelemetryDeviceOption(guid, name.Length > 0 ? name : guid, detail, axes.Count > 0, vid, pid));
            }
        }
        if (TryFfbJson(doc.RootElement, "warnings", out JsonElement warningList) && warningList.ValueKind == JsonValueKind.Array)
            warnings.AddRange(warningList.EnumerateArray().Select(w => w.ToString()).Where(w => w.Length > 0));
        return devices;
    }

    // The default is the first device with force axes; it is saved so JoyPro.Ffb uses the same one.
    private void ApplyFfbTelemetryDevices(List<FfbTelemetryDeviceOption> devices)
    {
        string current = SelectedFfbTelemetryDevice?.InstanceGuid ?? ffbTelemetryConfig.Device;
        ffbTelemetryLoading = true;
        try
        {
            FfbTelemetryDevices.Clear();
            foreach (FfbTelemetryDeviceOption device in devices)
                FfbTelemetryDevices.Add(device);
            if (current.Length > 0 && !devices.Any(d => string.Equals(d.InstanceGuid, current, StringComparison.OrdinalIgnoreCase)))
                FfbTelemetryDevices.Add(new FfbTelemetryDeviceOption(current, ffbTelemetryConfig.DeviceName.Length > 0 ? ffbTelemetryConfig.DeviceName : current, T("not connected"), false));
            SelectedFfbTelemetryDevice = FfbTelemetryDevices.FirstOrDefault(d => string.Equals(d.InstanceGuid, current, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ffbTelemetryLoading = false;
        }
        if (SelectedFfbTelemetryDevice == null && devices.FirstOrDefault(d => d.HasForceAxes) is { } first)
            SelectedFfbTelemetryDevice = first;
    }

    // ---- Games: setup, aircraft, graph ----

    internal static string FfbTelemetrySetupId(string game) => game switch
    {
        TelemetryGame.Dcs => "dcs",
        TelemetryGame.Il2 => "il2",
        TelemetryGame.Il2Korea => "il2korea",
        TelemetryGame.WarThunder => "warthunder",
        _ => game.ToLowerInvariant()
    };

    // mode: "install", "remove" or "dry-run" (a dry run of the install).
    [RelayCommand]
    private async Task FfbTelemetrySetupAsync(string? mode)
    {
        FfbTelemetryGameItem? game = SelectedFfbTelemetryGame;
        if (game == null || string.IsNullOrWhiteSpace(mode))
            return;
        if (!game.SetupNeeded)
        {
            FfbTelemetrySetupChanges = T("Nothing to set up: {0} always offers its telemetry.", game.Title);
            return;
        }
        bool remove = string.Equals(mode, "remove", StringComparison.OrdinalIgnoreCase);
        bool dryRun = string.Equals(mode, "dry-run", StringComparison.OrdinalIgnoreCase);
        List<string> args = ["ffb", "telemetry", "setup", FfbTelemetrySetupId(game.Id)];
        if (remove)
            args.Add("--remove");
        if (dryRun)
            args.Add("--dry-run");
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false,
            busyLabel: (remove ? T("Removing") : T("Installing")) + " the " + game.Title + " telemetry export...", forceText: false);
        if (result == null)
        {
            FfbTelemetrySetupChanges = StatusText;
            return;
        }
        FfbTelemetrySetupChanges = FormatFfbTelemetrySetupResult(result, dryRun, out bool? installed);
        if (result.ExitCode == 0 && !dryRun && installed is bool now)
            game.SetupInstalled = now;
    }

    internal static string FormatFfbTelemetrySetupResult(EngineCommandResult result, bool dryRun, out bool? installed)
    {
        installed = null;
        StringBuilder text = new();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            installed = FfbJsonBool(root, "Installed");
            List<string> changes = FfbJsonStrings(root, "Changes");
            text.AppendLine(changes.Count == 0
                ? (dryRun ? "Dry run: nothing would change." : "Nothing changed.")
                : (dryRun ? "Dry run - would change:" : "Changed:"));
            foreach (string change in changes)
                text.AppendLine("  " + change);
            foreach (string warning in FfbJsonStrings(root, "Warnings"))
                text.AppendLine(T("Warning: {0}", warning));
            foreach (string error in FfbJsonStrings(root, "Errors"))
                text.AppendLine(T("Error: {0}", error));
            List<string> files = FfbJsonStrings(root, "Files");
            if (files.Count > 0)
                text.AppendLine(T("Files: {0}", string.Join(", ", files)));
        }
        catch (JsonException)
        {
            if (result.ExitCode == 0)
                text.AppendLine(result.StandardOutput.Trim());
        }
        if (result.ExitCode != 0)
            text.AppendLine(BuildFailureStatus(result));
        return text.ToString().TrimEnd();
    }

    [RelayCommand]
    private void AddFfbTelemetryAircraft()
    {
        FfbTelemetryGameItem? game = SelectedFfbTelemetryGame;
        string name = (FfbTelemetryNewAircraft ?? string.Empty).Trim();
        if (name.Length == 0)
            name = FfbTelemetryCurrentAircraft.Trim();
        if (game == null || name.Length == 0)
        {
            FfbTelemetryMessage = T("Type the aircraft name as the game reports it (it shows in the status line while flying).");
            return;
        }
        if (game.Aircraft.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            FfbTelemetryMessage = name + " already has its own settings.";
            return;
        }
        game.Aircraft.Add(CreateFfbTelemetryAircraft(game.Id, name, string.Empty, 1));
        FfbTelemetryNewAircraft = string.Empty;
        QueueFfbTelemetrySet(new FfbTelemetryScope(game.Id, name), "strength", "1");
    }

    [RelayCommand]
    private async Task RemoveFfbTelemetryAircraftAsync(FfbTelemetryAircraftItem? plane)
    {
        FfbTelemetryGameItem? game = FfbTelemetryGames.FirstOrDefault(g => plane != null && g.Aircraft.Contains(plane));
        if (game == null || plane == null)
            return;
        await FlushFfbTelemetrySettingsAsync();
        if (!await RunFfbTelemetryEngineAsync(["ffb", "telemetry", "config", "clear", "--game", game.Id, "--plane", plane.Name], "Removing aircraft settings..."))
            return;
        game.Aircraft.Remove(plane);
        FfbTelemetryMessage = T("{0} uses the game's settings again.", plane.Name);
        SendFfbHostCommand(HostProtocol.Reload);
    }

    // Opens the graph the selected game uses in the editor (the built-in one as a copy).
    [RelayCommand]
    private void EditFfbTelemetryGameGraph()
    {
        FfbGraphOption? graph = SelectedFfbTelemetryGame?.Graph;
        if (graph == null || graph.IsBuiltIn)
            FfbGraphEditor.NewFromBuiltInCommand.Execute(null);
        else
            FfbGraphEditor.LoadFile(graph.Value);
        FfbTelemetryTab = FfbTelemetryEditorTab;
    }

    private void OnFfbGraphSaved(string fileName)
    {
        RebuildFfbGraphOptions();
        if (ffbHost != null && FfbTelemetryGraphInUse(ffbHostGame, fileName))
        {
            SendFfbHostCommand(HostProtocol.Reload);
            FfbGraphEditor.Message += T(" JoyPro.Ffb reloads it.");
        }
    }

    private bool FfbTelemetryGraphInUse(string gameId, string fileName)
    {
        FfbTelemetryGameItem? game = FfbTelemetryGames.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));
        if (game == null)
            return false;
        return string.Equals(game.Graph?.Value, fileName, StringComparison.OrdinalIgnoreCase) ||
               game.Aircraft.Any(a => string.Equals(a.Graph?.Value, fileName, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Process manager ----

    private void UpdateFfbTelemetryPolling()
    {
        if (FfbTelemetryEnabled)
        {
            if (ffbTelemetryPollCts == null)
            {
                ffbTelemetryPollCts = new CancellationTokenSource();
                _ = RunFfbTelemetryPollAsync(ffbTelemetryPollCts.Token);
            }
        }
        else
        {
            if (ffbTelemetryPollCts != null)
            {
                ffbTelemetryPollCts.Cancel();
                ffbTelemetryPollCts.Dispose();
                ffbTelemetryPollCts = null;
            }
            StopFfbHost(waitForExit: false);
        }
        UpdateFfbTelemetryStatusLine();
    }

    private async Task RunFfbTelemetryPollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            PollFfbTelemetryGamesOnce();
            try
            {
                await Task.Delay(FfbTelemetryPollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One look for a running game (enabled games only, in the page's order); starts JoyPro.Ffb for the first found.
    internal void PollFfbTelemetryGamesOnce()
    {
        if (!FfbTelemetryEnabled || ffbHost != null)
            return;
        foreach (FfbTelemetryGameItem game in FfbTelemetryGames)
        {
            if (!game.Enabled || game.ProcessNames.Count == 0)
                continue;
            int? pid = FfbFindGameProcess(game.ProcessNames);
            game.Running = pid != null;
            if (pid is int found && found != ffbBlockedGamePid)
            {
                StartFfbHost(game, found);
                return;
            }
        }
        UpdateFfbTelemetryStatusLine();
    }

    private void StartFfbHost(FfbTelemetryGameItem game, int gamePid)
    {
        // A recording of this game without force feedback gives the game's port back first (this process records now).
        StopTelemetryRecordHostFor(game.Id);
        List<string> args =
        [
            "run", "--game", game.Id,
            "--game-pid", gamePid.ToString(CultureInfo.InvariantCulture),
            "--parent-pid", FfbTelemetryGuiPid.ToString(CultureInfo.InvariantCulture),
            "--protocol"
        ];
        IFfbHostProcess process;
        try
        {
            process = FfbHostLauncher.Start(FfbTelemetryExePath, args);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            ffbBlockedGamePid = gamePid;
            ffbTelemetryLastFault = T("stopped - JoyPro.Ffb could not start: {0}", ex.Message);
            AppendFfbLog(ffbTelemetryLastFault);
            UpdateFfbTelemetryStatusLine();
            return;
        }
        ffbHost = process;
        ffbHostGame = game.Id;
        ffbHostGamePid = gamePid;
        ffbHostLastReason = "starting - " + game.Title;
        ffbHostLastState = string.Empty;
        ffbHostExitReason = string.Empty;
        ffbTelemetryLastFault = string.Empty;
        ffbWatchOn = false;
        FfbTelemetryProcessRunning = true;
        process.LineReceived += line => FfbTelemetryDispatch(() => OnFfbHostLine(process, line));
        process.ErrorLineReceived += line => FfbTelemetryDispatch(() =>
        {
            if (ReferenceEquals(process, ffbHost))
                AppendFfbLog(line);
        });
        process.Exited += () => FfbTelemetryDispatch(() => OnFfbHostExited(process));
        AppendFfbLog("JoyPro.Ffb started for " + game.Title + " (game pid " + gamePid.ToString(CultureInfo.InvariantCulture) + ").");
        FfbGraphEditor.ClearLiveValues();
        UpdateFfbGraphPreview();
        RefreshFfbTelemetryKeyOptions();
        UpdateFfbTelemetryStatusLine();
        UpdateFfbTelemetryWatch();
        if (process.HasExited)
            OnFfbHostExited(process);
    }

    private bool SendFfbHostCommand(string command)
    {
        IFfbHostProcess? process = ffbHost;
        if (process == null)
            return false;
        return process.Send(command);
    }

    // "stop", then killed after 3 s if it is still there.
    private void StopFfbHost(bool waitForExit)
    {
        IFfbHostProcess? process = ffbHost;
        if (process == null)
            return;
        ffbHost = null;
        ffbWatchOn = false;
        FfbTelemetryProcessRunning = false;
        process.Send(HostProtocol.Stop);
        if (waitForExit)
            EndFfbHost(process);
        else
            _ = Task.Run(() => EndFfbHost(process));
        AppendFfbLog("JoyPro.Ffb stopped.");
        FfbGraphEditor.ClearLiveValues();
        UpdateFfbTelemetryStatusLine();
        UpdateFfbTelemetryWatch();
    }

    private static void EndFfbHost(IFfbHostProcess process)
    {
        if (!process.WaitForExit(3000))
            process.Kill();
        process.Dispose();
    }

    public void StopFfbTelemetryForShutdown()
    {
        ffbPreviewCts?.Cancel();
        ffbPreviewCts = null;
        ffbTelemetryPollCts?.Cancel();
        ffbTelemetryPollCts = null;
        StopFfbHost(waitForExit: true);
    }

    private void OnFfbHostExited(IFfbHostProcess process)
    {
        if (!ReferenceEquals(process, ffbHost))
            return;
        ffbHost = null;
        ffbWatchOn = false;
        FfbTelemetryProcessRunning = false;
        process.Dispose();
        string reason = ffbHostExitReason.Length > 0 ? ffbHostExitReason : T("the process ended");
        bool normal = reason is "game exited" or "stop requested";
        if (ffbHostLastState == "faulted")
            ffbTelemetryLastFault = ffbHostLastReason;
        else if (!normal)
            ffbTelemetryLastFault = "stopped - " + reason;
        else
            ffbTelemetryLastFault = string.Empty;
        // A failed run is not restarted for the same game process (no restart loop); a new game start or a settings
        // change tries again.
        ffbBlockedGamePid = ffbTelemetryLastFault.Length > 0 ? ffbHostGamePid : null;
        if (FfbTelemetryGames.FirstOrDefault(g => g.Id == ffbHostGame) is { } game && reason == "game exited")
            game.Running = false;
        AppendFfbLog("JoyPro.Ffb ended: " + reason + ".");
        FfbTelemetryCurrentAircraft = string.Empty;
        FfbGraphEditor.ClearLiveValues();
        UpdateFfbTelemetryStatusLine();
        UpdateFfbTelemetryWatch();
    }

    private void OnFfbHostLine(IFfbHostProcess process, string line)
    {
        if (!ReferenceEquals(process, ffbHost))
            return;
        JsonObject? json = HostProtocol.Parse(line);
        if (json == null)
        {
            if (line.Trim().Length > 0)
                AppendFfbLog(line.Trim());
            return;
        }
        switch (FfbNodeString(json, "type"))
        {
            case "status":
                ffbHostLastState = FfbNodeString(json, "state");
                ffbHostLastReason = FfbTelemetryReasonFromStatus(json);
                FfbTelemetryCurrentAircraft = FfbNodeString(json, "aircraft");
                UpdateFfbTelemetryStatusLine();
                break;
            case "telemetry":
                if (json["values"] is JsonObject telemetry)
                    OnFfbTelemetryValues(telemetry);
                break;
            case "nodes":
                if (ffbWatchOn && json["values"] is JsonObject nodes)
                {
                    ffbNodeValues.Clear();
                    ReadFfbValues(nodes, ffbNodeValues);
                    FfbGraphEditor.SetLiveValues(ffbNodeValues);
                }
                break;
            case "log":
                AppendFfbLog(FfbNodeString(json, "message"));
                break;
            case "recording":
                OnTelemetryRecordingLine(json);
                break;
            case "exit":
                ffbHostExitReason = FfbNodeString(json, "reason");
                break;
        }
    }

    // The status line of a "status" line: its reason ("active - F-16C_50", "idle - no telemetry on UDP 41992",
    // "stopped after an error: ..."), else the state.
    internal static string FfbTelemetryReasonFromStatus(JsonObject status)
    {
        string reason = FfbNodeString(status, "reason");
        if (reason.Length > 0)
            return reason;
        string state = FfbNodeString(status, "state");
        string aircraft = FfbNodeString(status, "aircraft");
        return state switch
        {
            "active" => "active" + (aircraft.Length > 0 ? " - " + aircraft : string.Empty),
            "waiting" => "idle - waiting for the game",
            "silent" => "idle - no telemetry",
            "faulted" => "stopped after an error",
            "" => "starting",
            _ => state
        };
    }

    internal void UpdateFfbTelemetryStatusLine()
    {
        if (ffbHost != null)
        {
            FfbTelemetryStatusText = ffbHostLastReason;
            return;
        }
        if (!FfbTelemetryEnabled)
        {
            FfbTelemetryStatusText = FfbTelemetryOffText;
            return;
        }
        if (ffbTelemetryLastFault.Length > 0)
        {
            FfbTelemetryStatusText = ffbTelemetryLastFault;
            return;
        }
        List<string> names = FfbTelemetryGames.Where(g => g.Enabled).SelectMany(g => g.ProcessNames).Select(n => n + ".exe").ToList();
        FfbTelemetryStatusText = names.Count == 0 ? T("idle - every game is switched off") : T("idle - waiting for {0}", string.Join(" / ", names));
    }

    private void AppendFfbLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        FfbTelemetryLog.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        while (FfbTelemetryLog.Count > FfbTelemetryLogLines)
            FfbTelemetryLog.RemoveAt(0);
    }

    // ---- Telemetry inspector and key picker ----

    private void OnFfbTelemetryValues(JsonObject values)
    {
        ffbLatestTelemetry.Clear();
        ReadFfbValues(values, ffbLatestTelemetry);
        foreach (string key in ffbLatestTelemetry.Keys)
        {
            if (ffbSeenKeys.Add(key))
                ffbKeyOptionsDirty = true;
        }
        DateTime now = DateTime.UtcNow;
        if ((now - ffbInspectorUpdated).TotalMilliseconds < 200)
            return;
        ffbInspectorUpdated = now;
        if (ffbKeyOptionsDirty)
            RefreshFfbTelemetryKeyOptions();
        if (FfbTelemetryPageVisible && FfbTelemetryTab == FfbTelemetryInspectorTab)
            UpdateFfbTelemetryFields(rebuild: false);
    }

    private static void ReadFfbValues(JsonObject values, Dictionary<string, double> into)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in values)
        {
            if (pair.Value is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number))
                into[pair.Key] = number;
        }
    }

    partial void OnFfbTelemetryFieldFilterChanged(string value) => UpdateFfbTelemetryFields(rebuild: true);

    // Updates the values in place; the list itself is rebuilt only when keys appear or the filter changes.
    internal void UpdateFfbTelemetryFields(bool rebuild)
    {
        foreach (KeyValuePair<string, double> pair in ffbLatestTelemetry)
        {
            if (!ffbFieldItems.TryGetValue(pair.Key, out FfbTelemetryFieldItem? item))
            {
                TelemetryDescriptor? descriptor = ffbDescriptors.FirstOrDefault(d => d.Key == pair.Key);
                item = new FfbTelemetryFieldItem(pair.Key, descriptor?.Title ?? string.Empty, descriptor?.Unit ?? string.Empty);
                ffbFieldItems[pair.Key] = item;
                rebuild = true;
            }
            item.Value = pair.Value;
        }
        if (!rebuild)
            return;
        string filter = (FfbTelemetryFieldFilter ?? string.Empty).Trim();
        List<FfbTelemetryFieldItem> shown = ffbFieldItems.Values
            .Where(i => filter.Length == 0 || i.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Key.StartsWith(AircraftTelemetry.Prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(i => i.Key, StringComparer.Ordinal)
            .ToList();
        FfbTelemetryFields.Clear();
        foreach (FfbTelemetryFieldItem item in shown)
            FfbTelemetryFields.Add(item);
        FfbTelemetryFieldCount = ffbFieldItems.Count == 0
            ? T("No telemetry yet: it shows while JoyPro.Ffb runs for a game.")
            : T("{0} of {1} fields", shown.Count.ToString(CultureInfo.InvariantCulture), ffbFieldItems.Count.ToString(CultureInfo.InvariantCulture));
    }

    // The keys the Telemetry node offers: normalised ac.* keys, the current game's raw keys, keys seen live.
    private void RefreshFfbTelemetryKeyOptions()
    {
        string game = ffbHost != null ? ffbHostGame : SelectedFfbTelemetryGame?.Id ?? TelemetryGame.Dcs;
        if (!ffbKeyOptionsDirty && game == ffbKeyOptionsGame)
            return;
        ffbKeyOptionsDirty = false;
        ffbKeyOptionsGame = game;
        ffbDescriptors = FfbTelemetryDescriptors(game);
        IReadOnlyList<TelemetryDescriptor> descriptors = ffbDescriptors;
        FfbGraphEditor.DescribeKey = key => descriptors.FirstOrDefault(d => d.Key == key);
        List<string> keys = descriptors.Select(d => d.Key)
            .Concat(ffbSeenKeys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k.StartsWith(AircraftTelemetry.Prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (keys.SequenceEqual(FfbGraphEditor.TelemetryKeyOptions))
            return;
        FfbGraphEditor.TelemetryKeyOptions.Clear();
        foreach (string key in keys)
            FfbGraphEditor.TelemetryKeyOptions.Add(key);
    }

    internal static IReadOnlyList<TelemetryDescriptor> FfbTelemetryDescriptors(string game) => game switch
    {
        TelemetryGame.Dcs => [.. AircraftTelemetry.Descriptors, .. DcsTelemetryProtocol.Descriptors],
        TelemetryGame.Il2 or TelemetryGame.Il2Korea => [.. AircraftTelemetry.Descriptors, .. Il2TelemetrySource.Catalogue],
        _ => AircraftTelemetry.Descriptors
    };

    // ---- JSON helpers (the engine writes PascalCase, JoyPro.Ffb camelCase: names are matched ignoring case) ----

    private static bool TryFfbJson(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string FfbJsonString(JsonElement element, string name) =>
        TryFfbJson(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool? FfbJsonBool(JsonElement element, string name) =>
        TryFfbJson(element, name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static List<string> FfbJsonStrings(JsonElement element, string name) =>
        TryFfbJson(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString()).Where(v => v.Length > 0).ToList()
            : [];

    private static string FfbNodeString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;
}
