using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// A game JoyPro supports and whether it is installed (engine games installs): found by Steam, a known place or
// Windows' apps list, or set by the user (Settings > Games). Games that are not installed are blocked for import,
// export, scan, DB refresh and backup restore. Games the user does not have (Disabled, the first-start setup's
// "I have this game") count as not installed and are hidden from the game lists.
public sealed partial class GameInstallItem : ObservableObject
{
    public GameInstallItem(string gameId, string name, bool installed, string installPath, string source, bool overridden, bool markerFound, string note, IReadOnlyList<string> configPaths,
        bool disabled = false, IReadOnlyList<string>? markers = null, string configOverride = "", bool configInInstallation = false)
    {
        GameId = gameId;
        Name = name;
        Installed = installed;
        InstallPath = installPath;
        Source = source;
        Overridden = overridden;
        MarkerFound = markerFound;
        Note = note;
        ConfigPathList = configPaths;
        ConfigPaths = configPaths.Count == 0 ? T("No config file yet (the game writes it when you change its controls).") : string.Join("\n", configPaths);
        Disabled = disabled;
        Markers = markers ?? [];
        ConfigOverride = configOverride;
        ConfigInInstallation = configInInstallation;
    }

    public string GameId { get; }

    public string Name { get; }

    public bool Installed { get; }

    public bool NotInstalled => !Installed;

    public string InstallPath { get; }

    public string Source { get; }

    public bool Overridden { get; }

    public bool MarkerFound { get; }

    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public string ConfigPaths { get; }

    public IReadOnlyList<string> ConfigPathList { get; }

    public string FirstConfigPath => ConfigPathList.FirstOrDefault() ?? string.Empty;

    // The user said they do not have the game.
    public bool Disabled { get; }

    public bool HasGame => !Disabled;

    // The files (relative to the installation) that show a folder is the game's installation, as the engine checks them.
    public IReadOnlyList<string> Markers { get; }

    // The user's config location (engine games config set), or empty.
    public string ConfigOverride { get; }

    public bool ConfigOverridden => ConfigOverride.Length > 0;

    // The game keeps its controls in its installation: the config location follows the installation folder.
    public bool ConfigInInstallation { get; }

    // A detected installation (found by Steam, a known place or Windows' apps list), not the user's folder.
    public bool Detected => !Overridden && InstallPath.Length > 0 && (Installed || Disabled) && !string.Equals(Source, "test", StringComparison.Ordinal);

    public string Status => Disabled ? T("You do not have it") : Installed ? (Overridden ? T("Installed (your folder)") : T("Installed")) : T("Not installed");

    public string Where => InstallPath.Length > 0
        ? InstallPath + (Source.Length > 0 && !Overridden ? T("  ·  found via {0}", Source) : string.Empty)
        : T("Not found on this PC");

    // How many actions JoyPro knows for the game (engine games installs "Actions"; -1 = not reported).
    public int Actions { get; set; } = -1;

    // Why it has none (engine "ActionsNote").
    public string ActionsNote { get; set; } = string.Empty;

    // JoyPro cannot find any action of the game: no command list and no table of its own.
    public bool HasNoActions => Actions == 0;

    // Settings > Games: why it has none (and that the Start page leaves it out).
    public string ActionsText => !HasNoActions ? string.Empty
        : Installed ? T("JoyPro has no command list of it yet: Update on its Start page card builds it from the game's own files.")
        : T("JoyPro has no command list of it: the list is built from the installed game, and the game was not found on this PC. Until then it is not offered on the Start page.");

    // Not installed and nothing known: JoyPro cannot do anything for it on this PC, so the Start page leaves it out
    // (an installed game without a list stays: Update builds the list from its files).
    public bool OfferedOnStartPage => !(HasNoActions && !Installed);

    [ObservableProperty]
    private Bitmap? cover;
}
