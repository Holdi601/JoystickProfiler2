---
title: Easy words, tours and F1 help
status: full
order: 8
summary: Big text and short sentences for everyone, tours that point at the real buttons, F1 that explains any button, and how to get the welcome back.
---
JoyPro explains itself. **F1** explains any button you click. **Tours** show you around, one step at a time. **Easy
words** makes all of it big and short.

:::layer beginner
## F1: what does this button do?

1. Press **F1**. The mouse pointer turns into a question mark.
2. Click any button. It does not run. A small window says what it does.
3. Press **F1** or Esc to stop.

![F1 help: a small window explains the button](shot:f1-help)

**More details** shows the longer text. **More in the wiki** opens the button's page on this website.

## Tours

A tour makes the real button glow. It waits for your click. Then it goes on.

- **Next** is the big blue button. **Back** goes one step back.
- **Skip** leaves a step out. **End tour** stops.
- Every page has **Tour of this page**, at the top right next to **Help**.

![A tour card next to the glowing button](shot:tour)

## Easy words

Easy words means big cards and very short sentences. It is good for children. It is good for anyone who wants it
simple.

Switch it on in one of these places:

- in the welcome: **Easy words: big text, short sentences**,
- in the first window, the same box,
- on every tour card: **Easier words**,
- in Settings > **Tours**.

It stays on after a restart.

![F1 help with Easy words](shot:f1-help-easy)

## Get the welcome or a tour back

- **Help** at the top right > **Tours...**.
- Settings > **Tours** > **Show the welcome**.
- Double-click **JoyPro 2 - Tour** in JoyPro's folder.

![The welcome: what do you want to do?](shot:welcome)
:::

:::layer advanced
## Details

- **The welcome's answers**: **Play ACE COMBAT with my flight stick**, **DCS World or Falcon BMS**, **Just make my
  stick work**, **Show me around**, **I used JoyPro 1** and **No tour**. JoyPro marks one as **suggested**, from the
  games it found.
- **How long?** **Short** shows the steps you need. **Long, with every extra** shows the extras too.
- Settings > **Tours** lists every tour with **Start tour**. **Ask again at the next start** brings the welcome back
  and marks every tour unseen.
- Close JoyPro in the middle of a tour: it goes on at the same step at the next start.
- With the keyboard: Tab to a control, then Enter or Space shows its help.
- Details: [[page:gui/help.html|Help mode and tours]].
:::

:::layer technical
## Behind the help

The F1 texts are the first sentences of this wiki's entries (`build.py --help-catalog` writes
`gui/JoyPro.Gui/Resources/help-controls.json`). Tours: `Services/Help/TourCatalog.cs`. `JoyPro 2 - Tour.cmd` starts
`JoyPro.Gui.exe --welcome`. The choices are kept in `ui-settings.json` (`"tours"`).
:::

## Related

- [[page:gui/help.html|Help mode and tours]]
- [[page:faq.html|Help and FAQ]]
