using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Virtual functions on the Virtual Joystick page (docs/virtual-joystick.md "Virtual functions"): per game in virtual
// mode the functions JoyPro.VJoy evaluates between the physical inputs and the virtual outputs. Everything is stored
// by the engine (engine vjoy functions add|set|remove), which also gives new functions free virtual outputs and
// reserves them in the vJoy table; the editor previews with the forwarder's own evaluation code.
// Also here: "Set up vJoy for maximum" (engine vjoy configure-vjoy --max, one administrator prompt, a Windows restart
// afterwards that JoyPro never does itself) and what each game reads of a joystick.
public sealed partial class MainWindowViewModel
{
    private static readonly JsonSerializerOptions VirtualFunctionJson = new() { PropertyNameCaseInsensitive = true };

    public ObservableCollection<VirtualFunctionListItem> VirtualFunctions { get; } = [];

    public IReadOnlyList<VfTypeOption> VirtualFunctionTypes { get; } = VirtualFunctionCatalog.Types.Select(t => new VfTypeOption(t)).ToList();

    public ObservableCollection<VirtualJoystickGameItem> VirtualFunctionGames { get; } = [];

    internal List<VfDeviceOption> VirtualFunctionDevices { get; } = [];

    [ObservableProperty]
    private VirtualJoystickGameItem? virtualFunctionGame;

    [ObservableProperty]
    private VfTypeOption? newVirtualFunctionType;

    [ObservableProperty]
    private VirtualFunctionEditorViewModel? virtualFunctionEditor;

    [ObservableProperty]
    private string virtualFunctionMessage = string.Empty;

    [ObservableProperty]
    private bool vJoyAtMaximum = true;

    [ObservableProperty]
    private string vJoyMaximumText = string.Empty;

    public bool HasVirtualFunctions => VirtualFunctions.Count > 0;

    public bool HasVirtualFunctionEditor => VirtualFunctionEditor != null;

    public bool CanAddVirtualFunction => VirtualFunctionGame != null && NewVirtualFunctionType != null;

    public string VirtualFunctionsHeader => VirtualFunctions.Count == 0
        ? T("No virtual functions yet.")
        : T("{0} virtual function(s)", VirtualFunctions.Count.ToString(CultureInfo.InvariantCulture));

    partial void OnVirtualFunctionGameChanged(VirtualJoystickGameItem? value) => OnPropertyChanged(nameof(CanAddVirtualFunction));

    partial void OnNewVirtualFunctionTypeChanged(VfTypeOption? value) => OnPropertyChanged(nameof(CanAddVirtualFunction));

    partial void OnVirtualFunctionEditorChanged(VirtualFunctionEditorViewModel? oldValue, VirtualFunctionEditorViewModel? newValue)
    {
        oldValue?.Close();
        OnPropertyChanged(nameof(HasVirtualFunctionEditor));
    }

    // ---- status

    // Called from ApplyVirtualJoystickStatus: functions, input devices, per-game limits, vJoy maximum.
    internal void ApplyVirtualFunctionsStatus(JsonElement root)
    {
        VirtualFunctionDevices.Clear();
        if (root.TryGetProperty("InputDevices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement device in devices.EnumerateArray())
            {
                VirtualFunctionDevices.Add(new VfDeviceOption(ReadJsonString(device, "Device"), ReadJsonString(device, "Name"), ReadStringArray(device, "Axes"),
                    ReadJsonInt(device, "Buttons"), ReadJsonInt(device, "Hats")));
            }
        }

        string? selectedId = VirtualFunctionGame?.Id;
        VirtualFunctionGames.Clear();
        foreach (VirtualJoystickGameItem game in VirtualJoystickGames.Where(g => g.IsVirtual))
            VirtualFunctionGames.Add(game);
        VirtualFunctionGame = VirtualFunctionGames.FirstOrDefault(g => g.Id == selectedId) ?? VirtualFunctionGames.FirstOrDefault();

        // Per game: what it reads of a joystick, and what lies above it.
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement game in games.EnumerateArray())
            {
                VirtualJoystickGameItem? item = VirtualJoystickGames.FirstOrDefault(g => g.Id == ReadJsonString(game, "Id"));
                if (item == null)
                    continue;
                item.InputLimitText = game.TryGetProperty("InputLimit", out JsonElement limit) && limit.ValueKind == JsonValueKind.Object && ReadJsonBool(limit, "Known")
                    ? T("Reads of a device: {0}.", ReadJsonString(limit, "Describe").Replace("reads ", string.Empty, StringComparison.Ordinal))
                    : string.Empty;
                item.LimitWarningsText = string.Join(Environment.NewLine, ReadStringArray(game, "LimitWarnings"));
                item.FunctionCount = ReadJsonInt(game, "Functions");
            }
        }

