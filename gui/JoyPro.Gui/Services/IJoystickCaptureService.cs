using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services;

public interface IJoystickCaptureService
{
    string LastDiagnostics { get; }

    Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(
        JoystickCaptureService.CaptureOptions options,
        CancellationToken cancellationToken);

    // Reports every button / hat direction of every connected device (and the keyboard modifier keys) going down or
    // up until cancelled, and (onAxes) the axes of a device whenever they moved; for the overlay and the HUD. Runs in
    // the background, also while a game has the focus.
    Task MonitorAsync(System.Action<JoystickInputEvent> onEvent, System.Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken);
}

// A button, hat direction ("JOY_BTN_POV1_U") or keyboard modifier ("LCtrl") of a device going down or up.
// FirstReport: the change came with the device's first report after it was opened (DirectInput showed its empty
// defaults until then), so a button "going down" may be one that rests pressed, not a press of the user.
public sealed record JoystickInputEvent(string Device, string Input, bool Down, bool FirstReport = false);

// The 8 axes of a device in DIJOYSTATE2 order (X, Y, Z, Rx, Ry, Rz, slider 1, slider 2), each 0..1.
// FirstReport: the axes jumped from DirectInput's defaults to where they really are, nobody moved them.
public sealed record JoystickAxisSnapshot(string Device, double[] Axes, bool FirstReport = false);
