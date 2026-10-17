using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Models;

// One input of the old device in the Replace device preview (engine devices replace --dry-run): what it is, where it
// goes on the new device and why, who uses it. The new input can be changed (it becomes --map OLD=NEW).
public sealed partial class DeviceReplaceRow : ObservableObject
{
    public static string LeaveOut => T("(stays on the old device)");

    public DeviceReplaceRow(string oldInput, string oldPart, string newInput, string newPart, string method, string note, bool axis,
        IReadOnlyList<string> relations, IReadOnlyList<string> modifiers, IReadOnlyList<string> targetRelations, IReadOnlyList<string> choices)
    {
        OldInput = oldInput;
        OldPart = oldPart;
        EngineNewInput = newInput;
        NewPart = newPart;
        Method = method;
        Note = note;
        Axis = axis;
        Relations = relations;
        Modifiers = modifiers;
        TargetRelations = targetRelations;
        Choices = choices;
        chosenInput = newInput.Length > 0 ? newInput : LeaveOut;
    }

    public string OldInput { get; }

    public string OldPart { get; }

    // What the engine chose (the preview's own mapping).
    public string EngineNewInput { get; }

    public string NewPart { get; }

    public string Method { get; }

    public string Note { get; }

    public bool Axis { get; }

    public IReadOnlyList<string> Relations { get; }

    public IReadOnlyList<string> Modifiers { get; }

    public IReadOnlyList<string> TargetRelations { get; }

    // The new device's inputs of the same kind, "(stays on the old device)" first.
    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapped), nameof(IsUnmapped), nameof(ChangedByUser), nameof(MethodText), nameof(NewPartText), nameof(IsGuess), nameof(HasOverlap), nameof(OverlapText), nameof(IsPlainMethod))]
    private string chosenInput;

    public bool IsMapped => ChosenInput.Length > 0 && ChosenInput != LeaveOut;

    public bool IsUnmapped => !IsMapped;

    public bool ChangedByUser => !string.Equals(IsMapped ? ChosenInput : string.Empty, EngineNewInput, StringComparison.OrdinalIgnoreCase);

    public string OldText => OldPart.Length > 0 ? OldInput + "  " + OldPart : OldInput;

    public string NewPartText => ChangedByUser ? string.Empty : NewPart;

    public string MethodText => ChangedByUser
        ? T("chosen by you")
        : Method switch
        {
            "same input" => T("same input"),
            "same part" => T("same part"),
            "same kind" => Note.Length > 0 ? T("same kind of part: {0}", Note) : T("same kind of part"),
            "by order" => T("a guess (button order)"),
            "same id" => T("same input id (no matching part)"),
            "yours" => T("chosen by you"),
            _ => Note.Length > 0 ? Loc.Translate(Note) : T("no matching input")
        };

    public bool IsGuess => IsMapped && !ChangedByUser && (Method == "by order" || Method == "same id");

    public bool IsPlainMethod => IsMapped && !IsGuess;

    public string UsedByText
    {
        get
        {
            List<string> parts = [];
            if (Relations.Count > 0)
                parts.Add(Relations.Count == 1 ? Relations[0] : Relations[0] + " +" + (Relations.Count - 1));
            if (Modifiers.Count > 0)
                parts.Add(T("modifier {0}", string.Join(", ", Modifiers)));
            return string.Join("; ", parts);
        }
    }

    public string UsedByTip => string.Join(Environment.NewLine, Relations) + (Modifiers.Count > 0 ? Environment.NewLine + T("Modifier: {0}", string.Join(", ", Modifiers)) : string.Empty);

    public bool HasOverlap => TargetRelations.Count > 0 && IsMapped && !ChangedByUser;

    public string OverlapText => HasOverlap ? T("already used there by {0}", string.Join(", ", TargetRelations)) : string.Empty;
}

// One device moved to its new instance after a profile opened (engine devices auto-remap).
public sealed record DeviceRemapNoticeItem(string OldDevice, string NewDevice, int Binds, int Modifiers, string Reason)
{
    public string Text => Modifiers > 0
        ? T("{0}: {1} bind(s), {2} modifier(s) moved to the connected one (new ID {3})", ShortName(OldDevice), Binds, Modifiers, GuidOf(NewDevice))
        : T("{0}: {1} bind(s) moved to the connected one (new ID {2})", ShortName(OldDevice), Binds, GuidOf(NewDevice));

    public string Tip => OldDevice + "  ->  " + NewDevice + Environment.NewLine + Reason;

    internal static string ShortName(string device)
    {
        int guid = device.LastIndexOf(" {", StringComparison.Ordinal);
        return (guid > 0 && device.EndsWith('}') ? device[..guid] : device).Trim();
    }

    internal static string GuidOf(string device)
    {
        int open = device.LastIndexOf('{');
        return open >= 0 && device.EndsWith('}') ? device[open..] : string.Empty;
    }
}
