# JoyPro2 GUI User Wiki (Beginner Friendly)

This guide explains the JoyPro2 GUI in very simple language.

Think of JoyPro2 like this:
- Games have many controls.
- You want one clean setup for all your planes and devices.
- JoyPro2 helps you build that setup once, save it in one file, and export it back to games.

## What JoyPro2 Is For

JoyPro2 helps you:
1. Import controls from DCS, IL2, and Star Citizen.
2. Group matching in-game actions together (`Relations`).
3. Connect those actions to your real hardware (`Binds`).
4. Reuse and manage modifier buttons (`Modifiers`).
5. Export back to game files.
6. Validate your profile before export.

## The Main Idea (Very Simple)

- `Node`: one real game action (for one game/plane).
- `Relation`: a folder of nodes that mean the same thing.
- `Bind`: what physical button/axis controls that relation.
- `Modifier`: extra key/button (like shift layer).
- `.pr0file`: your master save file with all of this.

## Top Bar (Always Visible)

- The JoyPro banner: the JoyPro mark and name, and the theme buttons (`Light`, `Dark`, `System`). It shrinks on small
  windows. Details and previews: [docs/branding](../../docs/branding/README.md).
- The open profile as a card: its name in big letters (the full path as a tooltip), or "No profile open".
- `Open profile...`: pick an existing `.pr0file`. `New profile`: start an empty one at once; it gets a name in
  `Documents\JoyPro2\profiles` (`More > Rename...` gives it yours; importing from a game also makes one for you).
  `More > Open last profile`: the last profile. JoyPro opens your last profile by itself right after its window shows
  (Settings > Startup: "Open the last profile at start"); if the file is gone, it starts with no profile open.
- There is no Save button: every change is saved by itself. The chip next to the buttons says `Saving…` while a
  change is written and `Saved ✓` once it is on disk (its tooltip shows the time). Edits that are not an engine
  command yet (an axis curve you are dragging, marker positions and tables on the Layout page, a modifier that is not
  on a bind yet) are saved 2 seconds after your last change, and at once when you close JoyPro, open another profile,
  or export / import. `Ctrl+S` still saves at once. `More > Save as...` saves a copy under another name and goes on
  with the copy.
- Back / Forward arrows (Alt+Left / Alt+Right, the mouse's back and forward buttons): like a browser, back to the page
  and the place you were (the same Check list scrolled to the same card after `Open both relations`, the relation that
  was open, the search). They change nothing in the profile.
- `Undo` / `Redo` (Ctrl+Z / Ctrl+Y, also Ctrl+Shift+Z): step back and forth through your changes, also after a
  restart of JoyPro. The tooltip says what the next undo takes back.
- `History`: every save is a version. The panel lists them, newest first, with what changed ("Relation 'Throttle
  ALL' created, 42 actions added", "Before export to DCS"). `Details` shows what a version changed, `Compare` what is
  different now, `Restore` makes the profile as it was then (that is a change too, so `Undo` brings you back), `Keep`
  protects a version from the clean-up. The first line shows the space the history uses; `Clean up` thins it out now.
  Old versions are thinned out by themselves: all of the last day, one per hour for 7 days, one per day for 60 days,
  one per week after that, at most 200 MB per profile and 1 GB in total. Details:
  [docs/profile-history.md](../../docs/profile-history.md).
- `Add another profile...`: pick a second `.pr0file` and merge it into yours. The box next to it says who wins when
  both have the same relation ("The other profile wins" / "My profile wins"). JoyPro asks first and keeps a copy of
  your profile (`name.before-merge-<time>.pr0file`).
- `Reload`: read the profile, devices and games again. `Close profile`: closes it in JoyPro (the file stays).
- `Engine options (advanced)`: for experts only: the engine and profile paths as text, the merge path, and how a
  profile is repaired when it loads. Normal use needs none of it.

Every button has a symbol and a tooltip. Symbols that show a state: a green tick = done / installed / fine, an
orange triangle = needs a look, a red mark = failed, a blue "i" = information. The icon set is
`gui/JoyPro.Gui/Styles/Icons.axaml` (shared by every page).

Before anything that removes binds or changes files JoyPro asks in a small window (restore a backup, undo an export,
"Replace on my devices", clearing other devices, removing or replacing a device, merging profiles, starting over
from a game, moving the DCS defaults, clearing all DCS force feedback settings). `Cancel` changes nothing.

## Bottom Workflow Bar

Every page has a symbol and a tooltip. Use them in this order:
1. `Start` (house): your games, import
2. `Relations` (chain): which commands belong together
3. `Modifiers` (keys): shift buttons
4. `Binds` (gamepad): buttons, axes and curves
5. `Layout` (picture, optional)
6. `Export` (arrow up): write into the games
7. `Settings` (gear) / `Advanced` (console) when needed

`Check` checks the profile (results on the Export page); `Output` opens the engine's output panel. `HUD` and
`Force Feedback` are extras that work without a profile; `Virtual Joystick` is an extra per game (see below);
`Cockpits` (airplane) is a reference of real and fictional cockpits (see below). The status
line above the page buttons shows what JoyPro just did (its tooltip: the last exit code and how long it took); the
output panel opens above it and never covers it. On a narrow window the page buttons wrap into a second row.

## Start Page: Your Games and Import

`Your games` shows a card per game you have: its cover (the picture Steam keeps on this PC, or a JoyPro badge with
the game's name and a motif when there is none; nothing is downloaded), a green tick when it is installed (an orange triangle when
JoyPro does not find it: Settings > Games > `Choose folder...`), a blue note when the game was updated since JoyPro
read its commands, and the result of the last action on it. Buttons on a card:
- `Import`: take the binds you already made in this game into JoyPro (the import wizard below).
- `Export`: opens the Export page with only this game ticked.
- `Update`: reads the game's new commands after a game update (DCS: from its files, DCS stays closed; IL-2,
  Star Citizen and the other games: their own refresh). Only for games JoyPro can read itself.
- `Undo export`: puts the game's control files back from before JoyPro's last export (asks first; close the game).
- `Watch for changes` (a tick box): JoyPro notices when you change binds in this game and offers to take them over
  (see "Game changes and the system tray" below). Ticked for every installed game; untick it and JoyPro leaves the game
  alone (import and export still work). JoyPro remembers your choice: a game you unticked stays unticked, and a game
  you install later is ticked by itself. A game JoyPro does not find has the box greyed. `Watch all` next to the
  section title ticks or unticks all installed games at once. When `Settings > Games > Watch game configs` is off,
  nothing is watched whatever the boxes say (the page tells you above the cards).
- The crossed-out eye at the top right of a card hides the game (you do not have it or are not interested): it leaves
  the cards, the game count, the watcher, the setup wizard, the export page, the filters, the catalog, the action
  search and the HUD; your binds for it stay. Below the cards a line says how many games are hidden; its button opens
  `Settings > Games`, the one place that shows them again.
- `Look again`: find your games again.
- `Set up my devices automatically...`: the Setup wizard (below).
- `Supported games`: every game JoyPro knows, in your browser (below).

### Supported games

`Supported games` (Start page, right of `Your games`) opens the full list of games in your browser: every title JoyPro
handles (each version separately, e.g. MSFS 2020 and 2024, X-Plane 11 and 12, Prepar3D v4/v5/v6) and the ones it only
documents. A table per group (flight simulators, combat flight, space, military sims, arcade and one-device games)
shows what JoyPro does for each game: import, export, keyboard binds, modifiers, building the command list on your PC,
watching for changes, curves and dead zones, game settings, force feedback, the virtual joystick and Xbox pad, virtual
functions, the setup wizard, cockpits, cheat sheets, and whether JoyPro was tested in the game. ✓ = yes, ◐ = partly
(a numbered note says what is missing), ✗ = no, — = not applicable. Below it, per game: where the binds are stored,
what the command list is built from, how many devices and buttons the game reads, anti-cheat notes and what is still
open. The same page is in the repository: [docs/games/support.md](../../docs/games/support.md). Nothing is downloaded;
JoyPro writes the page into your temp folder (`JoyPro2\supported-games.html`).

### Lay out with AI

Binds > `Lay out with AI...`: say how you like your controls, e.g. "throttle and left-hand stick, trim on the hat,
curves softer for helis" (the example buttons add phrases JoyPro understands). `Preview` shows what JoyPro understood
(each preference in your language; new or saved; `Forget` removes one) and every change, grouped: binds that move,
unbound relations that get a place, relations split by aircraft class, axis curves, force feedback, virtual joystick.
Untick what you do not want, then `Apply`: one step, `Undo` takes it back. Your own layout stays wherever you ask for
nothing else; the preferences are kept in the profile and used again. It works without the local AI; with it on,
JoyPro also understands other wordings and languages. Details: [docs/layout-with-ai.md](../../docs/layout-with-ai.md).

### Setup wizard

`Set up my devices automatically...` (Start page, next to `Your games`; also on the Binds page) binds the most important
functions of the games you watch to your devices in five steps (details: [docs/setup-wizard.md](../../docs/setup-wizard.md)):
1. `Games`: the games ticked `Watch for changes` are ticked; change them for this setup.
2. `Devices`: each device with the role JoyPro proposes (main stick, throttle, left stick for space sims, collective,
   pedals, panel, keyboard joystick extra, virtual, not used). Change a role in its list, pick the grip of a base that
   takes several grips. The box on the right says what JoyPro will do (collective for helicopters, translation on the
   left stick ...). `Detect again` looks again.
3. `Priorities`: put your aircraft in order (`Add as next priority`, `Add as equal`, drag rows and aircraft, `Up`,
   `Down`, `Equal with above`, `Own row`, `Remove`); aircraft in one row count the same. Choose how many modifiers
   you want (`As few as possible`) and how much your stick should work like the real aircraft's (`Group by function`
   ... `Like the real aircraft`).
