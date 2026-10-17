using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The "Virtual joystick (vJoy + HidHide)" page (docs/virtual-joystick.md). Per game the user picks the physical
// devices (native) or the virtual joystick: JoyPro gives every input the game's relations use a fixed slot on a vJoy
// device, the export writes the binds there, JoyPro.VJoy.exe (gui/JoyPro.VJoy) forwards the physical inputs live and
// HidHide hides the physical devices from those games only.
//
// Everything that changes the PC goes through the engine, with one Windows administrator prompt each:
//   Start:  engine vjoy map -> forwarder-config -> apply-hidhide (prompt) -> JoyPro.VJoy run --sink vjoy
//   Stop:   "stop" to JoyPro.VJoy -> engine vjoy restore-hidhide (prompt)
//   Test:   JoyPro.VJoy run --sink dry (reads the sticks, writes nothing, no HidHide) for the live view
// The HidHide session journal (shared engine code, engine/VJoy/HidHideSession.cs) is read at start: a session whose
// owner (an earlier JoyPro) is gone is restored at once (engine vjoy recover), so devices never stay hidden.
// Auto start (off by default): only while it is on and a game uses virtual mode, the games' processes are looked up
// every 3 s; nothing polls otherwise.
public sealed partial class MainWindowViewModel
{
    private const string VirtualJoystickIdleText = "Not running.";

    public ObservableCollection<VirtualJoystickGameItem> VirtualJoystickGames { get; } = [];

    public ObservableCollection<VirtualJoystickSlotItem> VirtualJoystickSlots { get; } = [];

    public ObservableCollection<VirtualJoystickLiveDevice> VirtualJoystickLive { get; } = [];

    public ObservableCollection<string> VirtualJoystickLog { get; } = [];

    [ObservableProperty]
    private string vJoyStatusText = "Press Refresh to check vJoy and HidHide.";

    [ObservableProperty]
    private string vJoyDevicesText = string.Empty;

    [ObservableProperty]
    private string hidHideStatusText = string.Empty;

    [ObservableProperty]
    private string virtualJoystickSessionText = "No HidHide session.";

    [ObservableProperty]
    private string virtualJoystickNeedsText = string.Empty;

    [ObservableProperty]
    private bool virtualJoystickNeedsConfig;

    [ObservableProperty]
    private string virtualJoystickMessage = string.Empty;

    [ObservableProperty]
    private string virtualJoystickForwarderText = VirtualJoystickIdleText;

    [ObservableProperty]
    private string virtualJoystickStatsText = string.Empty;

    [ObservableProperty]
    private bool virtualJoystickRunning;

    [ObservableProperty]
    private bool virtualJoystickTestMode;

    [ObservableProperty]
    private bool virtualJoystickHideDevices = true;

    [ObservableProperty]
    private bool virtualJoystickAutoStart;

    [ObservableProperty]
    private bool virtualJoystickLiveView = true;

    [ObservableProperty]
    private bool virtualJoystickSessionActive;

    [ObservableProperty]
    private bool virtualJoystickSessionStale;

    [ObservableProperty]
    private int virtualJoystickRefreshRate = 60;

    [ObservableProperty]
    private bool vJoyInstalled;

    [ObservableProperty]
    private bool viGEmInstalled;

    [ObservableProperty]
    private string viGEmStatusText = string.Empty;

    public bool HasXboxGames => VirtualJoystickGames.Any(g => g.IsXbox);

    [ObservableProperty]
    private bool hidHideInstalled;

    public bool IsVirtualJoystickStage => CurrentStage == UiStage.VirtualJoystick;

    public bool HasVirtualJoystickGames => VirtualJoystickGames.Any(g => g.IsVirtual);

    public bool CanStartVirtualJoystick => !VirtualJoystickRunning && HasVirtualJoystickGames && (VirtualJoystickSlots.Count > 0 || VirtualFunctions.Any(f => f.Enabled));

    public string VirtualJoystickSlotsHeader => VirtualJoystickSlots.Count == 0
        ? T("No slots yet: switch a game to virtual (above) and bind its relations to your devices.")
        : T("{0} input(s) go to vJoy", VirtualJoystickSlots.Count.ToString(CultureInfo.InvariantCulture));

    public string VirtualJoystickRateText => T("Forwarding rate: {0} Hz (your fastest display; inputs that change go out at once).", VirtualJoystickRefreshRate.ToString(CultureInfo.InvariantCulture));

    // Test seams (defaults from VirtualJoystickServices, which the tests replace).
    internal IFfbHostLauncher VirtualJoystickLauncher { get; set; } = VirtualJoystickServices.Launcher;

    internal Func<IReadOnlyList<string>, int?> VirtualJoystickFindGame { get; set; } = VirtualJoystickServices.FindGameProcess;

    internal Func<int, string?> VirtualJoystickProcessPath { get; set; } = VirtualJoystickServices.ProcessPath;

    internal string VirtualJoystickExePath { get; set; } = VirtualJoystickServices.ExePath();

    internal string VirtualJoystickDataFolder { get; set; } = VirtualJoystickServices.DataFolder();

    internal Action<Action> VirtualJoystickDispatch { get; set; } = DispatchFfbToUi;

