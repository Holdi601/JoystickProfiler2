using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Services;

// One colour of the theme: the resource key the XAML uses ({DynamicResource JP.TextBrush}), its group in the theme
// editor and its name there (English, translated where shown).
public sealed record ThemeToken(string Key, string Group, string Label);

// A text / background pair the contrast check measures (WCAG: 4.5:1 for text, 3:1 for large text and controls).
public sealed record ThemeContrastPair(string Foreground, string Background, double Minimum, string? Under = null);

// The fonts of a theme: the UI font, the monospace font of key caps and input names (JOY_BTN5), the default weight,
// the line spacing (1 = the font's own) and how much bigger headings are than body text (1 = JoyPro's sizes).
public sealed class ThemeFonts
{
    public string Ui { get; set; } = string.Empty;

    public string Mono { get; set; } = string.Empty;

    public string Weight { get; set; } = "Normal";

    public double LineSpacing { get; set; } = 1.0;

    public double HeadingRatio { get; set; } = 1.0;

    public ThemeFonts Clone() => (ThemeFonts)MemberwiseClone();
}

// A colour scheme of the user's own: a JSON file in <data>\Themes (docs/ui/themes.md). Base: the built-in theme it
// starts from (Light or Dark: the controls JoyPro does not colour itself follow it); Colors: token key -> #AARRGGBB.
public sealed class CustomTheme
{
    public string Format { get; set; } = ThemeService.FormatName;

    public int Version { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string Base { get; set; } = "Dark";

    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);

    public ThemeFonts Fonts { get; set; } = new();

    // The text size the theme was made for (1 = 100 %); the user's own text size setting wins when he has one.
    public double? TextScale { get; set; }

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    public ThemeVariant BaseVariant => string.Equals(Base, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;

    public CustomTheme Clone() => new()
    {
        Format = Format,
        Version = Version,
        Name = Name,
        Base = Base,
        Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal),
        Fonts = Fonts.Clone(),
        TextScale = TextScale,
        FilePath = FilePath
    };
}

// Light, Dark, System and the user's own themes; the text size and UI scale on top of Windows' display scaling; the
// fonts. Every colour, font and text size of the GUI is a resource, so applying one here changes the whole window:
//   - colours: the tokens below (Styles/Design.axaml has the Light and Dark values);
//   - text sizes: JP.FontSize.<n> (JoyPro's size n times the text size, headings also times the heading ratio) and
//     Fluent's ControlContentThemeFontSize;
//   - fonts: JP.UiFont (the chosen font, then the bundled stack per glyph), JP.MonoFont, JP.UiFontWeight, JP.LineSpacing.
public static class ThemeService
{
    public const string FormatName = "joypro-theme";

    public const double MinScale = 0.8;
    public const double MaxScale = 2.0;
    public const double ScaleStep = 0.1;

    // JoyPro's font sizes; the XAML uses {DynamicResource JP.FontSize.12} instead of FontSize="12".
    public static readonly double[] FontSizes = [9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 14, 15, 16, 17, 18, 19, 20, 22, 24, 30, 44];

    // From this size on a text is a heading (the heading ratio applies).
    public const double HeadingSize = 15;

    public const string DefaultMonoFont = "Consolas, Cascadia Mono, Courier New";

    // The height of the page area JoyPro's pages are made for (a 1280x800 window).
    public const double PageDesignHeight = 560;

    public static string FontSizeKey(double size) => "JP.FontSize." + size.ToString(CultureInfo.InvariantCulture);