4. `Make the plan`: what was placed, what not, the modifiers made and why, the rules, a quality score per aircraft.
5. `Review`: per device JoyPro's drawing with what is bound where, for one aircraft or all (`Open the sheet` shows it
   full size), and the list of its inputs. Select a function to `Move` it to a free input, `Swap` it, `Remove from the
   plan` or `Put back`; a function under `Not placed` can be moved onto a free input. `Apply to my profile` writes it
   all in one step (one `Undo`); `Export to my games` opens the Export page.
Nothing changes in your profile before `Apply to my profile`. Closing the window keeps your choices for the next time.

### The import wizard (3 steps)
1. **Pick a game**: `Import` on its card.
2. **Choose how**:
   - `Add to my profile (recommended)`: what you have in JoyPro stays, the game's binds are added.
   - `Start over from this game`: the relations of your profile are replaced by the game's (JoyPro asks and keeps a
     copy of your profile first).
   - `Also take the keyboard keys`: flight commands only; joystick binds are always taken.
   - DCS: `All my DCS aircraft`, or untick it and tick the aircraft.
   - `Look first` shows what the game has (output panel) without changing anything; `Import now` imports.
3. **Done**: a green tick and how many relations your profile has now (or a red mark and what went wrong, with
   `Try again`). `See my relations`, `Import another game` or `Export to games`.

No profile open? The import makes one for you.

Changed something in JoyPro and not exported it yet (for example moved an action to another button)? An import brings
back what the game still has, so JoyPro says what would be lost first: "You changed 2 binds for DCS that are not in DCS
yet (e.g. moved 'Master Warning Button')". `Import (drop my changes for DCS)` imports (use it on purpose to get the
game's binds back; Undo or History brings your changes back), `Export first` puts your changes into the game (your
profile wins there) and imports nothing, `Cancel` changes nothing. `Do not ask again` switches the question off;
Settings > Confirmations switches it on again. Nothing unexported: no question. When a game changed its controls, the
take-over ("A game changed its controls") offers only what the game changed, never to undo what you changed in JoyPro.

### Expert import
`Expert import: paths, device filters and game values` (collapsed) is the full form for experienced users: your own
paths (portable installs, a second Saved Games folder), only some devices or ignore devices, values set in the game
(inverted, slider, curvature, deadzone, saturation), DCS defaults and `*_easy` aircraft, and the games of JoyPro's
game list with their game profile, export aircraft, dry run and DB source.

### Game update notice
When a game was updated since its key command DB was built (`engine db check-updates`), a yellow box lists it, one
row per game:
- `Refresh DB` reads the game's new commands into the DB with the game's own refresh (DCS: `dcs refresh id`, read
  from the game's Lua files without starting DCS; IL-2: `db refresh-il2`; Star Citizen: `db refresh-starcitizen`
  with the branch from Settings; other games: `<game> refresh-db`). The notice is checked again afterwards. Games
  whose action list only comes with JoyPro updates have no `Refresh DB`.
- `Keep DB` keeps that game's DB as it is and hides its notice (`db check-updates --accept <game>`).
- `Keep All DBs, Hide Notice` does that for every game (`--accept-all`); `Check Again` checks once more.

## Relations Page: Build Logical Actions

A relation is one function (for example "throttle" or "release flares") of all your games and aircraft: DCS F-16C
"Throttle", IL-2 "Engines throttle control", Star Citizen "Strafe forward / backward" ... all on one input of your stick.
The page works like JoyPro 1: pick a relation on the left, search on the right, tick what belongs, add it
([docs/relation-builder.md](../../docs/relation-builder.md)). Binding happens on the Binds page, not here.

The page: the relation list on the left (search, group chips, Ctrl/Shift for several); on the right the open relation
(its name, edited in place, and its actions) and three panels: `Add actions`, `Suggested for this relation` and
`JoyPro assistant`. Every button has a tooltip.

### Find a relation by what it does
Type in the search box of the relation list (or of the Binds page), e.g. `master`. With `Name + actions` (the
default) it finds relations by their name and by the titles of their actions. Under each card found by its actions
JoyPro shows the matching actions with the word marked, the game and the aircraft (`Master Caution Button · DCS World ·
AH-64D BLK.II (2)`): up to three lines, then `+N more`. Relations whose action has the whole search (`master
caution`) come first. Click a line: the relation opens with that action selected (on the Binds page the row is
selected and `What it does` marks the action).

### Make a relation
1. `New relation` (Ctrl+N): type its name, e.g. `Throttle ALL` (empty: it is named after its first action).
2. `Add actions`: type what it does, e.g. `throttle`. The search runs while you type, over every game and aircraft at
   once; synonyms count (`throttle` also finds thrust and the helicopter collective, `flare` the countermeasure
   dispense switches). The rows come grouped by game, each with its aircraft, the kind (axis or button) and why it was
   found.
3. Filters: `All` / `Axes` / `Buttons` (a relation holds one kind; an open relation shows only its own), the game chips
   of the result with their counts (none ticked = every game; `+n more games` for the rest), and the aircraft button to
   pick aircraft.
4. Tick the rows that belong (the box next to a game ticks all or none of its rows; `Tick all shown`, `Clear ticks`),
   then `Create relation with n actions`. Axes and buttons together are refused.
