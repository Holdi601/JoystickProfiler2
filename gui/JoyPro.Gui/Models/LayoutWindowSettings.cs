namespace JoyPro.Gui.Models;

// The layout window (ViewModels/LayoutWindowViewModel.cs, ui-settings.json "layoutWindow"): where it was, how big, and
// what it showed; every one survives a restart.
public sealed class LayoutWindowSettings
{
    public string Game { get; set; } = string.Empty;
    public string Aircraft { get; set; } = string.Empty;
    // "" = every device, else the device's name (without its instance id).
    public string Device { get; set; } = string.Empty;
    // Light up the control that is pressed or moved.
    public bool LightUp { get; set; } = true;
    // Switch game and aircraft to the running game (DCS's hook, the running game's process).
    public bool FollowGame { get; set; }
    // Position (screen pixels), size (DIPs), maximized, and the monitor it was on (its bounds, "x,y,w,h").
    public int? X { get; set; }
    public int? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public string Screen { get; set; } = string.Empty;
    // Open when JoyPro closed: it opens again at the next start.
    public bool WasOpen { get; set; }
}
