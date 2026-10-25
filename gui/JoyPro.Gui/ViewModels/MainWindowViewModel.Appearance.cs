using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// One colour of the theme editor: the token, its group heading (on the first row of a group) and the colour.
public sealed partial class ThemeTokenRow : ObservableObject
{
    private readonly Action<ThemeTokenRow> changed;

    public ThemeTokenRow(ThemeToken token, string value, bool isGroupStart, Action<ThemeTokenRow> changed)
    {
        Token = token;
        this.value = value;
        IsGroupStart = isGroupStart;
        this.changed = changed;
    }

    public ThemeToken Token { get; }

    public string Key => Token.Key;

    public string Group => T(Token.Group);

    public string Label => T(Token.Label);

    public bool IsGroupStart { get; }

    [ObservableProperty]
    private string value;

    partial void OnValueChanged(string value) => changed(this);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

// Settings > Appearance (docs/ui/themes.md): Light, Dark, System or a theme of the user's own; the text size and the
// UI scale on top of Windows' display scaling (Ctrl+Plus / Ctrl+Minus / Ctrl+0); the theme editor with every colour,
// the fonts, a live preview and the contrast check.
public sealed partial class MainWindowViewModel
{
    public const string CustomThemeMode = "Custom";

    public ObservableCollection<CustomTheme> CustomThemes { get; } = [];

    public ObservableCollection<ThemeTokenRow> ThemeTokenRows { get; } = [];

    public ObservableCollection<string> ThemeContrastProblems { get; } = [];

    public IReadOnlyList<string> ThemeFontWeights { get; } = ["Light", "Normal", "Medium", "SemiBold", "Bold"];

    public IReadOnlyList<string> ThemeBases { get; } = ["Dark", "Light"];

    // The active theme of the user's own (its file name in <data>\Themes), when ThemeMode is Custom.
    [ObservableProperty]
    private string activeCustomThemeFile = string.Empty;

    // The text size the user chose (null: none yet, the active theme's own or 100 %).
    private double? userTextScale;

    [ObservableProperty]
    private double uiScale = 1.0;

    [ObservableProperty]
    private CustomTheme? editingTheme;

    [ObservableProperty]
    private string editingThemeName = string.Empty;

    [ObservableProperty]
    private string editingThemeBase = "Dark";

    [ObservableProperty]
    private string editingUiFont = string.Empty;

    [ObservableProperty]
    private string editingMonoFont = string.Empty;

    [ObservableProperty]
    private string editingFontWeight = "Normal";

    [ObservableProperty]
    private double editingLineSpacing = 1.0;

    [ObservableProperty]
    private double editingHeadingRatio = 1.0;

    // The text size the theme was made for, in percent (0: none).
    [ObservableProperty]
    private double editingThemeTextScalePercent;

    // Bumped on every change of the edited theme: the live preview draws again.
    [ObservableProperty]
    private int themePreviewVersion;

    [ObservableProperty]
    private string themeEditorStatus = string.Empty;

    [ObservableProperty]
    private bool editingThemeHasChanges;

    private bool loadingThemeEditor;

    public bool IsCustomThemeActive => ThemeMode == CustomThemeMode;

    public bool HasEditingTheme => EditingTheme != null;

    public bool HasThemeContrastProblems => ThemeContrastProblems.Count > 0;

    public bool HasCustomThemes => CustomThemes.Count > 0;

    public double TextScale => userTextScale ?? ActiveCustomTheme()?.TextScale ?? 1.0;

    public double TextScalePercent
    {
        get => Math.Round(TextScale * 100);
        set => SetUserTextScale(value / 100.0);
    }

    public double UiScalePercent
    {
        get => Math.Round(UiScale * 100);
        set => UiScale = ThemeService.ClampScale(value / 100.0);
    }

    public string TextScaleLabel => T("{0} %", TextScalePercent.ToString(CultureInfo.InvariantCulture));

    public string UiScaleLabel => T("{0} %", UiScalePercent.ToString(CultureInfo.InvariantCulture));

    public bool HasUserTextScale => userTextScale != null;

    public string ThemeModeLabel => ThemeMode switch
    {
        "Light" => T("Light"),
        "Dark" => T("Dark"),
        CustomThemeMode => ActiveCustomTheme()?.Name ?? T("Your theme"),
        _ => T("System")
    };

    // The theme of the edited file, as it is now (for the preview host).
    public CustomTheme? ThemePreview => EditingTheme;

    private void InitializeAppearance()
    {
        ReloadCustomThemes();
    }

    private CustomTheme? ActiveCustomTheme() =>
        ThemeMode == CustomThemeMode && ActiveCustomThemeFile.Length > 0
            ? CustomThemes.FirstOrDefault(t => string.Equals(Path.GetFileName(t.FilePath), ActiveCustomThemeFile, StringComparison.OrdinalIgnoreCase))
            : null;

