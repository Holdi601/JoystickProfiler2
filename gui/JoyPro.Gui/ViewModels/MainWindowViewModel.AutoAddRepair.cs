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
// user had never bound in many relations. After a profile opens, a notice under the bar names it ("JoyPro added 448
// aircraft to 180 relations on 03.11.2026 after the command lists changed. These were not bound by you. Take them out
// again?") with the list per relation and aircraft to untick; "Take them out" is one undo step, "Keep them" stops asking.
// Nothing changes before one of them is clicked.
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

    public string AutoAddRepairListToggleText => IsAutoAddRepairListOpen ? T("Hide the list")
        : T("Show the list ({0} aircraft in {1} relations)", AutoAddRepairItems.Count, AutoAddRepairItems.Select(i => i.Key.Split('\t')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    // The engine's history labels of the load correction's own step and of this repair: the History panel and the
    // check's "Caused by" translate them by these patterns.
    internal static readonly string[] AutoAddHistoryLabels =
    [
        N("Updated to the new command lists: {0} aircraft in {1} relations"),
        N("Updated to the new command lists: {0} aircraft in {1} relation"),
        N("Took out aircraft JoyPro added: {0} aircraft in {1} relations"),
        N("Took out aircraft JoyPro added: {0} aircraft in {1} relation"),
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
        ApplyAutoAddRepair(result == null || result.ExitCode != 0 ? string.Empty : result.StandardOutput);
    }

    // The engine's answer -> the notice and its rows (empty: no notice).
    public void ApplyAutoAddRepair(string output)
    {
        AutoAddRepairItems.Clear();
        AutoAddRepairTitle = string.Empty;
        AutoAddRepairText = string.Empty;
        IsAutoAddRepairListOpen = false;
        if (string.IsNullOrWhiteSpace(output))
            return;
        List<(string Key, string Relation, string Game, string Plane, string Title)> entries = [];
        List<string> dates = [];
        List<(string Relation, List<string> Remove, List<string> Keeps)> relations = [];
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
                    entries.Add((ReadJsonString(e, "Key"), ReadJsonString(e, "Relation"), ReadJsonString(e, "Game"), ReadJsonString(e, "Plane"), ReadJsonString(e, "Title")));
            }
            if (root.TryGetProperty("Relations", out JsonElement rels) && rels.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in rels.EnumerateArray())
                    relations.Add((ReadJsonString(r, "Relation"), ReadStringArray(r, "Remove"), ReadStringArray(r, "Keeps")));
            }
        }
        catch (JsonException)
        {
            return;
        }
        if (entries.Count == 0)
            return;
        foreach (var entry in entries.OrderBy(e => e.Relation, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Plane, StringComparer.OrdinalIgnoreCase))
        {
            string plane = AircraftWords(entry.Game.Length > 0 ? entry.Game : "DCS", [entry.Plane]);
            AutoAddRepairItems.Add(new DuplicateChangeItem
            {
                Key = AutoAddRowKey(entry.Relation, entry.Key),
                Text = entry.Title.Length > 0 ? T("'{0}': {1} ('{2}')", entry.Relation, plane, DisplayText.ActionTitle(entry.Title)) : T("'{0}': {1}", entry.Relation, plane)
            });
        }
        int relationCount = entries.Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string when = string.Join(", ", dates.Where(d => d.Length > 0).Distinct());
        AutoAddRepairTitle = when.Length > 0
            ? T("JoyPro added {0} aircraft to {1} relations on {2} after the command lists changed.", entries.Count, relationCount, when)
            : T("JoyPro added {0} aircraft to {1} relations after the command lists changed.", entries.Count, relationCount);
        // The relation that got the most, as the example ("'HELO POWER BOTH' would be back to AH-64D ... and Ka-50 III").
        var example = relations.OrderByDescending(r => r.Remove.Count).FirstOrDefault();
        string sentence = T("These were not bound by you, and an export would give them a second bind. Take them out again? Untick any you want to keep.");
        if (example.Relation != null && example.Keeps.Count > 0)
            sentence += " " + T("E.g. '{0}' goes back to {1} ({2} taken out).", example.Relation, PlaneNamesShort("DCS", example.Keeps), example.Remove.Count);
        AutoAddRepairText = sentence;
        OnPropertyChanged(nameof(AutoAddRepairListToggleText));
        StatusText = AutoAddRepairTitle;
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
            removed = ReadJsonInt(doc.RootElement, "Removed");
        }
        catch (JsonException)
        {
        }
        DismissAutoAddRepair();
        RefreshHistoryState();
        await RefreshRelationsAsync();
        if (SelectedRelation != null)
            await ReloadBuilderRelationAsync(SelectedRelation.Name);
        StatusText = T("Took out {0} aircraft JoyPro had added. Ctrl+Z undoes it.", removed);
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
    }
}
