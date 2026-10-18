using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// One page of the variant's manual (overview, a grip with its HOTAS table, a panel) in the page list.
public sealed class CockpitPageItem
{
    public CockpitPageItem(ManualPage page, string group)
    {
        Page = page;
        Group = group;
    }

    public ManualPage Page { get; }

    // Overview, Grips, Panels.
    public string Group { get; }

    public string Title => Page.Title;

    public override string ToString() => Title;
}

// A hit of the search over the variant's pages: the page and the line that matched.
public sealed class CockpitPageHit
{
    public CockpitPageItem Page { get; init; } = null!;

    public string Text { get; init; } = string.Empty;

    public string Where => Page.Title;
}

// The Cockpits page as a manual: Overview -> Stick (drawing with numbered callouts and the HOTAS table with a column per
// mode or sensor of interest) -> Throttle -> one page per panel (drawing and numbered list), a search over every control
// and function, the seat of the version, and the export of a page (PNG) or of all pages (PDF) in the drawing style.
public sealed partial class CockpitsViewModel
{
    private bool showing;

    public ObservableCollection<CockpitVariantItem> Seats { get; } = [];

    [ObservableProperty] private CockpitVariantItem? selectedSeat;

    public bool HasSeats => Seats.Count > 1;

    public ObservableCollection<CockpitPageItem> Pages { get; } = [];

    // The grips' and the overview's pages (the tabs) and the panels (a list).
    public ObservableCollection<CockpitPageItem> MainPages { get; } = [];

