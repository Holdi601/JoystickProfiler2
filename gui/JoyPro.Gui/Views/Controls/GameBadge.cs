using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JoyPro.Gui.Views.Controls;

// A game's own picture when JoyPro has none: a JoyPro-drawn badge (a colour gradient per game, a simple vector motif
// from Styles/Icons.axaml and the game's short name). No artwork of the game is used. When the game's executable icon
// is known (Star Citizen, Falcon BMS, DCS standalone), or the publisher's mark downloaded from its site (Prepar3D,
// Falcon BMS), Logo shows it in place of the motif. Lies under the cover image, which hides it once a cover (Steam's
// on this PC, or the downloaded one) is loaded.
public sealed class GameBadge : Control
{
    public static readonly StyledProperty<string> GameIdProperty =
        AvaloniaProperty.Register<GameBadge, string>(nameof(GameId), string.Empty);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<GameBadge, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<IImage?> LogoProperty =
        AvaloniaProperty.Register<GameBadge, IImage?>(nameof(Logo));

    static GameBadge()
    {
        AffectsRender<GameBadge>(GameIdProperty, TitleProperty, LogoProperty);
    }

    public string GameId
    {
        get => GetValue(GameIdProperty);
        set => SetValue(GameIdProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IImage? Logo
    {
        get => GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    // game id -> short name, colour, motif.
    private static readonly Dictionary<string, (string Short, string Color, string Motif)> GameStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DCS"] = ("DCS", "#1F4E79", "Icon.Airplane"),
        ["IL2Game"] = ("IL-2", "#6B5426", "Icon.Airplane"),
        ["IL2Korea"] = ("IL-2 Korea", "#5E3A1E", "Icon.Airplane"),
        ["MSFS"] = ("MSFS", "#0E5A8A", "Icon.Airplane"),
        ["MSFS2024"] = ("MSFS 2024", "#0E5A8A", "Icon.Airplane"),
        ["MSFS2020"] = ("MSFS 2020", "#155F80", "Icon.Airplane"),
        ["XPlane"] = ("X-Plane", "#24476B", "Icon.Airplane"),
        ["XPlane12"] = ("X-Plane 12", "#24476B", "Icon.Airplane"),
        ["XPlane11"] = ("X-Plane 11", "#2B4F66", "Icon.Airplane"),
        ["FalconBMS"] = ("Falcon BMS", "#3B4A56", "Icon.Airplane"),
        ["WarThunder"] = ("War Thunder", "#7A1F1F", "Icon.Airplane"),
        ["VTOLVR"] = ("VTOL VR", "#245B4E", "Icon.Airplane"),
        ["VTOLVRNative"] = ("VTOL VR (native)", "#245B4E", "Icon.Airplane"),
        ["NuclearOption"] = ("Nuclear Option", "#5A5A1E", "Icon.Airplane"),
        ["Wardogs"] = ("WARDOGS", "#4A3B6B", "Icon.Airplane"),
        ["Arma2"] = ("Arma 2", "#3F4F2A", "Icon.Helicopter"),
        ["Arma2OA"] = ("Arma 2 OA", "#4A4A2A", "Icon.Helicopter"),
        ["Arma3"] = ("Arma 3", "#2F5233", "Icon.Helicopter"),
        ["ArmaReforger"] = ("Arma Reforger", "#355E3B", "Icon.Helicopter"),
        ["HLLVietnam"] = ("Hell Let Loose Vietnam", "#4E5B2A", "Icon.Helicopter"),
        ["Battlefield6"] = ("Battlefield 6", "#3A4A5C", "Icon.Airplane"),
        ["Battlefield2042"] = ("Battlefield 2042", "#2E4057", "Icon.Airplane"),
        ["AceCombat8"] = ("ACE COMBAT 8", "#5B2A2A", "Icon.Airplane"),
        ["AceCombat7"] = ("ACE COMBAT 7", "#6B3A1E", "Icon.Airplane"),
        ["ProjectWingman"] = ("Project Wingman", "#5A4A1A", "Icon.Airplane"),
        ["X4"] = ("X4", "#2B2F6B", "Icon.Rocket"),
        ["EliteDangerous"] = ("Elite Dangerous", "#8A4B0E", "Icon.Rocket"),
        ["StarCitizen"] = ("Star Citizen", "#1E3550", "Icon.Rocket"),
        ["IL21946"] = ("IL-2 1946", "#7A5A2E", "Icon.Airplane"),
        ["IL2CoD"] = ("Cliffs of Dover", "#4F5F3A", "Icon.Airplane"),
        ["IL2CoDBlitz"] = ("Cliffs of Dover Blitz", "#5C4A2A", "Icon.Airplane"),
        ["FSX"] = ("FSX", "#2E5E8C", "Icon.Airplane"),
        ["FSXSE"] = ("FSX Steam Edition", "#2A6E7A", "Icon.Airplane"),
        ["P3D4"] = ("Prepar3D v4", "#30465E", "Icon.Airplane"),
        ["P3D5"] = ("Prepar3D v5", "#2C4F6E", "Icon.Airplane"),
        ["P3D6"] = ("Prepar3D v6", "#294E7E", "Icon.Airplane"),
        ["GRebels"] = ("G-Rebels", "#5E2E4A", "Icon.Airplane"),
        ["TinyCombatArena"] = ("Tiny Combat Arena", "#4A5E2E", "Icon.Airplane"),
    };

    // One name per game everywhere in the GUI (cards, Settings > Games, lists, chips, pickers, status lines): the game's
    // own full name, the same as engine games installs / games integrations (without "(experimental)"). Only the badge,
    // a small tile, shows the short text above; GuiGameNamesTests keeps the two lists in step.
    private static readonly Dictionary<string, string> FullNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DCS"] = "DCS World",
        ["IL2Game"] = "IL-2 Sturmovik: Great Battles",
        ["IL2Korea"] = "IL-2 Series: Korea",
        ["StarCitizen"] = "Star Citizen",
        ["MSFS2024"] = "Microsoft Flight Simulator 2024",
        ["MSFS2020"] = "Microsoft Flight Simulator 2020",
        ["MSFS"] = "Microsoft Flight Simulator",
        ["XPlane12"] = "X-Plane 12",
        ["XPlane11"] = "X-Plane 11",
        ["XPlane"] = "X-Plane",
        ["WarThunder"] = "War Thunder",
        ["Arma3"] = "Arma 3",
        ["Arma2"] = "Arma 2",
        ["Arma2OA"] = "Arma 2: Operation Arrowhead",
        ["ArmaReforger"] = "Arma Reforger",
        ["Wardogs"] = "WARDOGS",
        ["HLLVietnam"] = "Hell Let Loose: Vietnam",
        ["Battlefield6"] = "Battlefield 6",
        ["Battlefield2042"] = "Battlefield 2042",
        ["AceCombat8"] = "ACE COMBAT 8: Wings of Theve",
        ["AceCombat7"] = "ACE COMBAT 7: Skies Unknown",
        ["ProjectWingman"] = "Project Wingman",
        ["X4"] = "X4: Foundations",
        ["EliteDangerous"] = "Elite Dangerous",
        ["FalconBMS"] = "Falcon BMS",
        ["VTOLVR"] = "VTOL VR (BYOJoystick mod)",
        ["VTOLVRNative"] = "VTOL VR (native rudder and brakes)",
        ["GRebels"] = "G-Rebels",
        ["TinyCombatArena"] = "Tiny Combat Arena",
        ["NuclearOption"] = "Nuclear Option",
        ["IL21946"] = "IL-2 Sturmovik: 1946",
        ["IL2CoD"] = "IL-2 Sturmovik: Cliffs of Dover",
        ["IL2CoDBlitz"] = "IL-2 Sturmovik: Cliffs of Dover Blitz",
        ["FSX"] = "Microsoft Flight Simulator X",
        ["FSXSE"] = "Microsoft Flight Simulator X: Steam Edition",
        ["P3D4"] = "Prepar3D v4",
        ["P3D5"] = "Prepar3D v5",
        ["P3D6"] = "Prepar3D v6",
    };

    // The full readable name of a game (FullNames, else the badge's text); empty for an unknown id.
    internal static string FullName(string gameId)
    {
        string id = gameId ?? string.Empty;
        if (FullNames.TryGetValue(id, out string? full))
            return full;
        return GameStyles.TryGetValue(id, out (string Short, string Color, string Motif) known) ? known.Short : string.Empty;
    }

    internal static (string Short, Color Color, string Motif) StyleOf(string gameId, string title)
    {
        if (GameStyles.TryGetValue(gameId ?? string.Empty, out (string Short, string Color, string Motif) known))
            return (known.Short, Color.Parse(known.Color), known.Motif);
        // Another game: a stable colour from its id, the airplane motif and its name.
        int hash = 17;
        foreach (char c in gameId ?? string.Empty)
            hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
        double hue = Math.Abs(hash % 360);
        return (string.IsNullOrWhiteSpace(title) ? gameId ?? string.Empty : title, FromHsv(hue, 0.55, 0.45), "Icon.Airplane");
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4)
            return;
        (string text, Color color, string motif) = StyleOf(GameId, Title);
        Color dark = Color.FromRgb((byte)(color.R * 0.45), (byte)(color.G * 0.45), (byte)(color.B * 0.45));
        LinearGradientBrush background = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(color, 0), new GradientStop(dark, 1) }
        };
        double radius = Math.Min(8, bounds.Width / 8);
        context.DrawRectangle(background, null, bounds, radius, radius);

        // The motif (or the game's own icon) in the upper part.
        double size = Math.Min(bounds.Width * 0.62, bounds.Height * 0.45);
        Rect motifRect = new((bounds.Width - size) / 2, bounds.Height * 0.12, size, size);
        if (Logo != null)
        {
            // Square icons fill the motif area; a wide or tall mark (a publisher's logo) keeps its proportions.
            Size logo = Logo.Size;
            double fit = logo.Width > 0 && logo.Height > 0 ? Math.Min(size / logo.Width, size / logo.Height) : 1;
            Size drawn = logo.Width > 0 && logo.Height > 0 ? new Size(logo.Width * fit, logo.Height * fit) : motifRect.Size;
            context.DrawImage(Logo, new Rect(motifRect.Center.X - drawn.Width / 2, motifRect.Center.Y - drawn.Height / 2, drawn.Width, drawn.Height));
        }
        else if (Application.Current != null && Application.Current.TryFindResource(motif, out object? value) && value is Geometry geometry)
        {
            Rect g = geometry.Bounds;
            if (g.Width > 0 && g.Height > 0)
            {
                double scale = size / Math.Max(g.Width, g.Height);
                Matrix transform = Matrix.CreateTranslation(-g.X - g.Width / 2, -g.Y - g.Height / 2) *
                                   Matrix.CreateScale(scale, scale) *
                                   Matrix.CreateTranslation(motifRect.Center.X, motifRect.Center.Y);
                using (context.PushTransform(transform))
                    context.DrawGeometry(new SolidColorBrush(Colors.White, 0.85), null, geometry);
            }
        }

        // The short name in the lower part: the biggest size at which it fits (wrapped at spaces, whole words).
        double room = Math.Max(1, bounds.Bottom - motifRect.Bottom - 4);
        double width = Math.Max(1, bounds.Width - 6);
        Typeface typeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        double fontSize = Math.Min(bounds.Width / 4.2, 18);
        FormattedText formatted = Format(text, typeface, fontSize, width);
        while (fontSize > 7 && (formatted.Height > room || LongestWord(text, typeface, fontSize) > width))
        {
            fontSize -= 0.5;
            formatted = Format(text, typeface, fontSize, width);
        }
        formatted.MaxTextHeight = room;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        double top = motifRect.Bottom + Math.Max(2, (bounds.Bottom - motifRect.Bottom - formatted.Height) / 2);
        context.DrawText(formatted, new Point(3, top));
    }

    private static FormattedText Format(string text, Typeface typeface, double fontSize, double width)
    {
        FormattedText formatted = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, Math.Max(7, fontSize), Brushes.White)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center
        };
        return formatted;
    }

    // The widest word: a word must not be broken inside, so the size shrinks until every word fits on its line.
    private static double LongestWord(string text, Typeface typeface, double fontSize)
    {
        double widest = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            FormattedText measured = new(word, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White);
            widest = Math.Max(widest, measured.WidthIncludingTrailingWhitespace);
        }
        return widest;
    }
}
