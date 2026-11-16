---
title: Help and troubleshooting
status: full
order: 1
summary: Quick answers for beginners, the support file, the game watcher, and answers to common questions.
---
:::video troubleshooting "When something does not work"
:::

Short answers first. Each one links to a page with the steps.

## Quick answers

**My stick does nothing in ACE COMBAT 8.**
ACE COMBAT 8 only knows an Xbox controller. JoyPro turns your stick into a pretend controller (a virtual Xbox pad).
Start the game first. At its title screen, Alt+Tab to JoyPro and press **Start**. See
[[page:how-do-i/xbox-only-games.html|Play an Xbox-only game with a HOTAS]].

**Start says "Too early".**
You pressed **Start** before the game was running. Start the game and wait for its title screen. Then hold Alt and
press Tab, click **Stop**, then **Start**. Alt+Tab back to the game.

**How do I install ViGEmBus? What is the box below the license?**
ViGEmBus is a free helper. The pretend controller needs it. On the Virtual Joystick page, tick the box below the
license first: that says yes to the download. Then press **Install ViGEmBus**. Windows asks for permission: click
**Yes**. You do this only once.

**The setup says "Some rare extras had no room". Is that bad?**
No. Your stick has fewer buttons than the game has controls. All the important ones are on your stick. **Change
something** in the setup window shows what did not fit.

**How do I change one button later?**
Open **Binds**, click a game control in the list, click **Record input** and press the new button. Then send it to
the game again: **Export**, **Export ticked games**. See [[page:how-do-i/change-one-button.html|Change one button
later]].

**How do I undo the quick setup?**
**Undo last export** on the game's card (Export page) puts the game's old buttons back. Ctrl+Z takes it back in
JoyPro. See [[page:how-do-i/undo-quick-setup.html|Undo the quick setup]].

**My device is not in JoyPro's list.**
It still works. JoyPro sets it up like a usual one of its kind: moving the stick steers, button 1 fires, the first hat
trims, the first lever is the throttle. Tell us about it on the [Discord](https://discord.gg/yK4gQA9xgk), with the
support file. See [[page:getting-started/supported-devices.html|Supported devices]].

**Where do I see my binds in the cockpit?**
In DCS, on the kneeboard. Layout page, **Cheat sheets**, **Put on the kneeboard**. See
[[page:how-do-i/dcs-kneeboard.html|See my binds on the DCS kneeboard]]. On a second monitor, use **Where is what?**
([[page:how-do-i/where-is-what.html|more]]).

**How do I get the tour or the welcome back?**
**Help** at the top right > **Tours...**, or Settings > **Tours** > **Show the welcome**. Or double-click **JoyPro 2 -
Tour** in JoyPro's folder.

**What are Easy words?**
Big text and very short sentences, in the tours and the help. Tick **Easy words: big text, short sentences** in the
welcome, or switch it in Settings > **Tours**. See [[page:how-do-i/easy-words-and-help.html|Easy words, tours and
F1]].

**What does a button do?**
Press F1, then click the button. It does not run; a small window explains it. Press F1 again to stop.

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
files and JoyPro 1's profiles.
:::

:::layer technical
The support file is made by [[cli:support bundle]] (`--list` shows the contents without writing).
:::

## The game watcher asks "Take the changes into your profile?"

When you change controls inside a game JoyPro manages, JoyPro asks after the game has closed whether to take the
changes into your profile. Where several relations use that button, it asks which relation the change belongs to, or
whether to make a new one.

- JoyPro's own exports never trigger the question: each export records the game's files.
- Switch it off for one game with **Watch for changes** on its Start page card, or for all games in Settings > Games
  > **Watch game configs**.
- To keep watching while JoyPro is closed: Settings > Run in the background > **Start with Windows (in the tray)**.

:::layer technical
Engine: [[cli:sync status]], [[cli:sync diff]], [[cli:sync apply]], [[cli:sync accept]]. Details:
[[doc:sync-watcher|game config watcher]].
:::

## Common questions

**Is JoyPro 2.7 faster?**
Yes, where you wait most. A profile opens sooner. The Check shows its first cards at once while you type in its
search. The Start page looks at your games in the background and never slows the window down. Clicks after an export
stay quick, and JoyPro gives memory back when you pause. War Thunder's controls load about five times faster, and
games in very long folder paths work.

**The game does not show my binds after an export.**
Was the game closed during the export? JoyPro refuses to write while it runs; the card shows the reason. Some games
need a step of their own: Falcon BMS builds its key file when you press Launch in its launcher; in MSFS a device may need JoyPro's
preset or import file (see its game page). Check the card's result and the Output panel. The game's page under
[[page:games/index.html|Games]] lists its specifics. ACE COMBAT 8 and Hell Let Loose: Vietnam get no export: they use
the pretend controller.

**The game's default binds are still on my device (DCS).**
Use **Replace on my devices** with **Also clear my other devices**: the written devices lose DCS's defaults in the
exported aircraft. Settings > **DCS: default binds** can switch the defaults off in the DCS install itself.

**I want my old controls back.**
Export page, the game's card: **Undo last export**. It restores the files from before the export, byte for byte.
Older backups: **Game config backups** at the bottom of the Export page.

**An export says "Not written to the game".**
You exported with **Only add**, and the game already uses that button for another function. The Check has a card
with **Export with 'Update' to replace it**, **Move to another button** and **Keep as is**.

**My stick got a new ID / I replaced a device.**
Imports treat devices with the same name as one device. To move binds to a new device, use **Replace device...**
(Settings, the device cards of the Layout page, or the Binds page's menu for the selected bind's device).

**A game is missing.**
Settings > Games: **Detect again**, or **Choose folder...** to link a portable install. Games JoyPro does not handle
yet are listed under [[page:games/support.html|Game support]].

**Can I undo a change in JoyPro?**
Yes: **Undo** / **Redo** in the top bar (Ctrl+Z / Ctrl+Y), also after a restart. **History** lists every version of
the profile.

**Is JoyPro safe with anti-cheat?**
JoyPro reads no game memory and injects nothing; it writes config files while the game is closed. The optional
virtual devices (vJoy, ViGEmBus, HidHide) are signed drivers; a game may still refuse virtual devices. See each game's
notes under [[page:games/support.html|Game support]].
