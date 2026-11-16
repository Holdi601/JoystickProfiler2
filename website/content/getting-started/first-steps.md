---
title: Your first profile in 10 minutes
status: full
order: 4
summary: Two ways to working buttons. The quick way lets JoyPro set up your devices. The way by hand imports your games, builds relations, binds buttons and exports.
---
There are two ways to working buttons. The quick way takes a few clicks: JoyPro does the work. The way by hand takes
longer: you decide every button. You can start quick and change things by hand later.

:::video first-steps "From install to the first flight"
:::

:::layer beginner
## The quick way

:::steps
1. **Set it all up for me**<br>on the Start page
2. **Use these buttons**<br>look at the list
3. **Write to my game**<br>JoyPro keeps a backup
4. **Fly**<br>your buttons are there
:::

1. On the Start page, press **Set it all up for me**. At the first start, **Done, and set everything up for me**
   does the same.
2. Look at the list of what goes where. Press **Use these buttons**.
3. Close your game. Press **Write to my game**.
4. Start your game and fly.

For ACE COMBAT 8 and other Xbox-only games, the end is different: **Set up the pretend controller**. All of it, step
by step: [[page:getting-started/quick-start.html|Quick start: make my stick work]].

Something wrong? See [[page:how-do-i/undo-quick-setup.html|Undo the quick setup]].

## Doing it by hand

Do you already have binds in your games? Or do you want to decide every button? Then go this way.

:::steps
1. **Import**<br>the binds of your games
2. **Relations**<br>one function, every game
3. **Binds**<br>a button or axis each
4. **Export**<br>into every game
:::

1. **Import.** On the Start page, press **Import** on your game's card, then **Import now**. JoyPro reads your binds
   and makes a profile. The game does not change. ([[page:flows/import.html|Import a game]])
2. **Relations.** Open **Relations**. A relation is one function ("Gear Up/Down") with its action in every game. To
   cover a second game, open a relation and press **Add** on a suggestion. ([[page:flows/relations.html|Build relations]])
3. **Binds.** Open **Binds**. Pick a relation, press **Record input**, then the button on your stick. For an axis,
   move it all the way. ([[gui:binds|Binds page]])
4. **Check.** Press **Check** in the bottom bar. Fix the red cards. ([[page:flows/check.html|The Check]])
5. **Export.** Close the game. Open **Export**, keep **Replace on my devices** and **Also clear my other devices**
   (this removes the old binds on your sticks in the game; JoyPro backs them up first). Tick the games, press **Export ticked games**. ([[page:flows/export.html|Export]])

Then start the game: your binds are in its controls menu.

:::tip Change something in the game?
After you close the game, JoyPro asks whether to take your change into the profile.
:::

**Undo last export** on the game's card puts the game's files back exactly as they were.

## Already have binds? Mix both ways

The Start page lists games where you made binds yourself. **Import, then fill the gaps** takes your binds in first.
Then the setup only uses the free buttons. Your own binds stay.
:::

:::layer advanced
## Faster ways by hand

- **Set up step by step...** (Start page) opens the [[page:gui/setup-wizard.html|setup wizard]] with all its steps:
  your games, your devices and their roles, your aircraft priorities, the plan, the review.
- **Fill the gaps...** (Start page) does the same for games already in your profile. Your binds stay where they are.
- **Auto-build relations from all my actions...** (Relations page) groups the same function of every aircraft and
  game (Auto Group). You see a preview first.
- **Auto Assign** puts unbound relations on the best controls of a device. **Lay out with AI** takes wishes in plain
  words ("trim on the hat") and shows every change before it applies.
- **Follow input** (Relations and Binds pages): press a button and its relation opens.
:::

:::layer technical
## Behind it

The quick way runs the setup wizard's engine steps by itself ([[cli:setup plan]], [[cli:setup apply]]) and then the
game's export. The way by hand uses the import ([[cli:dcs import]] and the other games' import), the relation commands
([[cli:relations add]]), the binds ([[cli:binds set]]), the Check ([[cli:profile validate]]) and the export
([[cli:dcs export]] and the others). Every change is one profile version, so Undo works across restarts.
:::

## Related

- [[page:getting-started/quick-start.html|Quick start: make my stick work]]
- [[page:how-do-i/index.html|How do I ...]]
- [[page:faq.html|Help and FAQ]]
