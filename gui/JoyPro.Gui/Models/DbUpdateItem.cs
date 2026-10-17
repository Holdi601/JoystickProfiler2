namespace JoyPro.Gui.Models;

// A game whose command list needs work (engine db check-updates): State "game-updated" (the game was updated since its
// list was built) or "no-catalog" (no list yet: release packages ship none, it is built from the installed game), with
// the engine's refresh command for it ("engine dcs refresh id", "engine wardogs refresh-db").
public sealed class DbUpdateItem
{
    public DbUpdateItem(string game, string displayName, string message, string refreshCommand, bool missingList = false)
    {
        Game = game;
        DisplayName = displayName;
        Message = message;
        RefreshCommand = refreshCommand;
        MissingList = missingList;
    }

    public string Game { get; }

    public string DisplayName { get; }

    public string Message { get; }

    public string RefreshCommand { get; }

    // No command list of the game yet: "Build List" instead of "Refresh DB", and nothing to keep.
    public bool MissingList { get; }

    public bool CanKeep => !MissingList;

    public string RefreshLabel => MissingList ? T("Build List") : T("Refresh DB");

    // JoyPro can refresh this game's DB (else only a JoyPro update brings new actions).
    public bool CanRefresh { get; set; }
}