    internal TimeSpan VirtualJoystickPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    internal int VirtualJoystickGuiPid { get; set; } = Environment.ProcessId;

    internal string VirtualJoystickGuiExe { get; set; } = Environment.ProcessPath ?? string.Empty;

    // Is the process (pid, start time in UTC ticks) still the owner of a HidHide session.
    internal Func<int, long, bool> VirtualJoystickOwnerAlive { get; set; } = IsProcessAlive;

    internal bool VirtualJoystickPolling => virtualJoystickPollCts != null;

    internal IFfbHostProcess? VirtualJoystickProcess => vjoyHost;

    private readonly Dictionary<string, List<string>> virtualJoystickProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> virtualJoystickModes = new(StringComparer.OrdinalIgnoreCase);
    private IFfbHostProcess? vjoyHost;
    private CancellationTokenSource? virtualJoystickPollCts;
    private bool virtualJoystickLoading;
    private bool virtualJoystickStopping;
    private bool virtualJoystickHidHideApplied;
    private int? virtualJoystickAutoGamePid;
    private string virtualJoystickConfigFile = string.Empty;

    // Called by the constructor; in the real app (not the tests) a session an earlier JoyPro left behind is restored.
    private void InitializeVirtualJoystick(bool realApp)
    {
        VirtualJoystickGames.CollectionChanged += (_, _) => NotifyVirtualJoystickState();
        VirtualJoystickSlots.CollectionChanged += (_, _) => NotifyVirtualJoystickState();
        VirtualFunctions.CollectionChanged += (_, _) => NotifyVirtualJoystickState();
        if (realApp)
            _ = RecoverStaleVirtualJoystickSessionAsync();
    }

    [RelayCommand]
    private void GoVirtualJoystick() => CurrentStage = UiStage.VirtualJoystick;

    private void OnVirtualJoystickStageChanged(bool visible)
    {
        if (visible && VirtualJoystickGames.Count == 0 && !IsBusy)
            _ = RefreshVirtualJoystickAsync();
    }

    private void NotifyVirtualJoystickState()
    {
        OnPropertyChanged(nameof(HasVirtualJoystickGames));
        OnPropertyChanged(nameof(HasXboxGames));
        OnPropertyChanged(nameof(CanStartVirtualJoystick));
        OnPropertyChanged(nameof(VirtualJoystickSlotsHeader));
    }

    partial void OnVirtualJoystickRunningChanged(bool value) => NotifyVirtualJoystickState();

    partial void OnVirtualJoystickRefreshRateChanged(int value) => OnPropertyChanged(nameof(VirtualJoystickRateText));

    partial void OnVirtualJoystickHideDevicesChanged(bool value)
    {
        if (!virtualJoystickLoading)
            _ = SaveVirtualJoystickSettingAsync("--hide-devices", value);
    }

    partial void OnVirtualJoystickAutoStartChanged(bool value)
    {
        if (!virtualJoystickLoading)
            _ = SaveVirtualJoystickSettingAsync("--auto-start", value);
        UpdateVirtualJoystickPolling();
    }

    partial void OnVirtualJoystickLiveViewChanged(bool value) => vjoyHost?.Send(value ? T("monitor on") : "monitor off");

    // ---- Status

