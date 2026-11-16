---
title: JoyPro 2
status: full
order: 0
summary: One profile for all your devices, in every flight and space game. Free, open source, for Windows.
---
:::hero shot:start
## One profile for all your devices, in every flight and space game.

JoyPro puts the same function on the same button in every aircraft and every game: gear, flaps, countermeasures,
trim, throttle. It can set up your stick, throttle and pedals by itself. Free and open source (GPL-3.0), no account,
no telemetry.

- [Download for Windows](https://github.com/Holdi601/JoystickProfiler2/releases/latest)
- [[page:getting-started/quick-start.html|Quick start: make my stick work]]
- [Discord](https://discord.gg/yK4gQA9xgk)
:::

## I want to ...

:::cards
- [[page:getting-started/quick-start.html|Make my stick work]]: JoyPro sets it all up for you. You only look and click.
- [[page:how-do-i/xbox-only-games.html|Play ACE COMBAT with my flight stick]]: For games that only know an Xbox controller. JoyPro makes a pretend one.
- [[page:getting-started/first-steps.html#doing-it-by-hand|Fly DCS World or Falcon BMS]]: Your buttons for every aircraft in one place, read from the game and written back.
- [[page:how-do-i/easy-words-and-help.html|Be shown around]]: Tours that point at the real buttons, and F1 help on any button.
- [[page:project/jp1-vs-jp2.html|Come from JoyPro 1]]: Where each old function sits now. Your old profile opens as it is.
:::

These are the answers of JoyPro's welcome. JoyPro asks them once, after the first start.

## New in 2.7

:::cards
- [[page:getting-started/quick-start.html|Set it all up for me]]: One card on the Start page sets up your devices; **Done, and set everything up for me** after the first start.
- [[page:how-do-i/head-tracking.html|Head tracking]]: JoyPro asks whether you use head tracking or VR, and puts looking around where it fits.
- [[page:how-do-i/several-inputs.html|Two inputs for one function]]: The same function on two buttons or two devices.
- [[page:how-do-i/where-is-what.html|Where is what?]]: See what each button of your devices does.
- [[page:how-do-i/dcs-kneeboard.html|DCS kneeboard]]: Your binds as pages on the kneeboard in the cockpit.
- [[page:how-do-i/easy-words-and-help.html|F1 and tours]]: Help on any button, tours that point at the real buttons, Easy words.
:::

## How JoyPro works

**The quick way.** JoyPro does it for you.

:::steps
1. **Set it all up for me**<br>on the Start page
2. **Use these buttons**<br>look at the list
3. **Write to my game**<br>JoyPro keeps a backup
4. **Fly**<br>your buttons are there
:::

**Your own way.** You decide every button.

:::steps
1. **Import**<br>the binds of your games
2. **Relations**<br>one function, every game
3. **Binds**<br>a button or axis each
4. **Export**<br>into every game
:::

- A **relation** is one function ("Gear Up/Down") with its action in every game and aircraft.
- A **bind** is the button or axis the relation is on. A relation can have several.
- **Export** writes it into each game's own control files. It makes a backup first, and you can undo it.
- Change something in a game later? JoyPro asks after the game closes whether to take it over.

## Start here

:::cards
- [[page:getting-started/install.html|Download and install]]: Unzip, start, done. What it needs and where it keeps things.
- [[page:getting-started/quick-start.html|Quick start]]: Working buttons in a few clicks.
- [[page:getting-started/supported-devices.html|Supported devices]]: Every stick works. The list of devices JoyPro knows by name.
- [[page:faq.html|Help and FAQ]]: Common questions, and the support file when something goes wrong.
- [[page:how-do-i/index.html|How do I ...]]: Short answers, one page each.
- [[page:getting-started/first-steps.html|Your first profile in 10 minutes]]: The quick way and the way by hand.
:::

## The main flows

:::cards
- [[page:flows/import.html|Import a game]]: Take the binds you made in a game into a profile.
- [[page:flows/relations.html|Build relations]]: Search, tick, add; suggestions and Auto Group.
- [[page:flows/export.html|Export]]: The four modes and what each changes in the game.
- [[page:flows/check.html|The Check]]: What would go wrong in the game, with one-click fixes.
:::

Every page has three levels: **for beginners** (no PC knowledge needed), **advanced** (every option) and **technical
internals** (the engine commands and the code). The switch at the top of a page picks one.

## Everything else

:::cards
- [[page:gui/start.html|Every GUI page]]: {{stat:gui.controls}} controls, each with what it does and what runs behind it.
- [[page:games/index.html|Games]]: {{stat:games.handled}} titles handled, {{stat:games.titles}} researched. A page per game.
- [[page:cli/index.html|Engine command line]]: {{stat:cli.commands}} commands, every option and value.
- [[page:project/jp1-vs-jp2.html|From JoyPro 1 to 2]]: What improved, with the numbers.
:::

:::stats
- **{{stat:games.handled}}** game titles handled
- **{{stat:languages}}** interface languages
- **{{stat:cli.commands}}** engine commands
- **{{stat:jp2.tests}}** automated tests
:::

## Community and licence

- Ideas, questions, problems: the [JoyPro Discord](https://discord.gg/yK4gQA9xgk) or an issue on
  [GitHub](https://github.com/Holdi601/JoystickProfiler2/issues).
- JoyPro is free software under the GNU General Public License, version 3 or later. It comes with no warranty.
  JoyPro backs up every game file before it writes one; keep your own backups anyway.
- This site is generated from JoyPro's source code and documentation, so it describes the current version.
  [[page:project/wiki-status.html|Wiki status]] shows how complete it is.
