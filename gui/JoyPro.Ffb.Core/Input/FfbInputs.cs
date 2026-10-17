using System.Globalization;
using System.Text.RegularExpressions;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Input;

/// <summary>Buttons, hat switches and the force feedback stick's own position for the input nodes of a graph.</summary>
/// <remarks>
/// Devices are named as JoyPro's "press the button now" capture names them ("Name {instance guid}", or just the name);
/// "" means the force feedback device itself. <see cref="Poll"/> is called once per tick, before the graph, on the force
/// loop thread; the Try methods then answer from that snapshot. A device that is missing reads as released / centred
/// (false), never as pressed.
/// </remarks>
public interface IFfbInputs
{
    /// <summary>Reads every device the graph asked for once (called by the runtime before each evaluation).</summary>
    void Poll();

    /// <summary>Whether a button or hat direction is held. False when the device or the input is unknown.</summary>
    bool TryGetButton(string device, FfbInputRef input, out bool pressed);

    /// <summary>The stick position on an axis, -1..1, as DirectInput reports it: right and pulled back (towards the pilot)
    /// are positive, which is JoyPro's force convention. The force invert of the device does not apply to positions.
    /// False while it cannot be read (e.g. the force feedback device is not acquired).</summary>
    bool TryGetStick(string device, FfbAxis axis, out double position);
}

/// <summary>What an input node reads: a button (0-based) or a hat direction.</summary>
public enum FfbInputKind
{
    None,
    Button,
    Hat
}

public enum FfbHatDirection
{
    Up = 0,
    UpRight = 4500,
    Right = 9000,
    DownRight = 13500,
    Down = 18000,
    DownLeft = 22500,
    Left = 27000,
    UpLeft = 31500
}

/// <summary>A parsed input name in JoyPro's (DCS) notation: "JOY_BTN5" (button 5 = index 4), "JOY_BTN_POV1_U" (hat 1 up).</summary>
public readonly record struct FfbInputRef(FfbInputKind Kind, int Index, FfbHatDirection Direction = FfbHatDirection.Up)
{
    public static readonly FfbInputRef None = default;

    private static readonly Regex ButtonPattern = new(@"^JOY_BTN(\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HatPattern = new(@"^JOY_BTN_POV(\d)_(U|UR|R|DR|D|DL|L|UL)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool IsNone => Kind == FfbInputKind.None;

    /// <summary>"JOY_BTN5", "JOY_BTN_POV1_U"; anything else (also "") is <see cref="None"/>.</summary>
    public static FfbInputRef Parse(string? text)
    {
        var value = (text ?? "").Trim();
        var button = ButtonPattern.Match(value);
        if (button.Success && int.TryParse(button.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 128)
            return new FfbInputRef(FfbInputKind.Button, number - 1);
        var hat = HatPattern.Match(value);
        if (hat.Success && int.TryParse(hat.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pov) && pov is >= 1 and <= 4)
            return new FfbInputRef(FfbInputKind.Hat, pov - 1, hat.Groups[2].Value.ToUpperInvariant() switch
            {
                "UR" => FfbHatDirection.UpRight,
                "R" => FfbHatDirection.Right,
                "DR" => FfbHatDirection.DownRight,
                "D" => FfbHatDirection.Down,
                "DL" => FfbHatDirection.DownLeft,
                "L" => FfbHatDirection.Left,
                "UL" => FfbHatDirection.UpLeft,
                _ => FfbHatDirection.Up
            });
        return None;
    }

    public override string ToString() => Kind switch
    {
        FfbInputKind.Button => "JOY_BTN" + (Index + 1).ToString(CultureInfo.InvariantCulture),
        FfbInputKind.Hat => "JOY_BTN_POV" + (Index + 1).ToString(CultureInfo.InvariantCulture) + "_" + Direction switch
        {
            FfbHatDirection.UpRight => "UR",
            FfbHatDirection.Right => "R",
            FfbHatDirection.DownRight => "DR",
            FfbHatDirection.Down => "D",
            FfbHatDirection.DownLeft => "DL",
            FfbHatDirection.Left => "L",
            FfbHatDirection.UpLeft => "UL",
            _ => "U"
        },
        _ => ""
    };

    /// <summary>Whether a hat at <paramref name="hundredths"/> of a degree (DirectInput POV; -1 or &gt;= 36000 = centred)
    /// counts as this direction. A straight direction also matches its two diagonals (a trim hat pressed slightly off
    /// still trims), a diagonal only itself.</summary>
    public bool HatMatches(int hundredths)
    {
        if (Kind != FfbInputKind.Hat || hundredths < 0 || hundredths >= 36000)
            return false;
        var target = (int)Direction;
        var distance = Math.Abs(hundredths - target) % 36000;
        if (distance > 18000)
            distance = 36000 - distance;
        var straight = target % 9000 == 0;
        return distance <= (straight ? 4500 : 2250);
    }
}

/// <summary>Matches a device setting ("Name {guid}", "{guid}", "Name") against a device's instance guid and names.</summary>
public static class FfbDeviceMatch
{
    private static readonly Regex GuidPattern = new(@"\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?", RegexOptions.CultureInvariant);

    /// <summary>The instance guid inside a device setting, if it names one.</summary>
    public static Guid? GuidOf(string? setting)
    {
        var match = GuidPattern.Match(setting ?? "");
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var guid) ? guid : null;
    }

    /// <summary>The name part of "Name {guid}" (or the whole text).</summary>
    public static string NameOf(string? setting)
    {
        var text = setting ?? "";
        var match = GuidPattern.Match(text);
        return (match.Success ? text.Remove(match.Index, match.Length) : text).Trim();
    }

    /// <summary>True when the setting names this device: by guid when it has one, else by instance or product name.</summary>
    public static bool Matches(string? setting, Guid instance, string instanceName, string productName)
    {
        if (GuidOf(setting) is { } guid)
            return guid == instance;
        var name = NameOf(setting);
        return name.Length > 0 &&
               (string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, productName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Inputs for tests and the editor preview: values set by hand.</summary>
public sealed class FakeFfbInputs : IFfbInputs
{
    private readonly Dictionary<(string, string), bool> _buttons = new();
    private readonly Dictionary<(string, FfbAxis), double> _stick = new();

    public int Polls { get; private set; }

    public void Poll() => Polls++;

    public void SetButton(string device, string input, bool pressed) => _buttons[(device, FfbInputRef.Parse(input).ToString())] = pressed;

    public void SetStick(string device, FfbAxis axis, double position) => _stick[(device, axis)] = position;

    public void ClearStick() => _stick.Clear();

    public bool TryGetButton(string device, FfbInputRef input, out bool pressed)
    {
        pressed = false;
        if (input.IsNone || !_buttons.TryGetValue((device, input.ToString()), out var value))
            return false;
        pressed = value;
        return true;
    }

    public bool TryGetStick(string device, FfbAxis axis, out double position) => _stick.TryGetValue((device, axis), out position);
}
