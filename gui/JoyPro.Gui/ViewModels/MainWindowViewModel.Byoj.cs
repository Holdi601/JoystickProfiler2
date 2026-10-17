using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// VTOL VR's BYOJoystick mod (docs/games/vtol-vr.md "BYOJoystick mod", docs/components.md): JoyPro's "Install
// BYOJoystick" on the VTOL VR card. The mod has no license (all rights reserved by its author) and its only official
// binary source is the Steam Workshop, so JoyPro neither ships nor copies it: after the user's consent (what it is, the
// source, the size Steam lists, the license situation) JoyPro backs up the user's BYOJ_<seat>.xml files and opens Steam
// on the Mod Loader (when missing) and the Workshop item, where the user subscribes. Steam downloads, checks and installs
// it where the Mod Loader expects it. "Check again" finds it, shows the SHA-256 of BYOJoystick.dll and builds JoyPro's
// target list from the installed mod (vtolvr refresh-db).
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool byojPanelVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallByojCommand))]
    private bool byojConsent;

    [ObservableProperty]
    private bool byojInstalled;

    [ObservableProperty]
    private bool byojModLoaderInstalled;

    [ObservableProperty]
    private string byojStatus = string.Empty;

    [ObservableProperty]
    private string byojDetails = string.Empty;

    [ObservableProperty]
    private string byojLicense = string.Empty;

    [ObservableProperty]
    private string byojSource = string.Empty;

    [ObservableProperty]
    private string byojSize = string.Empty;

    // Opens the panel and reads what is on this PC (engine vtolvr mod status; nothing is changed).
    [RelayCommand]
    private async Task OpenByojAsync(GameCardItem? card)
    {
        ByojPanelVisible = true;
        ByojConsent = false;
        await ReadByojStatusAsync();
    }

    [RelayCommand]
    private void CloseByoj()
    {
        ByojPanelVisible = false;
        ByojConsent = false;
    }

    private bool CanInstallByoj() => ByojConsent;

    // After the consent: back up the BYOJ files, then let Steam install the Mod Loader and the mod (the user subscribes
    // on the Workshop page). The consent is used up: a second install asks again.
    [RelayCommand(CanExecute = nameof(CanInstallByoj))]
    private async Task InstallByojAsync()
    {
        if (!ByojConsent)
            return;
        ByojConsent = false;
        EngineCommandResult? result = await RunEngineAsync(["vtolvr", "mod", "prepare", "--accept-license"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Preparing the BYOJoystick install..."), forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            ByojStatus = T("Could not prepare the install: {0}", (result?.StandardOutput ?? string.Empty).Trim());
            return;
        }
        List<string> open = [];
        int backedUp = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("OpenUrls", out JsonElement urls) && urls.ValueKind == JsonValueKind.Array)
                open.AddRange(urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString() ?? string.Empty).Where(u => u.StartsWith("steam://", StringComparison.Ordinal)));
            if (doc.RootElement.TryGetProperty("BackedUp", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                backedUp = files.GetArrayLength();
        }
        catch (JsonException)
        {
            ByojStatus = T("The engine's answer could not be read.");
            return;
        }
        foreach (string url in open)
        {
            try
            {
                ExternalProcess.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                ByojStatus = T("Steam could not be opened ({0}). Open {1} yourself.", ex.Message, ByojSource);
                return;
            }
        }
        ByojStatus = T("{0}{1}When Steam has downloaded it, press Check again.", (backedUp > 0 ? T("Your {0} BYOJ file(s) are backed up (Export > Game config backups). ", backedUp.ToString(CultureInfo.InvariantCulture)) : string.Empty), (open.Count == 0 ? T("BYOJoystick is already installed. ") : T("Steam opened: {0}press Subscribe on the BYO Joystick page. ", (open.Any(u => u.Contains("install", StringComparison.Ordinal)) ? T("install the VTOL VR Mod Loader, then ") : string.Empty))));
    }

    // Reads the status again; once the mod is there, builds the target list from it.
    [RelayCommand]
    private async Task CheckByojAsync()
    {
        bool wasInstalled = ByojInstalled;
        await ReadByojStatusAsync();
        if (!ByojInstalled)
            return;
        EngineCommandResult? refresh = await RunEngineAsync(["vtolvr", "refresh-db"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Reading BYOJoystick's targets..."), forceText: true);
        if (refresh != null && refresh.ExitCode == 0)
        {
            InvalidateDbCaches();
            RebuildRelationNodeGameOptions();
            ByojStatus = T("{0} JoyPro's VTOL VR command list was built from it.", (wasInstalled ? T("BYOJoystick is installed.") : T("BYOJoystick is installed now.")));
        }
        else
            ByojStatus = T("BYOJoystick is installed, but its targets could not be read: {0}", (refresh?.StandardOutput ?? string.Empty).Trim());
    }

    private async Task ReadByojStatusAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["vtolvr", "mod", "status"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Looking for BYOJoystick..."), forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            ByojStatus = T("Could not read the mod's status.");
            return;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            ByojInstalled = root.TryGetProperty("ModInstalled", out JsonElement installed) && installed.ValueKind == JsonValueKind.True;
            ByojModLoaderInstalled = root.TryGetProperty("ModLoaderInstalled", out JsonElement loader) && loader.ValueKind == JsonValueKind.True;
            ByojLicense = ReadJsonString(root, "License");
            ByojSource = ReadJsonString(root, "WorkshopPage");
            long size = root.TryGetProperty("ListedSize", out JsonElement listed) && listed.TryGetInt64(out long value) ? value : 0;
            ByojSize = size > 0 ? T("{0} MB (as Steam lists it)", (size / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture)) : string.Empty;
            string sha = ReadJsonString(root, "DllSha256");
            string dll = ReadJsonString(root, "DllPath");
            int targets = root.TryGetProperty("Targets", out JsonElement t) && t.TryGetInt32(out int count) ? count : 0;
            ByojDetails = ByojInstalled && dll.Length > 0
                ? T("Installed: {0}{1}SHA-256: {2}{3}{4} targets JoyPro can read.{5}{6}", dll, Environment.NewLine, sha, Environment.NewLine, targets.ToString(CultureInfo.InvariantCulture), Environment.NewLine, ReadJsonString(root, "Verification"))
                : T("Mod Loader: {0}; BYOJoystick: not installed", (ByojModLoaderInstalled ? "installed" : T("not installed (free on Steam)")));
            ByojStatus = ByojInstalled ? T("BYOJoystick is installed.") : T("BYOJoystick is not installed.");
        }
        catch (JsonException)
        {
            ByojStatus = T("The mod's status could not be read.");
        }
    }
}
