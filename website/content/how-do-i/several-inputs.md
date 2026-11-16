---
title: Several buttons for one function, several actions at once
status: full
order: 5
summary: Put one function on two buttons or two sticks, each with its own curve. Select several actions of a relation and copy, move or remove them together.
---
A **relation** is one function, for example "Gear Up/Down", in all your games. A relation can be on more than one
button or axis. And you can work on several of its actions at once.

:::layer beginner
## One function on two buttons

You can give a relation a second input, for example the same function on two sticks.

1. Open **Binds** in the bar at the bottom.
2. Click the relation in the list.
3. Find the part **More inputs**. Press **Record another input**.
4. Press the second button on your stick (or move the axis).

Now both inputs do the same job. The card in the list shows **+1**.

- **Make main** makes that input the main one.
- **Remove** takes an input off again.

:::note Some games keep only one
Some games keep only one input per function. They get the main input. Export tells you which one did not fit.
:::

## Several actions at once

In a relation, each game action is a card. You can pick several cards at once.

1. Open **Relations** and open a relation.
2. Hold Ctrl and click the action cards you want. Shift+click works too. Ctrl+A picks all.
3. A bar shows "n actions selected". Pick what to do:

| Button | What it does |
|---|---|
| **Copy** (Ctrl+C) | Remembers the actions. Open another relation of the same kind and press **Paste** (Ctrl+V). |
| **Move to...** | Moves them to a **New relation** or an **Existing relation**. |
| **Remove** (Del) | Takes them out of the relation, after one question. |

Each of these is one step. One Ctrl+Z takes it back.

## Move the actions of several relations

1. In the list of relations, Ctrl+click or Shift+click several relations.
2. Press **Move actions to...**.
3. Pick a new relation or an existing one.

All their actions go to that one relation. The empty relations are deleted.
:::

:::layer advanced
## More inputs in detail

- **Every input has its own device, modifiers and axis curve.** For an axis on two devices, **Axis response** has an
  **Input** list at the top. Pick input 1 (the main one) or input 2 and set its own curve.
- **Add the input picked by hand** makes the device and input picked by hand one more input.
- **Copy every bind of a device to another device**: pick the **First device** and the **Second device**, **Preview**,
  then **Copy**. Every bind on the first device also gets the matching input of the second one. Good for the same
  grip on two bases.
- **Every page finds a relation by any of its inputs**: **Follow input**, the Layout page, the **Devices** filter and
  the search with **Everything**.
- **Export**: where the game keeps several inputs per action, every input is written, the main one first. Where it
  keeps one, the main input is written and the export names the rest. Elite Dangerous keeps two buttons, MSFS two per
  device, most games one axis.

## Several actions in detail

- A click with Ctrl or Shift only selects. It does not open the action's aircraft.
- The right-click menu of an action has **Copy** and **Move to another relation...** too.
- **Copy** and **Move to...** keep each action's aircraft, also the switched-off ones.
- Only relations of the same kind are offered: a button relation takes button actions, an axis relation axis actions.
- **Move to...** with **New relation**: type a name. The new relation has the old one's kind, no bind and no groups.
:::

:::layer technical
## Behind it

Extra inputs: `Relation.ExtraBinds` (input 1 = the main bind). Engine: [[cli:binds extra add]], [[cli:binds extra list]],
[[cli:binds extra move]]. Several actions: [[cli:relations item copy-to]], [[cli:relations item move-to]],
[[cli:relations mass move-actions]]. Each is one engine call, so one profile version. Reference:
[[doc:multi-bind|Several inputs per relation]], [[doc:relation-multi-select|Several actions at once]].
:::

## Related

- [[page:flows/relations.html|Build relations]]
- [[gui:binds|The Binds page]]
- [[gui:relations|The Relations page]]