5. Each game shows its best rows first; `Show 39 more` under the game loads the rest of that game's hits for the same
   search, `Show less` folds them again, `Expand all` / `Collapse all` above the list do it for every game. The state
   stays while the search is the same; ticks survive expanding and collapsing.

### The aircraft of a row (same id, different meaning)
A DCS command id belongs to its aircraft: the same id can be "Pitch" in the jets and "Collective" in a helicopter, and
a cockpit switch id is another switch in every module. JoyPro never assumes the same id means the same everywhere:
- A row holds only the aircraft where the id has the row's title and category (or nearly: other word order, one word
  more, another category). Below the row: `3 of 4 aircraft · 1 differ`, and `same id, other meaning in n (left out)`,
  `n near` when some only differ a little.
- Click it to see every aircraft with that id, each with its own title and category, a tick, and `differs` / `near`
  markers. `All`, `None`, `Only same title`, `Invert` act on the aircraft shown; the filter box narrows them (`differs`
  shows the ones with another meaning). Space toggles the focused aircraft, Shift+click ticks a range.
- Ticking the row adds only the ticked aircraft; changing an aircraft tick ticks the row.

### Change a relation
- The name at the top of the open relation is a text box: change it, Enter (or leaving the box) renames it, Esc puts
  the old name back (`engine relations rename`).
- `Its actions`: one row per game action with the game badge, the aircraft, the title and the kind; `x` removes it.
  A DCS action shows its aircraft badge (`82 of 84 aircraft`): open it for the same aircraft list as in Add actions,
  change the ticks at any time, `Apply` saves them (one Ctrl+Z), `Reset` puts them back. An action whose ticked
  aircraft mix meanings is flagged `Mixed meanings` in orange; aircraft left out because their meaning differs stay in
  the list unticked (`n left out (meaning differs): tick to re-include`).
- `Same id, different meaning` (a notice above the actions): your profile has actions with aircraft where the id means
  something else (often from JoyPro 1 or an old auto-fill). `Show them` lists them (click one to open its relation),
  `Switch them off` unticks those aircraft in one step (one Ctrl+Z), `Not now` hides the notice. Aircraft whose title
  differs but whose function JoyPro reads as the same (`Cyclic Pitch` for `Pitch`) are not touched.
- `Add actions` → tick rows → `Add selected (n)`. Rows whose aircraft are all in the relation say `already in this
  relation`; a row another relation uses says `also in '<relation>'`.
- `Expand with AI`: explain in your own words what else belongs here (or what does not); the JoyPro assistant adds or
  removes it (below).
- `Delete` (trash icon) deletes the relation (asks first). Ctrl+Z undoes any change.

### Move one action to another button
A relation is one input: changing its input moves all of its actions. To move only one action (for example the MSFS
`Toggle Master Warning` that shares a button with your DCS actions):
1. Right-click the action in `Its actions` → `Move to another button...` (on the Binds page: right-click the bind row
   → `Move an action to another button...`, or `Move...` next to an action under `What it does`).
2. Press `Press the new button` and press it on your device, or pick the device and type the input (`JOY_BTN3`).
3. The window says where it goes: into the relation that already uses that button, or into a new relation named after
   the action when no relation uses it.
4. That aircraft already has another action on the new button (in a relation an aircraft has one action): the window
   names the relation and the action there and asks what should happen. `Swap`: that action goes to the old button
   (the two swap places). `Replace`: that action leaves the button and is not bound then. `Keep both`: the moved
   action gets its own relation on the same button, and both fire when you press it.
5. `Move`. The other actions stay on the old button; a relation left without actions goes away. One Ctrl+Z undoes it.
   Export to the game afterwards: until then the game still has the old button, and an import from the game brings
   it back.

