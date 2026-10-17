using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// A game the engine handles through its game registry (engine games integrations): WARDOGS, Arma, War Thunder,
// X-Plane 11/12, MSFS 2020/2024, ...
public sealed partial class RegistryGameItem : ObservableObject
{
    public RegistryGameItem(string gameId, string displayName, string commandName, string notes, bool experimental, bool supportsCatalogRefresh, bool supportsGameProfile, bool supportsAircraft = false)
    {
        GameId = gameId;
        DisplayName = experimental ? displayName + " (experimental)" : displayName;
        CommandName = commandName;
        Notes = notes;
        SupportsCatalogRefresh = supportsCatalogRefresh;
        SupportsGameProfile = supportsGameProfile;
        SupportsAircraft = supportsAircraft;
    }

    public string GameId { get; }

    public string DisplayName { get; }

    // First alias, used as "engine <CommandName> scan|import|export|...".
    public string CommandName { get; }

    public string Notes { get; }

    public bool SupportsCatalogRefresh { get; }

    public bool SupportsGameProfile { get; }

    // The game has an aircraft list (MSFS 2020/2024: DB\<GameId>\aircraft.tsv); export takes --aircraft for relations limited to one.
    public bool SupportsAircraft { get; }

    // Found on this PC or set in Settings > Games; the others are blocked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private bool installed = true;

    public string Label => Installed ? DisplayName : T("{0} (not installed)", DisplayName);

    public override string ToString() => DisplayName;
}
