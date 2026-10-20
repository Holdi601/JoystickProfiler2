using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// MSFS 2024 import (docs/games/msfs.md "Per-aircraft binds"): relations are for all aircraft unless the user splits
// them. A bind the sim has only in the profiles of some aircraft comes in for all aircraft; the import result lists
// them (engine "OnlyInAircraft") and the Relations page offers to limit each one, or all, to those aircraft.
// Add-on aircraft actions (actions.tsv aircraft column) show "only for: <aircraft>" in the relation editor's search.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<MsfsAircraftOnlyBind> MsfsAircraftOnlyBinds { get; } = [];

    public bool HasMsfsAircraftOnlyBinds => MsfsAircraftOnlyBinds.Count > 0;

    public string MsfsAircraftOnlyTitle => T("{0} relation(s) were found only in the sim's profiles of some aircraft and are now for all aircraft", MsfsAircraftOnlyBinds.Count);

    // The import's JSON "OnlyInAircraft" with the engine command that limits each one.
    internal static List<MsfsAircraftOnlyBind> ParseMsfsAircraftOnlyBinds(string? output)
    {
        List<MsfsAircraftOnlyBind> binds = [];
        string text = output ?? string.Empty;
        int start = text.IndexOf('{');
        if (start < 0 || !text.Contains("\"OnlyInAircraft\"", StringComparison.Ordinal))
            return binds;
        try
        {
            // The engine's JSON (notes may follow it).
            Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(text[start..]), new JsonReaderOptions { AllowMultipleValues = true });
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            if (!doc.RootElement.TryGetProperty("OnlyInAircraft", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return binds;
            foreach (JsonElement o in list.EnumerateArray())
            {
                string S(string name) => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                List<string> aircraft = o.TryGetProperty("Aircraft", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).Where(e => e.Length > 0).ToList() : [];
                if (S("Relation").Length == 0 || S("ActionId").Length == 0 || aircraft.Count == 0)
                    continue;
                binds.Add(new MsfsAircraftOnlyBind(S("Relation"), S("ActionId"), S("Device"), S("Input"), S("AircraftText"),
                    ["relations", "aircraft", "set", S("Relation"), "--node", (S("Game").Length > 0 ? S("Game") : "MSFS2024") + ":" + S("ActionId"), "--only", string.Join("|", aircraft)]));
            }
        }
        catch (JsonException)
        {
        }
        return binds;
    }

    internal void ShowMsfsAircraftOnlyBinds(string? output)
    {
        MsfsAircraftOnlyBinds.Clear();
        foreach (MsfsAircraftOnlyBind bind in ParseMsfsAircraftOnlyBinds(output))
            MsfsAircraftOnlyBinds.Add(bind);
        OnMsfsAircraftOnlyChanged();
    }

    private void OnMsfsAircraftOnlyChanged()
    {
        OnPropertyChanged(nameof(HasMsfsAircraftOnlyBinds));
        OnPropertyChanged(nameof(MsfsAircraftOnlyTitle));
    }

    [RelayCommand]
    private async Task LimitMsfsAircraftOnlyBindAsync(MsfsAircraftOnlyBind? bind)
    {
        if (bind == null)
            return;
        if (await LimitMsfsAircraftOnlyAsync(bind))
            StatusText = T("'{0}' is now only for {1}. Ctrl+Z undoes it.", bind.Relation, bind.AircraftText);
        await RefreshRelationsAsync();
    }

    [RelayCommand]
    private async Task LimitAllMsfsAircraftOnlyBindsAsync()
    {
        int done = 0;
        foreach (MsfsAircraftOnlyBind bind in MsfsAircraftOnlyBinds.ToList())
        {
            if (await LimitMsfsAircraftOnlyAsync(bind))
                done++;
        }
        StatusText = T("{0} relation(s) limited to the aircraft whose profiles had them.", done);
        await RefreshRelationsAsync();
    }

    [RelayCommand]
    private void DismissMsfsAircraftOnlyBinds()
    {
        MsfsAircraftOnlyBinds.Clear();
        OnMsfsAircraftOnlyChanged();
    }

    private async Task<bool> LimitMsfsAircraftOnlyAsync(MsfsAircraftOnlyBind bind)
    {
        EngineCommandResult? result = await RunEngineAsync(bind.LimitArgs.ToList(), includeProfile: true, mutatesProfile: true, busyLabel: "Limiting '" + bind.Relation + "' to its aircraft...");
        if (result == null || result.ExitCode != 0)
            return false;
        MsfsAircraftOnlyBinds.Remove(bind);
        OnMsfsAircraftOnlyChanged();
        return true;
    }

    // DB\<game>\actions.tsv: action id -> the aircraft keys of its sixth column (an add-on aircraft's own actions).
    internal static Dictionary<string, List<string>> CatalogActionAircraft(string catalogPath)
    {
        Dictionary<string, List<string>> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(catalogPath))
                return map;
            foreach (string line in File.ReadLines(catalogPath))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 6 || parts[0].Trim().Length == 0)
                    continue;
                List<string> aircraft = parts[5].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (aircraft.Count > 0)
                    map[parts[0].Trim()] = aircraft;
            }
        }
        catch (IOException)
        {
        }
        return map;
    }

    // "only for: PMDG 737-800" of a search row whose action exists only for some aircraft (titles from aircraft.tsv).
    private string OnlyForText(string game, string id)
    {
        if (!registryActionAircraft.TryGetValue(NormalizeRelationGameToken(game), out Dictionary<string, List<string>>? map) || !map.TryGetValue(id, out List<string>? keys))
            return string.Empty;
        registryAircraftLists.TryGetValue(NormalizeRelationGameToken(game), out List<RegistryAircraft>? fleet);
        List<string> names = keys.Select(k => fleet?.FirstOrDefault(a => string.Equals(a.Key, k, StringComparison.OrdinalIgnoreCase))?.Title is { Length: > 0 } title ? title : k).ToList();
        return T("only for: {0}", string.Join(", ", names));
    }
}
