using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Use this layout": the variant the user picked on the Cockpits page, for the setup wizard. The wizard can read the
// whole layout with "engine cockpits get <AircraftId> <VariantId>" (EngineArguments), docs/cockpits.md.
public sealed record CockpitLayoutRequest(string AircraftId, string VariantId, string AircraftName, string VariantName,
    IReadOnlyList<string> Games, IReadOnlyList<string> EngineArguments);

public sealed class CockpitAircraftItem
{
    public CockpitAircraftItem(CockpitAircraft aircraft) => Aircraft = aircraft;

    public CockpitAircraft Aircraft { get; }

    public string Id => Aircraft.Id;

    public string Name => Aircraft.Name;

    public string Subtitle
    {
        get
        {
            List<string> parts = [];
            if (Aircraft.Fiction)
                parts.Add(Aircraft.Franchise);
            else if (Aircraft.Maker.Length > 0)
                parts.Add(Aircraft.Maker);
            parts.Add(Plural("{0} variant", "{0} variants", Aircraft.Variants.Count));
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public string KindText => Aircraft.Fiction ? T("Fiction") : Aircraft.Category switch
    {
        "helicopter" => T("Helicopter"),
        "space" => T("Spacecraft"),
        _ => T("Fixed-wing")
    };

    public override string ToString() => Name;
}

public sealed class CockpitVariantItem
{
    public CockpitVariantItem(CockpitVariant variant) => Variant = variant;

    public CockpitVariant Variant { get; }

    public string Name => Variant.Name;

    public string Subtitle => string.Join(" · ", new[]
    {
        Variant.Years,
        Variant.Seat.Length > 0 ? T("Seat: {0}", Variant.Seat) : string.Empty,
        string.Join(", ", Variant.Games.Select(g => g.Game).Distinct(StringComparer.OrdinalIgnoreCase))
    }.Where(s => s.Length > 0));

    public override string ToString() => Name;
}

// One device of the variant: its drawing with the callout tables (the Layout page's canvas in view mode).
public sealed class CockpitSheetItem
{
    // lineArt: JoyPro's own drawing of this aircraft's grip with the positions of its controls; it comes before the
    // schematic drawing when it places at least half of the sheet's controls.
    public CockpitSheetItem(CockpitSheet sheet, Func<string, string> text, DeviceLineArt? lineArt = null)
    {
        Sheet = sheet;
        Callouts = new CalloutSheetViewModel { TextOf = (input, modifier) => modifier ? string.Empty : text(input) };
        Dictionary<string, double[]> onArt = lineArt == null ? [] : sheet.Known.Keys.Where(lineArt.Parts.ContainsKey)
            .ToDictionary(k => k, k => lineArt.Parts[k], StringComparer.OrdinalIgnoreCase);
        if (lineArt != null && sheet.Known.Count > 0 && onArt.Count * 2 >= sheet.Known.Count)
        {
            LineArt = new Views.Controls.LineArtImage(lineArt, DeviceReferenceService.ScreenDrawingStyle);
            Callouts.Show(sheet.Schematic, onArt, lineArt.Width, lineArt.Height, null);
        }
        else
        {
            Callouts.Show(sheet.Schematic, sheet.Known, sheet.Schematic.Width, sheet.Schematic.Height, sheet.Sheet);
        }
    }

    public CockpitSheet Sheet { get; }

    public CalloutSheetViewModel Callouts { get; }

    // JoyPro's own drawing of the real grip (shown instead of the schematic), or null.
    public Views.Controls.LineArtImage? LineArt { get; }

    public DeviceSchematic? Schematic => LineArt == null ? Sheet.Schematic : null;

    public string Title => Sheet.Title;

    public string Subtitle => LineArt != null ? T("JoyPro's drawing of the {0}", Sheet.Title.ToLowerInvariant())
        : Sheet.Drawing.Generic
        ? T("Generic drawing (JoyPro has no drawing of this grip{0}", (Sheet.Controls.Count > 0 ? T("): the controls in their order") : ")"))
        : T("{0} · JoyPro's drawing", Sheet.Drawing.Name);

    public bool HasUnplaced => Sheet.Unplaced > 0;

    public string UnplacedText => T("{0} control(s) not on the drawing: see \"Other controls\"", Sheet.Unplaced);
}

public sealed class CockpitPanelItem
{
    public string Name { get; init; } = string.Empty;

    public string Area { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}

public sealed partial class CockpitLinkItem : ObservableObject
{
    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    [RelayCommand]
    private void Open() => CockpitsViewModel.OpenUrl(Url);
}

public sealed partial class CockpitPhotoItem : ObservableObject
{
    public string Url { get; init; } = string.Empty;

    public string Page { get; init; } = string.Empty;

    public string Caption { get; init; } = string.Empty;

    public string Credit { get; init; } = string.Empty;

    [ObservableProperty] private Bitmap? image;

    [ObservableProperty] private string status = string.Empty;

    public bool HasPage => !string.IsNullOrWhiteSpace(Page);

    // A photo file to open (not only a page about it).
    public bool HasPhoto => !string.IsNullOrWhiteSpace(Url);

    // The photo could not be loaded: "Try again" is offered.
    [ObservableProperty] private bool failed;

    // Loads the photo again (set by the page; null = nothing to do).
    public Func<CockpitPhotoItem, Task>? Reload { get; set; }

    [RelayCommand]
    private void OpenPage() => CockpitsViewModel.OpenUrl(string.IsNullOrWhiteSpace(Page) ? Url : Page);

    // The photo itself in the browser (full size, to save it from there).
    [RelayCommand]
    private void OpenPhoto() => CockpitsViewModel.OpenUrl(HasPhoto ? Url : Page);

    [RelayCommand]
    private async Task TryAgain()
    {
        if (Reload != null)
            await Reload(this);
    }
}

// The Cockpits page (docs/cockpits.md): the cockpit database as a reference. Pick an aircraft (search and filters),
// its variant, and see the stick-side and throttle-side drawings with what every control does, the cockpit around
// the pilot, sources, and the photo links after the user's consent. "Compare with my setup" marks the functions the
// open profile has relations for; "Use this layout" hands the variant to the setup wizard.
public sealed partial class CockpitsViewModel : ObservableObject
{
    // The filters' values stay English (the view shows them translated, TConverter).
    private const string All = "All";
    private CockpitCatalog? catalog;
    private bool loaded;
    // control id + "|" + dir -> "relation (device input)" of the profile.
    private Dictionary<string, string> compared = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CockpitAircraftItem> AircraftList { get; } = [];

    public ObservableCollection<CockpitVariantItem> Variants { get; } = [];

    public ObservableCollection<CockpitSheetItem> Sheets { get; } = [];

    public ObservableCollection<CockpitPanelItem> Panels { get; } = [];

    public ObservableCollection<CockpitLinkItem> Sources { get; } = [];

    public ObservableCollection<CockpitPhotoItem> Photos { get; } = [];

    public ObservableCollection<string> GameOptions { get; } = [];

    public ObservableCollection<string> EraOptions { get; } = [];

    public ObservableCollection<string> RoleOptions { get; } = [];

    public ObservableCollection<string> NationOptions { get; } = [];

    public ObservableCollection<string> KindOptions { get; } = [All, N("Fixed-wing"), N("Helicopter"), N("Space"), N("Fiction"), N("Real only")];

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string selectedGame = All;
    [ObservableProperty] private string selectedEra = All;
    [ObservableProperty] private string selectedRole = All;
    [ObservableProperty] private string selectedNation = All;
    [ObservableProperty] private string selectedKind = All;
    [ObservableProperty] private CockpitAircraftItem? selectedAircraft;
    [ObservableProperty] private CockpitVariantItem? selectedVariant;
    [ObservableProperty] private DeviceSchematic? plan;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string compareStatus = string.Empty;
    [ObservableProperty] private bool isComparing;
    [ObservableProperty] private string photoStatus = string.Empty;
    // The device sheets next to each other (default) or one below the other (larger tables).
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Stacked))] private bool sideBySide = true;

