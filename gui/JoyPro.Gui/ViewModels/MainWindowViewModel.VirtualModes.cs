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
using JoyPro.Gui.Views.Controls;

namespace JoyPro.Gui.ViewModels;

// The Functions tab's Joystick Gremlin parts (docs/virtual-joystick.md "Modes", "Function chaining", "Coming from
// Joystick Gremlin"): the game's modes (engine vjoy modes), the graph of the game's functions where inputs are connected
// to other functions' outputs (engine vjoy functions set --input role=fn:<id>|<output>), the Gremlin profile import
// (engine vjoy import-gremlin, a dry run with its report first) and the vJoy device JoyPro tunnels into (Settings).
public sealed partial class MainWindowViewModel : IVfGraphHost
{
    private readonly Dictionary<string, VfModeSet> virtualModeSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> virtualCurrentModes = new(StringComparer.OrdinalIgnoreCase);

    // ---- modes

    public ObservableCollection<VfModeItem> VirtualFunctionModes { get; } = [];

    [ObservableProperty]
    private string newVirtualModeName = string.Empty;

    [ObservableProperty]
    private string newVirtualModeParent = VfModeItem.None;

    [ObservableProperty]
    private IReadOnlyList<string> virtualModeParentOptions = [VfModeItem.None];

    [ObservableProperty]
    private string virtualFunctionCurrentModeText = string.Empty;

    public bool HasVirtualModes => VirtualFunctionModes.Count > 0;

    public string VirtualModesHeader => VirtualFunctionGame == null ? T("Modes") : T("Modes of {0}", VirtualFunctionGame.Name);

    internal IReadOnlyList<VfMode> ModesOfGame(string game) => virtualModeSets.TryGetValue(game ?? string.Empty, out VfModeSet? set) ? set.Modes : [];

    private void RefreshVirtualModes()
    {
        string game = VirtualFunctionGame?.Id ?? string.Empty;
        VirtualFunctionModes.Clear();
        virtualModeSets.TryGetValue(game, out VfModeSet? set);
        List<string> names = set?.Modes.Select(m => m.Name).ToList() ?? [];
        foreach (VfMode mode in set?.Modes ?? [])
        {
            List<string> parents = [VfModeItem.None, .. names.Where(n => !string.Equals(n, mode.Name, StringComparison.OrdinalIgnoreCase) && !set!.Lineage(n).Contains(mode.Name, StringComparer.OrdinalIgnoreCase))];
            VfModeItem item = new(mode.Name, mode.Parent, string.Equals(mode.Name, set!.Start, StringComparison.OrdinalIgnoreCase), parents)
            {
                IsCurrent = virtualCurrentModes.TryGetValue(game, out string? current) && string.Equals(current, mode.Name, StringComparison.OrdinalIgnoreCase)
            };
            item.ParentChanged = changed => _ = SetVirtualModeParentAsync(changed);
            VirtualFunctionModes.Add(item);
        }
        VirtualModeParentOptions = [VfModeItem.None, .. names];
        if (!VirtualModeParentOptions.Contains(NewVirtualModeParent))
            NewVirtualModeParent = VfModeItem.None;
        VirtualFunctionCurrentModeText = virtualCurrentModes.TryGetValue(game, out string? now) && now.Length > 0 ? T("Mode now (live): {0}", now) : string.Empty;
        OnPropertyChanged(nameof(HasVirtualModes));
        OnPropertyChanged(nameof(VirtualModesHeader));
    }

    [RelayCommand]
    private async Task AddVirtualModeAsync()
    {
        string name = (NewVirtualModeName ?? string.Empty).Trim();
        if (VirtualFunctionGame == null || name.Length == 0)
        {
            VirtualFunctionMessage = T("Type a name for the new mode first.");
            return;
        }
        List<string> args = ["vjoy", "modes", "add", "--game", VirtualFunctionGame.Id, "--name", name];
        if (NewVirtualModeParent != VfModeItem.None && NewVirtualModeParent.Length > 0)
            args.AddRange(["--parent", NewVirtualModeParent]);
        if (await RunModesAsync(args, T("Adding the mode {0}...", name)))
            NewVirtualModeName = string.Empty;
    }

    [RelayCommand]
    private async Task RemoveVirtualModeAsync(VfModeItem? item)
    {
        if (item == null || VirtualFunctionGame == null)
            return;
        if (!await ConfirmAsync(T("Remove the mode {0}?", item.Name), T("Functions that work only in {0} then work in every mode, mode switches lose it as a target, and modes that inherit from it inherit from its parent.", item.Name),
                T("Remove"), isDanger: true))
            return;
        await RunModesAsync(["vjoy", "modes", "remove", "--game", VirtualFunctionGame.Id, "--name", item.Name], T("Removing the mode {0}...", item.Name));
    }

