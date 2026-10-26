using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// An import while the profile has changes for that game that were never exported (tester's log on 2.4.2: he moved an
// action to another button, pressed Import on the DCS card before exporting, and the import put the old bind back,
// twice). Before an import JoyPro asks the engine what was changed in JoyPro since the last export and the game does not
// have (engine sync pending); when there is something, it asks: export first, import anyway, or cancel.
public sealed partial class MainWindowViewModel
{
    private bool importGuardAsked;

    internal enum ImportGuardChoice
    {
        Import,
        ExportedFirst,
        Cancel
    }

    // True: go on with the import. "Export first" exports the game and stops (the game has the profile then; an import
    // would bring nothing back).
    internal async Task<bool> GuardUnexportedChangesAsync(string gameId, string gameName, string path = "")
    {
        // The import wizard asked already.
        if (importGuardAsked)
            return true;
        ImportGuardChoice choice = await AskAboutUnexportedChangesAsync(gameId, gameName, path);
        return choice == ImportGuardChoice.Import;
    }

    internal async Task<ImportGuardChoice> AskAboutUnexportedChangesAsync(string gameId, string gameName, string path)
    {
        if (!HasProfile || gameId.Length == 0)
            return ImportGuardChoice.Import;
        List<string> args = ["sync", "pending", "--game", gameId];
        if (path.Trim().Length > 0)
            args.AddRange(["--path", path.Trim()]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: N("Checking for changes not exported yet..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return ImportGuardChoice.Import;
        int count;
        List<(string Kind, string Title)> actions = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            if (!ReadJsonBool(root, "Known"))
                return ImportGuardChoice.Import;
            count = root.TryGetProperty("Count", out JsonElement countEl) && countEl.TryGetInt32(out int c) ? c : 0;
            if (root.TryGetProperty("Actions", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement action in list.EnumerateArray())
                    actions.Add((ReadJsonString(action, "Kind"), ReadJsonString(action, "Title")));
            }
        }
        catch (JsonException)
        {
            return ImportGuardChoice.Import;
        }
        if (count == 0)
            return ImportGuardChoice.Import;

        // What would be lost: the count and up to three examples; the import is undoable (profile history).
        string examples = string.Join(", ", actions.Take(3).Select(a => a.Kind switch
        {
            "moved" => T("moved '{0}'", a.Title),
            "removed" => T("removed '{0}'", a.Title),
            _ => T("added '{0}'", a.Title)
        }));
        string message = Plural("You changed {0} bind for {1} that is not in {1} yet (e.g. {2}). Importing now brings back what {1} still has.",
            "You changed {0} binds for {1} that are not in {1} yet (e.g. {2}). Importing now brings back what {1} still has.", count, count, gameName, examples) +
            "\n\n" + T("Undo or History brings your changes back after the import.");
        JoyProLog.Info("ImportGuard", gameId + ": " + count + " change(s) not exported yet before an import (" + string.Join(", ", actions.Take(5).Select(a => a.Kind + " " + a.Title)) + ")");
        ConfirmChoice answer = await ConfirmChoiceAsync(T("Changes not in {0} yet", gameName), message, T("Import (drop my changes for {0})", gameName), T("Export first"),
            ConfirmImportUnexportedKey);
        switch (answer)
        {
            case ConfirmChoice.Secondary:
                await ExportBeforeImportAsync(gameId, gameName);
                return ImportGuardChoice.ExportedFirst;
            case ConfirmChoice.Confirm:
                return ImportGuardChoice.Import;
            default:
                StatusText = T("Import from {0} cancelled: your changes stay as they are.", gameName);
                return ImportGuardChoice.Cancel;
        }
    }

    private async Task<EngineCommandResult?> ExportRegistryGameByIdAsync(string gameId)
    {
        if (RegistryGames.Count == 0)
            await RefreshRegistryGamesAsync();
        RegistryGameItem? game = RegistryGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (game == null)
            return null;
        if (SelectedRegistryGame != game)
            SelectedRegistryGame = game;
        return await RunRegistryGameExportAsync();
    }

    private async Task ExportBeforeImportAsync(string gameId, string gameName)
    {
        if (!await ConfirmRiskyExportAsync(gameName))
            return;
        // The point is to put the changes into the game: "keep the game's binds" (merge) would keep the old button of a
        // moved action (the game's bind is the one JoyPro wrote before), so this export lets the profile win; "clean" stays.
        string modeBefore = ExportMode;
        if (!string.Equals(ExportMode?.Trim(), "clean", StringComparison.OrdinalIgnoreCase))
            ExportMode = "merge-overwrite";
        EngineCommandResult? result;
        try
        {
            result = gameId switch
            {
                "DCS" => await RunDcsExportAsync(null),
                "IL2Game" => await RunIl2ExportAsync(),
                "StarCitizen" => await RunStarCitizenExportAsync(),
                _ => await ExportRegistryGameByIdAsync(gameId)
            };
        }
        finally
        {
            ExportMode = modeBefore;
        }
        StatusText = result?.ExitCode == 0
            ? T("Exported to {0}: the game has your changes now. Import again when you want to take changes made in the game.", gameName)
            : T("The export to {0} did not work; nothing was imported.", gameName);
    }
}
