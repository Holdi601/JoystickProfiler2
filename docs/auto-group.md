# Auto Group

Auto Group puts the same function of every DCS aircraft and every other supported game into one relation. For
example, "CMS forward" (dispense) of the F-16, the F/A-18, the A-10C, Falcon BMS, Star Citizen and War Thunder goes
into one relation, so one button does it in every aircraft and game.

Games with a function catalog: DCS (all modules and input layers), Falcon BMS, IL-2 Great Battles, IL-2 Korea, Star Citizen
(ship controls), X4: Foundations, Elite Dangerous, MSFS 2020 and 2024, X-Plane 11 and 12, War Thunder, Arma 3, Arma Reforger and
WARDOGS. `--planes` also takes a game id (`StarCitizen`, `X4`, `EliteDangerous`, `IL2Game`, `IL2Korea`, `FalconBMS`, `MSFS2020`,
`MSFS2024`, `XPlane11`, `XPlane12`, `WarThunder`, `Arma3`, `ArmaReforger`, `Wardogs`).

- GUI: Relations → **Group all my actions automatically...** → **By function (Auto Group)** (Preview first, then Apply).
- To group only some functions or aircraft, describe them in a sentence: [Group by description](natural-language-grouping.md).
- To build one relation step by step (one action, then what fits with one click), use the
  [relation editor](relation-builder.md); its suggestions use the same catalog and your learned style.
- CLI: `engine relations auto-group [--preset all|wing|era|purist] [--no-substitutes] [--planes p1|p2] [--replace] [--dry-run] [--no-map]`.

## Presets

| Preset | Relations |
|---|---|
| `all` | One relation per function for all aircraft (default). |
| `wing` | Split into fixed wing and helicopters. |
| `era` | Split by wing type and era (WW2, Korea, early/late Cold War, modern); spaceships form their own group. |
| `purist` | Only the basic flight controls are shared (axes, trigger, trim, gear, flaps, brakes, canopy, kneeboard). Every other function gets one relation per aircraft. |

## What it changes

- **Existing relations** of the same name (or the relation that stands for the function in your profile, whatever
  its name) keep their binds and only get the aircraft they are missing.
- **Commands you already use** in another relation stay there. They are listed under "Kept", so a bind is never moved
  behind your back.
- **Your own relations** (not tagged `AutoGroup`) get only what is their function: an axis that only the function
  catalog reads as it, its title without any of the function's words (a gunner seat's "Gun Up/Down" for pitch), stays
  out and is listed under "Kept" (`KeptOutOfOwnRelations`). Star Citizen's catalog spelling `map___action` is the
  profile's `map|action`, so an action already in the relation is never added twice.
- **New relations** get the group `AutoGroup` and a readable name ("Anti Skid Off", "AI menu down", not the catalog's
  `AntiSkidOff` / `ai_menu_down`; a relation of the catalog's spelling from an earlier run keeps being used).
  `--replace` removes those first. Your own relations are never removed.
- **In the GUI** the preview is a list (new and extended relations with their actions and aircraft, what was kept) and
  `Apply` asks first with the counts.
- **Disabled entries**: a command you switched off for an aircraft stays off ("Kept Disabled By You").
- **Unknown commands**: commands or aircraft the local DCS database does not know yet are skipped. Refresh the DCS
  database to include them.
- **Input layers**: the catalog also covers the layers that are not aircraft: `Default`, the `NS430` GPS,
  Combined Arms `ground_unit` and the VoIP push-to-talk layers (`VoiceChat`, `RadioVoIP`). They are shared by all
  aircraft, so the wing and era presets never split them.

## Where the functions come from

`DB/Functions` holds the function catalog:
- `slots.json`: the functions, their names and kinds.
- `aircraft.json`: wing type, era and variants.
- `<Game>.tsv` (`DCS.tsv`, `StarCitizen.tsv`, `FalconBMS.tsv` ...): aircraft (or game), function, command, status,
  HOTAS position, frequency and control.
- `<Game>.rejected.tsv`: old profile entries the manuals or game docs proved wrong.

Every aircraft was checked against its own manual. The catalog is shipped with updates, so new aircraft arrive
through the updater; no AI is involved at runtime.

What the catalog does not list yet (a new DCS module, a game without a `<Game>.tsv` such as IL-2 1946, Cliffs of Dover,
FSX / Prepar3D or Tiny Combat Arena, or an action a catalog aircraft lacks) comes from the
[function map](relation-builder.md#function-map): an action whose title clearly names a function (full weight, the
function's own words) goes to the slot the catalog uses most for that function and direction, so it follows your style;
only where the aircraft has no catalog row for that slot, and one action per aircraft and slot. The preview lists
these under `Function Map` with the aircraft and games they brought in. `--no-map` groups from the catalog alone;
`autoGroupIgnore` in `function-map.json` names aircraft left to the catalog (A-4E-C, the Hercules mod).

## Free slots and your style

If an aircraft lacks a function, Auto Group can fill the slot with a substitute (on by default, off with
`--no-substitutes`). It picks the substitute in this order:

1. **Your habit.** It looks at what you put on that function in other aircraft. It needs at least two separate
   decisions; entries the manuals rejected and kneeboard placeholders do not count. The preview lists these under
   "Learned From Your Profile".
2. **The catalog's substitute.** This is a function at the same physical position.

These rules always apply:
- Slew directions (TDC, cursor) never stand in for TMS, DMS or coolie slots. They go into "TDC Slew Up/Down/Left/Right".
- The press a slew needs goes into "TDC Depress / Designate".
- A substitute never repeats a command the aircraft already has in its own slot.
- MFD bezel buttons, keypad keys and aircraft-specific slots only take that aircraft's own commands.
