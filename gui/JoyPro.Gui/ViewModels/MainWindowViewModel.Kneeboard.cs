using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Kneeboard (Layout page, under the cheat sheets): the cheat sheet of each aircraft as a page of the game's kneeboard
// ("engine layout kneeboard", docs/device-reference.md "Kneeboard"). DCS: Saved Games\DCS and DCS.openbeta\Kneeboard\<aircraft>, which DCS
// and OpenKneeboard's "DCS Aircraft" tab show; or a folder of the user's choice (OpenKneeboard's "Folder" tab, other
// games). The devices, the look and the ticked aircraft are the cheat sheets' (Layout* properties); the engine writes
// only JoyPro's own pages (JoyPro_...) and never changes another file. The options are kept (ui-settings.json "layout").
public sealed partial class MainWindowViewModel
{
    // false: the DCS kneeboard; true: KneeboardFolder.
    [ObservableProperty]
    private bool kneeboardToFolder;

    [ObservableProperty]
    private string kneeboardFolder = string.Empty;

    // true: every aircraft the devices have binds in; false: the aircraft ticked for the cheat sheets.
    [ObservableProperty]
    private bool kneeboardAllAircraft = true;

    // Added to the file names (JoyPro 1's "Kneeboard postfix"), e.g. "_VR" for a second set.
    [ObservableProperty]
    private string kneeboardPostfix = string.Empty;

    // The page height in pixels (the page is 3:4 for DCS).
    [ObservableProperty]
    private string kneeboardSize = "2048";

    // One page per aircraft with all devices under each other, else one page per device.
    [ObservableProperty]
    private bool kneeboardOnePage;

    // The page: a list of the bound controls in large text (default), else the cheat sheet picture.
    [ObservableProperty]
    private bool kneeboardListPage = true;

    // The list page shows the device picture at the top (with a number on each control).
    [ObservableProperty]
    private bool kneeboardPicture = true;

    public bool KneeboardToDcs
    {
        get => !KneeboardToFolder;
        set => KneeboardToFolder = !value;
    }

    partial void OnKneeboardToFolderChanged(bool value) => OnPropertyChanged(nameof(KneeboardToDcs));

    [RelayCommand]
    private async Task ExportKneeboardAsync()
    {
        List<string> args = ["layout", "kneeboard"];
        if (KneeboardToFolder)
        {
            string folder = NormalizeUiPath(KneeboardFolder ?? string.Empty);
            if (folder.Length == 0)
            {
                CheatSheetStatus = T("Pick a folder for the kneeboard pages first.");
                StatusText = CheatSheetStatus;
                return;
            }
            args.AddRange(["--target", "folder", "--folder", folder]);
            // A folder serves any game: the one ticked for the cheat sheets when it is exactly one, else DCS.
            List<string> games = BuildEffectiveLayoutGameFilter();
            if (games.Count == 1)
                args.AddRange(["--game", games[0]]);
        }
        else
        {
            args.AddRange(["--target", "dcs"]);
            foreach (string savedGames in KneeboardDcsFolders(ExportDcsPath))
                args.AddRange(["--saved-games", savedGames]);
        }

        if (!KneeboardAllAircraft)
        {
            List<string> planes = BuildEffectiveLayoutPlaneFilter();
            if (planes.Count == 0)
            {
                CheatSheetStatus = T("Tick the aircraft above, or tick All aircraft.");
                StatusText = CheatSheetStatus;
                return;
            }
            foreach (string plane in planes)
                args.AddRange(["--plane", plane]);
        }

        List<string> selectedDevices = BuildEffectiveLayoutDeviceFilter();
        List<string> missing = [];
        foreach (string device in selectedDevices)
        {
            string layoutPath = await ResolveCheatSheetLayoutAsync(device);
            if (layoutPath.Length == 0)
            {
                missing.Add(device);
                continue;
            }
            args.AddRange(["--device", device, "--layout-map", device + "=" + layoutPath]);
        }
        if (selectedDevices.Count > 0 && missing.Count == selectedDevices.Count)
        {
            CheatSheetStatus = T("No picture with markers yet for: {0}. Pick the device under Device pictures first (or untick it).", string.Join(", ", missing.Select(DeviceDisplayName)));
            StatusText = CheatSheetStatus;
            return;
        }

        string postfix = (KneeboardPostfix ?? string.Empty).Trim();
        if (postfix.Length > 0)
            args.AddRange(["--postfix", postfix]);
        args.AddRange(["--size", ParseOrDefault(KneeboardSize, 2048, 512, 8192).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["--page", KneeboardListPage ? "list" : KneeboardToFolder ? "original" : "portrait"]);
        if (KneeboardListPage)
            args.AddRange(["--picture", KneeboardPicture ? "top" : "none"]);
        if (KneeboardOnePage)
            args.AddRange(["--combine", "true"]);
        args.AddRange(SheetStyleArguments(globalSheetStyle));
        args.AddRange(["--style", LayoutSheetLightStyle ? "light" : "dark"]);
        args.AddRange(DeviceReferenceService.ExportDrawingArguments());

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Putting the pages on the kneeboard..."));
        ShowKneeboardResult(result);
    }

    // The DCS Saved Games folders for the kneeboard: the DCS folder of the Export page and the DCS / DCS.openbeta
    // folders next to it that DCS made (with its Config folder). Empty: the engine finds them itself.
    internal static List<string> KneeboardDcsFolders(string? exportDcsPath)
    {
        List<string> folders = [];
        string chosen = (exportDcsPath ?? string.Empty).Trim();
        if (chosen.Length == 0)
            return folders;
        try
        {
            chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        }
        catch (Exception)
        {
            return folders;
        }
        if (Directory.Exists(chosen))
            folders.Add(chosen);
        string? parent = Path.GetDirectoryName(chosen);
        if (string.IsNullOrEmpty(parent))
            return folders;
        foreach (string name in new[] { "DCS", "DCS.openbeta" })
        {
            string sibling = Path.Combine(parent, name);
            if (Directory.Exists(Path.Combine(sibling, "Config")) && !folders.Contains(sibling, StringComparer.OrdinalIgnoreCase))
                folders.Add(sibling);
        }
        return folders;
    }

    private void ShowKneeboardResult(EngineCommandResult? result)
    {
        if (result == null || result.ExitCode != 0)
        {
            CheatSheetStatus = T("No kneeboard pages were written; see Engine Output for why.");
            return;
        }
        List<string> files = ParseLayoutRenderOutput(result.StandardOutput, out string folder);
        int aircraft = ParseKneeboardAircraftCount(result.StandardOutput);
        LoadCheatSheetPreviews(folder, files);
        CheatSheetStatus = T("Kneeboard pages written: {0} for {1} aircraft. Folder: {2}",
            files.Count.ToString(CultureInfo.InvariantCulture), aircraft.ToString(CultureInfo.InvariantCulture), folder);
        StatusText = CheatSheetStatus;
    }

    // "Aircraft: 3" of the engine's text output.
    internal static int ParseKneeboardAircraftCount(string? output)
    {
        foreach (string raw in (output ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Aircraft:", StringComparison.Ordinal) &&
                int.TryParse(line["Aircraft:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                return count;
        }
        return 0;
    }
}
