using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Models;

// What all HUD widgets share: the look (HudLook, the HUD's style), the lines of the binds ticker and the game / aircraft
// of the header.
public sealed partial class HudContext : ObservableObject
{
    public HudContext(ObservableCollection<OverlayLine> lines)
    {
        Lines = lines;
        lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoLines));
    }

    public ObservableCollection<OverlayLine> Lines { get; }

    public bool HasNoLines => Lines.Count == 0;

    [ObservableProperty]
    private string headerText = "JoyPro · no game running";

    [ObservableProperty]
    private Bitmap? headerPicture;

    // The HUD's own design (Look > Style); a widget with a style of its own uses that one instead.
    [ObservableProperty]
    private HudLook look = new(HudStylePresets.Default);

    public void ApplyLook(HudLookSettings settings) => Look = new HudLook(settings);
}

// One widget of the HUD (a device, the binds ticker or the game header), at its place in the reference screen.
public sealed partial class HudWidget : ObservableObject
{
    public HudWidget(HudWidgetConfig config, HudContext theme, HudDevice? device)
    {
        Config = config;
        Theme = theme;
        Device = device;
        x = config.X;
        y = config.Y;
        scale = config.Scale <= 0 ? 1 : config.Scale;
        visible = config.Visible;
        showName = config.ShowName;
        showPicture = config.ShowPicture;
        tilt = config.Tilt;
        showButtons = config.ShowButtons;
        showAxes = config.ShowAxes;
        showGimbal = config.ShowGimbal;
        showHats = config.ShowHats;
        pictureSource = string.IsNullOrWhiteSpace(config.PictureSource) ? T("Auto") : config.PictureSource;
        grip = config.Grip;
        axisX = config.AxisX ?? string.Empty;
        axisY = config.AxisY ?? string.Empty;
        axisTwist = config.AxisTwist ?? string.Empty;
        streamVisible = config.StreamVisible ?? config.Visible;
        config.StreamVisible = streamVisible;
        showChips = config.ShowChips;
        showValues = config.ShowValues;
        showLabels = config.ShowLabels;
        showKind = config.ShowKind;
        showHeaderPicture = config.ShowHeaderPicture;
        showStick = config.ShowStick;
        showThrottle = config.ShowThrottle;
        aircraftChoice = config.Aircraft ?? string.Empty;
        stickDevice = config.StickDevice ?? string.Empty;
        throttleDevice = config.ThrottleDevice ?? string.Empty;
        ownLook = config.Look == null ? null : new HudLook(config.Look);
        if (device != null)
            device.TiltEnabled = tilt;
        theme.PropertyChanged += OnThemeChanged;
    }

    // The design this widget is drawn with: its own style, else the HUD's.
    public HudLook Look => ownLook ?? Theme.Look;

    private HudLook? ownLook;

    public bool HasOwnStyle => ownLook != null;

    public string OwnStyleName => ownLook?.Settings.Name ?? string.Empty;

    // A style of its own (null = the HUD's style again).
    public void SetOwnStyle(HudLookSettings? settings)
    {
        Config.Look = settings?.Normalized();
        ownLook = Config.Look == null ? null : new HudLook(Config.Look);
        OnPropertyChanged(nameof(Look));
        OnPropertyChanged(nameof(HasOwnStyle));
        OnPropertyChanged(nameof(OwnStyleName));
    }

