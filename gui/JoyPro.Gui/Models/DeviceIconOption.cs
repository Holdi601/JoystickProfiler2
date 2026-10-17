using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One picture the Layout page offers as a device's icon: the photo, the maker's photo, JoyPro's line drawing (lines,
// cel-shaded, two-colour), JoyPro's drawing, the layout's picture, or "Automatic" (what JoyPro picks by itself).
public sealed partial class DeviceIconOption : ObservableObject
{
    // user, vendor, lineart, celart, twocolour, schematic, layout, file, auto.
    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    // The icon as it will look (square, cut out).
    public IImage? Preview { get; init; }

    // The icon's PNG (null for "Automatic").
    public byte[]? Png { get; init; }

    public bool IsAutomatic => Kind == "auto";

    [ObservableProperty]
    private bool isCurrent;
}
