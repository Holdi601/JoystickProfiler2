using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Cheat sheets (Layout page): printable pictures of the devices with what every button does ("engine layout render"),
// one overview per device and/or one per aircraft. They never touch a game; that is why they live on the Layout page
// and not on Export. The options are the Layout* properties of the main view model (devices, games, aircraft, look,
// output folder); this part adds the plan summary, the preview gallery and opening the folder or a picture.
public sealed partial class MainWindowViewModel
{
    private const int CheatSheetPreviewLimit = 48;

    public ObservableCollection<CheatSheetPreviewItem> CheatSheetPreviews { get; } = [];

    [ObservableProperty]
    private string cheatSheetStatus = "Pick devices and what to make, then press Create cheat sheets.";

    // The folder of the last render (the engine's default folder when no output folder is set).
    [ObservableProperty]
    private string lastCheatSheetFolder = string.Empty;

    // Sheets with tables and lines: the white page (else JoyPro 1's grey page), and one page per aircraft with all
    // devices under each other.
    [ObservableProperty]
    private bool layoutSheetLightStyle;

    [ObservableProperty]
    private bool layoutCombinePages;

    public bool HasCheatSheetPreviews => CheatSheetPreviews.Count > 0;

    public bool HasNoCheatSheetPreviews => CheatSheetPreviews.Count == 0;

    public string CheatSheetFolderDisplay
    {
        get
        {
            string folder = ResolveCheatSheetFolder();
            return folder.Length == 0 ? T("Default folder: LayoutExport next to JoyPro's engine") : folder;
        }
    }

    public string CheatSheetPlanSummary =>
        DescribeCheatSheetPlan(
            BuildEffectiveLayoutDeviceFilter().Count,
            BuildEffectiveLayoutGameFilter().Count,
            BuildEffectiveLayoutPlaneFilter().Count,
            LayoutIncludeOverview,
            LayoutIncludePlanes);

