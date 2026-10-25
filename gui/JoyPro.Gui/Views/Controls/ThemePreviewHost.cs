using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Views.Controls;

// The theme editor's live preview: a small copy of the main page (banner, a card with chips and buttons, the bottom
// bar) whose resources are the edited theme's colours and fonts, so it shows the theme before it is used. Version:
// bumped by the view model on every change.
public sealed class ThemePreviewHost : Border
{
    public static readonly StyledProperty<CustomTheme?> ThemeProperty =
        AvaloniaProperty.Register<ThemePreviewHost, CustomTheme?>(nameof(Theme));

    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<ThemePreviewHost, int>(nameof(Version));

    public CustomTheme? Theme
    {
        get => GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeProperty || change.Property == VersionProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Resources.Clear();
        CustomTheme? theme = Theme;
        if (theme == null)
            return;
        Dictionary<string, Color> colors = ThemeService.Resolve(theme);
        foreach ((string key, Color color) in colors)
            Resources[key] = new SolidColorBrush(color);
        Resources["JP.Banner.GradientBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(colors["JP.Banner.BackgroundBrush"], 0),
                new GradientStop(colors["JP.Banner.GlowBrush"], 0.55),
                new GradientStop(colors["JP.Banner.BackgroundBrush"], 1)
            }
        };
        Background = new SolidColorBrush(colors["JP.PageBackgroundBrush"]);
        // Plain text in the theme's text colour (not the window's, which may be another theme).
        SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, new SolidColorBrush(colors["JP.TextBrush"]));
        SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, new FontFamily(ThemeService.FontStack(theme.Fonts.Ui)));
        SetValue(Avalonia.Controls.Documents.TextElement.FontWeightProperty, ThemeService.ParseWeight(theme.Fonts.Weight));
        Resources["JP.MonoFont"] = new FontFamily(ThemeService.MonoStack(theme.Fonts.Mono));
        double ratio = theme.Fonts.HeadingRatio > 0 ? theme.Fonts.HeadingRatio : 1;
        double scale = ThemeService.CurrentTextScale;
        foreach (double size in ThemeService.FontSizes)
            Resources[ThemeService.FontSizeKey(size)] = System.Math.Round(size * scale * (size >= ThemeService.HeadingSize ? ratio : 1), 2);
        Resources["JP.LineSpacing"] = System.Math.Round((theme.Fonts.LineSpacing - 1) * 14 * scale, 2);
    }
}
