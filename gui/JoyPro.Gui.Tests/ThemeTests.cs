using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Styling;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The user's own themes (Settings > Appearance, Services/ThemeService.cs): save / load / import / export, a broken file,
// the chosen theme, text size and UI scale surviving a restart, the contrast check and its fix, fonts that are not
// installed, and that no colour is written into the XAML any more (so a theme really changes everything).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class ThemeTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "themes-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public ThemeTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // A warm light theme that is not JoyPro's: every surface, text and accent its own (also used by ThemeContrastTests).
    internal static CustomTheme SampleTheme()
    {
        CustomTheme theme = ThemeService.StartFrom(ThemeVariant.Light, "Sand");
        Dictionary<string, string> colors = new()
        {
            ["JP.PageBackgroundBrush"] = "#FFF3EDE2", ["JP.SurfaceBrush"] = "#FFFFFBF4", ["JP.SurfaceAltBrush"] = "#FFF8F1E6",
            ["JP.SurfaceRaisedBrush"] = "#FFF1E7D8", ["JP.BorderBrush"] = "#FFE0D3BF", ["JP.BorderStrongBrush"] = "#FFC9B79B",
            ["JP.TextBrush"] = "#FF2B2118", ["JP.TextMutedBrush"] = "#FF6B5643", ["JP.AccentBrush"] = "#FF9A3412",
            ["JP.AccentTextBrush"] = "#FF9A3412", ["JP.AccentSoftBrush"] = "#269A3412", ["JP.Banner.BackgroundBrush"] = "#FF3B2416",
            ["JP.Banner.GlowBrush"] = "#FF7C3A12", ["JP.Banner.AccentBrush"] = "#FFF59E0B", ["JP.Banner.TextBrush"] = "#FFFDE7C8",
            ["JP.BottomBar.BackgroundBrush"] = "#FFEADFCD", ["JP.Nav.ButtonBrush"] = "#FFF8F1E6", ["JP.Nav.TextBrush"] = "#FF2B2118",
            ["JP.Nav.SelectedBrush"] = "#FF9A3412", ["JP.Nav.SelectedTextBrush"] = "#FFFFFFFF", ["ButtonBackground"] = "#FFEDE2D0",
            ["ButtonBackgroundPointerOver"] = "#FFE4D6C0", ["ButtonBackgroundPressed"] = "#FFD9C8AE", ["ButtonForeground"] = "#FF2B2118",
            ["ListBoxItemBackgroundSelected"] = "#FFF0D9C2",
        };
        foreach ((string key, string value) in colors)
            theme.Colors[key] = value;
        theme.Fonts = new ThemeFonts { Ui = "Georgia", Mono = "Courier New", Weight = "Normal", LineSpacing = 1.1, HeadingRatio = 1.1 };
        ThemeService.AutoFixContrast(theme);
        return theme;
    }

    [Fact]
    public void Theme_SavesAndLoads_WithItsColoursAndFonts()
    {
        AvaloniaRenderThread.Run(() =>
        {
            CustomTheme theme = SampleTheme();
            string path = ThemeService.Save(theme);
            Assert.StartsWith(ThemeService.ThemesFolder, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(ThemeService.TryLoad(path, out CustomTheme? loaded, out string error), error);
            Assert.Equal("Sand", loaded!.Name);
            Assert.Equal("Light", loaded.Base);
            Assert.Equal(theme.Colors["JP.PageBackgroundBrush"], loaded.Colors["JP.PageBackgroundBrush"]);
            Assert.Equal(ThemeService.Tokens.Length, loaded.Colors.Count);
            Assert.Equal("Georgia", loaded.Fonts.Ui);
            Assert.Equal(1.1, loaded.Fonts.LineSpacing, 3);
            Assert.Contains(ThemeService.ListThemes(), t => t.Name == "Sand");
            // Export and import elsewhere: a second theme of the same name gets its own name.
            string shared = Path.Combine(temp, "shared.json");
            ThemeService.Export(loaded, shared);
            Assert.True(ThemeService.TryImport(shared, out CustomTheme? imported, out string importError, out _), importError);
            Assert.Equal("Sand 2", imported!.Name);
            Assert.Equal(2, ThemeService.ListThemes().Count);
        });
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{\"name\": \"No format\", \"colors\": {}}")]
    [InlineData("{\"format\": \"joypro-theme\", \"colors\": {}}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void MalformedThemeFile_IsRefused_AndNothingIsSaved(string json)
    {
        string file = Path.Combine(temp, "broken.json");
        File.WriteAllText(file, json);
        Assert.False(ThemeService.TryImport(file, out _, out string error, out _));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(ThemeService.ListThemes());
    }

    [Fact]
    public void ThemeFile_KeepsGoodColours_DropsBadOnes_AndClampsFonts()
    {
        string file = Path.Combine(temp, "half.json");
        File.WriteAllText(file, "{\"format\":\"joypro-theme\",\"name\":\"Half\",\"base\":\"light\",\"colors\":{\"JP.TextBrush\":\"#123456\",\"JP.SurfaceBrush\":\"blue-ish\",\"NotAToken\":\"#FFFFFF\"},\"fonts\":{\"ui\":\"No Such Font 4711\",\"lineSpacing\":9,\"headingRatio\":-1,\"weight\":\"heavy-ish\"},\"textScale\":7}");
        Assert.True(ThemeService.TryParse(File.ReadAllText(file), file, out CustomTheme? theme, out string error, out List<string> warnings), error);
        Assert.Equal("Light", theme!.Base);
        Assert.Equal("#FF123456", theme.Colors["JP.TextBrush"]);
        Assert.False(theme.Colors.ContainsKey("JP.SurfaceBrush"));
        Assert.False(theme.Colors.ContainsKey("NotAToken"));
        Assert.Equal(2, warnings.Count);
        Assert.Equal(2.0, theme.Fonts.LineSpacing);
        Assert.Equal(1.0, theme.Fonts.HeadingRatio);
        Assert.Equal("Normal", theme.Fonts.Weight);
        Assert.Equal(ThemeService.MaxScale, theme.TextScale);
        // A font this PC does not have: JoyPro's fonts stand in (a theme shared from another PC).
        Assert.Equal(Localization.Languages.FontStack(Localization.Loc.Current), ThemeService.FontStack(theme.Fonts.Ui));
    }

    [Fact]
    public void ChosenTheme_TextSize_AndUiScale_SurviveARestart_AndTheUsersTextSizeWins()
    {
        AvaloniaRenderThread.Run(() =>
        {
            CustomTheme theme = SampleTheme();
            theme.TextScale = 1.3;
            ThemeService.Save(theme);
            MainWindowViewModel first = new(new UiDesignRenderTests.DesignFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            try
            {
                Assert.Contains(first.CustomThemes, t => t.Name == "Sand");
                first.UseCustomThemeCommand.Execute(first.CustomThemes.Single(t => t.Name == "Sand"));
                Assert.True(first.IsCustomThemeActive);
                // No text size of his own yet: the theme's.
                Assert.Equal(130, first.TextScalePercent);
                first.TextScalePercent = 150;
                first.UiScalePercent = 120;
            }
            finally
            {
                first.StopGameConfigWatcher();
            }
            MainWindowViewModel second = new(new UiDesignRenderTests.DesignFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            try
            {
                Assert.Equal(MainWindowViewModel.CustomThemeMode, second.ThemeMode);
                Assert.Equal("Sand", second.ThemeModeLabel);
                Assert.Equal(150, second.TextScalePercent);
                Assert.Equal(1.2, second.UiScale, 3);
                // Ctrl+0: the theme's text size again.
                second.ResetTextScaleCommand.Execute(null);
                Assert.Equal(130, second.TextScalePercent);
                second.SetThemeLightCommand.Execute(null);
                Assert.Equal(100, second.TextScalePercent);
            }
            finally
            {
                second.StopGameConfigWatcher();
                ThemeService.Apply("System", null);
                ThemeService.ApplyTypography(1.0, null);
            }
        });
    }

    [Fact]
    public void ContrastCheck_FindsUnreadableText_AndTheFixMakesItReadable()
    {
        AvaloniaRenderThread.Run(() =>
        {
            CustomTheme theme = ThemeService.StartFrom(ThemeVariant.Dark, "Bad");
            theme.Colors["JP.TextMutedBrush"] = theme.Colors["JP.SurfaceBrush"];
            theme.Colors["ButtonForeground"] = "#FF777777";
            theme.Colors["ButtonBackground"] = "#FF707070";
            Assert.Contains(ThemeService.CheckContrast(theme), p => p.Pair.Foreground == "JP.TextMutedBrush");
            int changed = ThemeService.AutoFixContrast(theme);
            Assert.True(changed >= 2);
            Assert.Empty(ThemeService.CheckContrast(theme));
            // JoyPro's own themes pass their own check.
            Assert.Empty(ThemeService.CheckContrast(ThemeService.StartFrom(ThemeVariant.Dark, "d")));
            Assert.Empty(ThemeService.CheckContrast(ThemeService.StartFrom(ThemeVariant.Light, "l")));
            Assert.Empty(ThemeService.CheckContrast(SampleTheme()));
        });
    }

    [Fact]
    public void TextSize_ScalesEveryFontSizeResource_HeadingsByTheRatio()
    {
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                ThemeService.ApplyTypography(1.5, new ThemeFonts { HeadingRatio = 1.2, LineSpacing = 1.5, Weight = "SemiBold" });
                Avalonia.Application app = Avalonia.Application.Current!;
                Assert.True(app.TryGetResource(ThemeService.FontSizeKey(12), null, out object? body));
                Assert.Equal(18.0, (double)body!, 2);
                Assert.True(app.TryGetResource(ThemeService.FontSizeKey(20), null, out object? heading));
                Assert.Equal(36.0, (double)heading!, 2);
                Assert.True(app.TryGetResource("ControlContentThemeFontSize", null, out object? control));
                Assert.Equal(21.0, (double)control!, 2);
                Assert.True(app.TryGetResource("JP.UiFontWeight", null, out object? weight));
                Assert.Equal(FontWeight.SemiBold, weight);
                Assert.True(app.TryGetResource("JP.LineSpacing", null, out object? spacing));
                Assert.Equal(10.5, (double)spacing!, 2);
            }
            finally
            {
                ThemeService.ApplyTypography(1.0, null);
            }
        });
    }

    // Every colour goes through a theme resource: no colour literal in the XAML except the definitions of the tokens
    // themselves (Design.axaml, Icons.axaml), the logo and its concepts (Branding*.axaml), Fluent's base palette in
    // App.axaml and the in-game HUD overlay (its colours are the HUD designer's own settings).
    [Fact]
    public void Xaml_HasNoHardCodedColours()
    {
        string root = Path.Combine(RepoRoot(), "gui", "JoyPro.Gui");
        string[] allowedFiles = ["Branding.axaml", "BrandingConcepts.axaml", "HudSurface.axaml", "HudWindow.axaml"];
        HashSet<string> tokens = ThemeService.Tokens.Select(t => t.Key).Concat(["JP.HitTestBrush", "JP.Banner.GradientBrush", "ExpanderHeaderBackground",
            "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed", "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderBrushPointerOver",
            "ExpanderHeaderBorderBrushPressed", "ExpanderContentBackground", "ExpanderContentBorderBrush", "ListBoxItemBackgroundSelectedPointerOver",
            "ListBoxItemBackgroundSelectedPressed", "AccentTintBrush"]).ToHashSet(StringComparer.Ordinal);
        Regex colour = new(@"(?<attr>[\w.:]+)=""(?<value>#[0-9A-Fa-f]{3,8}|(?:White|Black|Red|Gray|Grey|Green|Blue|Yellow|Orange|DimGray|LightGray|SteelBlue))""");
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                continue;
            string name = Path.GetFileName(file);
            if (allowedFiles.Contains(name))
                continue;
            string[] lines = File.ReadAllLines(file);
            string? openKey = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match key = Regex.Match(line, @"x:Key=""(?<key>[^""]+)""");
                if (key.Success)
                    openKey = key.Groups["key"].Value;
                foreach (Match m in colour.Matches(line))
                {
                    string attr = m.Groups["attr"].Value;
                    // A text box's placeholder ("#00FF00" as an example colour) is a text.
                    if (attr is "Watermark" or "PlaceholderText" or "Text")
                        continue;
                    // A token's own definition (and the stops of the banner's gradient).
                    if ((name is "Design.axaml" or "Icons.axaml") && openKey != null && tokens.Contains(openKey))
                        continue;
                    // Fluent's base palette (the theme service sets SystemAccentColor over it).
                    if (name == "App.axaml" && line.Contains("ColorPaletteResources", StringComparison.Ordinal))
                        continue;
                    found.Add(name + ":" + (i + 1) + ": " + m.Value);
                }
                if (line.Contains("/>", StringComparison.Ordinal) && !line.Contains("<GradientStop", StringComparison.Ordinal) || line.Contains("</LinearGradientBrush>", StringComparison.Ordinal))
                    openKey = line.Contains("x:Key=", StringComparison.Ordinal) && !line.TrimEnd().EndsWith("/>", StringComparison.Ordinal) ? openKey : null;
            }
        }
        Assert.True(found.Count == 0, found.Count + " colour(s) written into the XAML:" + Environment.NewLine + string.Join(Environment.NewLine, found.Take(60)));
    }

    // Every font goes through a font resource: no font name in the XAML except the logo files.
    [Fact]
    public void Xaml_HasNoHardCodedFontFamilies()
    {
        string root = Path.Combine(RepoRoot(), "gui", "JoyPro.Gui");
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || name is "Branding.axaml" or "BrandingConcepts.axaml" or "HudSurface.axaml" or "HudWindow.axaml")
                continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(lines[i], @"FontFamily""?\s*(?:Value)?=""(?<v>[^""{][^""]*)"""))
                    found.Add(name + ":" + (i + 1) + ": " + m.Value);
                if (lines[i].Contains("<FontFamily x:Key=", StringComparison.Ordinal) && !lines[i].Contains("JP.MonoFont", StringComparison.Ordinal))
                    found.Add(name + ":" + (i + 1) + ": " + lines[i].Trim());
            }
        }
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }
}
