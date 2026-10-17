using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One aircraft of a suggestion or a misfit, with its picture (official module icon from the DCS installation).
public sealed partial class PlaneChoice : ObservableObject
{
    public PlaneChoice(string name)
    {
        Name = name;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool isSelected = true;

    [ObservableProperty]
    private Bitmap? image;
}

// An entry of a relation that looks wrong (the function catalog files it elsewhere, or the opposite direction).
public sealed partial class RelationMisfitItem : ObservableObject
{
    public RelationMisfitItem(string game, string id, string title, IReadOnlyList<string> planes, string reason, string moveTo)
    {
        Game = game;
        Id = id;
        Title = title;
        Planes = planes;
        Reason = reason;
        MoveTo = moveTo;
        PlanePictures = new ObservableCollection<PlaneChoice>(planes.Take(8).Select(p => new PlaneChoice(p)));
    }

    // Pictures of the first aircraft (or the game's box art for other games).
    public ObservableCollection<PlaneChoice> PlanePictures { get; }

    [ObservableProperty]
    private Bitmap? picture;

    public string Game { get; }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> Planes { get; }

    public string Reason { get; }

    public string MoveTo { get; }

    public bool CanMove => MoveTo.Length > 0;

    public string MoveLabel => T("Move to '{0}'", MoveTo);

    public string Where => Game != "DCS" && Game.Length > 0 ? Game : Planes.Count <= 4 ? string.Join(", ", Planes) : T("{0} +{1} more", string.Join(", ", Planes.Take(3)), (Planes.Count - 3));

    public string NodeSpec => Game + ":" + Id + ":" + string.Join("|", Planes);
}
