---
title: JoyPro 2
status: full
order: 0
summary: One profile for all your devices, in every flight and space game. Free, open source, for Windows.
---
<div class="hero">
<div>
<h2>One profile for all your devices, in every flight and space game.</h2>
<p>JoyPro puts the same function on the same button in every aircraft and every game: gear, flaps, countermeasures,
trim, throttle. Import what you already bound, let JoyPro fill the gaps, export back into each game's own settings.
Free and open source (GPL-3.0), no account, no telemetry.</p>
<div class="btns"><a class="btn" href="https://github.com/Holdi601/JoystickProfiler2/releases/latest">Download for Windows</a>
<a class="btn ghost" href="getting-started/first-steps.html">Your first profile in 10 minutes</a>
<a class="btn ghost" href="https://discord.gg/yK4gQA9xgk">Discord</a></div>
</div>
<div><img src="assets/shots/start-dark.webp" alt="JoyPro's Start page" loading="lazy"></div>
</div>

:::video intro "JoyPro 2 in three minutes"
:::

## How JoyPro thinks

```mermaid
flowchart LR
  I["Import<br/>your games' binds"] --> R["Relations<br/>one function, every game"]
  R --> B["Binds<br/>one button or axis each"]
  B --> C["Check<br/>in game words"]
  C --> E["Export<br/>into every game"]
  E -.->|"you rebind in a game"| W["Watcher<br/>asks to take it over"]
  W -.-> R
```

- A **relation** is one function ("Gear Up/Down") with its action in every game and aircraft.
- A **bind** is the button or axis (and modifiers) the relation is on.
- **Export** writes it into each game's own control files, with a backup first and Undo.

## Start here

<div class="cards">
<a class="card" href="getting-started/install.html"><b>Download and install</b><span>Unzip, start, done. What it needs and where it keeps things.</span></a>
<a class="card" href="getting-started/first-start.html"><b>First start</b><span>JoyPro finds your games; link portable installs.</span></a>
<a class="card" href="getting-started/first-steps.html"><b>First profile in 10 minutes</b><span>Import, relations, binds, check, export, fly.</span></a>
<a class="card" href="faq.html"><b>Help and troubleshooting</b><span>The support file, the watcher, common questions.</span></a>
</div>

## The four main flows

<div class="cards">
<a class="card" href="flows/import.html"><b>Import a game</b><span>Take the binds you made in a game into a profile.</span></a>
<a class="card" href="flows/relations.html"><b>Build relations</b><span>Search, tick, add; suggestions, Auto Group, assistant.</span></a>
<a class="card" href="flows/export.html"><b>Export</b><span>The four modes and what each changes in the game.</span></a>
<a class="card" href="flows/check.html"><b>The Check</b><span>What would go wrong in the game, with one-click fixes.</span></a>
</div>

Every page is written in layers: **for beginners** (no PC knowledge needed), **advanced** (every option) and
**technical internals** (the engine commands and the code path). The switch at the top of a page shows one layer.

## Everything else

<div class="cards">
<a class="card" href="gui/start.html"><b>Every GUI page</b><span>{{stat:gui.controls}} controls, each with what it does and what runs behind it.</span></a>
<a class="card" href="games/index.html"><b>Games</b><span>{{stat:games.handled}} titles handled, {{stat:games.titles}} researched: a page per game with what JoyPro does there.</span></a>
<a class="card" href="cli/index.html"><b>Engine command line</b><span>{{stat:cli.commands}} commands, every option and value.</span></a>
<a class="card" href="project/jp1-vs-jp2.html"><b>From JoyPro 1 to 2</b><span>What improved, with the numbers.</span></a>
</div>

<div class="stats">
<div><b>{{stat:games.handled}}</b>game titles handled</div>
<div><b>{{stat:languages}}</b>interface languages</div>
<div><b>{{stat:cli.commands}}</b>engine commands</div>
<div><b>{{stat:jp2.tests}}</b>automated tests</div>
</div>

## Community and licence

- Ideas, questions, problems: the [JoyPro Discord](https://discord.gg/yK4gQA9xgk) or an issue on
  [GitHub](https://github.com/Holdi601/JoystickProfiler2/issues).
- JoyPro is free software under the GNU General Public License, version 3 or later. It comes with no warranty.
  JoyPro backs up every game file before it writes one; keep your own backups anyway.
- This site is generated from JoyPro's source code and documentation, so it describes the current version.
  [Wiki status](project/wiki-status.html) shows how complete it is.