    private void ReloadCustomThemes()
    {
        string editing = EditingTheme?.FilePath ?? string.Empty;
        CustomThemes.Clear();
        foreach (CustomTheme theme in ThemeService.ListThemes())
            CustomThemes.Add(theme);
        OnPropertyChanged(nameof(HasCustomThemes));
        if (editing.Length > 0 && CustomThemes.FirstOrDefault(t => string.Equals(t.FilePath, editing, StringComparison.OrdinalIgnoreCase)) is { } again && !EditingThemeHasChanges)
            LoadThemeEditor(again.Clone());
    }

    // ---- applying ---------------------------------------------------------------------------------------------------

    private void ApplyAppearance()
    {
        CustomTheme? custom = ActiveCustomTheme();
        if (ThemeMode == CustomThemeMode && custom == null && ActiveCustomThemeFile.Length > 0 && persistUiFiles)
        {
            // The theme file is gone (deleted outside JoyPro): JoyPro's dark theme.
            JoyPro.Logging.JoyProLog.Info("Theme", "theme file missing: " + ActiveCustomThemeFile); // nonloc
        }
        ThemeService.Apply(ThemeMode, custom);
        ThemeService.ApplyTypography(TextScale, custom?.Fonts);
        OnPropertyChanged(nameof(IsCustomThemeActive));
        OnPropertyChanged(nameof(ThemeModeLabel));
        OnPropertyChanged(nameof(TextScalePercent));
        OnPropertyChanged(nameof(TextScaleLabel));
    }

    [RelayCommand]
    private void UseCustomTheme(CustomTheme? theme)
    {
        if (theme == null)
            return;
        if (string.IsNullOrWhiteSpace(theme.FilePath))
            ThemeService.Save(theme);
        ActiveCustomThemeFile = Path.GetFileName(theme.FilePath);
        ApplyTheme(CustomThemeMode);
    }

    partial void OnActiveCustomThemeFileChanged(string value) => SaveUiSettings();

    partial void OnUiScaleChanged(double value)
    {
        double clamped = ThemeService.ClampScale(value);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            UiScale = clamped;
            return;
        }
        OnPropertyChanged(nameof(UiScalePercent));
        OnPropertyChanged(nameof(UiScaleLabel));
        SaveUiSettings();
    }

