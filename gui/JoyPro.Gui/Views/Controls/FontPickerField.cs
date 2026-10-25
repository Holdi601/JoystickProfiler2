using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Views.Controls;

// A font setting: the installed fonts, each shown in itself, and "JoyPro's font" (empty value) first; a preview line
// below in the chosen font with JoyPro's fonts behind it (letters the font lacks come from them). Value: the font's
// family name (two-way). A font from a theme made on another PC that is not installed here shows as not installed.
public sealed class FontPickerField : UserControl
{
    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<FontPickerField, string>(nameof(Value), string.Empty, defaultBindingMode: BindingMode.TwoWay);

    // Only monospace fonts first (the font of key caps and input names); the others follow.
    public static readonly StyledProperty<bool> MonospaceProperty =
        AvaloniaProperty.Register<FontPickerField, bool>(nameof(Monospace));

    public static readonly StyledProperty<string> PreviewTextProperty =
        AvaloniaProperty.Register<FontPickerField, string>(nameof(PreviewText), string.Empty);

    private readonly ComboBox box = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 220, MaxDropDownHeight = 420 };
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) };
    private readonly TextBlock missing = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool syncing;

    private sealed record Choice(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    public FontPickerField()
    {
        box.ItemTemplate = new FuncDataTemplate<Choice>((choice, _) =>
        {
            TextBlock t = new() { Text = choice?.Label ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis };
            if (choice != null && choice.Name.Length > 0)
                t.FontFamily = new FontFamily(ThemeService.FontStack(choice.Name));
            return t;
        });
        box.SelectionChanged += (_, _) =>
        {
            if (!syncing && box.SelectedItem is Choice c)
                Value = c.Name;
        };
        preview.Classes.Add("caption");
        missing.Classes.Add("caption");
        missing.Bind(TextBlock.ForegroundProperty, missing.GetResourceObservable("JP.WarnTextBrush"));
        StackPanel panel = new() { Spacing = 2 };
        panel.Children.Add(box);
        panel.Children.Add(preview);
        panel.Children.Add(missing);
        Content = panel;
        Fill();
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Monospace
    {
        get => GetValue(MonospaceProperty);
        set => SetValue(MonospaceProperty, value);
    }

    public string PreviewText
    {
        get => GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
            Select();
        else if (change.Property == MonospaceProperty)
            Fill();
        else if (change.Property == PreviewTextProperty)
            ShowPreview();
    }

    private static readonly string[] MonospaceHints = ["mono", "consol", "courier", "code", "fixed", "terminal", "typewriter", "lucida console"];

    private void Fill()
    {
        IEnumerable<string> fonts = ThemeService.InstalledFonts();
        if (Monospace)
            fonts = fonts.OrderBy(f => MonospaceHints.Any(h => f.Contains(h, System.StringComparison.OrdinalIgnoreCase)) ? 0 : 1).ThenBy(f => f, System.StringComparer.OrdinalIgnoreCase);
        List<Choice> choices = [new Choice(string.Empty, Localization.Loc.T("JoyPro's font"))];
        choices.AddRange(fonts.Select(f => new Choice(f, f)));
        syncing = true;
        box.ItemsSource = choices;
        syncing = false;
        Select();
    }

    private void Select()
    {
        string value = (Value ?? string.Empty).Trim();
        syncing = true;
        try
        {
            List<Choice> choices = (box.ItemsSource as List<Choice>) ?? [];
            Choice? found = choices.FirstOrDefault(c => string.Equals(c.Name, value, System.StringComparison.OrdinalIgnoreCase));
            if (found == null && value.Length > 0)
            {
                // A theme from another PC: its font is not here; JoyPro's font stands in.
                found = new Choice(value, value);
                choices.Insert(1, found);
                box.ItemsSource = null;
                box.ItemsSource = choices;
            }
            box.SelectedItem = found ?? choices.FirstOrDefault();
            bool installed = value.Length == 0 || ThemeService.IsInstalled(value);
            missing.IsVisible = !installed;
            missing.Text = installed ? string.Empty : Localization.Loc.T("{0} is not installed on this PC: JoyPro's font is used instead.", value);
        }
        finally
        {
            syncing = false;
        }
        ShowPreview();
    }

    private void ShowPreview()
    {
        string sample = PreviewText.Length > 0 ? PreviewText : Localization.Loc.T("Throttle, gear and flaps in every game · 0123456789");
        preview.Text = sample + "  Ж Ω 日本語 한국어";
        string stack = Monospace ? ThemeService.MonoStack(Value) : ThemeService.FontStack(Value);
        preview.FontFamily = new FontFamily(stack);
    }
}
