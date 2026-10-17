# Suggest additions

Suggest additions reads a relation (its name and its commands) and lists what is still missing, each with the
reason. It also flags entries that look wrong, such as the gear lever of an aircraft in a UFC button relation (an
older auto-fill put those there). It works for every game JoyPro supports and needs no AI.

- GUI: the relation editor's **Suggested for this relation** list (Relations page, the open relation; see
  [Relation editor](relation-builder.md)). One click **Add**, **Add all high**, **x** = dismiss (**Never**),
  **Move here** for a command another relation uses, **Looks wrong in this relation** with **Move to '<relation>'** and
  **Remove**, **Next relation** to the next relation that can grow. It reloads after every change.
- CLI:
  - `engine relations suggest <relation> [--min high|medium|low] [--limit N] [--game g1|g2]` (the text output lists
    the relation's weighted functions under "Meanings", dismissed commands with `x` and wrong entries with `!`)
  - `engine relations suggest --all [--min ...]` (every relation with suggestions)
  - `engine relations suggest apply <relation> [--add game:id:plane1|plane2]... [--never game:id:plane1|plane2]...
    [--remove game:id:plane1|plane2]... [--move game:id:plane1|plane2=>Relation]... [--take game:id:plane1|plane2]...`
    (`--never` dismisses: added switched off, never proposed again; `--remove` restores a dismissed command; `--take`
    moves a command out of every other relation into this one)

## Where the suggestions come from

| Source | Confidence | Example reason |
|---|---|---|
| Function catalog (`DB/Functions`, checked against the manuals) | high (catalog substitute: medium); lower when only some of the relation's commands are that function | "Same function as A-10C 'HOTAS DMS Aft': 'HOTAS DMS Aft' is the 'A10 - DMS Down' function in A-10C II (function catalog, checked against the manuals)." |
| Your style: what you put on the function where an aircraft lacks it (learned like Auto Group, two separate decisions or more) | medium | "Your style: where an aircraft has no 'CMS FWD', you use its 'TMS Up' (3 times in your relations); AV8BNA." |
| Function map (`DB/Functions/function-map.json`, every game and aircraft, also where the catalog has no entry; same direction and kind) | high when both readings are sure and the title names the function, else medium | "Same function: Throttle axis, like F-16C_50 'Thrust'; 'Flight Control Collective' is it in UH-1H ('collective', JoyPro function map)." |
| Same DCS command id in more aircraft | high with the same title, else medium | "Same DCS command id in F/A-18C as in F-16C_50, same title." |
| Similar title in any game (at least two shared words, never the opposite direction) | medium or low | "Title matches 'HOTAS Coolie Switch Down' (87% of the words)." |

- The relation's function comes from its name (Auto Group names relations after the function) or from what its
  switched-on commands are in the catalog: the union of their functions, weighted by how many agree (half of them or
  more = full weight; below 20 % ignored). Dismissed (switched-off) commands do not count.
- One row holds every aircraft that has the same command, so one click can add a command to dozens of aircraft.
- A relation that Auto Group split ("[Fixed wing]", "[Rotary wing, WW2]", per aircraft) only gets aircraft of its own
  split.
- A command another relation already uses is still listed, with "Already in '<relation>'"; the editor offers **Move
  here** instead of Add, and **Add all high** leaves it out.

## What looks wrong

| Case | Example reason |
|---|---|
| The function catalog files the command under another function (a substitute for this function is fine) | "The function catalog files 'Landing Gear Up' under 'GearIn' in MosquitoFBMkVI_easy, not 'UFC__0'." |
| The command goes the opposite way of the relation (only when the catalog does not know the command) | "'Trim Nose Down' is the opposite direction of 'Trim Nose Up'." |

**Move** takes the aircraft out of this relation and adds them to the relation that has the command's function (the
bind of that relation applies from then on). **Remove** only takes them out. Both change the profile, not a game file.

## Pictures

DCS rows show the aircraft's module icon; other games show their box art. Both come from `engine media`, see
[Export](export.md#pictures).