    // "2 devices · an overview of each · one per aircraft (3 aircraft) → 8 pictures". 0 devices/games/aircraft = all.
    public static string DescribeCheatSheetPlan(int devices, int games, int planes, bool overview, bool perAircraft)
    {
        if (!overview && !perAircraft)
            return T("Tick an overview or one picture per aircraft.");
        List<string> parts =
        [
            devices == 0 ? T("every device with a picture") : devices == 1 ? "1 device" : devices.ToString(CultureInfo.InvariantCulture) + " devices"
        ];
        if (overview)
            parts.Add(devices == 1 ? T("an overview") : T("an overview of each"));
        if (perAircraft)
        {
            string scope = planes > 0
                ? planes.ToString(CultureInfo.InvariantCulture) + " aircraft"
                : games > 0
                    ? T("every aircraft of {0}{1}", games.ToString(CultureInfo.InvariantCulture), (games == 1 ? " game" : " games"))
                    : T("every aircraft of every game");
            parts.Add(T("one per aircraft ({0})", scope));
        }
        string text = string.Join(" · ", parts);
        if (devices > 0 && (!perAircraft || planes > 0))
        {
            int count = devices * ((overview ? 1 : 0) + (perAircraft ? planes : 0));
            text += " → " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " picture" : " pictures");
        }
        return text;
    }

    private void NotifyCheatSheetPlan() => OnPropertyChanged(nameof(CheatSheetPlanSummary));

    // The layout file a cheat sheet of the device is drawn on: the remembered one (the user's own or the picked one),
    // else the one the engine makes for a device the device catalog knows (JoyPro's drawing with a marker on every
    // input, no shipped pictures needed). "" = none.
    private async Task<string> ResolveCheatSheetLayoutAsync(string device)
    {
        bool hasRemembered = TryResolveRememberedLayoutPathForDevice(device, out string remembered) && File.Exists(remembered);
        // The user's own layout (JoyPro 1, his picture) first; then the tables and lines of the device's picture
        // (the Layout page's callout sheet); then whatever was picked or what the engine makes.
        if (hasRemembered && !IsGeneratedLayoutPath(remembered))
            return remembered;
        // The sheet on screen is what the user expects to print (as the layout editor, it is saved before the export).
        if (layoutCallouts?.HasSheet == true && deviceReference != null && SameDeviceName(deviceReference.DeviceName, device))
            SaveLayoutCalloutsFile(markSaved: true);
        if (DeviceReferenceService.CalloutFileOf(device) is { } callouts)
            return callouts;
        if (hasRemembered)
            return remembered;
        EngineCommandResult? result = await RunEngineAsync(["layout", "suggest", "--device", device, "--apply", "false"], includeProfile: false, mutatesProfile: false,
            busyLabel: "Finding a picture of " + DeviceDisplayName(device) + "...", forceText: false);
        if (result?.ExitCode != 0 || !TryParseLayoutSuggestions(result.StandardOutput, out List<LayoutSuggestionItem> found))
            return string.Empty;
        LayoutSuggestionItem? item = found.FirstOrDefault(s => SameDevice(s.Device, device) && s.HasLayout && File.Exists(s.LayoutPath));
        if (item == null)
            return string.Empty;
        RememberLayoutDeviceMapping(device, item.LayoutPath);
        return item.LayoutPath;
    }

    partial void OnLayoutIncludeOverviewChanged(bool value) => NotifyCheatSheetPlan();

    partial void OnLayoutIncludePlanesChanged(bool value) => NotifyCheatSheetPlan();

    partial void OnLayoutExportPathChanged(string value) => OnPropertyChanged(nameof(CheatSheetFolderDisplay));

    partial void OnLastCheatSheetFolderChanged(string value) => OnPropertyChanged(nameof(CheatSheetFolderDisplay));

    private string ResolveCheatSheetFolder()
    {
        string folder = NormalizeUiPath(LayoutExportPath ?? string.Empty);
        return folder.Length > 0 ? folder : NormalizeUiPath(LastCheatSheetFolder ?? string.Empty);
    }

    // The files "engine layout render" wrote ("Written Files:" list of its text output) and its export folder.
    public static List<string> ParseLayoutRenderOutput(string? output, out string folder)
    {
        folder = string.Empty;
        List<string> files = [];
        bool inFiles = false;
        foreach (string raw in (output ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Export Path:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["Export Path:".Length..].Trim();
                folder = value == "<none>" ? string.Empty : value;
                inFiles = false;
                continue;
            }
            if (line.StartsWith("Written Files:", StringComparison.OrdinalIgnoreCase))
            {
                inFiles = true;
                continue;
            }
            if (inFiles && line.StartsWith("  - ", StringComparison.Ordinal))
            {
                string file = line[4..].Trim();
                if (file.Length > 0)
                    files.Add(file);
                continue;
            }
            inFiles = false;
        }
        return files;
    }

    private void ShowCheatSheetResult(EngineCommandResult? result)
    {
        if (result == null || result.ExitCode != 0)
        {
            CheatSheetStatus = T("No cheat sheets were made; see Engine Output for why.");
            return;
        }
        List<string> files = ParseLayoutRenderOutput(result.StandardOutput, out string folder);
        if (folder.Length > 0)
            LastCheatSheetFolder = folder;
        LoadCheatSheetPreviews(folder, files);
        CheatSheetStatus = files.Count == 0
            ? T("The engine made no pictures. Check that the devices have a picture with markers (Device pictures).")
            : T("Made {0}{1}{2}.", files.Count.ToString(CultureInfo.InvariantCulture), (files.Count == 1 ? " cheat sheet" : " cheat sheets"), (folder.Length > 0 ? T(" in {0}", folder) : string.Empty));
    }

    private void LoadCheatSheetPreviews(string folder, IEnumerable<string> files)
    {
        foreach (CheatSheetPreviewItem old in CheatSheetPreviews)
            old.Thumbnail?.Dispose();
        CheatSheetPreviews.Clear();
        foreach (string file in files.Where(File.Exists).Take(CheatSheetPreviewLimit))
            CheatSheetPreviews.Add(new CheatSheetPreviewItem(file, Path.GetFileNameWithoutExtension(file), CheatSheetSubtitle(folder, file), TryLoadThumbnail(file)));
        OnPropertyChanged(nameof(HasCheatSheetPreviews));
        OnPropertyChanged(nameof(HasNoCheatSheetPreviews));
    }

    // "Overview" for a picture in the folder itself, else the sub folders: "DCS · F-16C_50".
    public static string CheatSheetSubtitle(string folder, string file)
    {
        string directory = Path.GetDirectoryName(file) ?? string.Empty;
        if (folder.Length == 0 || directory.Length == 0)
            return T("Overview");
        string relative = Path.GetRelativePath(folder, directory);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
            return T("Overview");
        return string.Join(" · ", relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
    }

    private static Bitmap? TryLoadThumbnail(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 360);
        }
        catch (Exception)
        {
            // No picture decoder (unit tests) or not a picture: the card shows an icon instead.
            return null;
        }
    }

    // The newest pictures of the output folder (also the ones of earlier sessions).
    [RelayCommand]
    private void RefreshCheatSheetPreviews() => RefreshCheatSheetPreviewsFromFolder(force: true);

    private void RefreshCheatSheetPreviewsFromFolder(bool force = false)
    {
        NotifyCheatSheetPlan();
        if (!force && CheatSheetPreviews.Count > 0)
            return;
        string folder = ResolveCheatSheetFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            if (force)
                CheatSheetStatus = folder.Length == 0 ? T("No cheat sheets made yet.") : T("The folder does not exist yet: {0}", folder);
            return;
        }
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(CheatSheetPreviewLimit)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CheatSheetStatus = T("Could not read {0}: {1}", folder, ex.Message);
            return;
        }
        LoadCheatSheetPreviews(folder, files);
        if (force || files.Count > 0)
            CheatSheetStatus = files.Count == 0 ? T("No cheat sheets in {0} yet.", folder) : T("{0} cheat sheet(s) in {1}.", files.Count.ToString(CultureInfo.InvariantCulture), folder);
    }

    [RelayCommand]
    private void OpenCheatSheetFolder()
    {
        string folder = ResolveCheatSheetFolder();
        if (folder.Length == 0)
        {
            CheatSheetStatus = T("Make cheat sheets first, or pick an output folder.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            CheatSheetStatus = T("The folder does not exist yet: {0}", folder);
            return;
        }
        OpenExternal(folder);
    }

    [RelayCommand]
    private void OpenCheatSheet(CheatSheetPreviewItem? item)
    {
        if (item == null || !File.Exists(item.FilePath))
            return;
        OpenExternal(item.FilePath);
    }
}
