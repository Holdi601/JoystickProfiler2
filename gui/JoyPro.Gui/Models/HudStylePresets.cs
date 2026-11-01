using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Models;

// The HUD's style presets (docs/hud.md "Styles"): a long list of complete designs for every element type. The names
// are English keys (N(), translated where shown); the first three are the styles of 2.5.0 (Glass, Neon, Minimal), so a
// hud.json of 2.5.0 keeps its look.
public static class HudStylePresets
{
    public static readonly IReadOnlyList<HudLookSettings> All = Build();

    public static HudLookSettings Default => All[0].Clone();

    public static HudLookSettings? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))?.Clone();

    private static List<HudLookSettings> Build()
    {
        List<HudLookSettings> list =
        [
            // ---- The 2.5.0 styles
            P(N("Glass"), "#38BDF8"),
            P(N("Neon"), "#38BDF8", card: "#00000000", alpha: 0.8, shadow: "Glow"),
            P(N("Minimal"), "#38BDF8", card: "#00000000", alpha: 0, border: 0, shadow: "None", glow: 0),

            // ---- Clean and modern
            P(N("Frosted light"), "#2563EB", card: "#D9F8FAFC", text: "#0F172A", dim: "#475569", track: "#260F172A", alpha: 0.25, radius: 18),
            P(N("Streamer clean"), "#6366F1", card: "#F2FFFFFF", text: "#111827", dim: "#4B5563", track: "#1F111827", alpha: 0, border: 0, radius: 22, font: "Segoe UI", chip: "Pill"),
            P(N("Streamer bold"), "#F97316", card: "#E6111111", text: "#FFFFFF", dim: "#FDBA74", alpha: 1, border: 3, radius: 14, scale: 1.3, spacing: 1, font: "Bahnschrift", bar: "Square", lever: "Square", barHeight: 10, leverWidth: 26, gimbalSize: 104),
            P(N("Compact"), "#38BDF8", scale: 0.85, radius: 10, gimbalSize: 64, barHeight: 4, leverWidth: 14, bar: "Thin", lever: "Thin"),
            P(N("Large text"), "#38BDF8", scale: 1.45, gimbalSize: 112, barHeight: 9, leverWidth: 26),
            P(N("Pastel"), "#F472B6", card: "#CC2E1F3A", text: "#FDF2F8", dim: "#F9A8D4", track: "#33FBCFE8", radius: 24, chip: "Pill", button: "Circle", gimbal: "Dot"),
            P(N("Outline only"), "#E2E8F0", card: "#00000000", alpha: 0.9, border: 1.5, shadow: "None", glow: 0, bar: "Outline", lever: "Outline", button: "Square", chip: "Square"),

            // ---- Cockpit displays
            P(N("Military green HUD"), "#39FF14", card: "#00000000", text: "#B6FF9E", dim: "#5FCC4A", track: "#3339FF14", alpha: 0, border: 0, shadow: "None", glow: 0.6,
                font: "Mono", spacing: 1.5, gimbal: "Crosshair", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Underline", radius: 0),
            P(N("Amber MFD"), "#FFB000", card: "#E6000000", text: "#FFD27A", dim: "#B37B00", track: "#33FFB000", alpha: 0.6, border: 2, radius: 4, shadow: "None", glow: 0.3,
                font: "Mono", spacing: 1, gimbal: "Square", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Square"),
            P(N("Airliner PFD"), "#22D3EE", pressed: "#E879F9", card: "#E61B1F27", text: "#FFFFFF", dim: "#9CA3AF", track: "#334B5563", alpha: 0.5, border: 1, radius: 6, shadow: "Soft", glow: 0,
                font: "Segoe UI", gimbal: "Crosshair", bar: "Square", lever: "Square", button: "Square", chip: "Square"),
            P(N("Glass cockpit"), "#00E5FF", pressed: "#FF00FF", card: "#F2050505", text: "#FFFFFF", dim: "#00BCD4", track: "#3300E5FF", alpha: 0.7, border: 1.5, radius: 8, glow: 0.4,
                font: "Segoe UI", gimbal: "Square", bar: "Square", lever: "Square"),
            P(N("Night vision"), "#7CFC00", card: "#B3001000", text: "#C7FF9E", dim: "#4F9A1C", track: "#267CFC00", alpha: 0.4, radius: 10, glow: 0.8, font: "Mono", gimbal: "Radar", bar: "Segmented", lever: "Segmented"),
            P(N("Red cockpit lighting"), "#FF3B30", card: "#D9100000", text: "#FFC2BD", dim: "#B3261E", track: "#33FF3B30", alpha: 0.5, radius: 10, glow: 0.7, gimbal: "Radar"),
            P(N("Vector wireframe"), "#00FFFF", card: "#00000000", text: "#CCFFFF", dim: "#00A3A3", track: "#3300FFFF", alpha: 0.9, border: 1, radius: 0, shadow: "Glow", glow: 1,
                font: "Mono", gimbal: "Crosshair", bar: "Outline", lever: "Outline", button: "Square", chip: "Square"),
            P(N("Retro terminal"), "#33FF66", card: "#E6001A08", text: "#33FF66", dim: "#1F9940", track: "#2633FF66", alpha: 0.7, border: 2, radius: 0, shadow: "None", glow: 0.5,
                font: "Courier New", bold: false, spacing: 1, gimbal: "Square", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Square"),
            P(N("Warbird"), "#E8C468", card: "#D9303A1E", text: "#F2E6C9", dim: "#BBA77A", track: "#33F2E6C9", alpha: 0.5, border: 2, radius: 6, glow: 0, font: "Georgia",
                gimbal: "Ring", bar: "Square", lever: "Square", button: "Circle", chip: "Square"),
            P(N("Hologram"), "#00E5FF", card: "#2600E5FF", text: "#E0FFFF", dim: "#7FDBFF", track: "#3300E5FF", alpha: 0.8, radius: 12, shadow: "Glow", glow: 1, gimbal: "Radar", bar: "Thin", lever: "Thin"),

            // ---- Games and genres
            P(N("Space sim orange"), "#FF7100", card: "#E6000000", text: "#FFB37A", dim: "#C25500", track: "#33FF7100", alpha: 0.7, border: 1.5, radius: 2, shadow: "None", glow: 0.6,
                font: "Bahnschrift", spacing: 1.5, gimbal: "Crosshair", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Square"),
            P(N("Holo cyan"), "#2DE2E6", card: "#B3062A3A", text: "#DFFBFF", dim: "#6FC7D1", track: "#262DE2E6", alpha: 0.5, radius: 4, glow: 0.7, font: "Bahnschrift", gimbal: "Radar", bar: "Thin", lever: "Thin"),
            P(N("Cyberpunk"), "#FCEE09", pressed: "#00F0FF", card: "#F2000000", text: "#FCEE09", dim: "#BDB100", track: "#33FCEE09", alpha: 1, border: 2, radius: 0, shadow: "None", glow: 0.5,
                font: "Bahnschrift", spacing: 2, gimbal: "Square", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Square"),
            P(N("Synthwave"), "#F72585", pressed: "#4CC9F0", card: "#B3240046", text: "#FFE3F3", dim: "#B5179E", track: "#33F72585", alpha: 0.7, radius: 14, shadow: "Glow", glow: 1, font: "Bahnschrift"),
            P(N("Neon pink"), "#FF2BD6", card: "#00000000", text: "#FFD6F6", dim: "#E879F9", alpha: 0.9, shadow: "Glow", glow: 1),
            P(N("Racing"), "#E10600", card: "#E6151515", text: "#FFFFFF", dim: "#A3A3A3", track: "#33FFFFFF", alpha: 1, border: 0, radius: 4, glow: 0, font: "Bahnschrift", spacing: 1,
                gimbal: "Square", bar: "Segmented", lever: "Segmented", button: "Square", chip: "Square"),
            P(N("Tactical tan"), "#C8A165", card: "#D92B2620", text: "#EDE3D1", dim: "#A8977A", track: "#33EDE3D1", alpha: 0.4, border: 1, radius: 4, glow: 0, font: "Bahnschrift", gimbal: "Crosshair", bar: "Square", lever: "Square", button: "Square"),
            P(N("Navy and gold"), "#FFD100", card: "#D9102A5C", text: "#FFFFFF", dim: "#AFC4E8", track: "#33FFFFFF", alpha: 0.8, border: 2, radius: 12, glow: 0.4, font: "Bahnschrift"),
            P(N("Stealth"), "#94A3B8", card: "#990A0A0A", text: "#CBD5E1", dim: "#64748B", track: "#1AFFFFFF", alpha: 0.2, radius: 8, shadow: "None", glow: 0, opacity: 0.8, bar: "Thin", lever: "Thin"),
            P(N("Ghost"), "#FFFFFF", card: "#00000000", text: "#FFFFFF", dim: "#CBD5E1", track: "#33FFFFFF", alpha: 0, border: 0, shadow: "None", glow: 0.3, opacity: 0.6, bar: "Thin", lever: "Thin", gimbal: "Dot"),

            // ---- Colour themes
            P(N("Arctic"), "#7DD3FC", card: "#B3E0F2FE", text: "#0C4A6E", dim: "#0369A1", track: "#330C4A6E", alpha: 0.5, radius: 18, glow: 0),
            P(N("Desert"), "#E0B872", card: "#CC3B2A17", text: "#FBEBD0", dim: "#C9A46B", track: "#33FBEBD0", alpha: 0.4, radius: 10, glow: 0.3),
            P(N("Ocean"), "#14B8A6", card: "#B3042F2E", text: "#CCFBF1", dim: "#5EEAD4", track: "#3314B8A6", radius: 16),
            P(N("Sunset"), "#FB923C", pressed: "#F43F5E", card: "#B33B0A20", text: "#FFEDD5", dim: "#FDBA74", track: "#33FB923C", radius: 16, shadow: "Glow"),
            P(N("Royal"), "#FACC15", card: "#CC2E1065", text: "#FEF9C3", dim: "#C4B5FD", track: "#33FACC15", alpha: 0.5, radius: 12),
            P(N("Carbon"), "#A3A3A3", pressed: "#F5F5F5", card: "#E6111111", text: "#F5F5F5", dim: "#737373", track: "#26FFFFFF", alpha: 0.4, radius: 4, glow: 0, gimbal: "Square", bar: "Square", lever: "Square", button: "Square", chip: "Square"),

            // ---- Readability
            P(N("High contrast"), "#FFFF00", card: "#FF000000", text: "#FFFFFF", dim: "#FFFFFF", track: "#66FFFFFF", alpha: 1, border: 3, radius: 6, shadow: "None", glow: 0, scale: 1.15, bar: "Square", lever: "Square", button: "Square", chip: "Square"),
            P(N("High contrast light"), "#0033CC", card: "#FFFFFFFF", text: "#000000", dim: "#000000", track: "#4D000000", alpha: 1, border: 3, radius: 6, shadow: "None", glow: 0, scale: 1.15, bar: "Square", lever: "Square", button: "Square", chip: "Square"),
            P(N("Monochrome"), "#FFFFFF", card: "#B3000000", text: "#FFFFFF", dim: "#A3A3A3", track: "#33FFFFFF", alpha: 0.4, radius: 10, glow: 0),
            P(N("Monochrome light"), "#111111", card: "#CCFFFFFF", text: "#111111", dim: "#525252", track: "#33111111", alpha: 0.3, radius: 10, glow: 0, shadow: "None"),
        ];
        return list;
    }

    // A preset from the Glass defaults with the given changes.
    private static HudLookSettings P(string name, string accent, string pressed = "", string card = "#B00B1220", string text = "#F1F5F9", string dim = "#94A3B8",
        string track = "#26FFFFFF", double alpha = 0.33, double border = 1, double radius = 16, string shadow = "Soft", double glow = 1, string font = "Default",
        double scale = 1, double spacing = 0.6, bool bold = true, double opacity = 1, string gimbal = "Ring", double gimbalSize = 84, string bar = "Rounded",
        double barHeight = 6, string lever = "Rounded", double leverWidth = 20, string button = "Rounded", string chip = "Pill") => new()
    {
        Name = name,
        Accent = accent,
        Pressed = pressed,
        CardBackground = card,
        Text = text,
        TextDim = dim,
        Track = track,
        CardBorderAlpha = alpha,
        CardBorderThickness = border,
        CardRadius = radius,
        CardShadow = shadow,
        Glow = glow,
        Font = font,
        TextScale = scale,
        LetterSpacing = spacing,
        BoldTitles = bold,
        Opacity = opacity,
        GimbalShape = gimbal,
        GimbalSize = gimbalSize,
        BarShape = bar,
        BarHeight = barHeight,
        LeverShape = lever,
        LeverWidth = leverWidth,
        ButtonShape = button,
        ChipShape = chip
    };
}
