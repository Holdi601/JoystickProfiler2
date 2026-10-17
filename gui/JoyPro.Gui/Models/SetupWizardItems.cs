using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Setup;

namespace JoyPro.Gui.Models;

// Items of the Setup wizard (SetupWizardViewModel, docs/setup-wizard.md).

public sealed partial class SetupStepItem : ObservableObject
{
    public SetupStepItem(int index, string title)
    {
        Index = index;
        Title = title;
    }

    public int Index { get; }

    public string Number => (Index + 1).ToString();

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodo))]
    private bool isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodo))]
    private bool isDone;

    public bool IsTodo => !IsCurrent && !IsDone;
}

public sealed partial class SetupGameChoice : ObservableObject
{
    public SetupGameChoice(string gameId, string name, bool watched, bool started = true)
    {
        GameId = gameId;
        Name = name;
        Watched = watched;
        Started = started;
        // Ticked: watched and started once (a game never started has no control files to set up yet).
        isChosen = watched && started;
    }

    public string GameId { get; }

    public string Name { get; }

    public bool Watched { get; }

    public bool Started { get; }

    public string Hint => !Started ? T("not started yet") : Watched ? T("watched on the Start page") : T("not watched");

    [ObservableProperty]
    private bool isChosen;
}

public sealed record SetupRoleOption(string Id, string Title)
{
    public override string ToString() => Title;
}

public sealed partial class SetupDeviceItem : ObservableObject
{
    public SetupDeviceItem(SetupDevice device, int inputs, IReadOnlyList<SetupRoleOption> roles)
    {
        Device = device;
        Inputs = inputs;
        Roles = roles;
        selectedRole = roles.FirstOrDefault(r => r.Id == device.Role) ?? roles.Last();
        Grips = device.Grips.Select(g => new SetupRoleOption(g.Id, g.Name)).ToList();
        selectedGrip = Grips.FirstOrDefault(g => g.Id == device.Grip);
    }

    public SetupDevice Device { get; }

    public string Name => SetupRoles.Short(Device);

    public string Details
    {
        get
        {
            List<string> parts = [Device.Kind];
            if (Device.Hand.Length > 0 && Device.Hand != "none")
                parts.Add(Device.Hand + " hand");
            if (Device.ForceFeedback)
                parts.Add("force feedback");
            if (Device.Generic)
                parts.Add(T("not in the catalog"));
            parts.Add(Inputs + " inputs");
            if (!Device.Connected)
                parts.Add(T("not connected"));
            return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public string Reason => Device.Reason;

    public int Inputs { get; }

    public IReadOnlyList<SetupRoleOption> Roles { get; }

    public IReadOnlyList<SetupRoleOption> Grips { get; }

    public bool HasGrips => Grips.Count > 0;

    public bool NeedsGrip => Device.NeedsGrip && SelectedGrip == null;

    public bool IsChanged => SelectedRole != null && SelectedRole.Id != Device.ProposedRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private SetupRoleOption? selectedRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsGrip))]
    private SetupRoleOption? selectedGrip;

    public Action? Changed { get; set; }

    partial void OnSelectedRoleChanged(SetupRoleOption? value)
    {
        if (value != null)
            Device.Role = value.Id;
        Changed?.Invoke();
    }

    partial void OnSelectedGripChanged(SetupRoleOption? value)
    {
        Device.Grip = value?.Id ?? string.Empty;
        Changed?.Invoke();
    }
}

public sealed record SetupAircraftItem(string Key, string Game, string ClassTitle)
{
    public string Display => Game == Key ? T("{0} (game)", Key) : Key;

    public string Details => (Game == Key ? T("one setup for the whole game") : Game) + " | " + ClassTitle;

    public override string ToString() => Display;
}

// One rank of the priority list; the aircraft in it count the same.
public sealed partial class SetupTierItem : ObservableObject
{
    public ObservableCollection<SetupAircraftItem> Aircraft { get; } = [];

    [ObservableProperty]
    private int rank;

    public string Title => T("Priority {0}{1}", Rank, (Aircraft.Count > 1 ? " (equal)" : string.Empty));

    partial void OnRankChanged(int value) => OnPropertyChanged(nameof(Title));

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

// One input of a device in the review: what the plan puts on it (one row per relation).
public sealed class SetupReviewRow
{
    public SetupReviewRow(SetupPlanRelation relation)
    {
        Relation = relation;
    }

    public SetupPlanRelation Relation { get; }

    public string Input => Relation.Input + (Relation.Modifier.Length > 0 ? " + " + Relation.Modifier : string.Empty);

    public string Control => Relation.Label.Length > 0 ? Relation.Label : Relation.Control;

    public string Name => Relation.Name + (Relation.Removed ? " (removed)" : string.Empty);

    public string Category => SetupImportance.CategoryTitle(Relation.Category.Length > 0 ? Relation.Category : SetupImportance.Systems);

    public string Reason => Relation.Kept ? T("your bind, kept") : Relation.Reason;

    public string Aircraft => Relation.Aircraft.Count <= 3 ? string.Join(", ", Relation.Aircraft) : string.Join(", ", Relation.Aircraft.Take(3)) + " +" + (Relation.Aircraft.Count - 3);

    public bool IsKept => Relation.Kept;

    public bool IsRemoved => Relation.Removed;

    public override string ToString() => Relation.Name;
}

public sealed class SetupQualityRow
{
    public SetupQualityRow(SetupQuality quality)
    {
        Quality = quality;
    }

    public SetupQuality Quality { get; }

    public string Aircraft => Quality.Aircraft + (Quality.Game != "DCS" && Quality.Game != Quality.Aircraft ? " (" + Quality.Game + ")" : string.Empty);

    public string Priority => Quality.Tier >= 0 ? T("Priority {0}", (Quality.Tier + 1)) : T("not listed");

    public string Score => Quality.Score.ToString("0") + " %";

    public double ScoreValue => Quality.Score;

    public string Placed => T("{0} / {1} placed", Quality.Placed, Quality.Total);

    public string Essential => T("{0} / {1} essential", Quality.EssentialPlaced, Quality.EssentialTotal);
}

public sealed class SetupNotPlacedRow
{
    public SetupNotPlacedRow(SetupNotPlaced item)
    {
        Item = item;
    }

    public SetupNotPlaced Item { get; }

    public string Name => Item.Relation;

    public string Category => SetupImportance.CategoryTitle(Item.Category.Length > 0 ? Item.Category : SetupImportance.Systems);

    public string Reason => Item.Reason;

    public string Aircraft => Item.Aircraft.Count <= 3 ? string.Join(", ", Item.Aircraft) : string.Join(", ", Item.Aircraft.Take(3)) + " +" + (Item.Aircraft.Count - 3);
}

public sealed record SetupTargetItem(string Device, string Input, string Modifier, string Text)
{
    public override string ToString() => Text;
}

public sealed record SetupSheetItem(string Device, string Title, string File);
