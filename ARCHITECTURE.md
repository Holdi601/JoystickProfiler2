# JoystickProfiler2 — Architecture Map

> A map of the project, checked against the source (2026-10-06). Keep it up to date
> when the structure changes, so a new contributor can get up to speed in one read.
>
> Note: `JoystickProfiler` (the first one) is the **legacy** project; it is no longer
> developed. All development happens in `JoystickProfiler2`.

## Solution — `JoyPro.sln` (6 projects)

| Project | Path | Role |
|---|---|---|
| `engine` | `engine\engine.csproj` | .NET 4.8 console CLI (`JoyPro.Engine`) |
| `JoyPro.Gui` | `GUI\JoyPro.Gui\` | Avalonia MVVM GUI |
| `JoyPro.Gui.Tests` | `GUI\JoyPro.Gui.Tests\` | GUI tests |
| `JoyPro.Ffb.Core` | `GUI\JoyPro.Ffb.Core\` | FFB runtime subsystem (`Graph`, `Output`, `Runtime`, `Telemetry`, `Host`) |
| `JoyPro.Ffb` | `GUI\JoyPro.Ffb\` | Telemetry force feedback process `JoyPro.Ffb.exe` (DirectInput output, dry sink, simulator; docs/ffb-telemetry.md) and telemetry recording (`record`, docs/telemetry-recording.md) |
| `JoyPro.Ffb.Core.Tests` | `GUI\JoyPro.Ffb.Core.Tests\` | FFB tests |
| `JoyPro.VJoy` | `GUI\JoyPro.VJoy\` | Virtual joystick forwarder `JoyPro.VJoy.exe` (DirectInput -> vJoy, bundled to `<GUI>\vjoy\`; docs/virtual-joystick.md) |

(Note: there is no root-level `Ffb.Core` — the FFB core lives under `GUI\`.)

## Engine CLI dispatch (`engine\Program.cs`)

- `Main → Run(args)`: pops global options (`--profile`, `--out`, `--editor`, `--text`, `--status`, `--progress`, `--benchmark`, `--load-corrections`, `--auto-add-db-items`, `--remap-missing-devices`, `--confirm-device-remap`, `--confirm-corrections`, `--correction-mode`, `--correction-auto-threshold`, `--correction-report`), validates option dependencies, then `switch (area)` on the first token:
  - `relations`, `binds`, `ffb`, `profile`, `db`, `devices`, `dcs`, `il2`, `starcitizen`/`sc`, `backups`, `media`, `overlay`, `hud`, `games`, `layout`, `sync` (game config watcher, `EngineSync.cs`), `vjoy` (virtual joystick mode, `EngineVJoy.cs` + `VJoy\`), `summary`
  - **default** → `Games.GameRegistry.TryResolve(area)` — registry games are dynamic command areas
- Each area routes to `RunXxx(command, outPath, editor, textOutput)` which switches on the action verb (`list`/`show`/`add`/`set`/`export`/`import`/`edit`/...).
- Data folders (`GameDataPaths.cs`, docs/installs.md "Game data"): JoyPro's own data (`DB\Functions`, `DB\Devices`, `DB\Llm`, `DB\DevicePhotos`, `DB\DeviceLayouts`, `TOOLS`) is read from the program folder (`MainStructure.PROGPATH`); every game list built on the user's PC (`DB\<Game>`, `CleanProfile\<Game>`, `KeyboardCleanProfile\<Game>`, `source-version.txt`) is read from `<data>\GameData` first (else the program folder) and written only there. `EngineState.Initialize` runs the copy-only migration from the program folder. Never build such paths from `PROGPATH` directly: use `GameDataPaths.DbFolder/DbFile/CleanProfileFolder` to read and `Writable*` to write (GUI: `Services\GameDataLocations.cs`).
- Cross-cutting: `GameBackups.Begin(...)` snapshots before mutating commands; `ShouldAutoSaveProfile` auto-saves after success (atomically, and every save is a version in `<data>\ProfileHistory`: `ProfileHistory.cs` shared with the GUI, `EngineProfileHistory.cs`, `--history-note`, a checkpoint before every export/import; docs/profile-history.md); `--benchmark`/`--status` drive `Measure`/`Status` output, `--progress`/`--status` the `[engine-progress]` lines and the cooperative cancel of `EngineProgress` (JOYPRO_CANCEL_FILE, exit code 3); crashes write reports to `%LOCALAPPDATA%\JoyPro2\CrashReports` (or `JOYPRO_DATA_DIR`).

## Core types (all in `engine\Core*.cs`)

- `MainStructure` (static) — `CoreMainStructure.cs`
- `Pr0file`, `MetaSave` — `CoreProfile.cs`
- `RelationItem`, `Relation`, `Bind` — `CoreRelation.cs`
- `InternalDataManagement` (static) + `LoadItemCorrectionMode`, `LoadCorrectionSuggestion`, `LoadCorrectionReportEntry`, `LoadCorrectionReport` — `CoreInternalDataManagement.cs`
- `LayoutFile` — `CoreLayoutFile.cs`
- `ModExists`, `PlaneState`, `ForceFeedbackS`, `DCSInput`, `OtherGameInput`, `Modifier` — `CoreTypes.cs`
- Per-feature handlers in `Engine*.cs`; game integrations in `Games\*Integration.cs`; per-game models in `AxisModels\` and `FfbModels\`.

## GUI → engine integration

- **Single choke point**: `MainWindowViewModel.RunEngineAsync(commandArgs, includeProfile, mutatesProfile, busyLabel, forceText, isCustomCommand)` (line ~7317 of `MainWindowViewModel.cs`). Every feature partial (`MainWindowViewModel.Backups.cs`, `.ExportGames.cs`, `.Hud.cs`, ...) funnels through it.
- It resolves the exe via `ResolveEnginePath()` (user `EnginePath` → file, or dir containing `engine.exe` / `engine\engine.exe` → auto-detect), prepends `--profile`, `--status`, `--benchmark`, correction flags (confirmations hard-coded `false` because the GUI can't answer console prompts), and `--text`, then serializes calls via a semaphore (`engineCallGate`) with `IsBusy`/`pendingEngineCalls`. Foreground calls run with `--status true` through `IEngineCliClient.RunStreamingAsync`: the stderr lines feed the live busy area (`BusyProgressViewModel`, `MainWindowViewModel.BusyProgress.cs`, `Views/Controls/BusyProgressPanel.axaml`) and its Cancel (`JOYPRO_CANCEL_FILE`); the result handed back has them stripped (`EngineProgressParser.StripProgress`).
- **Process layer**: `IEngineCliClient` / `EngineCliClient` (`Services\`) — `Process` with redirected stdio, **stdin closed immediately** so the engine never blocks on prompts, working dir = engine dir, OEM-codepage encoding (with Latin1 fallback). Returns `EngineCommandResult` (ExitCode, Duration, StandardOutput, StandardError, Arguments).
- GUI parses stdout text into the typed items in `Models\` (e.g. `RelationListItem`, `BindListItem`, `HudItems`, `GameBackupItem`).
- The `JoyPro.Ffb.Core` project is the FFB runtime (graph/output/runtime/telemetry/host); `JoyPro.Ffb.exe` runs it for one game while that game runs (started by the GUI, line protocol on stdin/stdout). Its settings and the game setup go through `engine ffb telemetry ...`, distinct from the game FFB settings of `engine ffb set/show`.

## Documentation

- Feature docs: `docs\` (auto-assign, auto-group, axis-curves, backups, devices, export, hud, installs, logging, profile-history (autosave, versions, undo/redo, the last profile at start), reimport, relation-builder (the Relations page editor: suggest, search, chat), suggest-additions, sync-watcher, virtual-joystick)
- Background logs and problem reports: `engine\JoyProLog.cs` (one file shared by the engine, the GUI and JoyPro.Ffb), `<data>\Logs`, see `docs\logging.md`
- Engine CLI reference: `engine\docs\ENGINE_CLI_WIKI.md`
