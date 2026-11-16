---
title: Undo what the quick setup did
status: full
order: 7
summary: Take back the quick setup in JoyPro with one Undo, and put the game's old buttons back with Undo last export.
---
The quick setup does two things. **Use these buttons** puts the buttons into JoyPro. **Write to my game** puts them
into your game. You can take back each one.

| What you want back | What you press |
|---|---|
| The game's old buttons | **Undo last export** on the game's card (Export page), or **Undo export** on its Start page card |
| JoyPro as it was before | **Undo** in the top bar, or Ctrl+Z |

:::layer beginner
## Put the game's old buttons back

1. Close the game.
2. Open **Export** in the bar at the bottom.
3. Find your game's card. Press **Undo last export**.

The game's files are back exactly as they were. The Start page's game card has the same: **Undo export**.

## Take it back in JoyPro

Press **Undo** in the top bar, or Ctrl+Z. **Use these buttons** was one step, so one Undo takes it all back. This also
works after a restart.

:::tip Do both
Undo in JoyPro does not change your game. **Undo last export** does not change JoyPro. To go back completely, do both.
:::

## In an Xbox-only game

ACE COMBAT 8 and Hell Let Loose: Vietnam got nothing written. Press **Stop** on the Virtual Joystick page, and the
pretend controller (virtual Xbox pad) is gone.
:::

:::layer advanced
## Details

- **Undo last export** restores the newest backup of that game. Older ones are under **Game config backups** at the
  bottom of the Export page.
- **History** in the top bar lists every version of your profile. The quick setup's version is named "Setup wizard: N
  relations, M binds".
- **Keep the old ones too** removed nothing from the game, so there is less to undo.
:::

:::layer technical
## Behind it

**Undo last export** runs [[cli:backups restore]] `latest --game <id>`. Backups live in
`%LOCALAPPDATA%\JoyPro2\GameBackups` (the newest 30 per game). The profile history is in
`%LOCALAPPDATA%\JoyPro2\ProfileHistory`; Undo goes back one version.
:::

## Related

- [[page:getting-started/quick-start.html|Quick start: make my stick work]]
- [[page:flows/export.html|Export to your games]]
