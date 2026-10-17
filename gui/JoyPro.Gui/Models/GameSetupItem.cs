using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.Models;

// One game of the game setup (first start, or Settings > Games > Run setup again): whether JoyPro found it, the
// user's "I have this game", a linked installation folder (portable installs, other drives) checked against the
// game's marker files like the engine checks them, and the config location where the engine lets the user set it.
// Nothing is written until Done (MainWindowViewModel.ApplyGameSetupAsync).
public sealed partial class GameSetupItem : ObservableObject
{
    public GameSetupItem(GameInstallItem install)
    {
        Install = install;
        OriginalLink = install.Overridden ? install.InstallPath : string.Empty;
        OriginalConfigLink = install.ConfigOverride;
        OriginalHasGame = !install.Disabled;
        hasGame = !install.Disabled && (install.Installed || install.Overridden);
        InitiallyTicked = hasGame;
        linkedFolder = OriginalLink;
        configLink = OriginalConfigLink;
    }

    public GameInstallItem Install { get; }

    public string GameId => Install.GameId;

    public string Name => Install.Name;

    public string OriginalLink { get; }

    public string OriginalConfigLink { get; }

    // Not marked as "I do not have this game" before the setup.
    public bool OriginalHasGame { get; }

    // Ticked when the setup opened: the game was found or linked (and not hidden before).
    public bool InitiallyTicked { get; }

    // "I have this game": ticked for the games JoyPro found (or the user linked). Unticking one of those hides the
    // game on Done; a game JoyPro did not find stays unticked and merely "not installed" (found once it is installed),
    // unless the user also ticks "I don't have it".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHideChoice))]
    private bool hasGame;

    // "I don't have it" for a game JoyPro did not find: hide it on Done.
    [ObservableProperty]
    private bool explicitlyHidden;

    public bool ShowHideChoice => !HasGame && !InitiallyTicked && OriginalHasGame;

    // Done stores the game as "I don't have this game" (engine games install disable).
    public bool HideOnDone => OriginalHasGame && !HasGame && (InitiallyTicked || ExplicitlyHidden);

    // Done brings a hidden game back (engine games install enable).
    public bool ShowOnDone => !OriginalHasGame && HasGame;

    // The user's installation folder, or empty for detection.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink), nameof(InstallText), nameof(MarkerStatus), nameof(MarkerOk), nameof(MarkerProblem), nameof(HasMarkerStatus), nameof(ConfigText))]
    private string linkedFolder;

    // The user's config location (file or folder), or empty for detection.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigLink), nameof(ConfigText))]
    private string configLink;

    public bool HasLink => LinkedFolder.Length > 0;

    public bool HasConfigLink => ConfigLink.Length > 0;

    public bool LinkChanged => !string.Equals(LinkedFolder, OriginalLink, StringComparison.OrdinalIgnoreCase);

    public bool ConfigLinkChanged => !string.Equals(ConfigLink, OriginalConfigLink, StringComparison.OrdinalIgnoreCase);

    public bool Detected => Install.Detected;

    public string DetectedLabel => Install.Overridden ? T("Linked by you") : Detected ? T("Found") : T("Not found");

    public bool ShowFound => !Install.Overridden && Detected;

    public bool ShowLinked => Install.Overridden;

    public bool ShowNotFound => !Install.Overridden && !Detected;

    public string DetectedText => Install.Overridden
        ? T("Your folder: {0}", Install.InstallPath)
        : Detected
            ? T("{0}  ·  found via {1}", Install.InstallPath, Install.Source)
            : T("JoyPro did not find it on this PC. If you have it (portable install, other drive), link its folder.");

    public string InstallText => HasLink
        ? T("{0}  (your folder)", LinkedFolder)
        : Detected ? T("{0}  (detected)", Install.InstallPath) : T("Not found: link its folder with Browse..., or JoyPro keeps it blocked.");

    // The linked folder checked like the engine does: one of the game's marker files (or folders) must be in it.
    public string MarkerStatus
    {
        get
        {
            if (!HasLink || !LinkChanged)
                return HasLink && Install.Overridden ? (Install.MarkerFound ? T("Marker found: the folder is the game's installation.") : MarkerMissingText()) : string.Empty;
            if (!Directory.Exists(LinkedFolder) && !File.Exists(LinkedFolder))
                return T("The folder does not exist.");
            // A picked .exe, launcher folder or folder inside the game counts as the game's folder (like the engine).
            string resolved = GameInstallFolder.Resolve(Install.Markers, LinkedFolder);
            string? marker = FoundMarker(resolved);
            if (marker == null)
                return MarkerMissingText();
            return string.Equals(resolved.TrimEnd('\\'), LinkedFolder.Trim().Trim('"').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? T("Marker found: {0}.", marker)
                : T("Marker found: {0} in {1} (JoyPro uses that folder).", marker, resolved);
        }
    }

    public bool HasMarkerStatus => MarkerStatus.Length > 0;

    public bool MarkerOk => MarkerStatus.StartsWith("Marker found", StringComparison.Ordinal);

    public bool MarkerProblem => HasMarkerStatus && !MarkerOk;

    public string ConfigText
    {
        get
        {
            if (HasConfigLink)
                return T("{0}  (yours)", ConfigLink);
            string detected = Install.ConfigPathList.FirstOrDefault(p => !string.Equals(p, Install.ConfigOverride, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (Install.ConfigInInstallation)
                return HasLink && LinkChanged
                    ? T("Inside the linked folder (taken from it on Done)")
                    : detected.Length > 0 ? T("{0}  (inside the installation)", detected) : T("Inside the installation folder");
            return detected.Length > 0 ? T("{0}  (detected)", detected) : T("Not found yet (the game writes it when you change its controls).");
        }
    }

    // The engine lets the user set the config location, except for games that keep it in the installation and for
    // games that keep their controls in the Windows registry.
    public bool CanChangeConfig => !Install.ConfigInInstallation && !IsRegistryKey(Install.FirstConfigPath);

    public string ConfigNote => Install.ConfigInInstallation
        ? T("Kept in the installation: it follows the installation folder above.")
        : IsRegistryKey(Install.FirstConfigPath)
            ? T("Kept in the Windows registry; JoyPro finds it there (read-only).")
            : string.Empty;

    public bool HasConfigNote => ConfigNote.Length > 0;

    // DCS (Saved Games\DCS or DCS.openbeta) and Star Citizen (profile folder) keep folders; others mostly one file.
    public bool ConfigIsFolder
    {
        get
        {
            if (GameId is "DCS" or "StarCitizen")
                return true;
            string path = Install.FirstConfigPath;
            return path.Length > 0 && Directory.Exists(path);
        }
    }

    [RelayCommand]
    private void UseDetectedFolder() => LinkedFolder = string.Empty;

    [RelayCommand]
    private void UseDetectedConfig() => ConfigLink = string.Empty;

    partial void OnLinkedFolderChanged(string value)
    {
        // Linking a folder means the user has the game.
        if (value.Length > 0)
            HasGame = true;
    }

    private string? FoundMarker(string folder)
    {
        return Install.Markers.FirstOrDefault(m => File.Exists(Path.Combine(folder, m)) || Directory.Exists(Path.Combine(folder, m)));
    }

    private string MarkerMissingText()
    {
        string markers = Install.Markers.Count == 0 ? T("the game's executable") : string.Join(" or ", Install.Markers);
        return T("Marker not found ({0}), but the folder is kept as you chose it.", markers);
    }

    private static bool IsRegistryKey(string path)
    {
        return path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase);
    }
}
