# Manage with AI / Edit with AI

Optional and local only. A request in your own words becomes a **plan** of JoyPro's own relation steps. The local AI
model (see [natural-language-grouping.md](natural-language-grouping.md)) only proposes the plan; JoyPro checks every
step against your profile and the catalog, shows it with a checkbox per step and runs the ticked steps when you press
**Apply**, as one change: one Undo takes all of them back. **Discard** forgets the plan. Without the model JoyPro works
as before, and a few simple requests still work through offline rules.

## Where

Every AI action shows the same status line of the local AI (its whole setup is Settings > Local AI): whether it is
switched on and whether the model is loaded right now, e.g. "AI: on (Qwen3 8B) - not loaded right now, starts with
your next request", with **Turn on the local AI** / **Set up the local AI** when it is off and a **Settings** link.
**Make a plan** reads the setting first (2.2.15 sent the first request after a start with the AI off) and shows
"Starting the AI model ..." while the model loads.

- **Manage with AI**: the wand button in the Relations list header (between the title and New). The whole profile:
  "append _sc to every relation that contains Star Citizen", "delete all relations that have no bind",
  "new relation with pitch of all games but no helicopters".
- **Edit with AI**: next to "Auto-build relations from all my actions..." for the open relation only: its actions, aircraft
  ticks, name, groups and axis tuning. Every step shows before and after; a step that would touch another relation is
  rejected.

## Steps

| Step | Fields | What JoyPro checks |
|------|--------|--------------------|
| create | name, kind (axis/button), describe | the name is free; the actions come from `describe` through the Group by description rules |
| delete | relations (exact names) | each name exists |
| rename | relation, to | the relation exists, the new name is free |
| add / remove | relation, describe (or games for remove) | the actions exist in the catalog and are of the relation's kind |
| aircraft | relation, planes, on | the aircraft are in the relation |
| groups | relation, add, remove | something changes |
| tuning | relation, curvature, deadzone, saturation, invert | an axis relation with a bind; values clamped |
| fill | planes (names, or "all DCS aircraft"), relations | a preview, see below |
| check | relations (none: all) | every action against all its ticked aircraft (relations check-aircraft): where the id does something else there, that aircraft is unticked; nothing found: a preview line |

Offline rules (no model): append / prepend a part to every relation that contains a game (its actions) or a word
(its name), delete relations without a bind, rename A to B ("rename it to ..." in Edit with AI), and fill / check: a
request about the relations with check words (check, verify, go through, review, make sure, fulfil, works properly)
becomes a `check` step; one with fill words (fill, add, expand, extend, update, include, bring) that names aircraft
("... the new modules: Mirage F1EE, F-15E Strike Eagle, C-130J": after a colon, in a list, with "and") becomes a
`fill` step of those aircraft; "all modules / aircraft in DCS" without names fills every DCS aircraft.

The model and the rules (`--llm auto`, what the app sends while the local AI is on): the exact rules (rename,
prefix/append, delete without binds) run without the model. Everything else goes to the model, with JoyPro's own
reading of fill/check as a hint; the plan keeps the check and fill steps the rules read even when the model leaves
them out. When the model does not start or answer, or answers with no steps for a request that names aircraft, JoyPro
makes the plan from its own reading and says why (with the model's reply). A model that does not start says why in
plain words (not enough memory, a llama.cpp too old for the model, a damaged GGUF file, ...; a model kept loaded for
the app is started once more with its output read to get that reason). `--llm off` (the local AI switched off) uses
the rules only; without a rule the answer says to switch the AI on in Settings > Local AI.

## Fill aircraft into the relations (mass edit)

"Check the modules released since 2023, i.e. F-15E Strike Eagle, F-4E Phantom II, OH-58D, CH-47F, F4U-1D, MiG-29A,
C-130J, La-7, F-100D, and fill them into the relations where they fit" becomes one `fill` step (offline rule or the
model; the model only names the aircraft). The aircraft names are resolved against the catalog (every seat: F-15E,
F-15E_WSO); names JoyPro does not know (a pack like "Flaming Cliffs 2024", a module not in the catalog yet) are listed.
Other games work the same way by their name ("Star Citizen").

Per relation x aircraft, two independent paths:

1. **Rules** (always): Auto Group's slot matching over the manual-checked function catalog, restricted to the named
   aircraft and run on the profile in memory (taken back at once), then Suggest missing for relations that span many
   aircraft but got nothing (20 s cap). 400+ relations take about half a minute.
2. **AI** (local model on; `--llm on` checks every pair, `auto` only the uncertain ones): JoyPro shows the model the
   relation (name, kind, its actions) and the aircraft's candidates (the rule picks plus a catalog search filtered to
   that aircraft), each as a small knowledge record: readable title, cockpit device/category, axis or button, function
   slot, and what it does from JoyPro's own data (cockpit guide text, the sim's description, the function's general
   explanation) or "no manual data". The model picks one candidate or none with a reason; only candidate ids count.
   Batches of 5 pairs, 120 s cap, picks cached per relation x aircraft in `<data>\Llm\fill-cache.json`.

Combined: both agree = high (ticked); one path = medium (rules: ticked, AI only: unticked until you agree); different
picks = ambiguous (both shown, unticked). The preview shows a summary ("57 relations get 312 actions for 10 aircraft;
4 relations ambiguous; 23 functions found no match"), the relations (expandable) with each action, its aircraft, the
path (Rules / AI / Rules + AI) and the reason, a checkbox per relation and per action, and filters by aircraft and
confidence. **Apply selected** hands the ticked actions back as `add` steps with exact `specs` (one history step).

Knowledge records per game (explanations, `relations action-info --coverage`, without the local cockpit pack): MSFS
2020/2024 rich (the sim's own descriptions, 90-99%); FSX/P3D, Arma 3, Arma Reforger 84-93% (general function texts);
DCS 52%, X-Plane 61-64%, IL-2 54-69%, War Thunder 61%, VTOL VR 64%, Falcon BMS 56%; thin: Star Citizen 32%, Elite
41%, Project Wingman 40%, Nuclear Option 44%, X4 45%. With the cockpit pack installed the cockpit guide adds the
per-control texts (mainly DCS).

## Command line

```
engine --profile p.pr0file relations plan ask "<request>" [--relation <name>] [--llm auto|on|off]
engine --profile p.pr0file relations plan apply (--file plan.json | '<json>') [--relation <name>] [--only 0,2] [--dry-run]
```

`ask` returns the plan (`Ops`) and the checked steps (`Steps`: Text, Ok, Error, Before, After, Details) and changes
nothing. `apply` runs the steps of `{"ops":[...]}` (only those of `--only`), saves the profile once (one history step)
unless `--dry-run`. `--relation` keeps every step inside that relation.
