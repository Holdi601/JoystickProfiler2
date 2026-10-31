using System.Collections.Generic;

namespace JoyPro.Gui.Models;

// The HUD the user designed (HUD page), saved as %APPDATA%\JoyPro2\hud.json. Positions are in a 1920 x 1080 reference
// screen; the HUD scales them to the real screen.
public sealed class HudLayout
{
    public const double ReferenceWidth = 1920;
    public const double ReferenceHeight = 1080;

    public List<HudWidgetConfig> Widgets { get; set; } = [];

    // Accent colour of pressed buttons, gimbal dots and lines (#RRGGBB).
    public string Accent { get; set; } = "#38BDF8";

    // Glass (dark frosted cards), Neon (no cards, glowing outlines) or Minimal (no cards, no glow).
    public string Style { get; set; } = "Glass";

    public double Opacity { get; set; } = 1;

    // A normal window with a plain background instead of the transparent on-screen HUD (for OBS window capture).
    public bool StreamWindow { get; set; }

    public string StreamBackground { get; set; } = "#00FF00";
}

public sealed class HudWidgetConfig
{
    public string Id { get; set; } = string.Empty;

    // device, binds (what the pressed button does) or header (game and aircraft).
    public string Kind { get; set; } = "device";

    // Device widgets: the device as JoyPro names it ("Name {GUID}").
    public string Device { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1;

    public bool Visible { get; set; } = true;

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