    public bool Stacked => !SideBySide;

    // Runs "engine cockpits compare <aircraft> <variant>" with the open profile; returns its JSON (null = no profile).
    public Func<string, string, Task<string?>>? CompareRunner { get; set; }

    // The setup wizard takes the layout from here (MainWindowViewModel.PendingCockpitLayout).
    public event EventHandler<CockpitLayoutRequest>? UseLayoutRequested;

    // The variant shown: the picked seat of the picked version (the version itself when it has one seat).
    public CockpitVariant? Variant => SelectedSeat?.Variant ?? SelectedVariant?.Variant;

    public CockpitAircraft? Aircraft => SelectedAircraft?.Aircraft;

    public bool HasVariant => Variant != null;

    public bool HasNoVariant => Variant == null;

    public string Title => Aircraft == null ? T("Cockpits") : Aircraft.Name + (Variant == null ? string.Empty : " · " + Variant.Name);

    public string Summary => Aircraft?.Summary ?? string.Empty;

    public string Facts
    {
        get
        {
            if (Aircraft == null || Variant == null)
                return string.Empty;
            List<string> facts = [];
            if (Variant.Years.Length > 0) facts.Add(Variant.Years);
            if (Aircraft.Nations.Count > 0) facts.Add(string.Join(", ", Aircraft.Nations));
            if (Aircraft.Role.Length > 0) facts.Add(Aircraft.Role.Replace('-', ' '));
            if (Variant.Seat.Length > 0) facts.Add(T("Seat: {0}", Variant.Seat));
            if (Variant.Games.Count > 0) facts.Add(T("In: {0}", string.Join(", ", Variant.Games.Select(g => g.Plane.Length > 0 ? g.Game + " (" + g.Plane + ")" : g.Game).Distinct())));
            facts.Add(T("Confidence: {0}", (Variant.Confidence.Length > 0 ? Variant.Confidence : "-")));
            return string.Join(" · ", facts);
        }
    }

