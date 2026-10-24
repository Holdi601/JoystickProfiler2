using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Settings page as sections that open and close (Views/Controls/SettingsSection.cs), in the user's order: drag a
// section by its handle or a tab at the top to move it, or use the section's menu. A search box shows only the sections
// with matching settings (open while searching). Order and open sections are kept in ui-settings.json.
public sealed partial class MainWindowViewModel
{
    // JoyPro's own order (new sections of a later version go where they stand here, SettingsSectionOrder.Merge).
    internal static readonly (string Id, string Title, string Keywords, string Tab)[] SettingsSectionDefaults =
    [
        ("games", N("Games"), "hidden games hide show installed folder portable watch config detect setup war thunder archives hell let loose vietnam oodle", N("Games")),
        ("binds", N("Binds"), "follow input record button held pressed switch binds page", N("Binds")),
        ("startup", N("Startup"), "last profile open start autosave history versions", N("Startup")),
        ("pages", N("Pages in the bottom bar"), "pages tabs navigation bottom bar menu hide show order reorder move drag", N("Pages")),
        ("language", N("Language"), "language translation locale english deutsch windows", N("Language")),
        ("tray", N("Run in the background (system tray)"), "tray windows autostart sign in close to tray helper watcher", N("System tray")),
        ("confirmations", N("Confirmations"), "ask confirm question before delete remove warning dialog", N("Confirmations")),
        ("about", N("About JoyPro and updates"), "version build commit date update check release github download install skip license about", N("About and updates")),
        ("privacy", N("Privacy"), "photos covers internet download consent telemetry", N("Privacy")),
        ("telemetry-recording", N("Telemetry recording"), "telemetry record recording tacview acmi csv flight session replay track export flight data", N("Telemetry recording")),
        ("drawings", N("Drawings"), "line art cel shaded colours colors style drawing reset layout markers default picture", N("Drawings")),
        ("report", N("Report a problem"), "bug crash log logs issue github support report", N("Report a problem")),
        (LocalAiSectionId, N("Local AI"), "ai artificial intelligence local model llm llama llama.cpp qwen gguf download start stop memory idle manage edit with ai assistant describe", N("Local AI")),
        ("components", N("Components"), "install optional vjoy hidhide driver download requirements net framework ai model llama", N("Components")),
        ("devices", N("Devices"), "device new stick sold move binds remove rename", N("Devices")),
        ("db-expert", N("Command lists (DB) and game paths: information for experts"), "db summary check list games planes fingerprint paths", N("DB and game paths")),
        ("dcs-defaults", N("DCS: its own default binds on your devices"), "dcs default binds move clear", N("DCS defaults")),
        ("command-lists", N("Command lists of DCS, Star Citizen and IL-2 (for experts)"), "refresh dcs star citizen il-2 il2 command list db", N("Command lists")),
        ("ffb-games", N("Force feedback settings of the games"), "ffb force feedback strength effects game settings", N("Game force feedback")),
        ("dcs-ffb", N("DCS force feedback: swap and invert per device"), "dcs ffb swap invert axis force feedback", N("DCS force feedback")),
    ];

    // Open at the first start: the games (the rest is a click away).
    private static readonly string[] SettingsSectionsOpenByDefault = ["games"];

    public ObservableCollection<SettingsSectionItem> SettingsSections { get; } = CreateSettingsSections();

    private bool applyingSettingsLayout;

    private static ObservableCollection<SettingsSectionItem> CreateSettingsSections()
    {
        ObservableCollection<SettingsSectionItem> sections = [];
        foreach ((string id, string title, string keywords, string tab) in SettingsSectionDefaults)
            sections.Add(new SettingsSectionItem(id, title, keywords, tab) { IsExpanded = SettingsSectionsOpenByDefault.Contains(id) });
        return sections;
    }

