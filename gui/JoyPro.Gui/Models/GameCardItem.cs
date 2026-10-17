using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One game on the Start page's "Your games" cards: its cover, whether it is installed, and what JoyPro can do with it
// (import its binds, update its command list, undo the last export). Kind says which engine commands do it:
// dcs, il2, starcitizen, or registry (engine <CommandName> scan|import|export|refresh-db).
public sealed partial class GameCardItem : ObservableObject
{
    public GameCardItem(string gameId, string name, string kind, string commandName, bool installed, bool canRefresh)
    {
        GameId = gameId;
        Name = name;
        Kind = kind;
        CommandName = commandName;
        Installed = installed;
        CanRefresh = canRefresh;
    }

    public string GameId { get; }

    public string Name { get; }

    public string Kind { get; }

    public string CommandName { get; }

    public bool Installed { get; }

    public bool NotInstalled => !Installed;

    // JoyPro can read the game's new commands itself (DCS, IL-2, Star Citizen, registry games with refresh-db).
    public bool CanRefresh { get; }

    public bool CanImport => Installed;

    public bool CanRefreshNow => Installed && CanRefresh;

    // VTOL VR's full HOTAS support needs the BYOJoystick mod: the card offers to install it (from the Steam Workshop).
    public bool OffersModInstall => Installed && GameId == "VTOLVR";

    public string InstallText => Installed ? T("Installed") : T("Not found on this PC");

    // "Watch for changes" (docs/sync-watcher.md#which-games-are-watched): JoyPro's watcher offers to take over binds the
    // user changes in the game. Ticked for installed games unless the user unticked it; a game not found has nothing
    // to watch (the box is off and greyed, and ticks itself once the game is installed unless the user unticked it).
    [ObservableProperty]
    private bool watch;

    public bool CanWatch => Installed;

    public string WatchTip => Installed
        ? T("Watch for changes: JoyPro notices when you change binds in this game and offers to take them over. Untick it and JoyPro leaves this game alone (import and export still work).")
        : T("Not found on this PC: nothing to watch. JoyPro ticks this once the game is installed (unless you unticked it before).");

    // Set by the view model: called when the user ticks or unticks the box (not for the value the card starts with).
    public System.Action<GameCardItem>? WatchChanged { get; set; }

    partial void OnWatchChanged(bool value) => WatchChanged?.Invoke(this);

    [ObservableProperty]
    private Bitmap? cover;

    // The game's executable icon on JoyPro's badge (games without a Steam cover on this PC).
    [ObservableProperty]
    private Bitmap? logo;

    // The game was updated since JoyPro's command list of it was made (engine db check-updates).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateHint))]
    private bool needsDbUpdate;

    // No command list of the game yet (fresh install; it is built from the game's own files with Update).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateHint))]
    private bool missingCommandList;

    public string UpdateHint => !NeedsDbUpdate ? string.Empty
        : MissingCommandList ? T("No command list yet: Update builds it from the game")
        : T("Game updated: update its command list");

    // The last thing done from this card ("Imported 14:02", "Command list updated", "Failed: ...").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ResultOk))]
    private string result = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultOk))]
    private bool resultFailed;

    public bool HasResult => Result.Length > 0;

    public bool ResultOk => HasResult && !ResultFailed;

    public override string ToString() => Name;
}
