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

// Plain-language faces of engine keywords for users who never saw the command line: the export modes and the
// profile merge side as described choices, the open profile's name, and "Undo last export" per game (engine backups
// restore latest --game). Docs: docs/gui-cli-parity.md.
public sealed partial class MainWindowViewModel
{
    // ---- The open profile ----

    public bool HasProfile => !string.IsNullOrWhiteSpace(ProfilePath);

    public string ProfileDisplayName => HasProfile ? Path.GetFileNameWithoutExtension(ProfilePath.Trim()) : "No profile open";

    public string ProfileDisplayHint => HasProfile
        ? ProfilePath.Trim()
        : T("Open a profile, start a new one, or import your binds from a game below (a profile is made for you).");

    private void OnProfileDisplayChanged()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileDisplayName));
        OnPropertyChanged(nameof(ProfileDisplayHint));
    }

    // ---- Export mode ----

    // Since 2.5.0 (user decision): "Replace on my devices" + "Also clear my other devices" is the default (as in JoyPro 1);
    // every mode touches only the exported aircraft. The merge modes no longer clear DCS's own defaults on the devices
    // (docs/export.md, docs/games/dcs.md "Export modes").
    public IReadOnlyList<ChoiceOption> ExportModeOptions { get; } =
    [
        new("clean", N("Replace on my devices"),
            N("The devices of your profile get exactly your binds in the exported aircraft: every other bind on them, the game's defaults too, is removed. Other aircraft and the keyboard stay. A backup is made first; Undo export brings it back."),
            "Icon.Delete", isRecommended: true),
        new("merge", N("Update, keep the game's binds"),
            N("Your binds go into the game and the game's other binds stay. Only a game bind on the same button (same device, input and modifiers, same aircraft) that does another function is removed. An action you moved keeps its old button in the game too."),
            "Icon.Shield"),
        new("merge-overwrite", N("Update, my profile wins"),
            N("Like 'keep the game's binds', and every action your profile binds loses its other buttons in the game (in that aircraft), so a moved action really moves. Other binds of the game stay."),
            "Icon.Sync"),
        new("add", N("Only add"),
            N("Adds your binds next to what the game has and never removes anything. A bind whose button the game already uses for another function is not written; JoyPro marks it in your profile and in the Check."),
            "Icon.Plus"),
    ];

    // The mode the engine gets (an empty box: JoyPro's default).
    internal string EffectiveExportMode => string.IsNullOrWhiteSpace(ExportMode) ? ExportSettings.DefaultMode : ExportMode.Trim();

    public ChoiceOption? SelectedExportModeOption
    {
        get => ExportModeOptions.FirstOrDefault(o => string.Equals(o.Key, ExportMode?.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null && !string.Equals(value.Key, ExportMode, StringComparison.OrdinalIgnoreCase))
                ExportMode = value.Key;
        }
    }

    public string ExportModeExplanation => SelectedExportModeOption?.Description ?? T("Pick how JoyPro writes into the game.");

    partial void OnExportModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedExportModeOption));
        OnPropertyChanged(nameof(ExportModeExplanation));
        OnPropertyChanged(nameof(ExportIsRisky));
        OnPropertyChanged(nameof(MoveActionGameNote));
        OnPropertyChanged(nameof(HasMoveActionGameNote));
    }

    // Clean mode or "nuke" removes binds from the game: the export asks first.
    public bool ExportIsRisky => string.Equals(ExportMode?.Trim(), "clean", StringComparison.OrdinalIgnoreCase) || ExportNukeUntouchedDevices;

    partial void OnExportNukeUntouchedDevicesChanged(bool value) => OnPropertyChanged(nameof(ExportIsRisky));

    // Before an export the user started: what will be removed, when the mode removes binds.
    internal async Task<bool> ConfirmRiskyExportAsync(string what)
    {
        if (!ExportIsRisky)
            return true;
        List<string> removes = [];
        if (string.Equals(ExportMode?.Trim(), "clean", StringComparison.OrdinalIgnoreCase))
            removes.Add(T("every bind on the devices of your profile that is not in your profile (the game's defaults too)"));
        if (ExportNukeUntouchedDevices)
            removes.Add(T("all binds of the devices your profile does not use"));
        // The default since 2.5.0 (clean + nuke): asked until "Do not ask again" (Settings > Confirmations).
        return await ConfirmAsync(T("Remove binds in {0}?", what),
            T("This export removes {0}, only in the aircraft you export. JoyPro backs up the game files first; \"Undo last export\" on the game's tile puts them back.", string.Join(" and ", removes)),
            T("Export and remove"), isDanger: true, dontAskKey: ConfirmExportRemovesKey);
    }

    // ---- Profile merge ----

    public IReadOnlyList<ChoiceOption> MergeModeOptions { get; } =
    [
        new("right", N("The other profile wins"), N("Where both profiles have the same relation, the one from the profile you add is taken."), "Icon.Merge"),
        new("left", N("My profile wins"), N("Where both profiles have the same relation, yours stays as it is."), "Icon.Shield"),
    ];

    public ChoiceOption? SelectedMergeModeOption
    {
        get => MergeModeOptions.FirstOrDefault(o => string.Equals(o.Key, MergeMode?.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null)
                MergeMode = value.Key;
        }
    }

    partial void OnMergeModeChanged(string value) => OnPropertyChanged(nameof(SelectedMergeModeOption));

    // Merge writes into the open profile: a copy of it is kept next to it first (name.before-merge-<time>.pr0file).
    internal string? BackupProfileBeforeChange(string reason)
    {
        try
        {
            string path = ProfilePath.Trim();
            if (path.Length == 0 || !File.Exists(path))
                return null;
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            string copy = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "." + reason + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(path));
            File.Copy(path, copy, overwrite: false);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Undo last export (per game tile) ----

    [RelayCommand]
    private async Task UndoLastExportAsync(ExportGameItem? game)
    {
        if (game == null)
            return;
        EngineCommandResult? result = await RestoreLatestGameBackupAsync(game.GameId, game.Name);
        if (result == null)
            return;
        game.ExportFailed = result.ExitCode != 0;
        game.ExportState = result.ExitCode == 0
            ? T("Last export undone {0}", DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture))
            : T("Undo failed: {0}", BuildFailureStatus(result));
        StatusText = result.ExitCode == 0 ? T("{0}: the files from before the last export are back.", game.Name) : T("Undo failed, see the tile.");
        if (GameBackups.Count > 0)
            await RefreshGameBackupsAsync();
    }

    // engine backups restore latest --game <id>, after asking; null when the user said no.
    private async Task<EngineCommandResult?> RestoreLatestGameBackupAsync(string gameId, string name)
    {
        if (!await ConfirmAsync(T("Undo the last export to {0}?", name),
                T("JoyPro puts back the {0} control files from before the last export. Close the game first. Your current files are backed up too, so pressing Undo again brings them back.", name), T("Undo export"), isDanger: true))
            return null;
        return await RunEngineAsync(["backups", "restore", "latest", "--game", gameId], includeProfile: false, mutatesProfile: false,
            busyLabel: "Putting back the " + name + " files...");
    }

    // Shows what an export would change without writing (games of the registry: --dry-run).
    [RelayCommand]
    private async Task PreviewExportGameAsync(ExportGameItem? game)
    {
        if (game == null)
            return;
        if (!game.CanPreview)
        {
            StatusText = T("{0}: no preview for this game. Every export makes a backup first, and \"Undo last export\" puts it back.", game.Name);
            return;
        }
        bool previous = RegistryGameDryRun;
        using IDisposable notRemembered = SuppressExportSettings();
        RegistryGameDryRun = true;
        try
        {
            await ExportGameAsync(game);
            EngineOutputExpanded = true;
        }
        finally
        {
            RegistryGameDryRun = previous;
        }
    }
}