    [RelayCommand]
    private Task SetVirtualStartModeAsync(VfModeItem? item) =>
        item == null || VirtualFunctionGame == null ? Task.CompletedTask : RunModesAsync(["vjoy", "modes", "start", "--game", VirtualFunctionGame.Id, "--name", item.Name], T("Setting the mode at start..."));

    [RelayCommand]
    private Task RenameVirtualModeAsync(VfModeItem? item)
    {
        string name = (item?.EditName ?? string.Empty).Trim();
        if (item == null || VirtualFunctionGame == null || name.Length == 0 || name == item.Name)
            return Task.CompletedTask;
        return RunModesAsync(["vjoy", "modes", "set", "--game", VirtualFunctionGame.Id, "--name", item.Name, "--rename", name], T("Renaming the mode {0}...", item.Name));
    }

    private Task SetVirtualModeParentAsync(VfModeItem item) =>
        VirtualFunctionGame == null ? Task.CompletedTask : RunModesAsync(["vjoy", "modes", "set", "--game", VirtualFunctionGame.Id, "--name", item.Name, "--parent", item.ParentName], T("Changing what {0} inherits...", item.Name));

    private async Task<bool> RunModesAsync(List<string> args, string busy)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: busy, forceText: false);
        bool ok = result != null && result.ExitCode == 0;
        VirtualFunctionMessage = result == null ? string.Empty : ok ? ReadMessage(result.StandardOutput, T("Saved.")) : T("Not changed: {0} {1}", FirstLine(result.StandardError), FirstLine(result.StandardOutput));
        if (ok)
            await RefreshVirtualJoystickAsync();
        return ok;
    }

    // "mode <game> <mode>" of a running forwarder.
    internal void ApplyVirtualModeLive(string line)
    {
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return;
        virtualCurrentModes[parts[1]] = parts[2].Trim();
        if (VirtualFunctionGame?.Id != parts[1])
            return;
        VirtualFunctionCurrentModeText = T("Mode now (live): {0}", parts[2].Trim());
        foreach (VfModeItem item in VirtualFunctionModes)
            item.IsCurrent = string.Equals(item.Name, parts[2].Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- the graph of the game's functions (function chaining)

    // true: the graph; false: the editor of one function.
    [ObservableProperty]
    private bool virtualFunctionShowGraph = true;

    [ObservableProperty]
    private IReadOnlyList<VirtualFunction> virtualFunctionGraph = [];

    public bool VirtualFunctionShowEditor => !VirtualFunctionShowGraph && HasVirtualFunctionEditor;

    public bool VirtualFunctionShowHint => !VirtualFunctionShowGraph && !HasVirtualFunctionEditor;

    partial void OnVirtualFunctionShowGraphChanged(bool value)
    {
        OnPropertyChanged(nameof(VirtualFunctionShowEditor));
        OnPropertyChanged(nameof(VirtualFunctionShowHint));
    }

    private void RefreshVirtualFunctionGraph()
    {
        string game = VirtualFunctionGame?.Id ?? string.Empty;
        VirtualFunctionGraph = VirtualFunctions.Where(f => f.Game == game).Select(f => f.Function).ToList();
        OnPropertyChanged(nameof(VirtualFunctionShowEditor));
        OnPropertyChanged(nameof(VirtualFunctionShowHint));
    }

    [RelayCommand]
    private void ShowVirtualFunctionGraph() => VirtualFunctionShowGraph = true;

    [RelayCommand]
    private void ShowVirtualFunctionEditor() => VirtualFunctionShowGraph = false;

    IReadOnlyList<VfMode> IVfGraphHost.Modes => ModesOfGame(VirtualFunctionGame?.Id ?? string.Empty);

    // Connects a function's output to another function's input (the engine refuses loops; checked here first).
    public async Task<bool> ConnectVirtualFunctionsAsync(string producerId, string outputRole, string consumerId, string inputRole)
    {
        string game = VirtualFunctionGame?.Id ?? string.Empty;
        List<VirtualFunction> functions = VirtualFunctionGraph.Select(f => f.Clone()).ToList();
        VirtualFunction? producer = functions.FirstOrDefault(f => f.Id == producerId);
        VirtualFunction? consumer = functions.FirstOrDefault(f => f.Id == consumerId);
        if (producer == null || consumer == null || game.Length == 0)
            return false;
        VfRoleInfo? role = VirtualFunctionCatalog.ExpandInputs(consumer).FirstOrDefault(r => r.Role == inputRole);
        string kind = VirtualFunctionCatalog.OutputKind(producer, outputRole, functions);
        if (role == null || !VfTokens.Fits(role.Kind, kind, true))
        {
            VirtualFunctionMessage = T("{0} of {1} needs {2}; the output {3} of {4} is {5}.", role?.Label ?? inputRole, consumer.Name, VirtualFunctionCatalog.KindWord(role?.Kind ?? "any"), outputRole, producer.Name, VirtualFunctionCatalog.KindWord(kind));
            return false;
        }
        VfInput input = consumer.InputOf(inputRole) ?? new VfInput { Role = inputRole };
        if (!consumer.Inputs.Contains(input))
            consumer.Inputs.Add(input);
        input.Device = VfChain.Device(producerId);
        input.Input = outputRole.ToUpperInvariant();
        string loop = VfChain.LoopProblem(functions);
        if (loop.Length > 0)
        {
            VirtualFunctionMessage = loop;
            return false;
        }
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "set", "--game", game, "--id", consumerId, "--input", inputRole + "=" + VfChain.Device(producerId) + "|" + outputRole.ToUpperInvariant()],
            includeProfile: true, mutatesProfile: true, busyLabel: T("Connecting {0} to {1}...", producer.Name, consumer.Name), forceText: false);
        bool ok = result != null && result.ExitCode == 0;
        VirtualFunctionMessage = result == null ? string.Empty : ok
            ? T("{0} now reads the output {1} of {2}.", consumer.Name + " " + (role.Label), outputRole, producer.Name)
            : T("Not connected: {0} {1}", FirstLine(result.StandardError), FirstLine(result.StandardOutput));
        if (ok)
            await RefreshVirtualJoystickAsync();
        return ok;
    }

    public async Task<bool> DisconnectVirtualFunctionInputAsync(string consumerId, string inputRole)
    {
        string game = VirtualFunctionGame?.Id ?? string.Empty;
        VirtualFunction? consumer = VirtualFunctionGraph.FirstOrDefault(f => f.Id == consumerId);
        if (consumer == null || game.Length == 0)
            return false;
        EngineCommandResult? result = await RunEngineAsync(["vjoy", "functions", "set", "--game", game, "--id", consumerId, "--input", inputRole + "="],
            includeProfile: true, mutatesProfile: true, busyLabel: T("Disconnecting {0}...", consumer.Name), forceText: false);
        bool ok = result != null && result.ExitCode == 0;
        VirtualFunctionMessage = result == null ? string.Empty : ok ? T("{0}: the input {1} is free again.", consumer.Name, inputRole) : T("Not changed: {0}", FirstLine(result.StandardError));
        if (ok)
            await RefreshVirtualJoystickAsync();
        return ok;
    }

    public Task RemoveVirtualFunctionAsync(string id)
    {
        VirtualFunctionListItem? item = VirtualFunctions.FirstOrDefault(f => f.Id == id && f.Game == VirtualFunctionGame?.Id);
        return item == null ? Task.CompletedTask : DeleteVirtualFunctionAsync(item);
    }

    public void OpenVirtualFunction(string id)
    {
        VirtualFunctionListItem? item = VirtualFunctions.FirstOrDefault(f => f.Id == id && f.Game == VirtualFunctionGame?.Id);
        if (item != null)
            EditVirtualFunction(item);
    }

    // ---- Joystick Gremlin import (a dry run with the report, then the import on the user's click)

    [ObservableProperty]
    private bool gremlinReplace;

    [ObservableProperty]
    private string gremlinReportText = string.Empty;

    [ObservableProperty]
    private string gremlinPendingFile = string.Empty;

    public ObservableCollection<string> GremlinNotImported { get; } = [];

    public ObservableCollection<string> GremlinNotes { get; } = [];

    public bool HasGremlinReport => GremlinPendingFile.Length > 0;

    public bool HasGremlinNotImported => GremlinNotImported.Count > 0;

    public bool HasGremlinNotes => GremlinNotes.Count > 0;

    partial void OnGremlinPendingFileChanged(string value) => OnPropertyChanged(nameof(HasGremlinReport));

    [RelayCommand]
    private async Task PreviewGremlinImportAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (VirtualFunctionGame == null)
        {
            VirtualFunctionMessage = T("Switch the game to vJoy or Xbox pad on the Overview first, then import into it.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(GremlinArgs(path, dryRun: true), includeProfile: true, mutatesProfile: false,
            busyLabel: T("Reading the Joystick Gremlin profile..."), forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            VirtualFunctionMessage = T("The Gremlin profile could not be read: {0} {1}", FirstLine(result?.StandardError), FirstLine(result?.StandardOutput));
            return;
        }
        ApplyGremlinReport(result.StandardOutput, path);
    }

    private List<string> GremlinArgs(string path, bool dryRun)
    {
        List<string> args = ["vjoy", "import-gremlin", path, "--game", VirtualFunctionGame?.Id ?? string.Empty];
        if (GremlinReplace)
            args.Add("--replace");
        if (dryRun)
            args.Add("--dry-run");
        return args;
    }

    internal void ApplyGremlinReport(string output, string path)
    {
        GremlinNotImported.Clear();
        GremlinNotes.Clear();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            List<string> lines = [];
            if (root.TryGetProperty("Devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement device in devices.EnumerateArray())
                {
                    string match = ReadJsonString(device, "Match");
                    lines.Add(T("{0}: {1}", ReadJsonString(device, "GremlinName"), match == "none" ? T("not found - imported on {0}", ReadJsonString(device, "JoyProDevice")) : T("{0} (by {1})", ReadJsonString(device, "JoyProDevice"), match)));
                }
            }
            int functions = root.TryGetProperty("Functions", out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            int modes = root.TryGetProperty("Modes", out JsonElement modeList) && modeList.ValueKind == JsonValueKind.Array ? modeList.GetArrayLength() : 0;
            string summary = T("{0}: {1} function(s), {2} mode(s) for {3}.", ReadJsonString(root, "Format"), functions.ToString(CultureInfo.InvariantCulture), modes.ToString(CultureInfo.InvariantCulture), VirtualFunctionGame?.Name ?? string.Empty);
            GremlinReportText = summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Concat(ReadStringArray(root, "Warnings")));
            foreach (string line in ReadStringArray(root, "NotImported"))
                GremlinNotImported.Add(line);
            foreach (string line in ReadStringArray(root, "Notes"))
                GremlinNotes.Add(line);
            GremlinPendingFile = path;
            VirtualFunctionMessage = string.Empty;
        }
        catch (JsonException)
        {
            VirtualFunctionMessage = T("The engine's answer could not be read.");
            GremlinPendingFile = string.Empty;
        }
        OnPropertyChanged(nameof(HasGremlinNotImported));
        OnPropertyChanged(nameof(HasGremlinNotes));
    }

    [RelayCommand]
    private async Task ConfirmGremlinImportAsync()
    {
        string path = GremlinPendingFile;
        if (path.Length == 0 || VirtualFunctionGame == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(GremlinArgs(path, dryRun: false), includeProfile: true, mutatesProfile: true,
            busyLabel: T("Importing the Joystick Gremlin profile..."), forceText: false);
        bool ok = result != null && result.ExitCode == 0;
        VirtualFunctionMessage = result == null ? string.Empty : ok
            ? ReadMessage(result.StandardOutput, T("Imported.")) + T(" Bind your relations to the vJoy outputs as before, export the game and press Start.")
            : T("Not imported: {0} {1}", FirstLine(result.StandardError), FirstLine(result.StandardOutput));
        if (!ok)
            return;
        GremlinPendingFile = string.Empty;
        await RefreshVirtualJoystickAsync();
        VirtualFunctionShowGraph = true;
    }

    [RelayCommand]
    private void CancelGremlinImport()
    {
        GremlinPendingFile = string.Empty;
        GremlinNotImported.Clear();
        GremlinNotes.Clear();
        OnPropertyChanged(nameof(HasGremlinNotImported));
        OnPropertyChanged(nameof(HasGremlinNotes));
    }

    // ---- the vJoy device JoyPro tunnels into (Settings) as the page sees it

    [ObservableProperty]
    private string virtualJoystickTunnelText = string.Empty;

    [ObservableProperty]
    private string virtualJoystickTunnelWarnings = string.Empty;

    internal void ApplyVirtualTunnelStatus(JsonElement settings)
    {
        int tunnel = ReadJsonInt(settings, "TunnelDevice");
        int first = ReadJsonInt(settings, "FirstDevice");
        VirtualJoystickTunnelWarnings = string.Join(Environment.NewLine, ReadStringArray(settings, "TunnelWarnings"));
        // Without a tunnel device the warning (when virtual mode writes a device) says it all.
        VirtualJoystickTunnelText = tunnel > 0
            ? T("JoyPro tunnels into vJoy {0} (Settings > Games): the table, the functions and the forwarder start there, and imports of games in native mode leave it out.", tunnel.ToString(CultureInfo.InvariantCulture))
            : VirtualJoystickTunnelWarnings.Length > 0 ? string.Empty
            : T("Settings names no vJoy device JoyPro tunnels into: virtual mode starts at vJoy {0}. Choose it in Settings > Games so imports of games in native mode leave it out.", Math.Max(1, first).ToString(CultureInfo.InvariantCulture));
    }
}
