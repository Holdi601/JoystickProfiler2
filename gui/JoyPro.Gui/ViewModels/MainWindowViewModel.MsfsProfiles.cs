using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.ViewModels;

// MSFS 2024 export: JoyPro writes the binds into the profile the sim uses for each device and category (Steam remote
// folder or the Microsoft Store save; docs/games/msfs.md "Writing your own MSFS 2024 profiles"), and says per device
// which profile got them or why the import file has them instead.
public sealed partial class MainWindowViewModel
{
    // On: the export writes into the user's own MSFS 2024 profiles (engine default; off: --user-profiles false).
    [ObservableProperty]
    private bool msfsWriteUserProfiles = true;

    // On: import files for every device and category (--import-files), not only where the profile could not be written.
    [ObservableProperty]
    private bool msfsAlwaysImportFiles;

    // The aircraft entry of a node that means the whole game (its id, or MSFS's id before 2020 and 2024 were split).
    internal static bool IsAllAircraftScope(string game, string plane) =>
        string.Equals(game, plane, StringComparison.OrdinalIgnoreCase) ||
        (game.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase) && string.Equals(plane, "MSFS", StringComparison.OrdinalIgnoreCase));

    // The Export page's games include MSFS 2024 (its option shows in step 3).
    public bool ExportsMsfs2024 => ExportGames.Any(g => string.Equals(g.GameId, "MSFS2024", StringComparison.OrdinalIgnoreCase));

    public bool SelectedRegistryGameIsMsfs2024 => string.Equals(SelectedRegistryGame?.GameId, "MSFS2024", StringComparison.OrdinalIgnoreCase);

    // One line per device and category of the last MSFS 2024 export.
    public ObservableCollection<string> MsfsProfileUpdates { get; } = new();

    public bool HasMsfsProfileUpdates => MsfsProfileUpdates.Count > 0;

    internal void AddMsfsUserProfileOptions(List<string> args)
    {
        if (!SelectedRegistryGameIsMsfs2024)
            return;
        if (!MsfsWriteUserProfiles)
            args.AddRange(["--user-profiles", "false"]);
        if (MsfsAlwaysImportFiles)
            args.Add("--import-files");
    }

    internal void ShowMsfsProfileUpdates(string? output)
    {
        MsfsProfileUpdates.Clear();
        foreach (string line in MsfsProfileUpdateLines(output))
            MsfsProfileUpdates.Add(line);
        OnPropertyChanged(nameof(HasMsfsProfileUpdates));
    }

    internal sealed record MsfsProfileUpdate(string Device, string Category, string Profile, string Status, string Reason, int Binds, IReadOnlyList<string> NotWritten, string Aircraft = "");

    private static readonly Regex ProfileTextLine = new(@"^\s*-\s*(?<device>.+?) \((?<category>General|Airplanes)\): (?<status>updated|unchanged|dry-run|import-file)(?: '(?<profile>.*?)'(?: \((?<aircraft>.*)\))?)?, (?<binds>\d+) bind\(s\)(?: - (?<reason>.*))?$", RegexOptions.CultureInvariant);

    // The per-device results of an export (its JSON "ProfileUpdates" or the text "Profiles:" section).
    internal static List<MsfsProfileUpdate> ParseMsfsProfileUpdates(string? output)
    {
        List<MsfsProfileUpdate> updates = [];
        string text = output ?? string.Empty;
        int start = text.IndexOf('{');
        if (start >= 0 && text.Contains("\"ProfileUpdates\"", StringComparison.Ordinal))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text[start..]);
                if (doc.RootElement.TryGetProperty("ProfileUpdates", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement u in list.EnumerateArray())
                    {
                        string S(string name) => u.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                        int binds = u.TryGetProperty("Binds", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
                        List<string> missing = u.TryGetProperty("NotWritten", out JsonElement n) && n.ValueKind == JsonValueKind.Array
                            ? n.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).ToList() : [];
                        updates.Add(new MsfsProfileUpdate(S("Device"), S("Category"), S("Profile"), S("Status"), S("Reason"), binds, missing, S("Aircraft")));
                    }
                }
                return updates;
            }
            catch (JsonException)
            {
            }
        }
        bool inProfiles = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Profiles:", StringComparison.Ordinal))
            {
                inProfiles = true;
                continue;
            }
            if (!inProfiles)
                continue;
            Match match = ProfileTextLine.Match(line);
            if (!match.Success)
            {
                if (!line.StartsWith("  ", StringComparison.Ordinal))
                    inProfiles = false;
                continue;
            }
            updates.Add(new MsfsProfileUpdate(match.Groups["device"].Value, match.Groups["category"].Value, match.Groups["profile"].Value, match.Groups["status"].Value,
                match.Groups["reason"].Value, int.Parse(match.Groups["binds"].Value, CultureInfo.InvariantCulture), [], match.Groups["aircraft"].Value));
        }
        // Binds that did not land: the text output's warnings.
        List<string> notWritten = Regex.Matches(text, @"MSFS 2024 '(?<device>[^']+)': not written: (?<what>[^\r\n]+?)\.?\r?$", RegexOptions.Multiline)
            .Select(m => m.Groups["device"].Value + ": " + m.Groups["what"].Value).ToList();
        if (notWritten.Count > 0 && updates.Count > 0)
            updates[0] = updates[0] with { NotWritten = notWritten };
        return updates;
    }

    // "VPC VMAX Prime Throttle (General): your profile 'Iain's VMAX' updated: 3 binds" and so on, in the UI language.
    internal static List<string> MsfsProfileUpdateLines(string? output)
    {
        List<string> lines = [];
        foreach (MsfsProfileUpdate u in ParseMsfsProfileUpdates(output))
        {
            string category = u.Category == "General" ? T("General") : T("Airplanes");
            // MSFS 2024 keeps a profile per device and aircraft: the line names the aircraft that use it.
            if (u.Aircraft.Length > 0 && u.Status is "updated" or "unchanged" or "dry-run")
            {
                lines.Add(u.Status switch
                {
                    "updated" => T("{0} ({1}): your profile '{2}' ({3}) updated: {4} bind(s)", u.Device, category, u.Profile, u.Aircraft, u.Binds),
                    "unchanged" => T("{0} ({1}): your profile '{2}' ({3}) already has the {4} bind(s)", u.Device, category, u.Profile, u.Aircraft, u.Binds),
                    _ => T("{0} ({1}): your profile '{2}' ({3}) would be updated (dry run)", u.Device, category, u.Profile, u.Aircraft)
                });
                foreach (string missing in u.NotWritten)
                    lines.Add(T("Not written: {0}", missing));
                continue;
            }
            lines.Add(u.Status switch
            {
                "updated" => T("{0} ({1}): your profile '{2}' (all your aircraft) updated: {3} bind(s)", u.Device, category, u.Profile, u.Binds),
                "unchanged" => T("{0} ({1}): your profile '{2}' (all your aircraft) already has the {3} bind(s)", u.Device, category, u.Profile, u.Binds),
                "dry-run" => T("{0} ({1}): your profile '{2}' (all your aircraft) would be updated (dry run)", u.Device, category, u.Profile),
                _ => T("{0} ({1}): not written into your profile, the import file has the binds: {2}", u.Device, category, Translate(u.Reason))
            });
            foreach (string missing in u.NotWritten)
                lines.Add(T("Not written: {0}", missing));
        }
        return lines;
    }
}
