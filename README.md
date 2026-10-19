# JoystickProfiler2 (JoyPro 2)

Successor of [JoystickProfiler](https://github.com/Holdi601/JoystickProfiler): the same relation/bind model as JoyPro 1, with a new UI.

**Ideas or problems? Join the Discord:** https://discord.gg/yK4gQA9xgk

- `engine/`: the JoyPro engine as a command-line tool (.NET Framework 4.8). It holds all profile, import/export and database logic. Command reference: `engine/docs/ENGINE_CLI_WIKI.md`.
- `gui/`: Avalonia desktop app (.NET 9). It runs the engine for every action. Docs are in `gui/docs/`.
- `EngineOnly_Runtime/`: JoyPro's own data that the build copies next to `engine.exe` (function groups, device catalog, conversion tables). Game command lists are built on your PC from your own game installation.
- `docs/`: feature docs: [Relation editor](docs/relation-builder.md) (start small, then grow), [Auto Group](docs/auto-group.md), [Group by description](docs/natural-language-grouping.md), [Auto Assign](docs/auto-assign.md), [Suggest additions](docs/suggest-additions.md), [Export](docs/export.md), the [HUD](docs/hud.md), [installed games](docs/installs.md), the [device catalog](docs/devices.md), [game config backups](docs/backups.md), [autosave and profile history](docs/profile-history.md) (undo across sessions, versions, the last profile at start), the [virtual joystick](docs/virtual-joystick.md), [optional components](docs/components.md) (one-click install of vJoy, HidHide, ViGEmBus) and the per-game notes in `docs/games/`.
- **Supported games:** [docs/games/support.md](docs/games/support.md) lists every game (each version separately, and the
  titles JoyPro only documents) with what JoyPro does for it, the device limits, anti-cheat notes and how it was tested in game.
- License: [GPL-3.0-or-later](LICENSE), the author's [notice](NOTICE.md) and the [third-party notices](THIRD_PARTY_NOTICES.md).
- Privacy: JoyPro has no account and no telemetry. It goes online only for update checks (GitHub), the makers'
  product photos of your devices (only after you said yes per device), game covers missing on this PC (switchable),
  the optional AI model download, the optional drivers you install from
  Settings > Components (GitHub) and a missing .NET Framework installer; these sites see your IP address. Details: [docs/privacy.md](docs/privacy.md).

## Build and run

```powershell
dotnet build gui/JoyPro.Gui/JoyPro.Gui.csproj      # also builds the engine and bundles it
dotnet run --project gui/JoyPro.Gui/JoyPro.Gui.csproj
```

The engine needs the .NET Framework 4.8 build tools (Visual Studio 2022 or Build Tools).

To test JoyPro2 like an installed copy, build it into a folder outside the repository and start it from there:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build_local.ps1             # builds into C:\JoyPro2-Local and starts JoyPro.Gui.exe
powershell -ExecutionPolicy Bypass -File tools\build_local.ps1 -NoStart    # build only; -OutDir <folder> for another place
```

Development builds in the repository only stop a JoyPro.Gui started from their own output folder, so the test copy keeps
running. JoyPro reads and writes your real game configs when you import or export (every export makes a backup first,
restorable on the Export page).

## Tests

```powershell
dotnet test gui/JoyPro.Gui.Tests/JoyPro.Gui.Tests.csproj
powershell -ExecutionPolicy Bypass -File engine/tests/engine_smoke_test.ps1
```

The engine smoke test runs the real engine against temporary fixture folders and never touches game installs or Saved Games.
Many game checks read real game command lists that only the maintainer's development copy has (lists extracted from
installed games are not part of this repository); without them the smoke test skips those parts, names each one, and
runs everything else.

## About this project

JoyPro is a hobby project. I started it for my own sticks and share it because it may help other pilots too.
JoyPro 2 was developed further with the help of Claude (Anthropic).

**No warranty, no liability.** JoyPro is free software under the GNU General Public License, version 3 or later. It
comes with no warranty, and I accept no liability for any damage, lost bindings or broken game configurations, to
the extent the law allows (see sections 15 and 16 of the [GPL](LICENSE)). JoyPro backs up every game file before it
writes one; keep your own backups anyway.

**Support.** I am happy to help when I can: open an issue on GitHub. But this is a spare-time project, and I cannot
promise support, fixes or updates, or say when they will come.

**Job offers.** I will soon be looking for a new job. If JoyPro is useful to you, I am grateful for a job offer or a
contact, especially in the Amsterdam area:

- Contact: gotthold1337@googlemail.com
- LinkedIn: https://www.linkedin.com/in/reinhold-radick-6661a2b5/

The full notice is in [NOTICE.md](NOTICE.md).