    private void OnThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudContext.Look) && ownLook == null)
            OnPropertyChanged(nameof(Look));
    }

    // The widget is dropped (devices read again): it stops listening to the HUD's look.
    public void Detach() => Theme.PropertyChanged -= OnThemeChanged;

    // The stick element's axis sources: empty = Automatic, else "<device>|<input>" (HudWidgetConfig).
    [ObservableProperty]
    private string axisX;

    [ObservableProperty]
    private string axisY;

    [ObservableProperty]
    private string axisTwist;

    partial void OnAxisXChanged(string value) => Config.AxisX = value ?? string.Empty;
    partial void OnAxisYChanged(string value) => Config.AxisY = value ?? string.Empty;
    partial void OnAxisTwistChanged(string value) => Config.AxisTwist = value ?? string.Empty;

    public string AxisSourceOf(int role) => role switch { HudDevice.RoleX => AxisX, HudDevice.RoleY => AxisY, _ => AxisTwist };

    public void SetAxisSource(int role, string value)
    {
        switch (role)
        {
            case HudDevice.RoleX: AxisX = value; break;
            case HudDevice.RoleY: AxisY = value; break;
            default: AxisTwist = value; break;
        }
    }

    public HudWidgetConfig Config { get; }

    public HudContext Theme { get; }

    public HudDevice? Device { get; }

    public string Kind => Config.Kind;

    public bool IsDevice => Kind == "device" && Device != null;

    public bool IsBinds => Kind == "binds";

    public bool IsHeader => Kind == "header";

    public bool IsAircraft => Kind == "aircraft";

    // The aircraft widget's stick and throttle (set by the view model; null for the other widgets).
    [ObservableProperty]
    private HudAircraft? aircraft;

    public string Title => Kind switch
    {
        "binds" => "What the button does",
        "header" => "Game and aircraft",
        "aircraft" => "Aircraft stick and throttle",
        _ => Device?.Title ?? Config.Device
    };

    public string Subtitle => Kind switch
    {
        "binds" => "Function of every pressed button in the running game",
        "header" => "The running game / DCS aircraft",
        "aircraft" => "The stick and throttle of the aircraft you fly, moving with yours",
        _ => Device?.Subtitle ?? string.Empty
    };

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;

    [ObservableProperty]
    private double scale;

    [ObservableProperty]
    private bool visible;

    [ObservableProperty]
    private bool showName;

    [ObservableProperty]
    private bool showPicture;

    [ObservableProperty]
    private bool tilt;

    [ObservableProperty]
    private bool showButtons;

    [ObservableProperty]
    private bool showAxes;

    [ObservableProperty]
    private bool showGimbal;

    [ObservableProperty]
    private bool showHats;

    [ObservableProperty]
    private bool streamVisible;

    [ObservableProperty]
    private bool showChips;

    [ObservableProperty]
    private bool showValues;

    [ObservableProperty]
    private bool showLabels;

    [ObservableProperty]
    private bool showKind;

    [ObservableProperty]
    private bool showHeaderPicture;

    [ObservableProperty]
    private bool showStick;

    [ObservableProperty]
    private bool showThrottle;

    // Aircraft widget: empty = the aircraft JoyPro knows, else "<game>/<picture set>".
    [ObservableProperty]
    private string aircraftChoice;

    [ObservableProperty]
    private string stickDevice;

    [ObservableProperty]
    private string throttleDevice;

    // Auto (product photo, else layout picture), Photo or Layout (button positions light up).
    [ObservableProperty]
    private string pictureSource;

    [ObservableProperty]
    private string grip;

    [ObservableProperty]
    private bool isSelected;

    public bool PictureVisible => ShowPicture && Device?.Picture != null;

    public bool MarkersVisible => ShowButtons && PictureVisible && Device?.ShowsLayout == true;

    public bool ButtonGridVisible => ShowButtons && Device != null && !MarkersVisible && Device.ButtonCells.Count > 0;

    public bool GimbalVisible => ShowGimbal && Device?.HasGimbal == true;

    public bool LeversVisible => ShowAxes && Device?.Levers.Count > 0;

    public bool AxesVisible => ShowAxes && Device?.Bars.Count > 0;

    public bool HatsVisible => ShowHats && Device?.Hats.Count > 0;

    public bool HasGripChoices => Device?.GripChoices.Count > 0;

    public bool ChipsVisible => ShowChips && ShowButtons;

    public bool AircraftStickVisible => ShowStick && Aircraft?.HasStick == true;

    public bool AircraftThrottleVisible => ShowThrottle && Aircraft?.HasThrottle == true;

    partial void OnAircraftChanged(HudAircraft? value) => RefreshAircraft();

    public void RefreshAircraft()
    {
        OnPropertyChanged(nameof(AircraftStickVisible));
        OnPropertyChanged(nameof(AircraftThrottleVisible));
    }

    // Changes go back into the saved layout.
    partial void OnXChanged(double value) => Config.X = Math.Round(value);
    partial void OnYChanged(double value) => Config.Y = Math.Round(value);
    partial void OnScaleChanged(double value) => Config.Scale = Math.Round(value, 2);
    partial void OnVisibleChanged(bool value) => Config.Visible = value;
    partial void OnStreamVisibleChanged(bool value) => Config.StreamVisible = value;
    partial void OnShowValuesChanged(bool value) => Config.ShowValues = value;
    partial void OnShowLabelsChanged(bool value) => Config.ShowLabels = value;
    partial void OnShowKindChanged(bool value) => Config.ShowKind = value;
    partial void OnShowHeaderPictureChanged(bool value) => Config.ShowHeaderPicture = value;
    partial void OnAircraftChoiceChanged(string value) => Config.Aircraft = value ?? string.Empty;
    partial void OnStickDeviceChanged(string value) => Config.StickDevice = value ?? string.Empty;
    partial void OnThrottleDeviceChanged(string value) => Config.ThrottleDevice = value ?? string.Empty;

    partial void OnShowChipsChanged(bool value)
    {
        Config.ShowChips = value;
        OnPropertyChanged(nameof(ChipsVisible));
    }

    partial void OnShowStickChanged(bool value)
    {
        Config.ShowStick = value;
        OnPropertyChanged(nameof(AircraftStickVisible));
    }

    partial void OnShowThrottleChanged(bool value)
    {
        Config.ShowThrottle = value;
        OnPropertyChanged(nameof(AircraftThrottleVisible));
    }
    partial void OnShowNameChanged(bool value) => Config.ShowName = value;

    partial void OnTiltChanged(bool value)
    {
        Config.Tilt = value;
        if (Device != null)
            Device.TiltEnabled = value;
    }
    partial void OnShowHatsChanged(bool value)
    {
        Config.ShowHats = value;
        OnPropertyChanged(nameof(HatsVisible));
    }

    partial void OnShowPictureChanged(bool value)
    {
        Config.ShowPicture = value;
        RefreshVisibility();
    }

    partial void OnShowButtonsChanged(bool value)
    {
        Config.ShowButtons = value;
        RefreshVisibility();
        OnPropertyChanged(nameof(ChipsVisible));
    }

    partial void OnShowAxesChanged(bool value)
    {
        Config.ShowAxes = value;
        RefreshVisibility();
    }

    partial void OnShowGimbalChanged(bool value)
    {
        Config.ShowGimbal = value;
        OnPropertyChanged(nameof(GimbalVisible));
    }

    partial void OnPictureSourceChanged(string value)
    {
        Config.PictureSource = value;
        Device?.ChoosePicture(value);
        RefreshVisibility();
    }

    partial void OnGripChanged(string value) => Config.Grip = value ?? string.Empty;

    public void RefreshVisibility()
    {
        OnPropertyChanged(nameof(PictureVisible));
        OnPropertyChanged(nameof(MarkersVisible));
        OnPropertyChanged(nameof(ButtonGridVisible));
        OnPropertyChanged(nameof(GimbalVisible));
        OnPropertyChanged(nameof(LeversVisible));
        OnPropertyChanged(nameof(AxesVisible));
        OnPropertyChanged(nameof(HatsVisible));
    }
}

