using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.Models;

// One game the profile exports to (engine profile games), shown as a tile with its cover. DCS opens to its aircraft,
// each with its picture; only the ticked aircraft are exported.
public sealed partial class ExportGameItem : ObservableObject
{
    public ExportGameItem(string gameId, string name, string exporter, int relations, int bound, bool installed = true, bool hidden = false, bool started = true)
    {
        Installed = installed;
        Hidden = hidden;
        GameId = gameId;
        Name = name;
        Exporter = exporter;
        Relations = relations;
        Bound = bound;
        Started = started;
        // Ticked for "Export ticked games" only when there is something to write and somewhere to write it: binds for
        // the game, the game installed and started once (its control files exist).
        DefaultSelected = bound > 0 && CanExport && started;
        isSelected = DefaultSelected;
    }

    // The game has written its control files (started once); without them most exports have nothing to edit.
    public bool Started { get; }

    // The tick the tile starts with (a tick the user changed survives a refresh).
    public bool DefaultSelected { get; }

    // A backup from before an export of this game exists: "Undo last export" can put it back.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoExport))]
    private bool hasBackup;

    public bool CanUndoExport => HasBackup && Installed;

    public string GameId { get; }

    public string Name { get; }

    // dcs, il2, starcitizen, registry or none.
    public string Exporter { get; }

    public int Relations { get; }

    public int Bound { get; }

    public bool Installed { get; }

    // The user does not have the game (Settings > Games): no tile.
    public bool Hidden { get; }

    public bool CanExport => Exporter != "none" && Installed;

    public string Summary => Plural("{0} of {1} relation bound", "{0} of {1} relations bound", Relations, Bound, Relations) +
                             (Planes.Count > 0 ? " · " + PlanesLabel : string.Empty) +
                             (CanExport ? string.Empty : " · " + T("no export for this game"));

    public ObservableCollection<ExportPlaneItem> Planes { get; } = [];

    public bool HasPlanes => Planes.Count > 0;

    public bool AllPlanesSelected => Planes.All(p => p.IsSelected);

    public string PlanesLabel
    {
        get
        {
            int selected = Planes.Count(p => p.IsSelected);
            return selected == Planes.Count ? Plural("{0} aircraft", "{0} aircraft", Planes.Count) : Plural("{0} of {1} aircraft", "{0} of {1} aircraft", Planes.Count, selected, Planes.Count);
        }
    }

    public string ExpandLabel => IsExpanded ? T("Hide aircraft") : T("Aircraft...");

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private Bitmap? cover;

    // Result of the last export from this tile ("Exported 14:02", "Failed: ...").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSucceeded))]
    private string exportState = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSucceeded))]
    private bool exportFailed;

    // The tile's status symbol: a green tick after a good export, a red mark after a failed one.
    public bool ExportSucceeded => ExportState.Length > 0 && !ExportFailed;

    // Games of the registry can show what an export would change without writing (--dry-run).
    public bool CanPreview => Exporter == "registry" && CanExport;

    public void AddPlane(ExportPlaneItem plane)
    {
        Planes.Add(plane);
        plane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ExportPlaneItem.IsSelected))
                return;
            OnPropertyChanged(nameof(PlanesLabel));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(AllPlanesSelected));
        };
        OnPropertyChanged(nameof(HasPlanes));
        OnPropertyChanged(nameof(PlanesLabel));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandLabel));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectAllPlanes()
    {
        foreach (ExportPlaneItem plane in Planes)
            plane.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoPlanes()
    {
        foreach (ExportPlaneItem plane in Planes)
            plane.IsSelected = false;
    }
}

// An aircraft (or DCS layer such as Default / UiLayer) of the DCS export.
public sealed partial class ExportPlaneItem : ObservableObject
{
    public ExportPlaneItem(string plane, int relations, int bound)
    {
        Plane = plane;
        Relations = relations;
        Bound = bound;
        displayName = plane;
    }

    public string Plane { get; }

    public int Relations { get; }

    public int Bound { get; }

    public string Summary => T("{0} of {1} bound", Bound, Relations);

    // The DB name under the module's own name ("FA-18C_hornet" under "F/A-18C").
    public bool ShowPlaneId => DisplayName != Plane;

    [ObservableProperty]
    private string displayName;

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(ShowPlaneId));

    [ObservableProperty]
    private bool isSelected = true;

    [ObservableProperty]
    private Bitmap? image;
}
