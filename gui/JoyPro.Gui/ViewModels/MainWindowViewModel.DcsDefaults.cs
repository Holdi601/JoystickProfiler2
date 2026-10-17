using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Settings > DCS default binds (docs/games/dcs.md, "Default binds in the DCS install"): the two optional changes to
// the DCS installation (engine dcs strip-defaults / dcs device-luas). Both are off by default: JoyPro's exports
// already remove DCS's default binds of every device they write. The engine keeps the switches (so a DCS list
// refresh can re-apply them after a DCS update); the GUI reads them with "status" and re-applies with "auto" at start.
public sealed partial class MainWindowViewModel
{
    // The switches as the engine has them (status); a change by the user asks first and runs the engine.
    [ObservableProperty]
    private bool dcsStripDefaults;

    [ObservableProperty]
    private bool dcsStripSkipUiLayer;

    [ObservableProperty]
    private bool dcsRemoveDeviceFiles;

    [ObservableProperty]
    private string dcsDefaultsStatus = T("Not checked yet.");

    // A warning that applies right now (DCS updated and something is not applied, clean profiles stale).
    [ObservableProperty]
    private string dcsDefaultsAttention = string.Empty;

    public ObservableCollection<string> DcsDefaultsPreviewFiles { get; } = [];

    [ObservableProperty]
    private string dcsDefaultsPreviewTitle = string.Empty;

    public bool HasDcsDefaultsAttention => DcsDefaultsAttention.Length > 0;

    public bool HasDcsDefaultsPreview => DcsDefaultsPreviewFiles.Count > 0 || DcsDefaultsPreviewTitle.Length > 0;

    // The same text is in MainWindow.axaml next to the switch.
    public static string DcsIntegrityWarning =>
        T("Changes DCS's own files. Multiplayer servers that check file integrity (pure scripts / IC) do not let you in while it is on; Restore puts DCS's files back. A DCS update or repair brings the original files back, JoyPro then switches it on again at its next start and before each DCS list update.");

    // Set while the switches are filled from the engine, so filling them does not run anything.
    private bool loadingDcsDefaults;

    partial void OnDcsDefaultsAttentionChanged(string value) => OnPropertyChanged(nameof(HasDcsDefaultsAttention));

    partial void OnDcsStripDefaultsChanged(bool value)
    {
        if (!loadingDcsDefaults)
            _ = SetDcsStripDefaultsAsync(value);
    }

    partial void OnDcsRemoveDeviceFilesChanged(bool value)
    {
        if (!loadingDcsDefaults)
            _ = SetDcsRemoveDeviceFilesAsync(value);
    }

