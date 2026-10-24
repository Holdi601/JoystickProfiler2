# Group by description

You write in English what belongs together, and JoyPro puts the matching commands of all aircraft and games into
relations:

- "speed brake in and out on all jets"
- "put gear up/down of every DCS warbird"
- "all trim hat up actions for helicopters"
- "flaps for all MSFS airliners and IL-2 bombers"
- "CMS forward for all DCS jets except the A-10"
- "lower the gear and raise the flaps on the F-16 and the Hornet"

- GUI: Relations → **Auto-build relations from all my actions...** → **By description** → type the description → **Preview** → untick what
  you do not want → **Create**.
- For one open relation, its chat box does the same per relation (`engine relations chat`, see
  [Relation editor](relation-builder.md#say-it-in-words-local-ai-model)).
- CLI: `engine relations from-text "<request>" [--games g1|g2] [--planes p1|p2] [--llm off|auto|on] [--substitutes]
  [--select <relation>]... [--skip-node game:id:plane1|plane2]... [--name <name>] [--min-confidence 0..1] [--dry-run]`.

## How it works

1. **Offline rules (always on).** A deterministic parser reads the request with the words in
   `DB/Functions/language.json`:
   - **functions** and their directions: speed brake/SB/airbrake/spoilers (out, in, toggle), gear/undercarriage (up,
     down, toggle), flaps (up, down, take-off, step), trim (nose up/down, left/right, reset, force trim), trim hat (hat
     up = nose down, as on the stick), CMS (forward/program/flare, aft/chaff, left, right, depress/ECM), TMS, DMS,
     master arm (on/off/toggle), NWS, canopy, tail hook, launch bar, wing fold, refuel probe, afterburner/AB/WEP,
     reverse thrust, drag chute, anti-skid, parking brake, wheel brakes, pitch/roll/rudder axes, throttle, zoom, TDC
     (axes, slew, depress), weapon release/pickle, trigger stages, gun/heater/MRM select, uncage, jettison, kneeboard,
     exterior/landing/navigation/anti-collision/formation lights, instrument/flood lights, flashlight, NVG, autopilot
     (on, off/disconnect, altitude/attitude hold, route, hover), radar (on, standby, lock, unlock, elevation,
     azimuth, TWS/RWS), sensor select (castle), china hat, waypoint next/previous, engine start/stop, APU,
     radiator, prop pitch/RPM, mixture/nozzle, tailwheel lock, comm switch/PTT/intercom/IFF, master caution, FBW
     disconnect, laser, HMD, targeting pod, seat change, master modes and crew AI;
   - **games**: DCS, MSFS (both MSFS 2020 and 2024; "MSFS 2020" / "MSFS 2024" for one), IL-2 (Great Battles), IL-2 Korea,
     War Thunder (WT), Falcon BMS, Star Citizen, X-Plane (11 and 12; "X-Plane 11" / "XP12" for one), Elite Dangerous,
     X4, VTOL VR, Nuclear Option, Arma 2/3/Reforger ("Arma" for all), WARDOGS, IL-2 1946, IL-2 Cliffs of Dover (and Blitz),
     Project Wingman, ACE COMBAT 7/8, Battlefield 6/2042, Tiny Combat Arena, G-Rebels, Hell Let Loose: Vietnam, FSX (SE),
     Prepar3D v4/v5/v6, with short names ("msfs24", "xp12", "wt", "sc", "elite", "bms", "p3d") (a game word with
     `members` in language.json stands for several games);
   - **space words**: strafe up/down/left/right/forward/back, jump drive (FSD, frame shift, hyperspace, supercruise,
     quantum, travel drive), decoupled / flight assist, cruise control, shields, power distribution; "boost" is the
     afterburner / ship boost;
   - **aircraft classes**: jets, prop aircraft, turboprops, warbirds, helicopters, fixed wing, spaceships, airliners,
     general aviation; roles (fighters, bombers, attack, multirole, trainers, transports, attack/utility
     helicopters, aerobatic); eras (WW2, Korea, Cold War early/late, modern); carrier, VTOL and multi-crew aircraft;
   - **aircraft names**: catalog names ("F-16C_50", "f16", "F-16"), families ("Mi-24", "A-10") and nicknames
     (Viper, Hornet, Hog, Tomcat, Apache, Huey, Hind, Black Shark, Spitfire, Mustang, Dora, Corsair ...).

   Text is normalised (case, punctuation, "f16" = "F-16" = "f 16", "F/A-18" = "FA-18", "A/G" = "A2G", plurals), the
   longest phrase wins ("passenger jet" is an airliner, "trim hat" beats "trim"). A direction belongs to the nearest
   function and counts for all words of that function ("push to talk radio 1" = Comm 1 only; "lower the gear and
   raise the flaps"). "in", "on" and "for" before "all", "every" or a game name are prepositions, not directions.
   Countermeasures are understood the way pilots say them: "flares", "pop flares", "flare button", "IR decoys" = CMS
   forward (program / flare); "chaff", "radar decoys" = CMS aft; "countermeasures", "CMS", "CMDS", "dispenser",
   "decoys", "ALE-47" = all CMS directions; "jammer"/"ECM" = CMS depress. Filler words ("pop", "release", "switch",
   "hat", "lever", "system") do not count as not understood.
   "except", "without", "but not" start the part that removes aircraft. Classes of the same kind are alternatives
   ("jets and helicopters"), different kinds narrow ("modern jets", "WW2 fighters and bombers"); a game before or
   after classes restricts them ("MSFS airliners", "jets and helicopters in DCS"). No aircraft named = all
   aircraft and games. Without a direction a function takes its usual directions (speed brake: out, in, toggle).
   If no function word matches, JoyPro looks the words up in the catalog's function names, the control descriptions
   and what each function does (`function_info`, below); a word that names a function ("bomb" in "bomb release")
   counts twice (lower confidence).
   `function_info` in `language.json` says for every function which cockpit system it belongs to, what it does on
   the aircraft and its close relatives (flares: CMS, chaff). The relatives are offered to the local model and named
   for aircraft that lack the asked function ("has CMS Aft").
2. **Function catalog.** Every function is a slot of `DB/Functions/slots.json`; the per-game `<Game>.tsv` files give
   each aircraft's command for it (checked against the manuals). A game without a `<Game>.tsv` of its own (FSX, Prepar3D, IL-2 1946,
   Cliffs of Dover, Project Wingman, ACE COMBAT, Battlefield, Tiny Combat Arena, G-Rebels, Hell Let Loose: Vietnam, any
   game added later) gets a table derived from the [function map](relation-builder.md#function-map): per slot its best
   action of that function and direction, status `derived` (`EngineFunctionTable`); a slot a game's table leaves out
   takes the function map's action, with a lower confidence and the reason in the explanation. Only an aircraft's own commands are used;
   `--substitutes` also takes the catalog's stand-ins. DCS commands must exist in the local DCS database, the others
   in the game's catalog.
3. **Proposals.** One relation per function and direction, named after it and the aircraft ("Speed Brake Out
   (jets)"). Every node carries a **confidence** (catalog status x how well the aircraft fits x how the function was
   found) and an **explanation** per aircraft (what the control does in that cockpit, the game's own name and
   category of the command from its database, e.g. `F-16C_50: stick: CMS forward = "Countermeasures Management Switch
   - Fwd" [Stick; HOTAS]`, the catalog status, why the aircraft is in the set). Aircraft in the set without that function are listed ("has Speed Brake Toggle").
   Games whose binds are shared by all their aircraft (MSFS, X-Plane, War Thunder, IL-2 ...) are included when they
   have such aircraft, with lower confidence ("MSFS binds are shared by all its aircraft; it also has jets").
4. **Create.** Like Auto Group: an existing relation of the same name gets the missing aircraft; a command you
   already use in another relation stays there ("Kept"); commands you switched off stay off; new relations get the
   group `FromText`; the profile is saved. `--select` creates only the named relations, `--skip-node` leaves out
   single commands; nodes below `--min-confidence` (default 0.5) are not added. `--dry-run` only shows.

`engine relations from-text --check` tests `language.json` against the catalog (unknown slots, aircraft or games).

## Optional local AI model

The offline rules are enough for the usual phrasings. For unusual wording ("the thing that slows the jet down",
"flares for the Turkey", "decoys for Russian helicopters") an open-source language model can help. It is **not part
of JoyPro and not downloaded** until you click Download, or you point JoyPro at a llama.cpp and model you already
have.

- It only **chooses**: JoyPro gives it the request and numbered lists the offline matcher built, and the model answers
  with ids from these lists, forced by a GBNF grammar (`{"note":"...","functions":[...],"include":[...],"exclude":[...]}`,
  the note first: one sentence of what it understood before it picks). JoyPro checks every id against the lists and
  the catalogs and drops anything else; with no valid function or aircraft the offline result stays. Classes and
  games picked together narrow each other like in a request (WW2 + IL-2 = IL-2's WW2 aircraft); "all" counts only
  alone. The commands still come from the catalog.
- `--llm auto` (the default once the model is on) asks it only when the offline rules found no function, used
  catalog words, or left words they did not understand; `--llm on` always asks; `--llm off` never.
- It runs **only while you use it**: JoyPro starts `llama-server` on 127.0.0.1 on a free port with one slot
  (`-np 1`) and waits until the model is loaded. From the command line the process is killed after the request (a
  Windows job object kills it with the engine, too). From the app (`JOYPRO_LLM_OWNER_PID`) the model stays loaded for
  the next request and the app stops it after the idle minutes (default 30, Settings > Local AI; it only counts while JoyPro does no AI work: a request, an answer streaming in or a plan being made or applied holds it, and it starts again when the last one ends. A ui-settings.json of 2.2.17 or older with the old default 2 gets 30; another value the user chose stays), with **Stop now**,
  and when JoyPro ends (the app's kill-on-close job, also on a crash). `<data>\Llm\server.json` describes the running
  server; the panel and a chip next to the AI buttons show it with its video memory (`nvidia-smi`, else the working
  set). `engine llm server` shows it, `engine llm stop` ends it, `engine llm start` loads it now (Settings > Local AI >
  **Start now**; from the app it stays loaded until the idle stop).
- **One place, one status line**: the whole setup (on/off, model, runtime, download, own files, Start now / Stop now,
  idle minutes) is **Settings > Local AI**. Every AI action (Manage with AI, Edit with AI, Expand with AI, the JoyPro
  assistant, Group by description, AI fill) shows the same line, e.g. "AI: on (Qwen3 8B) - not loaded right now, starts
  with your next request" / "AI: on (Qwen3 8B) - loaded, 5.3 GB, stops in 1:47" / "AI: off", with **Turn on the local AI**
  (a model is set up) or **Set up the local AI** and a **Settings** link. The app reads the setting (`engine llm status`)
  when an AI panel opens and before every AI request. Settings > Components shows the same state (green check when set
  up and on).
- **Privacy**: everything runs on your PC. The request never leaves it. The only network access is the download you
  start (Hugging Face for the model, GitHub for llama.cpp).

### What the model sees

`engine relations from-text "<request>" --show-prompt [--prompt-model <model>|local]` prints the whole conversation
and grammar without running a model.

- **System prompt** (`DB/Llm/models.json` `prompt.system`): JoyPro's task (group the same cockpit function of many
  aircraft so one button does it everywhere), the parts of the message, and the rules: match by meaning (flares = CMS
  forward, chaff = CMS aft, pickle = weapon release), a function without direction means its usual directions, a
  named direction only that one, keep the rules' aircraft unless they missed something, a single aircraft only when
  sure, class + game narrow each other, exclusions, note first, ids only.
- **Few-shot examples** (`prompt.examples`): three turns in exactly the format of the real prompt with the expected
  answer (an aircraft nickname the rules do not know, an exclusion, a description without function word plus class +
  game).
- **The request**, twice (top and bottom), and the **rule-based reading** ("CMS Forward (Program / Flare) for
  F-16C_50; words not understood: rhino").
- **Functions**: a heading per function with its cockpit system, other words for it and what it does on the
  aircraft (`function_info`), then one line per direction with button/axis, how many aircraft have it and example
  commands with the game's own name and the cockpit control:

  ```
  # Flares (countermeasures (CMS/CMDS dispenser); also: ir decoy, infrared decoy, heat decoy): drops flares (infrared
    decoys against heat-seeking missiles); in most cockpits this is CMS forward or the flare button ...
  F1 CMS Forward (Program / Flare) [button; 57 aircraft; e.g. F-16C_50 "Countermeasures Management Switch - Fwd"
    (stick: CMS forward), FA-18C_hornet "Dispense Switch - Aft(FLARE)/Center(OFF)" (throttle: dispense switch aft)]
  # Chaff (...)  F2 CMS Aft (Panic / Chaff) [...]
  # CMS (...)    F3 CMS Left, F4 CMS Right, F5 CMS Depress / ECM [...]
  ```

  Candidates: what the rules read, every direction of the named functions, their relatives ("flares" brings the
  whole CMS hat and chaff), and, only when some words were not understood, catalog functions found by word search
  (at least 40 % of the best score).
- **Aircraft, classes and games**: S1 all; the rules' reading ("request: IL-2 warbirds"); every class with its
  aircraft count and other words ("attack helicopters (25 aircraft; also: gunship, attack helo)"); every game (DCS
  modules with their own binds, the other games one bind set); single DCS aircraft with nicknames and kind
  ("F-14 (DCS; tomcat, turkey; carrier, jet, multicrew, fighter, cold war late)") - those sharing a word with the
  request first, all of them only when some words were not understood.

Per model (`models.json`): context size, how many function and aircraft lines (`maxFunctions`, `maxScopes`),
example commands per function (`examplesPerFunction`), few-shot examples (`fewShot`) and answer length
(`maxTokens`). JoyPro estimates the prompt size (about 3.2 characters per token) and shrinks it until it fits the
context minus the answer: fewer command examples, fewer few-shot examples, fewer single aircraft, fewer functions.

| Model | Context | Functions / aircraft lines | Command examples | Few-shot | Answer tokens |
|---|---|---|---|---|---|
| Qwen2.5 0.5B | 2048 | 10 / 40 | 1 | 1 | 120 |
| Qwen2.5 1.5B | 4096 | 16 / 70 | 1 | 2 | 160 |
| Qwen3 4B | 6144 | 24 / 100 | 2 | 3 | 200 |
| Qwen3 8B | 8192 | 32 / 140 | 2 | 3 | 220 |
| your own GGUF | 8192 (`--context`) | 32 / 120 | 2 | 3 | 240 |

Sampling for every model: greedy and repeatable (temperature 0, top-k 1, top-p 1, min-p 0, seed 7), **no
repetition penalty** (ids, quotes and brackets repeat on purpose; older llama.cpp builds default to 1.1), no prompt
cache; the grammar ends the answer after the closing brace. The catalog models get a ChatML prompt (`/completion`);
Qwen3 runs in non-thinking mode (`/no_think` and an empty think block). Your own GGUF gets its own chat template from
the server (`/v1/chat/completions` with the messages, `chat_template_kwargs: {"enable_thinking": false}`); `--template
chatml` forces ChatML.

### Model choice

JoyPro detects RAM (GlobalMemoryStatusEx), CPU cores and AVX/AVX2/AVX-512 (IsProcessorFeaturePresent) and the
graphics cards with their memory (display adapter registry, 64-bit memory size), and recommends:

| Hardware | Model | Download | Runs on |
|---|---|---|---|
| GPU with 8 GB or more, 8 GB RAM | Qwen3 8B, Q4_K_M | 4.68 GB | GPU (Vulkan build, all layers) |
| GPU with 4 GB or more, 8 GB RAM | Qwen3 4B, Q4_K_M | 2.33 GB | GPU (Vulkan build, all layers) |
| No such GPU, 16 GB RAM, 8 cores, AVX2 | Qwen3 4B, Q4_K_M | 2.33 GB | CPU |
| No such GPU, 8 GB RAM, AVX2 | Qwen2.5 1.5B Instruct, Q4_K_M | 1.04 GB | CPU |
| 4 GB RAM or no AVX2 | Qwen2.5 0.5B Instruct, Q4_K_M | 469 MB | CPU |
| Less than 4 GB RAM | none (offline rules only) | - | - |

A graphics card counts with 2 GB of own memory or more; integrated graphics share the system memory and do not count
even when they report a memory size ("AMD Radeon(TM) Graphics" of Ryzen CPUs, Intel UHD/Iris/HD Graphics). You can
pick another model in the list. All four are Apache-2.0 (Qwen2.5 3B is left out: its license is not Apache).

The runtime is llama.cpp (MIT). **auto** = the Vulkan build with a graphics card (NVIDIA, AMD, Intel Arc; about 60 MB
and fast enough for one short answer), else the CPU build (about 30 MB). **cuda** (NVIDIA, optional) = the CUDA build
plus the CUDA runtime DLLs of the same CUDA version (about 600 MB). `engine llm recommend --ram-gb 16 --vram-gb 0
--cores 4 --avx2 true` shows the choice for other hardware.

### Download (one click)

- GUI: **Settings > Local AI** (also from the **Settings** link of every AI status line and Settings > Components >
  **Set up**): the recommended model is preselected; read size and
  licenses (model and runtime), tick **I accept**, click **Download and switch on**. That one click downloads
  llama.cpp (the runtime chosen in **Runtime**: auto, vulkan, cuda or cpu) and the model with a progress bar, checks
  both SHA-256 checksums and switches the model on. **Cancel download** stops it, the next click resumes it.
  Choosing another model or runtime asks for consent again. **Remove model** deletes it.
- CLI: `engine llm status`, `engine llm download [<model>] --accept-license [--runtime auto|cpu|vulkan|cuda]`,
  `engine llm cancel`, `engine llm remove [<model>] [--all]`, `engine llm enable [<model>|local]`, `engine llm
  disable`. Without `--accept-license` the download only prints size, source and licenses.
- The model URL is pinned to a Hugging Face revision; size and SHA-256 are checked. llama.cpp comes from the
  official GitHub release; its SHA-256 is taken from the release metadata, and a release without one is refused.
  Downloads resume from `<file>.part` (HTTP Range); a file with a wrong checksum is deleted.
- Files: `%LOCALAPPDATA%\JoyPro\Llm` (or `JOYPRO_DATA_DIR\Llm`): `models\`, `runtime\<cpu|vulkan|cuda>\`,
  `settings.json`, `download-progress.json`.

### Your own llama.cpp and model

If you already have llama.cpp and a GGUF model, nothing needs to be downloaded:

- GUI: **Already have llama.cpp and a GGUF model?** → **Find on this PC** fills in the `llama-server.exe` and the
  model that suit the hardware (or **Browse...**), **Use these files** switches the model on (until then the section
  says the files are not in use yet, and "In use: ..." names the model the next request starts). **Stop using them**
  forgets them.
- CLI: `engine llm find-local [--root <folder>]...` lists what it finds; `engine llm use-local [--server
  <llama-server.exe>] [--model <file.gguf>] [--gpu-layers auto|all|N] [--context N] [--template server|chatml]`
  uses them (without paths: what find-local recommends); `engine llm use-local --clear`.
- The search is read-only and limited in depth and time (25 s): the PATH, `llama.cpp`, `llama`, `AI`, `LLM`,
  `models` and `tools` folders on every fixed drive, `%USERPROFILE%\llama.cpp`, `models` and `Downloads`, the
  Hugging Face cache, LM Studio's model folders, JoyPro's own model folder and `JOYPRO_LLM_SEARCH` (folders separated
  by `;`). Vision projectors (`mmproj`), embedding/reranker and vocab files, later parts of split models and files
  under 50 MB are skipped; Ollama's internal `llama-server` is skipped (its blobs are no `.gguf` files).
- Best pair: the largest model that fits completely on the graphics card (file size x 1.1 + 1 GB), else the smallest
  that fits the memory; a `llama-server.exe` with a GPU backend (`ggml-cuda.dll`, `ggml-vulkan.dll`, ...) when there
  is a graphics card.
- JoyPro starts it with `-m`, `--host 127.0.0.1`, `--port`, `-c`, `-np 1` and `-ngl` only when you set GPU layers
  (else llama.cpp fits the layers to the free graphics memory itself).

### Real model check

`engine/tests/manual/nlgroup_real_llm.ps1 -Server <llama-server.exe> -Model <file.gguf> [-RequestFile <file>]` runs
every request offline and with the model (temporary data folder, nothing downloaded) and prints a table. It kills
every llama process it started also on errors and checks with `tasklist` that none is left.

Result on the developer's PC (RTX 5080 16 GB, `C:\llama.cpp` CUDA build 11430, `Qwen3.8-27B-UD-Q3_K_XL.gguf`, your-own-model
path, `--llm on`; about 10-15 s per request including loading the 12 GB model; no llama process left afterwards):

| Request | Offline rules | With the model |
|---|---|---|
| flares | CMS Forward (Program / Flare), all aircraft | same |
| chaff and flares on the F-16 and F/A-18 | CMS Aft + CMS Forward, F-16C_50 + FA-18C_hornet | same |
| speed brake for all jets | Speed Brake Out/In/Toggle, jets | same |
| trim hat for warbirds in IL-2 | Trim Hat Up/Down/Left/Right, IL-2 warbirds | same |
| master arm | Master Arm On/Off/Toggle, all aircraft | same |
| gear and flaps on helicopters | Gear Up/Down/Toggle + 4 flap directions, helicopters | same |
| pop decoys on the Tomcat | all 5 CMS directions, F-14 + F-14_RIO | same |
| the button that drops bombs for WW2 bombers | Weapon Release, WW2 bombers | same |
| countermeasures on all helicopters except the Huey | all 5 CMS directions, helicopters without UH-1H | same |
| air brake on the hog and the warthog | Speed Brake Out/In/Toggle, A-10A/C/C II | same |
| the thing that slows the jet down in the air | Speed Brake Out/In/Toggle (catalog words), jets | same |
| lower the hook and fold the wings on carrier jets | Tail Hook Down + Wing Fold, carrier aircraft | same |
| flares for the Turkey | CMS Forward, all aircraft ("turkey" not understood) | CMS Forward, F-14 + F-14_RIO |
| IR and radar decoys for Russian helicopters | CMS Aft, all helicopters | CMS Forward + CMS Aft, Ka-50, Mi-24P, Mi-8 |
| pickle and gun trigger in BMS | Weapon Release + both trigger stages, Falcon BMS | same |
| switch the jammer on for all fighters | CMS Depress / ECM, fighters | same |
| nose wheel steering on the bug | Nosewheel Steering, all aircraft ("bug" not understood) | Nosewheel Steering, A-10 (wrong: the Bug is the Hornet) |
| chaff for the Hornet and the Tomcat but not the RIO seat | CMS Aft + WSO/RIO Seat (wrong), FA-18C + both F-14 seats | CMS Aft, FA-18C + F-14 without F-14_RIO |
| radio 1 for all gunships | Comm 1, attack helicopters | Comm 1, attack aircraft (wrong) |

The two wrong answers led to the last changes: "bug", "turkey", "lawn dart" are aircraft nicknames now (the rules
know them), classes show their other words ("attack helicopters ... also: gunship"), and the system prompt tells the
model to keep the rules' aircraft unless they missed something and to pick a single aircraft only when it is sure.

## Tests

`engine/tests/smoke/nlgroup.ps1` (part of the engine smoke test): a table of phrasings across DCS, MSFS, IL-2, War
Thunder and Falcon BMS with the expected functions and aircraft (including "flares" = CMS forward, "chaff and flares
on the F-16 and F/A-18", "countermeasure dispenser for helicopters" without a model); creation with
`--select`/`--skip-node`, kept commands and a second run that adds nothing; `--show-prompt` (what flares do, the
related CMS directions, the game's command names, nicknames, few-shot examples, grammar, the prompt fits the 8B
context and is shrunk for the 0.5B model); the model path through `engine llm fake-server`
(`JOYPRO_LLM_FAKE_RESPONSE`: greedy request without repetition penalty, canned answers with invented ids that must be
dropped, invalid JSON that must fall back, class + game that must narrow, one process per request that is gone
afterwards); your own llama.cpp (`find-local` with `--root`, mmproj skipped, `use-local`, the chat endpoint with
messages and thinking off, `--clear`); the model choice for injected hardware; the downloader against the loopback
stub (`JOYPRO_LLM_CATALOG`: consent, resume with HTTP Range, SHA-256 check, the CUDA build with its runtime archive,
removal). No test downloads a model. GUI: `gui/JoyPro.Gui.Tests/DescribeGroupTests.cs` (one-click download with the
chosen runtime, find and use your own llama.cpp). A real model: `engine/tests/manual/nlgroup_real_llm.ps1` (above).
