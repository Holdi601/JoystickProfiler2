using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One input of a marker: the DirectInput input, its control id and its own point on the picture (a hat direction sits
// on its side of the hat), 0..1 of the picture.
public sealed class ReferenceMarkerInput
{
    public string Input { get; set; } = string.Empty;
    public string Control { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}

// A physical part of the device on the shown picture (DeviceReferenceView draws it as a numbered badge).
public sealed partial class ReferenceMarker : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public int Number { get; init; }
    public string Type { get; init; } = "button";
    public string Role { get; init; } = string.Empty;
    public string Short { get; init; } = string.Empty;
    public List<ReferenceMarkerInput> Inputs { get; init; } = [];

    // Name: the printed label or the part in plain words; editable in the legend.
    [ObservableProperty] private string name = string.Empty;
    // Center, 0..1 of the shown picture.
    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
    // Radius as a fraction of the picture width (the drawn part's size on the schematic).
    [ObservableProperty] private double size = 0.02;
    [ObservableProperty] private bool isSelected;
    // A button of it is held right now (live input) or the assistant asks for it.
    [ObservableProperty] private bool isHighlighted;
    // The bound command(s), one line (from the profile's binds or the user's text).
    [ObservableProperty] private string command = string.Empty;
    // Placed by the user on this picture (else computed from the schematic).
    [ObservableProperty] private bool isPlaced;
    // Its position on the shown picture is known (JoyPro's drawing, a curated set for this photo, placed by the user):
    // only then it is drawn. A part without one is listed in the legend only.
    [ObservableProperty] private bool isShown = true;

    public string Badge => Short.Length > 0 && Short.Length <= 3 ? Short : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasInput(string input) => Inputs.Any(i => string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase));

    public string InputsText => string.Join(", ", Inputs.Where(i => i.Input.Length > 0).Select(i => i.Input));
}

// A part as the picture canvas (DeviceMarkerCanvas) sees it: the part's centre in picture pixels instead of 0..1 of the
// picture. Created by DeviceReferenceViewModel for the shown picture's size; follows the part's changes.
public sealed class ReferenceCanvasMarker : ObservableObject, IDevicePartMarker
{
    private readonly double width;
    private readonly double height;

    public ReferenceCanvasMarker(ReferenceMarker part, double width, double height)
    {
        Part = part;
        this.width = Math.Max(1, width);
        this.height = Math.Max(1, height);
        part.PropertyChanged += OnPartChanged;
    }

    public ReferenceMarker Part { get; }

    public string Input => Part.Inputs.FirstOrDefault(i => i.Input.Length > 0)?.Input ?? string.Empty;

    // button, axis, pov or slider (the canvas' colors): a part type of the device vocabulary mapped onto them.
    public string Kind => Part.Type switch
    {
        "hat" or "ministick" or "rocker" => "pov",
        "lever" or "slider" or "wheel" or "detent" => "slider",
        "gimbal" or "twist" or "rudder" or "brake" or "knob" => "axis",
        _ => "button"
    };

    public string Modifier => string.Empty;

    public double X => Part.X * width;

    public double Y => Part.Y * height;

    public string Command => Part.Command;

    public bool IsSelected => Part.IsSelected;

    public double Radius => Math.Max(0.006, Part.Size) * width;

    public string Badge => Part.Badge;

    public string Label => Part.Name;

    public bool IsHighlighted => Part.IsHighlighted;

    public string Details =>
        Part.Name + (Part.InputsText.Length > 0 ? "\n" + Part.InputsText : string.Empty) + (Part.Command.Length > 0 ? "\n" + Part.Command : string.Empty);

    public double PictureWidth => width;

    public double PictureHeight => height;

    // Stops following the part (the canvas markers are rebuilt).
    public void Detach() => Part.PropertyChanged -= OnPartChanged;

