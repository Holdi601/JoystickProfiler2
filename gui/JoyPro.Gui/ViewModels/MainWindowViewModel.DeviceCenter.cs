using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// One connected device as engine hud devices reports it (DirectInput name with GUID, HID name, VID:PID, the engine's
// model, the layout picture).
public sealed class HudDeviceInfo
{
    public string Device { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string VidPid { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public bool Generic { get; init; }
    public List<string> Axes { get; init; } = [];
    public int Buttons { get; init; }
    public int Hats { get; init; }
    public int GripChoices { get; init; }
    // The layout picture (PNG) and whether it is the user's own layout (the Layout page shows it then).
    public string LayoutPicture { get; init; } = string.Empty;
    public bool LayoutOwn { get; init; }
}

// Where a card's picture comes from, in the Layout page's order: the user's own layout, his photo of the model, the
// maker's photo (when "Manufacturer photos" is on), else JoyPro's drawing (set on the card at once).
public sealed class DevicePicturePlan
{
    public string LayoutPicture { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string VendorPhoto { get; init; } = string.Empty;
    // JoyPro's line drawing of the model (shipped): shown before the maker's photo, no consent needed.
    public DeviceLineArt? LineArt { get; init; }
}

// The device cards of the Start page ("Your devices") and Settings > Devices: every real connected game device (one
// card per physical device, with the device reference's model, kind and picture), vJoy devices marked as virtual, and
// the devices the profile uses but that are not connected (engine hud devices + devices list --connected
// --profile-devices). Driver entries without a device ("vJoy Driver", "Microsoft PC-joystick driver") get no card;
// keyboards and mice with game-controller interfaces (Wooting) are one line unless the profile uses them. A click on a
// card opens the Layout page with the device. "Move binds to..." and "Remove from profile" on a card fill the pickers of
// engine devices exchange / remove, which ask before they change the profile.
public sealed partial class MainWindowViewModel
{
    // Driver names Windows lists as game controllers (no device of their own).
    private static readonly HashSet<string> DriverDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HID-compliant game controller", "Microsoft PC-joystick driver", "vJoy Driver"
    };

    private List<HudDeviceInfo> lastHudDevices = [];

    public ObservableCollection<DeviceCardItem> DeviceCards { get; } = [];

    // How JoyPro's own drawings are painted (Settings > Drawings, the Layout page's Lines / Cel-shaded toggle) and in
    // exports and cheat sheets (the export options; the engine gets them as --drawing-style).
    private DrawingStyleOptions? screenDrawing;
    private DrawingStyleOptions? exportDrawing;

    public DrawingStyleOptions ScreenDrawing
    {
        get
        {
            if (screenDrawing == null)
            {
                screenDrawing = new DrawingStyleOptions(() => DeviceReferenceService.ScreenDrawingStyle, style => DeviceReferenceService.ScreenDrawingStyle = style);
                screenDrawing.Changed += (_, _) => RepaintDrawings();
            }
            return screenDrawing;
        }
    }

    public DrawingStyleOptions ExportDrawing =>
        exportDrawing ??= new DrawingStyleOptions(() => DeviceReferenceService.ExportDrawingStyle, style => DeviceReferenceService.ExportDrawingStyle = style);

    // Every shown picture of JoyPro's drawings in the new style: the Layout page's picture and the device cards.
    private void RepaintDrawings()
    {
        deviceReference?.RepaintDrawing();
        DrawingStyle style = DeviceReferenceService.ScreenDrawingStyle;
        foreach (DeviceCardItem card in DeviceCards.Concat(DashboardDevices).Distinct())
        {
            if (card.Picture is Views.Controls.LineArtImage drawing)
                card.Picture = new Views.Controls.LineArtImage(drawing.Art, style);
        }
    }

    public bool HasDeviceCards => DeviceCards.Count > 0;

    // Connected devices without a card of their own: "Wooting Two HE (ARM) (5 interfaces)".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherDevices))]
    private string otherDevicesText = string.Empty;

    public bool HasOtherDevices => OtherDevicesText.Length > 0;

    [RelayCommand]
    private async Task LoadDeviceCardsAsync()
    {
        await RefreshDevicesAsync();
        List<string> args = HudDevicesArguments();
        // The grip set for a base (Layout page or HUD designer): the engine names the model and its inputs with it.
        Dictionary<string, string> hudGrips = HudGrips();
        foreach (string device in ConnectedDevices.Where(d => d.Contains('{')))
        {
            if (ConfiguredGripOf(device, hudGrips) is { Length: > 0 } grip)
                args.AddRange(["--grip", device + "=" + grip]);
        }
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: N("Looking at your devices..."), forceText: false);
        // No answer (the engine is busy with something else): the cards stay as they are.
        if (result == null && lastHudDevices.Count > 0)
            return;
        lastHudDevices = result != null && result.ExitCode == 0 ? ParseHudDeviceInfos(result.StandardOutput) : [];
        await RebuildDeviceCardsAsync();
    }

    // The cards again from the last engine answer (after a grip was picked, the profile changed).
    private async Task RebuildDeviceCardsAsync(bool announce = true)
    {
        DeviceCatalog catalog;
        try
        {
            DeviceReferenceService.EnginePath = EnginePath ?? string.Empty;
            catalog = DeviceReferenceService.Catalog;
        }
        catch (Exception)
        {
            catalog = new DeviceCatalog();
        }
        Dictionary<string, string> hudGrips = HudGrips();
        DeviceCardSet set = BuildDeviceCards(lastHudDevices, ProfileDevices.ToList(), device => ConfiguredGripOf(device, hudGrips), catalog);

        DeviceCards.Clear();
        foreach (DeviceCardItem card in set.Cards)
            DeviceCards.Add(card);
        OtherDevicesText = set.Others;
        OnPropertyChanged(nameof(HasDeviceCards));
        if (announce)
        {
            StatusText = DeviceCards.Count == 0
                ? T("No devices found. Connect your devices and press Look again.")
                : T("{0} device(s) connected, {1} used in your profile.", DeviceCards.Count(c => c.Connected), DeviceCards.Count(c => c.InProfile));
        }
        // The Start page's "Your devices" at once (not only after the queued refresh of the UI thread).
        RebuildDashboard();
        // The Binds page's hand pick lists the device cards.
        RebuildBindPickOptions();
        // The 3D views of the connected devices (downloaded packs, not shipped): in the background.
        _ = CheckDevice3DAsync(DeviceCards.Where(c => c.Connected).Select(c => c.ModelId).ToList());
        await LoadDeviceCardPicturesAsync(set.Pictures);
    }

    internal static List<HudDeviceInfo> ParseHudDeviceInfos(string output)
    {
        List<HudDeviceInfo> found = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (!doc.RootElement.TryGetProperty("Devices", out JsonElement devices) || devices.ValueKind != JsonValueKind.Array)
                return found;
            foreach (JsonElement item in devices.EnumerateArray())
            {
                string device = ReadJsonString(item, "Device");
                if (device.Length == 0)
                    continue;
                List<string> axes = [];
                if (item.TryGetProperty("Axes", out JsonElement axisList) && axisList.ValueKind == JsonValueKind.Array)
                    axes.AddRange(axisList.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Object ? ReadJsonString(a, "Input") : string.Empty));
                bool hasLayout = item.TryGetProperty("Layout", out JsonElement layout) && layout.ValueKind == JsonValueKind.Object;
                found.Add(new HudDeviceInfo
                {
                    Device = device,
                    Name = ReadJsonString(item, "Name"),
                    VidPid = ReadJsonString(item, "VidPid"),
                    Model = ReadJsonString(item, "Model"),
                    ModelId = ReadJsonString(item, "ModelId"),
                    Kind = ReadJsonString(item, "Kind"),
                    Generic = ReadJsonBool(item, "Generic"),
                    Axes = axes,
                    Buttons = ReadJsonInt(item, "Buttons"),
                    Hats = ReadJsonInt(item, "Hats"),
                    GripChoices = item.TryGetProperty("GripChoices", out JsonElement grips) && grips.ValueKind == JsonValueKind.Array ? grips.GetArrayLength() : 0,
                    LayoutPicture = hasLayout ? ReadJsonString(layout, "Picture") : string.Empty,
                    LayoutOwn = hasLayout && ReadJsonBool(layout, "Own")
                });
            }
        }
        catch (JsonException)
        {
        }
        return found;
    }

    public sealed class DeviceCardSet
    {
        public List<DeviceCardItem> Cards { get; } = [];
        public List<(DeviceCardItem Card, DevicePicturePlan Plan)> Pictures { get; } = [];
        public string Others { get; set; } = string.Empty;
    }

    // A driver entry or a connected name without a device (no instance GUID: "vJoy Device" next to "vJoy Device {...}").
    internal static bool IsPseudoDevice(string device)
    {
        string text = (device ?? string.Empty).Trim();
        return !HasInstanceGuid(text) || DriverDeviceNames.Contains(DeviceProbe.RemoveGuid(text));
    }

    private static bool HasInstanceGuid(string device) => device.EndsWith('}') && device.Contains(" {", StringComparison.Ordinal);

    internal static bool IsVirtualDevice(HudDeviceInfo info) =>
        string.Equals(info.VidPid, "1234:BEAD", StringComparison.OrdinalIgnoreCase) ||
        info.Device.Contains("vJoy", StringComparison.OrdinalIgnoreCase) || info.Name.Contains("vJoy", StringComparison.OrdinalIgnoreCase);

    // The cards for the connected devices (engine hud devices) and the profile's devices that are not connected; the
    // model of each from the device reference (a grip picked for a base first, then the engine's model, then the
    // reference's own identification by VID:PID, HID name and inputs).
    internal static DeviceCardSet BuildDeviceCards(IReadOnlyList<HudDeviceInfo> connected, IReadOnlyList<string> profileDevices, Func<string, string?> gripOf, DeviceCatalog catalog)
    {
        DeviceCardSet set = new();
        bool InProfile(string device) => profileDevices.Any(d => SameDevice(d, device));
        List<(DeviceCardItem Card, DevicePicturePlan Plan, int Rank)> cards = [];
        List<HudDeviceInfo> others = [];
        foreach (HudDeviceInfo info in connected)
        {
            if (IsPseudoDevice(info.Device))
                continue;
            // The same device twice (the engine lists each instance once; a second answer line would be a duplicate).
            if (cards.Any(c => string.Equals(c.Card.Device, info.Device, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool inProfile = InProfile(info.Device);
            bool isVirtual = IsVirtualDevice(info);
            DeviceProbe probe = new()
            {
                VidPid = info.VidPid,
                ProductName = info.Name,
                InstanceName = info.Device,
                Buttons = info.Buttons,
                Hats = info.Hats,
                Axes = info.Axes.Where(a => a.Length > 0).ToList()
            };

            // The same identification as the Layout page: the user's answer to "Which one is it?" (a grip, a model or a
            // kind of device), else the engine's model, else the reference's identification; a guessed grip (a base
            // that does not say which grip is on it) shows the most likely one and asks.
            RefDevice? model = null;
            RefDevice? baseOnly = null;
            bool needsAnswer = false;
            string chosenClass = string.Empty;
            string? answer = gripOf(info.Device);
            if (answer is { Length: > 0 } && answer.StartsWith(DeviceReferenceService.ClassChoicePrefix, StringComparison.OrdinalIgnoreCase))
                chosenClass = answer[DeviceReferenceService.ClassChoicePrefix.Length..];
            else if (answer is { Length: > 0 })
                model = catalog.Find(answer);
            if (model == null && chosenClass.Length == 0)
            {
                DeviceMatch match;
                try
                {
                    match = DeviceIdentifier.Identify(catalog, probe);
                    if (match.Model == null && info.ModelId.Length > 0 && !info.Generic && !info.ModelId.StartsWith("generic", StringComparison.OrdinalIgnoreCase) && catalog.Find(info.ModelId) is { } engineModel)
                        match = new DeviceMatch { Model = engineModel, BaseModel = catalog.BaseOf(engineModel), Method = "engine" };
                }
                catch (Exception)
                {
                    match = new DeviceMatch();
                }
                DeviceQuestion? question = isVirtual ? null : DeviceChoices.Question(match, probe, catalog);
                if (match.Model != null)
                    model = match.Model;
                else if (question is { Candidates.Count: > 0 })
                    model = question.Candidates[0].Model;
                if (question is { Kind: "grip" or "model" })
                {
                    needsAnswer = true;
                    baseOnly = question.Base;
                }
                else if (question is { Kind: "unknown" })
                {
                    // At most a guess by a part of the name: the outline of its kind, not another product's picture.
                    chosenClass = question.GuessedClass;
                    model = null;
                }
            }
            // A keyboard's or mouse's controller interface: a device of its own only when it reports game inputs
            // (the Wooting's gamepad mode); its other interfaces are no device.
            string modelClass = model == null ? string.Empty : DeviceClasses.Of(model);
            if (model is { Kind: "other" } && !(modelClass is "keyboard" or "virtual" or "gamepad" && (info.Axes.Count > 0 || info.Hats > 0)))
                model = null;
            modelClass = model == null ? string.Empty : DeviceClasses.Of(model);
            if (isVirtual)
                chosenClass = "virtual";
            // A base whose grip is not known shows the base and asks "Which grip?" (the Layout page asks with photos).
            RefDevice? shown = needsAnswer && baseOnly != null ? baseOnly : model;
            string kind = shown?.Kind is { Length: > 0 } k && k != "other" ? k : chosenClass is "keyboard" or "gamepad" or "wheel" ? chosenClass
                : modelClass is "keyboard" or "gamepad" ? modelClass : info.Kind.Length > 0 ? info.Kind : "other";

            // Not a game device JoyPro knows (a keyboard's or mouse's controller interface) and not used: one line.
            if (shown == null && !isVirtual && kind == "other" && chosenClass.Length == 0 && !inProfile)
            {
                others.Add(info);
                continue;
            }

            bool ffb = ContainsFfb(info.Name) || ContainsFfb(info.Device) || ContainsFfb(shown?.Name) || ContainsFfb(shown == null ? null : catalog.BaseOf(shown)?.Name);
            string kindLabel = isVirtual ? T("Virtual")
                : kind == "base" ? (ffb ? T("FFB base") : T("Base"))
                : kind == "stick" && ffb ? T("FFB stick")
                : kind == "other" ? string.Empty
                // The engine's guess for an unknown "... Controller" / gamepad (the catalog's "controller" is a board).
                : kind == "controller" && shown == null ? T("Gamepad")
                : DeviceReferenceViewModel.KindName(kind);
            DeviceCardItem card = new(info.Device, info.Name, info.Model, kind, info.Axes.Count, info.Buttons, info.Hats, connected: true, inProfile: inProfile)
            {
                ModelName = shown?.Name ?? string.Empty,
                ModelId = shown?.Id ?? string.Empty,
                KindLabel = kindLabel.Length > 0 ? kindLabel : chosenClass.Length > 0 ? DeviceClasses.Title(chosenClass) : modelClass == "keyboard" ? "Keyboard" : string.Empty,
                IsVirtual = isVirtual,
                NeedsGrip = needsAnswer
            };

            // A drawing at once, like the Layout page: JoyPro's drawing of the model when it is recognizable, else the
            // outline of its class (never a made-up layout of parts).
            try
            {
                string cls = chosenClass.Length > 0 ? chosenClass : shown != null ? DeviceClasses.Of(shown) : DeviceClasses.Guess(probe);
                DeviceSchematic? drawing = null;
                if (shown != null && chosenClass.Length == 0)
                {
                    DeviceSchematic built = SchematicBuilder.Build(shown, catalog.BaseOf(shown));
                    if (DeviceClasses.DrawingIsRecognizable(shown, built))
                        drawing = built;
                }
                card.Schematic = drawing ?? DeviceClasses.Drawing(cls, shown?.Hand ?? DeviceIdentifier.HandFromName(info.Name + " " + info.Device), null);
                card.PictureSource = drawing != null ? T("JoyPro drawing") : T("Drawing: {0}", DeviceClasses.Title(cls));
            }
            catch (Exception)
            {
                card.Schematic = null;
            }
            DeviceLineArt? lineArt = null;
            try
            {
                // Like the Layout page: a drawing with JoyPro's positions first; one without only instead of the
                // class outline (when the schematic drawing is not recognizable).
                List<(DeviceLineArt Art, string Key)> arts = shown != null && chosenClass.Length == 0 ? DeviceReferenceService.LineArtsOf(shown) : [];
                lineArt = arts.Where(a => DeviceReferenceService.HasCuratedPositions(shown!.Id, a.Key)).Select(a => a.Art).FirstOrDefault()
                          ?? (card.PictureSource == T("JoyPro drawing") ? null : arts.Select(a => a.Art).FirstOrDefault());
            }
            catch (Exception)
            {
                lineArt = null;
            }
            if (lineArt != null)
            {
                card.Picture = new Views.Controls.LineArtImage(lineArt, DeviceReferenceService.ScreenDrawingStyle);
                card.PictureSource = T("JoyPro line drawing");
            }
            DevicePicturePlan plan = new()
            {
                LayoutPicture = info.LayoutOwn ? info.LayoutPicture : string.Empty,
                ModelId = shown?.Id ?? string.Empty,
                VendorPhoto = shown == null ? string.Empty : DevicePhotoDb.OfficialPhotos(shown).FirstOrDefault()?.Url ?? string.Empty,
                LineArt = lineArt
            };
            int rank = isVirtual ? 20 : KindRank(kind);
            cards.Add((card, plan, rank));
        }

        foreach ((DeviceCardItem card, DevicePicturePlan plan, int _) in cards.OrderBy(c => c.Rank).ThenBy(c => c.Card.Title, StringComparer.OrdinalIgnoreCase))
        {
            set.Cards.Add(card);
            set.Pictures.Add((card, plan));
        }

        // Devices of the profile that are not connected now (a driver entry of the profile is no device either).
        foreach (string device in profileDevices)
        {
            string text = (device ?? string.Empty).Trim();
            if (text.Length == 0 || DriverDeviceNames.Contains(DeviceProbe.RemoveGuid(text)))
                continue;
            if (set.Cards.Any(c => SameDevice(c.Device, text)) || connected.Any(c => SameDevice(c.Device, text)))
                continue;
            // A name without GUID of a device that has a card already.
            if (!HasInstanceGuid(text) && connected.Any(c => string.Equals(DeviceProbe.RemoveGuid(c.Device), text, StringComparison.OrdinalIgnoreCase)))
                continue;
            set.Cards.Add(new DeviceCardItem(text, text, string.Empty, string.Empty, 0, 0, 0, connected: false, inProfile: true));
        }

        set.Others = string.Join(", ", others
            .GroupBy(o => DeviceProbe.RemoveGuid(o.Name.Length > 0 ? o.Name : o.Device), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key + (g.Count() > 1 ? T(" ({0} interfaces)", g.Count()) : string.Empty)));
        return set;
    }

    private static bool ContainsFfb(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains("FFB", StringComparison.OrdinalIgnoreCase) || text.Contains("force feedback", StringComparison.OrdinalIgnoreCase));

    // Sticks first, then throttles, pedals, collectives, panels; unknown last.
    private static int KindRank(string kind) => kind switch
    {
        "stick" or "base" or "hotas" or "yoke" => 0,
        "throttle" or "quadrant" => 1,
        "pedals" => 2,
        "collective" => 3,
        "panel" or "mfd" or "buttonbox" or "controller" => 4,
        _ => 10
    };

    // engine hud devices; with the photo mode "never" the engine leaves the makers' photo addresses out.
    internal static List<string> HudDevicesArguments()
    {
        List<string> args = ["hud", "devices"];
        bool never;
        try
        {
            never = DevicePhotoConsent.Mode == DevicePhotoConsent.Never;
        }
        catch (Exception)
        {
            never = true;
        }
        if (never)
            args.Add("--no-device-photos");
        return args;
    }

    private List<(DeviceCardItem Card, DevicePicturePlan Plan)> lastCardPictures = [];

    // Downloads (once, on this PC) and decodes a maker's photo for a card; replaceable for tests (no download there).
    internal Func<string, Task<Bitmap?>> LoadCardPhoto { get; set; } = url => DeviceImageCache.LoadAsync(url, 240);

    // Settings > Privacy: how JoyPro handles the makers' product photos (DevicePhotoConsent).
    public string[] PhotoConsentModes { get; } = ["Ask for each device", "Always download", "Never"];

    public string PhotoConsentMode
    {
        get => DevicePhotoConsent.Mode switch
        {
            DevicePhotoConsent.Always => PhotoConsentModes[1],
            DevicePhotoConsent.Never => PhotoConsentModes[2],
            _ => PhotoConsentModes[0]
        };
        set
        {
            string mode = value == PhotoConsentModes[1] ? DevicePhotoConsent.Always : value == PhotoConsentModes[2] ? DevicePhotoConsent.Never : DevicePhotoConsent.Ask;
            if (mode == DevicePhotoConsent.Mode)
                return;
            DevicePhotoConsent.Mode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PhotoConsentSummary));
            _ = LoadDeviceCardPicturesAsync(lastCardPictures);
        }
    }

    public string PhotoConsentSummary
    {
        get
        {
            int yes = DevicePhotoConsent.AnswerCount("yes"), no = DevicePhotoConsent.AnswerCount("no");
            return yes + no == 0 ? T("No answers per device yet.") : T("Answered per device: {0} yes, {1} no.", yes, no);
        }
    }

    [RelayCommand]
    private async Task ForgetPhotoAnswersAsync()
    {
        DevicePhotoConsent.ForgetAnswers();
        OnPropertyChanged(nameof(PhotoConsentSummary));
        await LoadDeviceCardPicturesAsync(lastCardPictures);
        StatusText = T("JoyPro asks again before it downloads a maker's photo.");
    }

    // Game covers missing on this PC may be downloaded (own setting, Settings > Privacy).
    public bool GameCoverDownloads
    {
        get => DeviceReferenceService.GameCoversEnabled;
        set
        {
            if (value == DeviceReferenceService.GameCoversEnabled)
                return;
            DeviceReferenceService.GameCoversEnabled = value;
            OnPropertyChanged();
        }
    }

    // The answer on a card to "Download the maker's photo?": yes / no for its model, always / never for every device;
    // the cards' pictures follow at once (a photo is downloaded only after a yes).
    [RelayCommand]
    private Task CardPhotoYesAsync(DeviceCardItem? card) => AnswerCardPhotoAsync(card, "yes");

    [RelayCommand]
    private Task CardPhotoNoAsync(DeviceCardItem? card) => AnswerCardPhotoAsync(card, "no");

    [RelayCommand]
    private Task CardPhotoAlwaysAsync(DeviceCardItem? card) => AnswerCardPhotoAsync(card, DevicePhotoConsent.Always);

    [RelayCommand]
    private Task CardPhotoNeverAsync(DeviceCardItem? card) => AnswerCardPhotoAsync(card, DevicePhotoConsent.Never);

    private async Task AnswerCardPhotoAsync(DeviceCardItem? card, string answer)
    {
        if (card == null || card.ModelId.Length == 0)
            return;
        DevicePhotoConsent.Answer(card.ModelId, answer);
        await LoadDeviceCardPicturesAsync(lastCardPictures);
        OnPropertyChanged(nameof(PhotoConsentSummary));
    }

    internal async Task LoadDeviceCardPicturesAsync(List<(DeviceCardItem Card, DevicePicturePlan Plan)> pictures)
    {
        lastCardPictures = pictures;
        foreach ((DeviceCardItem card, DevicePicturePlan plan) in pictures)
        {
            PhotoConsentState consent;
            try
            {
                consent = plan.ModelId.Length > 0 && plan.VendorPhoto.Length > 0 ? DevicePhotoConsent.For(plan.ModelId) : PhotoConsentState.Denied;
            }
            catch (Exception)
            {
                consent = PhotoConsentState.Denied;
            }
            // A card that shows JoyPro's line drawing does not ask for the maker's photo (the Layout page still offers it).
            card.AsksPhotoConsent = consent == PhotoConsentState.Ask && plan.LineArt == null;
            card.PhotoConsentText = card.AsksPhotoConsent ? DevicePhotoConsent.Question(card.Title, plan.VendorPhoto) : string.Empty;
            bool vendorPhotos = consent == PhotoConsentState.Allowed;
            if (!vendorPhotos && card.PictureSource == T("Maker's photo"))
            {
                // The answer is now no: back to JoyPro's line drawing or drawing.
                card.Picture = plan.LineArt != null ? new Views.Controls.LineArtImage(plan.LineArt, DeviceReferenceService.ScreenDrawingStyle) : null;
                card.PictureSource = plan.LineArt != null ? T("JoyPro line drawing") : card.Schematic != null ? T("JoyPro drawing") : string.Empty;
            }
            // The icon the user chose on the Layout page comes first.
            if (DeviceIconService.ChoiceOf(card.Device) is { } chosen && DecodeCardPicture(chosen.File) is { } icon)
            {
                card.Picture = icon;
                card.PictureSource = T("Your icon: {0}", chosen.Title);
                cardsWithChosenIcon.Add(card);
                continue;
            }
            if (cardsWithChosenIcon.Remove(card))
            {
                // The choice was taken back: the automatic picture again.
                card.Picture = plan.LineArt != null ? new Views.Controls.LineArtImage(plan.LineArt, DeviceReferenceService.ScreenDrawingStyle) : null;
                card.PictureSource = plan.LineArt != null ? T("JoyPro line drawing") : card.Schematic != null ? T("JoyPro drawing") : string.Empty;
            }
            if (plan.LayoutPicture.Length > 0 && DecodeCardPicture(plan.LayoutPicture) is { } layout)
            {
                card.Picture = layout;
                card.PictureSource = T("Your layout");
                continue;
            }
            string? photo = null;
            try
            {
                photo = plan.ModelId.Length > 0 ? DeviceReferenceService.UserPhoto(plan.ModelId) : null;
            }
            catch (Exception)
            {
                photo = null;
            }
            if (photo != null && DecodeCardPicture(photo) is { } own)
            {
                card.Picture = own;
                card.PictureSource = T("Your photo");
                continue;
            }
            // JoyPro's own line drawing of the model comes before the maker's photo (and needs no download).
            if (plan.LineArt != null)
            {
                if (card.PictureSource != T("JoyPro line drawing"))
                {
                    card.Picture = new Views.Controls.LineArtImage(plan.LineArt, DeviceReferenceService.ScreenDrawingStyle);
                    card.PictureSource = T("JoyPro line drawing");
                }
                continue;
            }
            if (vendorPhotos && plan.VendorPhoto.Length > 0 && await LoadCardPhoto(plan.VendorPhoto) is { } vendor)
            {
                card.Picture = vendor;
                card.PictureSource = T("Maker's photo");
            }
        }
        // The bind list and the HUD designer show the same icons.
        OnDeviceIconsChanged();
    }

    private static Bitmap? DecodeCardPicture(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using FileStream stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 240);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The grip set for a base: picked on the Layout page, else in the HUD designer.
    private static string? ConfiguredGripOf(string device, Dictionary<string, string> hudGrips)
    {
        try
        {
            if (DeviceReferenceService.GripOf(device) is { Length: > 0 } picked)
                return picked;
        }
        catch (Exception)
        {
            // An unreadable grips file: the HUD's choice, else none.
        }
        return hudGrips.FirstOrDefault(g => SameDevice(g.Key, device)).Value;
    }

    // The Layout page's device reference: a grip picked for a base is kept, and the cards show it.
    private void OnReferenceGripChosen(string device, RefDevice grip)
    {
        if (string.IsNullOrWhiteSpace(device))
            return;
        try
        {
            DeviceReferenceService.SetGrip(device, grip.Id);
        }
        catch (Exception)
        {
            return;
        }
        if (lastHudDevices.Count > 0)
            _ = RebuildDeviceCardsAsync();
    }

    // A card: the Layout page with the device (its picture, markers and binds).
    private string? layoutDeviceToShow;
    private bool layoutStageRefreshing;

    [RelayCommand]
    private async Task OpenDeviceCardInLayoutAsync(DeviceCardItem? card)
    {
        if (card == null)
            return;
        IsCheatSheetsSection = false;
        layoutDeviceToShow = card.Device;
        // Opening the page reads the devices and their layouts first (RefreshLayoutStageDataAsync) and then shows this
        // device; when the page is open already (or busy), it is shown here.
        CurrentStage = UiStage.Layout;
        if (!layoutStageRefreshing && layoutDeviceToShow != null)
        {
            string device = layoutDeviceToShow;
            layoutDeviceToShow = null;
            await ShowWorkspaceDeviceAsync(device);
        }
    }

    // The name the profile knows the card's device by (connected devices may carry another name with the same GUID).
    private string ProfileNameOf(DeviceCardItem card) => ProfileDevices.FirstOrDefault(d => SameDevice(d, card.Device)) ?? card.Device;

    // "Move binds to...": the card's device becomes the old device of the exchange; the user picks the new one.
    [RelayCommand]
    private void MoveDeviceBindsFrom(DeviceCardItem? card)
    {
        if (card == null)
            return;
        ExchangeOldDevice = ProfileNameOf(card);
        StatusText = T("Now pick the device that gets the binds of {0} (Replace a device, below), then press Move binds.", card.Title);
    }

    [RelayCommand]
    private async Task RemoveDeviceCardAsync(DeviceCardItem? card)
    {
        if (card == null)
            return;
        DeviceRemoveName = ProfileNameOf(card);
        await RemoveDeviceFromProfileAsync();
        await LoadDeviceCardsAsync();
    }
}