    public static readonly ThemeToken[] Tokens =
    [
        new("JP.PageBackgroundBrush", N("Pages"), N("Page background")),
        new("JP.SurfaceBrush", N("Pages"), N("Cards and panels")),
        new("JP.SurfaceAltBrush", N("Pages"), N("Panels inside cards")),
        new("JP.SurfaceRaisedBrush", N("Pages"), N("Raised panels")),
        new("JP.BorderBrush", N("Pages"), N("Borders")),
        new("JP.BorderStrongBrush", N("Pages"), N("Strong borders")),
        new("JP.ImageWellBrush", N("Pages"), N("Behind pictures")),
        new("JP.OverlayDimBrush", N("Pages"), N("Dimmed page behind a question")),
        new("ListBoxItemBackgroundSelected", N("Pages"), N("Selected row")),
        new("CardBorderBrush", N("Pages"), N("Card frame")),
        new("CardTintBrush", N("Pages"), N("Tile background")),

        new("JP.TextBrush", N("Text"), N("Text")),
        new("JP.TextMutedBrush", N("Text"), N("Muted text")),
        new("JP.AccentTextBrush", N("Text"), N("Links and accent text")),
        new("JP.OnAccentTextBrush", N("Text"), N("Text on accent colour")),

        new("JP.AccentBrush", N("Accent"), N("Accent")),
        new("JP.AccentSoftBrush", N("Accent"), N("Soft accent (highlights)")),

        new("ButtonBackground", N("Buttons"), N("Button background")),
        new("ButtonBackgroundPointerOver", N("Buttons"), N("Button under the mouse")),
        new("ButtonBackgroundPressed", N("Buttons"), N("Button pressed")),
        new("ButtonBackgroundDisabled", N("Buttons"), N("Button switched off")),
        new("ButtonForeground", N("Buttons"), N("Button text")),
        new("ButtonForegroundDisabled", N("Buttons"), N("Button text, switched off")),
        new("ButtonBorderBrush", N("Buttons"), N("Button border")),

        new("JP.OkTextBrush", N("Status"), N("OK text")),
        new("JP.OkFillBrush", N("Status"), N("OK fill")),
        new("JP.WarnTextBrush", N("Status"), N("Warning text")),
        new("JP.WarnBorderBrush", N("Status"), N("Warning frame")),
        new("JP.DangerTextBrush", N("Status"), N("Error text")),
        new("JP.DangerFillBrush", N("Status"), N("Error background")),
        new("JP.Dot.OkBrush", N("Status"), N("Status dot: running")),
        new("JP.Dot.WarnBrush", N("Status"), N("Status dot: problem")),
        new("JP.Dot.OffBrush", N("Status"), N("Status dot: off")),
        new("StatusOkBrush", N("Status"), N("Status icon: OK")),
        new("StatusWarnBrush", N("Status"), N("Status icon: warning")),
        new("StatusErrorBrush", N("Status"), N("Status icon: error")),
        new("StatusInfoBrush", N("Status"), N("Status icon: information")),
        new("JP.ToastBackgroundBrush", N("Status"), N("Notes over lists")),
        new("JP.LiveRowBrush", N("Status"), N("Row of a pressed input")),
        new("JP.LiveRowPulseBrush", N("Status"), N("Row of a pressed input, pulsing")),
        new("JP.StepTodoBrush", N("Status"), N("Step still to come")),

        new("JP.Chip.GameBrush", N("Chips"), N("Game chip")),
        new("JP.Chip.GameTextBrush", N("Chips"), N("Game chip text")),
        new("JP.Chip.AircraftBrush", N("Chips"), N("Aircraft chip")),
        new("JP.Chip.AircraftTextBrush", N("Chips"), N("Aircraft chip text")),
        new("JP.Chip.DeviceBrush", N("Chips"), N("Device chip")),
        new("JP.Chip.DeviceTextBrush", N("Chips"), N("Device chip text")),
        new("JP.Chip.ModifierBrush", N("Chips"), N("Modifier chip")),
        new("JP.Chip.ModifierTextBrush", N("Chips"), N("Modifier chip text")),
        new("JP.Chip.GroupBrush", N("Chips"), N("Group chip")),
        new("JP.Chip.GroupTextBrush", N("Chips"), N("Group chip text")),
        new("JP.Chip.AxisBrush", N("Chips"), N("Axis chip")),
        new("JP.Chip.AxisTextBrush", N("Chips"), N("Axis chip text")),
        new("JP.Chip.OkBrush", N("Chips"), N("OK chip")),
        new("JP.Chip.OkTextBrush", N("Chips"), N("OK chip text")),
        new("JP.Chip.WarnBrush", N("Chips"), N("Warning chip")),
        new("JP.Chip.WarnTextBrush", N("Chips"), N("Warning chip text")),
        new("JP.KeycapBrush", N("Chips"), N("Key cap")),
        new("JP.KeycapBorderBrush", N("Chips"), N("Key cap border")),
        new("JP.KeycapTextBrush", N("Chips"), N("Key cap text")),

        new("JP.Banner.BackgroundBrush", N("Header banner"), N("Banner")),
        new("JP.Banner.GlowBrush", N("Header banner"), N("Banner glow")),
        new("JP.Banner.AccentBrush", N("Header banner"), N("Banner line")),
        new("JP.Banner.TextBrush", N("Header banner"), N("Banner text")),

        new("JP.BottomBar.BackgroundBrush", N("Bottom bar"), N("Bottom bar")),
        new("JP.Nav.ButtonBrush", N("Bottom bar"), N("Page button")),
        new("JP.Nav.TextBrush", N("Bottom bar"), N("Page button text")),
        new("JP.Nav.SelectedBrush", N("Bottom bar"), N("Open page")),
        new("JP.Nav.SelectedTextBrush", N("Bottom bar"), N("Open page text")),

        new("JP.Console.BackgroundBrush", N("Engine output"), N("Output background")),
        new("JP.Console.TextBrush", N("Engine output"), N("Output text")),
        new("JP.Console.BorderBrush", N("Engine output"), N("Output frame")),
        new("JP.Console.SelectionBrush", N("Engine output"), N("Output selection")),

        new("JP.Chart.PanelBrush", N("Charts"), N("Chart background")),
        new("JP.Chart.GridBrush", N("Charts"), N("Chart grid")),
        new("JP.Chart.AxisBrush", N("Charts"), N("Chart axes")),
        new("JP.Chart.CurveBrush", N("Charts"), N("Curve")),
        new("JP.Chart.ReferenceBrush", N("Charts"), N("Reference curve")),
        new("JP.Chart.PointBrush", N("Charts"), N("Live point")),
        new("JP.Chart.LabelBrush", N("Charts"), N("Chart labels")),

        new("JP.Marker.ButtonBrush", N("Markers"), N("Button marker")),
        new("JP.Marker.AxisBrush", N("Markers"), N("Axis marker")),
        new("JP.Marker.HatBrush", N("Markers"), N("Hat marker")),
        new("JP.Marker.SliderBrush", N("Markers"), N("Slider marker")),
        new("JP.Marker.BoundBrush", N("Markers"), N("Bound input")),
        new("JP.Marker.FreeBrush", N("Markers"), N("Free input")),
        new("JP.Marker.HighlightBrush", N("Markers"), N("Pressed input")),
        new("JP.Marker.BadgeBrush", N("Markers"), N("Marker label background")),
        new("JP.Marker.LabelBrush", N("Markers"), N("Marker label text")),
    ];

