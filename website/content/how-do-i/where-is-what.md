---
title: "Where is what? See what each button does"
status: full
order: 4
summary: The layout window shows every device with the function on each control. Press a button and it lights up. Keep it on a second monitor while you fly.
---
**Where is what?** opens a window with all your devices. Next to each button it says what the button does in your
game. Press a button on your stick, and its row lights up. Its function shows in big letters at the top.

:::layer beginner
## Open it

You find **Where is what?** in four places:

- on the Start page,
- at the top right of the Layout page,
- in the setup's last step (before anything changes, it shows the plan),
- in the tray menu (the small JoyPro icon next to the clock).

![The Where is what? window](shot:where-is-what)

## Use it

1. Pick a **Game**. Only games with binds on your devices are listed.
2. Pick an **Aircraft** (DCS). Games with one setup for the whole game have no aircraft list.
3. Pick a **Device**, or keep every device.
4. Press a button, move an axis or a hat. Its row lights up.

**Follow the game** switches to the game you play. In DCS it also switches to the aircraft you fly.

:::tip A second monitor
Put the window on a second monitor. Next time it opens there again, with the same size, game and aircraft.
:::

Changed a button? Press **Look again**. The window reads your binds again.
:::

:::layer advanced
## Details

- **Light up what I press** is on by default. JoyPro reads your devices only while the window is open and not
  minimized.
- One device alone gets the whole window. That is easier to read from a distance.
- While a setup plan waits to be applied, the window shows that plan. Otherwise it shows your profile.
- Left open when JoyPro closes, the window comes back at the next start.
- The [[gui:layout|Layout page]] shows one device at a time, with pictures and markers you can edit.
:::

:::layer technical
## Behind the window

Window `Views/LayoutWindow.axaml`, command `OpenLayoutWindowCommand` (Layout page), `OpenLayoutPreviewCommand` (Start
page) and `ShowLayoutPreviewCommand` (setup review). **Follow the game** uses JoyPro's DCS hook for the aircraft;
other games are found by their running program every 10 seconds. After a moment without input the devices are read
only every 250 ms.
:::

## Related

- [[page:how-do-i/dcs-kneeboard.html|See my binds on the DCS kneeboard]]
- [[gui:layout|The Layout page]]
