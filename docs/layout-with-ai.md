# Lay out with AI

Say in your own words how you like your controls, and JoyPro lays out your binds, axis curves, force feedback and the
virtual joystick to match. Before anything changes, you see every change and can untick what you do not want. Apply
saves all of it as one step, and Undo takes it back. Your preferences are kept in the profile and used again the next
time.

It works offline. JoyPro's phrase rules read the words, and the planner of Auto Assign and the Setup wizard
([auto-assign.md](auto-assign.md), [setup-wizard.md](setup-wizard.md)) makes the layout. The local AI model
([natural-language-grouping.md](natural-language-grouping.md)) only helps JoyPro understand. When it is switched on,
it turns sentences the rules do not understand into phrases they do, including sentences in other languages. It never
changes anything itself.

- GUI: Binds > **Lay out with AI...**
- CLI: `engine arrange ask | apply | prefs` (see [CLI](#cli) below)
- Code: `engine/SetupWizard/LayoutPhrases.cs` (the phrase rules, plain C#, linked into the GUI's tests),
  `engine/EngineLayout.cs` (preview, changes, apply, the preferences in the profile, the AI step),
  `gui/JoyPro.Gui/ViewModels/MainWindowViewModel.LayoutWithAi.cs`

## What JoyPro understands

Clauses are split at sentence ends, commas and semicolons, and each clause is read on its own. "Helis",
"helicopters", "space games", "jets" and game names ("in DCS", "for Star Citizen") set the scope of a clause.

| You say | JoyPro does |
|---|---|
| throttle and left-hand stick | Both left-hand devices take part: the throttle in aircraft and helicopters, the left-hand stick in space games. If one of them is missing, JoyPro says so. |
| use the left stick instead of the throttle | The left-hand stick is the left hand in every game, and the throttle is left out. |
| for space games use the left stick | Space translation and the ship's buttons go on the left-hand stick. |
| for space games use the left stick and its twist axis instead of pedals | Space yaw goes on the left stick's twist. The up/down translation moves off the twist, to a slider or mini-stick. |
| no pedals / without pedals | The pedals are left out, and rudder / yaw goes on the stick's twist. |
| only stick and throttle | Every other flight device is left out. |
| trim on the hat, countermeasures on the throttle hat, gear on the panel | The function goes on that kind of control (on that device, if one is named). |
| no countermeasures on the throttle hat | The function never goes there. |
| curves softer for helis / harder for jets / linear pitch in DCS / bigger dead zone | Axis tuning (see [Axis curves](#axis-curves-force-feedback-vjoy)). "much" or "a bit" make the step bigger or smaller. |
| softer ffb for helis, less ffb shake, no ffb in IL-2 | Force feedback settings. |
| vjoy for Star Citizen, native for DCS, xbox pad for X | The game's virtual joystick mode. |
| no modifiers / one modifier / two modifiers / as few modifiers as possible | The modifier budget. "No modifiers" is a rule: JoyPro adds none, even when the buttons are too few. |
| use a mode switch / no mode switch | Phase layers on a latched switch ([auto-assign.md](auto-assign.md#phase-layers)). |
| like the real aircraft / same button everywhere | Realism 100 % or 0 %. |
| keep my layout / from scratch | Only unbound relations get a place, or every bind is laid out again. |
| the F-16 first / mostly the Hornet | That aircraft heads the priority list. |

Anything else is listed under "Not understood". Say it more simply, or switch on the local AI.

**German.** The same wishes can be said in German: "Gas und linker Stick", "bei Weltraumspielen linker Stick mit Twist
statt Pedalen", "Trimmung auf den Hat", "weichere Kurven für Helis", "keine Modifier, nimm einen Moduswahlschalter",
"Totzone etwas größer", "kein Force Feedback in DCS", "die F-16 zuerst". The German words (devices, controls,
functions, aircraft classes, curves, force feedback, modifiers, the mode switch and the small words like statt, ohne,
kein, auf den) are read as their English meaning (`LayoutPhrases.GermanWords`), so a German text gives exactly the
preferences of its English text. The German test set is `engine/tests/fixtures/phrases/layout-de.json`.

## How the layout is made

1. **Your layout first.** Only these relations move:
   - relations a preference concerns, by function and aircraft class;
   - relations on a device whose role changed;
   - unbound relations, which get a place.

   Every other bind stays as it is and keeps its input. "from scratch" lays out all binds again; your previous
   controls still count there.
2. **A wish beats the previous control.**
   - A function you placed on a control gets a strong bonus there, larger than its previous control.
   - It is placed before the others.
   - A "never" is a hard rule.
3. **One relation for several classes.** Your profile may have one relation, such as "Rudder", that serves jets,
   helicopters and Star Citizen. A preference for one class, such as yaw on the left twist in space games, then splits
   that class's commands into a relation of their own, "Rudder [Spaceships]", and places it. Only whole commands move:
   a DCS command that serves aircraft of two classes stays where it is, and a note says so.
4. **The rules of Auto Assign hold**, checked as in the [combination matrix](auto-assign.md#combination-matrix):
   - inputs are shared only between aircraft that never fly at the same time;
   - trigger and pinky take only their own functions;
   - MFD keys and printed panel labels take only their own functions;
   - slew is not TMS;
   - one device per hand.
5. **A relation that moves keeps its axis tuning** (base curve and per game / aircraft tuning).

### Axis curves, force feedback, vJoy

- **Curves.**
  - For a class or a game, the tuning goes into each aircraft's own scope (`plane:DCS:<aircraft>`) or into the game's
    scope (`game:<game>`). For every aircraft, it goes into the base.
  - "Softer" means curvature +0.15 (much: +0.3, a bit: +0.08) relative to the relation's base curve. "Harder" means
    -0.1. A dead zone step is ±0.02.
  - Steps are relative to the base, not to the current value, so kept preferences used again give the same curve and
    never one more step.
- **Force feedback.**
  - Only the devices of your setup that have force feedback are changed.
  - DCS keeps one value per aircraft (`trimmer`, `shake`, `ignore`). IL-2 keeps game-wide values (`force`, `amplitude`,
    `enabled`).
  - Factors are relative to the game's default.
- **vJoy.**
  - The mode is set per game, in the same way as `engine vjoy mode`.
  - "space games" means the space games among your games.
  - A class without a game gets only a note.

## The preview

- **What JoyPro understood.** Each preference is shown in your language, together with your words. It is marked new,
  new and read by the local AI, or saved. You can forget a preference, or all of them.
- **Not understood.** The clauses JoyPro could not read.
- **What changes.** The changes are grouped:
  - relations split by aircraft class;
  - binds that move;
  - unbound relations that get a place;
  - axis curves;
  - force feedback;
  - virtual joystick.

  Every change shows its before and after values in plain words: "Hat 1 down (TMS hat down)", "Button 25 (Base button
  25)", the printed label of a panel switch, "curve +0.15", "UH-1H (DCS)", never an input or control id
  (`JOY_BTN_POV1_D`, `stick.hat.tms.press`) or a tuning code. Each group has a tick for all of its changes, and each change has its
  own tick.
- **Apply** runs the ticked changes in one engine call. That makes one history version, "Lay out with AI: N binds, ...",
  and **Undo** takes it back. Your request and "Also make relations for functions I do not have yet" are kept in the
  settings.

## CLI

- `engine arrange ask "<words>" [--llm auto|on|off] [--games g1,g2] [--add-missing] [--forget-saved] [--proposal <file>] [--role ...] [--grip ...] [--add-device ...]`
  - Prints the preview (JSON, or `--text`) and writes it to the proposal file with the plan next to it. Nothing is
    saved.
  - With no words, the kept preferences are used.
  - `--games` defaults to the games of your relations. `--add-missing` also makes relations for the functions you do
    not have yet, as the Setup wizard does.
- `engine arrange apply --proposal <file> [--skip <change id>]... [--dry-run]`
  - Applies the preview in one history version and keeps the preferences in the profile (`Pr0file.LayoutPreferences`).
  - Change ids are `bind:<relation>`, `split:<relation>`, `tuning:<relation>|<scope>`, `ffb:<device>|<scope>` and
    `vjoy:<game>`. Skipping a split also skips the new relation's bind.
- `engine arrange prefs [show | clear | remove <key>]`
  - Shows or edits the kept preferences.

The engine server runs `arrange prefs` and `arrange ask --llm off`. With the local AI model, `ask` runs in a process of
its own, as every AI path does.

## Tests

- `gui/JoyPro.Gui.Tests/LayoutWithAiTests.cs`:
  - phrases and what they compile into, on four device sets (small, medium, the author's pit, space with two sticks);
  - the German test set (each German text gives the preferences of its English one) and the same layout from German and
    English words through the real engine;
  - inputs and controls in plain words (Auto Assign preview, Setup wizard review, Lay out preview, plan reasons and notes);
  - the same phrases through the real engine, on fake devices with the shipped catalogs: trim on a hat, no modifier,
    curves only for helicopters, rudder on the twist without pedals, space yaw on the left twist, kept preferences used
    again;
  - the author's pit on a copy of a real profile (`JOYPRO_LAYOUT_BIG_PROFILE`);
  - the Binds page panel with a fake engine, and its settings across a restart;
  - offscreen pictures (`JOYPRO_LAYOUT_AI_OUT`).
- `engine/tests/smoke/autoassignmatrix.ps1`: ask, apply with a skip, the history version, the kept preferences, and
  forgetting one.