// A connected device with its live state: tilt of the picture, gimbal dot, lever and axis bars, pressed buttons, hats.
public sealed partial class HudDevice : ObservableObject
{
    public const double PictureHeight = 230;
    public const double GimbalRadius = 34;
    // Devices with more buttons (virtual buttons of VKB / Virpil bases) show only the pressed ones as chips.
    private const int MaxGridButtons = 32;

    private readonly Dictionary<string, HudMarker> markersByInput = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> pressed = [];
    private int rollIndex = -1, pitchIndex = -1, twistIndex = -1;

    // ---- The stick element's axes (2.5.0 HUD bug: a VKB grip on a VPforce Rhino base is a device of its own without
    //      X/Y; the gimbal read its missing X/Y and lay in a corner). A missing or dead axis is centred, never a corner;
    //      Automatic takes an axis the device lacks from the base it sits on; the designer can pick any device's axis.
    //      The live path allocates nothing: the snapshot array is kept by reference, masks are ints, the dependents a
    //      list built when the sources change; properties are set only when their value changes.
    public const int RoleX = 0, RoleY = 1, RoleTwist = 2;

    // The axes the device reports (bit per DirectInput axis index), seen in a snapshot, moved since the first one.
    private int reportedMask, seenMask, movedMask;
    private readonly double[] firstAxes = new double[8];
    private double[]? lastAxes;
    // Per role: a picked source (device and axis index), or null = Automatic.
    private readonly HudDevice?[] pickedDevice = new HudDevice?[3];
    private readonly int[] pickedIndex = [-1, -1, -1];
    // The base this device sits on (Automatic takes the axes it lacks from there).
    private HudDevice? pairedBase;
    // Devices whose stick element reads an axis of this one.
    private readonly List<HudDevice> axisDependents = [];

