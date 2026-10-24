using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Settings > Pages: the user hides pages of the bottom bar and puts them in his order (drag a row or use its arrows;
// "Reset to default" brings JoyPro's order back). Settings itself cannot be hidden. A hidden page that another page opens
// (a link such as "Open the Modifiers page", the search, follow input) shows in the bar while it is open. Kept in
// ui-settings.json (navPageOrder, hiddenNavPages); the window orders and hides its bar buttons from NavPages
// (MainWindow.axaml.cs ApplyNavBar).
public sealed partial class MainWindowViewModel
{
    // JoyPro's order: id, title, icon (resource key), the stage it opens (null: an action such as Check or Output).
    internal static readonly (string Id, string Title, string Icon, UiStage? Stage)[] NavPageDefaults =
    [
        ("start", N("Start"), "Icon.Home", UiStage.Start),
        ("relations", N("Relations"), "Icon.Link", UiStage.Relations),
        ("binds", N("Binds"), "Icon.Gamepad", UiStage.Binds),
        ("modifiers", N("Modifiers"), "Icon.Keycap", UiStage.Modifiers),
        ("layout", N("Layout"), "Icon.Image", UiStage.Layout),
        ("export", N("Export"), "Icon.Export", UiStage.Export),
        ("hud", "HUD", "Icon.Monitor", UiStage.Hud),
        ("ffb", N("Force Feedback"), "Icon.Vibrate", UiStage.FfbTelemetry),
        ("vjoy", N("Virtual Joystick"), "Icon.Joystick", UiStage.VirtualJoystick),
        ("cockpits", N("Cockpits"), "Icon.Airplane", UiStage.Cockpits),
        ("settings", N("Settings"), "Icon.Cog", UiStage.Settings),
        ("advanced", N("Advanced"), "Icon.Console", UiStage.Advanced),
        ("check", N("Check"), "Icon.Ok", null),
        ("output", N("Output"), "Icon.Console", null)
    ];

    public const string SettingsNavPageId = "settings";

    public ObservableCollection<NavPageItem> NavPages { get; } = CreateNavPages();

    private bool applyingNavPages;

    // The bar changed (order, a page hidden or shown, a hidden page opened or left): the window applies it.
    public event EventHandler? NavBarChanged;

    private static ObservableCollection<NavPageItem> CreateNavPages()
    {
        ObservableCollection<NavPageItem> pages = [];
        foreach ((string id, string title, string icon, UiStage? stage) in NavPageDefaults)
            pages.Add(new NavPageItem(id, title, icon, stage, canHide: id != SettingsNavPageId));
        return pages;
    }

    private void InitializeNavPages()
    {
        NavPages.CollectionChanged += (_, _) => OnNavPagesEdited();
        foreach (NavPageItem page in NavPages)
            page.PropertyChanged += OnNavPagePropertyChanged;
        RefreshNavPagePositions();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentStage) or nameof(EngineOutputExpanded))
                UpdateTemporaryNavPages();
        };
    }

    private void OnNavPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavPageItem.IsVisible))
        {
            if (sender is NavPageItem { CanHide: false, IsVisible: false } settings)
            {
                // Settings stays in the bar (it is the way back to this list).
                settings.IsVisible = true;
                return;
            }
            UpdateTemporaryNavPages();
            OnNavPagesEdited();
        }
        else if (e.PropertyName == nameof(NavPageItem.IsShownInBar))
        {
            NavBarChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnNavPagesEdited()
    {
        RefreshNavPagePositions();
        if (applyingNavPages)
            return;
        OnPropertyChanged(nameof(NavPagesSummary));
        NavBarChanged?.Invoke(this, EventArgs.Empty);
        SaveUiSettings();
    }

    private void RefreshNavPagePositions()
    {
        for (int i = 0; i < NavPages.Count; i++)
        {
            NavPages[i].IsFirst = i == 0;
            NavPages[i].IsLast = i == NavPages.Count - 1;
        }
    }

    // A hidden page shows in the bar while it is open (a link of another page opened it), and Output while its panel is.
    private void UpdateTemporaryNavPages()
    {
        foreach (NavPageItem page in NavPages)
        {
            bool open = page.Stage is UiStage stage ? CurrentStage == stage : page.Id == "output" && EngineOutputExpanded;
            page.IsTemporarilyShown = !page.IsVisible && open;
        }
        OnPropertyChanged(nameof(HiddenOpenPageHint));
        OnPropertyChanged(nameof(HasHiddenOpenPageHint));
    }

    // "Binds is hidden in the bar: it shows while it is open." (a line over the bar while a hidden page is open)
    public string HiddenOpenPageHint => NavPages.FirstOrDefault(p => p.IsTemporarilyShown && p.Stage != null) is { } page
        ? T("{0} is hidden in the bar and shows only while it is open.", T(page.Title))
        : string.Empty;

    public bool HasHiddenOpenPageHint => HiddenOpenPageHint.Length > 0;

    // Shows the open hidden page in the bar for good (the hint's button).
    [RelayCommand]
    private void ShowOpenHiddenPage()
    {
        foreach (NavPageItem page in NavPages.Where(p => p.IsTemporarilyShown && p.Stage != null).ToList())
            page.IsVisible = true;
    }

    public string NavPagesSummary
    {
        get
        {
            int hidden = NavPages.Count(p => !p.IsVisible);
            return hidden == 0 ? T("All {0} pages shown", NavPages.Count) : Plural("{0} page hidden", "{0} pages hidden", hidden);
        }
    }

    public NavPageItem? FindNavPage(string id) => NavPages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    // Moves a page to a new place in the list (drag and drop, the arrows).
    public void MoveNavPage(NavPageItem page, int targetIndex)
    {
        int from = NavPages.IndexOf(page);
        if (from < 0)
            return;
        int to = Math.Clamp(targetIndex, 0, NavPages.Count - 1);
        if (from != to)
            NavPages.Move(from, to);
    }

    [RelayCommand]
    private void MoveNavPageUp(NavPageItem? page)
    {
        if (page != null)
            MoveNavPage(page, NavPages.IndexOf(page) - 1);
    }

    [RelayCommand]
    private void MoveNavPageDown(NavPageItem? page)
    {
        if (page != null)
            MoveNavPage(page, NavPages.IndexOf(page) + 1);
    }

    // JoyPro's order, every page shown.
    [RelayCommand]
    private void ResetNavPages() => ApplyNavPageLayout(null, null, save: true);

    // The saved order and hidden pages onto the list (new pages of a later version go where they stand in JoyPro's order).
    private void ApplyNavPageLayout(IReadOnlyList<string>? order, IReadOnlyList<string>? hidden, bool save = false)
    {
        applyingNavPages = true;
        try
        {
            List<string> merged = SettingsSectionOrder.Merge(NavPageDefaults.Select(d => d.Id).ToList(), order);
            for (int target = 0; target < merged.Count; target++)
            {
                NavPageItem? page = FindNavPage(merged[target]);
                int current = page == null ? -1 : NavPages.IndexOf(page);
                if (current >= 0 && current != target)
                    NavPages.Move(current, target);
            }
            foreach (NavPageItem page in NavPages)
                page.IsVisible = !page.CanHide || hidden == null || !hidden.Contains(page.Id, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            applyingNavPages = false;
        }
        RefreshNavPagePositions();
        UpdateTemporaryNavPages();
        OnPropertyChanged(nameof(NavPagesSummary));
        NavBarChanged?.Invoke(this, EventArgs.Empty);
        if (save)
            SaveUiSettings();
    }

    private List<string> NavPageOrderForSave() => NavPages.Select(p => p.Id).ToList();

    private List<string> HiddenNavPagesForSave() => NavPages.Where(p => !p.IsVisible).Select(p => p.Id).ToList();
}

// One page of the bottom bar in Settings > Pages.
public sealed partial class NavPageItem : ObservableObject
{
    public NavPageItem(string id, string title, string icon, UiStage? stage, bool canHide)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Stage = stage;
        CanHide = canHide;
    }

    public string Id { get; }

    // English (N()); shown through T().
    public string Title { get; }

    public string DisplayTitle => T(Title);

    // The bar button's icon (a resource key such as Icon.Home).
    public string Icon { get; }

    public Avalonia.Media.Geometry? IconGeometry => DisplayText.Icon(Icon);

    public UiStage? Stage { get; }

    public bool CanHide { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShownInBar))]
    private bool isVisible = true;

    // Hidden, but open right now: shown in the bar until it is left.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShownInBar))]
    private bool isTemporarilyShown;

    [ObservableProperty] private bool isFirst;
    [ObservableProperty] private bool isLast;

    public bool IsShownInBar => IsVisible || IsTemporarilyShown;

    public string VisibilityTip => CanHide ? T("Show this page in the bottom bar") : T("Settings always stays in the bar (this list is there)");
}
