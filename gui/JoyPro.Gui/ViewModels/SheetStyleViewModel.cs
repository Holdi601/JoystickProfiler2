using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// A modifier and its colour in the style panel's list.
public sealed partial class SheetModifierRow : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string color = "#FF38BDF8";
}

// A preset in the style panel's list: one of JoyPro's four or one the user saved.
public sealed record SheetStylePresetChoice(string Id, string Title, bool IsUser)
{
    public override string ToString() => Title;
}

// The style panel of the cheat sheets (Views/Controls/SheetStyleEditor.axaml; docs/ui/layout-redesign.md "Sheet style"):
// font, size and weight; the label colours; page, tables, header row, table text and lines; the marker colour per kind
// of input; the modifier colours as a list; JoyPro's drawing on the sheet. Presets (JoyPro default, Print, High contrast,
// Dark sheet, the user's own) fill it. Every change raises Changed with the new style (Current); the host keeps it
// (the global style or the shown device's own) and repaints the editor and the preview.
public sealed partial class SheetStyleViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> WeightChoices = [N("Normal"), N("Semibold"), N("Bold")];

    private static readonly string[] WeightValues = ["normal", "semibold", "bold"];

    private bool loading;
    private string name = string.Empty;
    private string drawingSpec = string.Empty;

    public SheetStyleViewModel(bool deviceScope)
    {
        DeviceScope = deviceScope;
        Modifiers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SheetModifierRow row in e.NewItems)
                    row.PropertyChanged += OnRowChanged;
            if (e.OldItems != null)
                foreach (SheetModifierRow row in e.OldItems)
                    row.PropertyChanged -= OnRowChanged;
            OnPropertyChanged(nameof(HasModifiers));
            Raise();
        };
        RefreshPresets();
        Load(SheetStyle.Preset("joypro"));
    }

    // The panel of the Layout page (the device's own style can be switched on there) or the cheat sheets' (global).
    public bool DeviceScope { get; }

    public event EventHandler<SheetStyle>? Changed;

    // "This device uses its own style" was switched (the host moves the style between global and the device).
    public event EventHandler<bool>? OwnStyleSwitched;

    public IReadOnlyList<string> Weights => WeightChoices;

    public ObservableCollection<SheetModifierRow> Modifiers { get; } = [];

    public ObservableCollection<SheetStylePresetChoice> Presets { get; } = [];

    public bool HasModifiers => Modifiers.Count > 0;

    [ObservableProperty] private string fontFamily = string.Empty;
    [ObservableProperty] private double fontSize = SheetStyle.BaseFontSize;
    [ObservableProperty] private string fontWeight = WeightChoices[0];
    [ObservableProperty] private string textColor = "#FFFFFFFF";
    [ObservableProperty] private string outlineColor = "#FF000000";
    [ObservableProperty] private string labelBackground = "#88000000";
    [ObservableProperty] private string pageBackground = "#FF2B3038";
    [ObservableProperty] private string tableBackground = "#FFF7F7F7";
    [ObservableProperty] private string tableBorder = "#FF0E1116";
    [ObservableProperty] private string headerBackground = "#FFD5DAE1";
    [ObservableProperty] private string headerText = "#FF0E1116";
    [ObservableProperty] private string tableText = "#FF0E1116";
    [ObservableProperty] private string lineColor = "#FFE6E8EB";
    [ObservableProperty] private string markerButton = "#FF22C55E";
    [ObservableProperty] private string markerAxis = "#FF38BDF8";
    [ObservableProperty] private string markerHat = "#FFA855F7";
    [ObservableProperty] private string markerSlider = "#FFFBBF24";

    // The preview of the panel (Views/Controls/SheetStylePreview.cs): a new object on every change.
    [ObservableProperty] private SheetStyle previewStyle = SheetStyle.Preset("joypro");

    [ObservableProperty] private SheetStylePresetChoice? selectedPreset;

    [ObservableProperty] private string newPresetName = string.Empty;

    [ObservableProperty] private string status = string.Empty;

    // JoyPro's drawing on the sheet (the export drawing style for the global style, the device style's own otherwise).
    [ObservableProperty] private DrawingStyleOptions? drawing;

    // ---- the device's own style (Layout page) ----

    // A device is shown whose sheet can carry a style of its own.
    [ObservableProperty] private bool canOwnStyle;

    [ObservableProperty] private bool usesOwnStyle;

    // "All cheat sheets" / "Only <device>".
    [ObservableProperty] private string scopeText = string.Empty;

    public bool ShowOwnStyleToggle => DeviceScope && CanOwnStyle;

    public bool CanDeletePreset => SelectedPreset is { IsUser: true };

    partial void OnCanOwnStyleChanged(bool value) => OnPropertyChanged(nameof(ShowOwnStyleToggle));

    partial void OnUsesOwnStyleChanged(bool value)
    {
        if (!loading)
            OwnStyleSwitched?.Invoke(this, value);
    }

    partial void OnSelectedPresetChanged(SheetStylePresetChoice? value) => OnPropertyChanged(nameof(CanDeletePreset));

    // Every value change: the style is new.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(FontFamily):
            case nameof(FontSize):
            case nameof(FontWeight):
            case nameof(TextColor):
            case nameof(OutlineColor):
            case nameof(LabelBackground):
            case nameof(PageBackground):
            case nameof(TableBackground):
            case nameof(TableBorder):
            case nameof(HeaderBackground):
            case nameof(HeaderText):
            case nameof(TableText):
            case nameof(LineColor):
            case nameof(MarkerButton):
            case nameof(MarkerAxis):
            case nameof(MarkerHat):
            case nameof(MarkerSlider):
                Raise();
                break;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Raise();

    private void Raise()
    {
        if (loading)
            return;
        SheetStyle style = Current;
        PreviewStyle = style;
        Changed?.Invoke(this, style);
    }

    // The style the panel shows now (every value set).
    public SheetStyle Current
    {
        get
        {
            SheetStyle s = new()
            {
                Name = name,
                FontFamily = (FontFamily ?? string.Empty).Trim(),
                FontSize = double.IsFinite(FontSize) && FontSize > 0 ? Math.Clamp(Math.Round(FontSize, 1), 6, 96) : SheetStyle.BaseFontSize,
                FontWeight = WeightValues[Math.Max(0, IndexOfWeight(FontWeight))],
                TextColor = Color(TextColor, "#FFFFFFFF"),
                OutlineColor = Color(OutlineColor, "#FF000000"),
                LabelBackground = Color(LabelBackground, "#88000000"),
                PageBackground = Color(PageBackground, "#FF2B3038"),
                TableBackground = Color(TableBackground, "#FFF7F7F7"),
                TableBorder = Color(TableBorder, "#FF0E1116"),
                HeaderBackground = Color(HeaderBackground, "#FFD5DAE1"),
                HeaderText = Color(HeaderText, "#FF0E1116"),
                TableText = Color(TableText, "#FF0E1116"),
                LineColor = Color(LineColor, "#FFE6E8EB"),
                MarkerButton = Color(MarkerButton, "#FF22C55E"),
                MarkerAxis = Color(MarkerAxis, "#FF38BDF8"),
                MarkerHat = Color(MarkerHat, "#FFA855F7"),
                MarkerSlider = Color(MarkerSlider, "#FFFBBF24"),
                Drawing = Drawing?.Current.ToString() ?? drawingSpec
            };
            foreach (SheetModifierRow row in Modifiers)
            {
                string modifier = (row.Name ?? string.Empty).Trim();
                string color = SheetStyle.NormalizeColor(row.Color);
                if (modifier.Length > 0 && color.Length > 0 && !s.Modifiers.Any(m => string.Equals(m.Name, modifier, StringComparison.OrdinalIgnoreCase)))
                    s.Modifiers.Add(new SheetModifierColor(modifier, color));
            }
            return s;
        }
    }

    private static string Color(string? value, string fallback)
    {
        string c = SheetStyle.NormalizeColor(value);
        return c.Length > 0 ? c : fallback;
    }

    private static int IndexOfWeight(string? title)
    {
        int i = WeightChoices.ToList().FindIndex(w => string.Equals(w, title, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            return i;
        return Array.IndexOf(WeightValues, (title ?? string.Empty).Trim().ToLowerInvariant());
    }

    // Shows a style (no Changed).
    public void Load(SheetStyle? style)
    {
        SheetStyle s = SheetStyleStore.Complete(style);
        loading = true;
        try
        {
            name = s.Name;
            drawingSpec = s.Drawing;
            FontFamily = s.FontFamily;
            FontSize = s.FontSize > 0 ? s.FontSize : SheetStyle.BaseFontSize;
            int w = Array.IndexOf(WeightValues, s.NormalizedWeight);
            FontWeight = WeightChoices[w < 0 ? 0 : w];
            TextColor = s.TextColor;
            OutlineColor = s.OutlineColor;
            LabelBackground = s.LabelBackground;
            PageBackground = s.PageBackground;
            TableBackground = s.TableBackground;
            TableBorder = s.TableBorder;
            HeaderBackground = s.HeaderBackground;
            HeaderText = s.HeaderText;
            TableText = s.TableText;
            LineColor = s.LineColor;
            MarkerButton = s.MarkerButton;
            MarkerAxis = s.MarkerAxis;
            MarkerHat = s.MarkerHat;
            MarkerSlider = s.MarkerSlider;
            Modifiers.Clear();
            foreach (SheetModifierColor m in s.Modifiers)
                Modifiers.Add(new SheetModifierRow { Name = m.Name, Color = m.Color });
        }
        finally
        {
            loading = false;
        }
        PreviewStyle = Current;
    }

    // Shows the own-style switch without moving any style (the host sets it when the shown device changes).
    public void SetScope(bool canOwn, bool ownStyle, string scopeText)
    {
        loading = true;
        try
        {
            CanOwnStyle = canOwn;
            UsesOwnStyle = canOwn && ownStyle;
            ScopeText = scopeText;
        }
        finally
        {
            loading = false;
        }
    }

    // ---- presets ----

    public void RefreshPresets()
    {
        string? keep = SelectedPreset?.Id;
        Presets.Clear();
        foreach ((string id, string title) in SheetStyleStore.BuiltIn)
            Presets.Add(new SheetStylePresetChoice(id, T(title), false));
        foreach (SheetStyle user in SheetStyleStore.UserPresets())
            Presets.Add(new SheetStylePresetChoice(user.Name, user.Name, true));
        SelectedPreset = Presets.FirstOrDefault(p => string.Equals(p.Id, keep, StringComparison.OrdinalIgnoreCase)) ?? Presets.FirstOrDefault();
    }

    // The style of a preset (built-in or the user's), every value set.
    public static SheetStyle StyleOf(SheetStylePresetChoice choice) =>
        choice.IsUser
            ? SheetStyleStore.UserPresets().FirstOrDefault(p => string.Equals(p.Name, choice.Id, StringComparison.OrdinalIgnoreCase)) ?? SheetStyle.Preset("joypro")
            : SheetStyle.Preset(choice.Id);

    // Raised before Changed when a preset is applied: the host also takes its drawing style.
    public event EventHandler<SheetStyle>? PresetApplied;

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is not { } choice)
            return;
        SheetStyle style = StyleOf(choice);
        PresetApplied?.Invoke(this, style);
        Load(style);
        Status = T("Preset applied: {0}", choice.Title);
        Raise();
    }

    [RelayCommand]
    private void SavePreset()
    {
        string presetName = (NewPresetName ?? string.Empty).Trim();
        if (presetName.Length == 0)
        {
            Status = T("Type a name for the preset first.");
            return;
        }
        if (SheetStyleStore.IsBuiltInName(presetName))
        {
            Status = T("{0} is one of JoyPro's presets: pick another name.", presetName);
            return;
        }
        try
        {
            SheetStyleStore.SavePreset(Current, presetName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = T("The preset could not be saved: {0}", ex.Message);
            return;
        }
        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.IsUser && string.Equals(p.Id, presetName, StringComparison.OrdinalIgnoreCase)) ?? SelectedPreset;
        NewPresetName = string.Empty;
        Status = T("Preset saved: {0}", presetName);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsUser: true } choice)
            return;
        try
        {
            SheetStyleStore.DeletePreset(choice.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = T("The preset could not be deleted: {0}", ex.Message);
            return;
        }
        RefreshPresets();
        Status = T("Preset deleted: {0}", choice.Title);
        PresetsChanged?.Invoke(this, EventArgs.Empty);
    }

    // A preset was saved or deleted (the other panel lists it too).
    public event EventHandler? PresetsChanged;

    // ---- modifiers ----

    [RelayCommand]
    private void AddModifier()
    {
        string[] palette = ["#FF38BDF8", "#FFF59E0B", "#FF34D399", "#FFA78BFA", "#FFFB7185", "#FFF472B6", "#FF22D3EE", "#FFFACC15"];
        Modifiers.Add(new SheetModifierRow { Name = T("Modifier {0}", Modifiers.Count + 1), Color = palette[Modifiers.Count % palette.Length] });
    }

    [RelayCommand]
    private void RemoveModifier(SheetModifierRow? row)
    {
        if (row != null)
            Modifiers.Remove(row);
    }
}