    public HudDevice(string device, string name, string model, string kind, int buttons, int hats, IEnumerable<(string Input, string Control, string Label)> axes,
        IReadOnlyDictionary<string, string> labels, IEnumerable<(string Id, string Name)> gripChoices, string hand = "")
    {
        Hand = hand;
        Device = device;
        Name = name;
        Model = model;
        DeviceKind = kind;
        ButtonCount = buttons;
        Labels = labels;
        GripChoices = gripChoices.Select(g => new HudChoice(g.Id, g.Name)).ToList();
        foreach ((string input, string control, string label) in axes)
        {
            int index = AxisIndex(input);
            if (index < 0)
                continue;
            reportedMask |= 1 << index;
            string role = control.ToLowerInvariant();
            bool stickLike = kind is "stick" or "yoke" or "hotas" or "base" or "controller";
            if (rollIndex < 0 && (role.EndsWith(".axis.roll", StringComparison.Ordinal) || (role.Length == 0 && stickLike && input == "JOY_X")))
                rollIndex = index;
            else if (pitchIndex < 0 && (role.EndsWith(".axis.pitch", StringComparison.Ordinal) || (role.Length == 0 && stickLike && input == "JOY_Y")))
                pitchIndex = index;
            else if (twistIndex < 0 && (role.EndsWith(".axis.twist", StringComparison.Ordinal) || (role.Length == 0 && kind == "stick" && input == "JOY_RZ")))
                twistIndex = index;

            string shortLabel = ShortLabel(label, input);
            if (IsLever(role, kind, input))
                Levers.Add(new HudAxis(index, input, shortLabel, bipolar: false));
            else
                Bars.Add(new HudAxis(index, input, shortLabel, Bipolar(role, kind, input)));
        }
        if (buttons <= MaxGridButtons)
        {
            for (int i = 1; i <= buttons; i++)
                ButtonCells.Add(new HudButton(i));
        }
        for (int i = 1; i <= Math.Min(hats, 4); i++)
            Hats.Add(new HudHat(i));
    }

    public string Device { get; }

    public string Name { get; }

    public string Model { get; }

    public string DeviceKind { get; }

    // left, right, none or empty (catalog).
    public string Hand { get; }

    public int ButtonCount { get; }

    public IReadOnlyDictionary<string, string> Labels { get; }

    // What each input is in the control vocabulary ("JOY_BTN3" -> "stick.hat.tms.up"; docs/devices.md), from the
    // device catalog: the aircraft widget lights the aircraft's control of the same name.
    public IReadOnlyDictionary<string, string> Controls { get; set; } = new Dictionary<string, string>();

    // The stick element's position, -1..1 per role (0 = centred): the stick element draws it, the aircraft widget reads it.
    [ObservableProperty]
    private double stickX;

    [ObservableProperty]
    private double stickY;

    [ObservableProperty]
    private double stickTwist;

    // Called after the device's axes or its stick element moved (no allocation: the aircraft widget marks itself dirty).
    public Action<HudDevice>? Moved { get; set; }

    // A lever's travel, 0..1 (0 when the device has no such lever).
    public double LeverValue(int index) => index >= 0 && index < Levers.Count ? Levers[index].Value : 0;

    public List<HudChoice> GripChoices { get; }

    // "VID:PID" in hex (engine hud devices), empty when unknown.
    public string VidPid { get; init; } = string.Empty;

    // A virtual device (vJoy, also the one JoyPro's forwarder tunnels into; ViGEm or another virtual joystick): it only
    // mirrors real devices, so it never is a stick's base and never sits on one.
    public bool IsVirtual => LooksVirtual(Device, Name, VidPid);

    // A base by itself: of the kind "base", or a base that takes grips (a VPforce Rhino: the grip choices of the catalog).
    public bool IsBase => DeviceKind == "base" || GripChoices.Count > 0;