    [RelayCommand]
    private async Task RefreshVirtualJoystickAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "status"], includeProfile: !string.IsNullOrWhiteSpace(ProfilePath), mutatesProfile: false,
            busyLabel: N("Checking vJoy and HidHide..."), forceText: false);
        if (result != null && result.ExitCode == 0)
            ApplyVirtualJoystickStatus(result.StandardOutput);
    }

    internal void ApplyVirtualJoystickStatus(string output)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ExtractJsonPayload(output));
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            virtualJoystickLoading = true;
            try
            {
                if (root.TryGetProperty("VJoy", out JsonElement vjoy))
                {
                    VJoyInstalled = ReadJsonBool(vjoy, "Installed");
                    VJoyStatusText = VJoyInstalled
                        ? T("vJoy {0} is installed.{1}", ReadJsonString(vjoy, "Version"), (ReadJsonBool(vjoy, "Fake") ? T(" (test mode)") : string.Empty))
                        : T("vJoy is not installed. Install it under \"What this page needs\" below (free); JoyPro then sets up its devices for you.");
                    List<string> devices = [];
                    if (vjoy.TryGetProperty("Devices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement device in list.EnumerateArray())
                        {
                            string name = ReadJsonString(device, "DirectInputName");
                            devices.Add(T("vJoy device {0}: {1}{2}", ReadJsonInt(device, "Id"), ReadJsonString(device, "Describe"), (name.Length == 0 ? " (games do not see it yet)" : string.Empty)));
                        }
                    }
                    VJoyDevicesText = devices.Count == 0 ? T("No vJoy device is configured yet.") : string.Join(Environment.NewLine, devices);
                }

                if (root.TryGetProperty("HidHide", out JsonElement hid))
                {
                    HidHideInstalled = ReadJsonBool(hid, "Installed");
                    // Parts HidHide did not tell without administrator rights are shown as unknown, never as off/empty.
                    List<string> unreadable = ReadStringArray(hid, "Unreadable");
                    HidHideStatusText = HidHideInstalled
                        ? T("HidHide {0} is installed; device hiding is {1} ({2}, {3}, {4}).", ReadJsonString(hid, "Version"), (unreadable.Contains("cloaking state") ? "unknown" : ReadJsonBool(hid, "CloakOn") ? "on" : "off"), (unreadable.Contains("inverse mode") ? "list mode unknown" : ReadJsonBool(hid, "InverseOn") ? "only listed programs lose hidden devices" : "only listed programs see hidden devices"), (unreadable.Contains("hidden devices") ? T("hidden devices: only readable with administrator rights (JoyPro reads them when you press Start)") : T("{0} hidden device(s)", ReadStringArray(hid, "Devices").Count)), (unreadable.Contains("program list") ? "programs unknown" : T("{0} program(s)", ReadStringArray(hid, "Apps").Count)))
                        : T("HidHide is not installed. Without it the games see your physical devices and the virtual one; install HidHide (free) under \"What this page needs\" below to hide them.");
                }

                if (root.TryGetProperty("Session", out JsonElement session))
                {
                    string phase = ReadJsonString(session, "Phase");
                    VirtualJoystickSessionActive = phase is "active" or "applying";
                    VirtualJoystickSessionStale = ReadJsonBool(session, "Stale");
                    VirtualJoystickSessionText = !VirtualJoystickSessionActive
                        ? T("No HidHide session: HidHide is as you set it up.")
                        : T("{0}{1} device(s) hidden from {2} ({3}).", (VirtualJoystickSessionStale ? T("A session of an earlier JoyPro is still active (it did not end cleanly): press Restore HidHide. ") : T("JoyPro's HidHide session is active: ")), ReadStringArray(session, "AddedDevices").Count, string.Join(", ", ReadStringArray(session, "Games")), ReadJsonString(session, "Strategy"));
                }

                if (root.TryGetProperty("ViGEm", out JsonElement vigem))
                {
                    ViGEmInstalled = ReadJsonBool(vigem, "Installed");
                    ViGEmStatusText = ViGEmInstalled
                        ? T("ViGEmBus {0} is installed{1}: the virtual Xbox pad works.", ReadJsonString(vigem, "Version"), (ReadJsonBool(vigem, "Running") ? "" : T(" but not running (restart Windows)")))
                        : T("ViGEmBus is not installed. Games set to \"Xbox pad\" need it (free driver by Nefarius): install it under \"What this page needs\" below, then press Refresh.");
                }

                if (root.TryGetProperty("Settings", out JsonElement settings))
                {
                    VirtualJoystickHideDevices = ReadJsonBool(settings, "HideDevices");
                    VirtualJoystickAutoStart = ReadJsonBool(settings, "AutoStart");
                }

                if (root.TryGetProperty("RefreshRateHz", out JsonElement rate) && rate.TryGetInt32(out int hz))
                    VirtualJoystickRefreshRate = hz;

                ApplyVirtualJoystickGames(root);
                ApplyVirtualJoystickSlots(root);
                ApplyVirtualJoystickNeeds(root);
                if (root.TryGetProperty("VJoy", out JsonElement vjoyDevices))
                    ReadVJoyDirectInputNames(vjoyDevices);
                ApplyVirtualFunctionsStatus(root);
                // Problems of the table (no free slot left) show; the message of the user's last action stays otherwise.
                List<string> errors = ReadStringArray(root, "Errors");
                if (errors.Count > 0)
                    VirtualJoystickMessage = string.Join(" ", errors);
            }
            finally
            {
                virtualJoystickLoading = false;
            }
        }

        NotifyVirtualJoystickState();
        UpdateVirtualJoystickPolling();
    }

    private void ApplyVirtualJoystickGames(JsonElement root)
    {
        if (!root.TryGetProperty("Games", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
            return;
        List<VirtualJoystickGameItem> items = [];
        virtualJoystickProcessNames.Clear();
        foreach (JsonElement game in games.EnumerateArray())
        {
            string id = ReadJsonString(game, "Id");
            if (id.Length == 0 || IsGameHidden(id))
                continue;
            string mode = ReadJsonString(game, "Mode");
            bool isVirtual = mode is VirtualJoystickGameItem.ModeVJoy or VirtualJoystickGameItem.ModeXbox;
            int relations = ReadJsonInt(game, "Relations");
            virtualJoystickProcessNames[id] = ReadStringArray(game, "ProcessNames");
            // The games the profile uses, and every game already in virtual mode.
            if (relations == 0 && !isVirtual)
                continue;
            virtualJoystickModes[id] = isVirtual ? mode : VirtualJoystickGameItem.ModeNative;
            items.Add(new VirtualJoystickGameItem(id, ReadJsonString(game, "Name"), relations, isVirtual ? mode : VirtualJoystickGameItem.ModeNative, ReadStringArray(game, "Exes"),
                OnVirtualJoystickModeToggled, ReadJsonInt(game, "GamepadLayout"), ReadJsonBool(game, "WritesGamepadBinds"),
                ReadJsonString(game, "SuggestedMode"), ReadJsonString(game, "DeviceLimitNote")));
        }

        VirtualJoystickGames.Clear();
        foreach (VirtualJoystickGameItem item in items)
            VirtualJoystickGames.Add(item);
    }

    private void ApplyVirtualJoystickSlots(JsonElement root)
    {
        VirtualJoystickSlots.Clear();
        if (!root.TryGetProperty("Mapping", out JsonElement mapping) || mapping.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement slot in mapping.EnumerateArray())
        {
            VirtualJoystickSlots.Add(new VirtualJoystickSlotItem(ReadJsonString(slot, "Source"), ReadJsonString(slot, "SourceName"), ReadJsonString(slot, "Input"),
                ReadJsonInt(slot, "Device"), ReadJsonString(slot, "Target"), ReadJsonBool(slot, "Connected"), ReadJsonBool(slot, "Used"), ReadJsonBool(slot, "New"),
                ReadStringArray(slot, "Relations")));
        }

        if (root.TryGetProperty("XboxMapping", out JsonElement xbox) && xbox.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement slot in xbox.EnumerateArray())
            {
                VirtualJoystickSlots.Add(new VirtualJoystickSlotItem(ReadJsonString(slot, "Source"), ReadJsonString(slot, "SourceName"), ReadJsonString(slot, "Input"),
                    0, ReadJsonString(slot, "Target"), ReadJsonBool(slot, "Connected"), ReadJsonBool(slot, "Used"), ReadJsonBool(slot, "New"),
                    ReadStringArray(slot, "Relations"), ReadJsonString(slot, "Game"), ReadJsonString(slot, "TargetName"), ReadJsonBool(slot, "Pinned")));
            }
        }
    }

    private void ApplyVirtualJoystickNeeds(JsonElement root)
    {
        List<string> lines = [];
        bool needsConfig = false;
        if (root.TryGetProperty("Needs", out JsonElement needs) && needs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement need in needs.EnumerateArray())
            {
                string id = ReadJsonInt(need, "Id").ToString(CultureInfo.InvariantCulture);
                if (ReadJsonBool(need, "Sufficient"))
                {
                    lines.Add(T("vJoy device {0} offers everything the table needs ({1}).", id, ReadJsonString(need, "Current")));
                    continue;
                }

                needsConfig = true;
                lines.Add(T("vJoy device {0} needs: {1}. JoyPro sets it to: {2}.", id, string.Join(", ", ReadStringArray(need, "Missing")), ReadJsonString(need, "Recommended")));
            }
        }

        if (root.TryGetProperty("XboxGames", out JsonElement xboxGames) && xboxGames.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement game in xboxGames.EnumerateArray())
            {
                List<string> unfit = ReadStringArray(game, "Unfit");
                lines.Add(T("Xbox pad for {0}: {1} input(s) placed{2}", ReadJsonString(game, "Game"), ReadJsonInt(game, "Slots"), (unfit.Count == 0 ? ", everything fits." : "; does not fit: " + string.Join(" ", unfit))));
                foreach (string note in ReadStringArray(game, "Notes"))
                    lines.Add("  " + note);
            }
        }

        VirtualJoystickNeedsConfig = needsConfig;
        VirtualJoystickNeedsText = string.Join(Environment.NewLine, lines);
    }

    // ---- Per game: native / virtual

    private void OnVirtualJoystickModeToggled(VirtualJoystickGameItem item, string mode) => _ = SetVirtualJoystickModeAsync(item, mode);

    internal async Task SetVirtualJoystickModeAsync(VirtualJoystickGameItem item, string mode)
    {
        string previous = virtualJoystickModes.TryGetValue(item.Id, out string? known) ? known : VirtualJoystickGameItem.ModeNative;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "mode", item.Id, mode], includeProfile: true, mutatesProfile: true,
            busyLabel: item.Name + (mode == VirtualJoystickGameItem.ModeNative ? T(": physical devices...") : mode == VirtualJoystickGameItem.ModeXbox ? T(": virtual Xbox pad...") : T(": virtual joystick...")), forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            item.SetQuietly(previous);
            VirtualJoystickMessage = T("Could not switch {0}: {1} {2}", item.Name, FirstLine(result?.StandardError), FirstLine(result?.StandardOutput));
            return;
        }

        virtualJoystickModes[item.Id] = mode;
        VirtualJoystickMessage = mode == VirtualJoystickGameItem.ModeVJoy
            ? T("{0} now uses the virtual joystick. Export it again (Export page) so the game gets the vJoy binds.", item.Name)
            : mode == VirtualJoystickGameItem.ModeXbox
                ? T("{0} now uses the virtual Xbox 360 pad (needs the ViGEmBus driver). {1}", item.Name, item.XboxNote)
                : T("{0} uses your physical devices again. Export it again so the game gets the binds on them.", item.Name);
        await RefreshVirtualJoystickAsync();
    }

    [RelayCommand]
    private async Task UpdateVirtualJoystickMappingAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "map"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Updating the vJoy mapping..."), forceText: false);
        if (result != null)
            VirtualJoystickMessage = result.ExitCode == 0 ? ReadMessage(result.StandardOutput, "The mapping is saved in the profile.") : T("The mapping could not be updated: {0}", FirstLine(result.StandardError));
        await RefreshVirtualJoystickAsync();
    }

    [RelayCommand]
    private async Task ConfigureVJoyAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "configure-vjoy"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Configuring vJoy (Windows asks for administrator rights)..."), forceText: false);
        if (result != null)
            VirtualJoystickMessage = ReadMessage(result.StandardOutput, FirstLine(result.StandardError));
        await RefreshVirtualJoystickAsync();
    }

    private async Task SaveVirtualJoystickSettingAsync(string option, bool value)
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
            return;
        await RunEngineAsync(["vjoy", "settings", option, value ? "on" : "off"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Saving the virtual joystick setting..."), forceText: false);
    }

    // ---- Start / stop

    [RelayCommand]
    private Task StartVirtualJoystickAsync() => StartVirtualJoystickCoreAsync(test: false, gamePid: null);

    // Reads the sticks and shows what vJoy would get; writes nothing to vJoy and does not touch HidHide.
    [RelayCommand]
    private Task TestVirtualJoystickAsync() => StartVirtualJoystickCoreAsync(test: true, gamePid: null);

    [RelayCommand]
    private Task StopVirtualJoystickAsync() => StopVirtualJoystickCoreAsync("stopped");

    internal async Task<bool> StartVirtualJoystickCoreAsync(bool test, int? gamePid)
    {
        if (vjoyHost != null)
            return true;
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            VirtualJoystickMessage = T("Open a profile first.");
            return false;
        }

        EngineCommandResult? map = await RunEngineAsync(["vjoy", "map"], includeProfile: true, mutatesProfile: true, busyLabel: N("Updating the vJoy mapping..."), forceText: false);
        if (map == null || map.ExitCode != 0)
        {
            VirtualJoystickMessage = T("The mapping is not ready: {0} {1}", FirstLine(map?.StandardError), FirstLine(map?.StandardOutput));
            return false;
        }

        Directory.CreateDirectory(VirtualJoystickDataFolder);
        virtualJoystickConfigFile = Path.Combine(VirtualJoystickDataFolder, "forwarder.json");
        // One virtual Xbox pad: the table of the Xbox game that runs (auto start), else the engine takes the first.
        List<string> configArgs = ["vjoy", "forwarder-config", "--out", virtualJoystickConfigFile];
        bool xbox = VirtualJoystickGames.Any(g => g.IsXbox);
        if (gamePid is int runningPid && VirtualJoystickGames.FirstOrDefault(g => g.IsXbox && virtualJoystickProcessNames.TryGetValue(g.Id, out List<string>? names) && VirtualJoystickFindGame(names) == runningPid) is { } runningXbox)
            configArgs.AddRange(["--xbox-game", runningXbox.Id]);
        // The virtual functions of the game that started (auto start); started by hand: of every virtual game.
        if (gamePid is int gameRunning && VirtualJoystickGames.FirstOrDefault(g => g.IsVirtual && virtualJoystickProcessNames.TryGetValue(g.Id, out List<string>? gameNames) && VirtualJoystickFindGame(gameNames) == gameRunning) is { } runningGame)
            configArgs.AddRange(["--game", runningGame.Id]);
        if (xbox && !test && !ViGEmInstalled)
        {
            VirtualJoystickMessage = T("A game is set to \"Xbox pad\", but the ViGEmBus driver is not installed: press \"Get ViGEmBus\", install it and press Refresh (or use Test, which needs no driver).");
            return false;
        }
        EngineCommandResult? config = await RunEngineAsync(configArgs, includeProfile: true, mutatesProfile: false,
            busyLabel: N("Writing the forwarding table..."), forceText: false);
        if (config == null || config.ExitCode != 0)
        {
            VirtualJoystickMessage = T("The forwarding table could not be written: {0} {1}", FirstLine(config?.StandardError), FirstLine(config?.StandardOutput));
            return false;
        }

        if (!test && VirtualJoystickHideDevices)
        {
            List<string> args = ["vjoy", "apply-hidhide", "--owner-pid", VirtualJoystickGuiPid.ToString(CultureInfo.InvariantCulture)];
            if (VirtualJoystickGuiExe.Length > 0)
                args.AddRange(["--trusted", VirtualJoystickGuiExe]);
            args.AddRange(["--trusted", VirtualJoystickExePath]);
            if (gamePid is int pid && VirtualJoystickProcessPath(pid) is { Length: > 0 } gameExe)
                args.AddRange(["--exe", gameExe]);
            EngineCommandResult? hide = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
                busyLabel: N("Hiding the physical devices from the games (Windows asks for administrator rights)..."), forceText: false);
            if (hide == null)
                return false;
            if (hide.ExitCode != 0)
            {
                VirtualJoystickMessage = ReadMessage(hide.StandardOutput, FirstLine(hide.StandardError)) +
                                         (hide.ExitCode == 2 ? T(" Nothing was started; press Start again and allow the prompt, or switch off \"Hide the physical devices\".") : string.Empty);
                await RefreshVirtualJoystickAsync();
                return false;
            }

            virtualJoystickHidHideApplied = true;
        }

        if (!File.Exists(VirtualJoystickExePath))
        {
            VirtualJoystickMessage = T("JoyPro.VJoy.exe is missing (expected next to JoyPro in vjoy\\): {0}", VirtualJoystickExePath);
            await RestoreVirtualJoystickHidHideAsync();
            return false;
        }

        List<string> run = ["run", "--config", virtualJoystickConfigFile, "--sink", test ? "dry" : "vjoy", "--parent-pid", VirtualJoystickGuiPid.ToString(CultureInfo.InvariantCulture), "--protocol"];
        if (xbox)
            run.AddRange(["--xbox-sink", test ? "dry" : "vigem"]);
        if (VirtualJoystickLiveView)
            run.Add("--monitor");
        try
        {
            IFfbHostProcess host = VirtualJoystickLauncher.Start(VirtualJoystickExePath, run);
            vjoyHost = host;
            host.LineReceived += line => VirtualJoystickDispatch(() => OnVirtualJoystickLine(line));
            host.ErrorLineReceived += line => VirtualJoystickDispatch(() => AddVirtualJoystickLog("stderr: " + line));
            host.Exited += () => VirtualJoystickDispatch(() => _ = OnVirtualJoystickExitedAsync(host));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            vjoyHost = null;
            VirtualJoystickMessage = T("JoyPro.VJoy could not be started: {0}", ex.Message);
            await RestoreVirtualJoystickHidHideAsync();
            return false;
        }

        virtualJoystickAutoGamePid = gamePid;
        virtualJoystickStopping = false;
        VirtualJoystickRunning = true;
        // It may have ended before the handlers were attached (vJoy busy, bad table): same as an end by itself.
        if (vjoyHost is { HasExited: true } ended)
        {
            await OnVirtualJoystickExitedAsync(ended);
            return false;
        }

        VirtualJoystickTestMode = test;
        VirtualJoystickForwarderText = test ? T("Test: reading your devices, nothing goes to vJoy.") : T("Starting...");
        VirtualJoystickMessage = test
            ? T("Test mode: move your sticks and press buttons - the live view shows what the virtual joystick would get. vJoy and HidHide are not touched.")
            : T("Virtual joystick on: start the game now (or keep it running). Stop switches everything back.");
        await RefreshVirtualJoystickAsync();
        return true;
    }

    internal async Task StopVirtualJoystickCoreAsync(string reason)
    {
        IFfbHostProcess? host = vjoyHost;
        if (host != null)
        {
            virtualJoystickStopping = true;
            host.Send("stop");
            await Task.Run(() =>
            {
                if (!host.WaitForExit(3000))
                    host.Kill();
            });
            vjoyHost = null;
            host.Dispose();
        }

        VirtualJoystickRunning = false;
        VirtualJoystickTestMode = false;
        virtualJoystickAutoGamePid = null;
        VirtualJoystickForwarderText = VirtualJoystickIdleText;
        VirtualJoystickLive.Clear();
        await RestoreVirtualJoystickHidHideAsync();
        VirtualJoystickMessage = reason == "game exited"
            ? T("The game ended: forwarding stopped and HidHide has its previous setup again.")
            : T("Stopped. HidHide has its previous setup again.");
        await RefreshVirtualJoystickAsync();
    }

    private async Task OnVirtualJoystickExitedAsync(IFfbHostProcess host)
    {
        if (!ReferenceEquals(vjoyHost, host) || virtualJoystickStopping)
            return;
        // Ended by itself (error, vJoy busy, crash): nothing may stay hidden.
        vjoyHost = null;
        host.Dispose();
        VirtualJoystickRunning = false;
        VirtualJoystickTestMode = false;
        VirtualJoystickForwarderText = VirtualJoystickIdleText;
        await RestoreVirtualJoystickHidHideAsync();
        if (VirtualJoystickMessage.Length == 0 || !VirtualJoystickMessage.StartsWith("JoyPro.VJoy", StringComparison.Ordinal))
            VirtualJoystickMessage = T("JoyPro.VJoy ended by itself (see the log); HidHide has its previous setup again.");
    }

    [RelayCommand]
    private async Task RestoreHidHideAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "restore-hidhide"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Restoring HidHide (Windows asks for administrator rights)..."), forceText: false);
        if (result != null)
        {
            VirtualJoystickMessage = ReadMessage(result.StandardOutput, FirstLine(result.StandardError));
            if (result.ExitCode == 0)
                virtualJoystickHidHideApplied = false;
        }

        await RefreshVirtualJoystickAsync();
    }

    private async Task RestoreVirtualJoystickHidHideAsync()
    {
        if (!virtualJoystickHidHideApplied)
            return;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "restore-hidhide"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Restoring HidHide (Windows asks for administrator rights)..."), forceText: false);
        if (result != null && result.ExitCode == 0)
            virtualJoystickHidHideApplied = false;
        else if (result != null)
            VirtualJoystickMessage = T("HidHide was not restored yet: {0} Press Restore HidHide.", ReadMessage(result.StandardOutput, FirstLine(result.StandardError)));
    }

    // At start: a session whose owner (an earlier JoyPro) is gone is restored, so no device stays hidden after a crash.
    internal async Task<bool> RecoverStaleVirtualJoystickSessionAsync()
    {
        HidHideJournal? journal;
        try
        {
            string path = Path.Combine(VirtualJoystickDataFolder, "hidhide-session.txt");
            journal = File.Exists(path) ? HidHideJournal.Parse(await File.ReadAllTextAsync(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (journal == null)
            return false;
        bool stale = journal.OwnerPid == 0 ? journal.Phase != HidHideJournal.PhaseActive : !VirtualJoystickOwnerAlive(journal.OwnerPid, journal.OwnerStartTicks);
        if (!stale)
            return false;
        VirtualJoystickSessionStale = true;
        VirtualJoystickMessage = T("JoyPro did not end cleanly last time and HidHide still hid devices for virtual joystick mode. Restoring HidHide (Windows asks for administrator rights)...");
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "recover"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Restoring HidHide after an unclean end..."), forceText: false);
        bool ok = result != null && result.ExitCode == 0;
        VirtualJoystickSessionStale = !ok;
        VirtualJoystickMessage = ok
            ? T("HidHide had a session of an earlier JoyPro that did not end cleanly; it has its previous setup again.")
            : T("HidHide still has a session of an earlier JoyPro: open Virtual joystick and press Restore HidHide.");
        return ok;
    }

    // ---- Forwarder output

    private void OnVirtualJoystickLine(string line)
    {
        if (line.StartsWith("out ", StringComparison.Ordinal))
        {
            ApplyVirtualJoystickOut(line);
            return;
        }

        if (line.StartsWith("xout ", StringComparison.Ordinal))
        {
            ApplyVirtualJoystickXboxOut(line);
            return;
        }

        if (line.StartsWith("fn ", StringComparison.Ordinal))
        {
            ApplyVirtualFunctionLive(line);
            return;
        }

        if (line.StartsWith("stats ", StringComparison.Ordinal))
        {
            VirtualJoystickStatsText = DescribeVirtualJoystickStats(line);
            return;
        }

        if (line.StartsWith("ready ", StringComparison.Ordinal))
            VirtualJoystickForwarderText = (VirtualJoystickTestMode ? T("Test running: ") : T("Running: ")) + DescribeVirtualJoystickReady(line);
        else if (line.StartsWith("error ", StringComparison.Ordinal))
            VirtualJoystickMessage = T("JoyPro.VJoy: {0}", line.Substring(6));
        AddVirtualJoystickLog(line);
    }

    private void AddVirtualJoystickLog(string line)
    {
        VirtualJoystickLog.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        while (VirtualJoystickLog.Count > 200)
            VirtualJoystickLog.RemoveAt(0);
    }

    // "out <id> <8 axes> <buttons hex (128 bits, button 128 first)> <4 hats>"
    internal void ApplyVirtualJoystickOut(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 15 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return;
        VirtualJoystickLiveDevice? device = VirtualJoystickLive.FirstOrDefault(d => d.Id == id);
        if (device == null)
        {
            device = new VirtualJoystickLiveDevice(id);
            VirtualJoystickLive.Add(device);
        }

        for (int axis = 0; axis < 8; axis++)
        {
            if (!int.TryParse(parts[2 + axis], NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
                continue;
            double percent = Math.Clamp(raw / 32767.0 * 100.0, 0, 100);
            VirtualJoystickAxisItem item = device.Axes[axis];
            if (Math.Abs(item.Value - percent) > 0.05)
            {
                item.Value = percent;
                item.ValueText = percent.ToString("0", CultureInfo.InvariantCulture) + " %";
            }
        }

        string hex = parts[10];
        List<string> pressed = [];
        for (int i = 0; i < hex.Length; i++)
        {
            int nibble = Convert.ToInt32(hex[hex.Length - 1 - i].ToString(), 16);
            for (int bit = 0; bit < 4; bit++)
            {
                if ((nibble & (1 << bit)) != 0)
                    pressed.Add((i * 4 + bit + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        string buttons = pressed.Count == 0 ? "none" : string.Join(", ", pressed);
        if (device.ButtonsText != buttons)
            device.ButtonsText = buttons;
        List<string> hats = [];
        for (int hat = 0; hat < 4; hat++)
        {
            string value = parts[11 + hat];
            if (value != "-1" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int angle))
                hats.Add(T("hat {0}: {1}°", (hat + 1).ToString(CultureInfo.InvariantCulture), (angle / 100).ToString(CultureInfo.InvariantCulture)));
        }

        string hatsText = hats.Count == 0 ? "centred" : string.Join(", ", hats);
        if (device.HatsText != hatsText)
            device.HatsText = hatsText;
    }

    private static readonly (ushort Mask, string Name)[] XboxButtonNames =
    [
        (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"), (0x0100, "LB"), (0x0200, "RB"), (0x0020, "Back"), (0x0010, "Start"),
        (0x0040, "LS"), (0x0080, "RS"), (0x0400, "Guide"), (0x0001, "D-pad up"), (0x0002, "D-pad down"), (0x0004, "D-pad left"), (0x0008, "D-pad right")
    ];

    // "xout <LX> <LY> <RX> <RY> <LT> <RT> <buttons hex>": the virtual Xbox pad as JoyPro.VJoy writes it.
    internal void ApplyVirtualJoystickXboxOut(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8)
            return;
        VirtualJoystickLiveDevice? device = VirtualJoystickLive.FirstOrDefault(d => d.Id == 0);
        if (device == null)
        {
            device = new VirtualJoystickLiveDevice(0);
            VirtualJoystickLive.Add(device);
        }

        for (int axis = 0; axis < 6; axis++)
        {
            if (!int.TryParse(parts[1 + axis], NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
                continue;
            double percent = axis < 4 ? Math.Clamp((raw + 32768) / 65535.0 * 100.0, 0, 100) : Math.Clamp(raw / 255.0 * 100.0, 0, 100);
            VirtualJoystickAxisItem item = device.Axes[axis];
            if (Math.Abs(item.Value - percent) > 0.05)
            {
                item.Value = percent;
                item.ValueText = percent.ToString("0", CultureInfo.InvariantCulture) + " %";
            }
        }

        if (!ushort.TryParse(parts[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort buttons))
            return;
        List<string> pressed = XboxButtonNames.Where(b => (buttons & b.Mask) != 0).Select(b => b.Name).ToList();
        string text = pressed.Count == 0 ? "none" : string.Join(", ", pressed);
        if (device.ButtonsText != text)
            device.ButtonsText = text;
        if (device.HatsText != T("(D-pad: see buttons)"))
            device.HatsText = T("(D-pad: see buttons)");
    }

    private static string DescribeVirtualJoystickReady(string line)
    {
        Dictionary<string, string> values = ParseKeyValues(line);
        return T("vJoy device(s) {0}, {1} physical device(s) connected, {2} slot(s), {3} Hz.", values.GetValueOrDefault("devices", "?"), values.GetValueOrDefault("sources", "?"), values.GetValueOrDefault("slots", "?"), values.GetValueOrDefault("rate", "?"));
    }

    private static string DescribeVirtualJoystickStats(string line)
    {
        Dictionary<string, string> values = ParseKeyValues(line);
        return T("{0} passes/s, {1} vJoy updates, devices {2}, memory {3} kB", values.GetValueOrDefault("hz", "?"), values.GetValueOrDefault("writes", "0"), values.GetValueOrDefault("sources", "?"), values.GetValueOrDefault("ws-kb", "?"));
    }

    private static Dictionary<string, string> ParseKeyValues(string line)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int split = part.IndexOf('=');
            if (split > 0)
                values[part[..split]] = part[(split + 1)..];
        }

        return values;
    }

    // ---- Auto start while a virtual-mode game runs

    private void UpdateVirtualJoystickPolling()
    {
        bool want = VirtualJoystickAutoStart && HasVirtualJoystickGames;
        if (want && virtualJoystickPollCts == null)
        {
            virtualJoystickPollCts = new CancellationTokenSource();
            _ = PollVirtualJoystickGamesAsync(virtualJoystickPollCts.Token);
        }
        else if (!want && virtualJoystickPollCts != null)
        {
            virtualJoystickPollCts.Cancel();
            virtualJoystickPollCts.Dispose();
            virtualJoystickPollCts = null;
        }
    }

    private async Task PollVirtualJoystickGamesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(VirtualJoystickPollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await CheckVirtualJoystickGamesOnceAsync();
        }
    }

    internal async Task CheckVirtualJoystickGamesOnceAsync()
    {
        List<string> names = VirtualJoystickGames.Where(g => g.IsVirtual)
            .SelectMany(g => virtualJoystickProcessNames.TryGetValue(g.Id, out List<string>? list) ? list : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return;
        if (vjoyHost == null)
        {
            if (VirtualJoystickFindGame(names) is int pid)
            {
                AddVirtualJoystickLog("A game with virtual mode started (pid " + pid.ToString(CultureInfo.InvariantCulture) + "): starting the virtual joystick.");
                await StartVirtualJoystickCoreAsync(test: false, gamePid: pid);
            }
        }
        else if (virtualJoystickAutoGamePid != null && VirtualJoystickFindGame(names) == null)
        {
            await StopVirtualJoystickCoreAsync("game exited");
        }
    }

    // ---- Shutdown

    // Closing JoyPro: the forwarder stops (vJoy neutral), and a HidHide session is restored by an engine process that
    // outlives the GUI (its administrator prompt may come after the window is gone); declined, the next start does it.
    public void StopVirtualJoystickForShutdown()
    {
        virtualJoystickPollCts?.Cancel();
        IFfbHostProcess? host = vjoyHost;
        vjoyHost = null;
        if (host != null)
        {
            virtualJoystickStopping = true;
            host.Send("stop");
            if (!host.WaitForExit(2000))
                host.Kill();
            host.Dispose();
        }

        if (!virtualJoystickHidHideApplied)
            return;
        string engine = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
            return;
        ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(engine) ?? string.Empty };
        info.ArgumentList.Add("vjoy");
        info.ArgumentList.Add("restore-hidhide");
        try
        {
            ExternalProcess.Start(info)?.Dispose();
            virtualJoystickHidHideApplied = false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // ---- Helpers

    private static bool IsProcessAlive(int pid, long startTicks)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;
            return startTicks == 0 || Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static string ReadMessage(string? output, string fallback)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output ?? string.Empty));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? fallback;
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(fallback) ? FirstLine(output) : fallback;
    }

    private static string FirstLine(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        int end = value.IndexOfAny(['\r', '\n']);
        return end < 0 ? value : value[..end];
    }
}