    private List<string> DcsInstallArgs(params string[] head)
    {
        List<string> args = ["dcs", .. head];
        if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            args.AddRange(["--install-path", DcsInstallPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--saved-games", DcsSavedGamesPath.Trim()]);
        return args;
    }

    [RelayCommand]
    private async Task RefreshDcsDefaultsStatusAsync()
    {
        if (!IsDcsInstalled)
        {
            DcsDefaultsStatus = T("DCS World is not installed on this PC.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(DcsInstallArgs("strip-defaults", "status"), includeProfile: false, mutatesProfile: false,
            busyLabel: T("Checking DCS's default-bind files..."), forceText: false, background: true);
        ApplyDcsDefaultsResult(result);
    }

    // Once at start (after the DB check): re-applies a switched-on option a DCS update undid. Silent when nothing is on.
    internal async Task AutoApplyDcsDefaultsAsync()
    {
        if (!IsDcsInstalled)
            return;
        EngineCommandResult? result = await RunEngineAsync(DcsInstallArgs("strip-defaults", "auto"), includeProfile: false, mutatesProfile: false,
            busyLabel: T("Checking DCS's default-bind files..."), forceText: false, background: true);
        ApplyDcsDefaultsResult(result);
        if (result != null && result.ExitCode == 0 && TryReadDcsDefaults(result.StandardOutput, out DcsDefaultsInfo info) &&
            (info.Patched > 0 || info.DeviceFilesMoved > 0))
            StatusText = T("DCS was updated: JoyPro switched its default binds off again ({0} files patched, {1} device files moved).", info.Patched, info.DeviceFilesMoved);
    }

    [RelayCommand]
    private async Task PreviewDcsStripDefaultsAsync()
    {
        List<string> args = DcsInstallArgs("strip-defaults", "apply", "--dry-run");
        if (DcsStripSkipUiLayer)
            args.AddRange(["--skip-ui-layer", "true"]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: T("Listing DCS's default-bind files..."), forceText: false);
        if (result == null || !TryReadDcsDefaults(result.StandardOutput, out DcsDefaultsInfo info))
            return;
        ShowDcsDefaultsPreview(info.Files.Count == 0
            ? T("Nothing to change: all {0} input files of DCS are already without default binds.", info.AlreadyPatched)
            : T("{0} input files of DCS would lose their default binds ({1} already done):", info.Files.Count, info.AlreadyPatched), info.Files);
    }

    [RelayCommand]
    private async Task PreviewDcsDeviceFilesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(DcsInstallArgs("device-luas", "list"), includeProfile: false, mutatesProfile: false, busyLabel: T("Listing DCS's device default files..."), forceText: false);
        if (result == null || !TryReadDcsDefaults(result.StandardOutput, out DcsDefaultsInfo info))
            return;
        ShowDcsDefaultsPreview(info.Files.Count == 0
            ? (info.DeviceFilesBackedUp > 0 ? T("No device-specific default files in DCS ({0} kept by JoyPro).", info.DeviceFilesBackedUp) : T("No device-specific default files in DCS."))
            : T("{0} device-specific default files would move to JoyPro's backup folder:", info.Files.Count), info.Files);
    }

    private void ShowDcsDefaultsPreview(string title, IEnumerable<string> files)
    {
        DcsDefaultsPreviewTitle = title;
        DcsDefaultsPreviewFiles.Clear();
        foreach (string file in files)
            DcsDefaultsPreviewFiles.Add(file);
        OnPropertyChanged(nameof(HasDcsDefaultsPreview));
    }

    // Does again what is switched on (after a DCS update the original files are back).
    [RelayCommand]
    private async Task ApplyDcsInstallDefaultsNowAsync()
    {
        await RunDcsDefaultsChangeAsync(DcsInstallArgs("strip-defaults", "auto"), T("Switching DCS's default binds off again..."), rebuildClean: true);
    }

    [RelayCommand]
    private async Task RestoreDcsInstallDefaultsAsync()
    {
        if (!await ConfirmAsync(T("Put DCS's default binds back?"),
                T("JoyPro puts DCS's original input files back (and the device default files it moved) and stops switching them off after updates. Close DCS first."),
                T("Restore DCS files")))
            return;
        await RunDcsDefaultsChangeAsync(DcsInstallArgs("strip-defaults", "restore"), T("Restoring DCS's input files..."));
        await RunDcsDefaultsChangeAsync(DcsInstallArgs("device-luas", "restore"), T("Putting DCS's device default files back..."), rebuildClean: true);
    }

    private async Task SetDcsStripDefaultsAsync(bool on)
    {
        bool ok = on
            ? await ConfirmAsync(T("Switch DCS's default binds off in the DCS install?"),
                T("Every input file of DCS loses its default binds, for all devices (also those JoyPro does not write). Each file is kept as <file>.nocombo.bak.") + " " +
                DcsIntegrityWarning + " " + T("Close DCS first."),
                T("Switch off"), isDanger: true)
            : await ConfirmAsync(T("Put DCS's default binds back?"), T("DCS's original input files come back from their .nocombo.bak copies. Close DCS first."), T("Restore"));
        if (!ok)
        {
            SetDcsDefaultsSwitches(!on, DcsRemoveDeviceFiles);
            return;
        }
        List<string> args = DcsInstallArgs("strip-defaults", on ? "apply" : "restore");
        if (on)
            args.AddRange(["--skip-ui-layer", DcsStripSkipUiLayer ? "true" : "false"]);
        await RunDcsDefaultsChangeAsync(args, on ? T("Switching DCS's default binds off...") : T("Restoring DCS's input files..."));
    }

    private async Task SetDcsRemoveDeviceFilesAsync(bool on)
    {
        bool ok = on
            ? await ConfirmAsync(T("Move DCS's device default files away?"),
                T("DCS's own default files for single devices (\"Joystick - HOTAS Warthog.lua\" and similar) move into JoyPro's backup folder, so DCS uses the general default.lua for every device. JoyPro then rebuilds its DCS list once (a few minutes, DCS is not started). Close DCS first."),
                T("Move files"), isDanger: true)
            : await ConfirmAsync(T("Put DCS's device default files back?"), T("The files come back from JoyPro's backup folder; JoyPro then rebuilds its DCS list once. Close DCS first."), T("Put back"));
        if (!ok)
        {
            SetDcsDefaultsSwitches(DcsStripDefaults, !on);
            return;
        }
        await RunDcsDefaultsChangeAsync(DcsInstallArgs("device-luas", on ? "remove" : "restore"),
            on ? T("Moving DCS's device default files...") : T("Putting DCS's device default files back..."), rebuildClean: true);
    }

    // Runs one change; with rebuildClean the clean profiles are built again when the engine says they no longer match.
    private async Task RunDcsDefaultsChangeAsync(List<string> args, string busyLabel, bool rebuildClean = false)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: busyLabel, forceText: false);
        ApplyDcsDefaultsResult(result);
        if (result == null || result.ExitCode != 0 || !TryReadDcsDefaults(result.StandardOutput, out DcsDefaultsInfo info))
            return;
        if (rebuildClean && info.CleanProfilesStale)
        {
            EngineCommandResult? clean = await RunEngineAsync(DcsCleanRefreshArguments(), includeProfile: false, mutatesProfile: false,
                busyLabel: T("Building the DCS clean profiles from the game's default binds..."));
            if (clean != null && clean.ExitCode == 0)
                await RefreshDcsDefaultsStatusAsync();
        }
    }

