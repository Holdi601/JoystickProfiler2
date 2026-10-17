using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// What the device reference shows for one device (docs/device-reference.md):
//   pictures, best first: the user's photo > the maker's reference photos (one per view, downloaded on this PC) >
//   JoyPro's drawing of the model (only when it is recognizable) > JoyPro's outline of the device class (never empty);
//   markers only where the position is known: JoyPro's drawing knows every part; on a photo only JoyPro's curated set
//   for exactly that photo content (SHA-256) or what the user placed. A photo without positions shows no marker: the
//   legend lists the inputs and the marker assistant places them (press the button, click its spot).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class DeviceReferenceViewModel : ObservableObject
{
    private readonly Dictionary<string, string> bindings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DeviceReferenceService.LegendText> legendTexts = new(StringComparer.OrdinalIgnoreCase);
    private int version;
    private bool updating;
    private DeviceProbe? lastProbe;

    public DeviceReferenceViewModel()
    {
        vendorPhotosEnabled = false;
    }

    // The drawing style changed (Settings, the Layout page's toggle): JoyPro's drawing is painted again in it.
    public void RepaintDrawing()
    {
        if (LineArtPicture?.Art is DeviceLineArt art)
        {
            LineArtPicture = new Views.Controls.LineArtImage(art, DeviceReferenceService.ScreenDrawingStyle);
            NotifyCanvasPicture();
            PictureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ consent to the maker's photo (per device)

    // JoyPro asks once per device model before it downloads the maker's photo (DevicePhotoConsent): the question shows
    // above the picture while the answer is open; the drawing stays until the user says yes.
    [ObservableProperty] private bool asksPhotoConsent;
    [ObservableProperty] private string photoConsentText = string.Empty;

    // Yes / No for this device, Always for all my devices, Never.
    [RelayCommand]
    private void AnswerPhotoConsent(string? answer)
    {
        if (Model == null || IsGeneric)
            return;
        string a = (answer ?? string.Empty).Trim().ToLowerInvariant();
        DevicePhotoConsent.Answer(Model.Id, a);
        bool allowed = DevicePhotoConsent.Allows(Model.Id);
        AsksPhotoConsent = false;
        if (allowed != VendorPhotosEnabled)
        {
            updating = true;
            VendorPhotosEnabled = allowed;
            updating = false;
        }
        ApplyPhotoConsent(allowed);
    }

    // The shown model's consent state into the switch and the question (no download here).
    private void LoadPhotoConsent()
    {
        PhotoConsentState state = Model == null || IsGeneric ? PhotoConsentState.Denied : DevicePhotoConsent.For(Model.Id);
        VendorPhotosEnabled = state == PhotoConsentState.Allowed;
        DevicePhoto? first = Model == null || IsGeneric ? null : DevicePhotoDb.OfficialPhotos(Model).FirstOrDefault();
        AsksPhotoConsent = state == PhotoConsentState.Ask && first != null;
        PhotoConsentText = AsksPhotoConsent && Model != null ? DevicePhotoConsent.Question(Model.Name, first!.Url) : string.Empty;
    }

    // Downloads the manufacturer's picture (on this PC only) and returns the local file; replaceable for tests.
    public Func<string, Task<string?>> FetchVendorPhoto { get; set; } = DeviceImageCache.LocalFileAsync;

    // The local copy of a maker's photo when it was downloaded before (no download); replaceable for tests.
    public Func<string, string?> CachedVendorPhoto { get; set; } = DeviceImageCache.CachedFile;

    // Decodes a photo for the view; null when it cannot be read (tests without a renderer return null).
    public Func<string, Bitmap?> DecodePhoto { get; set; } = file =>
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 1400);
        }
        catch (Exception)
        {
            return null;
        }
    };

    public ObservableCollection<ReferenceMarker> Markers { get; } = [];
    public ObservableCollection<ReferenceLegendRow> Legend { get; } = [];
    public ObservableCollection<ReferencePictureOption> Pictures { get; } = [];
    public ObservableCollection<RefDevice> GripChoices { get; } = [];

    // The model as shown (a copy: the assistant may add inputs); null before anything is shown.
    [ObservableProperty] private RefDevice? model;
    [ObservableProperty] private RefDevice? baseModel;
    // JoyPro's drawing of the model: it defines the parts (markers and legend) whichever picture is shown.
    [ObservableProperty] private DeviceSchematic? schematic;
    // JoyPro's outline of the device's class (no markers): the last picture source.
    [ObservableProperty] private DeviceSchematic? classSchematic;
    [ObservableProperty] private string deviceClass = string.Empty;
    // The connected device (JoyPro name with GUID) this reference is for; empty for a model picked from the catalog.
    [ObservableProperty] private string deviceName = string.Empty;
    [ObservableProperty] private string title = string.Empty;
    [ObservableProperty] private string subtitle = string.Empty;
    // How the model was found ("USB id 231D:0125 and product name").
    [ObservableProperty] private string matchReason = string.Empty;
    [ObservableProperty] private string status = string.Empty;
    // schematic, class, lineart, vendor or user.
    [ObservableProperty] private string pictureKind = "schematic";
    [ObservableProperty] private Bitmap? photo;
    // JoyPro's line drawing when it is the shown picture (in a photo's frame, positions keyed like the photo's).
    [ObservableProperty] private Views.Controls.LineArtImage? lineArtPicture;
    [ObservableProperty] private string photoFile = string.Empty;
    // "Picture: VKB's web site" under a manufacturer's photo.
    [ObservableProperty] private string photoCredit = string.Empty;
    [ObservableProperty] private ReferencePictureOption? selectedPicture;
    [ObservableProperty] private ReferenceMarker? selectedMarker;
    [ObservableProperty] private ReferenceLegendRow? selectedLegendRow;
    [ObservableProperty] private RefDevice? selectedGrip;
    [ObservableProperty] private bool isEditable = true;
    [ObservableProperty] private bool hasUnsavedChanges;
    // Not in the catalog: a generic picture from what the device reports.
    [ObservableProperty] private bool isGeneric;
    [ObservableProperty] private bool vendorPhotosEnabled;
    [ObservableProperty] private MarkerAssistantViewModel? assistant;
    // How many parts have a known position on the shown picture.
    [ObservableProperty] private int knownMarkerCount;

    public bool HasModel => Model != null;
    public bool HasPhoto => Photo != null;
    public bool ShowsSchematic => PictureKind is "schematic" or "class" || (Photo == null && LineArtPicture == null);

    // The shown picture when it is not a schematic: the photo, or JoyPro's line drawing.
    public Avalonia.Media.IImage? ShownImage => Photo != null ? Photo : LineArtPicture;
    public bool HasGripChoices => GripChoices.Count > 0;
    public bool HasUserPhoto => Pictures.Any(p => p.Kind == "user");

    // The shown picture knows no position of any part (a photo without a position set, the class outline): the page
    // offers the marker assistant instead of markers.
    public bool HasNoKnownPositions => Model != null && Markers.Count > 0 && KnownMarkerCount == 0 && Assistant == null;

    // Width / height of the shown picture.
    public double PictureAspect => !ShowsSchematic && ShownImage is { Size.Height: > 0 } image ? image.Size.Width / image.Size.Height
        : ShownDrawing is { Height: > 0 } drawing ? drawing.Width / drawing.Height : 1;

    private DeviceSchematic? ShownDrawing => PictureKind == "class" ? ClassSchematic : Schematic;

    // Raised when the picture or the markers changed (the view redraws).
    public event EventHandler? PictureChanged;

    // Raised when the user picked a grip for a base (the host keeps the choice, like the HUD's grip setting).
    public event EventHandler<RefDevice>? GripChosen;

    // Raised when the user said what an unknown device is: a catalog model id or "class:<class>" (the host keeps it).
    public event EventHandler<string>? DeviceChosen;

    // ------------------------------------------------------------------ showing a device

    public Task ShowModelAsync(string modelId, string device = "")
    {
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        RefDevice? found = catalog.Find(modelId);
        if (found == null)
        {
            Status = T("'{0}' is not in the device catalog.", modelId);
            return Task.CompletedTask;
        }
        GripChoices.Clear();
        ClearQuestion();
        OnPropertyChanged(nameof(HasGripChoices));
        return ShowAsync(found, catalog.BaseOf(found), device, T("picked from the catalog"), generic: false);
    }

    // A connected device: identified from its USB id, name and inputs. A base with exchangeable grips, a USB id several
    // products share and an unknown device ask once "Which one is it?" (the best fit is shown meanwhile); the answer is
    // passed in as knownModelId next time (pickedGrip), a "class:<class>" answer shows the class outline.
    public Task ShowDeviceAsync(DeviceProbe probe, string device, string? knownModelId = null, bool pickedGrip = false)
    {
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        lastProbe = probe;
        GripChoices.Clear();
        ClearQuestion();
        if (!string.IsNullOrWhiteSpace(knownModelId) && knownModelId.StartsWith(DeviceReferenceService.ClassChoicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(HasGripChoices));
            return ShowClassAsync(knownModelId[DeviceReferenceService.ClassChoicePrefix.Length..], device, T("the kind of device you picked"));
        }
        RefDevice? known = string.IsNullOrWhiteSpace(knownModelId) || knownModelId.StartsWith("generic", StringComparison.OrdinalIgnoreCase) ? null : catalog.Find(knownModelId);
        DeviceMatch match = known != null
            ? new DeviceMatch { Model = known, BaseModel = catalog.BaseOf(known), Method = "engine", Reason = pickedGrip ? T("the one you picked for this device") : T("the grip or model set for this device") }
            : DeviceReferenceService.Identify(probe);
        // The engine's pick is checked like an own identification: a grip it only guessed is asked for.
        DeviceQuestion? question = pickedGrip ? null : DeviceChoices.Question(match, probe, catalog);
        if (known != null && pickedGrip && known.IsGrip)
        {
            foreach (RefDevice grip in catalog.GripsOf(known.Base))
                GripChoices.Add(grip);
        }
        // The grip (or model) choice next to the picture: the candidates, most likely first.
        foreach (RefDevice candidate in (question is { Kind: "grip" or "model" } ? question.Candidates.Select(c => c.Model) : []).Concat(match.Choices))
        {
            if (!GripChoices.Any(g => g.Id == candidate.Id))
                GripChoices.Add(candidate);
        }
        Task task;
        if (question is { Kind: "unknown" })
        {
            // Not known for sure (at most a guess by a part of its name): the outline of its kind, never a picture of
            // another product, until the user says what it is.
            task = ShowClassAsync(question.GuessedClass.Length > 0 ? question.GuessedClass : DeviceClasses.Guess(probe), device, T("not in the device catalog"));
        }
        else if (match.Model != null)
        {
            task = ShowAsync(match.Model, match.BaseModel, device, match.Reason, generic: false);
        }
        else if (question is { Kind: "grip", Base: not null })
        {
            // Nothing tells which grip is on the base: the base (its photo) until the user answers.
            task = ShowAsync(question.Base, null, device, match.Reason, generic: false);
        }
        else if (question is { Candidates.Count: > 0 })
        {
            RefDevice shown = question.Candidates[0].Model;
            task = ShowAsync(shown, match.BaseModel ?? catalog.BaseOf(shown), device, match.Reason, generic: false);
        }
        else
        {
            string cls = question?.GuessedClass is { Length: > 0 } g ? g : DeviceClasses.Guess(probe);
            task = ShowClassAsync(cls, device, T("not in the device catalog"));
        }
        if (question != null)
            SetQuestion(question);
        OnPropertyChanged(nameof(HasGripChoices));
        return task;
    }

    // A device JoyPro does not know, shown as its class: the class outline and a legend of what it reports.
    public Task ShowClassAsync(string cls, string device, string reason)
    {
        string kind = cls switch
        {
            "stick" or "throttle" or "pedals" or "collective" or "yoke" or "quadrant" or "hotas" => cls,
            _ => "panel"
        };
        RefDevice generic = DeviceGeneric.Model(lastProbe ?? new DeviceProbe { InstanceName = device }, kind);
        return ShowAsync(generic, null, device, reason, generic: true, forcedClass: cls);
    }

    public async Task ShowAsync(RefDevice shown, RefDevice? shownBase, string device, string reason, bool generic, string? forcedClass = null)
    {
        int current = ++version;
        updating = true;
        try
        {
            RefDevice copy = shown.Clone();
            // A catalog model that lists no inputs (vJoy, a keyboard's controller interface): the inputs the device
            // reports, so the legend is complete.
            // A base shown while JoyPro asks for its grip: its own axes plus what the device reports.
            // Any other model: the axes the device reports beyond the catalog's (a panel's Slider 1/2), so every axis gets
            // a table to attach. Not its buttons: VKB and Virpil report up to 128 virtual ones.
            if (lastProbe != null && !string.IsNullOrEmpty(device) && (lastProbe.Buttons > 0 || lastProbe.Hats > 0 || lastProbe.Axes is { Count: > 0 }))
            {
                bool everything = !copy.AllControls.Any() || copy.Kind == "base";
                RefDevice reported = DeviceGeneric.Model(lastProbe, copy.Kind is "other" ? "panel" : copy.Kind == "base" ? "stick" : copy.Kind);
                RefDevice? catalogBase = shownBase ?? DeviceReferenceService.Catalog.BaseOf(shown);
                HashSet<string> known = new(copy.AllControls.Concat(catalogBase?.AllControls ?? []).Select(c => c.Input), StringComparer.OrdinalIgnoreCase);
                copy.Axes.AddRange(reported.Axes.Where(a => !known.Contains(a.Input)));
                if (everything)
                    copy.Controls.AddRange(reported.Controls.Where(c => !known.Contains(c.Input)));
            }
            Model = copy;
            BaseModel = shownBase;
            DeviceName = device ?? string.Empty;
            IsGeneric = generic;
            MatchReason = reason ?? string.Empty;
            Title = shown.Name;
            Subtitle = SubtitleOf(copy);
            SelectedGrip = GripChoices.Contains(shown) ? shown : null;
            legendTexts = DeviceReferenceService.LegendTexts(shown.Id);
            Schematic = DeviceReferenceService.Schematic(Model);
            DeviceClass = forcedClass is { Length: > 0 } ? forcedClass : generic && lastProbe != null ? DeviceClasses.Guess(lastProbe) : DeviceClasses.Of(shown);
            ClassSchematic = DeviceClasses.Drawing(DeviceClass, copy.Hand, null);
            Status = string.Empty;
            HasUnsavedChanges = false;
            LoadPhotoConsent();
            BuildPictureOptions();
        }
        finally
        {
            updating = false;
        }
        await SelectPictureAsync(Pictures.First(p => p.Kind is "schematic" or "class"));
        // The best picture, in the order of the list: the user's photo, JoyPro's line drawing with JoyPro's positions,
        // the maker's photo (downloaded on this PC after the user's yes), else the schematic drawing that is already
        // shown; a line drawing without positions only instead of the class outline.
        bool hasSchematic = Pictures.Any(p => p.Kind == "schematic");
        foreach (ReferencePictureOption best in Pictures.Where(p => p.IsPhoto || (p.IsLineArt && (!hasSchematic || DeviceReferenceService.HasCuratedPositions(Model!.Id, p.Key)))).ToList())
        {
            if (current != version)
                return;
            if (await SelectPictureAsync(best, quiet: true))
                break;
        }
    }

    private static string SubtitleOf(RefDevice device)
    {
        List<string> parts = [];
        if (device.Vendor.Length > 0)
            parts.Add(device.Vendor);
        parts.Add(KindName(device.Kind));
        if (device.Hand is "left" or "right")
            parts.Add(device.Hand + " hand");
        if (device.VidPids.Count > 0)
            parts.Add(T("USB {0}{1}", string.Join(", ", device.VidPids.Take(3)), (device.VidPids.Count > 3 ? " ..." : string.Empty)));
        int known = device.AllControls.Count(c => c.Input.Length > 0), all = device.AllControls.Count();
        if (all > 0)
            parts.Add(known == all ? "all " + all + " inputs known" : T("{0} of {1} inputs known", known, all));
        if (device.Origin == "user")
            parts.Add("your definition");
        return string.Join(" · ", parts);
    }

    public static string KindName(string kind) => kind switch
    {
        "stick" => T("Stick"),
        "throttle" => T("Throttle"),
        "hotas" => "HOTAS",
        "base" => T("Base"),
        "pedals" => T("Pedals"),
        "collective" => T("Collective"),
        "yoke" => T("Yoke"),
        "quadrant" => T("Throttle quadrant"),
        "mfd" => "MFD",
        "panel" => T("Panel"),
        "buttonbox" => T("Button box"),
        "controller" => T("Controller board"),
        "keyboard" => T("Keyboard"),
        "gamepad" => T("Gamepad"),
        "wheel" => T("Wheel"),
        _ => T("Device")
    };

    // The pictures of the model, best first: the user's photo, JoyPro's line drawings of the model (shipped, no consent
    // needed; the one with JoyPro's positions first), the maker's reference photos (official hosts only, after the
    // user's consent), JoyPro's schematic drawing when it is recognizable, else JoyPro's outline of the class.
    private void BuildPictureOptions()
    {
        Pictures.Clear();
        if (Model == null)
            return;
        string? user = DeviceReferenceService.UserPhoto(Model.Id);
        if (user != null)
            Pictures.Add(new ReferencePictureOption { Kind = "user", Title = "Your photo", Source = user, Key = DeviceLayoutSet.HashFile(user) });
        // JoyPro's line drawings: the ones that carry JoyPro's positions come right after the user's photo; the others
        // (a picture without positions yet) after the maker's photos and the schematic drawing, before the class outline.
        List<ReferencePictureOption> placed = [], unplaced = [];
        if (!IsGeneric)
        {
            List<(DeviceLineArt Art, string Key)> arts = DeviceReferenceService.LineArtsOf(Model);
            foreach ((DeviceLineArt art, string key) in arts)
            {
                string title = arts.Count == 1 ? T("JoyPro line drawing") : T("Line drawing · {0}", new DevicePhoto { View = art.View }.ViewTitle);
                ReferencePictureOption option = new() { Kind = "lineart", Title = title, View = art.View, LineArt = art, Key = key };
                (DeviceReferenceService.HasCuratedPositions(Model.Id, key) ? placed : unplaced).Add(option);
            }
        }
        foreach (ReferencePictureOption option in placed)
            Pictures.Add(option);
        if (VendorPhotosEnabled && !IsGeneric)
        {
            List<DevicePhoto> photos = DevicePhotoDb.OfficialPhotos(Model);
            foreach (DevicePhoto photo in photos)
            {
                string title = photos.Count == 1 ? T("Photo from {0}", VendorName(Model)) : photo.ViewTitle + " · " + VendorName(Model);
                string? cached = CachedVendorPhoto(photo.Url);
                string key = photo.Sha256.Length > 0 ? photo.Sha256 : cached != null ? DeviceLayoutSet.HashFile(cached) : string.Empty;
                Pictures.Add(new ReferencePictureOption { Kind = "vendor", Title = title, Source = photo.Url, View = photo.View, Photo = photo, Key = key });
            }
        }
        bool schematic = Schematic != null && DeviceClasses.DrawingIsRecognizable(Model, Schematic);
        if (schematic)
            Pictures.Add(new ReferencePictureOption { Kind = "schematic", Title = "JoyPro drawing", Key = "schematic" });
        foreach (ReferencePictureOption option in unplaced)
            Pictures.Add(option);
        if (!schematic)
            Pictures.Add(new ReferencePictureOption { Kind = "class", Title = "Drawing: " + DeviceClasses.Title(DeviceClass), Key = "class" });
        OnPropertyChanged(nameof(HasUserPhoto));
    }

    private static string VendorName(RefDevice device) => device.Vendor.Length > 0 ? device.Vendor : "the manufacturer";

    partial void OnSelectedPictureChanged(ReferencePictureOption? value)
    {
        if (value != null && !updating && !ReferenceEquals(value, loadedPicture))
            _ = SelectPictureAsync(value);
    }

    partial void OnSelectedGripChanged(RefDevice? value)
    {
        if (value == null || updating || Model == null || string.Equals(value.Id, Model.Id, StringComparison.OrdinalIgnoreCase))
            return;
        ClearQuestion();
        _ = ShowAsync(value, DeviceReferenceService.Catalog.BaseOf(value), DeviceName, "grip picked by you", generic: false);
        GripChosen?.Invoke(this, value);
    }

    // The switch next to the picture ("Product photo of this device") is this device's answer: on = yes, off = no.
    partial void OnVendorPhotosEnabledChanged(bool value)
    {
        if (updating)
            return;
        if (Model == null || IsGeneric)
            return;
        DevicePhotoConsent.Answer(Model.Id, value ? "yes" : "no");
        AsksPhotoConsent = false;
        ApplyPhotoConsent(value);
    }

    private void ApplyPhotoConsent(bool value)
    {
        if (Model == null)
            return;
        updating = true;
        try
        {
            BuildPictureOptions();
        }
        finally
        {
            updating = false;
        }
        ReferencePictureOption? next = value ? Pictures.FirstOrDefault(p => p.Kind == "vendor") : PictureKind == "vendor" ? Pictures.First(p => !p.IsPhoto) : null;
        if (next != null)
            _ = SelectPictureAsync(next);
    }

    private ReferencePictureOption? loadedPicture;

    // Shows one of the pictures; quiet = do not report a picture that could not be loaded (automatic choice). False
    // when the picture could not be shown.
    public async Task<bool> SelectPictureAsync(ReferencePictureOption option, bool quiet = false)
    {
        int current = version;
        string file = string.Empty;
        Bitmap? bitmap = null;
        if (option.Kind == "user")
        {
            file = option.Source;
        }
        else if (option.Kind == "vendor")
        {
            string? local = null;
            try
            {
                local = await FetchVendorPhoto(option.Source);
            }
            catch (Exception)
            {
                local = null;
            }
            file = local ?? string.Empty;
        }
        if (option.IsPhoto)
        {
            bitmap = file.Length > 0 && File.Exists(file) ? DecodePhoto(file) : null;
            if (bitmap == null)
            {
                if (!quiet)
                    Status = option.Kind == "vendor" ? T("The manufacturer's picture could not be loaded (offline?). JoyPro's drawing is shown.") : T("Your photo could not be read.");
                return false;
            }
            option.Key = DeviceLayoutSet.HashFile(file);
        }
        Views.Controls.LineArtImage? lineArt = option.IsLineArt && option.LineArt != null ? new Views.Controls.LineArtImage(option.LineArt, DeviceReferenceService.ScreenDrawingStyle) : null;
        if (option.IsLineArt && lineArt == null)
            return false;
        if (current != version)
            return false;
        updating = true;
        try
        {
            PictureKind = option.Kind;
            Photo = bitmap;
            LineArtPicture = lineArt;
            PhotoFile = file;
            PhotoCredit = option.Kind == "vendor" && Model != null ? T("Picture: {0}'s web site (loaded on this PC only)", VendorName(Model)) : string.Empty;
            loadedPicture = option;
            SelectedPicture = option;
        }
        finally
        {
            updating = false;
        }
        RebuildMarkers();
        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(ShowsSchematic));
        OnPropertyChanged(nameof(PictureAspect));
        NotifyCanvasPicture();
        return true;
    }

    // ------------------------------------------------------------------ markers and legend

    // Where the parts are on a picture (0..1), by part id: every part on JoyPro's drawing (its own positions, moved by
    // the user's saved ones); on the class outline and on photos only what is known (saved by the user, or JoyPro's
    // curated set for exactly that photo content).
    private Dictionary<string, double[]> PositionsOn(ReferencePictureOption option, out string source)
    {
        source = string.Empty;
        if (Model == null)
            return new(StringComparer.OrdinalIgnoreCase);
        switch (option.Kind)
        {
            case "schematic":
            {
                Dictionary<string, double[]> positions = new(StringComparer.OrdinalIgnoreCase);
                if (Schematic != null)
                {
                    foreach (SchematicMarker m in Schematic.Markers)
                        positions[m.Id] = [m.X, m.Y];
                }
                foreach (KeyValuePair<string, double[]> saved in DeviceReferenceService.PictureMarkers(Model.Id, "schematic"))
                    positions[saved.Key] = saved.Value;
                source = "JoyPro's drawing";
                return positions;
            }
            case "class":
                source = "your positions";
                return DeviceReferenceService.PictureMarkers(Model.Id, "class");
            default:
            {
                Dictionary<string, double[]> positions = DeviceReferenceService.KnownPositions(Model.Id, option.Key, out source);
                // Positions saved before photos had keys (the user's own placements of this picture kind).
                if (positions.Count == 0 && option.Kind == "user")
                    positions = DeviceReferenceService.PictureMarkers(Model.Id, "user");
                return positions;
            }
        }
    }

    private void RebuildMarkers()
    {
        string? selectedId = SelectedMarker?.Id;
        foreach (ReferenceLegendRow row in Legend)
            row.PropertyChanged -= OnLegendRowChanged;
        foreach (ReferenceMarker old in Markers)
            old.PropertyChanged -= OnMarkerChanged;
        Markers.Clear();
        Legend.Clear();
        if (Model == null || Schematic == null || loadedPicture == null)
        {
            KnownMarkerCount = 0;
            RebuildCanvasMarkers();
            PictureChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        Dictionary<string, double[]> known = PositionsOn(loadedPicture, out string source);
        HashSet<string> userPlaced = loadedPicture.Kind == "schematic"
            ? new HashSet<string>(DeviceReferenceService.PictureMarkers(Model.Id, "schematic").Keys, StringComparer.OrdinalIgnoreCase)
            : loadedPicture.HasPhotoFrame ? new HashSet<string>(DeviceReferenceService.UserLayout(Model.Id, loadedPicture.Key)?.Markers.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(known.Keys, StringComparer.OrdinalIgnoreCase);
        // On a photo or the outline the part's ring is small: the drawing's size does not fit there.
        bool drawn = loadedPicture.Kind == "schematic";
        Dictionary<string, List<string>> views = ViewsOfParts();
        foreach (SchematicMarker m in Schematic.Markers)
        {
            bool isKnown = known.TryGetValue(m.Id, out double[]? xy);
            ReferenceMarker marker = new()
            {
                Id = m.Id,
                Number = m.Number,
                Type = m.Type,
                Role = m.Role,
                Short = m.Short,
                Name = m.Name,
                X = isKnown ? xy![0] : 0.5,
                Y = isKnown ? xy![1] : 0.5,
                Size = drawn ? m.Size : 0.012,
                IsPlaced = isKnown && userPlaced.Contains(m.Id),
                IsShown = isKnown,
                Inputs = m.Inputs.Select(i => new ReferenceMarkerInput
                {
                    Input = i.Input,
                    Control = i.Control,
                    Direction = i.Direction,
                    Name = i.Name,
                    OffsetX = drawn ? i.X - m.X : 0,
                    OffsetY = drawn ? i.Y - m.Y : 0
                }).ToList()
            };
            marker.PropertyChanged += OnMarkerChanged;
            Markers.Add(marker);
            string viewText = views.TryGetValue(m.Id, out List<string>? on) && on.Count > 0 ? string.Join(", ", on) : T("not placed yet");
            foreach (ReferenceMarkerInput input in marker.Inputs.Count > 0 ? marker.Inputs : [new ReferenceMarkerInput { Name = marker.Name }])
            {
                legendTexts.TryGetValue(input.Input.Length > 0 ? input.Input : "#" + marker.Id, out DeviceReferenceService.LegendText? text);
                string bound = input.Input.Length > 0 && bindings.TryGetValue(input.Input, out string? command) ? command : string.Empty;
                ReferenceLegendRow row = new()
                {
                    Marker = marker,
                    Input = input.Input,
                    Control = input.Control,
                    Direction = input.Direction,
                    Name = text != null && text.Name.Length > 0 ? text.Name : input.Name,
                    Command = text != null && text.Text.Length > 0 ? text.Text : bound,
                    IsEdited = text != null && (text.Name.Length > 0 || text.Text.Length > 0),
                    ViewText = viewText
                };
                row.PropertyChanged += OnLegendRowChanged;
                Legend.Add(row);
            }
            marker.Command = string.Join(" / ", Legend.Where(r => r.Marker == marker && r.Command.Length > 0).Select(r => r.Command).Distinct());
        }
        loadedPicture.KnownCount = Markers.Count(m => m.IsShown);
        KnownMarkerCount = loadedPicture.KnownCount;
        Status = PictureStatus(loadedPicture, source);
        RebuildCanvasMarkers();
        SelectedMarker = selectedId == null ? null : Markers.FirstOrDefault(m => m.Id == selectedId);
        OnPropertyChanged(nameof(HasNoKnownPositions));
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    // What the status bar says about the shown picture's markers.
    private string PictureStatus(ReferencePictureOption option, string source)
    {
        if (Model == null || Markers.Count == 0)
            return string.Empty;
        int shown = Markers.Count(m => m.IsShown);
        if (option.Kind == "vendor" && DeviceReferenceService.CuratedPhotoChanged(Model.Id, option.Source, option.Key))
            return T("The maker changed this photo: JoyPro's marker positions were made for the old one and are not shown. Place them again with the marker assistant.");
        if (option.Kind == "schematic")
            return string.Empty;
        if (shown == 0)
            return option.Kind == "class"
                ? T("JoyPro has no picture of this model yet: the legend lists every input. Drop your photo on the picture, or place the markers with the marker assistant.")
                : T("JoyPro knows no positions on this {0} yet. Marker assistant: press a button on the device, then click its spot.", (option.IsLineArt ? "drawing" : "photo"));
        return shown == Markers.Count
            ? T("All {0} parts placed ({1}).", shown, source)
            : T("{0} of {1} parts placed on this picture ({2}); the legend says where the others are.", shown, Markers.Count, source);
    }

    // Per part: the pictures that know its position ("Front", "JoyPro drawing"), for the legend.
    private Dictionary<string, List<string>> ViewsOfParts()
    {
        Dictionary<string, List<string>> views = new(StringComparer.OrdinalIgnoreCase);
        if (Model == null)
            return views;
        foreach (ReferencePictureOption option in Pictures)
        {
            if (option.HasPhotoFrame && option.Key.Length == 0)
                continue;
            string label = option.Kind switch
            {
                "schematic" => "drawing",
                "lineart" => "line drawing",
                "class" => "outline",
                "user" => "your photo",
                _ => option.Photo?.ViewTitle ?? "photo"
            };
            foreach (string id in PositionsOn(option, out _).Keys)
            {
                if (!views.TryGetValue(id, out List<string>? list))
                    views[id] = list = [];
                if (!list.Contains(label))
                    list.Add(label);
            }
        }
        return views;
    }

    private void OnMarkerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReferenceMarker.IsShown))
        {
            KnownMarkerCount = Markers.Count(m => m.IsShown);
            if (loadedPicture != null)
                loadedPicture.KnownCount = KnownMarkerCount;
            RebuildCanvasMarkers();
            OnPropertyChanged(nameof(HasNoKnownPositions));
        }
    }

    private void OnLegendRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (updating || sender is not ReferenceLegendRow row || Model == null)
            return;
        if (e.PropertyName is not (nameof(ReferenceLegendRow.Name) or nameof(ReferenceLegendRow.Command)))
            return;
        string key = row.Input.Length > 0 ? row.Input : "#" + row.Marker.Id;
        string defaultCommand = row.Input.Length > 0 && bindings.TryGetValue(row.Input, out string? bound) ? bound : string.Empty;
        string defaultName = row.Marker.Inputs.FirstOrDefault(i => i.Input == row.Input)?.Name ?? row.Marker.Name;
        DeviceReferenceService.LegendText text = new()
        {
            Name = row.Name == defaultName ? string.Empty : row.Name,
            Text = row.Command == defaultCommand ? string.Empty : row.Command
        };
        legendTexts[key] = text;
        row.IsEdited = text.Name.Length > 0 || text.Text.Length > 0;
        DeviceReferenceService.SaveLegendTexts(Model.Id, legendTexts);
        row.Marker.Command = string.Join(" / ", Legend.Where(r => r.Marker == row.Marker && r.Command.Length > 0).Select(r => r.Command).Distinct());
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    // What each input does in the profile (input -> command text, e.g. "Gear Up (F-16C)"). Rows the user wrote
    // himself keep his text.
    public void SetBindings(IReadOnlyDictionary<string, string> commandsByInput)
    {
        bindings.Clear();
        foreach (KeyValuePair<string, string> pair in commandsByInput)
            bindings[pair.Key] = pair.Value;
        updating = true;
        try
        {
            foreach (ReferenceLegendRow row in Legend)
            {
                if (legendTexts.TryGetValue(row.Input.Length > 0 ? row.Input : "#" + row.Marker.Id, out DeviceReferenceService.LegendText? text) && text.Text.Length > 0)
                    continue;
                row.Command = row.Input.Length > 0 && bindings.TryGetValue(row.Input, out string? command) ? command : string.Empty;
            }
            foreach (ReferenceMarker marker in Markers)
                marker.Command = string.Join(" / ", Legend.Where(r => r.Marker == marker && r.Command.Length > 0).Select(r => r.Command).Distinct());
        }
        finally
        {
            updating = false;
        }
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    public ReferenceMarker? MarkerOf(string input) => Markers.FirstOrDefault(m => m.HasInput(input));

    // A button of the device went down or up (live input): its marker lights up.
    public void SetPressed(string input, bool down)
    {
        ReferenceMarker? marker = MarkerOf(input);
        if (marker == null || marker.IsHighlighted == down)
            return;
        marker.IsHighlighted = down;
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    // Moves a marker (0..1 of the picture), e.g. dragged in the view or placed by the marker assistant: its position
    // on this picture is known from now on.
    public void MoveMarker(ReferenceMarker marker, double x, double y)
    {
        marker.X = Math.Clamp(x, 0, 1);
        marker.Y = Math.Clamp(y, 0, 1);
        marker.IsPlaced = true;
        marker.IsShown = true;
        HasUnsavedChanges = true;
        foreach (ReferenceLegendRow row in Legend.Where(r => r.Marker == marker && r.ViewText == T("not placed yet")))
            row.ViewText = loadedPicture?.Title ?? "this picture";
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedMarkerChanged(ReferenceMarker? oldValue, ReferenceMarker? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        foreach (ReferenceLegendRow row in Legend)
            row.IsSelected = newValue != null && row.Marker == newValue;
        if (newValue != null && (SelectedLegendRow == null || SelectedLegendRow.Marker != newValue))
            SelectedLegendRow = Legend.FirstOrDefault(r => r.Marker == newValue);
        SyncCanvasSelection(newValue);
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedLegendRowChanged(ReferenceLegendRow? value)
    {
        if (value != null && SelectedMarker != value.Marker)
            SelectedMarker = value.Marker;
    }

    // Keeps the positions the user placed on the shown picture: on a photo under its content hash (another photo, or
    // the maker's new version of it, does not get them).
    [RelayCommand]
    private void SaveMarkers()
    {
        if (Model == null || loadedPicture == null)
            return;
        SavePlacedMarkersFor(Model.Id);
        HasUnsavedChanges = false;
        Status = T("Markers saved for {0}.", (loadedPicture.Title.Length > 0 ? loadedPicture.Title : "this picture"));
    }

    // Saves the placed parts of the shown picture under a model id (the assistant also saves them under a new user
    // definition's id).
    public void SavePlacedMarkersFor(string modelId)
    {
        if (loadedPicture == null)
            return;
        Dictionary<string, double[]> placed = Markers.Where(m => m.IsPlaced).ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);
        if (loadedPicture.HasPhotoFrame)
        {
            if (loadedPicture.Key.Length == 0)
                return;
            DeviceReferenceService.SaveUserLayout(new DeviceLayoutSet
            {
                Model = modelId,
                PhotoKind = loadedPicture.Kind,
                PhotoUrl = loadedPicture.Kind == "vendor" ? loadedPicture.Source : string.Empty,
                PhotoSha256 = loadedPicture.Key,
                View = loadedPicture.View,
                Markers = placed
            });
        }
        else
        {
            DeviceReferenceService.SavePictureMarkers(modelId, loadedPicture.Kind, placed);
        }
    }

    [RelayCommand]
    private void ResetMarkers()
    {
        if (Model == null || loadedPicture == null)
            return;
        if (loadedPicture.HasPhotoFrame)
            DeviceReferenceService.RemoveUserLayout(Model.Id, loadedPicture.Key);
        else
            DeviceReferenceService.RemovePictureMarkers(Model.Id, loadedPicture.Kind);
        HasUnsavedChanges = false;
        RebuildMarkers();
        Status = loadedPicture.IsPhoto ? T("Your positions on this photo are removed.")
            : loadedPicture.IsLineArt ? T("Your positions on this drawing are removed.") : T("Markers back where JoyPro puts them.");
    }

    // The user's own photo of the model (file picker or drag and drop): copied into JoyPro's folder and shown.
    public async Task SetUserPhotoAsync(string file)
    {
        if (Model == null || !File.Exists(file))
            return;
        DeviceReferenceService.SetUserPhoto(Model.Id, file);
        updating = true;
        try
        {
            BuildPictureOptions();
        }
        finally
        {
            updating = false;
        }
        ReferencePictureOption option = Pictures.First(p => p.Kind == "user");
        await SelectPictureAsync(option);
        if (PictureKind == "user" && KnownMarkerCount == 0)
            Status = T("Your photo is shown. Marker assistant: press a button on the device, then click its spot on the photo.");
    }

    [RelayCommand]
    private async Task RemoveUserPhotoAsync()
    {
        if (Model == null)
            return;
        DeviceReferenceService.RemoveUserPhoto(Model.Id);
        updating = true;
        try
        {
            BuildPictureOptions();
        }
        finally
        {
            updating = false;
        }
        foreach (ReferencePictureOption option in Pictures.Where(p => p.Kind == "vendor").ToList())
        {
            if (await SelectPictureAsync(option, quiet: true))
                return;
        }
        await SelectPictureAsync(Pictures.First(p => !p.IsPhoto));
    }

    // ------------------------------------------------------------------ sharing a layout (joypro-device-layout)

    // The shown picture's positions as a shareable layout: model, the photo's address and hash (the user's own photo
    // itself inside), the parts with a known position.
    // The tables and lines of the shown picture (the Layout page's callout sheet); shared with the positions.
    public Func<CalloutSheet?>? CurrentCallouts { get; set; }

    // The picture the markers are shown on (its kind and content key).
    public ReferencePictureOption? LoadedPicture => loadedPicture;

    public DeviceLayoutSet? CurrentLayoutSet()
    {
        if (Model == null || loadedPicture == null)
            return null;
        DeviceLayoutSet set = new()
        {
            Model = Model.Id,
            PhotoKind = loadedPicture.HasPhotoFrame ? loadedPicture.Kind : "drawing",
            PhotoUrl = loadedPicture.Kind == "vendor" ? loadedPicture.Source : string.Empty,
            PhotoSha256 = loadedPicture.HasPhotoFrame ? loadedPicture.Key : string.Empty,
            View = loadedPicture.View,
            Markers = Markers.Where(m => m.IsShown).ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase),
            Source = "shared from JoyPro",
            Callouts = CurrentCallouts?.Invoke()
        };
        if (loadedPicture.Kind == "user" && File.Exists(PhotoFile))
        {
            set.PhotoFileBase64 = Convert.ToBase64String(File.ReadAllBytes(PhotoFile));
            set.PhotoFileName = Path.GetFileName(PhotoFile);
        }
        return set;
    }

    public void ExportLayout(string file)
    {
        DeviceLayoutSet? set = CurrentLayoutSet();
        if (set == null)
            return;
        File.WriteAllText(file, set.ToJson(), new UTF8Encoding(false));
        Status = T("Layout exported to {0}{1}", file, (set.PhotoKind == "vendor" ? T(" (the photo's address only; the photo itself is the maker's).") : "."));
    }

    // A shared layout: the positions are kept for its model and photo; a photo of the user inside becomes his photo.
    public async Task<bool> ImportLayoutAsync(string file)
    {
        DeviceLayoutSet set;
        try
        {
            set = DeviceLayoutSet.Parse(File.ReadAllText(file, Encoding.UTF8), file);
        }
        catch (Exception ex)
        {
            Status = T("Not a JoyPro layout: {0}", ex.Message);
            return false;
        }
        if (set.PhotoKind == "user" && set.PhotoFileBase64.Length > 0)
        {
            string temp = Path.Combine(Path.GetTempPath(), "JoyPro2-import-" + Guid.NewGuid().ToString("N") + Path.GetExtension(set.PhotoFileName is { Length: > 0 } n ? n : ".png"));
            File.WriteAllBytes(temp, Convert.FromBase64String(set.PhotoFileBase64));
            string stored = DeviceReferenceService.SetUserPhoto(set.Model, temp);
            File.Delete(temp);
            set.PhotoSha256 = DeviceLayoutSet.HashFile(stored);
        }
        if (set.PhotoKind == "drawing")
            DeviceReferenceService.SavePictureMarkers(set.Model, "schematic", set.Markers);
        else
            DeviceReferenceService.SaveUserLayout(set);
        // Tables and lines of the shared layout: the sheet of that picture from now on.
        if (set.Callouts != null)
        {
            string key = set.PhotoKind == "drawing" ? "schematic" : set.PhotoSha256;
            if (key.Length > 0)
                DeviceReferenceService.SaveCallouts(set, key);
        }
        if (Model != null && string.Equals(Model.Id, set.Model, StringComparison.OrdinalIgnoreCase))
        {
            updating = true;
            try
            {
                BuildPictureOptions();
            }
            finally
            {
                updating = false;
            }
            ReferencePictureOption? target = Pictures.FirstOrDefault(p => p.HasPhotoFrame && string.Equals(p.Key, set.PhotoSha256, StringComparison.OrdinalIgnoreCase))
                                             ?? Pictures.FirstOrDefault(p => p.Kind == "vendor" && string.Equals(p.Source, set.PhotoUrl, StringComparison.OrdinalIgnoreCase));
            if (target != null)
                await SelectPictureAsync(target);
            else
                RebuildMarkers();
        }
        Status = T("{0} marker position(s) imported for {1}.", set.Markers.Count, set.Model);
        return true;
    }

    // ------------------------------------------------------------------ the device as a definition

    // The shown model with the user's names and (on the schematic) marker positions: what is saved as the user's own
    // definition and shared as JSON.
    public RefDevice? CurrentDefinition()
    {
        if (Model == null)
            return null;
        RefDevice definition = Model.Clone();
        if (IsGeneric || definition.Id.StartsWith("generic", StringComparison.OrdinalIgnoreCase))
            definition.Id = "user-" + DeviceVocabulary.Slug(definition.Name);
        definition.Origin = "user";
        definition.Vendor = definition.Vendor.Length > 0 ? definition.Vendor : T("User");
        foreach (RefControl control in definition.AllControls)
        {
            ReferenceLegendRow? row = control.Input.Length > 0 ? Legend.FirstOrDefault(r => string.Equals(r.Input, control.Input, StringComparison.OrdinalIgnoreCase)) : null;
            if (row != null && row.IsEdited && legendTexts.TryGetValue(row.Input, out DeviceReferenceService.LegendText? text) && text.Name.Length > 0)
                control.Label = text.Name;
        }
        if (PictureKind == "schematic")
        {
            foreach (ReferenceMarker marker in Markers.Where(m => m.IsPlaced))
                definition.Markers[marker.Id] = [Math.Round(marker.X, 4), Math.Round(marker.Y, 4)];
        }
        if (definition.Sources.Count == 0)
            definition.Sources.Add(T("JoyPro marker assistant"));
        return definition;
    }

    [RelayCommand]
    private void SaveDefinition()
    {
        RefDevice? definition = CurrentDefinition();
        if (definition == null)
            return;
        string file = DeviceReferenceService.SaveUserDefinition(definition);
        Status = T("Saved as your device definition: {0}", file);
    }

    public void ExportDefinition(string file)
    {
        RefDevice? definition = CurrentDefinition();
        if (definition == null)
            return;
        File.WriteAllText(file, DeviceReferenceService.DefinitionJson(definition), new UTF8Encoding(false));
        Status = T("Exported to {0}. Share it or send it to JoyPro to add the device to the catalog.", file);
    }

    public async Task<int> ImportDefinitionsAsync(string file)
    {
        List<RefDevice> devices = DeviceReferenceService.ImportDefinitions(file);
        if (devices.Count > 0)
            await ShowModelAsync(devices[0].Id, DeviceName);
        Status = T("{0} device definition(s) imported.", devices.Count);
        return devices.Count;
    }

    // Adds a control the catalog did not know (the marker assistant found an input that has no part yet) and draws the
    // device again. Returns the new marker.
    public ReferenceMarker? AddControl(RefControl control)
    {
        if (Model == null || control.Input.Length > 0 && Model.AllControls.Any(c => string.Equals(c.Input, control.Input, StringComparison.OrdinalIgnoreCase)))
            return MarkerOf(control.Input);
        if (control.Type == "analog")
            Model.Axes.Add(control);
        else
            Model.Controls.Add(control);
        RedrawKeepingPlaced();
        return MarkerOf(control.Input);
    }

    // Gives a known part that has no number yet its input (the assistant: "press the TMS hat up").
    public bool AssignInput(ReferenceMarker marker, string input, string direction)
    {
        if (Model == null)
            return false;
        RefControl? control = Model.AllControls.FirstOrDefault(c => c.Input.Length == 0 && string.Equals(DeviceVocabulary.ElementOf(c.Control), marker.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                  (direction.Length == 0 || c.Control.EndsWith("." + direction, StringComparison.OrdinalIgnoreCase)))
                               ?? Model.AllControls.FirstOrDefault(c => c.Input.Length == 0 && string.Equals(DeviceVocabulary.ElementOf(c.Control), marker.Id, StringComparison.OrdinalIgnoreCase));
        if (control == null)
            return false;
        control.Input = input.ToUpperInvariant();
        RedrawKeepingPlaced();
        return true;
    }

    private void RedrawKeepingPlaced()
    {
        if (Model == null)
            return;
        Dictionary<string, double[]> placed = Markers.Where(m => m.IsPlaced).ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);
        string? selected = SelectedMarker?.Id;
        Schematic = DeviceReferenceService.Schematic(Model);
        RebuildMarkers();
        foreach (ReferenceMarker marker in Markers)
        {
            if (placed.TryGetValue(marker.Id, out double[]? xy))
            {
                marker.X = xy[0];
                marker.Y = xy[1];
                marker.IsPlaced = true;
                marker.IsShown = true;
            }
        }
        if (selected != null)
            SelectedMarker = Markers.FirstOrDefault(m => m.Id == selected);
        Subtitle = SubtitleOf(Model);
        HasUnsavedChanges = true;
        PictureChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ the marker assistant

    // The input capture the assistant listens to (set by the host; none = no assistant).
    public IJoystickCaptureService? CaptureService { get; set; }

    public bool HasAssistant => Assistant != null;

    partial void OnAssistantChanged(MarkerAssistantViewModel? value)
    {
        OnPropertyChanged(nameof(HasAssistant));
        OnPropertyChanged(nameof(HasNoKnownPositions));
    }

    [RelayCommand]
    private void OpenAssistant()
    {
        if (CaptureService == null || Model == null)
        {
            Status = Model == null ? T("Pick or connect a device first.") : T("The marker assistant needs the input reader of the main window.");
            return;
        }
        if (Assistant == null)
            _ = new MarkerAssistantViewModel(this, CaptureService);
    }

    [RelayCommand]
    private void CloseAssistant()
    {
        Assistant?.StopCommand.Execute(null);
        Assistant = null;
    }

    partial void OnPhotoChanged(Bitmap? value)
    {
        NotifyCanvasPicture();
        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(ShowsSchematic));
        OnPropertyChanged(nameof(PictureAspect));
    }

    partial void OnModelChanged(RefDevice? value) => OnPropertyChanged(nameof(HasModel));

    partial void OnSchematicChanged(DeviceSchematic? value) => NotifyCanvasPicture();

    partial void OnClassSchematicChanged(DeviceSchematic? value) => NotifyCanvasPicture();

    partial void OnPictureKindChanged(string value)
    {
        OnPropertyChanged(nameof(ShowsSchematic));
        NotifyCanvasPicture();
    }
}
