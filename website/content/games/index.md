---
title: Games
status: full
category: index
summary: Every game JoyPro handles, with a page per game: how to get it working in 3 steps, what JoyPro does there, which files it reads and writes, what you see in the game's controls menu and what you do yourself.
---
JoyPro handles {{stat:games.handled}} game titles and has notes on {{stat:games.documented}} more. Each page below
explains one game (or one family of games that share their files). It starts with **Get it working in 3 steps**. The
**Show:** switch at the top of a game page picks the level: **Beginner** (the steps, import, relations, export and
what you then see in the game), **Advanced** (the files, what each export mode does in that game, what is never
touched, curves, force feedback, the watcher) and **Technical** (file formats, how devices are identified, the engine
commands).

:::video games-overview "JoyPro and your games: one profile, every game"
:::

:::layer beginner
## The quick way for most games

:::steps
1. **Set it all up for me**<br>on the Start page
2. **Use these buttons**<br>look at the list first
3. **Write to my game**<br>keep the game closed
:::

ACE COMBAT 8 and Hell Let Loose: Vietnam only know an Xbox controller. For them, JoyPro makes a pretend controller
instead. See [[page:how-do-i/xbox-only-games.html|Play an Xbox-only game with a HOTAS]].
:::

## How your binds reach a game

```mermaid
%% caption: Most games: Export writes your binds into the game's files. One-device and Xbox-only games: JoyPro sends your inputs live while Start is on. Xbox-only games get nothing written.
flowchart LR
  I["Most games:<br/>quick setup or Import"] --> P["Your profile"]
  P --> E["Export<br/>(game closed,<br/>backup first)"]
  E --> M["The game's<br/>controls menu"]
  Q["One-device and<br/>Xbox-only games:<br/>your profile"] --> V["Virtual Joystick"]
  V --> S["Start while<br/>you play"]
```

Every game follows the same rules: JoyPro writes only the actions of the game's command list, refuses to write while
the game runs, backs the files up first (**Undo last export** on the Export page puts them back byte for byte) and
skips a device or action it cannot match instead of guessing. Devices that are not connected, your mouse, the
import's ignored devices and the vJoy device JoyPro tunnels into are never cleared.

{{game-index}}

## More

- [[page:getting-started/quick-start.html|Quick start: make my stick work]]: the quick way, step by step.
- [[page:games/support.html|Game support]]: the full matrix of every title, generated from the engine's code (import,
  export, keyboard, modifiers, curves, watcher, force feedback, vJoy, in-game test).
- [[page:flows/export.html|Export flow]]: the four export modes in general, and what changes in each kind of game.
- [[page:flows/import.html|Import flow]]: the import wizard and its options.
- [[gui:vjoy|Virtual Joystick]]: games that read too few joysticks or only an Xbox controller.
