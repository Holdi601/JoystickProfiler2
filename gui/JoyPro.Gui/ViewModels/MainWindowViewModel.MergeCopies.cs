using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The check's "copied relations" cards (engine relations merge-copies): relations an import copied ("<name>__1",
// "<name>__2", ...) that have the same input as "<name>", e.g. after a DCS import of a device Windows had given new IDs
// over time. The check lists them from a dry run (the card is the preview: which copies, the input, the aircraft);
// "Merge into one" merges a family, "Merge all" every family, each as one profile version (one undo step).
public sealed partial class MainWindowViewModel
{
    private readonly List<CheckIssueCard> copyCards = [];

    // The copy families as cards (one served engine call after the check: a dry run, nothing changes).
    private async Task LoadMergeCopiesIssuesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "merge-copies", "--dry-run"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking for copied relations..."), forceText: false, background: true);
        ApplyMergeCopies(result == null || result.ExitCode != 0 ? string.Empty : result.StandardOutput);
    }

    // The engine's dry run -> one card per relation family.
    public void ApplyMergeCopies(string output)
    {
        copyCards.Clear();
        List<(string Relation, List<string> Copies, string Device, string Input, List<string> Modifiers, List<string> Aircraft)> families = [];
        try
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
                if (doc.RootElement.TryGetProperty("Families", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in list.EnumerateArray())
                        families.Add((ReadJsonString(f, "Relation"), ReadStringArray(f, "Copies"), ReadJsonString(f, "Device"), ReadJsonString(f, "Input"),
                            ReadStringArray(f, "Modifiers"), ReadStringArray(f, "Aircraft")));
                }
            }
        }
        catch (JsonException)
        {
        }

        int totalCopies = families.Sum(f => f.Copies.Count);
        foreach (var family in families.Where(f => f.Relation.Length > 0 && f.Copies.Count > 0))
        {
            string relation = family.Relation;
            string bind = DisplayText.BindLabel(family.Device, family.Input, family.Modifiers);
            List<string> planes = family.Aircraft.Select(a => a.Contains('|') ? a[(a.IndexOf('|') + 1)..] : a).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string game = family.Aircraft.Select(a => a.Contains('|') ? a[..a.IndexOf('|')] : "DCS").FirstOrDefault() ?? "DCS";
            List<string> lines = family.Copies.Take(8).Select(c => "'" + c + "'").ToList();
            if (family.Copies.Count > 8)
                lines.Add(T("... and {0} more", family.Copies.Count - 8));
            string headline = family.Copies.Count == 1
                ? T("1 relation is a copy of '{0}' with the same input", relation)
                : T("{0} relations are copies of '{1}' with the same input", family.Copies.Count, relation);
            string details = T("All of them are on {0}. Merged into one, '{1}' keeps every aircraft ({2}), the groups and the tuning per aircraft.",
                bind.Length > 0 ? bind : DisplayText.ShortDevice(family.Device), relation, planes.Count);
            List<string> names = [relation, .. family.Copies];
            copyCards.Add(new CheckIssueCard
            {
                Kind = "copies",
                Headline = headline,
                Details = details,
                ActionLines = lines,
                Game = game,
                RelationsSort = relation,
                AircraftSort = string.Join(", ", planes),
                SearchText = string.Join("\n", new[] { headline, details, string.Join(" ", family.Copies), string.Join(" ", planes) }),
                Relations = names,
                MergeCopiesText = T("Merge into one"),
                MergeCopiesCommand = new AsyncRelayCommand(() => MergeRelationCopiesAsync(relation, family.Copies.Count)),
                MergeAllCopiesText = families.Count > 1 ? T("Merge all copied relations ({0})", totalCopies) : string.Empty,
                MergeAllCopiesCommand = families.Count > 1 ? new AsyncRelayCommand(() => MergeAllRelationCopiesAsync(totalCopies)) : null,
                OpenAllCommand = new RelayCommand(() => OpenRelations(names))
            });
        }
        checkDataVersion++;
        BuildCheckSections();
    }

    internal async Task MergeRelationCopiesAsync(string relation, int copies)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "merge-copies", "--name", relation], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Merging copied relations..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Merged {0} copies into '{1}'. Ctrl+Z undoes it.", copies, relation);
    }

    internal async Task MergeAllRelationCopiesAsync(int copies)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "merge-copies"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Merging copied relations..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Merged {0} copied relations. Ctrl+Z undoes it.", copies);
    }
}