    private static readonly Regex VirtualName = new(@"\bvjoy\b|vigem|virtual (joystick|device|controller|gamepad)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksVirtual(string? device, string? name, string? vidPid) =>
        string.Equals((vidPid ?? string.Empty).Trim(), "1234:BEAD", StringComparison.OrdinalIgnoreCase) ||
        VirtualName.IsMatch(device ?? string.Empty) || VirtualName.IsMatch(name ?? string.Empty);

    public string Title =>DeviceAliases.Shown(Device) is { Length: > 0 } alias ? alias : Model.Length > 0 ? Model : Name;

    public void RefreshName()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShortTitle));
    }

    public string Subtitle => DeviceKind + (Model.Length > 0 && Model != Name ? " · " + Name : string.Empty);

    public string ShortTitle
    {
        get
        {
            string text = Regex.Replace(Title, @"\s*\(.*?\)", string.Empty).Trim();
            return text.Length > 34 ? text[..33] + "…" : text;
        }
    }

    public ObservableCollection<HudAxis> Bars { get; } = [];

    public ObservableCollection<HudAxis> Levers { get; } = [];

    public ObservableCollection<HudButton> ButtonCells { get; } = [];

    public ObservableCollection<HudHat> Hats { get; } = [];

    public ObservableCollection<HudMarker> Markers { get; } = [];

    // The held buttons by name ("Trigger", "Weapon release"), newest last.
    public ObservableCollection<string> PressedChips { get; } = [];

    // The stick element shows when X and Y have a source: the device's own, the base's (Automatic) or picked ones.
    public bool HasGimbal => HasSource(RoleX) && HasSource(RoleY);

    public bool HasTwist => HasSource(RoleTwist);

    // The axes the device reports, as "JOY_X" ... (the designer's axis lists).
    public IEnumerable<string> ReportedAxes()
    {
        for (int i = 0; i < AxisTokens.Length; i++)
        {
            if ((reportedMask & (1 << i)) != 0)
                yield return AxisTokens[i];
        }
    }

    public static readonly string[] AxisTokens = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    public bool Reports(int index) => index >= 0 && index < 8 && (reportedMask & (1 << index)) != 0;

    // The device's own axis of a role (roll, pitch, twist by the catalog's roles or the stick defaults), -1 = none.
    public int OwnIndex(int role) => role switch { RoleX => rollIndex, RoleY => pitchIndex, _ => twistIndex };

    public HudDevice? PairedBase => pairedBase;

    // An axis that gives a position: reported, and either moved since the first report or not stuck at an end of its
    // travel (a grip's X/Y without sensors reads 0 or 1 for ever).
    public bool AxisLive(int index)
    {
        if (!Reports(index))
            return false;
        if ((movedMask & (1 << index)) != 0 || lastAxes == null || index >= lastAxes.Length)
            return true;
        double v = lastAxes[index];
        return v > 0.002 && v < 0.998;
    }

    private bool HasSource(int role) =>
        pickedDevice[role] != null ? pickedDevice[role]!.Reports(pickedIndex[role])
        : OwnIndex(role) >= 0 || (pairedBase != null && pairedBase.OwnIndex(role) >= 0);

    // Where a role's axis comes from now: the picked one, else (Automatic) the device's own while it is live, else the
    // base's; (null, -1) = nothing, the element stays centred.
    public (HudDevice? Device, int Index) SourceOf(int role)
    {
        if (pickedDevice[role] is { } picked)
            return (picked, pickedIndex[role]);
        int own = OwnIndex(role);
        if (own >= 0 && AxisLive(own))
            return (this, own);
        if (pairedBase != null && pairedBase.OwnIndex(role) is int baseIndex and >= 0 && pairedBase.AxisLive(baseIndex))
            return (pairedBase, baseIndex);
        return own >= 0 ? (this, own) : (null, -1);
    }

    // Sets the axis sources (the HUD designer, a device list change); the old ones stop feeding this device.
    public void SetAxisSources(HudDevice? baseDevice, (HudDevice? Device, int Index) x, (HudDevice? Device, int Index) y, (HudDevice? Device, int Index) twist)
    {
        pairedBase = ReferenceEquals(baseDevice, this) ? null : baseDevice;
        (HudDevice? Device, int Index)[] picks = [x, y, twist];
        for (int role = 0; role < 3; role++)
        {
            pickedDevice[role] = picks[role].Device;
            pickedIndex[role] = picks[role].Device == null ? -1 : picks[role].Index;
        }
        OnPropertyChanged(nameof(HasGimbal));
        OnPropertyChanged(nameof(HasTwist));
        OnPropertyChanged(nameof(CanTilt));
        UpdateGimbal();
    }

    // The devices this one reads axes from (itself excluded): the base and the picked ones.
    public IEnumerable<HudDevice> AxisProviders()
    {
        if (pairedBase != null)
            yield return pairedBase;
        for (int role = 0; role < 3; role++)
        {
            if (pickedDevice[role] is { } picked && !ReferenceEquals(picked, this) && !ReferenceEquals(picked, pairedBase))
                yield return picked;
        }
    }

    public void ClearAxisDependents() => axisDependents.Clear();

    public void AddAxisDependent(HudDevice device)
    {
        if (!ReferenceEquals(device, this) && !axisDependents.Contains(device))
            axisDependents.Add(device);
    }

    public bool CanTilt => HasGimbal && DeviceKind is "stick" or "base" or "hotas" or "yoke";

    // The widget's Tilt option: the picture follows the stick only when on.
    public bool TiltEnabled { get; set; } = true;

    // Both pictures, loaded later; the widget's picture source picks one.
    public Bitmap? Photo { get; set; }

    public Bitmap? LayoutPicture { get; set; }

    public bool HasLayout => LayoutPicture != null;

    [ObservableProperty]
    private Bitmap? picture;

    // The device's icon (the one chosen on the Layout page, else the device card's picture): the designer's list.
    [ObservableProperty]
    private IImage? icon;

    public IImage? ListIcon => Icon ?? Picture;

    partial void OnPictureChanged(Bitmap? value) => OnPropertyChanged(nameof(ListIcon));

    partial void OnIconChanged(IImage? value) => OnPropertyChanged(nameof(ListIcon));

    [ObservableProperty]
    private bool showsLayout;

    [ObservableProperty]
    private double pictureWidth = 180;

    // Tilt of the picture in degrees (Rotate3DTransform).
    [ObservableProperty]
    private double roll;

    [ObservableProperty]
    private double pitch;

    [ObservableProperty]
    private double twist;

    [ObservableProperty]
    private double gimbalX;

    [ObservableProperty]
    private double gimbalY;

    // Twist as a turn of the gimbal ring's tick.
    [ObservableProperty]
    private double twistTick;

    // Names of the buttons that are held ("Trigger · Weapon release").
    [ObservableProperty]
    private string pressedText = string.Empty;

    public bool HasPressed => PressedText.Length > 0;

    partial void OnPressedTextChanged(string value) => OnPropertyChanged(nameof(HasPressed));

    public void SetPictures(Bitmap? photo, Bitmap? layout, IEnumerable<(string Input, double X, double Y)> markers, string source)
    {
        Photo = photo;
        LayoutPicture = layout;
        Markers.Clear();
        markersByInput.Clear();
        foreach ((string input, double mx, double my) in markers)
        {
            HudMarker marker = new(input, mx, my);
            Markers.Add(marker);
            markersByInput[input] = marker;
        }
        ChoosePicture(source);
        OnPropertyChanged(nameof(HasLayout));
    }

    public void ChoosePicture(string source)
    {
        Bitmap? chosen = source switch
        {
            "Layout" => LayoutPicture ?? Photo,
            // Auto and Photo: the manufacturer's picture only (a JoyPro 1 layout picture carries its label boxes).
            _ => Photo
        };
        Picture = chosen;
        ShowsLayout = chosen != null && chosen == LayoutPicture && Markers.Count > 0;
        if (chosen != null && chosen.Size.Height > 0)
            PictureWidth = Math.Round(PictureHeight * chosen.Size.Width / chosen.Size.Height);
        foreach (HudMarker marker in Markers)
            marker.Place(PictureWidth, PictureHeight);
    }

    // A snapshot of this device's axes (0..1 each): its bars and levers, its stick element and the stick elements that
    // read an axis of it. Nothing is allocated here (runs for every axis report while the HUD is on).
    public void ApplyAxes(double[] axes)
    {
        int count = Math.Min(axes.Length, firstAxes.Length);
        for (int i = 0; i < count; i++)
        {
            int bit = 1 << i;
            if ((seenMask & bit) == 0)
            {
                firstAxes[i] = axes[i];
                seenMask |= bit;
            }
            else if ((movedMask & bit) == 0 && Math.Abs(axes[i] - firstAxes[i]) > 0.02)
            {
                movedMask |= bit;
            }
        }
        lastAxes = axes;
        UpdateGimbal();
        for (int i = 0; i < Bars.Count; i++)
        {
            HudAxis axis = Bars[i];
            if (axis.Index < axes.Length)
                axis.Value = axes[axis.Index];
        }
        for (int i = 0; i < Levers.Count; i++)
        {
            HudAxis axis = Levers[i];
            if (axis.Index < axes.Length)
                axis.Value = axes[axis.Index];
        }
        for (int i = 0; i < axisDependents.Count; i++)
            axisDependents[i].UpdateGimbal();
        Moved?.Invoke(this);
    }

    // -1..1 of a role from its source; 0 (centred) when there is none, it reports nothing yet or it is dead.
    private double RoleValue(int role)
    {
        (HudDevice? device, int index) = SourceOf(role);
        if (device == null || index < 0 || device.lastAxes is not { } axes || index >= axes.Length || !device.AxisLive(index))
            return 0;
        return Deadzone(axes[index] * 2 - 1);
    }

    // The stick element and the tilt of the picture from the axis sources (only changed values reach the view).
    public void UpdateGimbal()
    {
        double rollValue = RoleValue(RoleX), pitchValue = RoleValue(RoleY), twistValue = RoleValue(RoleTwist);
        StickX = Math.Round(rollValue, 3);
        StickY = Math.Round(pitchValue, 3);
        StickTwist = Math.Round(twistValue, 3);
        bool tilt = TiltEnabled && CanTilt;
        Roll = tilt ? Math.Round(rollValue * 18, 2) : 0;
        Pitch = tilt ? Math.Round(-pitchValue * 14, 2) : 0;
        Twist = tilt ? Math.Round(twistValue * 24, 2) : 0;
        GimbalX = Math.Round(rollValue * GimbalRadius, 1);
        GimbalY = Math.Round(pitchValue * GimbalRadius, 1);
        TwistTick = Math.Round(twistValue * 90, 1);
        Moved?.Invoke(this);
    }

    public void SetInput(string input, bool down)
    {
        if (input.StartsWith("JOY_BTN_POV", StringComparison.OrdinalIgnoreCase))
        {
            // JOY_BTN_POV<n>_<dir>
            string rest = input[11..];
            int split = rest.IndexOf('_');
            if (split > 0 && int.TryParse(rest[..split], out int hat) && hat >= 1 && hat <= Hats.Count)
                Hats[hat - 1].Set(rest[(split + 1)..], down);
            return;
        }
        if (!input.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase) || !int.TryParse(input[7..], out int number))
            return;
        if (down)
            pressed.Add(number);
        else
            pressed.Remove(number);
        if (number <= ButtonCells.Count)
            ButtonCells[number - 1].IsPressed = down;
        if (markersByInput.TryGetValue(input, out HudMarker? marker))
            marker.IsPressed = down;
        string name = ButtonName(number);
        if (down && !PressedChips.Contains(name))
            PressedChips.Add(name);
        else if (!down)
            PressedChips.Remove(name);
        while (PressedChips.Count > 5)
            PressedChips.RemoveAt(0);
        PressedText = string.Join(" · ", pressed.OrderBy(n => n).Take(4).Select(ButtonName));
    }

    public void ReleaseAll()
    {
        foreach (int number in pressed.ToList())
            SetInput("JOY_BTN" + number, false);
        foreach (HudHat hat in Hats)
            hat.Set(string.Empty, false);
    }

    private static double Deadzone(double value) => Math.Abs(value) < 0.02 ? 0 : value;

    // The catalog's name of a button without its hints ("Button 5 (DCS: uncage / cage)" -> "Button 5").
    private string ButtonName(int number)
    {
        string label = Labels.TryGetValue("JOY_BTN" + number, out string? text) ? Regex.Replace(text, @"\s*\(.*?\)", string.Empty).Trim() : string.Empty;
        return label.Length > 0 ? label : "Button " + number;
    }

    public static int AxisIndex(string input) => input.ToUpperInvariant() switch
    {
        "JOY_X" => 0,
        "JOY_Y" => 1,
        "JOY_Z" => 2,
        "JOY_RX" => 3,
        "JOY_RY" => 4,
        "JOY_RZ" => 5,
        "JOY_SLIDER1" => 6,
        "JOY_SLIDER2" => 7,
        _ => -1
    };

    private static bool IsLever(string role, string kind, string input)
    {
        if (role.Contains("ministick", StringComparison.Ordinal))
            return false;
        if (role.StartsWith("throttle.axis.", StringComparison.Ordinal) || role.StartsWith("collective.axis.main", StringComparison.Ordinal) || role.Contains("brake", StringComparison.Ordinal))
            return true;
        return role.Length == 0 && kind == "throttle" && input is "JOY_Z" or "JOY_RX" or "JOY_RY";
    }

    private static bool Bipolar(string role, string kind, string input)
    {
        if (role.Length > 0)
            return role.Contains("roll") || role.Contains("pitch") || role.Contains("twist") || role.Contains("rudder") || role.Contains("ministick") || role.EndsWith(".x") || role.EndsWith(".y");
        return kind is "stick" or "pedals" or "yoke" ? input is "JOY_X" or "JOY_Y" or "JOY_RZ" : false;
    }

    // "Microstick B Y (DCS: slew/TDC vertical)" -> "Microstick B Y".
    private static string ShortLabel(string label, string input)
    {
        string text = Regex.Replace(label ?? string.Empty, @"\s*\(.*?\)", string.Empty).Trim();
        if (text.Length == 0)
            text = input.Replace("JOY_", string.Empty);
        return text.Length > 16 ? text[..15] + "…" : text;
    }
}