    // Text and the backgrounds it stands on. Under: what a semi-transparent background (a chip) is drawn on.
    public static readonly ThemeContrastPair[] ContrastPairs =
    [
        new("JP.TextBrush", "JP.PageBackgroundBrush", 4.5),
        new("JP.TextBrush", "JP.SurfaceBrush", 4.5),
        new("JP.TextBrush", "JP.SurfaceAltBrush", 4.5),
        new("JP.TextBrush", "JP.SurfaceRaisedBrush", 4.5),
        new("JP.TextBrush", "ListBoxItemBackgroundSelected", 4.5),
        new("JP.TextMutedBrush", "JP.PageBackgroundBrush", 4.5),
        new("JP.TextMutedBrush", "JP.SurfaceBrush", 4.5),
        new("JP.TextMutedBrush", "JP.SurfaceAltBrush", 4.5),
        new("JP.AccentTextBrush", "JP.SurfaceBrush", 4.5),
        new("JP.AccentTextBrush", "JP.PageBackgroundBrush", 4.5),
        new("JP.OnAccentTextBrush", "JP.AccentBrush", 4.5),
        new("JP.OkTextBrush", "JP.SurfaceBrush", 4.5),
        new("JP.WarnTextBrush", "JP.SurfaceBrush", 4.5),
        new("JP.DangerTextBrush", "JP.SurfaceBrush", 4.5),
        new("JP.OnAccentTextBrush", "JP.OkFillBrush", 4.5),
        new("JP.OnAccentTextBrush", "JP.ToastBackgroundBrush", 4.5),
        new("JP.OnAccentTextBrush", "JP.StepTodoBrush", 4.5),
        new("ButtonForeground", "ButtonBackground", 4.5, "JP.SurfaceBrush"),
        new("ButtonForeground", "ButtonBackgroundPointerOver", 4.5, "JP.SurfaceBrush"),
        new("ButtonForeground", "ButtonBackgroundPressed", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.GameTextBrush", "JP.Chip.GameBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.AircraftTextBrush", "JP.Chip.AircraftBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.DeviceTextBrush", "JP.Chip.DeviceBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.ModifierTextBrush", "JP.Chip.ModifierBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.GroupTextBrush", "JP.Chip.GroupBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.AxisTextBrush", "JP.Chip.AxisBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.OkTextBrush", "JP.Chip.OkBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Chip.WarnTextBrush", "JP.Chip.WarnBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.KeycapTextBrush", "JP.KeycapBrush", 4.5, "JP.SurfaceBrush"),
        new("JP.Banner.TextBrush", "JP.Banner.BackgroundBrush", 4.5),
        new("JP.Nav.TextBrush", "JP.Nav.ButtonBrush", 4.5, "JP.BottomBar.BackgroundBrush"),
        new("JP.Nav.SelectedTextBrush", "JP.Nav.SelectedBrush", 4.5, "JP.BottomBar.BackgroundBrush"),
        new("JP.Console.TextBrush", "JP.Console.BackgroundBrush", 4.5),
        new("JP.Chart.LabelBrush", "JP.Chart.PanelBrush", 4.5),
        new("JP.Chart.CurveBrush", "JP.Chart.PanelBrush", 3.0),
        new("JP.Marker.LabelBrush", "JP.Marker.BadgeBrush", 4.5, "JP.SurfaceAltBrush"),
        new("JP.AccentBrush", "JP.SurfaceBrush", 3.0),
    ];

    // Keys JoyPro derives from the tokens (Fluent's expanders and accent shades follow the surfaces and the accent).
    private static readonly (string Key, string From, double Lighten)[] Derived =
    [
        ("ExpanderHeaderBackground", "JP.SurfaceBrush", 0),
        ("ExpanderHeaderBackgroundPointerOver", "JP.SurfaceRaisedBrush", 0),
        ("ExpanderHeaderBackgroundPressed", "JP.SurfaceAltBrush", 0),
        ("ExpanderHeaderBorderBrush", "JP.BorderBrush", 0),
        ("ExpanderHeaderBorderBrushPointerOver", "JP.BorderStrongBrush", 0),
        ("ExpanderHeaderBorderBrushPressed", "JP.BorderStrongBrush", 0),
        ("ExpanderContentBackground", "JP.SurfaceAltBrush", 0),
        ("ExpanderContentBorderBrush", "JP.BorderBrush", 0),
        ("ListBoxItemBackgroundSelectedPointerOver", "ListBoxItemBackgroundSelected", 0.06),
        ("ListBoxItemBackgroundSelectedPressed", "ListBoxItemBackgroundSelected", -0.04),
    ];

    private static ResourceDictionary? overrides;

    public static string ThemesFolder
    {
        get
        {
            string folder = Path.Combine(AppInfo.DataFolder, "Themes");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    // ---- the theme files ------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static List<CustomTheme> ListThemes()
    {
        List<CustomTheme> themes = [];
        foreach (string file in Directory.EnumerateFiles(ThemesFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoad(file, out CustomTheme? theme, out _) && theme != null)
                themes.Add(theme);
        }
        return themes;
    }

    // A theme file, checked: it must be a JoyPro theme with a name and a base; colours that are no colour and unknown
    // keys are left out (warnings), a broken file is refused with the reason.
    public static bool TryLoad(string path, out CustomTheme? theme, out string error) => TryParse(SafeRead(path), path, out theme, out error, out _);

    public static bool TryParse(string? json, string path, out CustomTheme? theme, out string error, out List<string> warnings)
    {
        theme = null;
        warnings = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = T("The file is empty or cannot be read.");
            return false;
        }
        CustomTheme? parsed;
        try
        {
            // The file says what it is: "format": "joypro-theme" (a JSON of something else is no theme).
            using (JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "format", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == FormatName))
                {
                    error = T("This is not a JoyPro theme (\"format\": \"joypro-theme\" is missing).");
                    return false;
                }
            }
            parsed = JsonSerializer.Deserialize<CustomTheme>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            error = T("This is not a JoyPro theme (the JSON is broken: {0}).", e.Message.Split('.')[0]);
            return false;
        }
        if (parsed == null || !string.Equals(parsed.Format, FormatName, StringComparison.Ordinal))
        {
            error = T("This is not a JoyPro theme (\"format\": \"joypro-theme\" is missing).");
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Name))
        {
            error = T("The theme has no name.");
            return false;
        }
        parsed.Name = parsed.Name.Trim();
        parsed.Base = string.Equals(parsed.Base, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Dictionary<string, string> colors = new(StringComparer.Ordinal);
        HashSet<string> known = Tokens.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach ((string key, string value) in parsed.Colors ?? [])
        {
            if (!known.Contains(key))
            {
                warnings.Add(T("Unknown colour '{0}' left out.", key));
                continue;
            }
            if (!Color.TryParse(value ?? string.Empty, out Color color))
            {
                warnings.Add(T("'{0}' is no colour ({1}); JoyPro's colour is used.", value ?? string.Empty, key));
                continue;
            }
            colors[key] = Hex(color);
        }
        parsed.Colors = colors;
        parsed.Fonts ??= new ThemeFonts();
        parsed.Fonts.LineSpacing = Math.Clamp(double.IsFinite(parsed.Fonts.LineSpacing) && parsed.Fonts.LineSpacing > 0 ? parsed.Fonts.LineSpacing : 1, 0.8, 2.0);
        parsed.Fonts.HeadingRatio = Math.Clamp(double.IsFinite(parsed.Fonts.HeadingRatio) && parsed.Fonts.HeadingRatio > 0 ? parsed.Fonts.HeadingRatio : 1, 0.8, 1.6);
        parsed.Fonts.Ui = (parsed.Fonts.Ui ?? string.Empty).Trim();
        parsed.Fonts.Mono = (parsed.Fonts.Mono ?? string.Empty).Trim();
        parsed.Fonts.Weight = ParseWeight(parsed.Fonts.Weight).ToString();
        if (parsed.TextScale is double scale)
            parsed.TextScale = double.IsFinite(scale) ? ClampScale(scale) : null;
        parsed.FilePath = path;
        theme = parsed;
        return true;
    }

