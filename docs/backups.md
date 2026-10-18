# Game config backups

(The profile itself has its own history: every save is a version, see [profile history](profile-history.md).)

Before JoyPro changes a game file, it copies the file into a backup set. This covers every export (DCS, IL-2,
Star Citizen and all registry games), the DCS stick-defaults commands and a restore itself. If a game's controls
break after an export, restore the set from before that export.

- GUI: Export → **Game config backups** (Refresh, Show Files, Preview Restore, Restore).
- CLI:
  - `engine backups list [--game <id>]`
  - `engine backups show <id>`
  - `engine backups restore <id|latest> [--game <id>] [--dry-run]`

## How it works

- One set per game and command run, in `%LOCALAPPDATA%\JoyPro\GameBackups\<date-time>_<game>\`. The set holds the
  copies plus `manifest.json` with the original paths. `JOYPRO_DATA_DIR` moves the folder (tests).
- Files the export created (for example a new DCS `.diff.lua`) are recorded as "did not exist"; a restore removes
  them again.
- Games that keep their controls in the registry (Nuclear Option: `HKCU\Software\Shockfront\NuclearOption`) get a
  snapshot of the values JoyPro may change (`registry\NNNN_<key>.json` in the set: Player0's Rewired controller maps).
  A restore writes those values back and removes controller-map values the export added; other values of the key
  stay. (`reg export` is no alternative: it stops at the game's 255+ character value names.)
- A restore first backs up the current files into a new set, so it can be undone (`Undo: engine backups restore <id>`).
- A backup of a game that is not installed (not found, no folder set in Settings > Games) cannot be restored
  ([installed games](installs.md)).
- A restore is refused while the game runs, because the game would write its own state back on exit.
- Dry runs write no backup.
- Backups, restores and the exports' temp-file replaces go through `engine/SafeFileIo.cs`: they copy the bytes instead
  of `File.Copy`/`File.Replace` carrying the file's attributes over. A game file with the EFS "encrypted" mark
  (a Store package folder, a folder from another PC, an encrypted AppData) made `File.Copy` fail with "The specified
  file could not be encrypted." (ERROR_ENCRYPTION_FAILED) where Windows cannot encrypt (FAT/exFAT, network share, EFS
  off). JoyPro's own temp and backup files lose the mark; a game file that really cannot be written gets a message
  saying what to do. Tests: `SafeFileIoTests` (the native calls fail with HRESULT 0x80071770;
  `JOYPRO_TEST_FAULT_ENCRYPTION=copy,replace,move,stream` does the same for an engine run).
- The newest 30 sets per game are kept; older ones are deleted when a new set is made.
- The single `<file>.jpbak` next to a changed file (copy of the state before the last export) stays as before.