    public void SetUserTextScale(double? scale)
    {
        userTextScale = scale is double s ? ThemeService.ClampScale(s) : null;
        // The resources live on the UI thread (a command may run elsewhere in tests).
        double textScale = TextScale;
        ThemeFonts? fonts = ActiveCustomTheme()?.Fonts;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            ThemeService.ApplyTypography(textScale, fonts);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ThemeService.ApplyTypography(textScale, fonts));
        OnPropertyChanged(nameof(TextScale));
        OnPropertyChanged(nameof(TextScalePercent));
        OnPropertyChanged(nameof(TextScaleLabel));
        OnPropertyChanged(nameof(HasUserTextScale));
        SaveUiSettings();
    }

    [RelayCommand]
    private void IncreaseTextScale() => SetUserTextScale(TextScale + ThemeService.ScaleStep);

    [RelayCommand]
    private void DecreaseTextScale() => SetUserTextScale(TextScale - ThemeService.ScaleStep);

    // Ctrl+0: the text size of the theme (or 100 %) again.
    [RelayCommand]
    private void ResetTextScale() => SetUserTextScale(null);

    [RelayCommand]
    private void ResetUiScale() => UiScale = 1.0;

    // ---- the editor -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void EditCustomTheme(CustomTheme? theme)
    {
        if (theme != null)
            LoadThemeEditor(theme.Clone());
    }

    [RelayCommand]
    private void NewThemeFromLight() => LoadThemeEditor(ThemeService.StartFrom(ThemeVariant.Light, ThemeService.UniqueName(T("My light theme"))), changed: true);

    [RelayCommand]
    private void NewThemeFromDark() => LoadThemeEditor(ThemeService.StartFrom(ThemeVariant.Dark, ThemeService.UniqueName(T("My dark theme"))), changed: true);

    [RelayCommand]
    private void DuplicateEditingTheme()
    {
        if (EditingTheme == null)
            return;
        CustomTheme copy = CurrentEditingTheme().Clone();
        copy.FilePath = string.Empty;
        copy.Name = ThemeService.UniqueName(T("{0} copy", copy.Name));
        LoadThemeEditor(copy, changed: true);
    }

    // Every colour back to the base theme's (Light or Dark), the fonts to JoyPro's.
    [RelayCommand]
    private void ResetEditingTheme()
    {
        if (EditingTheme == null)
            return;
        CustomTheme reset = ThemeService.StartFrom(EditingThemeBase == "Light" ? ThemeVariant.Light : ThemeVariant.Dark, EditingThemeName);
        reset.FilePath = EditingTheme.FilePath;
        LoadThemeEditor(reset, changed: true);
        ThemeEditorStatus = T("Every colour is {0} again.", EditingThemeBase == "Light" ? T("Light") : T("Dark"));
    }

    [RelayCommand]
    private void SaveEditingTheme()
    {
        if (EditingTheme == null)
            return;
        CustomTheme theme = CurrentEditingTheme();
        if (string.IsNullOrWhiteSpace(theme.Name))
            theme.Name = ThemeService.UniqueName(T("My theme"));
        else if (CustomThemes.Any(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.FilePath, theme.FilePath, StringComparison.OrdinalIgnoreCase)))
            theme.Name = ThemeService.UniqueName(theme.Name);
        try
        {
            ThemeService.Save(theme);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ThemeEditorStatus = T("The theme could not be saved: {0}", e.Message);
            return;
        }
        EditingThemeHasChanges = false;
        LoadThemeEditor(theme.Clone());
        ReloadCustomThemes();
        ThemeEditorStatus = T("Saved: {0}", theme.Name);
        // The active theme changed: the window follows.
        if (ThemeMode == CustomThemeMode && string.Equals(Path.GetFileName(theme.FilePath), ActiveCustomThemeFile, StringComparison.OrdinalIgnoreCase))
            ApplyAppearance();
    }

    [RelayCommand]
    private void UseEditingTheme()
    {
        if (EditingTheme == null)
            return;
        SaveEditingTheme();
        if (EditingTheme != null)
            UseCustomTheme(CustomThemes.FirstOrDefault(t => string.Equals(t.FilePath, EditingTheme.FilePath, StringComparison.OrdinalIgnoreCase)) ?? EditingTheme);
    }

    [RelayCommand]
    private async Task DeleteEditingThemeAsync()
    {
        if (EditingTheme == null)
            return;
        CustomTheme theme = EditingTheme;
        if (!string.IsNullOrWhiteSpace(theme.FilePath) &&
            !await ConfirmAsync(T("Delete the theme {0}?", theme.Name), T("Its file is deleted. Export it first to keep a copy."), T("Delete"), isDanger: true))
            return;
        bool wasActive = ThemeMode == CustomThemeMode && string.Equals(Path.GetFileName(theme.FilePath), ActiveCustomThemeFile, StringComparison.OrdinalIgnoreCase);
        ThemeService.Delete(theme);
        EditingTheme = null;
        ThemeTokenRows.Clear();
        ReloadCustomThemes();
        if (wasActive)
        {
            ActiveCustomThemeFile = string.Empty;
            ApplyTheme(theme.BaseVariant == ThemeVariant.Light ? "Light" : "Dark");
        }
        ThemeEditorStatus = T("Deleted: {0}", theme.Name);
    }

    [RelayCommand]
    private void AutoFixThemeContrast()
    {
        if (EditingTheme == null)
            return;
        CustomTheme theme = CurrentEditingTheme();
        int changed = ThemeService.AutoFixContrast(theme);
        LoadThemeEditor(theme, changed: changed > 0 || EditingThemeHasChanges);
        ThemeEditorStatus = changed == 0 ? T("Every text is readable already.") : Plural("{0} colour changed so every text is readable.", "{0} colours changed so every text is readable.", changed);
    }

    // A theme file from elsewhere (the view picks it): checked, copied into the themes folder, opened in the editor.
    public bool ImportTheme(string path)
    {
        if (!ThemeService.TryImport(path, out CustomTheme? theme, out string error, out List<string> warnings) || theme == null)
        {
            ThemeEditorStatus = T("Not imported: {0}", error);
            return false;
        }
        ReloadCustomThemes();
        LoadThemeEditor(theme.Clone());
        ThemeEditorStatus = warnings.Count == 0
            ? T("Imported: {0}", theme.Name)
            : T("Imported: {0}", theme.Name) + " " + string.Join(" ", warnings.Take(3));
        return true;
    }

    public bool ExportEditingTheme(string path)
    {
        if (EditingTheme == null)
            return false;
        try
        {
            ThemeService.Export(CurrentEditingTheme(), path);
            ThemeEditorStatus = T("Exported to {0}", path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ThemeEditorStatus = T("The theme could not be saved: {0}", e.Message);
            return false;
        }
    }

    public string SuggestedThemeFileName => (EditingTheme?.Name is { Length: > 0 } n ? n : "theme") + ".json";

    private void LoadThemeEditor(CustomTheme theme, bool changed = false)
    {
        loadingThemeEditor = true;
        try
        {
            EditingTheme = theme;
            EditingThemeName = theme.Name;
            EditingThemeBase = theme.Base;
            EditingUiFont = theme.Fonts.Ui;
            EditingMonoFont = theme.Fonts.Mono;
            EditingFontWeight = theme.Fonts.Weight;
            EditingLineSpacing = theme.Fonts.LineSpacing;
            EditingHeadingRatio = theme.Fonts.HeadingRatio;
            EditingThemeTextScalePercent = theme.TextScale is double s ? Math.Round(s * 100) : 0;
            Dictionary<string, Color> resolved = ThemeService.Resolve(theme);
            ThemeTokenRows.Clear();
            string group = string.Empty;
            foreach (ThemeToken token in ThemeService.Tokens)
            {
                ThemeTokenRows.Add(new ThemeTokenRow(token, ThemeService.Hex(resolved[token.Key]), token.Group != group, OnThemeTokenChanged));
                group = token.Group;
            }
        }
        finally
        {
            loadingThemeEditor = false;
        }
        EditingThemeHasChanges = changed;
        OnPropertyChanged(nameof(HasEditingTheme));
        ThemeEditorChanged();
    }

    // The edited theme with every field of the editor in it.
    private CustomTheme CurrentEditingTheme()
    {
        CustomTheme theme = EditingTheme ?? new CustomTheme();
        theme.Name = (EditingThemeName ?? string.Empty).Trim();
        theme.Base = EditingThemeBase == "Light" ? "Light" : "Dark";
        foreach (ThemeTokenRow row in ThemeTokenRows)
        {
            if (Color.TryParse(row.Value, out Color c))
                theme.Colors[row.Key] = ThemeService.Hex(c);
        }
        theme.Fonts = new ThemeFonts
        {
            Ui = EditingUiFont ?? string.Empty,
            Mono = EditingMonoFont ?? string.Empty,
            Weight = EditingFontWeight ?? "Normal",
            LineSpacing = Math.Clamp(EditingLineSpacing, 0.8, 2.0),
            HeadingRatio = Math.Clamp(EditingHeadingRatio, 0.8, 1.6)
        };
        theme.TextScale = EditingThemeTextScalePercent >= ThemeService.MinScale * 100 ? ThemeService.ClampScale(EditingThemeTextScalePercent / 100.0) : null;
        return theme;
    }

    private void OnThemeTokenChanged(ThemeTokenRow row)
    {
        if (loadingThemeEditor || EditingTheme == null)
            return;
        if (Color.TryParse(row.Value, out Color c))
            EditingTheme.Colors[row.Key] = ThemeService.Hex(c);
        EditingThemeHasChanges = true;
        ThemeEditorChanged();
    }

    partial void OnEditingThemeNameChanged(string value) => EditorFieldChanged();

    partial void OnEditingThemeBaseChanged(string value) => EditorFieldChanged();

    partial void OnEditingUiFontChanged(string value) => EditorFieldChanged();

    partial void OnEditingMonoFontChanged(string value) => EditorFieldChanged();

    partial void OnEditingFontWeightChanged(string value) => EditorFieldChanged();

    partial void OnEditingLineSpacingChanged(double value) => EditorFieldChanged();

    partial void OnEditingHeadingRatioChanged(double value) => EditorFieldChanged();

    partial void OnEditingThemeTextScalePercentChanged(double value) => EditorFieldChanged();

    private void EditorFieldChanged()
    {
        if (loadingThemeEditor || EditingTheme == null)
            return;
        CurrentEditingTheme();
        EditingThemeHasChanges = true;
        ThemeEditorChanged();
    }

    private void ThemeEditorChanged()
    {
        ThemeContrastProblems.Clear();
        if (EditingTheme != null)
        {
            foreach (ThemeService.ContrastProblem p in ThemeService.CheckContrast(CurrentEditingTheme()))
            {
                string fg = ThemeService.Tokens.FirstOrDefault(t => t.Key == p.Pair.Foreground)?.Label ?? p.Pair.Foreground;
                string bg = ThemeService.Tokens.FirstOrDefault(t => t.Key == p.Pair.Background)?.Label ?? p.Pair.Background;
                ThemeContrastProblems.Add(T("{0} on {1}: {2}:1, needs {3}:1", T(fg), T(bg),
                    p.Ratio.ToString("0.0", CultureInfo.InvariantCulture), p.Pair.Minimum.ToString("0.0", CultureInfo.InvariantCulture)));
            }
        }
        OnPropertyChanged(nameof(HasThemeContrastProblems));
        OnPropertyChanged(nameof(ThemePreview));
        ThemePreviewVersion++;
    }
}
