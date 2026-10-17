using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Game config backups (engine backups): every export first copies the game files it changes into a backup set.
// The user can list the sets, see their files, preview a restore and restore one; a restore backs up the current
// files first, so it can be undone with the next set.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameBackupItem> GameBackups { get; } = [];

    public ObservableCollection<string> GameBackupGames { get; } = [AllGameBackups];

    private const string AllGameBackups = "All games";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreGameBackup))]
    private GameBackupItem? selectedGameBackup;

    // A backup of a game that is not installed cannot be restored (the engine refuses it too).
    public bool CanRestoreGameBackup => SelectedGameBackup != null && IsGameInstalled(SelectedGameBackup.Game);

    [ObservableProperty]
    private string selectedGameBackupGame = AllGameBackups;

    [ObservableProperty]
    private string gameBackupOutput = string.Empty;

    [RelayCommand]
    private async Task RefreshGameBackupsAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["backups", "list"], includeProfile: false, mutatesProfile: false, busyLabel: N("Loading game backups..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        string? selectedId = SelectedGameBackup?.Id;
        // Backups of games the user does not have (Settings > Games) are left out, like the games themselves.
        List<GameBackupItem> sets = ParseGameBackups(result.StandardOutput).Where(s => !IsGameHidden(s.Game)).ToList();
        foreach (string game in GameBackupGames.Where(g => g != AllGameBackups && IsGameHidden(g)).ToList())
            GameBackupGames.Remove(game);
        foreach (string game in sets.Select(s => s.Game).Distinct().OrderBy(g => g).Where(g => !GameBackupGames.Contains(g)))
            GameBackupGames.Add(game);
        GameBackups.Clear();
        foreach (GameBackupItem set in sets.Where(s => SelectedGameBackupGame == AllGameBackups || s.Game == SelectedGameBackupGame))
            GameBackups.Add(set);
        // The export tiles offer "Undo last export" only for a game with a backup.
        gamesWithBackups.Clear();
        foreach (GameBackupItem set in sets)
            gamesWithBackups.Add(set.Game);
        foreach (ExportGameItem game in ExportGames)
            game.HasBackup = gamesWithBackups.Contains(game.GameId);
        SelectedGameBackup = GameBackups.FirstOrDefault(s => s.Id == selectedId) ?? GameBackups.FirstOrDefault();
    }

    partial void OnSelectedGameBackupGameChanged(string value) => _ = RefreshGameBackupsAsync();

    [RelayCommand]
    private async Task ShowGameBackupAsync()
    {
        if (SelectedGameBackup == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["backups", "show", SelectedGameBackup.Id], includeProfile: false, mutatesProfile: false, busyLabel: N("Reading backup..."));
        if (result != null)
            GameBackupOutput = result.ExitCode == 0 ? result.StandardOutput : result.StandardError + result.StandardOutput;
    }

    [RelayCommand]
    private async Task PreviewRestoreGameBackupAsync()
    {
        if (SelectedGameBackup == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["backups", "restore", SelectedGameBackup.Id, "--dry-run"], includeProfile: false, mutatesProfile: false, busyLabel: N("Previewing restore..."));
        if (result != null)
            GameBackupOutput = result.ExitCode == 0 ? result.StandardOutput : result.StandardError + result.StandardOutput;
    }

    [RelayCommand]
    private async Task RestoreGameBackupAsync()
    {
        if (SelectedGameBackup == null)
            return;
        if (!await ConfirmAsync(T("Restore the {0} backup?", SelectedGameBackup.Game),
                T("JoyPro puts the {0} control files back the way they were on {1}. Close the game first. Your current files are backed up too, so this can be undone.", SelectedGameBackup.Game, SelectedGameBackup.Created), T("Restore"), isDanger: true))
            return;
        EngineCommandResult? result = await RunEngineAsync(["backups", "restore", SelectedGameBackup.Id], includeProfile: false, mutatesProfile: false, busyLabel: N("Restoring game files..."));
        if (result == null)
            return;
        GameBackupOutput = result.ExitCode == 0 ? result.StandardOutput : result.StandardError + result.StandardOutput;
        StatusText = result.ExitCode == 0 ? T("Restored {0} backup {1}.", SelectedGameBackup.Game, SelectedGameBackup.Id) : T("Restore failed, see the backup panel.");
        await RefreshGameBackupsAsync();
    }

    private static List<GameBackupItem> ParseGameBackups(string output)
    {
        List<GameBackupItem> sets = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return sets;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string id = ReadJsonString(item, "Id");
                if (id.Length == 0)
                    continue;
                int files = item.TryGetProperty("Files", out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
                sets.Add(new GameBackupItem(id, ReadJsonString(item, "Game"), ReadJsonString(item, "Created").Replace('T', ' '), files, ReadJsonString(item, "Command")));
            }
        }
        catch (JsonException)
        {
        }
        return sets;
    }
}
