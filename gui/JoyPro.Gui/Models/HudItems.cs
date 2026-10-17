using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// What all HUD widgets share: the look (accent colour, style, opacity), the lines of the binds ticker and the
// game / aircraft of the header.
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

    [ObservableProperty]
    private IBrush accent = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#38BDF8"));

    [ObservableProperty]
    private Color accentColor = Color.Parse("#38BDF8");

    [ObservableProperty]
    private IBrush accentSoft = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#4038BDF8"));

    [ObservableProperty]
    private BoxShadows glow = BoxShadows.Parse("0 0 14 3 #CC38BDF8");

    [ObservableProperty]
    private IBrush cardBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#B00B1220"));

    [ObservableProperty]
    private IBrush cardBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#5538BDF8"));

    [ObservableProperty]
    private BoxShadows cardShadow = BoxShadows.Parse("0 10 30 0 #99000000");

    [ObservableProperty]
    private double opacity = 1;

    public void Apply(string accentHex, string style, double hudOpacity)
    {
        Color color = Color.TryParse(accentHex, out Color parsed) ? parsed : Color.Parse("#38BDF8");
        AccentColor = color;
        Accent = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color);
        AccentSoft = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        string hex = color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        bool glass = style != "Neon" && style != "Minimal";
        Glow = style == "Minimal" ? default : BoxShadows.Parse("0 0 14 3 #CC" + hex);
        CardBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(glass ? Color.Parse("#B00B1220") : Colors.Transparent);
        CardBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(style == "Minimal" ? Colors.Transparent : Color.FromArgb(glass ? (byte)0x55 : (byte)0xCC, color.R, color.G, color.B));
        CardShadow = glass ? BoxShadows.Parse("0 10 30 0 #99000000") : style == "Neon" ? BoxShadows.Parse("0 0 18 1 #66" + hex) : default;
        Opacity = Math.Clamp(hudOpacity, 0.2, 1);
    }
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
        if (device != null)
            device.TiltEnabled = tilt;
    }

    public HudWidgetConfig Config { get; }

    public HudContext Theme { get; }

    public HudDevice? Device { get; }

    public string Kind => Config.Kind;

    public bool IsDevice => Kind == "device" && Device != null;

    public bool IsBinds => Kind == "binds";

    public bool IsHeader => Kind == "header";

    public string Title => Kind switch
    {
        "binds" => "What the button does",
        "header" => "Game and aircraft",
        _ => Device?.Title ?? Config.Device
    };

    public string Subtitle => Kind switch
    {
        "binds" => "Function of every pressed button in the running game",
        "header" => "The running game / DCS aircraft",
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

    // Changes go back into the saved layout.
    partial void OnXChanged(double value) => Config.X = Math.Round(value);
    partial void OnYChanged(double value) => Config.Y = Math.Round(value);
    partial void OnScaleChanged(double value) => Config.Scale = Math.Round(value, 2);
    partial void OnVisibleChanged(bool value) => Config.Visible = value;
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

    public List<HudChoice> GripChoices { get; }

    public string Title => Model.Length > 0 ? Model : Name;

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

    public bool HasGimbal => rollIndex >= 0 && pitchIndex >= 0;

    public bool HasTwist => twistIndex >= 0;

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

    public void ApplyAxes(double[] axes)
    {
        double Bi(int index) => index >= 0 && index < axes.Length ? Deadzone(axes[index] * 2 - 1) : 0;
        double rollValue = Bi(rollIndex), pitchValue = Bi(pitchIndex), twistValue = Bi(twistIndex);
        bool tilt = TiltEnabled && CanTilt;
        Roll = tilt ? Math.Round(rollValue * 18, 2) : 0;
        Pitch = tilt ? Math.Round(-pitchValue * 14, 2) : 0;
        Twist = tilt ? Math.Round(twistValue * 24, 2) : 0;
        GimbalX = Math.Round(rollValue * GimbalRadius, 1);
        GimbalY = Math.Round(pitchValue * GimbalRadius, 1);
        TwistTick = Math.Round(twistValue * 90, 1);
        foreach (HudAxis axis in Bars.Concat(Levers))
        {
            if (axis.Index < axes.Length)
                axis.Value = axes[axis.Index];
        }
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

    public string Percent => Bipolar ? ((int)Math.Round((Value - 0.5) * 200)).ToString("+0;-0;0") : ((int)Math.Round(Value * 100)) + "%";

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(FillLeft));
        OnPropertyChanged(nameof(FillWidth));
        OnPropertyChanged(nameof(LeverFill));
        OnPropertyChanged(nameof(Percent));
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
