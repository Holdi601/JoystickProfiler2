# Device catalog (`DB/Devices`)

The device catalog says what every DirectInput input of a known simulation device physically is, in one shared
vocabulary. [Auto Assign](auto-assign.md) uses it to place functions on the right controls. One JSON file per vendor:

```json
{
  "vendor": "VKB",
  "devices": [
    {
      "id": "vkb-gunfighter-mcg-pro",
      "name": "VKB Gunfighter Modern Combat Grip Pro (MCG Pro, with Twist)",
      "kind": "stick",
      "hand": "right",
      "base": "vkb-gunfighter-base",
      "identify": { "vidPids": ["231D:0125"], "names": ["VKB-Sim Gunfighter Modern Combat Pro Twist"] },
      "image": "https://...",
      "sources": ["https://..."],
      "confidence": "medium",
      "axes": [ { "input": "JOY_X", "control": "stick.axis.roll" } ],
      "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1", "finger": "index", "type": "momentary" } ]
    }
  ]
}
```

- `id`: lower-case slug, unique across all files.
- `kind`: `stick`, `throttle`, `hotas` (stick and throttle on one USB id), `base` (gimbal or FFB base without grip),
  `pedals`, `collective`, `yoke`, `quadrant`, `panel`, `mfd`, `buttonbox`, `controller` (DIY boards), `other`.
- `hand`: `right`, `left`, `either` or `none`.
- `base`: for grips and heads on an exchangeable base. The base has its own entry (`kind: base`) with its own axes
  and buttons. Each grip lists the numbers its controls get on that base.
- `identify`: USB `VID:PID` (hex, as Windows reports it) and HID product names. Devices are matched by VID:PID first,
  then by name. If one product name names a grip exactly, that grip is chosen without asking.
- `image`: the manufacturer's own product picture. It is loaded at run time on the user's PC and never shipped. It is
  only shown when its host is in the file's top-level `officialHosts` (the vendor's own sites and CDNs). See
  [device-reference.md](device-reference.md).
