using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Views.Controls;

// A colour setting: a swatch that opens a picker (spectrum, hue, opacity, the colour as #RRGGBB / #AARRGGBB, a few
// colours of the theme to pick from) and the colour as text next to it. Value: "#AARRGGBB" (two-way). Used by the
// theme editor (Settings > Appearance) and the cheat sheet style panel.
public sealed class ColorField : UserControl
{
    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<ColorField, string>(nameof(Value), "#FF000000", defaultBindingMode: BindingMode.TwoWay);

    // Colours offered under the spectrum (the theme's accents and status colours, black, white).
    public static readonly StyledProperty<IReadOnlyList<string>?> SwatchesProperty =
        AvaloniaProperty.Register<ColorField, IReadOnlyList<string>?>(nameof(Swatches));

    public static readonly StyledProperty<bool> ShowTextProperty =
        AvaloniaProperty.Register<ColorField, bool>(nameof(ShowText), true);

    private static readonly string[] DefaultSwatches =
    [
        "#FF000000", "#FFFFFFFF", "#FF0F172A", "#FF1E293B", "#FF64748B", "#FFE2E8F0", "#FF2563EB", "#FF38BDF8",
        "#FF16A34A", "#FF22C55E", "#FFD97706", "#FFF59E0B", "#FFDC2626", "#FFF43F5E", "#FFA855F7", "#FF14B8A6"
    ];

    private readonly Border swatch = new() { Width = 26, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
    private readonly Button swatchButton = new() { Padding = new Thickness(3), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox text = new() { MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 3) };
    private readonly ColorSpectrum spectrum = new() { Width = 220, Height = 150, Components = ColorSpectrumComponents.SaturationValue };
    private readonly ColorSlider hue = new() { ColorComponent = ColorComponent.Component1, ColorModel = ColorModel.Hsva, Orientation = Orientation.Horizontal, Height = 22 };
    private readonly ColorSlider alpha = new() { ColorComponent = ColorComponent.Alpha, ColorModel = ColorModel.Hsva, Orientation = Orientation.Horizontal, Height = 22, IsAlphaVisible = true };
    private readonly TextBox flyoutText = new() { Padding = new Thickness(6, 3) };
    private readonly WrapPanel swatchPanel = new() { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 220 };
    private bool syncing;

    public ColorField()
    {
        swatch.Bind(Border.BorderBrushProperty, swatch.GetResourceObservable("JP.BorderStrongBrush"));
        swatchButton.Content = swatch;
        StackPanel picker = new() { Spacing = 8, Width = 220 };
        picker.Children.Add(spectrum);
        picker.Children.Add(hue);
        picker.Children.Add(alpha);
        picker.Children.Add(flyoutText);
        picker.Children.Add(swatchPanel);
        swatchButton.Flyout = new Flyout { Content = picker, Placement = PlacementMode.BottomEdgeAlignedLeft };
        ToolTip.SetTip(swatchButton, Localization.Loc.T("Pick a colour"));
        ToolTip.SetTip(text, Localization.Loc.T("The colour as #RRGGBB, or #AARRGGBB with its opacity first"));

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        row.Children.Add(swatchButton);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Content = row;

        spectrum.ColorChanged += (_, e) => FromPicker(e.NewColor, spectrum);
        hue.ColorChanged += (_, e) => FromPicker(e.NewColor, hue);
        alpha.ColorChanged += (_, e) => FromPicker(e.NewColor, alpha);
        text.LostFocus += (_, _) => FromText(text.Text);
        text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                FromText(text.Text);
        };
        flyoutText.LostFocus += (_, _) => FromText(flyoutText.Text);
        flyoutText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                FromText(flyoutText.Text);
        };
        BuildSwatches();
        Show(Parse(Value));
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IReadOnlyList<string>? Swatches
    {
        get => GetValue(SwatchesProperty);
        set => SetValue(SwatchesProperty, value);
    }

    public bool ShowText
    {
        get => GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public Color Color => Parse(Value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !syncing)
            Show(Parse(Value));
        else if (change.Property == SwatchesProperty)
            BuildSwatches();
        else if (change.Property == ShowTextProperty)
            text.IsVisible = ShowText;
    }

    private static Color Parse(string? value) => Color.TryParse(value ?? string.Empty, out Color c) ? c : Colors.Black;

    private void FromText(string? value)
    {
        string t = (value ?? string.Empty).Trim();
        if (t.Length > 0 && !t.StartsWith('#') && System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"))
            t = "#" + t;
        if (Color.TryParse(t, out Color c))
            Set(c, null);
        else
            Show(Parse(Value));
    }

    private void FromPicker(Color c, object source)
    {
        if (!syncing)
            Set(c, source);
    }

    private void Set(Color c, object? source)
    {
        syncing = true;
        try
        {
            Value = ThemeService.Hex(c);
            Show(c, source);
        }
        finally
        {
            syncing = false;
        }
    }

    private void Show(Color c, object? source = null)
    {
        bool was = syncing;
        syncing = true;
        try
        {
            swatch.Background = new SolidColorBrush(c);
            string hex = c.A == 255 ? "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") : ThemeService.Hex(c);
            text.Text = hex;
            flyoutText.Text = hex;
            if (source != spectrum)
                spectrum.Color = c;
            if (source != hue)
                hue.Color = c;
            if (source != alpha)
                alpha.Color = c;
        }
        finally
        {
            syncing = was;
        }
    }

    private void BuildSwatches()
    {
        swatchPanel.Children.Clear();
        foreach (string hex in Swatches ?? DefaultSwatches)
        {
            if (!Color.TryParse(hex, out Color c))
                continue;
            Border chip = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), BorderThickness = new Thickness(1) };
            chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("JP.BorderStrongBrush"));
            Button pick = new() { Content = chip, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, Background = Brushes.Transparent };
            ToolTip.SetTip(pick, ThemeService.Hex(c));
            pick.Click += (_, _) => Set(c, null);
            swatchPanel.Children.Add(pick);
        }
    }
}
