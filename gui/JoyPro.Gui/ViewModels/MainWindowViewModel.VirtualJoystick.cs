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
    // Kept in English (the page translates it through TConverter, also after a language change).
    private static readonly string VirtualJoystickIdleText = N("Not running.");

    public ObservableCollection<VirtualJoystickGameItem> VirtualJoystickGames { get; } = [];

    public ObservableCollection<VirtualJoystickSlotItem> VirtualJoystickSlots { get; } = [];

    public ObservableCollection<VirtualJoystickLiveDevice> VirtualJoystickLive { get; } = [];

    public ObservableCollection<string> VirtualJoystickLog { get; } = [];

    [ObservableProperty]
    private string vJoyStatusText = N("Press Refresh to check vJoy and HidHide.");

    [ObservableProperty]
    private string vJoyDevicesText = string.Empty;

    [ObservableProperty]
    private string hidHideStatusText = string.Empty;

    [ObservableProperty]
    private string virtualJoystickSessionText = N("No HidHide session.");

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

    // Only Xbox pad games are virtual (no vJoy game): "How to use it" tells the pad's steps (nothing to switch or export).
    public bool VirtualJoystickPadOnly => HasXboxGames && !VirtualJoystickGames.Any(g => g.Mode == VirtualJoystickGameItem.ModeVJoy);

    public bool VirtualJoystickShowsVJoySteps => !VirtualJoystickPadOnly;

    // Pad-only players see the pretend controller's page: vJoy, HidHide, the forwarding rate and the modes only under
    // "Show details" (kept over a restart, ui-settings.json).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualJoystickShowsExpertParts))]
    private bool virtualJoystickShowDetails;

    partial void OnVirtualJoystickShowDetailsChanged(bool value) => SaveUiSettings();

    public bool VirtualJoystickShowsExpertParts => !VirtualJoystickPadOnly || VirtualJoystickShowDetails;

    // The bottom bar's name of the page: the pretend controller for a player whose games are all on it (as the page
    // title), else Virtual Joystick. Before the page was read: the pad tour, or the tour's games all pad games.
    public string VirtualJoystickNavTitle => VirtualJoystickPadOnly || (!virtualJoystickStatusRead && (ActiveTour?.Id == Services.Help.TourCatalog.Pad ||
                                                                     (TourGameIds.Count > 0 && TourGameIds.All(g => Services.Help.TourCatalog.PadGames.Contains(g)))))
        ? T("Pretend controller")
        : T("Virtual Joystick");

    // A game is on the Xbox pad (by itself or chosen) but ViGEmBus is missing: the page says so with the one-click
    // install of the component (the same card as under "What this page needs").
    public bool VirtualJoystickNeedsViGEm => HasXboxGames && !ViGEmInstalled && virtualJoystickStatusRead;

    public string VirtualJoystickViGEmMissingText => !VirtualJoystickNeedsViGEm
        ? string.Empty
        : T("{0} uses the pretend controller, and the pretend controller needs the free helper ViGEmBus (by Nefarius). Tick the box below, then click Install ViGEmBus. JoyPro sees it by itself when it is there.",
            string.Join(", ", VirtualJoystickGames.Where(g => g.IsXbox).Select(g => g.Name)));

    public ComponentItem? VirtualJoystickViGEmComponent => FindComponent(Services.Components.ComponentCatalog.ViGEmBusId);

    private bool virtualJoystickStatusRead;

    // ---- Several games on the Xbox pad: each keeps its own pad table, one is live at a time.

    public ObservableCollection<VirtualJoystickGameItem> VirtualJoystickXboxGames { get; } = [];

    public bool HasSeveralXboxGames => VirtualJoystickXboxGames.Count > 1;

    // The game whose pad table a manual Start/Test uses when none of the Xbox games runs (saved in the profile).
    [ObservableProperty]
    private VirtualJoystickGameItem? virtualJoystickXboxPadGame;

    // The game whose pad table is live now (empty while stopped or without the pad).
    [ObservableProperty]
    private string virtualJoystickLiveXboxGame = string.Empty;

    private string virtualJoystickSavedXboxPadGame = string.Empty;

    partial void OnVirtualJoystickXboxPadGameChanged(VirtualJoystickGameItem? value)
    {
        if (virtualJoystickLoading || value == null || string.Equals(value.Id, virtualJoystickSavedXboxPadGame, StringComparison.OrdinalIgnoreCase))
            return;
        virtualJoystickSavedXboxPadGame = value.Id;
        if (!string.IsNullOrWhiteSpace(ProfilePath))
            _ = RunEngineAsync(["vjoy", "settings", "--xbox-pad-game", value.Id], includeProfile: true, mutatesProfile: true,
                busyLabel: N("Saving the virtual joystick setting..."), forceText: false);
        if (VirtualJoystickRunning && VirtualJoystickLiveXboxGame.Length > 0 && !string.Equals(VirtualJoystickLiveXboxGame, value.Id, StringComparison.OrdinalIgnoreCase))
            VirtualJoystickMessage = T("The pad table of {0} goes live with the next Start (Stop, then Start).", value.Name);
    }

    // The Xbox game whose pad table goes live: the one whose process runs (the game that started, with auto start),
    // else the user's pick, else the first.
    internal string ChooseVirtualJoystickXboxGame(int? gamePid)
    {
        List<VirtualJoystickGameItem> xboxGames = VirtualJoystickGames.Where(g => g.IsXbox).ToList();
        if (xboxGames.Count == 0)
            return string.Empty;
        foreach (VirtualJoystickGameItem game in xboxGames)
        {
            if (!virtualJoystickProcessNames.TryGetValue(game.Id, out List<string>? names) || names.Count == 0)
                continue;
            int? pid = VirtualJoystickFindGame(names);
            if (pid != null && (gamePid == null || pid == gamePid))
                return game.Id;
        }

        return VirtualJoystickXboxPadGame is { IsXbox: true } picked ? picked.Id : xboxGames[0].Id;
    }

    // Per pad control of the game's table (VirtualJoystickSlots, engine status "XboxMapping"): what it does and where it
    // comes from, "Fire machine gun (VKB Gladiator BTN1)". Key: LX LY RX RY LT RT, A B X Y LB RB Back Start LS RS Guide,
    // D-pad up/down/left/right, D-pad (a whole hat).
    internal static Dictionary<string, string> XboxControlLabels(IEnumerable<VirtualJoystickSlotItem> slots, string game)
    {
        Dictionary<string, List<string>> parts = new(StringComparer.OrdinalIgnoreCase);
        foreach (VirtualJoystickSlotItem slot in slots)
        {
            if (!slot.IsXbox || !slot.Used || (game.Length > 0 && !string.Equals(slot.Game, game, StringComparison.OrdinalIgnoreCase)))
                continue;
            string target = slot.Target.Trim().TrimEnd('~').ToUpperInvariant();
            string sign = string.Empty;
            if (target.Length > 2 && (target[^1] == '+' || target[^1] == '-') && target.StartsWith("XB_", StringComparison.Ordinal))
            {
                sign = target[^1].ToString();
                target = target[..^1];
            }

            string what = slot.Relations.FirstOrDefault() ?? (string.IsNullOrWhiteSpace(slot.TargetName) ? slot.Target : slot.TargetName);
            string input = slot.Input.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) ? slot.Input[4..] : slot.Input;
            string text = (sign.Length > 0 ? sign + " " : string.Empty) + what + " (" + (string.IsNullOrWhiteSpace(slot.SourceName) ? slot.Source : slot.SourceName) + " " + input + ")";
            foreach (string control in XboxControlNames(target))
            {
                if (!parts.TryGetValue(control, out List<string>? list))
                    parts[control] = list = [];
                if (!list.Contains(text))
                    list.Add(text);
            }
        }

        return parts.ToDictionary(p => p.Key, p => string.Join("; ", p.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> XboxControlNames(string token) => token switch
    {
        "XB_LX" => ["LX"],
        "XB_LY" => ["LY"],
        "XB_RX" => ["RX"],
        "XB_RY" => ["RY"],
        "XB_LT" => ["LT"],
        "XB_RT" => ["RT"],
        "XB_TRIGGERS" => ["LT", "RT"],
        "XB_A" => ["A"],
        "XB_B" => ["B"],
        "XB_X" => ["X"],
        "XB_Y" => ["Y"],
        "XB_LB" => ["LB"],
        "XB_RB" => ["RB"],
        "XB_BACK" => ["Back"],
        "XB_START" => ["Start"],
        "XB_LS" => ["LS"],
        "XB_RS" => ["RS"],
        "XB_GUIDE" => ["Guide"],
        "XB_DPAD_UP" => ["D-pad up"],
        "XB_DPAD_DOWN" => ["D-pad down"],
        "XB_DPAD_LEFT" => ["D-pad left"],
        "XB_DPAD_RIGHT" => ["D-pad right"],
        "XB_DPAD" => ["D-pad"],
        _ => []
    };

    // The live view of the pad gets the labels of the live game's table.
    private void FillXboxLiveLabels(VirtualJoystickLiveDevice device)
    {
        string game = VirtualJoystickLiveXboxGame;
        Dictionary<string, string> labels = XboxControlLabels(VirtualJoystickSlots, game);
        device.Game = game.Length == 0 ? string.Empty : VirtualJoystickGames.FirstOrDefault(g => string.Equals(g.Id, game, StringComparison.OrdinalIgnoreCase))?.Name ?? game;
        foreach (VirtualJoystickAxisItem axis in device.Axes)
            axis.Mapping = labels.TryGetValue(axis.Name, out string? text) ? text : string.Empty;
        device.Controls.Clear();
        foreach (string name in XboxButtonNames.Select(b => b.Name).Append("D-pad"))
        {
            if (labels.TryGetValue(name, out string? text))
                device.Controls.Add(new VirtualJoystickPadControlItem(name, text));
        }
    }

    partial void OnViGEmInstalledChanged(bool value) => NotifyVirtualJoystickState();

    private void ApplyVirtualJoystickXboxGames(string savedPick)
    {
        VirtualJoystickXboxGames.Clear();
        foreach (VirtualJoystickGameItem game in VirtualJoystickGames.Where(g => g.IsXbox))
            VirtualJoystickXboxGames.Add(game);
        virtualJoystickSavedXboxPadGame = savedPick;
        VirtualJoystickXboxPadGame = VirtualJoystickXboxGames.FirstOrDefault(g => string.Equals(g.Id, savedPick, StringComparison.OrdinalIgnoreCase)) ?? VirtualJoystickXboxGames.FirstOrDefault();
        OnPropertyChanged(nameof(HasSeveralXboxGames));
    }

    // Games whose virtual Xbox pad must be created after their title screen shows (docs/virtual-joystick.md "When to
    // start the pad"): a pad that exists before the launch never reaches them.
    internal static readonly string[] XboxPadAfterTitleGames = ["HLLVietnam", "AceCombat8"];

    // The exception for those games: named when the page knows them set to "Xbox pad", hidden when the page knows the
    // games and neither is, a general note naming both while the games are not loaded yet.
    public string VirtualXboxPadTimingNote
    {
        get
        {
            if (VirtualJoystickGames.Count == 0)
                return T("Xbox controller games: start the game first and wait for its title screen. Then press Start. Too early? Press Stop, then Start.");
            List<VirtualJoystickGameItem> late = VirtualJoystickGames
                .Where(g => g.IsXbox && XboxPadAfterTitleGames.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (late.Count == 0)
                return string.Empty;
            if (late.Count > 1)
                return T("Xbox controller games: start the game first and wait for its title screen. Then press Start. Too early? Press Stop, then Start.");
            return T("{0} (Xbox controller): start the game first and wait for its title screen. Then press Start. Too early? Press Stop, then Start.", late[0].Name);
        }
    }

    public bool HasVirtualXboxPadTimingNote => VirtualXboxPadTimingNote.Length > 0;

    public string VirtualJoystickStartTip => VirtualJoystickPadOnly
        ? WithXboxPadTimingNote(T("Turns on the pretend controller: your stick and throttle drive it live. Xbox controller games need no administrator prompt."))
        : WithXboxPadTimingNote(T("Hides your physical devices from the games set to Virtual (Windows asks once for administrator rights) and copies their inputs to vJoy live. vJoy: press it before you start the game."));

    // Auto start starts when the game's program starts, before its title screen: right for vJoy games, too early for the
    // title-screen games (XboxPadAfterTitleGames), which the tip names instead of repeating the Start rule.
    public string VirtualJoystickAutoStartTip
    {
        get
        {
            string text = T("JoyPro looks for the game every 3 seconds while this is on and starts when the game starts. For games set to vJoy, pressing Start before the game is still the safest: some games only look for devices when they start.");
            List<string> late = VirtualJoystickGames.Where(g => g.IsXbox && XboxPadAfterTitleGames.Contains(g.Id, StringComparer.OrdinalIgnoreCase)).Select(g => g.Name).ToList();
            if (VirtualJoystickGames.Count == 0)
                late = [T("Xbox controller games")];
            return late.Count == 0
                ? text
                : text + " " + T("Not for {0}: auto start comes before the title screen, which is too early. Click Start yourself at the title screen.", string.Join(", ", late));
        }
    }

    private string WithXboxPadTimingNote(string text)
    {
        string note = VirtualXboxPadTimingNote;
        return note.Length == 0 ? text : text + " " + note;
    }

    [ObservableProperty]
    private bool hidHideInstalled;

    // The last start failed: the page shows Try again next to the message.
    [ObservableProperty]
    private bool virtualJoystickCanRetry;

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
    // A start is running (map, table, HidHide, forwarder): a second one (auto start while the user pressed Start) waits
    // for nothing and does nothing, so no second administrator prompt can come.
    private bool virtualJoystickStarting;
    // A start failed: auto start does not try again by itself while the game still runs (no prompt every few seconds);
    // it gets one try per game start, the user can always press Try again / Start.
    private bool virtualJoystickAutoStartBlocked;
    // Games that read only Xbox controllers (XInput): they never see the physical sticks, so they need no HidHide.
    private readonly HashSet<string> virtualJoystickReadsOnlyXbox = new(StringComparer.OrdinalIgnoreCase);
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
        OnPropertyChanged(nameof(VirtualJoystickNeedsViGEm));
        OnPropertyChanged(nameof(VirtualJoystickViGEmMissingText));
        OnPropertyChanged(nameof(VirtualXboxPadTimingNote));
        OnPropertyChanged(nameof(HasVirtualXboxPadTimingNote));
        OnPropertyChanged(nameof(VirtualJoystickStartTip));
        OnPropertyChanged(nameof(VirtualJoystickAutoStartTip));
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

    // Protocol words for JoyPro.VJoy, never translated (a translated "monitor on" left the live view empty in every
    // language but English).
    partial void OnVirtualJoystickLiveViewChanged(bool value) => vjoyHost?.Send(value ? "monitor on" : "monitor off");

    // ---- Status

    [RelayCommand]
    private async Task RefreshVirtualJoystickAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "status"], includeProfile: !string.IsNullOrWhiteSpace(ProfilePath), mutatesProfile: false,
            busyLabel: N("Checking vJoy and HidHide..."), forceText: false);
        if (result != null && result.ExitCode == 0)
            ApplyVirtualJoystickStatus(result.StandardOutput);
    }

    private string vigemVersion = string.Empty;
    private bool vigemRunning;

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
                    virtualJoystickStatusRead = true;
                    ViGEmInstalled = ReadJsonBool(vigem, "Installed");
                    vigemVersion = ReadJsonString(vigem, "Version");
                    vigemRunning = ReadJsonBool(vigem, "Running");
                }

                if (root.TryGetProperty("Settings", out JsonElement settings))
                {
                    VirtualJoystickHideDevices = ReadJsonBool(settings, "HideDevices");
                    VirtualJoystickAutoStart = ReadJsonBool(settings, "AutoStart");
                    ApplyVirtualTunnelStatus(settings);
                }

                if (root.TryGetProperty("RefreshRateHz", out JsonElement rate) && rate.TryGetInt32(out int hz))
                    VirtualJoystickRefreshRate = hz;

                ApplyVirtualJoystickGames(root);
                ApplyVirtualJoystickXboxGames(root.TryGetProperty("Settings", out JsonElement padSettings) ? ReadJsonString(padSettings, "XboxPadGame") : string.Empty);
                // The helper's line once the games are known (the first read too): plain words for pad players and during
                // the pad tour, the expert line only for someone who set games to "Xbox pad" by hand.
                if (root.TryGetProperty("ViGEm", out _))
                    ViGEmStatusText = ViGEmInstalled
                        ? T("ViGEmBus {0} is installed{1}: the pretend controller works.", vigemVersion, (vigemRunning ? "" : T(" but not running (restart Windows)")))
                        : VirtualJoystickPadOnly || HasXboxGames || ActiveTour?.Id == JoyPro.Gui.Services.Help.TourCatalog.Pad || TourGameIds.Any(JoyPro.Gui.Services.Help.TourCatalog.PadGames.Contains)
                            ? T("The pretend controller needs the free helper ViGEmBus. Install it in the card below.")
                            : T("ViGEmBus is not installed. Games set to \"Xbox pad\" need it (free driver by Nefarius): install it under \"What this page needs\" below, then press Refresh.");
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
        virtualJoystickReadsOnlyXbox.Clear();
        foreach (JsonElement game in games.EnumerateArray())
        {
            string id = ReadJsonString(game, "Id");
            if (id.Length == 0 || IsGameHidden(id))
                continue;
            if (ReadJsonBool(game, "ReadsOnlyXbox"))
                virtualJoystickReadsOnlyXbox.Add(id);
            // The engine's effective mode: the user's choice, else the Xbox pad for a game that reads only Xbox controllers.
            string mode = ReadJsonString(game, "Mode");
            bool isVirtual = mode is VirtualJoystickGameItem.ModeVJoy or VirtualJoystickGameItem.ModeXbox;
            bool autoMode = ReadJsonBool(game, "AutoMode");
            int relations = ReadJsonInt(game, "Relations");
            virtualJoystickProcessNames[id] = ReadStringArray(game, "ProcessNames");
            // The games the profile uses, every game the user put in virtual mode, and an installed game that is on the
            // Xbox pad by itself (it reads only Xbox pads: the pad tour shows it before the profile uses it). An Xbox-only
            // game that is not installed and not used stays off the list.
            if (relations == 0 && (!isVirtual || autoMode) && !(autoMode && gameInstallsKnown && installedGames.Contains(id)))
                continue;
            virtualJoystickModes[id] = isVirtual ? mode : VirtualJoystickGameItem.ModeNative;
            items.Add(new VirtualJoystickGameItem(id, DisplayText.GameName(id, ReadJsonString(game, "Name")), relations, isVirtual ? mode : VirtualJoystickGameItem.ModeNative, ReadStringArray(game, "Exes"),
                OnVirtualJoystickModeToggled, ReadJsonInt(game, "GamepadLayout"), ReadJsonBool(game, "WritesGamepadBinds"),
                ReadJsonString(game, "SuggestedMode"), ReadJsonString(game, "DeviceLimitNote"), autoMode));
        }

        // Alphabetical by the name shown (the user's language rules, case ignored); the function game picker follows.
        items.Sort((a, b) => VirtualJoystickGameNameOrder.Compare(a.Name, b.Name));
        VirtualJoystickGames.Clear();
        foreach (VirtualJoystickGameItem item in items)
            VirtualJoystickGames.Add(item);
        OnPropertyChanged(nameof(VirtualJoystickPadOnly));
        OnPropertyChanged(nameof(VirtualJoystickShowsVJoySteps));
        OnPropertyChanged(nameof(VirtualJoystickShowsExpertParts));
        OnPropertyChanged(nameof(VirtualJoystickNavTitle));
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
        // Pad-only players: the vJoy lines and the pad's technical notes (device ids, chords) go under Show details.
        List<string> details = [];
        List<string> expert = VirtualJoystickPadOnly ? details : lines;
        bool needsConfig = false;
        if (root.TryGetProperty("Needs", out JsonElement needs) && needs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement need in needs.EnumerateArray())
            {
                string id = ReadJsonInt(need, "Id").ToString(CultureInfo.InvariantCulture);
                if (ReadJsonBool(need, "Sufficient"))
                {
                    expert.Add(T("vJoy device {0} offers everything the table needs ({1}).", id, ReadJsonString(need, "Current")));
                    continue;
                }

                needsConfig = true;
                expert.Add(T("vJoy device {0} needs: {1}. JoyPro sets it to: {2}.", id, string.Join(", ", ReadStringArray(need, "Missing")), ReadJsonString(need, "Recommended")));
            }
        }

        if (root.TryGetProperty("XboxGames", out JsonElement xboxGames) && xboxGames.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement game in xboxGames.EnumerateArray())
            {
                List<string> unfit = ReadStringArray(game, "Unfit");
                // The game's name, never its id; the pad's words are the pretend controller's.
                string name = DisplayText.Game(ReadJsonString(game, "Game"));
                int slots = ReadJsonInt(game, "Slots");
                lines.Add(unfit.Count == 0
                    ? Plural("{1}: {0} input is on the pretend controller. Everything fits.", "{1}: {0} inputs are on the pretend controller. Everything fits.", slots, slots, name)
                    : Plural("{1}: {0} input is on the pretend controller. These do not fit: {2}", "{1}: {0} inputs are on the pretend controller. These do not fit: {2}", slots, slots, name, string.Join(" ", unfit)));
                foreach (string note in ReadStringArray(game, "Notes"))
                    expert.Add("  " + note);
            }
        }

        VirtualJoystickNeedsConfig = needsConfig;
        VirtualJoystickNeedsText = string.Join(Environment.NewLine, lines);
        VirtualJoystickNeedsDetails = string.Join(Environment.NewLine, details);
    }

    // The technical lines of the status for a pad-only player (shown under Show details).
    [ObservableProperty]
    private string virtualJoystickNeedsDetails = string.Empty;

    internal static StringComparer VirtualJoystickGameNameOrder => StringComparer.Create(JoyPro.Gui.Localization.Loc.Culture, ignoreCase: true);

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
            // The status line says what the game uses now, never the result of an earlier switch.
            StatusText = VirtualJoystickMessage;
            return;
        }

        virtualJoystickModes[item.Id] = mode;
        VirtualJoystickMessage = mode == VirtualJoystickGameItem.ModeVJoy
            ? T("{0} now uses the virtual joystick. Export it again (Export page) so the game gets the vJoy binds.", item.Name)
            : mode == VirtualJoystickGameItem.ModeXbox
                ? T("{0} now uses the pretend controller, a virtual Xbox 360 controller (needs the free helper ViGEmBus). {1}", item.Name, item.XboxNote)
                : T("{0} uses your physical devices again. Export it again so the game gets the binds on them.", item.Name);
        // The status line says the latest switch in words (an earlier "...virtual Xbox pad: done." must not stay).
        StatusText = VirtualJoystickMessage;
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
    private async Task StartVirtualJoystickAsync()
    {
        // A title-screen game (XboxPadAfterTitleGames) that is not running yet never sees a pretend controller made now:
        // Start still starts, but says so clearly (nobody is left wondering why the game does nothing).
        string early = VirtualJoystickTooEarlyGame();
        if (await StartVirtualJoystickCoreAsync(test: false, gamePid: null) && early.Length > 0)
        {
            VirtualJoystickMessage = T("Too early: {0} is not running yet, so it will not see the pretend controller. Start {0}. At its title screen, hold Alt and press Tab to come back here. Then click Stop and Start.", early);
            StatusText = VirtualJoystickMessage;
        }
    }

    // The name of the title-screen game Start is too early for: every game on the pretend controller is one, JoyPro
    // knows its program name, and none of them runs. Empty: not too early (or JoyPro cannot tell).
    internal string VirtualJoystickTooEarlyGame()
    {
        List<VirtualJoystickGameItem> virtualGames = VirtualJoystickGames.Where(g => g.IsVirtual).ToList();
        if (virtualGames.Count == 0 || !virtualGames.All(g => g.IsXbox && XboxPadAfterTitleGames.Contains(g.Id, StringComparer.OrdinalIgnoreCase)))
            return string.Empty;
        foreach (VirtualJoystickGameItem game in virtualGames)
        {
            if (!virtualJoystickProcessNames.TryGetValue(game.Id, out List<string>? names) || names.Count == 0 || VirtualJoystickFindGame(names) != null)
                return string.Empty;
        }
        return string.Join(", ", virtualGames.Select(g => g.Name));
    }

    // Reads the sticks and shows what vJoy would get; writes nothing to vJoy and does not touch HidHide.
    [RelayCommand]
    private Task TestVirtualJoystickAsync() => StartVirtualJoystickCoreAsync(test: true, gamePid: null);

    [RelayCommand]
    private Task StopVirtualJoystickAsync() => StopVirtualJoystickCoreAsync("stopped");

    internal async Task<bool> StartVirtualJoystickCoreAsync(bool test, int? gamePid)
    {
        if (vjoyHost != null)
            return true;
        if (virtualJoystickStarting)
            return false;
        virtualJoystickStarting = true;
        VirtualJoystickCanRetry = false;
        bool started = false;
        try
        {
            started = await StartVirtualJoystickOnceAsync(test, gamePid);
            return started;
        }
        finally
        {
            virtualJoystickStarting = false;
            // Every failed start (by hand or automatic) waits for the user: Try again, never a retry by itself.
            if (!started)
            {
                virtualJoystickAutoStartBlocked = true;
                VirtualJoystickCanRetry = !test;
            }
            else
            {
                virtualJoystickAutoStartBlocked = false;
            }
        }
    }

    // A game in virtual mode reads joysticks (DirectInput/HID), so HidHide matters for it; a game that reads only Xbox
    // controllers never sees the physical sticks and needs no HidHide (and no administrator prompt).
    private bool VirtualJoystickNeedsHidHide => VirtualJoystickGames.Any(g => g.IsVirtual && !virtualJoystickReadsOnlyXbox.Contains(g.Id));

    private async Task<bool> StartVirtualJoystickOnceAsync(bool test, int? gamePid)
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            VirtualJoystickMessage = T("Open a profile first.");
            return false;
        }

        // Start saves the mapping in the profile; Test only checks it (--dry-run: nothing is written, no history
        // version), the forwarding table below is built from the same plan either way.
        EngineCommandResult? map = test
            ? await RunEngineAsync(["vjoy", "map", "--dry-run"], includeProfile: true, mutatesProfile: false, busyLabel: N("Checking the vJoy mapping..."), forceText: false)
            : await RunEngineAsync(["vjoy", "map"], includeProfile: true, mutatesProfile: true, busyLabel: N("Updating the vJoy mapping..."), forceText: false);
        if (map == null || map.ExitCode != 0)
        {
            VirtualJoystickMessage = T("The mapping is not ready: {0} {1}", FirstLine(map?.StandardError), FirstLine(map?.StandardOutput));
            return false;
        }

        Directory.CreateDirectory(VirtualJoystickDataFolder);
        virtualJoystickConfigFile = Path.Combine(VirtualJoystickDataFolder, "forwarder.json");
        // One virtual Xbox pad at a time, several games may be on it (each with its own table): the table of the Xbox game
        // that runs (the one that started, with auto start), else the one picked on the page, else the first.
        List<string> configArgs = ["vjoy", "forwarder-config", "--out", virtualJoystickConfigFile];
        bool xbox = VirtualJoystickGames.Any(g => g.IsXbox);
        string liveXbox = ChooseVirtualJoystickXboxGame(gamePid);
        if (liveXbox.Length > 0)
            configArgs.AddRange(["--xbox-game", liveXbox]);
        // The virtual functions of the game that started (auto start); started by hand: of every virtual game.
        if (gamePid is int gameRunning && VirtualJoystickGames.FirstOrDefault(g => g.IsVirtual && virtualJoystickProcessNames.TryGetValue(g.Id, out List<string>? gameNames) && VirtualJoystickFindGame(gameNames) == gameRunning) is { } runningGame)
            configArgs.AddRange(["--game", runningGame.Id]);
        if (xbox && !test && !ViGEmInstalled)
        {
            VirtualJoystickMessage = VirtualJoystickPadOnly
                ? T("The pretend controller needs the free helper ViGEmBus. Install it in the card below. Test works without it.")
                : T("A game is set to \"Xbox pad\", but the ViGEmBus driver is not installed: press \"Get ViGEmBus\", install it and press Refresh (or use Test, which needs no driver).");
            return false;
        }
        EngineCommandResult? config = await RunEngineAsync(configArgs, includeProfile: true, mutatesProfile: false,
            busyLabel: N("Writing the forwarding table..."), forceText: false);
        if (config == null || config.ExitCode != 0)
        {
            VirtualJoystickMessage = T("The forwarding table could not be written: {0} {1}", FirstLine(config?.StandardError), FirstLine(config?.StandardOutput));
            return false;
        }

        string hidHideWarning = string.Empty;
        if (!test && VirtualJoystickHideDevices && VirtualJoystickNeedsHidHide)
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
            (string phase, bool blocking, string problem) = ReadHidHideOutcome(hide.StandardOutput);
            // A session that changed HidHide is restored at Stop (or when JoyPro closes), whatever its checks said.
            bool sessionActive = phase is "active" or "applying";
            if (hide.ExitCode == 0)
            {
                virtualJoystickHidHideApplied = phase != "none";
            }
            else if (hide.ExitCode == 2)
            {
                // The administrator prompt was declined: the user said no, nothing starts.
                VirtualJoystickMessage = ReadMessage(hide.StandardOutput, FirstLine(hide.StandardError)) +
                                         T(" Nothing was started; press Start again and allow the prompt, or switch off \"Hide the physical devices\".");
                await RefreshVirtualJoystickAsync();
                return false;
            }
            else
            {
                virtualJoystickHidHideApplied = sessionActive;
                if (blocking)
                {
                    // The games could not see the virtual joystick: HidHide goes back, nothing starts.
                    VirtualJoystickMessage = T("HidHide hides the virtual joystick from the games, so nothing was started: {0}", problem.Length > 0 ? problem : ReadMessage(hide.StandardOutput, FirstLine(hide.StandardError)));
                    await RestoreVirtualJoystickHidHideAsync();
                    await RefreshVirtualJoystickAsync();
                    return false;
                }

                // Not every device could be hidden: forwarding still works, the game may also see the physical devices.
                hidHideWarning = T("Virtual joystick on, but HidHide did not hide everything: {0} The game may see your physical devices as well as the virtual joystick.", problem.Length > 0 ? problem : ReadMessage(hide.StandardOutput, FirstLine(hide.StandardError)));
            }
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
        VirtualJoystickLiveXboxGame = liveXbox;
        virtualJoystickStopping = false;
        VirtualJoystickRunning = true;
        // It may have ended before the handlers were attached (vJoy busy, bad table): same as an end by itself.
        if (vjoyHost is { HasExited: true } ended)
        {
            await OnVirtualJoystickExitedAsync(ended);
            return false;
        }

        VirtualJoystickTestMode = test;
        // Pad-only players hear the pretend controller's words (no vJoy, no HidHide).
        bool padWords = VirtualJoystickPadOnly;
        VirtualJoystickForwarderText = test
            ? padWords ? T("Test: reading your stick. Nothing goes to the game.") : T("Test: reading your devices, nothing goes to vJoy.")
            : T("Starting...");
        VirtualJoystickMessage = test
            ? padWords
                ? T("Test: move your stick and press buttons. The picture shows what the pretend controller would get. Nothing is changed.")
                : T("Test mode: move your sticks and press buttons - the live view shows what the virtual joystick would get. vJoy and HidHide are not touched.")
            : hidHideWarning.Length > 0
                ? hidHideWarning
                : padWords
                    ? T("The pretend controller is on. Stop turns it off.")
                    : T("Virtual joystick on: start the game now (or keep it running). Stop switches everything back.");
        await RefreshVirtualJoystickAsync();
        return true;
    }

    // The session phase, whether the result stops the games from seeing the virtual joystick, and the first problem of
    // an engine vjoy apply-hidhide answer.
    private static (string Phase, bool Blocking, string Problem) ReadHidHideOutcome(string? output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output ?? string.Empty));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (string.Empty, false, string.Empty);
            List<string> problems = ReadStringArray(root, "Problems");
            return (ReadJsonString(root, "SessionPhase"), ReadJsonBool(root, "Blocking"), problems.Count == 0 ? string.Empty : problems[0]);
        }
        catch (JsonException)
        {
            return (string.Empty, false, string.Empty);
        }
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
        VirtualJoystickLiveXboxGame = string.Empty;
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
        VirtualJoystickLiveXboxGame = string.Empty;
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

        // "mode <game> <mode>": the forwarder's current mode of a game's functions.
        if (line.StartsWith("mode ", StringComparison.Ordinal))
            ApplyVirtualModeLive(line);

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
            FillXboxLiveLabels(device);
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
        foreach (VirtualJoystickPadControlItem control in device.Controls)
        {
            bool down = control.Name == "D-pad" ? (buttons & 0x000F) != 0 : pressed.Contains(control.Name);
            if (control.Pressed != down)
                control.Pressed = down;
        }
        // What each pressed button does shows on its line above (Controls); this line names the buttons.
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
            if (VirtualJoystickFindGame(names) is not int pid)
            {
                // No game runs: the next game start gets its one automatic try.
                virtualJoystickAutoStartBlocked = false;
                return;
            }

            // One automatic try per game start: after a failed start (a declined or failed administrator step, a
            // mapping that is not ready) nothing is tried again by itself while the game runs.
            if (virtualJoystickAutoStartBlocked || virtualJoystickStarting)
                return;
            AddVirtualJoystickLog("A game with virtual mode started (pid " + pid.ToString(CultureInfo.InvariantCulture) + "): starting the virtual joystick.");
            if (!await StartVirtualJoystickCoreAsync(test: false, gamePid: pid))
                VirtualJoystickMessage = T("{0} Automatic start does not try again while the game runs: press Try again when you are ready.", VirtualJoystickMessage).Trim();
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
