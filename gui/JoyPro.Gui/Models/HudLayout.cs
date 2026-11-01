using System.Collections.Generic;

namespace JoyPro.Gui.Models;

// The HUD the user designed (HUD page), saved as %APPDATA%\JoyPro2\hud.json. Positions are in a 1920 x 1080 reference
// screen; the HUD scales them to the real screen.
public sealed class HudLayout
{
    public const double ReferenceWidth = 1920;
    public const double ReferenceHeight = 1080;

    public List<HudWidgetConfig> Widgets { get; set; } = [];

    // 2.5.0: accent colour, style name (Glass, Neon, Minimal) and opacity; since 2.5.1 in Look (read once, then kept in
    // step so an older JoyPro still finds them).
    public string Accent { get; set; } = "#38BDF8";

    public string Style { get; set; } = "Glass";

    public double Opacity { get; set; } = 1;

    // The design of the whole HUD (docs/hud.md "Styles"); null in a 2.5.0 file (made from Style, Accent, Opacity).
    public HudLookSettings? Look { get; set; }

    // The user's own styles (Save style), by name.
    public List<HudLookSettings> UserStyles { get; set; } = [];

    // Outputs (docs/hud.md "Streamer mode"): the on-screen HUD over the game and, independently, the stream outputs.
    public bool OnScreen { get; set; } = true;

    // The local page for OBS's Browser Source (http://127.0.0.1:<StreamPort>/hud, this PC only).
    public bool StreamBrowser { get; set; }

    public int StreamPort { get; set; } = 8765;

    // A window of its own for OBS's Window Capture, not on top (it may sit behind the game).
    public bool StreamWindow { get; set; }

    // The stream window's background: Transparent (per-pixel alpha) or a chroma key colour (#RRGGBB).
    public string StreamBackground { get; set; } = "Transparent";

    // Live updates of the HUD per second at most (on screen and stream).
    public int MaxFps { get; set; } = 30;

    // The designer's preview shows the stream's widgets ("The stream") instead of the screen's ("My screen").
    public bool PreviewStream { get; set; }
}

public sealed class HudWidgetConfig
{
    public string Id { get; set; } = string.Empty;

    // device, binds (what the pressed button does), header (game and aircraft) or aircraft (its stick and throttle).
    public string Kind { get; set; } = "device";

    // Device widgets: the device as JoyPro names it ("Name {GUID}").
    public string Device { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1;

    // Shown on the on-screen HUD.
    public bool Visible { get; set; } = true;

    // Shown in the stream outputs (Browser Source, stream window); null in a 2.5.0 file = as Visible.
    public bool? StreamVisible { get; set; }

    // A style of its own (a preset or own style; null = the HUD's style).
    public HudLookSettings? Look { get; set; }

    public bool ShowName { get; set; } = true;

    public bool ShowPicture { get; set; } = true;

    // The picture tilts and twists with the stick's axes.
    public bool Tilt { get; set; } = true;

    // Pressed buttons light up on the picture (where JoyPro knows their position) or in a button grid.
    public bool ShowButtons { get; set; } = true;

    public bool ShowAxes { get; set; } = true;

    // Stick position as a dot in a ring, twist as an arc.
    public bool ShowGimbal { get; set; } = true;

    public bool ShowHats { get; set; } = true;

    // The held buttons by name under the widget.
    public bool ShowChips { get; set; } = true;

    // The numbers next to bars and levers ("+42", "80%") and their names.
    public bool ShowValues { get; set; } = true;

    public bool ShowLabels { get; set; } = true;

    // The kind next to the name ("stick", "throttle").
    public bool ShowKind { get; set; } = true;

    // Header: the game's or aircraft's picture.
    public bool ShowHeaderPicture { get; set; } = true;

    // Aircraft widget (kind "aircraft"): the aircraft's own stick and throttle from the game (docs/hud.md).
    public bool ShowStick { get; set; } = true;

    public bool ShowThrottle { get; set; } = true;

    // Empty = the aircraft JoyPro knows (DCS aircraft choice or the DCS hook), else "<game>/<picture set>".
    public string Aircraft { get; set; } = string.Empty;

    // Where the aircraft's stick and throttle move from: empty = Automatic (the right-hand stick, the throttle), else a
    // device ("Name {GUID}").
    public string StickDevice { get; set; } = string.Empty;

    public string ThrottleDevice { get; set; } = string.Empty;

    // Auto (product photo, else layout picture), Photo or Layout.
    public string PictureSource { get; set; } = "Auto";

    // The grip on a base that takes several (catalog id), as for Auto Assign.
    public string Grip { get; set; } = string.Empty;

    // Where the stick element's axes come from: empty = Automatic (the device's own axis; a grip without X/Y takes them
    // from the base it sits on), else "<device>|<input>" ("VPforce Rhino FFB Joystick {GUID}|JOY_X").
    public string AxisX { get; set; } = string.Empty;

    public string AxisY { get; set; } = string.Empty;

    public string AxisTwist { get; set; } = string.Empty;
}