    private void OnPartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReferenceMarker.X):
                OnPropertyChanged(nameof(X));
                break;
            case nameof(ReferenceMarker.Y):
                OnPropertyChanged(nameof(Y));
                break;
            case nameof(ReferenceMarker.Size):
                OnPropertyChanged(nameof(Radius));
                break;
            case nameof(ReferenceMarker.Name):
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(Details));
                break;
            case nameof(ReferenceMarker.Command):
                OnPropertyChanged(nameof(Command));
                OnPropertyChanged(nameof(Details));
                break;
            default:
                OnPropertyChanged(e.PropertyName);
                break;
        }
    }
}

// One row of the legend table: an input of a marker, its physical name and what it does in the profile.
public sealed partial class ReferenceLegendRow : ObservableObject
{
    public ReferenceMarker Marker { get; init; } = new();
    public string Input { get; init; } = string.Empty;
    public string Control { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;

    public int Number => Marker.Number;
    public string Badge => Marker.Badge;
    public string InputText => Input.Length > 0 ? Input : "—";
    public bool HasInput => Input.Length > 0;

    // The physical name ("TMS hat up"); editable, kept per model.
    [ObservableProperty] private string name = string.Empty;
    // What it does: the bound command(s) from the profile, or the user's own text; editable.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBound), nameof(IsFree))]
    private string command = string.Empty;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isEdited;
    // The input is pressed (or the axis moved) right now: the row lights up.
    [ObservableProperty] private bool isLive;

    // Something is bound (or written) there: a green dot in the lists; free: a hollow one.
    public bool IsBound => HasInput && Command.Length > 0;

    public bool IsFree => HasInput && Command.Length == 0;
    // Where the part is shown: the pictures that know its position ("Front, Side"), else "not placed yet".
    [ObservableProperty] private string viewText = string.Empty;
}

// A picture the reference can show: the user's own photo, JoyPro's line drawing of the model ("lineart", shipped), a
// reference photo of the maker (one per view, downloaded on this PC only after the user's consent), JoyPro's schematic
// drawing of the model ("schematic") or JoyPro's outline of the device class ("class").
public sealed class ReferencePictureOption
{
    public string Kind { get; init; } = "schematic";
    public string Title { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    // front, side, back, top, base, main, sheet (photos); empty for drawings.
    public string View { get; init; } = string.Empty;
    // The photo's catalog entry (its SHA-256 on record, the parts it shows); null for drawings and the user's photo.
    public JoyPro.DeviceReference.DevicePhoto? Photo { get; init; }
    // The key the picture's marker positions are kept under: the photo's SHA-256 once it is known, "schematic" or
    // "class" for the drawings.
    public string Key { get; set; } = string.Empty;
    // How many parts have a known position on it (filled when the markers are built).
    public int KnownCount { get; set; }
    // JoyPro's line drawing (Kind "lineart"): it lies in the frame of one reference photo, so Key is that photo's
    // SHA-256 when JoyPro knows it (its curated positions and sheet apply), else the drawing's own key.
    public JoyPro.DeviceReference.DeviceLineArt? LineArt { get; init; }

    public bool IsPhoto => Kind is "user" or "vendor";

    public bool IsLineArt => Kind == "lineart";

    // A picture in a photo's frame: positions and sheets are kept per content key (photos and line drawings).
    public bool HasPhotoFrame => IsPhoto || IsLineArt;

    public override string ToString() => Title;
}

// A model the user can pick in "Which one is it?": its photo (or JoyPro's drawing), how likely it is and why.
public sealed partial class ChoiceCandidate : ObservableObject
{
    public JoyPro.DeviceReference.RefDevice Model { get; init; } = new();
    public string Title { get; init; } = string.Empty;
    public int Score { get; init; }
    public string Why { get; init; } = string.Empty;
    public bool IsBest { get; init; }
    public string PhotoUrl { get; init; } = string.Empty;
    public JoyPro.DeviceReference.DeviceSchematic? Drawing { get; init; }

    [ObservableProperty] private Avalonia.Media.IImage? picture;

    public string ScoreText => T("{0} % likely", Score.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public override string ToString() => Title;
}

// A kind of device for a device JoyPro does not know (DeviceClasses).
public sealed class ClassChoice
{
    public string Class { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool IsGuess { get; init; }
    public JoyPro.DeviceReference.DeviceSchematic? Drawing { get; init; }

    public override string ToString() => Title;
}
