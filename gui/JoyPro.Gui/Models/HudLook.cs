using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace JoyPro.Gui.Models;

// The design of the HUD (docs/hud.md "Styles"): one set of options for every element type (cards, text, the stick
// element, axis bars, levers, buttons, chips), saved in hud.json for the whole HUD and, where a widget has a style of
// its own, for that widget. A preset is such a set with a name (HudStylePresets); a user's own style is one saved under
// a name of his own, also as a style file (.joypro-hudstyle.json) to share.
public sealed class HudLookSettings
{
    public const string FileFormat = "joypro-hud-style";

    // The preset or own style this set started from (shown in the list; empty after changes are made by hand).
    public string Name { get; set; } = "Glass";

    // Accent: gimbal dot, filled bars, chips (#RRGGBB).
    public string Accent { get; set; } = "#38BDF8";

    // Pressed buttons and lit markers; empty = the accent.
    public string Pressed { get; set; } = string.Empty;

    public double Opacity { get; set; } = 1;

    // Default (JoyPro's UI font), Mono, or a font name (Bahnschrift, Segoe UI, Consolas, ...).
    public string Font { get; set; } = "Default";

    // Text size factor (1 = 14 px titles, 13 px chips, 11 px captions).
    public double TextScale { get; set; } = 1;

    public double LetterSpacing { get; set; } = 0.6;

    public bool BoldTitles { get; set; } = true;

    public string Text { get; set; } = "#F1F5F9";

    public string TextDim { get; set; } = "#94A3B8";

    // Cards: background (#AARRGGBB, transparent = no card), border (empty = the accent, faded), thickness, corners.
    public string CardBackground { get; set; } = "#B00B1220";

    public string CardBorder { get; set; } = string.Empty;

    public double CardBorderAlpha { get; set; } = 0.33;

    public double CardBorderThickness { get; set; } = 1;

    public double CardRadius { get; set; } = 16;

    // None, Soft (drop shadow) or Glow (accent glow).
    public string CardShadow { get; set; } = "Soft";

    // Glow of the gimbal dot and the name dot (0 = none).
    public double Glow { get; set; } = 1;

    // The empty part of bars, levers and buttons.
    public string Track { get; set; } = "#26FFFFFF";

    // Stick element: Ring, Square, Crosshair, Radar or Dot; its size in pixels.
    public string GimbalShape { get; set; } = "Ring";

    public double GimbalSize { get; set; } = 84;

    // Axis bars: Rounded, Square, Thin, Segmented or Outline; their height.
    public string BarShape { get; set; } = "Rounded";

    public double BarHeight { get; set; } = 6;

    // Levers (throttles, brakes): the same shapes; their width.
    public string LeverShape { get; set; } = "Rounded";

    public double LeverWidth { get; set; } = 20;

    // Buttons of the button grid and markers on pictures: Rounded, Circle or Square.
    public string ButtonShape { get; set; } = "Rounded";

    // Held buttons by name: Pill, Square or Underline.
    public string ChipShape { get; set; } = "Pill";

    public HudLookSettings Clone() => (HudLookSettings)MemberwiseClone();

    // Fills in what an older or hand-written file lacks and keeps every value in its range.
    public HudLookSettings Normalized()
    {
        HudLookSettings s = Clone();
        s.Name ??= string.Empty;
        s.Accent = ValidColor(s.Accent, "#38BDF8");
        s.Pressed = string.IsNullOrWhiteSpace(s.Pressed) ? string.Empty : ValidColor(s.Pressed, s.Accent);
        s.Opacity = Clamp(s.Opacity, 0.2, 1, 1);
        s.Font = string.IsNullOrWhiteSpace(s.Font) ? "Default" : s.Font.Trim();
        s.TextScale = Clamp(s.TextScale, 0.7, 2, 1);
        s.LetterSpacing = Clamp(s.LetterSpacing, 0, 4, 0.6);
        s.Text = ValidColor(s.Text, "#F1F5F9");
        s.TextDim = ValidColor(s.TextDim, "#94A3B8");
        s.CardBackground = ValidColor(s.CardBackground, "#B00B1220");
        s.CardBorder = string.IsNullOrWhiteSpace(s.CardBorder) ? string.Empty : ValidColor(s.CardBorder, s.Accent);
        s.CardBorderAlpha = Clamp(s.CardBorderAlpha, 0, 1, 0.33);
        s.CardBorderThickness = Clamp(s.CardBorderThickness, 0, 6, 1);
        s.CardRadius = Clamp(s.CardRadius, 0, 32, 16);
        s.CardShadow = OneOf(s.CardShadow, HudLook.CardShadows, "Soft");
        s.Glow = Clamp(s.Glow, 0, 1, 1);
        s.Track = ValidColor(s.Track, "#26FFFFFF");
        s.GimbalShape = OneOf(s.GimbalShape, HudLook.GimbalShapes, "Ring");
        s.GimbalSize = Clamp(s.GimbalSize, 50, 160, 84);
        s.BarShape = OneOf(s.BarShape, HudLook.BarShapes, "Rounded");
        s.BarHeight = Clamp(s.BarHeight, 2, 16, 6);
        s.LeverShape = OneOf(s.LeverShape, HudLook.BarShapes, "Rounded");
        s.LeverWidth = Clamp(s.LeverWidth, 6, 36, 20);
        s.ButtonShape = OneOf(s.ButtonShape, HudLook.ButtonShapes, "Rounded");
        s.ChipShape = OneOf(s.ChipShape, HudLook.ChipShapes, "Pill");
        return s;
    }