    public bool IsFiction => Aircraft?.Fiction == true;

    public string FictionNote => Aircraft == null ? string.Empty :
        T("A craft of {0}. JoyPro draws no film or game designs: a generic stick and throttle with the functions as the work shows them, and links to see the real thing.", Aircraft.Franchise);

    public string CockpitText => Variant?.Cockpit ?? string.Empty;

    public bool HasCockpitText => !string.IsNullOrWhiteSpace(CockpitText);

    public string Notes => Variant?.Notes ?? string.Empty;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public bool HasPanels => Panels.Count > 0;

    public bool HasPhotos => Aircraft?.Photos.Count > 0;

    public bool AskPhotos => HasPhotos && CockpitPhotoConsent.For(Aircraft?.Id) == PhotoConsentState.Ask;

    public bool PhotosDenied => HasPhotos && CockpitPhotoConsent.For(Aircraft?.Id) == PhotoConsentState.Denied;

    public string PhotoQuestion => Aircraft == null ? string.Empty : CockpitPhotoConsent.Question(Aircraft.Name, Aircraft.Photos.Select(p => p.Url.Length > 0 ? p.Url : p.Page));

    public string PhotoHeader => Aircraft == null ? T("Photos") : T("Photos ({0} links)", Aircraft.Photos.Count.ToString(CultureInfo.InvariantCulture));

    public int Count => AircraftList.Count;

    public string CountText => Plural("{0} aircraft", "{0} aircraft", AircraftList.Count);

    // Loads the database the first time the page is shown.
    public void EnsureLoaded()
    {
        if (loaded)
            return;
        loaded = true;
        Reload();
    }

    public void Reload()
    {
        catalog = CockpitService.Catalog;
        loaded = true;
        RefreshPicturesStatus();
        RebuildOptions();
        ApplyFilter();
        int variants = catalog.Listed.Sum(a => a.Variants.Count);
        Status = catalog.Aircraft.Count == 0
            ? T("No cockpit database found ({0}).", catalog.Folder)
            : T("{0} aircraft, {1} variants.", catalog.Listed.Count().ToString(CultureInfo.InvariantCulture), variants.ToString(CultureInfo.InvariantCulture));
        if (SelectedAircraft == null && AircraftList.Count > 0)
            SelectedAircraft = AircraftList.FirstOrDefault(a => a.Id == "f-16") ?? AircraftList[0];
    }