    private void InitializeSettingsPage()
    {
        SettingsSections.CollectionChanged += OnSettingsSectionsChanged;
        foreach (SettingsSectionItem item in SettingsSections)
            item.PropertyChanged += OnSettingsSectionPropertyChanged;
        InitializeNavPages();
    }

    private void OnSettingsSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (applyingSettingsLayout)
            return;
        OnPropertyChanged(nameof(SettingsSectionsSummary));
        SaveUiSettings();
    }

    private void OnSettingsSectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsSectionItem.IsContentVisible) && sender is SettingsSectionItem { Id: "ffb-games" })
            SyncFfbSettingsSection();
        if (e.PropertyName == nameof(SettingsSectionItem.IsContentVisible) && sender is SettingsSectionItem { Id: "telemetry-recording" })
            SyncTelemetryRecordingSection();
        if (e.PropertyName != nameof(SettingsSectionItem.IsExpanded) || applyingSettingsLayout)
            return;
        OnPropertyChanged(nameof(SettingsSectionsSummary));
        SaveUiSettings();
    }

    // The games' force feedback settings load when their section is open on the Settings page (not at start, when the
    // section was left open last time).
    private void SyncFfbSettingsSection()
    {
        if (CurrentStage == UiStage.Settings && FindSettingsSection("ffb-games") is { IsContentVisible: true } && !IsFfbSettingsExpanded)
            IsFfbSettingsExpanded = true;
    }

    // The saved layout (ui-settings.json) onto the sections.
    private void ApplySettingsLayout(IReadOnlyList<string>? order, IReadOnlyList<string>? open)
    {
        applyingSettingsLayout = true;
        try
        {
            List<string> merged = SettingsSectionOrder.Merge(SettingsSectionDefaults.Select(d => d.Id).ToList(), order);
            for (int target = 0; target < merged.Count; target++)
            {
                int current = IndexOfSettingsSection(merged[target]);
                if (current >= 0 && current != target)
                    SettingsSections.Move(current, target);
            }
            if (open != null)
            {
                foreach (SettingsSectionItem item in SettingsSections)
                    item.IsExpanded = open.Contains(item.Id, StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            applyingSettingsLayout = false;
        }
        OnPropertyChanged(nameof(SettingsSectionsSummary));
    }

    private List<string> SettingsSectionOrderForSave() => SettingsSections.Select(s => s.Id).ToList();

    private List<string> OpenSettingsSectionsForSave() => SettingsSections.Where(s => s.IsExpanded).Select(s => s.Id).ToList();

    private int IndexOfSettingsSection(string id)
    {
        for (int i = 0; i < SettingsSections.Count; i++)
        {
            if (string.Equals(SettingsSections[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public SettingsSectionItem? FindSettingsSection(string id) => SettingsSections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public string SettingsSectionsSummary
    {
        get
        {
            int open = SettingsSections.Count(s => s.IsExpanded);
            return T("{0} of {1} sections open", open, SettingsSections.Count);
        }
    }

    // ---- Search ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchingSettings))]
    private string settingsSearchText = string.Empty;

    public bool IsSearchingSettings => SettingsSearchText.Trim().Length > 0;

    [ObservableProperty]
    private string settingsSearchStatus = string.Empty;

    partial void OnSettingsSearchTextChanged(string value) => ApplySettingsSearch();

    // The page has read the texts of a section's settings (SettingsSectionsPanel): the search finds them too.
    internal void SettingsSectionTextsRead()
    {
        if (IsSearchingSettings)
            ApplySettingsSearch();
    }

    private void ApplySettingsSearch()
    {
        List<string> words = SettingsSectionOrder.Words(SettingsSearchText);
        int matches = 0;
        foreach (SettingsSectionItem item in SettingsSections)
        {
            bool match = item.Matches(words);
            item.MatchesSearch = match;
            item.OpenedBySearch = words.Count > 0 && match;
            if (match)
                matches++;
        }
        SettingsSearchStatus = words.Count == 0 ? string.Empty
            : matches == 0 ? T("No setting matches \"{0}\".", SettingsSearchText.Trim())
            : Plural("{0} section matches \"{1}\".", "{0} sections match \"{1}\".", matches, matches, SettingsSearchText.Trim());
    }

    [RelayCommand]
    private void ClearSettingsSearch() => SettingsSearchText = string.Empty;

    // ---- Open, close, move ----

    [RelayCommand]
    private void ExpandAllSettings()
    {
        foreach (SettingsSectionItem item in SettingsSections)
            item.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllSettings()
    {
        foreach (SettingsSectionItem item in SettingsSections)
            item.IsExpanded = false;
    }

    [RelayCommand]
    private void ToggleSettingsSection(SettingsSectionItem? item)
    {
        if (item != null)
            item.IsExpanded = !item.IsExpanded;
    }

    // Moves a section to a place in the list (drag and drop, the section's menu); the other sections close up.
    public void MoveSettingsSection(SettingsSectionItem item, int targetIndex)
    {
        int from = SettingsSections.IndexOf(item);
        if (from < 0)
            return;
        int to = Math.Clamp(targetIndex, 0, SettingsSections.Count - 1);
        if (from != to)
            SettingsSections.Move(from, to);
    }

    [RelayCommand]
    private void MoveSettingsSectionUp(SettingsSectionItem? item)
    {
        if (item != null)
            MoveSettingsSection(item, SettingsSections.IndexOf(item) - 1);
    }

    [RelayCommand]
    private void MoveSettingsSectionDown(SettingsSectionItem? item)
    {
        if (item != null)
            MoveSettingsSection(item, SettingsSections.IndexOf(item) + 1);
    }

    [RelayCommand]
    private void MoveSettingsSectionToTop(SettingsSectionItem? item)
    {
        if (item != null)
            MoveSettingsSection(item, 0);
    }

    [RelayCommand]
    private void MoveSettingsSectionToBottom(SettingsSectionItem? item)
    {
        if (item != null)
            MoveSettingsSection(item, SettingsSections.Count - 1);
    }

    // JoyPro's own order and only the games open.
    [RelayCommand]
    private void ResetSettingsLayout()
    {
        ApplySettingsLayout(SettingsSectionDefaults.Select(d => d.Id).ToList(), SettingsSectionsOpenByDefault);
        SaveUiSettings();
        StatusText = T("Settings: JoyPro's order of the sections again.");
    }

    // A tab at the top: a click opens or closes its section (opening scrolls to it).
    public void ToggleSettingsSectionFromTab(SettingsSectionItem item)
    {
        if (!item.MatchesSearch)
            SettingsSearchText = string.Empty;
        bool open = !item.IsContentVisible;
        if (!open && item.OpenedBySearch)
            SettingsSearchText = string.Empty;
        item.IsExpanded = open;
        if (open)
            SettingsSectionFocusRequested?.Invoke(item.Id);
    }

    // A double click on a tab: only this section open, and the page jumps to it.
    public void ShowOnlySettingsSection(SettingsSectionItem item)
    {
        SettingsSearchText = string.Empty;
        foreach (SettingsSectionItem other in SettingsSections)
            other.IsExpanded = ReferenceEquals(other, item);
        SettingsSectionFocusRequested?.Invoke(item.Id);
    }

    // Raised to scroll the page to a section (MainWindow).
    public event Action<string>? SettingsSectionFocusRequested;

    // The Settings page with the section open (a tab at the top, About from the version in the corner, ...).
    [RelayCommand]
    private void OpenSettingsSection(object? target)
    {
        SettingsSectionItem? item = target as SettingsSectionItem ?? (target is string id ? FindSettingsSection(id) : null);
        if (item == null)
            return;
        if (!item.MatchesSearch)
            SettingsSearchText = string.Empty;
        item.IsExpanded = true;
        CurrentStage = UiStage.Settings;
        SettingsSectionFocusRequested?.Invoke(item.Id);
    }
}
