using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Settings > Language (docs/localization.md): the Windows display language by default, or one the user picks; kept in
// ui-settings.json "language" (empty: the Windows language). XAML texts switch at once; texts JoyPro already wrote
// (the status line, lists) follow with the next refresh.
public partial class MainWindowViewModel
{
    private string languageChoice = string.Empty;
    private bool applyingLanguage;

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = BuildLanguageChoices();

    [ObservableProperty]
    private LanguageChoice? selectedLanguageChoice;

    // The language JoyPro shows now (Settings shows it under the picker).
    public string LanguageInUse => Loc.Current.NativeName;

    partial void OnSelectedLanguageChoiceChanged(LanguageChoice? value)
    {
        if (value == null || applyingLanguage)
            return;
        ApplyLanguageChoice(value.Code);
        SaveUiSettings();
    }

    // At start (from ui-settings.json) and from the picker: the language for the whole GUI.
    internal void ApplyLanguageChoice(string? code)
    {
        languageChoice = (code ?? string.Empty).Trim();
        LanguageInfo before = Loc.Current;
        Loc.SetLanguage(languageChoice);
        languageChoice = Loc.Choice;
        applyingLanguage = true;
        try
        {
            SelectedLanguageChoice = LanguageChoices.FirstOrDefault(c => string.Equals(c.Code, Loc.Choice, StringComparison.OrdinalIgnoreCase))
                ?? LanguageChoices[0];
        }
        finally
        {
            applyingLanguage = false;
        }
        OnPropertyChanged(nameof(LanguageInUse));
        // The tray host's menu and notifications follow (tray-strings.json next to ui-settings.json).
        if (persistUiFiles)
            Services.TrayStrings.Write(Services.AppInfo.SettingsFolder);
        // Every binding of the window reads its text again (texts made with T() in a getter follow the language).
        // Texts the view model starts with (field values, set before the language is known): in the language now.
        // A text written in the language before is found in its strings and shown in the language now.
        StatusText = Loc.Retranslate(StatusText, before);
        ValidationSummary = Loc.Retranslate(ValidationSummary, before);
        OutputText = T(OutputText);
        RecordStatusText = Loc.Retranslate(RecordStatusText, before);
        lastResultStatus = Loc.Retranslate(lastResultStatus, before);
        if (!ReferenceEquals(before, Loc.Current))
        {
            OnPropertyChanged(string.Empty);
            // Lists JoyPro built with texts (the next steps) are built again; the status line says what happened.
            if (languageApplied)
            {
                RebuildDashboard();
                RefreshTextsForLanguage();
                StatusText = T("Language: {0}", Loc.Current.NativeName);
            }
        }
        languageApplied = true;
    }

    private bool languageApplied;

    // Texts JoyPro made from engine answers on the main pages (the axis tuning scopes, the watcher line): made again in
    // the new language. Lists follow with their next refresh.
    private void RefreshTextsForLanguage()
    {
        if (WatchGameConfigs && gameSyncWatcherStarted)
            ApplyGameWatchChoices([]);
        else if (!WatchGameConfigs)
            GameWatchStatus = T("Off: game configs are not watched.");
        foreach (AxisTuningScopeItem scope in AxisTuningScopes)
            scope.Label = AxisTuningScopeLabelNow(scope);
        if (cockpits != null)
        {
            cockpits.RefreshPicturesStatus();
            cockpits.RefreshTextsForLanguage();
        }
    }

    private static IReadOnlyList<LanguageChoice> BuildLanguageChoices()
    {
        LanguageInfo system = Languages.ForCulture(CultureInfo.CurrentUICulture);
        List<LanguageChoice> list = [new LanguageChoice(string.Empty, system)];
        list.AddRange(Languages.All.Select(l => new LanguageChoice(l.Code, l)));
        return list;
    }
}

// One entry of the language picker: Code empty is "the Windows language" (Language: the one that means now).
public sealed class LanguageChoice(string code, LanguageInfo language)
{
    public string Code { get; } = code;

    public LanguageInfo Language { get; } = language;

    public bool IsSystem => Code.Length == 0;

    public string Label => IsSystem
        ? T("Windows language") + " (" + Language.NativeName + ")"
        : Language.ToString();

    public override string ToString() => Label;
}
