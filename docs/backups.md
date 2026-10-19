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
  off). A file JoyPro writes matches the EFS encryption of its folder: in an encrypted game folder (a Microsoft Store app
  installed on another drive keeps its data EFS-encrypted, and Windows refuses a plain file there with "The APPX file can
  not be accessed because it is not encrypted as expected", ERROR_APPX_FILE_NOT_ENCRYPTED 409, an EFS refusal) new files take the
  folder's mark and JoyPro never removes it; the temp next to a game file loses the mark only in a plain folder. Only
  JoyPro's own backup and temp files in its own folders lose the mark, and only where Windows cannot encrypt. The
  `<file>.jpbak` next to a game file is a `File.Copy` (keeps the EFS encryption), the bytes only when Windows cannot
  EFS-encrypt there. A game file that really cannot be written gets a message saying what to do (6000 and 409 each have
  their own). Tests: `SafeFileIoTests` (the native calls fail with HRESULT 0x80071770 or 0x80070199, EFS state and
  File.Encrypt simulated; `JOYPRO_TEST_REAL_EFS=1` runs one test with real EFS, which makes an EFS certificate for the
  Windows user if there is none; `JOYPRO_TEST_FAULT_ENCRYPTION=copy,replace,move,stream` (or `replace:409`, ...) does
  the same for an engine run).
- The newest 30 sets per game are kept; older ones are deleted when a new set is made.
- The single `<file>.jpbak` next to a changed file (copy of the state before the last export) stays as before.