// An accent colour of the HUD page's palette.
public sealed record HudAccentOption(string Hex, IBrush Brush);

public sealed record HudChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class HudAxis : ObservableObject
{
    public const double BarWidth = 112;
    public const double LeverHeight = 92;

    public HudAxis(int index, string input, string label, bool bipolar)
    {
        Index = index;
        Input = input;
        Label = label;
        Bipolar = bipolar;
        Value = 0.5;
    }

    public int Index { get; }

    public string Input { get; }

    public string Label { get; }

    public bool Bipolar { get; }

    // 0..1 as DirectInput reports it.
    [ObservableProperty]
    private double value;

    // Horizontal bar: fill from the middle (bipolar) or from the left.
    public double FillLeft => Bipolar ? Math.Min(Value, 0.5) * BarWidth : 0;

    public double FillWidth => Math.Max(2, Bipolar ? Math.Abs(Value - 0.5) * BarWidth : Value * BarWidth);

    // Lever: fill from the bottom.
    public double LeverFill => Math.Max(3, Value * LeverHeight);

    // From tables made once: an axis report makes no new string.
    public string Percent => Bipolar
        ? BipolarTexts[Math.Clamp((int)Math.Round((Value - 0.5) * 200), -100, 100) + 100]
        : PercentTexts[Math.Clamp((int)Math.Round(Value * 100), 0, 100)];

