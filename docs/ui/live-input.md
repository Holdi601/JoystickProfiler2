# Live input: Record, Follow input, live highlight

## Record takes transitions only

Record (Bind Studio, modifiers, the marker assistant, the FFB graph editor) takes a button or hat that changes state,
never one that was already pressed when recording started. Many devices have buttons that rest pressed: the middle of a
3-way switch, a mode switch, VPC throttles' detents (the left MT-50 on a test PC rests on 10 buttons, the WinWing F18
takeoff panel on 9).

- `Services/RecordButtonTracker.cs` decides per device. Buttons pressed during the warmup are "held at start" and only
  count after being released and pressed again.
- DirectInput shows a device's empty defaults (nothing pressed) when it is opened, until the device's first report
  (measured: most devices report within 0-50 ms; a device that only reports on change, like vJoy, not until touched).
  A button that appears with that first report may be a resting one, so it counts once it is released again; a
  resting button never is. The old capture took the lowest new button of the first report: the resting one.
- Settings > Binds / Bind Studio > Recording settings: "Also take a button that rests pressed when it is released"
  (off by default; `RecordReleaseOfHeldButtons` in `ui-settings.json`).
- Axes were already measured from the baseline at arming, with the first report re-centring jumped axes
  (`RebaseJumpedAxes`); unchanged.

## Follow input (Binds and Relations pages)

On by default; the "Follow input" chip in Bind Studio's header, the cursor chip in the Relations list's header and
Settings > Binds switch it (`FollowInputOnBinds`, one switch for both pages). On the Relations page the jump selects
(and opens) the relation's card, in the order of the relation list; the same rules and the same note.
Pressing a button or hat selects the relation bound to it; pressing it again selects the next relation on the same
input: the ones without modifiers first, then the ones with, each in list order. An axis moved more than 40 % of its
travel selects its relation; every further large move (at least 350 ms apart) steps on, until the axis stops. A note
over the list says what happened: "BTN 8 on L-VPC Stick WarBRD: Gear (1/3)". A relation hidden by the search is shown by
clearing the search. No jumping while Record runs, a text box has the focus, or the picture (Visual Assign) is open.
Logic: `Services/FollowInputTracker.cs` (debounce 120 ms; a press in a device's first report counts only when released
within 1.5 s), `MainWindowViewModel.LiveInput.cs`.

A jump moves the list under a mouse that did not move; the card that slides under the pointer must not pop its tooltip.
The VM raises `FollowInputActivity` for every input follow input reacts to (each press, each axis step, also a large
axis move too soon for a step), besides `FollowInputJumped` for real jumps. The window (`Views/TooltipHold.cs`) then
arms its hold again: tooltips off (`ToolTip.ServiceEnabled`, inherited), an open one closed, and a guard closes any
tooltip that still opens (a show timer started before the hold). Only real mouse movement releases it: a screen-space
path of at least 60 px (device-independent, counted in steps of 4 px so jitter and synthetic moves at the same place
never add up) that got at least 16 px away. Clicks, the wheel and keyboard focus do not release it. After the release
the normal show delay applies. An inactive window never shows tooltips; becoming active arms the hold again.

## Live highlight (Visual Assign, Layout page)

While a device picture is on screen, the shown device's held inputs light up: the marker pulses amber (with its leader
line and label), the row in the input lists, the layout file's connector line, and on the callout sheet the row and its
table's line. A moving axis shows a small bar with its position under its marker (a gimbal shows X and Y). State:
`Models/LiveInputState.cs` (`WorkspaceLiveInput`), drawn by `DeviceMarkerCanvas` / `CalloutSheetCanvas` (`LiveInput`
property; a 50 ms pulse timer runs only while something is held or moving).

## One monitor, low cost

`JoystickCaptureService.MonitorAsync` (15 ms DirectInput polls) runs while the Binds page (follow input on) or a device
picture is on screen, and stops when the page is left, the window is minimized or the feature is switched off. Events
carry `FirstReport` so consumers can tell a device's first report from a press.

## Tests

`RecordButtonTrackerTests` (a fake device with resting buttons), `FollowInputTests`, `LiveInputRenderTests` (the icon
journey always; `JOYPRO_LIVE_OUT=<folder>` renders the live pages at 2267x1250 and 1280x800), `LiveInputVJoyTests`
(`JOYPRO_RECORD_VJOY=1`: real DirectInput with vJoy device 1 fed by the test; never configures vJoy),
`CaptureProbeTests` (`JOYPRO_CAPTURE_PROBE=<file>`: which buttons each connected device reports at open and later).