- `productUrl`: the official product page.
- `reference` (optional): `shape` (the drawing family) and `markers` (part id → `[x, y]`, 0..1 of JoyPro's drawing).
  See [device-reference.md](device-reference.md#catalog-fields).
- `sources`: where the numbers come from (manuals, community maps). Facts only, no copied text or pictures.
- `confidence`: `high` (manufacturer manual), `medium` (community map or default profile), `low` (control list without
  verified numbers; such controls have no `input`).
- `input`: `JOY_X JOY_Y JOY_Z JOY_RX JOY_RY JOY_RZ JOY_SLIDER1 JOY_SLIDER2`, `JOY_BTN<n>` (1-based) or
  `JOY_BTN_POV<h>_<U|UR|R|DR|D|DL|L|UL>`.
- `type`: `momentary`, `latched` (a switch position that stays), `rotary` (one encoder step) or `analog`.
- `label`: the printed label of panel switches ("LDG GEAR UP", "ANTI SKID ON"). Auto Assign matches it against the
  function and its direction.
- `finger`: `index`, `middle`, `thumb`, `pinky`, `palm` or `hand` (grip controls only).

## Control vocabulary

`N` counts left to right, top to bottom. Directions are `up|down|left|right|press`; 3-way switches use
`fwd|center|aft` or `up|center|down`.

| Device | Controls |
|---|---|
| Stick | `stick.axis.roll\|pitch\|twist\|brake_lever\|slider\|rotary.N\|ministick.x\|ministick.y\|ministick2.x\|ministick2.y`, `stick.trigger.stage1\|stage2\|flip`, `stick.weapon_release`, `stick.button.thumb.N`, `stick.hat.trim\|tms\|dms\|cms\|N.<dir>`, `stick.button.index_lower`, `stick.button.index.N`, `stick.pinky.lever\|button`, `stick.ministick.press\|<dir>`, `stick.base.button.N`, `stick.base.toggle.N.<up\|down>`, `stick.base.hat.N.<dir>` |
| Throttle | `throttle.axis.main\|left\|right\|slider.N\|rotary.N\|ministick.x\|ministick.y\|ministick2.x\|ministick2.y\|friction\|wheel.N`, `throttle.hat.mic.<up\|down\|fwd\|aft\|press>`, `throttle.speedbrake.<fwd\|aft\|center>`, `throttle.boat.<fwd\|center\|aft>`, `throttle.china.<fwd\|aft>`, `throttle.coolie.<dir>`, `throttle.dogfight.<fwd\|center\|aft>`, `throttle.cage`, `throttle.pinky.<button\|lever\|fwd\|aft>`, `throttle.ministick.press`, `throttle.hat.N.<dir>`, `throttle.button.N`, `throttle.detent.<idle\|afterburner\|cutoff>`, `throttle.base.button.N`, `throttle.base.toggle.N.<up\|center\|down>`, `throttle.base.rotary.N.<cw\|ccw>`, `throttle.base.encoder.N.<cw\|ccw\|press>` |
| Collective | `collective.axis.main\|throttle_twist\|rotary.N`, `collective.hat.N.<dir>`, `collective.button.N`, `collective.switch.N.<pos>` |
| Pedals | `pedals.axis.rudder\|brake_left\|brake_right` |
| Yoke | `yoke.axis.roll\|pitch`, `yoke.hat.N.<dir>`, `yoke.button.N`, `yoke.rocker.N.<up\|down>`, `yoke.switch.N.<pos>` |
| Quadrant | `quadrant.axis.lever.N`, `quadrant.button.N`, `quadrant.toggle.N.<pos>`, `quadrant.detent.N` |
| Panel | `panel.button.N`, `panel.toggle.N.<up\|down>`, `panel.toggle3.N.<up\|center\|down>`, `panel.rotary.N.<cw\|ccw>`, `panel.encoder.N.<cw\|ccw\|press>`, `panel.selector.N.pos.M`, `panel.axis.N`, `panel.lever.gear.<up\|down>`, `panel.lever.flaps.<up\|mid\|down>`, `panel.key.<label>` |
| MFD | `mfd.<left\|right\|center>.osb.<T1..T5\|B1..B5\|L1..L5\|R1..R5>`, `mfd.<side>.rocker.<name>.<up\|down>` |

`JOYPRO_DEVICE_KB` points the engine to another catalog folder (tests). The user's own definitions in
`<data>\Devices\*.json` (made with the marker assistant or imported, same format) are read after the catalog and
replace a shipped model with the same id.

## Replace a device (`engine devices replace`, `devices auto-remap`)

JoyPro 1's "replace entire joystick device". Code: `engine/EngineDeviceReplace.cs`; GUI:
`MainWindowViewModel.DeviceReplace.cs`, `Views/ReplaceDeviceWindow.axaml`.

- **New ID, same device** (new PC, Windows reinstall, other USB port): `devices auto-remap [--dry-run]` gives a profile
  device that is not connected the connected device of the same product: same name (letters and digits) or the USB id
  the profile remembers (`JoysticksPGuids`), not used in the profile yet. Several connected devices of the kind (a
  Wooting keyboard's game controller interfaces) are told apart by the inputs the profile uses (axes, highest button,
  hats); two old devices for one new one, or two that still fit, are left to Replace device. Never another product.
  The GUI runs it when a profile opens (Engine options "Remap Missing Devices") and shows what moved with Undo. The load
  correction of profiles saved with another device list (`--remap-missing-devices`) uses the same rules; before, it
  took the most similar name among all connected devices, even another product.
- **Replace**: `devices replace <old> <new> [--mode auto|same|layout] [--map OLD=NEW]... [--merge add|keep|replace]
  [--grip <device>=<grip id>]... [--dry-run]`. `auto` = `same` for the same product (same name, USB id or catalog
  model), else `layout`. By layout, per input of the old device: the same control id (`stick.trigger.stage1`), the same
  id on the other family's device, then each old part goes to one new part of its kind (hat, trigger, lever, brake,
  ministick, twist ...; the same place in the order first, then the one that has most of its directions; a four-way
  ministick that reports buttons counts as a hat; POV hats the catalog does not list are hats of their own), buttons in
  their order as a guess, and last the same input id. Axes only go to axes. A device that is not connected is
  identified by its name (also "Joystick - HOTAS Warthog" for the catalog's "HOTAS Warthog Joystick") and its inputs
  come from the profile; unknown devices use the generic layout. Inputs without a target stay on the old device.
  Modifiers follow their input. When nothing is left on the old device, its name, force feedback and activity follow
  (picture and USB id only for the same product). The new device already has binds: `--merge` is required (`add` both
  stay, `keep` the new device's win and the old bind stays on the old device, `replace` the old device's win and the
  new device's binds on those inputs are removed). The preview (`--dry-run`, JSON) lists every input with its part,
  method, relations, modifiers and the new device's relations on the same input.
- Checked on the user's old-PC profile (copy): auto-remap moved the same five devices the user had relinked by hand,
  and the result (502 binds, reformers included) equals that hand-made profile.