    public bool SameDesign(HudLookSettings other)
    {
        HudLookSettings a = Normalized(), b = other.Normalized();
        a.Name = b.Name = string.Empty;
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);

    private static string OneOf(string? value, IReadOnlyList<string> choices, string fallback) =>
        choices.FirstOrDefault(c => string.Equals(c, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    // A colour as #AARRGGBB (one spelling for one colour: "#38BDF8" and "#FF38BDF8" are the same design).
    internal static string ValidColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || !Color.TryParse(value.Trim(), out Color color))
            return Color.TryParse(fallback, out Color f) ? Hex(f) : fallback;
        return Hex(color);
    }

    private static string Hex(Color c) => "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
}

// A style file (Look > Export style / Import style): the format line and the options.
public sealed class HudStyleFile
{
    public string Format { get; set; } = HudLookSettings.FileFormat;

    public int Version { get; set; } = 1;

    public HudLookSettings Style { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void Write(string path, HudLookSettings style)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        File.WriteAllText(path, JsonSerializer.Serialize(new HudStyleFile { Style = style.Normalized() }, Options));
    }

    // The style of a file, or null when it is not a HUD style (a theme, a layout, broken JSON).
    public static HudLookSettings? Read(string path)
    {
        try
        {
            HudStyleFile? file = JsonSerializer.Deserialize<HudStyleFile>(File.ReadAllText(path), Options);
            if (file == null || !string.Equals(file.Format, HudLookSettings.FileFormat, StringComparison.OrdinalIgnoreCase) || file.Style == null)
                return null;
            HudLookSettings style = file.Style.Normalized();
            if (string.IsNullOrWhiteSpace(style.Name))
                style.Name = Path.GetFileNameWithoutExtension(path).Replace(".joypro-hudstyle", string.Empty, StringComparison.OrdinalIgnoreCase);
            return style;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

// The resolved design the views draw with: brushes, sizes and shapes made once per change of the options (never per
// frame). HudWidgetHost puts them into a widget's resources, the stream page gets them as CSS values (CssVariables).
public sealed class HudLook
{
    public static readonly IReadOnlyList<string> GimbalShapes = ["Ring", "Square", "Crosshair", "Radar", "Dot"];
    public static readonly IReadOnlyList<string> BarShapes = ["Rounded", "Square", "Thin", "Segmented", "Outline"];
    public static readonly IReadOnlyList<string> ButtonShapes = ["Rounded", "Circle", "Square"];
    public static readonly IReadOnlyList<string> ChipShapes = ["Pill", "Square", "Underline"];
    public static readonly IReadOnlyList<string> CardShadows = ["None", "Soft", "Glow"];
    public static readonly IReadOnlyList<string> Fonts = ["Default", "Mono", "Bahnschrift", "Segoe UI", "Consolas", "Arial", "Verdana", "Trebuchet MS", "Georgia", "Courier New", "Impact"];

    public HudLook(HudLookSettings settings)
    {
        Settings = settings.Normalized();
        HudLookSettings s = Settings;
        AccentColor = Color.Parse(s.Accent);
        Color pressed = s.Pressed.Length > 0 ? Color.Parse(s.Pressed) : AccentColor;
        Accent = new ImmutableSolidColorBrush(AccentColor);
        AccentSoft = new ImmutableSolidColorBrush(Color.FromArgb(0x66, AccentColor.R, AccentColor.G, AccentColor.B));
        ChipBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x40, AccentColor.R, AccentColor.G, AccentColor.B));
        Pressed = new ImmutableSolidColorBrush(pressed);
        Text = new ImmutableSolidColorBrush(Color.Parse(s.Text));
        TextDim = new ImmutableSolidColorBrush(Color.Parse(s.TextDim));
        Track = new ImmutableSolidColorBrush(Color.Parse(s.Track));
        Color card = Color.Parse(s.CardBackground);
        CardBackground = new ImmutableSolidColorBrush(card);
        Color border = s.CardBorder.Length > 0 ? Color.Parse(s.CardBorder) : AccentColor;
        CardBorder = new ImmutableSolidColorBrush(s.CardBorder.Length > 0 && border.A < 255
            ? border
            : Color.FromArgb((byte)Math.Round(s.CardBorderAlpha * 255), border.R, border.G, border.B));
        CardBorderThickness = new Thickness(s.CardBorderThickness);
        CardRadius = new CornerRadius(s.CardRadius);
        HasCard = card.A > 0 || (s.CardBorderThickness > 0 && s.CardBorderAlpha > 0);
        string hex = AccentColor.R.ToString("X2") + AccentColor.G.ToString("X2") + AccentColor.B.ToString("X2");
        byte glowAlpha = (byte)Math.Round(s.Glow * 0xCC);
        Glow = s.Glow <= 0 ? default : BoxShadows.Parse("0 0 " + Num(14 * s.Glow) + " " + Num(3 * s.Glow) + " #" + glowAlpha.ToString("X2") + hex);
        CardShadow = s.CardShadow switch
        {
            "Soft" when card.A > 0 => BoxShadows.Parse("0 10 30 0 #99000000"),
            "Glow" => BoxShadows.Parse("0 0 18 1 #66" + hex),
            _ => default
        };
        Font = s.Font switch
        {
            "Default" => UiFont("JP.UiFont"),
            "Mono" => UiFont("JP.MonoFont"),
            _ => new FontFamily(s.Font + ", " + UiFont("JP.UiFont"))
        };
        TitleWeight = s.BoldTitles ? FontWeight.SemiBold : FontWeight.Normal;
        TitleSize = Math.Round(14 * s.TextScale, 1);
        TextSize = Math.Round(13 * s.TextScale, 1);
        SmallSize = Math.Round(11 * s.TextScale, 1);
        TinySize = Math.Round(10 * s.TextScale, 1);
        ButtonCellSize = Math.Round(22 * Math.Max(1, s.TextScale * 0.9));
        ButtonRadius = new CornerRadius(s.ButtonShape switch { "Circle" => ButtonCellSize / 2, "Square" => 2, _ => 6 });
        MarkerRadius = new CornerRadius(s.ButtonShape == "Square" ? 2 : 8);
        ChipRadius = new CornerRadius(s.ChipShape switch { "Square" => 3, "Underline" => 0, _ => 11 });
        ChipBorder = s.ChipShape == "Underline" ? new Thickness(0, 0, 0, 2) : new Thickness(1);
        ChipFill = s.ChipShape == "Underline" ? new ImmutableSolidColorBrush(Colors.Transparent) : ChipBackground;
    }

    public HudLookSettings Settings { get; }

    public Color AccentColor { get; }
    public IBrush Accent { get; }
    public IBrush AccentSoft { get; }
    public IBrush ChipBackground { get; }
    public IBrush ChipFill { get; }
    public IBrush Pressed { get; }
    public IBrush Text { get; }
    public IBrush TextDim { get; }
    public IBrush Track { get; }
    public IBrush CardBackground { get; }
    public IBrush CardBorder { get; }
    public Thickness CardBorderThickness { get; }
    public CornerRadius CardRadius { get; }
    public bool HasCard { get; }
    public BoxShadows Glow { get; }
    public BoxShadows CardShadow { get; }
    public FontFamily Font { get; }
    public FontWeight TitleWeight { get; }
    public double TitleSize { get; }
    public double TextSize { get; }
    public double SmallSize { get; }
    public double TinySize { get; }
    public double ButtonCellSize { get; }
    public CornerRadius ButtonRadius { get; }
    public CornerRadius MarkerRadius { get; }
    public CornerRadius ChipRadius { get; }
    public Thickness ChipBorder { get; }

    // The resources HudWidgetHost puts into a widget (the templates read them with DynamicResource).
    public void ApplyTo(Avalonia.Controls.IResourceDictionary resources)
    {
        resources["HudAccent"] = Accent;
        resources["HudAccentSoft"] = AccentSoft;
        resources["HudPressed"] = Pressed;
        resources["HudChipBackground"] = ChipFill;
        resources["HudChipBorder"] = ChipBorder;
        resources["HudChipRadius"] = ChipRadius;
        resources["HudText"] = Text;
        resources["HudTextDim"] = TextDim;
        resources["HudTrack"] = Track;
        resources["HudCardBackground"] = CardBackground;
        resources["HudCardBorder"] = CardBorder;
        resources["HudCardBorderThickness"] = CardBorderThickness;
        resources["HudCardRadius"] = CardRadius;
        resources["HudCardShadow"] = CardShadow;
        resources["HudGlow"] = Glow;
        resources["HudFont"] = Font;
        resources["HudTitleWeight"] = TitleWeight;
        resources["HudTitleSize"] = TitleSize;
        resources["HudTextSize"] = TextSize;
        resources["HudSmallSize"] = SmallSize;
        resources["HudTinySize"] = TinySize;
        resources["HudLetterSpacing"] = Settings.LetterSpacing;
        resources["HudButtonSize"] = ButtonCellSize;
        resources["HudButtonRadius"] = ButtonRadius;
        resources["HudMarkerRadius"] = MarkerRadius;
        resources["HudOpacity"] = Settings.Opacity;
        resources["HudGimbalShape"] = Settings.GimbalShape;
        resources["HudGimbalSize"] = Settings.GimbalSize;
        resources["HudBarShape"] = Settings.BarShape;
        resources["HudBarHeight"] = Settings.BarHeight;
        resources["HudLeverShape"] = Settings.LeverShape;
        resources["HudLeverWidth"] = Settings.LeverWidth;
    }

    // The same design for the stream page (CSS custom properties and the shape names).
    public Dictionary<string, string> CssVariables()
    {
        HudLookSettings s = Settings;
        return new Dictionary<string, string>
        {
            ["accent"] = Css(AccentColor),
            ["soft"] = Css(Color.FromArgb(0x66, AccentColor.R, AccentColor.G, AccentColor.B)),
            ["chip"] = s.ChipShape == "Underline" ? "transparent" : Css(Color.FromArgb(0x40, AccentColor.R, AccentColor.G, AccentColor.B)),
            ["pressed"] = Css(((ImmutableSolidColorBrush)Pressed).Color),
            ["text"] = Css(Color.Parse(s.Text)),
            ["dim"] = Css(Color.Parse(s.TextDim)),
            ["track"] = Css(Color.Parse(s.Track)),
            ["cardBg"] = Css(((ImmutableSolidColorBrush)CardBackground).Color),
            ["cardBorder"] = Css(((ImmutableSolidColorBrush)CardBorder).Color),
            ["cardBw"] = Num(s.CardBorderThickness) + "px",
            ["cardRadius"] = Num(s.CardRadius) + "px",
            ["cardShadow"] = CssShadow(CardShadow),
            ["glow"] = CssShadow(Glow),
            ["font"] = CssFont(s.Font),
            ["titleWeight"] = s.BoldTitles ? "600" : "400",
            ["title"] = Num(TitleSize) + "px",
            ["size"] = Num(TextSize) + "px",
            ["small"] = Num(SmallSize) + "px",
            ["tiny"] = Num(TinySize) + "px",
            ["ls"] = Num(s.LetterSpacing) + "px",
            ["btn"] = Num(ButtonCellSize) + "px",
            ["btnRadius"] = Num(ButtonRadius.TopLeft) + "px",
            ["markerRadius"] = Num(MarkerRadius.TopLeft) + "px",
            ["chipRadius"] = Num(ChipRadius.TopLeft) + "px",
            ["opacity"] = Num(s.Opacity),
            ["gimbal"] = s.GimbalShape.ToLowerInvariant(),
            ["gimbalSize"] = Num(s.GimbalSize),
            ["bar"] = s.BarShape.ToLowerInvariant(),
            ["barHeight"] = Num(s.BarHeight),
            ["lever"] = s.LeverShape.ToLowerInvariant(),
            ["leverWidth"] = Num(s.LeverWidth),
            ["chipShape"] = s.ChipShape.ToLowerInvariant()
        };
    }

    private static FontFamily UiFont(string key)
    {
        if (Application.Current?.TryGetResource(key, null, out object? value) == true && value is FontFamily family)
            return family;
        return key == "JP.MonoFont" ? new FontFamily("Cascadia Mono, Consolas, Courier New") : new FontFamily("Bahnschrift, Segoe UI");
    }

    private static string CssFont(string font) => font switch
    {
        "Default" => "Bahnschrift, 'Segoe UI', 'Yu Gothic UI', 'Microsoft YaHei UI', 'Malgun Gothic', sans-serif",
        "Mono" => "'Cascadia Mono', Consolas, 'Courier New', monospace",
        _ => "'" + font.Replace("'", string.Empty) + "', Bahnschrift, 'Segoe UI', sans-serif"
    };

    private static string CssShadow(BoxShadows shadows)
    {
        if (shadows.Count == 0)
            return "none";
        BoxShadow b = shadows[0];
        return Num(b.OffsetX) + "px " + Num(b.OffsetY) + "px " + Num(b.Blur) + "px " + Num(b.Spread) + "px " + Css(b.Color);
    }

    internal static string Css(Color c) =>
        "rgba(" + c.R + "," + c.G + "," + c.B + "," + Num(Math.Round(c.A / 255.0, 3)) + ")";

    internal static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