    private static readonly string[] BipolarTexts = Enumerable.Range(-100, 201).Select(v => v.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private static readonly string[] PercentTexts = Enumerable.Range(0, 101).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%").ToArray();

    // Cached event arguments: an axis report changes these many times a second while the HUD is on (no allocation).
    private static readonly System.ComponentModel.PropertyChangedEventArgs FillLeftChanged = new(nameof(FillLeft));
    private static readonly System.ComponentModel.PropertyChangedEventArgs FillWidthChanged = new(nameof(FillWidth));
    private static readonly System.ComponentModel.PropertyChangedEventArgs LeverFillChanged = new(nameof(LeverFill));
    private static readonly System.ComponentModel.PropertyChangedEventArgs PercentChanged = new(nameof(Percent));

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(FillLeftChanged);
        OnPropertyChanged(FillWidthChanged);
        OnPropertyChanged(LeverFillChanged);
        OnPropertyChanged(PercentChanged);
    }
}

public sealed partial class HudButton : ObservableObject
{
    public HudButton(int number)
    {
        Number = number;
    }

    public int Number { get; }

    [ObservableProperty]
    private bool isPressed;
}

// A button position on the layout picture (0..1 of the picture), placed in widget pixels.
public sealed partial class HudMarker : ObservableObject
{
    public const double Size = 16;

