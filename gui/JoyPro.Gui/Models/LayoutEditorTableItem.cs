using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class LayoutEditorTableItem : ObservableObject
{
    [ObservableProperty]
    private string name = "Table";

    [ObservableProperty]
    private string mode = "panel";

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;

    [ObservableProperty]
    private double width = 320;

    [ObservableProperty]
    private double height = 200;

    [ObservableProperty]
    private int columns = 1;

    [ObservableProperty]
    private string backgroundColor = "#F6F8FB";

    [ObservableProperty]
    private double backgroundOpacity = 0.93;

    [ObservableProperty]
    private bool dropShadow = true;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isFlashing;

    public string DisplayBorder => IsFlashing ? "#E0F2FE" : (IsSelected ? "#FDE047" : "#A3E635");

    public string DisplayHeaderBackground => IsFlashing ? "#CC0891B2" : (IsSelected ? "#CCCA8A04" : "#CC65A30D");

    public double DisplayBorderThickness => IsFlashing ? 4 : (IsSelected ? 3 : 2);

    public string DisplayBackground => BuildDisplayBackground();

    public string DisplayBoxShadow => DropShadow ? "0 2 10 0 #70000000" : "0 0 0 0 #00000000";

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(DisplayHeaderBackground));
        OnPropertyChanged(nameof(DisplayBorderThickness));
    }

    partial void OnIsFlashingChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(DisplayHeaderBackground));
        OnPropertyChanged(nameof(DisplayBorderThickness));
    }

    partial void OnModeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Mode = "panel";
            return;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (!string.Equals(normalized, "list", System.StringComparison.OrdinalIgnoreCase))
            normalized = "panel";
        if (!string.Equals(Mode, normalized, System.StringComparison.Ordinal))
            Mode = normalized;
    }

    partial void OnBackgroundColorChanged(string value) => OnPropertyChanged(nameof(DisplayBackground));

    partial void OnBackgroundOpacityChanged(double value)
    {
        double clamped = value;
        if (clamped < 0)
            clamped = 0;
        else if (clamped > 1)
            clamped = 1;

        if (System.Math.Abs(clamped - value) > 0.0001)
        {
            BackgroundOpacity = clamped;
            return;
        }

        OnPropertyChanged(nameof(DisplayBackground));
    }

    partial void OnDropShadowChanged(bool value) => OnPropertyChanged(nameof(DisplayBoxShadow));

    private string BuildDisplayBackground()
    {
        string raw = (BackgroundColor ?? string.Empty).Trim();
        if (raw.Length == 0)
            raw = "#F6F8FB";

        if (raw.StartsWith("#", System.StringComparison.Ordinal))
            raw = raw.Substring(1);

        if (raw.Length == 8)
            raw = raw.Substring(2);

        if (raw.Length != 6)
            raw = "F6F8FB";

        int alpha = (int)System.Math.Round(System.Math.Clamp(BackgroundOpacity, 0, 1) * 255.0, System.MidpointRounding.AwayFromZero);
        return "#" + alpha.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + raw.ToUpperInvariant();
    }
}
