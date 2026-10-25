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

    // The cheat sheet style of the Layout page (MainWindowViewModel.SheetStyle.cs, ApplySheetStyle): the border, the
    // header row and, for a "list" table (the engine fills it in the style's table colours), the table background.
    // Empty = JoyPro's colours of before.
    private string sheetBorder = string.Empty;
    private string sheetHeaderBackground = string.Empty;
    private string sheetTableBackground = string.Empty;
    private string sheetTableText = string.Empty;

    public void ApplySheetStyle(string border, string headerBackground, string tableBackground, string tableText = "")
    {
        sheetBorder = border ?? string.Empty;
        sheetHeaderBackground = headerBackground ?? string.Empty;
        sheetTableBackground = tableBackground ?? string.Empty;
        sheetTableText = tableText ?? string.Empty;
        OnPropertyChanged(nameof(DisplayBorder));
        OnPropertyChanged(nameof(DisplayHeaderBackground));
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(DisplayHintText));
    }

    // The "{0} column(s)" hint on the table: the style's table text when it reads on this table's own background (a
    // panel table keeps its light background in a dark style), else dark or light text, whichever reads (4.5:1).
    public string DisplayHintText => ReadableOn(DisplayBackground, sheetTableText);

    public static string ReadableOn(string background, string preferred)
    {
        Avalonia.Media.Color paper = Avalonia.Media.Colors.White;
        Avalonia.Media.Color bg = Avalonia.Media.Color.TryParse(background ?? string.Empty, out Avalonia.Media.Color b) ? JoyPro.Gui.Services.ThemeService.Over(b, paper) : paper;
        if (Avalonia.Media.Color.TryParse(preferred ?? string.Empty, out Avalonia.Media.Color p) && JoyPro.Gui.Services.ThemeService.Contrast(JoyPro.Gui.Services.ThemeService.Over(p, bg), bg) >= 4.5)
            return preferred!;
        Avalonia.Media.Color dark = Avalonia.Media.Color.Parse("#0F172A"), light = Avalonia.Media.Color.Parse("#F8FAFC");
        return JoyPro.Gui.Services.ThemeService.Contrast(dark, bg) >= JoyPro.Gui.Services.ThemeService.Contrast(light, bg) ? "#0F172A" : "#F8FAFC";
    }

    public string DisplayBorder => IsFlashing ? "#E0F2FE" : (IsSelected ? "#FDE047" : sheetBorder.Length > 0 ? sheetBorder : "#A3E635");

    public string DisplayHeaderBackground => IsFlashing ? "#CC0891B2" : (IsSelected ? "#CCCA8A04" : sheetHeaderBackground.Length > 0 ? sheetHeaderBackground : "#CC65A30D");

    public double DisplayBorderThickness => IsFlashing ? 4 : (IsSelected ? 3 : 2);

    public string DisplayBackground => string.Equals(Mode, "list", System.StringComparison.OrdinalIgnoreCase) && sheetTableBackground.Length > 0
        ? sheetTableBackground
        : BuildDisplayBackground();

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
        OnPropertyChanged(nameof(DisplayBackground));
    }

    partial void OnBackgroundColorChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(DisplayHintText));
    }

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
        OnPropertyChanged(nameof(DisplayHintText));
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
