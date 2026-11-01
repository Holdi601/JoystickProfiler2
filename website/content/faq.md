---
title: Help and troubleshooting
status: full
order: 1
summary: The support file, the game watcher, and answers to common questions.
---
:::video troubleshooting "When something does not work"
:::

## Something went wrong: the support file

The fastest way to get help is the **support file**. It holds everything needed to understand a problem, so nobody
has to ask you for a second file.

:::layer beginner
1. Open **Settings > Report a problem**.
2. Describe in **What happened?** what you did and what you expected.
3. Leave **Include my open profile and its history** ticked (it helps a lot; untick it if you do not want to share
   your binds and device names).
4. Press **Create Report**. JoyPro lists what goes into the file; confirm. The zip is saved to
   `Documents\JoyPro2\Problem Reports` and shown in Explorer.
5. Press **Open GitHub Issue Page** and drag the zip into the issue, or post it on the
   [Discord](https://discord.gg/yK4gQA9xgk).

Nothing is uploaded by JoyPro itself. Your Windows user name is replaced in every text of the file.
:::

:::layer advanced
What the file holds: `summary.txt` (versions, system, folders, the games' environment, connected devices, and what
was left out), JoyPro's logs of at least the last 14 days (at most 100 MB), crash reports and the last errors,
JoyPro's settings and state files, the devices with vJoy and HidHide state, per game the watcher record, the list of
backups and the newest backup set, and with your consent the open profile, its history, the games' current bind
files and JoyPro 1's profiles. Engine: [[cli:support bundle]] (`--list` shows the contents without writing).
:::

## The game watcher asks "Take the changes into your profile?"

When you change controls inside a game JoyPro manages, JoyPro asks after the game has closed whether to take the
changes into your profile. Where several relations use that button, it asks which relation the change belongs to, or
whether to make a new one.

- JoyPro's own exports never trigger the question: each export records the game's files.
- Switch it off for one game with **Watch for changes** on its Start page card, or for all games in Settings > Games
  > **Watch game configs**.
- To keep watching while JoyPro is closed: Settings > Run in the background > **Start with Windows (in the tray)**.
- Engine: [[cli:sync status]], [[cli:sync diff]], [[cli:sync apply]], [[cli:sync accept]]. Details:
  [[doc:sync-watcher|game config watcher]].

## Common questions

**The game does not show my binds after an export.**
Was the game closed during the export? JoyPro refuses to write while it runs; the tile shows the reason. Some games
need a step of their own: Falcon BMS builds its key file when you press Launch in its launcher; in MSFS a device may need JoyPro's
preset or import file (see its game page). Check the tile's result and the Output panel. The game's page under [Games](games/index.html)
lists its specifics.

**The game's default binds are still on my device (DCS).**
Use **Replace on my devices** with **Also clear my other devices**: the written devices lose DCS's defaults in the
exported aircraft. Settings > **DCS: default binds** can switch the defaults off in the DCS install itself.

**I want my old controls back.**
Export page, the game's tile: **Undo last export**. It restores the files from before the export, byte for byte.
Older backups: **Game config backups** at the bottom of the Export page.

**An export says "Not written to the game".**
You exported with **Only add**, and the game already uses that button for another function. The Check has a card
with **Export with 'Update' to replace it**, **Move to another button** and **Keep as is**.

**My stick got a new ID / I replaced a device.**
Imports treat devices with the same name as one device. To move binds to a new device, use **Replace device...**
(Settings, the device cards of the Layout page, or the Binds page's menu for the selected bind's device).

**A game is missing.**
Settings > Games: **Detect again**, or **Choose folder...** to link a portable install. Games JoyPro does not handle
yet are listed under [Game support](games/support.html).

**Can I undo a change in JoyPro?**
Yes: **Undo** / **Redo** in the top bar (Ctrl+Z / Ctrl+Y), also after a restart. **History** lists every version of
the profile.

**Is JoyPro safe with anti-cheat?**
JoyPro reads no game memory and injects nothing; it writes config files while the game is closed. The optional
virtual devices (vJoy, ViGEmBus, HidHide) are signed drivers; a game may still refuse virtual devices. See each game's
notes under [Game support](games/support.html).