    // Shows an aircraft and variant (ids, names or aliases), e.g. from a search elsewhere.
    public bool Show(string aircraft, string? variant = null)
    {
        EnsureLoaded();
        if (catalog == null)
            return false;
        CockpitVariant? v = catalog.FindVariant(aircraft, variant ?? string.Empty);
        if (v == null)
            return false;
        CockpitAircraftItem? item = AircraftList.FirstOrDefault(a => a.Aircraft == v.Aircraft);
        if (item == null)
        {
            SearchText = string.Empty;
            SelectedGame = SelectedEra = SelectedRole = SelectedNation = SelectedKind = All;
            ApplyFilter();
            item = AircraftList.FirstOrDefault(a => a.Aircraft == v.Aircraft);
        }
        if (item == null)
            return false;
        SelectedAircraft = item;
        // A seat: its version first, then the seat.
        string version = v.SeatOf.Length > 0 ? v.SeatOf : v.Id;
        SelectedVariant = Variants.FirstOrDefault(x => string.Equals(x.Variant.Id, version, StringComparison.OrdinalIgnoreCase))
                          ?? Variants.FirstOrDefault(x => string.Equals(x.Variant.Id, v.Id, StringComparison.OrdinalIgnoreCase)) ?? SelectedVariant;
        SelectedSeat = Seats.FirstOrDefault(x => string.Equals(x.Variant.Id, v.Id, StringComparison.OrdinalIgnoreCase)) ?? SelectedSeat;
        return true;
    }

    private void RebuildOptions()
    {
        if (catalog == null)
            return;
        List<CockpitAircraft> listed = catalog.Listed.ToList();
        Fill(GameOptions, listed.SelectMany(a => a.Variants.Select(v => catalog.Resolve(v))).SelectMany(v => v.Games.Select(g => g.Game)));
        Fill(EraOptions, listed.Select(a => a.Era), CockpitCatalog.Eras);
        Fill(RoleOptions, listed.Select(a => a.Role));
        Fill(NationOptions, listed.SelectMany(a => a.Nations));
        // The lists were rebuilt (the combo boxes cleared their selection): keep the filters, else "All".
        SelectedGame = Keep(GameOptions, SelectedGame);
        SelectedEra = Keep(EraOptions, SelectedEra);
        SelectedRole = Keep(RoleOptions, SelectedRole);
        SelectedNation = Keep(NationOptions, SelectedNation);
        SelectedKind = Keep(KindOptions, SelectedKind);
    }