### Suggested for this relation
What means the same in your other games and aircraft, from JoyPro's function catalog (checked against the manuals),
no AI:
- every row shows the game, the confidence (`High 95%`, `Medium`, `Weak`) and why it fits ("Same function as F-16C_50
  'Countermeasures Management Switch - Fwd'", "Your style: ...", "Same command id", "Title match"); the tooltip has
  every reason;
- `Add` (one click), `Add all (n)` (every row shown that no other relation uses), `x` = dismiss: never suggested again
  for this relation (`Dismissed for this relation` on the left lists them, `Restore` brings one back);
- a row another relation already uses says `Already in '<relation>'` and offers `Move here`;
- game chips filter the list; `Also weak matches` adds the title-only matches;
- `Looks wrong in this relation`: entries an old auto-fill put here, with `Move to '<relation>'` and `Remove`.
The list reloads after every change. `More` → `Next relation with suggestions` opens the next relation that can grow.

### JoyPro assistant
Say what you want in a sentence; JoyPro changes your relations at once and the answer shows what it did:
`"Throttle ALL": created · added DCS 48 aircraft, Star Citizen 2, IL-2 5 · now 160 actions`, the steps it took and
ids it rejected. `Undo this` in the answer takes the whole answer back (one step; Ctrl+Z works too). Click a relation
name in the answer to open it.
- With the optional local AI model (`AI model...` sets it up: download with size and license, or your own llama.cpp
  and GGUF) it understands free sentences: "Add all inputs that bind all thrust to one axis into one relation called
  Throttle ALL, including collectives and spaceship forward/backward thrusters". The model works through JoyPro's
  tools (look up, search, suggest, create, add, remove, rename); JoyPro checks every id it uses. It runs on this PC
  only for the answer and is stopped afterwards.
- Without the model the offline brain understands simple commands: `Throttle ALL: all throttle axes`, `add the
  collectives to Throttle`, `remove War Thunder from Throttle`, `rename Throttle to Throttle ALL`, and "... into one
  relation called X including A and B". For anything else the answer says that it needs the model; nothing changes.
- `Expand with AI` on the open relation makes the assistant work on it (`Working on: <relation>`, x for the whole
  profile).

### Group management
- `Groups` (under the list): add groups via CSV (`Startup,Combat`), remove selected groups, rename groups globally.

### Auto-build relations from all my actions (whole profile)
`Auto-build relations from all my actions...` builds relations for the whole profile at once:
- `By function (Auto Group)`: the same function of every aircraft and game into relations ([docs/auto-group.md](../../docs/auto-group.md)).
- `By description`: several relations from one sentence (below).

### Group by description
`Auto-build relations from all my actions...` → `By description` builds relations from a sentence (`engine relations from-text`,
[docs/natural-language-grouping.md](../../docs/natural-language-grouping.md)):
1. Type what belongs together, e.g. `speed brake in and out on all jets`, `gear up/down of every DCS warbird`,
   `all trim hat up actions for helicopters`, `flaps for all MSFS airliners and IL-2 bombers`.
2. `Preview` shows what JoyPro understood (functions, aircraft, words it did not understand) and one relation per
   function and direction. Open a relation to see every command with its confidence and the reason per aircraft
   (what the control does in that cockpit, why the aircraft fits). Commands already in another relation say where
   they stay. Low-confidence rows (a game whose binds are shared by all its aircraft, words found only in the
   catalog) start unticked.
3. Untick relations or single commands you do not want, then `Create`. Existing relations of the same name only get
   what they miss; new ones get the group `FromText`.

`Local AI model (optional)`: the offline rules always work. Tick `Use local AI model` to let a free open-source model
help with unusual wording (and with the JoyPro assistant). JoyPro shows this PC's hardware, the recommended model
(size, GPU or CPU) and the licenses; nothing is downloaded until you tick `I accept the size and the licenses above`
and click `Download` (progress bar, `Cancel download`, resumable). `Remove model` deletes it. The model runs only while
a request is processed and everything stays on this PC.

## Modifiers Page: Manage Modifier Library

Use this page to create/rename/delete modifiers profile-wide.

### Create modifier
1. Enter `Name`.
2. Select `Device`.
3. Select `Input` (or use `Record Modifier Input`).
4. Click `Create Modifier`.

Rules:
- Duplicate name is blocked.
- Duplicate device+input is blocked and warns immediately.
- Create button stays disabled until inputs are valid.
- A modifier that is not on a bind yet ("temporary") is kept with the profile by the autosave and comes back when you
  open the profile again; it reaches the games once it is on a bind.

## Binds Page: Connect Relations to Hardware

This is where you assign real joystick/keyboard inputs to relations.

### Typical bind flow
1. Select relation/bind row on left.
2. Set device/input directly or use record capture.
3. Apply axis options if needed (invert, deadzone, curvature, saturation).
4. Add/remove modifiers for that bind.
5. Nothing to save: every change is saved by itself (`Saved ✓` in the top bar), `Undo` takes it back.

To put only one of the relation's actions on another button, see "Move one action to another button" on the
Relations page (also on the bind row's right-click menu and `Move...` under `What it does`).

### Put relations on my buttons automatically (Auto Assign)
`Put relations on my buttons automatically...` (Binds page toolbar; the Start page's `Auto Assign` step opens it) binds the relations to the best controls of your devices (`engine relations auto-assign`):
1. `Detect Devices` lists the connected devices as the device catalog knows them. Pick the grip of a base that takes
   several grips, tick `Leave out` for devices that should get nothing, add devices that are not connected from the
   catalog (`Search Catalog`, `Add`).
2. `Layout` on a device row (or `Show Layout` for the picked catalog device) shows what JoyPro knows about each input
   of that model: input, control (e.g. `stick.hat.tms.up`), label, kind and finger (`engine devices kb show`).
3. Options: modifiers at most, `Keep my layout`, `Re-assign bound relations`, `Also move binds of devices that are not
   connected`, and `Only the selected relations`: only the relations selected in the list (Ctrl/Shift for several)
   (or the bind selected on the Binds page) are assigned; the others keep their binds and their controls stay taken (`--relation`).
4. `Preview` shows the result without changing anything; `Explain Selected` adds, for the selected relations, the
   best candidate controls with their score and what occupies them (`--explain`); `Apply` binds.

### Axis Tuning (curve manager)
For an axis relation, open **Axis Tuning**:
- **Scope**: `All games (base)`, each game of the relation, each active DCS aircraft (`DCS / F-16C_50`) and game families
  (`IL-2 family (Great Battles + Korea)`). The most specific scope wins: aircraft, game, family, all games.
- The status line says whether the scope has its own settings or inherits them (and from where; IL-2 Korea fits the
  base curve to its own exponent when it inherits).
- The editors are the scope's game model (DCS: dead zone, saturation, curvature or an 11-point user curve, slider,
  invert; IL-2 Korea: exponent, dead zones, centre offset, invert; other games: dead zone, invert). Edits redraw the
  curve at once; the dashed orange curve is the base while the scope inherits. While the panel is open, the dot shows
  where the bound stick axis is now and what the game gets ("in 0.42 -> out 0.18").
- **Drag the points** on the curve: with the user curve on, its 11 points are white handles on the preview; drag one
  up or down to set the output there (the sliders follow).
- **Curve points** (X-Plane, whose response curve takes any number of stops): tick `Curve points` to edit the stops
  as a table (`x` = stick travel, `y` = output, both 0..1, `x` increasing, at least two points), `Add point` (in the
  widest gap) and `Remove`. Ticking it starts from the stops the game gets now; dead zone, saturation, curvature and
  the 11-point curve are greyed out while the stops replace them. The stops are handles on the preview too: drag them
  up/down and along the stick (between their neighbours). Saving sends them as `--curve-points x:y,...`; unticking
  and saving removes them (`--curve-points none`). The same rules as the engine's are checked before saving.
- **Save for this scope** stores the values (`engine binds tuning set`); **Inherit (clear)** removes the scope's own
  values (`engine binds tuning clear`). Invalid numbers are flagged and never sent. You do not have to press it: 2
  seconds after your last edit the autosave stores valid values for the shown scope (and at once before you open
  another profile, close JoyPro or export).

### Sorting and filtering
- Filter by text, group, and device.
- Click table headers to sort.
- Multi-sort follows your click order.

## Layout Page (Optional)

Every device as a picture, with what each button does (details: [docs/device-reference.md](../../docs/device-reference.md)):
- The best picture first: your layout, your photo, JoyPro's own drawing of the device (with JoyPro's positions of its
  controls, nothing downloaded), the maker's photos (one button per view, loaded on your PC only after your yes),
  JoyPro's schematic drawing, or an outline of the kind of device. Never an empty box.
- `Lines | Cel-shaded` next to the picture: JoyPro's own drawing as lines, or with flat colours under bold outlines.
  The colours (JoyPro's theme, white on black like a manual, blueprint, black on white, or your own line, page and
  leader colours) are in Settings > Drawings. The Start page's device cards use the same style.
- Markers only where JoyPro knows the position. A photo without positions shows the legend and offers
  `Place the markers` (press the button on the device, click its spot).
- A base that does not say which grip is on it (Virpil WarBRD, VPforce Rhino ...) asks once "Which grip is on it?"
  with pictures, most likely first. An unknown device asks "Which device is this?" (catalog search or the kind of
  device). JoyPro remembers the answer; the Start page cards use it too.
- `Use my old JoyPro layouts...`: JoyPro 1 layouts found on the PC are taken over (copied, matched by device name);
  the button picks another folder. They show the bound commands in your layout's own font, like JoyPro 1.
- `Share layout...` / `Import layout...`: marker positions to share (the maker's photo goes by its address only),
  with the tables and lines.

Tables and lines (like JoyPro 1's cheat sheets; details:
[docs/device-reference.md](../../docs/device-reference.md#tables-and-lines-callout-sheets)):
- A picture with known positions shows a table per control around it (a hat: press, up, right, left, down; a switch:
  its positions; an axis; a keypad or a row of toggles as ONE grid laid out like the keys; a knob's push, pull and turn
  as one table) and thin lines to the parts. `Split into single tables` (Edit) gives each key its own table again,
  `Group again` undoes that. JoyPro places them by
  itself; parts without a spot are listed under "Other controls". `Tables` on the toolbar switches back to markers.
- Drag a relation from `Drag a relation onto a row` (right) onto a row to bind it there; the second column binds with
  the modifier picked under `Modifier column shows`. Right-click a row: `Change...` or `Clear`.
- The box next to `Tables`: `Relation names`, or a game and aircraft: the rows then show that game's own menu titles.
- `Edit`: drag tables (they stay there), their right edge, a line sideways, a spot; drag a table's round handle to a
  spot to give it a line; `Row +/-`, `Modifier column`, `Pinned`, `Re-route line`, `Remove line`, `Auto arrange`,
  `Reset`; Ctrl+Z / Ctrl+Y; `Save`. Marker positions and tables are also saved by themselves 2 seconds after your
  last edit (Ctrl+Z still works afterwards). Only the old layout-file editor (a JoyPro 1 style `.layout` with a chosen
  output file) waits for `Save`, because a save writes a new layout file.
- The cheat sheets print these sheets (3508 px wide), with a GAME / PLANE band per aircraft; `White page` and `One
  page with all devices` under "What to make". A device JoyPro has its own drawing of is printed on that drawing (like
  a manual's button diagram), in the drawing style chosen under "What to make" (lines or cel-shaded, colours).

Layout files and cheat sheets:
- Auto-suggest templates by device name/GUID (`Suggest` button).
- Works even without a loaded profile (uses connected DirectInput devices).
- Optionally auto-apply best template matches into profile layout mappings (if profile is loaded).
- Select suggestions and push them into `Layout Map` + `Devices` with `Apply Selected`.
- Select one suggestion row and it auto-loads into the embedded editor preview.
- `Open + Load Selected` loads the currently selected suggestion into the embedded editor immediately.
- `Reload Source Path` reloads the currently typed source file (`.layout` or image).
- `Place Marker On Click` and `Place Table On Click` let you click directly on the image to place new items.
- Drag marker labels and table overlays directly on the image.
- Edit marker token/type/modifier and table name/size/columns in the right-side editor, then save.
- Unknown devices can now try a web-image backdrop fallback first, then synthetic backdrop generation if no usable web image is found.
- Table overlays are persisted next to templates as `<layout>.tables.json`.
- Text style controls for layout labels stay here (font/size/colors).

## HUD Page

Your sticks, throttles and pedals live on screen (or in a stream window for OBS): official pictures that tilt with
the stick, levers and axes that move, held buttons that light up with their names, and what each pressed button does in
the running game. Drag the widgets in the preview, tick what to show, pick style and accent colour, then
`Start HUD`. Details: [docs/hud.md](../../docs/hud.md).

## Force Feedback Page (Telemetry)

JoyPro drives a force feedback stick (VPforce Rhino, MOZA, ...) from the game's telemetry: stall buffet, G weight,
gear/flaps/speed brake rumble, runway rumble, gun bursts, hits, IL-2 cockpit shake and an airspeed-stiffened centring
spring. Supported: DCS World, IL-2 Great Battles, IL-2 Korea, War Thunder.

- **On/Off switch** (top right): while on, JoyPro checks every 5 seconds whether one of these games runs and then starts
  its force feedback process (`ffb\JoyPro.Ffb.exe`); the process ends by itself when the game exits. While off, nothing
  runs or polls. The status line next to the title says what is going on, for example `idle - waiting for DCS.exe`,
  `idle - no telemetry on UDP 41992`, `active - F-16C_50` or `stopped after an error: ...`.
- **Settings tab**: force feedback device (`Refresh` lists the devices; nothing moves), overall strength, max force (a
  hard cap, 60 % by default), invert X/Y. Per game: on/off, strength, which graph it uses (built-in or a saved file),
  every effect of the built-in graph on/off with its own strength, aircraft with their own graph or strength, and the
  telemetry export (`Install`, `Remove`, `Dry run` shows what would change; War Thunder needs nothing). Every change is
  saved at once (`engine ffb telemetry config set`) and a running process reloads it.
- **Graph editor tab**: the effects are a graph of nodes, like the Forza haptics blueprint. Add nodes from the list on
  the left, drag from an output (right side of a node) to an input (left side) to connect them; JoyPro refuses wires
  that would close a loop or do not fit (the reason shows below the canvas). Click a wire and press `Delete` (or
  right-click it) to remove it. Drag the background or use the middle mouse button to pan, the wheel to zoom, `Fit` to
  see everything. The panel on the right edits the selected node; a Telemetry node offers the game's keys, a Curve node
  gets a Bezier editor (drag points and handles, double-click adds a point, right-click removes one). `New from built-in`
  starts from a copy of the built-in graph; `Save as` stores it in the Graphs folder
  (`%LOCALAPPDATA%\JoyPro2\Ffb\Telemetry\Graphs\*.jpffb.json`), where the game and aircraft pickers find it. While a game
  runs, the nodes show their live values; without the game, constants and their maths are previewed.
- **Telemetry inspector tab**: every value the game sends right now (key, value, title, unit), with a filter; useful to
  pick keys for Telemetry nodes.

## Virtual Joystick Page (vJoy + HidHide)

Some games take only a few joysticks, pick up the wrong ones or renumber them. Per game you can switch to **Virtual**:
JoyPro puts every axis, button and hat the game's binds use onto one virtual joystick (vJoy), and the game sees only
that device. Needs vJoy 2.1.9 and HidHide 1.5 (free; the Xbox pad needs ViGEmBus); details in
`docs/virtual-joystick.md`. Not installed yet? "What this page needs" on the Overview tab installs them with one click
each (see "Components" under Settings below).

1. **Overview tab**: switch the game from `Native` to `Virtual`. The status box says whether vJoy and HidHide are
   installed; if the vJoy device lacks buttons, axes or hats, press `Set up vJoy for the table` (Windows asks once for
   administrator rights; a device never gets smaller).
2. **Export** the game again (Export page): the game file gets the binds on the virtual joystick, your profile keeps
   your real devices.
3. Press **Start** before you start the game: JoyPro hides your real devices from that game only (HidHide, one
   administrator prompt) and copies every input to the virtual joystick live (as often as your fastest screen refreshes,
   at least 60 times a second; button presses at once). Other programs keep seeing your devices.
4. Press **Stop** when you are done: HidHide gets back exactly the setup it had before. Closing JoyPro does the same. If
   JoyPro or the PC crashed, JoyPro repairs HidHide when it starts the next time (or press `Restore HidHide`).

- **Test** shows what the virtual joystick would get without touching vJoy or HidHide (Live test tab: axis bars, pressed
  buttons, hats).
- **Mapping tab**: which real input goes to which virtual input. Inputs keep their place for good (`Update mapping`
  only adds new ones), so the game's binds never move.
- **Xbox pad** (instead of vJoy, per game): for games that only take Xbox controllers or only one device (for example
  Hell Let Loose: Vietnam). JoyPro creates one virtual Xbox 360 controller with the free ViGEmBus driver (the page shows
  whether it is installed and can install it for you, see "What this page needs" below). Your sticks drive
  its two sticks, two triggers, D-pad and 14 buttons; where JoyPro knows the game's own controller layout, each action
  goes to the control the game expects. The Mapping tab lists what does not fit. The Live test tab shows the virtual
  pad. Hell Let Loose: Vietnam: pick the controller layout (A, B or C) in the game's Options > Controls, then press
  `Update mapping` here; bind your collective to both "Collective up" and "Collective down" in one relation so one
  lever drives both triggers.
- **One-device games**: games that read only one joystick or only Xbox pads show a note under their name, for example
  "One-device game: ... Suggested: vJoy". Press **Use it** to switch the game to that mode. These games are Battlefield
  6/2042 (vJoy; JoyPro sets the vJoy device up without Rx/Ry, which Battlefield takes for an Xbox pad), Hell Let Loose:
  Vietnam and ACE COMBAT 8 (Xbox pad). ACE COMBAT 8: bind your throttle to both "Throttle up" and "Throttle down" in one
  relation, so the lever drives both triggers.
- Options: "Hide the real devices" (off = the game sees both, may give double input), "Start and stop by itself" (JoyPro
  looks for the game every 3 seconds while this is on).
- **Set up vJoy for maximum** (Overview, shown while a vJoy device has less): gives each vJoy device JoyPro uses 8 axes,
  128 buttons and 4 hats (one administrator prompt). **Windows must restart** before the new setup works; JoyPro never
  restarts Windows, do it when it suits you, then export the virtual games again.
- **What a game reads**: under each game the page shows what it reads of one joystick, where JoyPro knows it (for
  example "32 buttons, 8 axes, 1 hat" for Arma 2), and warns in orange when an input or a virtual function output lies
  above it ("reads only the first 32 buttons of a joystick: button 40 on vJoy 1 will not work there"). Games where it
  is not known show nothing.

### Virtual functions (Functions tab)

Extras that run inside the virtual joystick, between your devices and what the game gets (like Joystick Gremlin):

| Function | What it does | For example |
|---|---|---|
| ≡ Axis bands | splits an axis into bands; each band holds or pulses its own button | throttle detents (idle cutoff, afterburner gate), flap positions |
| ↻ Button cycle | each press steps through buttons A, B, C, A ... (back and reset buttons optional) | radio presets, master modes |
| ◐ Toggle | a push button becomes an on/off button | a lamp test that stays on |
| ⇅ Switch to on/off pulses | a switch gives one button when switched on and another when switched off | gear up / gear down binds |
| ⋯ Short / long / double press | up to three buttons from one | next / previous / mark waypoint |
| ⇧ Shift layer | while a shift button is held, other inputs drive other outputs | games without modifiers |
| ⋈ Merge two axes | difference, sum, average, larger, smaller | toe brakes as one axis |
| ⋔ Split an axis | below and above the centre on two axes | one rocker as two brakes |
| ∿ Axis curve | dead zone, saturation, curvature, invert (JoyPro's axis curves) | a game without curve settings |
| ⇔ Axis ends to buttons | a button at each end of an axis | throttle at idle presses a button |
| ✛ Axes to hat | a mini stick as an 8- or 4-way hat | slew stick as hat |
| ± Buttons to axis | two buttons move an axis at a speed, optional spring back | trim, zoom |
| ⟳ Axis to encoder pulses | turning an axis gives up/down pulses | radio or altimeter knob |
| » Turbo | repeats a press while held (or until pressed again) | countermeasure bursts |
| ▶ Macro | one press plays presses with delays | start-up sequence |
| ✣ Hat to buttons | a hat as 4 or 8 buttons | games that cannot bind hats |
| ⊕ Buttons to hat | four buttons as an 8-way hat (diagonals from two) | a 4-way switch as hat |

1. Pick a game in virtual mode and a function on the left, press **Add function**. JoyPro gives it free virtual
   buttons (or axes, hats; on the Xbox pad free controls) and keeps them out of the mapping table.
2. In the editor on the right choose its **inputs** (device and input). The **node view** shows inputs, the function
   and its outputs; an output lights up while it fires.
3. Axis bands and axis ends: drag the **white handles** on the axis bar to move the edges; the amber zones are the
   hysteresis (the lever must pass an edge by that much). Per band choose "hold while inside" or a pulse on entering
   or leaving.
4. **Try it** runs the function right in the editor: move the sliders, press the input buttons, pick hat directions.
   With **Test** or **Start** running, the editor shows your real devices instead (LIVE).
5. **Save**. A running virtual joystick takes the change after Stop and Start.
6. Bind your relations to the outputs like to any vJoy button: type the relation's name under the outputs and press
   **Bind relation** on an output (or bind "vJoy Device" + the button on the Relations page), then export the game.

Keys (KEY_...) only when you tick **Keyboard output** for a function; they are typed only while the virtual joystick
runs for real (Start), never in Test. With Start by hand all virtual games' functions run; with auto start only the
started game's.

## Cockpits Page (reference)

Look up how a real aircraft's stick and throttle are laid out, version by version (docs/cockpits.md).

1. Left: type in the search box (an aircraft, a version, a game's aircraft name, or a control such as "TMS" or "china
   hat"), or narrow the list with Kind (fixed-wing, helicopter, space, fiction, real only), Game, Era, Role and Nation.
2. Pick the aircraft, then the version / block / tranche below the list (F-16C Block 50/52, Typhoon Tranche 3A,
   AH-64D pilot or CPG seat ...).
3. The page shows:
   - the stick side (stick, cyclic, yoke, side-stick) and the throttle side (throttle, collective, quadrant) as JoyPro's
     drawings with a numbered table per control: every direction with what it does. Untick "Side by side" for larger
     tables one below the other; the mouse wheel zooms, dragging moves;
   - "Surrounding cockpit": a plan of the panels around the pilot and what each panel is for;
   - "JoyPro's drawings": JoyPro's own drawings of the aircraft's real grips, where it has them (no download);
   - "Photos": real photos are only links. JoyPro asks before it downloads any ("Yes", "No", "Always for cockpits",
     "Never"); "Open the page" shows the photo in your browser;
   - "Sources": where the data comes from, with links. "Confidence" in the header says how sure it is.
4. Craft from films, series and games (X-wing, Viper, Star Citizen ships ...) are shown on a generic stick and throttle:
   JoyPro draws no film or game designs.
5. `Compare with my setup` (with a profile open): each function gets "✓ <relation> (<input>)" when your profile has a
   relation for it in that aircraft, else "(no relation)".
6. `Use this layout` hands the aircraft and version to the setup wizard (the same data as `engine cockpits get
   <aircraft> <variant> --json`).

## Export Page

Three numbered steps. Details: [docs/export.md](../../docs/export.md).

1. **Check your profile**: `Check now` looks for problems (relations without binds, the same button twice, ...);
   the list is further down the page.
2. **How JoyPro writes into the games** (pick one card):
   - `Update, keep the game's binds` (default, the safe one): your binds go in; where the game already uses the same
     command on that device, the game's bind stays. Nothing is removed. (engine `merge`)
   - `Update, my profile wins`: like Update, but your bind replaces the game's on a conflict. (`merge-overwrite`)
   - `Only add`: adds your binds next to the game's; nothing is replaced or removed. (`add`)
   - `Replace on my devices` (orange): the devices of your profile get exactly your binds; everything else on them,
     the game's defaults too, is removed. Other devices and the keyboard stay. JoyPro asks before each export. (`clean`)
   - `Also clear my other devices` (orange): devices without a bind in your profile lose all their binds; asks first.
3. **Export to games**: a tile per game of the profile with its cover. Tick games and press `Export ticked games`, or
   `Export now` on a tile. The DCS tile opens with `Aircraft...`: only the ticked aircraft are exported. A tile shows a
   green tick and the time after a good export, a red mark and the reason after a failed one. `Undo last export`
   puts that game's files back from before the last export (asks first). `Preview` (games of JoyPro's game list)
   shows what an export would change without writing.

Every export backs up the game files first. `Options and paths (for experts)` keeps the single-game export buttons,
your own paths, DCS keyboard defaults, "clean in every other aircraft" and the Star Citizen options.

The page remembers your last choices: the mode, the options, the paths, and per profile the games and aircraft you
ticked or unticked yourself. They are back at the next start and when you open the profile again.

`Game config backups` lists every backup (game, time, files) with `Show Files`, `Preview Restore` and `Restore`
(asks first; the current files are backed up too, so a restore can be undone).

Layout pictures (PNG) are also here:
- Set the layout export folder (`Browse...`), the devices, games and aircraft (`All` / `None`).
- Export overview and/or per-plane PNGs.
- Optional rendering helpers:
  - control markers (button/axis/pov/slider symbols)
  - legend tables, with their width (px), rows at most and font size
  - modifier color overrides
  - per-type symbol overrides

## Validate Page

Run validation before export.

Validation reports:
- Errors
- Warnings

Use it to catch issues before writing game files.

## First Start: Your Games

On the first start JoyPro2 detects the games in the background and then opens `Your games`: what it found (and
how: Steam, known place, Windows apps list), one row per supported game with `I have this game` (ticked for the games
it found), `Browse...` to link an installation folder (portable installs, other drives; checked at once against the
game's marker files, "marker found" or "not found but kept") and, where the game allows it, `Browse...` for the
config location (for example `Saved Games\DCS.openbeta`). `Done` writes it all through the engine; found games you
unticked (and games marked `I don't have it`) are hidden everywhere (export page, game lists). Games JoyPro did not
find start unticked but are not hidden, so they are found once installed. `Later` changes nothing. The window is shown once; Settings > Games >
`Run setup again` shows it again. Details: [docs/installs.md](../../docs/installs.md#first-start).

## Settings Page

The page is a list of sections that open and close: click a section's title (the arrow shows open or closed). At
first only `Games` is open. `Open all` / `Close all` at the top; the line next to them says how many are open. The
tabs under the title name every section: a click opens it and scrolls to it. Move a section by dragging its handle
(the dots left of the title) or its tab, or with its `...` menu (Move up / down / to the top / to the bottom); the
arrow button at the top brings back JoyPro's order. The search box shows only the sections with a matching setting
(every word must match: titles, labels, buttons, tips), opened while you search. JoyPro remembers the order and the
open sections for the next start.

`Games` lists every supported game: installed or not, its folder, how JoyPro found it and where it keeps
its controls. `Choose folder...` makes a folder the game's installation (for a game JoyPro did not find); `Use
detected` forgets it again. `Hide` hides a game everywhere in JoyPro; the hidden ones are listed under "Hidden games" with `Show again` (the
only place that lists them). `Run setup again` opens the first-start setup. Games that are not installed are
blocked for import, export, scan, DB refresh and backup restore. Details: [docs/installs.md](../../docs/installs.md).

`Appearance`: Light, Dark, System or a theme of your own; `Text size` and `UI scale` (80 % to 200 %, on top of the
Windows display scaling; Ctrl+Plus / Ctrl+Minus / Ctrl+0 change the text size). The `Theme editor` makes a theme from
Light or Dark: every colour of JoyPro (pages, text, buttons, chips, banner, bottom bar, charts, markers) with a colour
picker, the font and the font of key caps, weight, line spacing and heading size, a live preview, and a warning with
`Fix the contrast` when a text would be hard to read. `Export...` saves a theme as a file to share; `Import...` takes one
in. Details: [docs/ui/themes.md](../../docs/ui/themes.md).

`Confirmations`: the questions JoyPro asks before it changes something hard to take back, with a switch for each
question that can be turned off (removing an action from a relation; an import that drops changes not exported yet).

`About JoyPro and updates`: the version, build date and commit, the license and third-party notices, the project and
release pages, `Check for updates` and `Check for updates at start`. A newer version opens a dialog with what changed:
`Update now` (download, check, install, restart), `Later` or `Skip this version`. The version is also in the bottom
right corner of the window (a click opens this section) and `Check for updates` in the top bar's `More` menu.
Details: [docs/updates.md](../../docs/updates.md).

`Startup`: "Open the last profile at start" (on unless you untick it) opens the profile you had open last, after the
window shows. Below it: how the profile history thins out old versions.

`Drawings`: how JoyPro's own drawings of your devices look. `Drawing style`: Lines or Cel-shaded (flat colours under
bold outlines, the default). `Colours`: JoyPro (follows the theme: light lines on JoyPro's dark surface with blue
leader lines, dark lines on the light one), Manual (white on black), Blueprint, Print (black on white) or Custom; the
three boxes take your own line, page and leader colours as `#RRGGBB` (empty = the preset's). The first editor is for
the Layout page and the device cards, the second (`Cheat sheets and exports`) for the cheat sheets; the cheat sheet
export options show the same second editor.

### Game changes and the system tray

When you change controls in a game that JoyPro exported to, JoyPro notices it after the game has closed and asks
whether to take the changes into your profile: `Take over`, `Not now` (asks again later) or `Ignore these` (your
profile stays; the next export writes it over the game's changes). `Settings > Games > Watch game configs` turns
this on or off for all games; the `Watch for changes` box on each game card on the Start page picks the games (the
tray helper follows the same boxes). Details: [docs/sync-watcher.md](../../docs/sync-watcher.md).

JoyPro can only notice changes while something of it runs. So that you do not have to keep JoyPro open, a tiny
helper can keep watching from the **system tray** (the small icons next to the clock; Windows may hide them behind
the `^` arrow). The helper is not the full JoyPro: it uses no noticeable CPU and only about 12 MB of memory.
`Settings > Run in the background (system tray)`:

- `Start with Windows (in the tray)`: the helper starts by itself when you sign in to Windows; JoyPro's window does
  not open. No administrator rights are needed. Untick it to remove it from Windows' startup again. If you turned
  JoyPro off in Windows' own `Settings > Apps > Startup`, the line under the box tells you; switch it on there again.
  Works for portable copies too (the copy you tick it in is started).
- `Close to tray`: when you close JoyPro (X), the helper goes on watching. Untick it and closing JoyPro ends the
  watching too. Ticking `Start with Windows` ticks this as well (you can untick it).

While JoyPro is open it watches the games itself; the helper waits (its icon stays) so you are never asked twice.

The tray icon: click it to open JoyPro. Right-click it for `Open JoyPro2`, `Watching game configs` (on/off; greyed
while JoyPro is open, change it there) and `Exit` (ends the helper). When a game changed controls JoyPro manages
while JoyPro is closed, Windows shows a message from the helper ("A game changed its controls"). Click it (or the
tray menu's `Review the changes made in ...`, or the icon) and JoyPro opens with the questions. Ignore it and the
menu entry stays until you review or the next start of JoyPro asks. The message never takes the focus away from a
game.

JoyPro runs only once: starting it again while it is already running brings the open JoyPro to the front.

### Devices
- A card per device: connected devices with their product picture, axes, buttons and hats, and devices your profile
  uses that are not connected now (orange triangle). `Look again` reads them again.
- Devices that come back with a new ID (your profile from an old PC, a Windows reinstall, another USB port) get their
  binds by themselves when the profile opens: the same product, connected and not used yet. A notice at the top lists
  what moved, with `Undo`; devices where more than one fits (two identical pedals) are named there and left to you.
- `Replace...` on a card (also `Replace device...` below the cards, in the notice, on a device of the Layout page and in
  the Binds page's `More`) opens the Replace device window: the old device (also one that is not connected) and the
  device that gets its binds. The preview lists every input: the same product keeps its input ids; another device is
  mapped by its parts from the device catalog (trigger to trigger, hat 2 to hat 2, roll to roll; buttons without a
  matching part by their order, marked as a guess). Change any input, or leave it on the old device; inputs that do
  not fit stay on the old device. When the new device has binds already, choose: keep both, keep the new device's,
  or replace them. `Move binds` is one change: `Undo` (notice or top bar) takes it back.
- `Remove` on a card removes that device's binds from your profile (asks first; a copy is kept).
- `Device lists and names (for experts)`: the plain lists, the input catalog and `Device Details` (VID:PID, HID name,
  DirectInput axes/buttons/hats in the output panel).

### DCS: its own default binds on your sticks
- DCS puts its own default binds on every stick it knows. `Move DCS stick defaults away` so only your binds are
  used; `Restore DCS defaults` brings them back. Close DCS first; both ask. `Find the folders` fills the paths.

### Command lists (for experts)
- Normally a game's command list is updated with `Update` on its card (Start page).
- `Command lists (DB) and game paths: information for experts`: DB summary, check, games, planes, fingerprint, game
  paths.
- `Command lists of DCS, Star Citizen and IL-2 (for experts)`: only some DCS aircraft (`All` / `None`), headless or the
  old click mode (`Clicks XML`, `Clicks Dir`, `DCS EXE` are prefilled), dry runs; Star Citizen `Branch` (`LIVE`,
  `PTU`, ...; empty: the first one found); IL-2.

### Force feedback settings of the games
- Game, device and aircraft pickers; every setting as a slider or switch with its range; `Apply`, `Revert`, `Clear`.

### DCS force feedback: swap and invert per device
- If your force feedback stick pushes the wrong way in DCS: tick `Swap Axis` / `Invert X` / `Invert Y` for it and
  `Apply`; `Clear` gives the device DCS's defaults back. `Clear All` asks first.
- Filter by name, `Connected only` / `Profile only`, quick selection buttons, `Auto-apply toggle changes`.
- `Manual single-device (advanced)` for a device name typed by hand.

### Report a Problem
Something went wrong? Four steps, nothing is sent by itself:
1. Write into `What happened?` what you did, what you expected and what happened instead.
2. Leave `Include my open profile and its history (your binds and device names)` ticked (on by default, remembered)
   unless you do not want to send your binds.
3. Press `Create Report`. JoyPro shows what goes in, then saves one zip (in `Documents\JoyPro2\Problem Reports`) and
   shows it in Explorer. It holds your text, the JoyPro and Windows versions, JoyPro's logs of at least the last 14
   days, errors and crash reports, JoyPro's settings and versions, your devices with vJoy and HidHide, and per game its
   folders and JoyPro's backups; with the box ticked also the open profile, its saved versions and the games' current
   bind files. It stays on your PC until you send it.
4. Press `Open GitHub Issue Page` (a prefilled issue opens in your browser) and drag the zip into the text box, or send
   the zip to the developer another way. `Show Report` shows the zip in Explorer again.

Your Windows user name, the path of your user folder, the PC name, e-mail addresses, passwords/tokens and Steam ids
are removed from all text files in the zip, also inside the copy of the profile. You can open the zip and read
everything before sending it. Details: [docs/logging.md](../../docs/logging.md#problem-report).

`Keep background logs for N days` (default 30; at most 200 MB together, the oldest go first) sets how long JoyPro
keeps its logs; older ones are deleted automatically. `Open Log Folder` opens `%LOCALAPPDATA%\JoyPro2\Logs`. Details: [docs/logging.md](../../docs/logging.md).

### Updates and components
- `Updates of JoyPro`: version, `Check for Updates`, `Install Update`.
- `Components` > `Needed by JoyPro`: .NET Framework 4.8 and the engine files; what is missing, `Install Missing`.
- `Components` > `Optional`: one card each for vJoy, HidHide and ViGEmBus (see below), and the local AI model
  (`Set up` opens its own download on the Relations page, Auto-build relations from all my actions > By description). Force feedback needs nothing extra.

### Components: install vJoy, HidHide, ViGEmBus with one click
These free drivers are not part of JoyPro; you only need them for the Virtual Joystick page. Each card (in Settings >
Components and on the Virtual Joystick page under "What this page needs") tells you:
- what the program does and why JoyPro needs it, and whether it is installed and running;
- where it comes from (the official installer of its project, version and size) and how JoyPro checks it;
- its license (open `License: ...` and read it).

To install: tick "I have read what ... does and its license, and I want JoyPro to download and install it", then press
`Install <name>`. JoyPro downloads the installer (progress bar, `Cancel download`), checks its checksum and its digital
signature, and starts it. Windows asks once "Do you want to allow this app to make changes?" (press Yes) and may ask
whether to install the driver (press Install). The card then shows "Installed ... and running". If it says **Restart
Windows**, save your work and restart; JoyPro never restarts the PC itself. If you press No in Windows' prompt,
nothing is installed and the card says so. `Update to <version>` appears when yours is older than the version JoyPro
offers; JoyPro never installs an older version over a newer one. `Windows apps` opens Windows' Installed apps list to
repair or uninstall a component; `Project page` opens its official page. `Check again` looks again (read-only).
Details for experts: [docs/components.md](../../docs/components.md).

## Advanced Page

For experts only: type any engine command yourself. Everything a normal user needs has its own button on the other
pages; [docs/gui-cli-parity.md](../../docs/gui-cli-parity.md) lists every engine command and its place in the GUI.

### Engine options without a GUI control
Every engine feature a user needs has a place in the GUI. These options are left to the raw command box on purpose
(developer, research or diagnostic tools, or covered another way):
- `binds tuning models` / `binds tuning preview`: the GUI draws the same curves itself (the models are built in).
- `<game> catalog`, `devices inputs --include-off`, `db list|check --missing --keyboard --strict-names`,
  `db check-updates --no-record`, `db refresh-il2|refresh-starcitizen --db-path`, `--include-user-profiles`: DB
  maintenance and diagnostics.
- `dcs refresh ... --menu-wait-seconds --settle-seconds --ocr-menu-timeout-seconds --modules-to-scan --default-stick
  --gui-scale --window-*`: tuning of the in-game (OCR) DCS refresh, which starts DCS; the normal refresh reads the
  game's files.
- `binds set --format dcs|il2`, `--clear-input`: input spelling conversion; the GUI picks inputs and has `Clear` with
  input only.
- `relations suggest --limit`: the GUI lists every suggestion.
- `profile merge` with more than two profiles, `--mode ask`: merge twice; the GUI cannot answer prompts.
- `dcs defaults cut|restore --backup-path`: the default backup folder is used.
- `--out` and the confirmation options: the GUI reads the output itself and never asks on the command line.

## Good Beginner Workflow (Recommended)

1. `Start`: `Import` on the card of a game you already set up (the wizard makes a profile for you).
2. `Relations`: check the relations, add actions from the search over every game (tick, Add selected), take what is suggested, or ask the JoyPro assistant; `Auto-build relations from all my actions` does the whole profile. On the Binds page `Put relations on my buttons automatically` binds them.
3. `Modifiers`: create shift buttons if you want them.
4. `Binds`: assign or record buttons and axes; curves in `Axis Tuning`.
5. `Export`: `Check now`, keep "Update, keep the game's binds", tick the games, `Export ticked games`.
6. Something wrong in the game? `Undo last export` on its tile.

## Small Example (DCS A-10C_2 + FA-18C_hornet)

1. Start page: `Import` on the DCS World card.
2. Untick `All my DCS aircraft`, tick `A-10C_2` and `FA-18C_hornet`, keep `Add to my profile`.
3. `Import now`, then `See my relations`.
4. In `Relations`, search `wheel brake` and make sure your brake relation exists.
5. In `Binds`, bind the brake relation to your paddle.
6. `Export`: `Export now` on the DCS tile.

## Troubleshooting

- Button does nothing:
  - Check status line and Engine Output panel.
  - Make sure profile path is valid.
  - Check if required fields are empty.
- Relation add fails:
  - You must provide a source (`DB node` or `Copy from relation`).
- Can not create modifier:
  - Name or input is duplicate, or fields are missing.
- A change went wrong, or you want yesterday's profile back:
  - `Undo` (Ctrl+Z), or `History` > `Restore` on the version you want (that can be undone too).
- Anything else, or JoyPro crashed:
  - Settings > `Report a Problem` (see above): the zip contains the logs the developer needs.
  - Your changes are on disk: every change is saved by itself, the file is never half written.

## Keep Docs Up To Date

- Engine command docs are generated from live `engine --help`.
- GUI docs should be updated whenever a button/flow changes.
- Keep examples synced with current UI labels.
