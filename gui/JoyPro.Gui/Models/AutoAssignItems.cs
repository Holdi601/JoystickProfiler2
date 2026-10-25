using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// A device of the Auto Assign setup as the engine identified it (engine devices kb identify).
public sealed partial class AutoAssignDeviceItem : ObservableObject
{
    public AutoAssignDeviceItem(string device, string model, string kind, bool connected, bool generic, string note, string alternativeTo, IReadOnlyList<AutoAssignGripItem> grips, string modelId = "")
    {
        ModelId = modelId;
        Device = device;
        Model = model;
        Kind = kind;
        Connected = connected;
        Generic = generic;
        Note = note;
        AlternativeTo = alternativeTo;
        Grips = grips;
    }

    public string Device { get; }

    public string Model { get; }

    // The catalog id of the recognised model (empty: generic layout, or a base whose grip is not picked yet).
    public string ModelId { get; }

    public bool HasLayout => ModelId.Length > 0 || NeedsGrip;

    public string Kind { get; }

    public bool Connected { get; }

    public bool Generic { get; }

    public string Note { get; }

    public string AlternativeTo { get; }

    public IReadOnlyList<AutoAssignGripItem> Grips { get; }

    public bool NeedsGrip => Grips.Count > 0;

    public string Summary
    {
        get
        {
            string model = NeedsGrip ? (SelectedGrip != null ? SelectedGrip.Name : T("pick the grip")) : Model;
            string text = model + (Kind.Length > 0 ? " [" + Kind + "]" : string.Empty) + (Generic ? " - generic layout" : string.Empty);
            if (AlternativeTo.Length > 0)
                text += T(" - left out, same role as {0}", AlternativeTo);
            return text;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private AutoAssignGripItem? selectedGrip;

    // Left out of Auto Assign (--ignore-device).
    [ObservableProperty]
    private bool isIgnored;

    public AutoAssignGripItem? FindGrip(string id) => Grips.FirstOrDefault(g => g.Id == id);
}

// A grip that fits an ambiguous base, with the manufacturer's product picture.
public sealed partial class AutoAssignGripItem : ObservableObject
{
    public AutoAssignGripItem(string id, string name, string imageUrl)
    {
        Id = id;
        Name = name;
        ImageUrl = imageUrl;
    }

    public string Id { get; }

    public string Name { get; }

    public string ImageUrl { get; }

    [ObservableProperty]
    private Bitmap? image;

    public override string ToString() => Name;
}

// A device of the catalog (engine devices kb list), to add a device that is not connected.
public sealed class AutoAssignCatalogItem
{
    public AutoAssignCatalogItem(string id, string name, string kind, string baseId, string confidence)
    {
        Id = id;
        Name = name;
        Kind = kind;
        BaseId = baseId;
        Confidence = confidence;
    }

    public string Id { get; }

    public string Name { get; }

    public string Kind { get; }

    public string BaseId { get; }

    public string Confidence { get; }

    public string Display => Name + " [" + Kind + "]" + (Confidence == "low" ? " - layout partly unknown" : string.Empty);

    public override string ToString() => Display;
}

// One input of a catalog device's layout (engine devices kb show).
public sealed class AutoAssignLayoutRow
{
    // Input and control in plain words ("Button 4", "TMS hat up"), never the ids.
    public AutoAssignLayoutRow(string input, string control, string label, string type, string finger)
    {
        Input = input.Length > 0 ? JoyPro.Gui.Services.ReadableNames.Input(input) : "?";
        Control = JoyPro.Gui.Services.ReadableNames.Control(control);
        Label = label;
        Type = type;
        Finger = finger;
    }

    public string Input { get; }

    public string Control { get; }

    public string Label { get; }

    public string Type { get; }

    public string Finger { get; }

    public string Details => string.Join(", ", new[] { Label.Length > 0 ? "\"" + Label + "\"" : string.Empty, Type, Finger.Length > 0 ? Finger + " finger" : string.Empty }.Where(v => v.Length > 0));
}
