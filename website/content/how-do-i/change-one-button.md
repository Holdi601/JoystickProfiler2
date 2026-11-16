---
title: Change one button later
status: full
order: 6
summary: Give one game control another button, then send it to the game again. Four steps: three on the Binds page, then one export.
---
JoyPro set up your stick, or you did. Now you want another button for one thing. That takes the four steps below:
three on the Binds page, then one export.

:::steps
1. **Binds**<br>in the bar at the bottom
2. **Pick a game control**<br>in the list
3. **Record input**<br>press your button
4. **Export**<br>send it to the game
:::

:::layer beginner
## Step by step

1. Open **Binds** in the bar at the bottom. Everything you set up is there.
2. Click a game control in the list, for example "Gear Up/Down". The search box above finds it fast.
3. Click **Record input**. Then press the new button on your stick. For an axis, move it all the way.
4. Send it to the game again. Close the game first. Open **Export** in the bar at the bottom. Press **Export ticked
   games**.

![The Binds page](shot:binds)

**Record input** shows only after you picked a game control. Pick one first.

## In an Xbox-only game

ACE COMBAT 8 and Hell Let Loose: Vietnam get no export. Press **Stop**, then **Start** on the Virtual Joystick page.
The pretend controller (virtual Xbox pad) then uses your new button.

## Undo

Pressed the wrong button? Press Ctrl+Z. Your old button is back.
:::

:::layer advanced
## More ways

- **Press the button you want to change.** **Follow input** on the Binds page opens the relation of the button you
  press.
- **On the picture.** The [[gui:layout|Layout page]] in **Bind** mode: click an input, pick a relation, **Bind to this
  input**.
- **One game only.** The Start page's game card has **Export** for that game.
- **A second button for the same thing.** See [[page:how-do-i/several-inputs.html|Several buttons for one function]].
- **The same button in two relations** is allowed. The [[page:flows/check.html|Check]] tells you where it means two
  jobs in one aircraft.
:::

:::layer technical
## Behind it

**Record input** waits for the next input of any device and sets the bind ([[cli:binds set]]); the export is the
game's export with the Export page's mode ([[page:flows/export.html|Export]]). Each change is one profile version, so
Ctrl+Z works also after a restart.
:::

## Related

- [[gui:binds|The Binds page]]
- [[page:flows/export.html|Export to your games]]