    public static string Serialize(CustomTheme theme) => JsonSerializer.Serialize(theme, JsonOptions);

    // Saves the theme into the themes folder (its own file, named after the theme); returns the file.
    public static string Save(CustomTheme theme)
    {
        string path = !string.IsNullOrWhiteSpace(theme.FilePath) && Path.GetDirectoryName(Path.GetFullPath(theme.FilePath)) is string dir &&
                      string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(ThemesFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? theme.FilePath
            : UniquePath(theme.Name);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(theme), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        theme.FilePath = path;
        return path;
    }

    // A theme from elsewhere (shared by a friend): checked, then copied into the themes folder under its name.
    public static bool TryImport(string path, out CustomTheme? theme, out string error, out List<string> warnings)
    {
        if (!TryParse(SafeRead(path), path, out theme, out error, out warnings) || theme == null)
            return false;
        CustomTheme imported = theme;
        imported.FilePath = string.Empty;
        if (ListThemes().Any(t => string.Equals(t.Name, imported.Name, StringComparison.OrdinalIgnoreCase)))
            imported.Name = UniqueName(imported.Name);
        Save(imported);
        return true;
    }

    public static void Export(CustomTheme theme, string path) => File.WriteAllText(path, Serialize(theme), new UTF8Encoding(false));

    public static void Delete(CustomTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.FilePath) && File.Exists(theme.FilePath))
            File.Delete(theme.FilePath);
    }

    public static string UniqueName(string name)
    {
        HashSet<string> names = ListThemes().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string baseName = string.IsNullOrWhiteSpace(name) ? T("My theme") : name.Trim();
        if (!names.Contains(baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = baseName + " " + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private static string UniquePath(string name)
    {
        string slug = Regex.Replace((name ?? "theme").Trim(), @"[^\p{L}\p{Nd}._-]+", "-").Trim('-', '.');
        if (slug.Length == 0)
            slug = "theme";
        string path = Path.Combine(ThemesFolder, slug + ".json");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(ThemesFolder, slug + "-" + i.ToString(CultureInfo.InvariantCulture) + ".json");
        return path;
    }

    private static string? SafeRead(string path)
    {
        try
        {
            FileInfo info = new(path);
            // A theme is a few kB; a huge file is no theme.
            return info.Exists && info.Length < 2_000_000 ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- colours ----------------------------------------------------------------------------------------------------

    public static string Hex(Color c) => "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    // JoyPro's own value of a token in Light or Dark (the custom theme left out).
    private static readonly Dictionary<(string Key, bool Light), Color> Defaults = [];

    // JoyPro's Light and Dark value of every token, read once on the UI thread (App start), so a view model command on
    // another thread can start a theme from them.
    public static void CacheDefaults()
    {
        if (Application.Current == null || !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return;
        lock (Defaults)
        {
            foreach (ThemeToken token in Tokens)
            {
                Defaults[(token.Key, true)] = ReadDefault(token.Key, ThemeVariant.Light);
                Defaults[(token.Key, false)] = ReadDefault(token.Key, ThemeVariant.Dark);
            }
        }
    }

    public static Color DefaultColor(string key, ThemeVariant variant)
    {
        lock (Defaults)
        {
            if (Defaults.TryGetValue((key, variant == ThemeVariant.Light), out Color cached))
                return cached;
        }
        return Avalonia.Threading.Dispatcher.UIThread.CheckAccess() ? ReadDefault(key, variant) : (variant == ThemeVariant.Light ? Colors.White : Colors.Black);
    }

    private static Color ReadDefault(string key, ThemeVariant variant)
    {
        Application? app = Application.Current;
        if (app == null)
            return Colors.Magenta;
        foreach (IResourceProvider provider in app.Resources.MergedDictionaries.Reverse())
        {
            if (ReferenceEquals(provider, overrides))
                continue;
            if (provider.TryGetResource(key, variant, out object? value) && ColorOf(value) is Color c)
                return c;
        }
        if (app.Styles.TryGetResource(key, variant, out object? styled) && ColorOf(styled) is Color s)
            return s;
        return variant == ThemeVariant.Light ? Colors.White : Colors.Black;
    }

    public static Color? ColorOf(object? value) => value switch
    {
        // Fluent's brushes are often a colour with an opacity ("white at 20 %"): the opacity goes into the alpha.
        ISolidColorBrush b => Color.FromArgb((byte)Math.Round(b.Color.A * Math.Clamp(b.Opacity, 0, 1)), b.Color.R, b.Color.G, b.Color.B),
        Color c => c,
        IGradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
        _ => null
    };

    // Every token of a theme: its own colours, the rest from its base.
    public static Dictionary<string, Color> Resolve(CustomTheme theme)
    {
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        foreach (ThemeToken token in Tokens)
        {
            colors[token.Key] = theme.Colors.TryGetValue(token.Key, out string? hex) && Color.TryParse(hex, out Color c)
                ? c
                : DefaultColor(token.Key, theme.BaseVariant);
        }
        return colors;
    }

    // A new theme that starts as JoyPro's Light or Dark (every colour written out, so the file shows them all).
    public static CustomTheme StartFrom(ThemeVariant variant, string name)
    {
        CustomTheme theme = new() { Name = name, Base = variant == ThemeVariant.Light ? "Light" : "Dark" };
        foreach (ThemeToken token in Tokens)
            theme.Colors[token.Key] = Hex(DefaultColor(token.Key, variant));
        return theme;
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromRgb((byte)Math.Round(top.R * a + under.R * (1 - a)), (byte)Math.Round(top.G * a + under.G * (1 - a)), (byte)Math.Round(top.B * a + under.B * (1 - a)));
    }

    public sealed record ContrastProblem(ThemeContrastPair Pair, Color Foreground, Color Background, double Ratio);

    // The pairs of the theme below their minimum (semi-transparent backgrounds composited on what they stand on, the
    // page under all of it).
    public static List<ContrastProblem> CheckContrast(CustomTheme theme)
    {
        Dictionary<string, Color> colors = Resolve(theme);
        List<ContrastProblem> problems = [];
        foreach (ThemeContrastPair pair in ContrastPairs)
        {
            (Color fg, Color bg) = Shown(colors, pair);
            double ratio = Contrast(fg, bg);
            if (ratio + 0.005 < pair.Minimum)
                problems.Add(new ContrastProblem(pair, fg, bg, ratio));
        }
        return problems;
    }

    private static (Color Fg, Color Bg) Shown(Dictionary<string, Color> colors, ThemeContrastPair pair)
    {
        Color page = colors["JP.PageBackgroundBrush"];
        page = Color.FromRgb(page.R, page.G, page.B);
        Color under = pair.Under != null ? Over(colors[pair.Under], page) : page;
        Color bg = Over(colors[pair.Background], under);
        Color fg = Over(colors[pair.Foreground], bg);
        return (fg, bg);
    }

    // Fixes every pair below its minimum: the text colour gets lighter or darker (keeping its hue) until it reads; the
    // background only when no text colour would do. Returns how many colours changed.
    public static int AutoFixContrast(CustomTheme theme)
    {
        HashSet<string> changed = new(StringComparer.Ordinal);
        for (int pass = 0; pass < 4; pass++)
        {
            bool any = false;
            // A text colour used on several backgrounds (button text on normal, hover, pressed) gets one colour that
            // reads on all of them.
            foreach (IGrouping<string, ThemeContrastPair> group in ContrastPairs.GroupBy(p => p.Foreground))
            {
                Dictionary<string, Color> colors = Resolve(theme);
                List<(ThemeContrastPair Pair, Color Bg)> pairs = group.Select(p => (p, Shown(colors, p).Bg)).ToList();
                if (pairs.All(p => Contrast(Over(colors[group.Key], p.Bg), p.Bg) + 0.005 >= p.Pair.Minimum))
                    continue;
                Color original = colors[group.Key];
                Color? fixedFg = ReadableOnAll(original, pairs);
                if (fixedFg is Color fg)
                {
                    theme.Colors[group.Key] = Hex(fg);
                    changed.Add(group.Key);
                }
                else
                {
                    // No text colour reads on all of them: the backgrounds that fail move away from the best text colour.
                    Color best = Contrast(Colors.White, pairs[0].Bg) >= Contrast(Colors.Black, pairs[0].Bg) ? Colors.White : Colors.Black;
                    theme.Colors[group.Key] = Hex(best);
                    changed.Add(group.Key);
                    foreach ((ThemeContrastPair pair, Color bg) in pairs)
                    {
                        if (Contrast(best, bg) + 0.005 >= pair.Minimum)
                            continue;
                        Color b = colors[pair.Background];
                        Color fixedBg = Readable(Color.FromRgb(b.R, b.G, b.B), best, pair.Minimum + 0.1);
                        theme.Colors[pair.Background] = Hex(fixedBg);
                        changed.Add(pair.Background);
                    }
                }
                any = true;
            }
            if (!any)
                break;
        }
        return changed.Count;
    }

    // The colour with its hue, lighter or darker by as little as possible, readable on every background of the pairs.
    private static Color? ReadableOnAll(Color color, List<(ThemeContrastPair Pair, Color Bg)> pairs)
    {
        (double h, double s, double l) = ToHsl(color);
        for (double step = 0; step <= 1.0; step += 0.01)
        {
            foreach (int direction in new[] { 1, -1 })
            {
                double nl = l + direction * step;
                if (nl < 0 || nl > 1)
                    continue;
                Color candidate = FromHsl(h, s, nl, 255);
                if (pairs.All(p => Contrast(candidate, p.Bg) >= p.Pair.Minimum + 0.1))
                    return candidate;
            }
        }
        foreach (Color plain in new[] { Colors.White, Colors.Black })
        {
            if (pairs.All(p => Contrast(plain, p.Bg) >= p.Pair.Minimum))
                return plain;
        }
        return null;
    }

    // The colour with its hue and saturation, lighter or darker (whichever reaches the contrast with less change).
    public static Color Readable(Color color, Color background, double minimum)
    {
        (double h, double s, double l) = ToHsl(color);
        Color? best = null;
        double bestDistance = double.MaxValue;
        foreach (int direction in new[] { 1, -1 })
        {
            for (double step = 0; step <= 1.0; step += 0.01)
            {
                double nl = l + direction * step;
                if (nl < 0 || nl > 1)
                    break;
                Color candidate = FromHsl(h, s, nl, 255);
                if (Contrast(candidate, background) >= minimum)
                {
                    if (step < bestDistance)
                    {
                        bestDistance = step;
                        best = candidate;
                    }
                    break;
                }
            }
        }
        return best ?? (Contrast(Colors.White, background) > Contrast(Colors.Black, background) ? Colors.White : Colors.Black);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max - min > 1e-9)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l, byte a)
    {
        double r, g, b;
        if (s <= 1e-9)
            r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    private static Color Lighten(Color c, double amount)
    {
        (double h, double s, double l) = ToHsl(c);
        return FromHsl(h, s, Math.Clamp(l + amount, 0, 1), c.A);
    }

    // ---- applying ---------------------------------------------------------------------------------------------------

    // The built-in mode (Light, Dark, System) or a custom theme on top of its base.
    public static void Apply(string mode, CustomTheme? theme)
    {
        Application? app = Application.Current;
        if (app == null)
            return;
        ResourceDictionary dict = Overrides(app);
        dict.ThemeDictionaries.Clear();
        foreach (object key in dict.Keys.ToList())
            dict.Remove(key);
        if (theme != null)
        {
            ThemeVariant variant = theme.BaseVariant;
            app.RequestedThemeVariant = variant;
            ResourceDictionary colors = new();
            ResourceDictionary banner = new();
            Dictionary<string, Color> resolved = Resolve(theme);
            foreach (ThemeToken token in Tokens)
            {
                if (!theme.Colors.ContainsKey(token.Key))
                    continue;
                Color c = resolved[token.Key];
                object value = new SolidColorBrush(c);
                colors[token.Key] = value;
                if (token.Key.StartsWith("JP.Banner.", StringComparison.Ordinal))
                    banner[token.Key] = new SolidColorBrush(c);
            }
            // The banner: its colour with a glow in the middle.
            if (theme.Colors.ContainsKey("JP.Banner.BackgroundBrush") || theme.Colors.ContainsKey("JP.Banner.GlowBrush"))
            {
                colors["JP.Banner.GradientBrush"] = BannerGradient(resolved["JP.Banner.BackgroundBrush"], resolved["JP.Banner.GlowBrush"]);
                banner["JP.Banner.GradientBrush"] = BannerGradient(resolved["JP.Banner.BackgroundBrush"], resolved["JP.Banner.GlowBrush"]);
            }
            foreach ((string key, string from, double lighten) in Derived)
            {
                if (theme.Colors.ContainsKey(from))
                    colors[key] = new SolidColorBrush(lighten == 0 ? resolved[from] : Lighten(resolved[from], lighten));
            }
            if (theme.Colors.ContainsKey("JP.AccentBrush"))
            {
                Color accent = resolved["JP.AccentBrush"];
                colors["SystemAccentColor"] = accent;
                colors["SystemAccentColorDark1"] = Lighten(accent, -0.08);
                colors["SystemAccentColorDark2"] = Lighten(accent, -0.16);
                colors["SystemAccentColorDark3"] = Lighten(accent, -0.24);
                colors["SystemAccentColorLight1"] = Lighten(accent, 0.08);
                colors["SystemAccentColorLight2"] = Lighten(accent, 0.16);
                colors["SystemAccentColorLight3"] = Lighten(accent, 0.24);
            }
            dict.ThemeDictionaries[variant] = colors;
            // The banner keeps the dark theme for its buttons (ThemeVariantScope): its own colours go there too.
            if (variant != ThemeVariant.Dark)
                dict.ThemeDictionaries[ThemeVariant.Dark] = banner;
        }
        else
        {
            app.RequestedThemeVariant = mode switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        Refresh();
    }

    private static LinearGradientBrush BannerGradient(Color background, Color glow) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(background, 0), new GradientStop(glow, 0.55), new GradientStop(background, 1) }
    };

    private static ResourceDictionary Overrides(Application app)
    {
        if (overrides == null || !app.Resources.MergedDictionaries.Contains(overrides))
        {
            overrides = new ResourceDictionary();
            app.Resources.MergedDictionaries.Add(overrides);
        }
        return overrides;
    }

    // Text size and UI scale: JoyPro's sizes times the user's text size (headings also times the heading ratio).
    public static double CurrentTextScale { get; private set; } = 1.0;

    public static ThemeFonts CurrentFonts { get; private set; } = new();

    // Again with the current values (the language changed: its CJK font goes into the stacks).
    public static void ApplyTypography() => ApplyTypography(CurrentTextScale, CurrentFonts);

    public static void ApplyTypography(double textScale, ThemeFonts? fonts)
    {
        CurrentTextScale = ClampScale(textScale);
        CurrentFonts = fonts?.Clone() ?? new ThemeFonts();
        Application? app = Application.Current;
        if (app == null)
            return;
        if (Defaults.Count == 0)
            CacheDefaults();
        double scale = ClampScale(textScale);
        double ratio = fonts?.HeadingRatio is double r && r > 0 ? r : 1;
        foreach (double size in FontSizes)
            SetResource(app, FontSizeKey(size), Math.Round(size * scale * (size >= HeadingSize ? ratio : 1), 2));
        SetResource(app, "ControlContentThemeFontSize", Math.Round(14 * scale, 2));
        // A page is never laid out lower than JoyPro's page height times the text size (it scrolls instead).
        SetResource(app, "JP.PageMinHeight", scale > 1.01 ? Math.Round(PageDesignHeight * scale) : 0.0);
        SetResource(app, "JP.UiFontWeight", ParseWeight(fonts?.Weight));
        double spacing = fonts?.LineSpacing is double ls && ls > 0 ? ls : 1;
        SetResource(app, "JP.LineSpacing", Math.Round((spacing - 1) * 14 * scale, 2));
        SetResource(app, "JP.UiFont", new FontFamily(FontStack(fonts?.Ui)));
        SetResource(app, "JP.MonoFont", new FontFamily(MonoStack(fonts?.Mono)));
        Refresh();
    }

    // Only a value that changed is written: every write makes every text that uses it lay out again.
    private static void SetResource(Application app, string key, object value)
    {
        if (app.Resources.TryGetValue(key, out object? old) && Equals(old, value))
            return;
        app.Resources[key] = value;
    }

    // The chosen font first, then JoyPro's bundled stack: a letter the font lacks (Japanese, Cyrillic) comes from the
    // next font that has it. A font that is not installed on this PC (a theme from another PC) is left out.
    public static string FontStack(string? font)
    {
        string stack = Languages.FontStack(Loc.Current);
        string chosen = (font ?? string.Empty).Trim();
        return chosen.Length > 0 && IsInstalled(chosen) ? Quote(chosen) + ", " + stack : stack;
    }

    public static string MonoStack(string? font)
    {
        string chosen = (font ?? string.Empty).Trim();
        string tail = DefaultMonoFont + ", " + Languages.FontStack(Loc.Current);
        return chosen.Length > 0 && IsInstalled(chosen) ? Quote(chosen) + ", " + tail : tail;
    }

    private static string Quote(string font) => font.Contains(',') ? font.Replace(",", string.Empty) : font;

    public static bool IsInstalled(string font)
    {
        try
        {
            return InstalledFonts().Contains(font, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string>? installedFonts;

    public static IReadOnlyList<string> InstalledFonts()
    {
        if (installedFonts != null)
            return installedFonts;
        List<string> names = [];
        try
        {
            foreach (FontFamily family in FontManager.Current.SystemFonts)
                names.Add(family.Name);
        }
        catch (Exception)
        {
            // No font manager (a test without Avalonia): no installed fonts.
        }
        installedFonts = names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('@')).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return installedFonts;
    }

    public static FontWeight ParseWeight(string? weight) =>
        Enum.TryParse(weight ?? string.Empty, ignoreCase: true, out FontWeight w) && Enum.IsDefined(w) ? w : FontWeight.Normal;

    public static double ClampScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(Math.Round(scale / 0.05) * 0.05, MinScale, MaxScale) : 1.0;

    // Drawn controls (charts, markers) read the theme when they render: everything draws again.
    public static void Refresh()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Window window in desktop.Windows)
                Invalidate(window);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Invalidate(Visual root)
    {
        root.InvalidateVisual();
        foreach (Visual v in root.GetVisualDescendants())
            v.InvalidateVisual();
    }

    public static event EventHandler? Changed;

    // A resource of the theme for drawn controls ("JP.Chart.CurveBrush"), else the fallback.
    public static IBrush Brush(Visual? at, string key, string fallback)
    {
        object? value = null;
        if (at is StyledElement element && element.TryFindResource(key, (at as Control)?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant, out value) && value is IBrush own)
            return own;
        if (Application.Current != null && Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out value) && value is IBrush app)
            return app;
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