    private static string Keep(ObservableCollection<string> options, string? value) =>
        options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? All;

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values, IList<string>? order = null)
    {
        List<string> distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        distinct = order == null
            ? distinct.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList()
            : distinct.OrderBy(v => order.IndexOf(v) < 0 ? 99 : order.IndexOf(v)).ToList();
        target.Clear();
        target.Add(All);
        foreach (string v in distinct)
            target.Add(v);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedGameChanged(string value) => ApplyFilter();
    partial void OnSelectedEraChanged(string value) => ApplyFilter();
    partial void OnSelectedRoleChanged(string value) => ApplyFilter();
    partial void OnSelectedNationChanged(string value) => ApplyFilter();
    partial void OnSelectedKindChanged(string value) => ApplyFilter();

    private static string Value(string? option) => string.IsNullOrWhiteSpace(option) || option == All ? string.Empty : option;

    public void ApplyFilter()
    {
        if (catalog == null)
            return;
        string kind = Value(SelectedKind) switch
        {
            "Fixed-wing" => "fixed-wing",
            "Helicopter" => "helicopter",
            "Space" => "space",
            "Fiction" => "fiction",
            "Real only" => "real",
            _ => string.Empty
        };
        List<CockpitAircraft> found = catalog.Search(new CockpitFilter
        {
            Query = SearchText ?? string.Empty,
            Game = Value(SelectedGame),
            Era = Value(SelectedEra),
            Role = Value(SelectedRole),
            Nation = Value(SelectedNation),
            Category = kind
        });
        // A search for a control or a function ("TMS", "china hat") also finds the aircraft that have it.
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(SearchText))
            found = catalog.SearchControls(SearchText, 400).Select(h => h.Item1.Aircraft).Where(a => a != null).Distinct().ToList()!;
        CockpitAircraft? keep = SelectedAircraft?.Aircraft;
        AircraftList.Clear();
        foreach (CockpitAircraft a in found)
            AircraftList.Add(new CockpitAircraftItem(a));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        CockpitAircraftItem? again = keep == null ? null : AircraftList.FirstOrDefault(a => a.Aircraft == keep);
        if (again != null)
            SelectedAircraft = again;
    }

    partial void OnSelectedAircraftChanged(CockpitAircraftItem? value)
    {
        Variants.Clear();
        if (value != null && catalog != null)
        {
            // The versions; a seat of a version (seatOf) is picked under "Seat".
            foreach (CockpitVariant v in value.Aircraft.Variants.Where(x => x.SeatOf.Length == 0 || !value.Aircraft.Variants.Any(b => string.Equals(b.Id, x.SeatOf, StringComparison.OrdinalIgnoreCase))))
                Variants.Add(new CockpitVariantItem(catalog.Resolve(v)));
        }
        SelectedVariant = Variants.FirstOrDefault();
        if (SelectedVariant == null)
            ShowVariant();
    }

    partial void OnSelectedVariantChanged(CockpitVariantItem? value)
    {
        compared.Clear();
        comparedCommands.Clear();
        CompareStatus = string.Empty;
        showing = true;
        Seats.Clear();
        if (value != null && catalog != null && SelectedAircraft != null)
        {
            Seats.Add(new CockpitVariantItem(value.Variant));
            foreach (CockpitVariant seat in SelectedAircraft.Aircraft.Variants.Where(x => string.Equals(x.SeatOf, value.Variant.Id, StringComparison.OrdinalIgnoreCase)))
                Seats.Add(new CockpitVariantItem(catalog.Resolve(seat)));
        }
        SelectedSeat = Seats.FirstOrDefault();
        showing = false;
        OnPropertyChanged(nameof(HasSeats));
        ShowVariant();
    }

    partial void OnSelectedSeatChanged(CockpitVariantItem? value)
    {
        if (showing)
            return;
        compared.Clear();
        CompareStatus = string.Empty;
        ShowVariant();
    }

    private void ShowVariant()
    {
        Sheets.Clear();
        Panels.Clear();
        Sources.Clear();
        Photos.Clear();
        PhotoStatus = string.Empty;
        CockpitVariant? v = Variant;
        if (catalog != null && v != null)
        {
            BuildSheets();
            foreach (CockpitPanel p in v.Panels)
                Panels.Add(new CockpitPanelItem { Name = p.Name, Area = CockpitSchematic.AreaTitle(p.Area), Text = p.Text });
            foreach (CockpitSource s in v.Sources)
                Sources.Add(new CockpitLinkItem { Title = s.Title, Url = s.Url, Detail = SourceKind(s.Kind) + (s.Covers.Length > 0 ? " · " + s.Covers : string.Empty) });
            foreach (CockpitLink l in v.Aircraft?.Links ?? [])
                Sources.Add(new CockpitLinkItem { Title = l.Title, Url = l.Url, Detail = T("Link") });
            // No plan of area boxes (the user's verdict): the pictures from the games or the guide's tree show the cockpit.
            Plan = null;
            PhotoLoad = LoadPhotosAsync();
        }
        else
        {
            Plan = null;
        }
        BuildPages();
        PackCheck = CheckPicturePackAsync();
        foreach (string name in new[]
                 {
                     nameof(HasVariant), nameof(HasNoVariant), nameof(Title), nameof(Summary), nameof(Facts), nameof(IsFiction), nameof(FictionNote),
                     nameof(CockpitText), nameof(HasCockpitText), nameof(Notes), nameof(HasNotes), nameof(HasPanels), nameof(HasPhotos), nameof(AskPhotos),
                     nameof(PhotosDenied), nameof(PhotoQuestion), nameof(PhotoHeader)
                 })
            OnPropertyChanged(name);
    }

    private void BuildSheets()
    {
        Sheets.Clear();
        MainSheets.Clear();
        ExtraSheets.Clear();
        if (catalog == null || Variant == null)
            return;
        // JoyPro's own drawings of this aircraft's real grips (DB\Cockpits\line-drawings): before the schematic
        // drawing when they carry the controls' positions, else shown with the photos (no download, no consent).
        // Read one by one (the folder also holds every panel drawing of every aircraft).
        List<DeviceLineArt> traced = [];
        CockpitDrawings.Clear();
        foreach (CockpitSheet sheet in CockpitSchematic.SheetsOf(catalog, Variant))
        {
            CockpitSheet s = sheet;
            // The traced line drawings are not shown (CockpitManualPages.TracedDrawings).
            DeviceLineArt? art = CockpitManualPages.TracedDrawings ? CockpitManualPages.DeviceArt(CockpitService.Folder() ?? string.Empty, Variant, s.Device) : null;
            if (art != null)
                traced.Add(art);
            CockpitSheetItem item = new(s, input => RowText(s, input), art);
            Sheets.Add(item);
            // The first stick-side and the first throttle-side device go side by side; the rest (a second grip set,
            // pedals, panels) below at full width.
            bool stickSide = CockpitCatalog.IsStickDevice(s.Device), throttleSide = CockpitCatalog.IsThrottleDevice(s.Device);
            if ((stickSide && !MainSheets.Any(m => CockpitCatalog.IsStickDevice(m.Sheet.Device))) ||
                (throttleSide && !MainSheets.Any(m => CockpitCatalog.IsThrottleDevice(m.Sheet.Device))))
                MainSheets.Add(item);
            else
                ExtraSheets.Add(item);
        }
        foreach (DeviceLineArt art in traced.Where(a => !Sheets.Any(s => s.LineArt?.Art == a)))
            CockpitDrawings.Add(new Views.Controls.LineArtImage(art, DeviceReferenceService.ScreenDrawingStyle));
        OnPropertyChanged(nameof(HasExtraSheets));
        OnPropertyChanged(nameof(HasCockpitDrawings));
    }

    // JoyPro's own drawings of the aircraft's grips without positions: shown before the photo links.
    public ObservableCollection<Views.Controls.LineArtImage> CockpitDrawings { get; } = [];

    public bool HasCockpitDrawings => CockpitDrawings.Count > 0;

    // The stick side and the throttle side (shown next to each other).
    public ObservableCollection<CockpitSheetItem> MainSheets { get; } = [];

    // Further devices of the variant (shown below, full width).
    public ObservableCollection<CockpitSheetItem> ExtraSheets { get; } = [];

    public bool HasExtraSheets => ExtraSheets.Count > 0;

    // A row's text, with the profile's relation after a comparison.
    private string RowText(CockpitSheet sheet, string input)
    {
        string text = sheet.TextOf(input, false);
        if (compared.Count == 0 || !sheet.Rows.TryGetValue(input, out Tuple<CockpitControl, CockpitFunction>? row))
            return text;
        if (row.Item2.Fn.Length == 0)
            return text;
        return compared.TryGetValue(row.Item1.Id + "|" + row.Item2.Dir, out string? relation) ? text + "  ✓ " + relation : T("{0}  (no relation)", text);
    }

    private static string SourceKind(string kind) => kind switch
    {
        "manual" => T("Flight manual"),
        "game-manual" => T("Game manual"),
        "wikipedia" => "Wikipedia",
        "reference" => T("Reference"),
        "tutorial" => T("Tutorial"),
        "film" => T("Film / series"),
        _ => kind
    };

    // ------------------------------------------------------------------ photos

    // The photo loading under way (tests and the journey wait for it).
    public Task PhotoLoad { get; private set; } = Task.CompletedTask;

    private async Task LoadPhotosAsync()
    {
        CockpitAircraft? aircraft = Aircraft;
        CockpitVariant? variant = Variant;
        if (aircraft == null || aircraft.Photos.Count == 0)
            return;
        List<CockpitPhoto> photos = aircraft.Photos
            .OrderBy(p => p.Variant.Length > 0 && variant != null && string.Equals(p.Variant, variant.Id, StringComparison.OrdinalIgnoreCase) ? 0 : p.Variant.Length == 0 ? 1 : 2)
            .ToList();
        foreach (CockpitPhoto p in photos)
        {
            Photos.Add(new CockpitPhotoItem
            {
                Url = p.Url,
                Page = p.Page,
                Caption = (p.Caption.Length > 0 ? p.Caption : p.View) + (p.Variant.Length > 0 ? " (" + p.Variant + ")" : string.Empty),
                Credit = string.Join(" · ", new[] { p.Credit, p.License, DevicePhotoConsent.SiteOf(p.Page.Length > 0 ? p.Page : p.Url) }.Where(s => !string.IsNullOrWhiteSpace(s)))
            });
        }
        if (!CockpitPhotoConsent.Allows(aircraft.Id))
        {
            PhotoStatus = PhotosDenied ? T("Photos are off for this aircraft (Settings > Privacy, or answer again below). The links open in your browser.") : string.Empty;
            return;
        }
        if (!DeviceImageCache.InternetPicturesEnabled || DeviceImageCache.InternetPicturesBlocked)
        {
            PhotoStatus = T("Pictures from the internet are switched off.");
            return;
        }
        PhotoStatus = T("Loading the photos...");
        int loadedCount = 0;
        foreach (CockpitPhotoItem item in Photos.ToList())
        {
            if (aircraft != Aircraft)
                break;
            if (!item.HasPhoto)
            {
                item.Status = T("Only a page about it (open the page).");
                continue;
            }
            // Checked again before every download: the answer may have changed meanwhile.
            if (!CockpitPhotoConsent.Allows(aircraft.Id))
                break;
            item.Reload = ReloadPhotoAsync;
            if (await LoadPhotoAsync(item))
                loadedCount++;
        }
        if (aircraft == Aircraft)
            PhotoStatus = PhotoCountText(loadedCount);
    }

    private string PhotoCountText(int loaded)
    {
        int photoCount = Photos.Count(p => p.HasPhoto);
        return photoCount == Photos.Count
            ? T("{0} of {1} photos shown (stored only on this PC).", loaded, photoCount)
            : T("{0} of {1} photos shown (stored only on this PC); {2} only link to a page.", loaded, photoCount, Photos.Count - photoCount);
    }

    // One photo: Wikimedia's thumbnail (what Wikimedia asks programs to use), else the file itself; one download at a
    // time and again after "too many requests" (DeviceImageCache).
    private async Task<bool> LoadPhotoAsync(CockpitPhotoItem item)
    {
        item.Failed = false;
        item.Status = string.Empty;
        string? thumb = CommonsThumbnail(item.Url, 960);
        Bitmap? bitmap = thumb == null ? null : await DeviceImageCache.LoadAsync(thumb, 640);
        bitmap ??= await DeviceImageCache.LoadAsync(item.Url, 640);
        item.Image = bitmap;
        item.Failed = bitmap == null;
        item.Status = bitmap == null ? T("Could not load the photo. Try again, or open the photo or its page in your browser.") : string.Empty;
        return bitmap != null;
    }

    private async Task ReloadPhotoAsync(CockpitPhotoItem item)
    {
        CockpitAircraft? aircraft = Aircraft;
        if (aircraft == null || !CockpitPhotoConsent.Allows(aircraft.Id) || !DeviceImageCache.InternetPicturesEnabled || DeviceImageCache.InternetPicturesBlocked)
            return;
        item.Status = T("Loading the photos...");
        await LoadPhotoAsync(item);
        if (aircraft == Aircraft)
            PhotoStatus = PhotoCountText(Photos.Count(p => p.Image != null));
    }

    // https://upload.wikimedia.org/wikipedia/commons/a/ab/Name.jpg -> .../commons/thumb/a/ab/Name.jpg/960px-Name.jpg
    // (Wikimedia's standard thumbnail address); null for any other address.
    public static string? CommonsThumbnail(string url, int width)
    {
        const string prefix = "https://upload.wikimedia.org/wikipedia/commons/";
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || url.Contains("/thumb/", StringComparison.OrdinalIgnoreCase))
            return null;
        string rest = url[prefix.Length..];
        string[] parts = rest.Split('/');
        if (parts.Length != 3 || parts[0].Length != 1 || parts[1].Length != 2)
            return null;
        string name = parts[2];
        string thumbName = name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name + ".png"
            : name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ? "lossy-page1-" + name + ".jpg"
            : name;
        return prefix + "thumb/" + parts[0] + "/" + parts[1] + "/" + name + "/" + width.ToString(CultureInfo.InvariantCulture) + "px-" + thumbName;
    }

    [RelayCommand]
    private void PhotoAnswer(string? answer)
    {
        CockpitPhotoConsent.Answer(Aircraft?.Id, answer ?? "no");
        OnPropertyChanged(nameof(AskPhotos));
        OnPropertyChanged(nameof(PhotosDenied));
        Photos.Clear();
        PhotoLoad = LoadPhotosAsync();
    }

    // ------------------------------------------------------------------ compare and use

    [RelayCommand]
    private async Task CompareAsync()
    {
        CockpitVariant? v = Variant;
        if (v == null || v.Aircraft == null)
            return;
        if (CompareRunner == null)
        {
            CompareStatus = T("Open a profile to compare it with this cockpit.");
            return;
        }
        IsComparing = true;
        CompareStatus = T("Comparing with your profile...");
        try
        {
            string? json = await CompareRunner(v.Aircraft.Id, v.Id);
            if (json == null)
            {
                CompareStatus = T("Open a profile first (Start page), then compare.");
                return;
            }
            ApplyCompare(json);
        }
        catch (Exception ex)
        {
            CompareStatus = T("The comparison failed: {0}", ex.Message);
        }
        finally
        {
            IsComparing = false;
        }
    }

    // The JSON of "engine cockpits compare": Functions[] with Control, Dir and Relations[] (Relation, Device, Input).
    public void ApplyCompare(string json)
    {
        compared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int start = json.IndexOf('{');
        if (start < 0)
        {
            CompareStatus = T("The engine gave no comparison.");
            return;
        }
        using JsonDocument doc = JsonDocument.Parse(json[start..]);
        JsonElement root = doc.RootElement;
        int matched = root.TryGetProperty("Matched", out JsonElement m) ? m.GetInt32() : 0;
        int total = root.TryGetProperty("Total", out JsonElement t) ? t.GetInt32() : 0;
        if (root.TryGetProperty("Functions", out JsonElement functions))
        {
            foreach (JsonElement f in functions.EnumerateArray())
            {
                string control = f.GetProperty("Control").GetString() ?? string.Empty;
                string dir = f.GetProperty("Dir").GetString() ?? string.Empty;
                List<string> relations = [];
                foreach (JsonElement r in f.GetProperty("Relations").EnumerateArray())
                {
                    string name = r.GetProperty("Relation").GetString() ?? string.Empty;
                    string input = r.TryGetProperty("Input", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
                    relations.Add(input.Length > 0 ? name + " (" + input + ")" : name);
                }
                if (relations.Count > 0)
                    compared[control + "|" + dir] = string.Join(", ", relations.Distinct());
            }
        }
        CompareStatus = T("Your profile has relations for {0} of the {1} functions with a JoyPro function id (✓ in the tables).", matched, total);
        ApplyCompareCommands(root);
        BuildSheets();
        BuildPages();
    }

    [RelayCommand]
    private void ClearCompare()
    {
        compared.Clear();
        ClearCompareCommands();
        CompareStatus = string.Empty;
        BuildSheets();
        BuildPages();
    }

    [RelayCommand]
    private void UseLayout()
    {
        CockpitVariant? v = Variant;
        if (v == null || v.Aircraft == null)
            return;
        CockpitLayoutRequest request = new(v.Aircraft.Id, v.Id, v.Aircraft.Name, v.Name,
            v.Games.Select(g => g.Plane.Length > 0 ? g.Game + ":" + g.Plane : g.Game).ToList(),
            ["cockpits", "get", v.Aircraft.Id, v.Id, "--json"]);
        UseLayoutRequested?.Invoke(this, request);
        Status = T("The layout of the {0} {1} is ready for the setup wizard.", v.Aircraft.Name, v.Name);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedGame = SelectedEra = SelectedRole = SelectedNation = SelectedKind = All;
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        try
        {
            ExternalProcess.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser: nothing to do.
        }
    }
}
