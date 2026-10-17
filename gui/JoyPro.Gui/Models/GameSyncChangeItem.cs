using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One change a game made to its controls (engine sync diff): ticked to take it over, and the relation it belongs to,
// a new relation (with a name) or ignore.
public sealed partial class GameSyncChangeItem : ObservableObject
{
    public const string NewRelationOption = "(new relation)";
    public const string IgnoreOption = "(ignore)";
    public const string TakeOverOption = "(take over)";

    public GameSyncChangeItem(string id, string kind, string description, IReadOnlyList<string> candidates, string suggested, string newRelationName)
    {
        Id = id;
        Kind = kind;
        Description = description;
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !Targets.Contains(candidate))
                Targets.Add(candidate);
        }
        // A game-wide setting goes into the profile's settings for the game (no relation); a removed bind can only leave a
        // relation it is in.
        if (kind == "changed-setting")
            Targets.Add(TakeOverOption);
        else if (kind != "removed")
            Targets.Add(NewRelationOption);
        Targets.Add(IgnoreOption);
        newRelationNameValue = newRelationName;
        selectedTargetValue = kind == "changed-setting" ? (suggested == "ignore" ? IgnoreOption : TakeOverOption) : suggested switch
        {
            "new" => kind == "removed" ? IgnoreOption : NewRelationOption,
            "ignore" or "" => IgnoreOption,
            _ => Targets.Contains(suggested) ? suggested : IgnoreOption
        };
    }

    public string Id { get; }

    public string Kind { get; }

    public string Description { get; }

    public string KindLabel => Kind switch
    {
        "added" => "New bind",
        "removed" => "Bind removed",
        "changed-tuning" => "Axis tuning changed",
        "changed-modifiers" => "Modifiers changed",
        "changed-setting" => "Game setting changed",
        _ => Kind
    };

    // The relations that bind this input (or hold the action), then "new relation" and "ignore".
    public ObservableCollection<string> Targets { get; } = [];

    [ObservableProperty]
    private bool isSelected = true;

    private string selectedTargetValue;

    public string SelectedTarget
    {
        get => selectedTargetValue;
        set
        {
            if (SetProperty(ref selectedTargetValue, value ?? IgnoreOption))
                OnPropertyChanged(nameof(IsNewRelation));
        }
    }

    private string newRelationNameValue;

    public string NewRelationName
    {
        get => newRelationNameValue;
        set => SetProperty(ref newRelationNameValue, value ?? string.Empty);
    }

    public bool IsNewRelation => SelectedTarget == NewRelationOption;

    // engine sync apply: Target is a relation name, "new" (with Name) or "ignore".
    public (string Target, string Name) Choice()
    {
        if (!IsSelected || SelectedTarget == IgnoreOption || string.IsNullOrWhiteSpace(SelectedTarget))
            return ("ignore", string.Empty);
        if (SelectedTarget == NewRelationOption)
            return ("new", NewRelationName.Trim());
        if (SelectedTarget == TakeOverOption)
            return ("apply", string.Empty);
        return (SelectedTarget, string.Empty);
    }
}
