using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The repair for profiles an earlier load correction filled up (docs/relation-builder.md "Load correction", engine
// profile auto-added): up to 2.6.1 "Auto Add DB Items" switched on, after Refresh DB or a JoyPro update, aircraft the
// user had never bound in many relations. After a profile opens, a notice under the bar names it ("JoyPro switched on
// 426 aircraft you did not choose, in 167 relations, on 03.11.2026.") with the list per relation and aircraft to untick;
// "Take them out" is one undo step, "Keep them" stops asking. Nothing changes before one of them is clicked.
//
// Input layers such as "Default" are no aircraft (EngineDcsExport.IsNonAircraftLayer: an export writes them into the
// layer's own folder, never into a module). They are not counted and come last in the list; a profile with nothing else
// gets no notice at all, and its entries stay as they are (the engine command still lists them).
public sealed partial class MainWindowViewModel
{
    // One row per relation and aircraft, ticked = taken out.
    public ObservableCollection<DuplicateChangeItem> AutoAddRepairItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoAddRepairNotice))]
    private string autoAddRepairTitle = string.Empty;

    // What it means and an example relation.
    [ObservableProperty]
    private string autoAddRepairText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoAddRepairListToggleText))]
    private bool isAutoAddRepairListOpen;

    public bool HasAutoAddRepairNotice => AutoAddRepairTitle.Length > 0;

    // The real aircraft and their relations (the list also has the input layers).
    private int autoAddRepairAircraft;
    private int autoAddRepairRelations;

    public string AutoAddRepairListToggleText => IsAutoAddRepairListOpen ? T("Hide the list")
        : autoAddRepairRelations == 1 ? T("Show the list ({0} aircraft in {1} relation)", autoAddRepairAircraft, autoAddRepairRelations)
        : T("Show the list ({0} aircraft in {1} relations)", autoAddRepairAircraft, autoAddRepairRelations);

    // The engine's history labels of the load correction's own step and of this repair: the History panel and the
    // check's "Caused by" translate them by these patterns.
    internal static readonly string[] AutoAddHistoryLabels =
    [
        N("Updated to the new command lists: {0} aircraft in {1} relations"),
        N("Updated to the new command lists: {0} aircraft in {1} relation"),
        N("Took out aircraft JoyPro added: {0} aircraft in {1} relations"),
        N("Took out aircraft JoyPro added: {0} aircraft in {1} relation"),
        N("Took out input layers JoyPro added: {0} in {1} relations"),
        N("Took out input layers JoyPro added: {0} in {1} relation"),
        N("Kept the aircraft JoyPro added")
    ];

    // Keys of the rows (relation<TAB>engine key).
    private static string AutoAddRowKey(string relation, string key) => relation + "\t" + key;

    // After a profile opened: the engine reads the profile history (read-only).
    internal async Task CheckAutoAddRepairAsync()
    {
        if (!HasProfile)
            return;
        EngineCommandResult? result = await RunEngineAsync(["profile", "auto-added"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking for aircraft JoyPro added by itself..."), forceText: false, background: true);
        // The list names the aircraft as the modules do; the relation list's load of those names may still be on its way.
        if (result != null && result.ExitCode == 0 && RelationListItem.AircraftNames.Count == 0)
        {
            MediaIndex index = await GetMediaIndexAsync();
            if (index.AircraftNames.Count > 0)
                RelationListItem.AircraftNames = new Dictionary<string, string>(index.AircraftNames, StringComparer.OrdinalIgnoreCase);
        }
        ApplyAutoAddRepair(result == null || result.ExitCode != 0 ? string.Empty : result.StandardOutput);
    }

    // The engine's answer -> the notice and its rows (empty: no notice).
    public void ApplyAutoAddRepair(string output)
    {
        AutoAddRepairItems.Clear();
        AutoAddRepairTitle = string.Empty;
        AutoAddRepairText = string.Empty;
        IsAutoAddRepairListOpen = false;
        autoAddRepairAircraft = 0;
        autoAddRepairRelations = 0;
        if (string.IsNullOrWhiteSpace(output))
            return;
        List<(string Key, string Relation, string Game, string Plane, string Title, bool NonAircraft)> entries = [];
        List<string> dates = [];
        List<(string Relation, List<string> Keeps)> relations = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            if (!ReadJsonBool(root, "Found"))
                return;
            if (root.TryGetProperty("Steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in steps.EnumerateArray())
                {
                    string utc = ReadJsonString(s, "Utc");
                    dates.Add(DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at)
                        ? at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
                        : ReadJsonString(s, "Local"));
                }
            }
            if (root.TryGetProperty("Entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in list.EnumerateArray())
                    entries.Add((ReadJsonString(e, "Key"), ReadJsonString(e, "Relation"), ReadJsonString(e, "Game"), ReadJsonString(e, "Plane"), ReadJsonString(e, "Title"), ReadJsonBool(e, "NonAircraft")));
            }
            if (root.TryGetProperty("Relations", out JsonElement rels) && rels.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in rels.EnumerateArray())
                    relations.Add((ReadJsonString(r, "Relation"), ReadStringArray(r, "KeepsAircraft")));
            }
        }
        catch (JsonException)
        {
            return;
        }
        // Only input layers: nothing an export could put in a module, and nothing to ask.
        var aircraft = entries.Where(e => !e.NonAircraft).ToList();
        if (aircraft.Count == 0)
            return;
        foreach (var entry in entries.OrderBy(e => e.NonAircraft).ThenBy(e => e.Relation, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Plane, StringComparer.OrdinalIgnoreCase))
        {
            string plane = entry.NonAircraft ? T("{0} (not an aircraft)", InputLayerName(entry.Plane)) : AircraftWords(entry.Game.Length > 0 ? entry.Game : "DCS", [entry.Plane]);
            // The command's name when it says more than the relation's own name.
            string title = entry.Title.Length > 0 ? DisplayText.ActionTitle(entry.Title) : string.Empty;
            AutoAddRepairItems.Add(new DuplicateChangeItem
            {
                Key = AutoAddRowKey(entry.Relation, entry.Key),
                Text = title.Length > 0 && !SameWords(title, entry.Relation) ? T("'{0}': {1}, command '{2}'", entry.Relation, plane, title) : T("'{0}': {1}", entry.Relation, plane)
            });
        }
        autoAddRepairAircraft = aircraft.Count;
        autoAddRepairRelations = aircraft.Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string when = string.Join(", ", dates.Where(d => d.Length > 0).Distinct());
        AutoAddRepairTitle = autoAddRepairRelations == 1
            ? (when.Length > 0
                ? T("JoyPro switched on {0} aircraft you did not choose, in {1} relation, on {2}.", autoAddRepairAircraft, autoAddRepairRelations, when)
                : T("JoyPro switched on {0} aircraft you did not choose, in {1} relation.", autoAddRepairAircraft, autoAddRepairRelations))
            : (when.Length > 0
                ? T("JoyPro switched on {0} aircraft you did not choose, in {1} relations, on {2}.", autoAddRepairAircraft, autoAddRepairRelations, when)
                : T("JoyPro switched on {0} aircraft you did not choose, in {1} relations.", autoAddRepairAircraft, autoAddRepairRelations));
        string sentence = T("When the command lists changed, an older version of JoyPro added every aircraft that shares a command with one of your actions. It no longer does that. An export would bind these actions in those aircraft too. Take them out removes only these; your own binds stay. Untick any you want to keep.");
        // The relation that got the most and has aircraft of its own, as the example.
        var example = aircraft.Where(e => e.Game.Length == 0 || e.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Relation, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Relation: g.Key, Loses: g.Select(e => e.Plane).ToList(), Keeps: relations.FirstOrDefault(r => r.Relation.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Keeps ?? []))
            .Where(r => r.Keeps.Count > 0)
            .OrderBy(r => LooksLikeAnId(r.Relation)).ThenByDescending(r => r.Loses.Count).ThenBy(r => r.Relation, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (example.Relation != null)
            sentence += " " + T("For example, '{0}' stays on {1} and loses {2}.", example.Relation, PlaneNamesShort("DCS", example.Keeps), PlaneNamesShort("DCS", example.Loses));
        if (aircraft.Count < entries.Count)
            sentence += " " + T("The entries marked 'not an aircraft' at the end of the list are input layers of DCS, such as Default. They are ticked like the others.");
        AutoAddRepairText = sentence;
        OnPropertyChanged(nameof(AutoAddRepairListToggleText));
        StatusText = AutoAddRepairTitle;
    }

    // DCS's input layers in words instead of their folder names (names, not translated).
    private static string InputLayerName(string layer) => layer.ToLowerInvariant() switch
    {
        "uilayer" => "UI Layer",
        "cameraobject" => "Camera Object",
        "camerafree" => "Camera Free",
        "cameraroom" => "Camera Room",
        "commandmenu" => "Command Menu",
        "quadmenu" => "Quad Menu",
        "radiovoip" => "Radio VoIP",
        "voicechat" => "Voice Chat",
        "ground_unit" => "Ground Unit",
        "trackir" => "TrackIR",
        "headtracker" => "Head Tracker",
        _ => layer
    };

    // A relation an import named after the game's id ("Sound_On_Off__Keyboard__S__d122pnil...__button"): no example.
    private static bool LooksLikeAnId(string relation) => relation.Contains("__", StringComparison.Ordinal) || relation.Contains("pnil", StringComparison.OrdinalIgnoreCase);

    // "F1 - Cockpit view" and "F1 Cockpit view": the same words.
    private static bool SameWords(string a, string b)
    {
        static string Letters(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Letters(a) == Letters(b);
    }

    [RelayCommand]
    private void ToggleAutoAddRepairList() => IsAutoAddRepairListOpen = !IsAutoAddRepairListOpen;

    [RelayCommand]
    private void TickAllAutoAddRepair()
    {
        bool all = AutoAddRepairItems.All(i => i.IsTicked);
        foreach (DuplicateChangeItem item in AutoAddRepairItems)
            item.IsTicked = !all;
    }

    // "Take them out": every ticked aircraft back to "not in the action", one profile version (Ctrl+Z).
    [RelayCommand]
    internal async Task TakeOutAutoAddedAsync()
    {
        List<string> skip = AutoAddRepairItems.Where(i => !i.IsTicked).Select(i => i.Key[(i.Key.LastIndexOf('\t') + 1)..]).ToList();
        if (skip.Count == AutoAddRepairItems.Count)
        {
            StatusText = T("Tick at least one aircraft, or choose Keep these aircraft.");
            return;
        }
        List<string> args = ["profile", "auto-added", "--apply"];
        if (skip.Count > 0)
            args.AddRange(["--skip", string.Join(";", skip)]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
            busyLabel: N("Taking out the aircraft JoyPro added..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        int removed = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            removed = ReadJsonInt(doc.RootElement, "Removed") - ReadJsonInt(doc.RootElement, "RemovedNonAircraft");
        }
        catch (JsonException)
        {
        }
        DismissAutoAddRepair();
        RefreshHistoryState();
        await RefreshRelationsAsync();
        if (SelectedRelation != null)
            await ReloadBuilderRelationAsync(SelectedRelation.Name);
        StatusText = removed > 0 ? T("Took out {0} aircraft JoyPro had added. Ctrl+Z undoes it.", removed) : T("Took out the input layers JoyPro had added. Ctrl+Z undoes it.");
    }

    // "Keep them": nothing changes in the relations; the profile remembers the answer and JoyPro does not ask again.
    [RelayCommand]
    internal async Task KeepAutoAddedAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["profile", "auto-added", "--keep"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Keeping the aircraft JoyPro added..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        DismissAutoAddRepair();
        RefreshHistoryState();
        StatusText = T("Kept the aircraft as they are. JoyPro does not ask about them again.");
    }

    // "Later": hidden until the profile opens again.
    [RelayCommand]
    private void DismissAutoAddRepair()
    {
        AutoAddRepairTitle = string.Empty;
        AutoAddRepairText = string.Empty;
        IsAutoAddRepairListOpen = false;
        AutoAddRepairItems.Clear();
        autoAddRepairAircraft = 0;
        autoAddRepairRelations = 0;
    }
}
