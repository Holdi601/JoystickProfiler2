# Auto Assign

Auto Assign binds your relations to the physical controls of your devices. It works for any setup: one stick or a
full pit, mixed vendors, panels, pedals, a second stick. It runs without AI. A device catalog says what every input of
a known device physically is (trigger, TMS hat, gear lever ...), and the function catalog says where each function
belongs in a real cockpit.

- CLI: `engine relations auto-assign [--grip <device>=<grip id>]... [--add-device <catalog id>]... [--ignore-device <device>]... [--relation <name>]... [--max-modifiers 0|1|2] [--overwrite [--include-missing]] [--no-style] [--explain <relation>]... [--dry-run]`
- Devices: `engine devices kb identify` shows how your devices were recognised; `engine devices kb list [query]` and
  `engine devices kb show <id>` browse the catalog.

Run it with `--dry-run` first: it prints every assignment and changes nothing.

## Your devices

- **Recognised devices** are matched by USB VID:PID and product name against `DB/Devices` (about 320 sim devices:
  VKB, Virpil, Thrustmaster, WinWing, Logitech/Saitek, VPforce, MOZA, CH, Honeycomb, Turtle Beach, Brunner,
  RealSimulator, pedals, DIY boards ...).
- **Bases with exchangeable grips** (Gunfighter, Rhino, WarBRD ...) report the same USB id for every grip. If the
  product name does not name the grip, JoyPro lists the grips with their pictures and you pick one:
  `--grip "VPforce Rhino FFB Joystick=vpforce-rhino-tm-f16-grip"`.
- **Unknown devices** get a generic layout from what Windows reports (axes, buttons, hats). Their kind is guessed from
  the name; keyboards, wheels and gamepads are left out.
- **Devices that are not connected** can be added from the catalog with `--add-device <id>`. Their binds use a
  placeholder device; exchange it for the real device once it is connected.
- **One device per hand.** A relation has one bind, so a second right-hand stick (for example a Rhino next to a VKB) is
  left out as an alternative. The device your profile already uses wins, then devices you added, known layouts, a
  throttle over a left-hand stick. Use `--ignore-device` on the other one to switch.

## How functions are placed

1. **Your own layout first.** With `--overwrite`, every relation goes back on the control it had before (same
   product, same input, same shift state), also after Windows gave the device a new id. `--no-style` ignores it.
2. **Strong matches.** A function goes to its own position: TMS up on the TMS hat, pickle on the weapon release, gear
   on the gear lever, ANTI SKID on the switch labelled ANTI SKID. Labels also decide the direction (LAUNCH BAR EXTEND).
3. **Groups stay together.** The four directions and the press of TMS, DMS, CMS, trim, slew, sensor select, weapon
   select and comm go on one hat. Slew and TDC X/Y go on one thumb stick. On/off pairs (gear, master arm, hook,
   launch bar ...) go on both positions of one toggle switch.
4. **Everything else** goes where it fits best. The function placed first is the one with the fewest good options.
   Frequent functions go on the grips, rare ones on the base or panels.

Rules that always hold:

- Trigger, weapon release, pinky and detents only take functions made for them.
- Flight axes and self-centring thumb sticks only take the axes made for them (no RPM on a ministick).
- MFD keys only go on MFD keys, and cockpit panel functions stay off the grips.
- **Inputs are shared between aircraft.** One input serves several relations as long as they have no aircraft in
  common, for example the left throttle of the F/A-18 and of the MiG-29. DCS input layers (UI, NS430, voice) count for
  every DCS aircraft. Every other game has one input configuration for the whole game.
- **At most two modifier layers**, and only if the busiest aircraft has more grip functions than grip buttons. The
  shift button your profile already uses comes first, then the pinky lever or button.

## What it changes

- **Bound relations** keep their binds; only unbound relations are assigned. `--relation` limits it to some relations.
- **`--overwrite`** re-lays out the binds on the devices of this setup. Binds on the keyboard, virtual devices (vJoy)
  and devices that are not connected stay. `--include-missing` also moves the binds of devices that are not connected
  (a replaced device).
- **"Not Assigned"** lists functions with no fitting free input (for example MFD keys without an MFD).
- **`--explain <relation>`** shows the slot of a relation, its previous control and the best candidates with their
  score and the relations of the same aircraft that already use them.

## Devices without published button numbers

For many configurable devices (Virpil, WinWing, MOZA grips, Rhino grips) the button numbers depend on the firmware
profile and are not published; their catalog entries have `confidence: low`. Auto Assign still uses the inputs Windows
reports, and your previous binds tell it where things are. A guided "press the control" teach mode is planned.

## Where the data comes from

- `DB/Devices/*.json`: the device catalog, one file per vendor (format: [devices.md](devices.md)).
- `DB/Functions/slot_controls.tsv`: for every function slot of the Auto Group catalog, its side (stick, throttle,
  panel), the controls it belongs on in order of preference, latched pairs (`gear:in`), panel label keywords, how
  often it is used, and the hat or 2D group it belongs to (`tms:up`, `tdc_axes:x`).

To set up the relations and binds for the games you watch in one go (device roles, aircraft priorities, modifiers, realism, review), use the [Setup wizard](setup-wizard.md).
