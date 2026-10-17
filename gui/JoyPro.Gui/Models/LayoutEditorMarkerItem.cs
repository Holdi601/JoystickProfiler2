using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class LayoutEditorMarkerItem : ObservableObject, IDeviceMarker
{
    [ObservableProperty]
    private string token = string.Empty;

    // What the input does for the device the layout belongs to (the bound relation); "" = not bound.
    [ObservableProperty]
    private string command = string.Empty;

    string IDeviceMarker.Input => Token ?? string.Empty;

    string IDeviceMarker.Kind => Type ?? "button";

    string IDeviceMarker.Modifier => Modifier ?? string.Empty;

    public string ShortLabel => DeviceMarkerText.ShortLabel(Token);

    public bool IsBound => !string.IsNullOrWhiteSpace(Command);

    public bool HasModifier => !string.IsNullOrWhiteSpace(Modifier);

    public string KindLabel =>
        (Type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "axis" => "Axis",
            "pov" => "Hat",
            "slider" => "Slider",
            _ => T("Button")
        };

    public string PositionLabel => "x " + X.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "  y " + Y.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    partial void OnTokenChanged(string value) => OnPropertyChanged(nameof(ShortLabel));

    partial void OnCommandChanged(string value) => OnPropertyChanged(nameof(IsBound));

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(PositionLabel));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(PositionLabel));

    [ObservableProperty]
    private string type = "button";

    [ObservableProperty]
    private string modifier = string.Empty;

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isFlashing;

    // The input is pressed (or the axis moved) right now: its row in the list lights up.
    [ObservableProperty]
    private bool isLive;

    public string DisplayBackground =>
        IsFlashing
            ? "#EE06B6D4"
            : (Type ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "axis" => "#CC0E7490",
                "pov" => "#CC7C3AED",
                "slider" => "#CCC2410C",
                _ => "#CC1D4ED8"
            };

    public string DisplayBorder =>
        IsFlashing
            ? "#E0F2FE"
            : IsSelected
            ? "#FDE047"
            : (string.IsNullOrWhiteSpace(Modifier) ? "#BFDBFE" : "#F59E0B");

    public double DisplayBorderThickness => IsFlashing ? 4 : (IsSelected ? 3 : 2);

    public string DisplaySymbol =>
        (Type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "axis" => "AX",
            "pov" => "POV",
            "slider" => "SL",
            _ => "BTN"
        };

    public string DisplayTypeShort =>
        (Type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "axis" => "a",
            "pov" => "p",
            "slider" => "s",
            _ => "b"
        };

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(DisplaySymbol));
        OnPropertyChanged(nameof(DisplayTypeShort));
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(KindLabel));
    }

    partial void OnModifierChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(HasModifier));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(DisplayBorderThickness));
    }

    partial void OnIsFlashingChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(DisplayBorderThickness));
    }
}
