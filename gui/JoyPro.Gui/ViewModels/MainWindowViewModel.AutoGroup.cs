using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// "Auto Group": groups the same function of all DCS aircraft into relations (engine relations auto-group),
// from the manual-verified function catalog plus the style learned from the user's own relations.
public sealed partial class MainWindowViewModel
{
    public sealed record AutoGroupPreset(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<AutoGroupPreset> AutoGroupPresets { get; } =
    [
        new("all", "Same function together (all aircraft)"),
        new("wing", "Split fixed wing / helicopters"),
        new("era", "Split by wing type and era"),
        new("purist", "Purist: only basic flight controls shared"),
    ];

    [ObservableProperty]
    private bool isAutoGroupPanelOpen;

    [ObservableProperty]
    private AutoGroupPreset? selectedAutoGroupPreset;

    [ObservableProperty]
    private bool autoGroupIncludeSubstitutes = true;

    [ObservableProperty]
    private bool autoGroupReplace;

    [ObservableProperty]
    private string autoGroupPlanes = string.Empty;

    [ObservableProperty]
    private string autoGroupPreview = string.Empty;

    [RelayCommand]
    private void ToggleAutoGroupPanel()
    {
        IsAutoGroupPanelOpen = !IsAutoGroupPanelOpen;
        SelectedAutoGroupPreset ??= AutoGroupPresets[0];
    }

    // The last preview (engine relations auto-group --dry-run, JSON) and the arguments it was made with: Apply asks with
    // its counts without running the preview again.
    private AutoGroupSummary? autoGroupLastPreview;
    private string autoGroupLastPreviewKey = string.Empty;

    [RelayCommand]
    private async Task PreviewAutoGroupAsync() => await RunAutoGroupPreviewAsync();

    private async Task<AutoGroupSummary?> RunAutoGroupPreviewAsync()
    {
        List<string> args = AutoGroupArguments(dryRun: true);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Previewing Auto Group..."), forceText: false);
        if (result == null)
            return null;
        if (result.ExitCode != 0)
        {
            AutoGroupPreview = T("Auto Group could not run: {0}", (result.StandardError + result.StandardOutput).Trim());
            return null;
        }
        AutoGroupSummary? summary = AutoGroupSummary.Parse(ExtractJsonPayload(result.StandardOutput));
        if (summary == null)
        {
            AutoGroupPreview = result.StandardOutput.Trim();
            return null;
        }
        AutoGroupPreview = summary.Describe(preview: true);
        autoGroupLastPreview = summary;
        autoGroupLastPreviewKey = string.Join(" ", args) + "|" + ProfilePath;
        return summary;
    }

    // Apply asks first with what will change (the preview's counts; a preview runs first when there is none for these
    // settings), then changes the profile in one step (one undo step).
    [RelayCommand]
    private async Task ApplyAutoGroupAsync()
    {
        AutoGroupSummary? plan = autoGroupLastPreview != null && autoGroupLastPreviewKey == string.Join(" ", AutoGroupArguments(dryRun: true)) + "|" + ProfilePath
            ? autoGroupLastPreview
            : await RunAutoGroupPreviewAsync();
        if (plan != null)
        {
            if (plan.Created == 0 && plan.Extended == 0 && plan.Removed == 0)
            {
                StatusText = T("Auto Group: nothing to add.");
                return;
            }
            if (!await ConfirmAsync(T("Group all my actions automatically?"), plan.Question(), T("Apply")))
                return;
        }
        EngineCommandResult? result = await RunEngineAsync(
            AutoGroupArguments(dryRun: false),
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Auto grouping relations..."),
            forceText: false);
        autoGroupLastPreview = null;
        autoGroupLastPreviewKey = string.Empty;
        if (result == null)
            return;
        if (result.ExitCode != 0)
        {
            AutoGroupPreview = T("Auto Group could not run: {0}", (result.StandardError + result.StandardOutput).Trim());
            return;
        }
        AutoGroupSummary? done = AutoGroupSummary.Parse(ExtractJsonPayload(result.StandardOutput));
        AutoGroupPreview = done?.Describe(preview: false) ?? result.StandardOutput.Trim();
        if (done != null)
            StatusText = T("Auto Group: {0}. Ctrl+Z undoes it.", done.Headline());
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
    }

    // engine relations auto-group (JSON) in plain words: what is new, which of the user's relations get more, what was
    // kept where it is.
    internal sealed class AutoGroupSummary
    {
        public int Created { get; private init; }
        public int Extended { get; private init; }
        public int Removed { get; private init; }
        public int Nodes { get; private init; }
        public int Aircraft { get; private init; }
        public List<(string Name, bool IsAxis, bool Existing, int Added, List<string> Aircraft)> Relations { get; } = [];
        public List<string> Kept { get; } = [];
        public List<string> Warnings { get; } = [];

        public static AutoGroupSummary? Parse(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("RelationsCreated", out _))
                    return null;
                AutoGroupSummary s = new()
                {
                    Created = Int(root, "RelationsCreated"),
                    Extended = Int(root, "RelationsExtended"),
                    Removed = Int(root, "RelationsRemoved"),
                    Nodes = Int(root, "NodesAdded"),
                    Aircraft = Int(root, "AircraftCovered")
                };
                if (root.TryGetProperty("Relations", out JsonElement relations) && relations.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement r in relations.EnumerateArray())
                    {
                        List<string> planes = r.TryGetProperty("Aircraft", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                            ? a.EnumerateArray().Select(p => p.GetString() ?? string.Empty).Where(p => p.Length > 0).ToList()
                            : [];
                        s.Relations.Add((Str(r, "Name"), r.TryGetProperty("IsAxis", out JsonElement ax) && ax.ValueKind == JsonValueKind.True,
                            r.TryGetProperty("Existing", out JsonElement ex) && ex.ValueKind == JsonValueKind.True, Int(r, "NodesAdded"), planes));
                    }
                }
                foreach (string name in new[] { "Kept", "MeaningNotes" })
                {
                    if (root.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                        s.Kept.AddRange(list.EnumerateArray().Select(k => k.GetString() ?? string.Empty).Where(k => k.Length > 0));
                }
                foreach (string name in new[] { "Warnings", "Errors" })
                {
                    if (root.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                        s.Warnings.AddRange(list.EnumerateArray().Select(k => k.GetString() ?? string.Empty).Where(k => k.Length > 0));
                }
                return s;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ", StringComparison.Ordinal);

        // "526 new relations, 1 of yours gets more, 11 449 actions for 141 aircraft and games"
        public string Headline()
        {
            List<string> parts = [];
            if (Removed > 0)
                parts.Add(T("{0} earlier Auto Group relations replaced", N(Removed)));
            parts.Add(N(Created) + (Created == 1 ? " new relation" : " new relations"));
            if (Extended > 0)
                parts.Add(N(Extended) + (Extended == 1 ? " of yours gets more" : " of yours get more"));
            parts.Add(T("{0} actions for {1} aircraft and games", N(Nodes), N(Aircraft)));
            return string.Join(", ", parts);
        }

        public string Question()
        {
            StringBuilder sb = new();
            sb.Append(Headline()).Append('.');
            List<string> yours = Relations.Where(r => r.Existing).Select(r => "'" + r.Name + "' +" + r.Added).Take(8).ToList();
            if (yours.Count > 0)
                sb.Append(T(" Your relations that get more: ")).Append(string.Join(", ", yours)).Append(Extended > yours.Count ? " ..." : string.Empty).Append('.');
            sb.Append(T(" Binds stay as they are; Ctrl+Z undoes it all."));
            return sb.ToString();
        }

        // The preview / result as a readable list.
        public string Describe(bool preview)
        {
            StringBuilder sb = new();
            sb.AppendLine((preview ? "Preview, nothing changed yet: " : "Done: ") + Headline() + ".");
            List<(string Name, bool IsAxis, bool Existing, int Added, List<string> Aircraft)> yours = Relations.Where(r => r.Existing).ToList();
            if (yours.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(T("Your relations that get more:"));
                foreach (var r in yours.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("  " + Line(r));
            }
            List<(string Name, bool IsAxis, bool Existing, int Added, List<string> Aircraft)> created = Relations.Where(r => !r.Existing).ToList();
            if (created.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(T("New relations ({0}):", N(created.Count)));
                foreach (var r in created.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("  " + Line(r));
            }
            if (Kept.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(T("Left where they are ({0}):", N(Kept.Count)));
                foreach (string k in Kept.Take(40))
                    sb.AppendLine("  " + k);
                if (Kept.Count > 40)
                    sb.AppendLine(T("  ... and {0} more", N(Kept.Count - 40)));
            }
            foreach (string w in Warnings.Distinct().Take(10))
                sb.AppendLine(T("Note: {0}", w));
            return sb.ToString().TrimEnd();
        }

        // "Pitch (axis): +13 actions - FSX, MSFS2020, MSFS2024 +4 more"
        private static string Line((string Name, bool IsAxis, bool Existing, int Added, List<string> Aircraft) r)
        {
            string where = r.Aircraft.Count <= 4 ? string.Join(", ", r.Aircraft) : T("{0} +{1} more", string.Join(", ", r.Aircraft.Take(3)), (r.Aircraft.Count - 3));
            return r.Name + " (" + (r.IsAxis ? "axis" : "button") + "): +" + r.Added + (r.Added == 1 ? " action" : " actions") + (where.Length > 0 ? " \u2014 " + where : string.Empty);
        }
    }

    private List<string> AutoGroupArguments(bool dryRun)
    {
        List<string> args = ["relations", "auto-group", "--preset", (SelectedAutoGroupPreset ?? AutoGroupPresets[0]).Key];
        if (!AutoGroupIncludeSubstitutes)
            args.Add("--no-substitutes");
        if (AutoGroupReplace)
            args.Add("--replace");
        if (!string.IsNullOrWhiteSpace(AutoGroupPlanes))
        {
            args.Add("--planes");
            args.Add(AutoGroupPlanes.Trim());
        }
        if (dryRun)
            args.Add("--dry-run");
        return args;
    }
}