    private void SetDcsDefaultsSwitches(bool strip, bool deviceFiles)
    {
        loadingDcsDefaults = true;
        try
        {
            DcsStripDefaults = strip;
            DcsRemoveDeviceFiles = deviceFiles;
        }
        finally
        {
            loadingDcsDefaults = false;
        }
    }

    private void ApplyDcsDefaultsResult(EngineCommandResult? result)
    {
        if (result == null)
            return;
        if (!TryReadDcsDefaults(result.StandardOutput, out DcsDefaultsInfo info))
        {
            if (result.ExitCode != 0)
                DcsDefaultsStatus = T("DCS's default-bind files: {0}", Loc.Translate(FirstLine(result.StandardError, result.StandardOutput)));
            return;
        }
        SetDcsDefaultsSwitches(info.StripEnabled, info.RemoveDeviceLuas);
        loadingDcsDefaults = true;
        try
        {
            DcsStripSkipUiLayer = info.SkipUiLayer;
        }
        finally
        {
            loadingDcsDefaults = false;
        }
        DcsDefaultsStatus = DescribeDcsDefaults(info);
        DcsDefaultsAttention = info.Errors.Count > 0
            ? string.Join(" ", info.Errors.Select(Loc.Translate))
            : info.CleanProfilesStale
                ? T("The device default files of DCS changed since JoyPro's DCS list was built: update the DCS list (Start page, DCS card) so exports remove every DCS default bind.")
                : info.NeedsReapply
                    ? T("DCS brought some of its default-bind files back (update or repair): JoyPro switches them off again at its next start, or press Apply now.")
                    : string.Empty;
    }

    internal static string DescribeDcsDefaults(DcsDefaultsInfo info)
    {
        string strip = info.StripEnabled
            ? (info.SkipUiLayer
                ? T("Default binds switched off in {0} of {1} DCS input files (UI layer kept).", info.AlreadyPatched, info.ProfileFiles)
                : T("Default binds switched off in {0} of {1} DCS input files.", info.AlreadyPatched, info.ProfileFiles))
            : info.AlreadyPatched > 0
                ? T("{0} DCS input files are still without default binds (switched off by JoyPro or the strip script): Restore puts them back.", info.AlreadyPatched)
                : T("DCS's input files are unchanged ({0} files).", info.ProfileFiles);
        string devices = info.RemoveDeviceLuas
            ? T("Device default files: {0} kept by JoyPro, {1} in DCS.", info.DeviceFilesBackedUp, info.DeviceFilesInInstall)
            : info.DeviceFilesBackedUp > 0
                ? T("{0} device default files are still in JoyPro's backup folder.", info.DeviceFilesBackedUp)
                : T("Device default files: {0} in DCS, unchanged.", info.DeviceFilesInInstall);
        string version = info.DcsVersion.Length > 0 ? " " + T("DCS {0}.", info.DcsVersion) : string.Empty;
        return strip + " " + devices + version;
    }

    internal sealed class DcsDefaultsInfo
    {
        public bool StripEnabled { get; set; }
        public bool SkipUiLayer { get; set; }
        public bool RemoveDeviceLuas { get; set; }
        public bool CleanProfilesStale { get; set; }
        public bool NeedsReapply { get; set; }
        public int ProfileFiles { get; set; }
        public int Patched { get; set; }
        public int AlreadyPatched { get; set; }
        public int DeviceFilesInInstall { get; set; }
        public int DeviceFilesMoved { get; set; }
        public int DeviceFilesBackedUp { get; set; }
        public string DcsVersion { get; set; } = string.Empty;
        public List<string> Files { get; } = [];
        public List<string> Errors { get; } = [];
    }

    internal static bool TryReadDcsDefaults(string output, out DcsDefaultsInfo info)
    {
        info = new DcsDefaultsInfo();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ProfileFiles", out _))
                return false;
            info.StripEnabled = TryGetJsonBool(root, "StripEnabled", false);
            info.SkipUiLayer = TryGetJsonBool(root, "SkipUiLayer", false);
            info.RemoveDeviceLuas = TryGetJsonBool(root, "RemoveDeviceLuas", false);
            info.CleanProfilesStale = TryGetJsonBool(root, "CleanProfilesStale", false);
            info.NeedsReapply = TryGetJsonBool(root, "NeedsReapply", false);
            info.ProfileFiles = JsonInt(root, "ProfileFiles");
            info.Patched = JsonInt(root, "Patched");
            info.AlreadyPatched = JsonInt(root, "AlreadyPatched");
            info.DeviceFilesInInstall = JsonInt(root, "DeviceFilesInInstall");
            info.DeviceFilesMoved = JsonInt(root, "DeviceFilesMoved");
            info.DeviceFilesBackedUp = JsonInt(root, "DeviceFilesBackedUp");
            info.DcsVersion = GetJsonString(root, "DcsVersion");
            foreach (string key in new[] { "Files", "Errors" })
            {
                if (!root.TryGetProperty(key, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        (key == "Files" ? info.Files : info.Errors).Add(item.GetString() ?? string.Empty);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int JsonInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }

    private static string FirstLine(params string[] texts)
    {
        foreach (string text in texts)
        {
            string line = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            if (line.Length > 0)
                return line;
        }
        return T("the engine gave no answer.");
    }
}