        VirtualFunctions.Clear();
        if (root.TryGetProperty("Functions", out JsonElement functions) && functions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in functions.EnumerateArray())
            {
                VirtualFunctionListItem? item = ReadFunctionItem(entry);
                if (item != null)
                    VirtualFunctions.Add(item);
            }
        }

        // vJoy's maximum: 128 buttons, 8 axes, 4 hats per device.
        List<string> below = [];
        if (root.TryGetProperty("VJoy", out JsonElement vjoy) && vjoy.TryGetProperty("Devices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement device in list.EnumerateArray())
            {
                if (ReadJsonBool(device, "Configured") && device.TryGetProperty("AtMaximum", out JsonElement max) && max.ValueKind == JsonValueKind.False)
                    below.Add(T("vJoy device {0}: {1}", ReadJsonInt(device, "Id").ToString(CultureInfo.InvariantCulture), ReadJsonString(device, "Describe")));
            }
        }

        VJoyAtMaximum = below.Count == 0;
        VJoyMaximumText = below.Count == 0
            ? string.Empty
            : T("Now: {0}. vJoy's maximum: 8 axes, 128 buttons, 4 hats per device. Games that read only 32 buttons are warned about on their card.", string.Join("; ", below));
        OnPropertyChanged(nameof(HasVirtualFunctions));
        OnPropertyChanged(nameof(VirtualFunctionsHeader));
        // An open editor keeps its edits; the engine's notes for it are refreshed on save.
    }

    private static VirtualFunctionListItem? ReadFunctionItem(JsonElement entry)
    {
        if (!entry.TryGetProperty("Function", out JsonElement functionElement) || functionElement.ValueKind != JsonValueKind.Object)
            return null;
        VirtualFunction? function = JsonSerializer.Deserialize<VirtualFunction>(functionElement.GetRawText(), VirtualFunctionJson);
        if (function == null)
            return null;
        function.Normalize();
        List<string> outputs = [];
        if (entry.TryGetProperty("Outputs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement output in list.EnumerateArray())
            {
                string target = ReadJsonString(output, "Target");
                if (target.Length > 0)
                    outputs.Add(ReadJsonString(output, "Role") + " " + ReadJsonString(output, "Describe"));
            }
        }

        return new VirtualFunctionListItem(ReadJsonString(entry, "Game"), ReadJsonString(entry, "GameName"), function, ReadStringArray(entry, "Problems"), ReadStringArray(entry, "Warnings"), outputs);
    }

    // ---- add / edit / save / remove

    [RelayCommand]
    private async Task AddVirtualFunctionAsync()
    {
        if (VirtualFunctionGame == null || NewVirtualFunctionType == null)
        {
            VirtualFunctionMessage = T("Pick a game in virtual mode and a function type first.");
            return;
        }

        string game = VirtualFunctionGame.Id;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "add", "--game", game, "--type", NewVirtualFunctionType.Id], includeProfile: true, mutatesProfile: true,
            busyLabel: "Adding " + NewVirtualFunctionType.Name + "...", forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            VirtualFunctionMessage = T("The function could not be added: {0} {1}", FirstLine(result?.StandardError), FirstLine(result?.StandardOutput));
            return;
        }

        string id = ReadAddedId(result.StandardOutput);
        VirtualFunctionMessage = T("{0} Choose its inputs below; JoyPro picked free virtual outputs.", ReadMessage(result.StandardOutput, "Added."));
        await RefreshVirtualJoystickAsync();
        VirtualFunctionListItem? added = VirtualFunctions.FirstOrDefault(f => f.Id == id && f.Game == game);
        if (added != null)
            EditVirtualFunction(added);
    }

    private static string ReadAddedId(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.TryGetProperty("Item", out JsonElement item))
                return ReadJsonString(item, "Id");
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    [RelayCommand]
    private void EditVirtualFunction(VirtualFunctionListItem? item)
    {
        if (item == null)
            return;
        bool xbox = VirtualJoystickGames.FirstOrDefault(g => g.Id == item.Game)?.IsXbox ?? false;
        VirtualFunctionEditor = new VirtualFunctionEditorViewModel(item.Game, item.GameName, item.Function, VirtualFunctionDevices, xbox, item.Problems.Concat(item.Warnings).ToList())
        {
            BindHandler = BindVirtualFunctionOutputAsync
        };
    }

    [RelayCommand]
    private void CloseVirtualFunctionEditor() => VirtualFunctionEditor = null;

    [RelayCommand]
    private async Task SaveVirtualFunctionAsync()
    {
        VirtualFunctionEditorViewModel? editor = VirtualFunctionEditor;
        if (editor == null)
            return;
        VirtualFunction function = editor.ToFunction();
        Directory.CreateDirectory(VirtualJoystickDataFolder);
        string spec = Path.Combine(VirtualJoystickDataFolder, "function-spec.json");
        await File.WriteAllTextAsync(spec, JsonSerializer.Serialize(function));
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "set", "--game", editor.Game, "--id", editor.Id, "--spec", "@" + spec], includeProfile: true, mutatesProfile: true,
            busyLabel: "Saving " + function.Name + "...", forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            VirtualFunctionMessage = T("The function was not saved: {0} {1}", FirstLine(result?.StandardError), FirstLine(result?.StandardOutput));
            return;
        }

        VirtualFunctionMessage = ReadMessage(result.StandardOutput, "Saved.") + (VirtualJoystickRunning ? T(" Press Stop and Start (or Test) so the forwarder takes the change.") : string.Empty);
        await RefreshVirtualJoystickAsync();
        VirtualFunctionListItem? saved = VirtualFunctions.FirstOrDefault(f => f.Id == editor.Id && f.Game == editor.Game);
        if (saved != null)
            EditVirtualFunction(saved);
    }

    [RelayCommand]
    private async Task DeleteVirtualFunctionAsync(VirtualFunctionListItem? item)
    {
        if (item == null)
            return;
        if (!await ConfirmAsync(T("Remove {0}?", item.Name), T("The function {0} ({1}) of {2} is removed and its virtual outputs are free again. Binds in your relations on those outputs stay; remove them there if you do not need them.", item.Name, item.TypeName, item.GameName),
                T("Remove"), isDanger: true))
            return;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "remove", "--game", item.Game, "--id", item.Id], includeProfile: true, mutatesProfile: true,
            busyLabel: "Removing " + item.Name + "...", forceText: false);
        VirtualFunctionMessage = result == null ? string.Empty : result.ExitCode == 0 ? ReadMessage(result.StandardOutput, "Removed.") : T("Not removed: {0}", FirstLine(result.StandardError));
        if (VirtualFunctionEditor?.Id == item.Id)
            VirtualFunctionEditor = null;
        await RefreshVirtualJoystickAsync();
    }

    [RelayCommand]
    private async Task ToggleVirtualFunctionAsync(VirtualFunctionListItem? item)
    {
        if (item == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "set", "--game", item.Game, "--id", item.Id, "--enabled", item.Enabled ? "off" : "on"], includeProfile: true, mutatesProfile: true,
            busyLabel: (item.Enabled ? T("Switching off ") : T("Switching on ")) + item.Name + "...", forceText: false);
        if (result != null && result.ExitCode != 0)
            VirtualFunctionMessage = T("Not changed: {0}", FirstLine(result.StandardError));
        await RefreshVirtualJoystickAsync();
    }

    // A relation bound to a function's output: engine binds set <relation> --device <vJoy device> --input <JOY_BTN30>.
    internal async Task BindVirtualFunctionOutputAsync(VfOutputRoleItem? output, string relationName)
    {
        VirtualFunctionEditorViewModel? editor = VirtualFunctionEditor;
        if (editor == null || output == null || output.TargetToken.Length == 0 || output.TargetToken.StartsWith("KEY_", StringComparison.Ordinal))
            return;
        string relation = (relationName ?? string.Empty).Trim();
        if (relation.Length == 0)
        {
            VirtualFunctionMessage = T("Type the relation's name in \"Bind a relation to an output\" first.");
            return;
        }

        string device = output.IsVJoyTarget ? VJoyDirectInputName(output.VJoyDevice) : T("Xbox 360 Controller (JoyPro virtual pad)");
        if (device.Length == 0)
        {
            VirtualFunctionMessage = T("vJoy device {0} is not seen by Windows yet (configure vJoy, then Refresh).", output.VJoyDevice.ToString(CultureInfo.InvariantCulture));
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(["binds", "set", relation, "--device", device, "--input", output.TargetToken], includeProfile: true, mutatesProfile: true,
            busyLabel: "Binding " + relation + "...", forceText: false);
        VirtualFunctionMessage = result == null ? string.Empty : result.ExitCode == 0
            ? T("{0} is bound to {1} ({2} {3}). Export the game again.", relation, output.Describe, editor.Name, output.Label)
            : T("Not bound: {0} {1}", FirstLine(result.StandardError), FirstLine(result.StandardOutput));
    }

    // The DirectInput name ("vJoy Device {GUID}") of a vJoy device from the last status.
    private readonly Dictionary<int, string> vjoyDirectInputNames = [];

    private string VJoyDirectInputName(int id) => vjoyDirectInputNames.TryGetValue(id, out string? name) ? name : string.Empty;

    internal void ReadVJoyDirectInputNames(JsonElement vjoy)
    {
        vjoyDirectInputNames.Clear();
        if (!vjoy.TryGetProperty("Devices", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement device in list.EnumerateArray())
        {
            string name = ReadJsonString(device, "DirectInputName");
            if (name.Length > 0)
                vjoyDirectInputNames[ReadJsonInt(device, "Id")] = name;
        }
    }

    // ---- vJoy maximum

    [RelayCommand]
    private async Task SetUpVJoyMaximumAsync()
    {
        if (!await ConfirmAsync(T("Set up vJoy for its maximum?"),
                T("JoyPro runs vJoyConfig once (Windows asks for administrator rights) so each vJoy device it uses has 8 axes, 128 buttons and 4 hats. Windows must restart before the new setup takes effect - JoyPro never restarts Windows, do it when it suits you. Afterwards export the games in virtual mode again. Games that read only 32 buttons of a device are listed on their card."),
                T("Set up vJoy"), isDanger: false))
            return;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "configure-vjoy", "--max"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Setting up vJoy (Windows asks for administrator rights)..."), forceText: false);
        if (result != null)
            VirtualJoystickMessage = ReadMessage(result.StandardOutput, FirstLine(result.StandardError));
        await RefreshVirtualJoystickAsync();
    }

    // ---- live ("fn <id> <input 0..1000> <state> <active>")

    internal void ApplyVirtualFunctionLive(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int input) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int active))
            return;
        string id = parts[1];
        VirtualFunctionListItem? item = VirtualFunctions.FirstOrDefault(f => f.Id == id);
        if (item != null)
        {
            string text = T("live: state {0}, {1}", state.ToString(CultureInfo.InvariantCulture), (active == 0 ? T("no output on") : T("{0} output(s) on", CountBits(active).ToString(CultureInfo.InvariantCulture))));
            if (item.LiveText != text)
                item.LiveText = text;
        }

        if (VirtualFunctionEditor?.Id == id)
            VirtualFunctionEditor.ApplyLive(input, state, active);
    }

    private static int CountBits(int value)
    {
        int count = 0;
        for (uint v = (uint)value; v != 0; v &= v - 1)
            count++;
        return count;
    }
}