    public ObservableCollection<CockpitPageItem> PanelPages { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(PageNumberText), nameof(HasPage))]
    private CockpitPageItem? selectedPage;

    [ObservableProperty] private CockpitPageItem? selectedMainPage;

    [ObservableProperty] private CockpitPageItem? selectedPanelPage;

    [ObservableProperty] private string pageSearchText = string.Empty;

    [ObservableProperty] private string exportStatus = string.Empty;

    public ObservableCollection<CockpitPageHit> PageHits { get; } = [];

    public bool HasPageHits => PageHits.Count > 0;

    public ManualPage? CurrentPage => SelectedPage?.Page;

    public bool HasPage => SelectedPage != null;

    public bool HasPanelPages => PanelPages.Count > 0;

    public string PageNumberText => SelectedPage == null ? string.Empty
        : T("Page {0} of {1}", (Pages.IndexOf(SelectedPage) + 1).ToString(CultureInfo.InvariantCulture), Pages.Count.ToString(CultureInfo.InvariantCulture));

    // The drawing style of the screen (Settings > Drawings) and the theme.
    public DrawingStyle PageStyle => DeviceReferenceService.ScreenDrawingStyle;

    // The theme is read on the UI thread only (a view model used elsewhere draws light pages).
    public static bool ThemeIsDark => Avalonia.Threading.Dispatcher.UIThread.CheckAccess() && Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    // Where exports go (Documents\JoyPro2\Cockpits); tests set it.
    public string ExportFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro2", "Cockpits");

    private bool syncingPages;

    private ManualPageOptions PageOptions(string highlight)
    {
        CockpitPictureSet? pictures = FindGamePictures(Variant);
        BuildGuide(Variant, pictures);
        return new()
        {
            Dark = ThemeIsDark,
            Colors = PageStyle.Resolve(ThemeIsDark),
            Folder = CockpitService.Folder() ?? string.Empty,
            Highlight = highlight,
            Mark = compared.Count == 0 ? null : CompareMark,
            Pictures = pictures,
            PictureMode = PictureModeKey,
            Guide = GuideTree
        };
    }

    // "✓ relation (input)" for a row the profile holds, "–" for a row with a function id it does not.
    private string? CompareMark(CockpitControl c, CockpitFunction f, string mode)
    {
        string fn = mode.Length > 0 ? f.FnIn(mode) : f.Fn;
        if (fn.Length == 0)
            return null;
        return compared.TryGetValue(c.Id + "|" + f.Dir, out string? relation) ? "✓ " + relation : T("(no relation)");
    }

    private void BuildPages(string? keepId = null)
    {
        keepId ??= SelectedPage?.Page.Id;
        syncingPages = true;
        Pages.Clear();
        MainPages.Clear();
        PanelPages.Clear();
        CockpitVariant? v = Variant;
        if (catalog != null && v != null)
        {
            foreach (ManualPage page in CockpitManualPages.Build(catalog, v, PageOptions(PageSearchText)))
            {
                CockpitPageItem item = new(page, page.Kind == "panel" ? T("Panels") : page.Kind == "device" ? T("Grips") : T("Overview"));
                Pages.Add(item);
                if (page.Kind == "panel")
                    PanelPages.Add(item);
                else
                    MainPages.Add(item);
            }
        }
        syncingPages = false;
        SelectedPage = Pages.FirstOrDefault(p => p.Page.Id == keepId) ?? Pages.FirstOrDefault();
        OnPropertyChanged(nameof(HasPanelPages));
        OnPropertyChanged(nameof(PageNumberText));
        RunPageSearch();
    }

    partial void OnSelectedPageChanged(CockpitPageItem? value)
    {
        if (syncingPages)
            return;
        syncingPages = true;
        SelectedMainPage = value != null && MainPages.Contains(value) ? value : null;
        SelectedPanelPage = value != null && PanelPages.Contains(value) ? value : null;
        syncingPages = false;
    }

    partial void OnSelectedMainPageChanged(CockpitPageItem? value)
    {
        if (!syncingPages && value != null)
            SelectedPage = value;
    }

    partial void OnSelectedPanelPageChanged(CockpitPageItem? value)
    {
        if (!syncingPages && value != null)
            SelectedPage = value;
    }

    // A link on a page ("panel:<id>", "device:<device>", "overview").
    public bool OpenPage(string id)
    {
        CockpitPageItem? page = Pages.FirstOrDefault(p => string.Equals(p.Page.Id, id, StringComparison.OrdinalIgnoreCase));
        if (page == null)
            return false;
        if (!ReferenceEquals(page, SelectedPage))
            SelectedTag = string.Empty;
        SelectedPage = page;
        return true;
    }

    [RelayCommand]
    private void NextPage() => Step(1);

    [RelayCommand]
    private void PreviousPage() => Step(-1);

    private void Step(int delta)
    {
        if (Pages.Count == 0)
            return;
        int at = SelectedPage == null ? 0 : Pages.IndexOf(SelectedPage);
        SelectedPage = Pages[Math.Clamp(at + delta, 0, Pages.Count - 1)];
    }

    // ------------------------------------------------------------------ search over the variant's pages

    // The search box searches the pages (marks) and the guide tree (filters it).
    partial void OnPageSearchTextChanged(string value)
    {
        BuildPages();
        GuideSearchText = value ?? string.Empty;
    }

    private void RunPageSearch()
    {
        PageHits.Clear();
        string query = PageSearchText ?? string.Empty;
        List<string> terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CockpitCatalog.Norm).Where(t => t.Length > 0).ToList();
        if (terms.Count > 0)
        {
            foreach (CockpitPageItem page in Pages)
            {
                // The page's highlighted items: a table cell, a callout or a list line.
                foreach (ManualPageItem item in page.Page.Items.Where(i => i.Highlight && i.Lines.Count > 0).Take(12))
                {
                    string text = string.Join(" ", item.Lines);
                    if (PageHits.Any(h => h.Page == page && h.Text == text))
                        continue;
                    PageHits.Add(new CockpitPageHit { Page = page, Text = text });
                    if (PageHits.Count >= 60)
                        break;
                }
            }
        }
        OnPropertyChanged(nameof(HasPageHits));
        if (PageHits.Count > 0 && SelectedPage != null && !PageHits.Any(h => h.Page == SelectedPage))
            SelectedPage = PageHits[0].Page;
    }

    [RelayCommand]
    private void OpenHit(CockpitPageHit? hit)
    {
        if (hit != null)
            SelectedPage = hit.Page;
    }

    // The tooltip of a page item: the JoyPro function a cell maps to, or the control.
    public string? TagText(string tag)
    {
        List<string> parts = [];
        foreach (string t in tag.Split('|'))
        {
            if (t.StartsWith("fn:", StringComparison.Ordinal))
            {
                string id = t[3..];
                parts.Add(T("JoyPro function: {0}", (CockpitService.FunctionNames.TryGetValue(id, out string? name) ? name + " (" + id + ")" : id)));
            }
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    // ------------------------------------------------------------------ export

    private string ExportName(string suffix)
    {
        CockpitVariant? v = Variant;
        string name = v == null ? "cockpit" : (v.Aircraft?.Id ?? "aircraft") + "-" + v.Id;
        return Path.Combine(ExportFolder, CockpitCatalog.Slug(name) + (suffix.Length > 0 ? "-" + CockpitCatalog.Slug(suffix) : string.Empty));
    }

    [RelayCommand]
    private void ExportPage()
    {
        if (SelectedPage == null)
            return;
        try
        {
            Directory.CreateDirectory(ExportFolder);
            string file = ExportName(SelectedPage.Page.Id) + ".png";
            Views.Controls.ManualPageView.SavePng(SelectedPage.Page, file, 1.5, PageStyle, ThemeIsDark);
            ExportStatus = T("Saved: {0}", file);
        }
        catch (Exception ex)
        {
            ExportStatus = T("The export failed: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void ExportAllPages()
    {
        if (Pages.Count == 0)
            return;
        try
        {
            Directory.CreateDirectory(ExportFolder);
            string file = ExportName(string.Empty) + ".pdf";
            Views.Controls.ManualPageView.SavePdf(Pages.Select(p => p.Page), file, 1.5, PageStyle, ThemeIsDark);
            ExportStatus = T("Saved: {0}", file);
        }
        catch (Exception ex)
        {
            ExportStatus = T("The export failed: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        try
        {
            Directory.CreateDirectory(ExportFolder);
            ExternalProcess.Start(new System.Diagnostics.ProcessStartInfo(ExportFolder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No shell: nothing to do.
        }
    }
}