    public HudMarker(string input, double x, double y)
    {
        Input = input;
        RelativeX = x;
        RelativeY = y;
    }

    public string Input { get; }

    public double RelativeX { get; }

    public double RelativeY { get; }

    [ObservableProperty]
    private double left;

    [ObservableProperty]
    private double top;

    [ObservableProperty]
    private bool isPressed;

    public void Place(double width, double height)
    {
        Left = RelativeX * width - Size / 2;
        Top = RelativeY * height - Size / 2;
    }
}

// A hat with its 8 directions around a centre dot.
public sealed partial class HudHat : ObservableObject
{
    private static readonly string[] Directions = ["U", "UR", "R", "DR", "D", "DL", "L", "UL"];

    public HudHat(int number)
    {
        Number = number;
        for (int i = 0; i < Directions.Length; i++)
        {
            double angle = i * Math.PI / 4;
            Dots.Add(new HudHatDot(Directions[i], 13 + Math.Sin(angle) * 11, 13 - Math.Cos(angle) * 11));
        }
    }

    public int Number { get; }

    public ObservableCollection<HudHatDot> Dots { get; } = [];

    [ObservableProperty]
    private bool isCentered = true;

    public void Set(string direction, bool down)
    {
        foreach (HudHatDot dot in Dots)
        {
            if (dot.Direction == direction)
                dot.IsActive = down;
            else if (down)
                dot.IsActive = false;
        }
        IsCentered = !Dots.Any(d => d.IsActive);
    }
}

public sealed partial class HudHatDot : ObservableObject
{
    public HudHatDot(string direction, double left, double top)
    {
        Direction = direction;
        Left = left;
        Top = top;
    }

    public string Direction { get; }

    public double Left { get; }

    public double Top { get; }

    [ObservableProperty]
    private bool isActive;
}
