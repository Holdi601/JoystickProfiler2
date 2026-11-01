using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The HUD's styles (docs/hud.md "Styles"): a long list of presets, the user's own styles (Save style, style files to
// import and export) and the design options per element type (HudLookEditor). The HUD's style is kept in hud.json
// ("Look"), own styles in "UserStyles"; a widget can take another preset or own style than the HUD ("Style" on the
// right). Every change shows at once in the designer's preview, on screen and on the stream page.
public sealed partial class MainWindowViewModel
{
    public const string HudStyleOwnPrefix = "own:";
    public const string HudStyleOfHud = "";

    public HudLookEditor HudLookEditor => hudLookEditor ??= CreateHudLookEditor();

    private HudLookEditor? hudLookEditor;
    private bool applyingHudStyle;

    // The style before the changes made by hand (Undo changes goes back to it): the style picked, saved or imported
    // last, or the HUD's look when JoyPro started.
    private HudLookSettings? hudStyleBeforeChanges;

    // Presets, then own styles ("own:<name>"); the names of presets are translated where shown.
    public ObservableCollection<HudChoice> HudStyleOptions { get; } = [];

    // The same list with "The HUD's style" first (a widget's style).
    public ObservableCollection<HudChoice> HudWidgetStyleOptions { get; } = [];

    public IReadOnlyList<HudChoice> HudFonts { get; } = HudLook.Fonts.Select(f => new HudChoice(f, f switch { "Default" => N("JoyPro's font"), "Mono" => N("Monospace"), _ => f })).ToList();

    public IReadOnlyList<HudChoice> HudGimbalShapes { get; } = Choices(HudLook.GimbalShapes, N("Ring"), N("Square"), N("Crosshair"), N("Radar"), N("Dot"));

    public IReadOnlyList<HudChoice> HudBarShapes { get; } = Choices(HudLook.BarShapes, N("Rounded"), N("Square"), N("Thin"), N("Segmented"), N("Outline"));

    public IReadOnlyList<HudChoice> HudButtonShapes { get; } = Choices(HudLook.ButtonShapes, N("Rounded"), N("Circle"), N("Square"));

    public IReadOnlyList<HudChoice> HudChipShapes { get; } = Choices(HudLook.ChipShapes, N("Pill"), N("Square"), N("Underline"));

    public IReadOnlyList<HudChoice> HudCardShadows { get; } = Choices(HudLook.CardShadows, N("None"), N("Soft shadow"), N("Glow"));

    private static List<HudChoice> Choices(IReadOnlyList<string> ids, params string[] names) =>
        ids.Select((id, i) => new HudChoice(id, names[i])).ToList();

    [ObservableProperty]
    private HudChoice? selectedHudStyleOption;

    // The name the own style is saved under (Save style).
    [ObservableProperty]
    private string hudStyleName = string.Empty;

    [ObservableProperty]
    private string hudStyleStatus = string.Empty;

    // The HUD's style by name (a preset or "own:<name>"): what tests and the old style box set.
    public string HudStyle
    {
        get => SelectedHudStyleOption?.Id ?? string.Empty;
        set => SelectedHudStyleOption = HudStyleOptions.FirstOrDefault(o => string.Equals(o.Id, value, StringComparison.OrdinalIgnoreCase)) ?? SelectedHudStyleOption;
    }

    public bool HudStyleIsOwn => SelectedHudStyleOption?.Id.StartsWith(HudStyleOwnPrefix, StringComparison.Ordinal) == true;

    public int HudPresetCount => HudStylePresets.All.Count;

    private HudLookEditor CreateHudLookEditor()
    {
        HudLookEditor editor = new();
        editor.Changed += (_, _) => OnHudLookEdited();
        return editor;
    }

    partial void OnSelectedHudStyleOptionChanged(HudChoice? value)
    {
        OnPropertyChanged(nameof(HudStyle));
        OnPropertyChanged(nameof(HudStyleIsOwn));
        if (value == null || applyingHudStyle)
            return;
        HudLookSettings? style = HudStyleById(value.Id);
        if (style == null)
            return;
        hudStyleBeforeChanges = style.Normalized();
        LoadHudLookIntoEditor(style);
        ApplyHudLookSettings(style);
        if (value.Id.StartsWith(HudStyleOwnPrefix, StringComparison.Ordinal))
            HudStyleName = style.Name;
    }

    // A preset by name or an own style by "own:<name>" (a copy).
    private HudLookSettings? HudStyleById(string id)
    {
        if (id.StartsWith(HudStyleOwnPrefix, StringComparison.Ordinal))
        {
            string name = id[HudStyleOwnPrefix.Length..];
            return hudLayout.UserStyles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Normalized();
        }
        return HudStylePresets.Find(id);
    }

    private void RefreshHudStyleOptions()
    {
        string? selected = SelectedHudStyleOption?.Id;
        string? widgetStyle = SelectedHudWidgetStyle?.Id;
        applyingHudStyle = true;
        try
        {
            HudStyleOptions.Clear();
            HudWidgetStyleOptions.Clear();
            HudWidgetStyleOptions.Add(new HudChoice(HudStyleOfHud, N("The HUD's style")));
            foreach (HudLookSettings preset in HudStylePresets.All)
            {
                HudChoice choice = new(preset.Name, preset.Name);
                HudStyleOptions.Add(choice);
                HudWidgetStyleOptions.Add(choice);
            }
            foreach (HudLookSettings own in hudLayout.UserStyles.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                HudChoice choice = new(HudStyleOwnPrefix + own.Name, T("Own: {0}", own.Name));
                HudStyleOptions.Add(choice);
                HudWidgetStyleOptions.Add(choice);
            }
            SelectedHudStyleOption = HudStyleOptions.FirstOrDefault(o => o.Id == selected) ?? MatchingHudStyleOption(hudLayout.Look);
        }
        finally
        {
            applyingHudStyle = false;
        }
        OnPropertyChanged(nameof(SelectedHudWidgetStyle));
    }

    // The list entry of a design: its name when it is still that preset or own style unchanged, else none (edited).
    private HudChoice? MatchingHudStyleOption(HudLookSettings? look)
    {
        if (look == null)
            return null;
        HudChoice? named = HudStyleOptions.FirstOrDefault(o => HudStyleById(o.Id) is { } s && string.Equals(s.Name, look.Name, StringComparison.OrdinalIgnoreCase) && s.SameDesign(look));
        return named ?? HudStyleOptions.FirstOrDefault(o => HudStyleById(o.Id) is { } s && s.SameDesign(look));
    }

    private void LoadHudLookIntoEditor(HudLookSettings look)
    {
        HudLookEditor.Load(look);
        HudContext.ApplyLook(look);
    }

    // An option was changed by hand: the HUD takes it at once; the list shows no preset any more unless it is one.
    private void OnHudLookEdited()
    {
        if (applyingHudStyle)
            return;
        string name = SelectedHudStyleOption != null && HudStyleById(SelectedHudStyleOption.Id) is { } current && current.SameDesign(HudLookEditor.ToSettings(string.Empty))
            ? current.Name
            : string.Empty;
        HudLookSettings look = HudLookEditor.ToSettings(name);
        ApplyHudLookSettings(look);
        if (name.Length == 0)
        {
            applyingHudStyle = true;
            try
            {
                SelectedHudStyleOption = MatchingHudStyleOption(look);
            }
            finally
            {
                applyingHudStyle = false;
            }
        }
    }

    private void ApplyHudLookSettings(HudLookSettings look)
    {
        HudLookSettings normal = look.Normalized();
        HudContext.ApplyLook(normal);
        hudLayout.Look = normal;
        // Kept in step for an older JoyPro reading the same hud.json.
        hudLayout.Accent = "#" + normal.Accent[3..];
        hudLayout.Style = HudStylePresets.All.Take(3).Any(p => p.Name == normal.Name) ? normal.Name : "Glass";
        hudLayout.Opacity = normal.Opacity;
        HudStreamLayoutChanged();
        ScheduleHudSave();
    }

    // Save style: the current design as an own style under HudStyleName (an own style of that name is replaced).
    [RelayCommand]
    private void SaveHudStyle()
    {
        string name = (HudStyleName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            HudStyleStatus = T("Type a name for the style first.");
            return;
        }
        if (HudStylePresets.Find(name) != null)
        {
            HudStyleStatus = T("\"{0}\" is the name of a preset: pick another name.", name);
            return;
        }
        HudLookSettings style = HudLookEditor.ToSettings(name);
        hudLayout.UserStyles.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        hudLayout.UserStyles.Add(style);
        hudLayout.Look = style;
        RefreshHudStyleOptions();
        applyingHudStyle = true;
        try
        {
            SelectedHudStyleOption = HudStyleOptions.FirstOrDefault(o => o.Id == HudStyleOwnPrefix + name);
        }
        finally
        {
            applyingHudStyle = false;
        }
        ApplyHudLookSettings(style);
        hudStyleBeforeChanges = style.Normalized();
        HudStyleStatus = T("Saved as your style \"{0}\".", name);
    }

    [RelayCommand]
    private void DeleteHudStyle()
    {
        if (!HudStyleIsOwn || SelectedHudStyleOption == null)
            return;
        string name = SelectedHudStyleOption.Id[HudStyleOwnPrefix.Length..];
        hudLayout.UserStyles.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (HudWidget widget in HudWidgets.Where(w => w.HasOwnStyle && string.Equals(w.OwnStyleName, name, StringComparison.OrdinalIgnoreCase)))
            widget.SetOwnStyle(null);
        RefreshHudStyleOptions();
        ScheduleHudSave();
        HudStyleStatus = T("Your style \"{0}\" is deleted; the HUD keeps its look.", name);
    }

    // Export style: the current design as a style file (.joypro-hudstyle.json).
    public string SuggestedHudStyleFileName => SafeFileName(HudStyleName.Length > 0 ? HudStyleName : SelectedHudStyleOption?.Name ?? "HUD style") + ".joypro-hudstyle.json";

    public bool ExportHudStyle(string path)
    {
        try
        {
            string name = HudStyleName.Trim().Length > 0 ? HudStyleName.Trim() : hudLayout.Look?.Name is { Length: > 0 } n ? n : "HUD style";
            HudStyleFile.Write(path, HudLookEditor.ToSettings(name));
            HudStyleStatus = T("Style exported to {0}.", path);
            return true;
        }
        catch (Exception ex)
        {
            HudStyleStatus = T("Could not export the style: {0}", ex.Message);
            return false;
        }
    }

    // Import style: a style file becomes an own style (its name, numbered when taken) and the HUD's style.
    public bool ImportHudStyle(string path)
    {
        HudLookSettings? style = HudStyleFile.Read(path);
        if (style == null)
        {
            HudStyleStatus = T("{0} is not a JoyPro HUD style file.", Path.GetFileName(path));
            return false;
        }
        string name = style.Name.Trim().Length > 0 ? style.Name.Trim() : "Imported style";
        string unique = name;
        for (int i = 2; HudStylePresets.Find(unique) != null || hudLayout.UserStyles.Any(s => string.Equals(s.Name, unique, StringComparison.OrdinalIgnoreCase) && !s.SameDesign(style)); i++)
            unique = name + " " + i;
        style.Name = unique;
        hudLayout.UserStyles.RemoveAll(s => string.Equals(s.Name, unique, StringComparison.OrdinalIgnoreCase));
        hudLayout.UserStyles.Add(style);
        hudLayout.Look = style;
        HudStyleName = unique;
        RefreshHudStyleOptions();
        applyingHudStyle = true;
        try
        {
            SelectedHudStyleOption = HudStyleOptions.FirstOrDefault(o => o.Id == HudStyleOwnPrefix + unique);
            LoadHudLookIntoEditor(style);
        }
        finally
        {
            applyingHudStyle = false;
        }
        ApplyHudLookSettings(style);
        hudStyleBeforeChanges = style.Normalized();
        HudStyleStatus = T("Imported the style \"{0}\".", unique);
        return true;
    }

    // Undo changes: back to the style before the changes made by hand (the one picked, saved or imported last, or the
    // HUD's look at start).
    [RelayCommand]
    private void ResetHudStyle()
    {
        HudLookSettings style = hudStyleBeforeChanges?.Normalized()
                                ?? (SelectedHudStyleOption != null && HudStyleById(SelectedHudStyleOption.Id) is { } s ? s : HudStylePresets.Default);
        LoadHudLookIntoEditor(style);
        ApplyHudLookSettings(style);
        applyingHudStyle = true;
        try
        {
            SelectedHudStyleOption = MatchingHudStyleOption(style);
        }
        finally
        {
            applyingHudStyle = false;
        }
    }

    // The › button: the next style of the list (after the last one the first again).
    [RelayCommand]
    private void NextHudStyle()
    {
        if (HudStyleOptions.Count == 0)
            return;
        int index = SelectedHudStyleOption == null ? 0 : (HudStyleOptions.IndexOf(SelectedHudStyleOption) + 1) % HudStyleOptions.Count;
        SelectedHudStyleOption = HudStyleOptions[index];
    }

    // ---- A widget's own style ----------------------------------------------------------------------------------------

    public HudChoice? SelectedHudWidgetStyle
    {
        get
        {
            HudWidget? widget = SelectedHudWidget;
            if (widget == null)
                return null;
            if (!widget.HasOwnStyle)
                return HudWidgetStyleOptions.FirstOrDefault();
            HudLookSettings own = widget.Look.Settings;
            return HudWidgetStyleOptions.Skip(1).FirstOrDefault(o => HudStyleById(o.Id) is { } s && string.Equals(s.Name, own.Name, StringComparison.OrdinalIgnoreCase))
                   ?? HudWidgetStyleOptions.FirstOrDefault();
        }
        set
        {
            HudWidget? widget = SelectedHudWidget;
            if (widget == null || value == null)
                return;
            widget.SetOwnStyle(value.Id == HudStyleOfHud ? null : HudStyleById(value.Id));
            OnPropertyChanged();
            HudStreamLayoutChanged();
            ScheduleHudSave();
        }
    }

    private static string SafeFileName(string name)
    {
        string text = new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return text.Trim().Length == 0 ? "HUD style" : text.Trim();
    }
}
