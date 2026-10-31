param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$OutputPath = "",
    [string]$EngineExePath = "",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function To-NonNullString {
    param(
        [object]$Value
    )

    if ($null -eq $Value) {
        return ""
    }

    return $Value.ToString()
}

function Resolve-EngineExe {
    param(
        [string]$EngineDir,
        [string]$Configuration,
        [string]$Platform,
        [string]$EngineExePath
    )

    if (-not [string]::IsNullOrWhiteSpace($EngineExePath)) {
        if (Test-Path $EngineExePath) {
            return (Resolve-Path $EngineExePath).Path
        }
        throw "Specified engine executable was not found: $EngineExePath"
    }

    $candidate = Join-Path $EngineDir ("bin\" + $Platform + "\" + $Configuration + "\engine.exe")
    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).Path
    }

    Write-Host "Building engine ($Configuration|$Platform) for wiki generation..."
    dotnet build (Join-Path $EngineDir "engine.csproj") -c $Configuration | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dotnet build failed while preparing wiki."

    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).Path
    }

    throw "engine.exe not found after build at: $candidate"
}

function Extract-HelpSections {
    param(
        [string[]]$HelpLines
    )

    $globalOptions = New-Object System.Collections.Generic.List[string]
    $commands = New-Object System.Collections.Generic.List[string]
    $mode = ""

    foreach ($lineRaw in $HelpLines) {
        $line = if ($null -eq $lineRaw) { "" } else { $lineRaw.ToString() }
        $trimmed = $line.Trim()

        if ($trimmed -eq "Global options:") {
            $mode = "global"
            continue
        }
        if ($trimmed -eq "Commands:") {
            $mode = "commands"
            continue
        }

        if ($mode -eq "global") {
            if ($line -match "^\s{2}--") {
                [void]$globalOptions.Add($line.TrimEnd())
            }
            continue
        }

        if ($mode -eq "commands") {
            if ($line -match "^\s{2}engine ") {
                [void]$commands.Add($line.TrimEnd())
            }
        }
    }

    return [PSCustomObject]@{
        GlobalOptions = $globalOptions
        Commands = $commands
    }
}

function Get-CommandKey {
    param(
        [string]$CommandLine
    )

    $line = (To-NonNullString $CommandLine).Trim()
    if ($line.StartsWith("engine ", [System.StringComparison]::OrdinalIgnoreCase)) {
        $line = $line.Substring(7).Trim()
    }

    $tokens = $line -split "\s+"
    $keyTokens = New-Object System.Collections.Generic.List[string]
    foreach ($token in $tokens) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }
        if ($token.StartsWith("<") -or $token.StartsWith("[") -or $token.StartsWith("(")) {
            break
        }
        [void]$keyTokens.Add($token)
    }

    return ($keyTokens -join " ")
}

function Get-CommandSummaryForDocs {
    param(
        [string]$CommandKey
    )

    $key = (To-NonNullString $CommandKey).Trim().ToLowerInvariant()
    switch ($key) {
        "summary" { return "Shows a quick overview of the currently loaded profile state." }
        "profile summary" { return "Shows profile-focused counts and status." }
        "profile load" { return "Loads a `.pr0file` into memory so later commands act on it." }
        "profile save" { return "Saves current in-memory profile state to a `.pr0file`." }
        "profile new" { return "Creates an empty profile and saves it to the given `.pr0file` (overwrites an existing file)." }
        "profile save-last" { return "Saves to JoyPro's last-used profile path." }
        "profile validate" { return "Validates the loaded profile and reports both warnings and errors across relation items, binds, modifiers, and active-node states." }
        "dcs scan" { return "Parses DCS input files and previews what would be imported, without applying changes." }
        "dcs import" { return "Parses DCS input files and applies results to the in-memory profile." }
        "dcs export" { return "Writes current profile mappings back to DCS-compatible files." }
        "dcs defaults cut" { return "Backs up joystick defaults and removes stick-specific defaults from DCS game-folder aircraft input files." }
        "dcs defaults restore" { return "Restores joystick defaults from BACKUP_JOYSTICK_DEFAULTS back into DCS game-folder aircraft input files." }
        "dcs strip-defaults" { return "Switches DCS's default joystick binds off in the DCS installation (every joystick profile lua wrapped, combos = nil, original kept as <file>.nocombo.bak), or restores the originals; status and auto (re-apply after a DCS update)." }
        "dcs device-luas" { return "Moves the device-specific default files of the DCS installation into JoyPro's backup folder so DCS uses default.lua for every device, or puts them back." }
        "dcs verify-clean" { return "Runs DCS's own input code (luae.exe) on a copy of a Saved Games input folder and lists every DCS default bind still active on the joysticks." }
        "dcs refresh clean" { return "Automates DCS Controls -> Clear All flow and rebuilds JoyPro clean-profile baseline files." }
        "dcs refresh id" { return "Automates DCS Controls -> Make HTML flow and refreshes DB HTML files from exported layouts." }
        "dcs refresh preview-clicks" { return "Shows where DCS XML/OCR click anchors resolve on your current screen and writes an annotated preview image." }
        "dcs ffb list" { return "Lists DCS force-feedback inversion/swap settings per device from your profile/device context." }
        "dcs ffb set" { return "Creates or updates DCS force-feedback settings (invert X/Y, swap axis) for one device." }
        "dcs ffb clear" { return "Clears DCS force-feedback settings for one or more devices (or all)." }
        "il2 scan" { return "Parses IL2 input files and previews import results." }
        "il2 import" { return "Parses IL2 input files and applies results to the in-memory profile." }
        "il2 export" { return "Exports current profile mappings to IL2-compatible files." }
        "db summary" { return "Shows DB health/coverage summary for clean profile data." }
        "db list" { return "Lists DB entries, optionally filtered." }
        "db check" { return "Validates DB completeness and reports missing required data." }
        "db games" { return "Lists available game names from DB catalog." }
        "db planes" { return "Lists plane/module names from DB catalog." }
        "db paths" { return "Shows the program folder (JoyPro's own data), the game-data folder <data>\GameData (lists built from your games) and, per game folder, where it is read from (game-data or program) and its recorded game build (docs/installs.md, Game data)." }
        "games paths" { return "Discovers install/config paths for DCS, IL2, and Star Citizen on this machine." }
        "games integrations" { return "Lists the games supported through the game registry (id, aliases, experimental flag, DB refresh support)." }
        "games support" { return "How far JoyPro supports each game (docs/games/support.md): one row per title with the feature marks taken from the code (import, export, keyboard, modifiers, list refresh, watcher, axis tuning, settings, force feedback, vJoy / Xbox modes, virtual functions, function tables, cockpits, cheat sheets) and the in-game test results, footnotes and per-title facts of docs/games/support-notes.json (built into engine.exe). JSON by default; --markdown writes the page (tools/game_support.ps1), --html the page the GUI opens. Exit 1 when the notes miss a registered game, name an unknown game or cell, claim more than the code or leave a partial mark without a reason." }
        "layout render" { return "Renders joystick layout images per game/plane (command descriptions) and overview images (relation names) from JoyPro .layout mappings." }
        "devices list" { return "Lists connected devices and/or devices referenced in profile data." }
        "devices inputs" { return "Lists full supported input-token catalogs (joystick axes/buttons/POV and keyboard key names for DCS/IL2)." }
        "devices exchange" { return "Replaces one device name with another across profile binds, modifiers, and device metadata." }
        "devices remove" { return "Removes all references to one device from profile binds, modifiers, and device metadata." }
        "relations list" { return "Lists relations (logical actions) in the profile." }
        "relations show" { return "Shows one relation in detail." }
        "relations add" { return "Creates a relation with node targets and optional bind/group data." }
        "relations group list" { return "Lists groups on one relation, or all groups across all relations." }
        "relations group add" { return "Adds group tags to a relation." }
        "relations group remove" { return "Removes group tags from a relation." }
        "relations group rename" { return "Renames a group across all relations that currently use it." }
        "relations item add" { return "Adds node targets to an existing relation." }
        "relations item remove" { return "Removes node targets from an existing relation." }
        "relations item enable" { return "Enables selected node targets on a relation." }
        "relations item disable" { return "Disables selected node targets on a relation." }
        "relations mass delete" { return "Deletes many relations in one command using explicit names, query filter, or --all." }
        "relations mass duplicate" { return "Duplicates many relations in one command." }
        "relations mass merge" { return "Merges selected relations into one new relation name." }
        "relations mass split" { return "Splits selected planes out of selected relations into new postfix relations." }
        "relations mass add-prefix" { return "Adds a prefix to many relation names at once." }
        "relations mass add-postfix" { return "Adds a postfix to many relation names at once." }
        "relations mass remove-part" { return "Removes a substring from many relation names at once." }
        "relations mass export" { return "Exports selected relations in legacy .rl or JSON format." }
        "relations export" { return "Exports relations to JSON." }
        "relations import" { return "Imports relations from JSON." }
        "relations edit" { return "Opens relations JSON in an external editor for round-trip editing." }
        "binds list" { return "Lists bind entries (hardware mapping layer)." }
        "binds show" { return "Shows one bind in detail." }
        "binds set" { return "Creates or updates one relation bind (device/input/modifiers and optional tuning values)." }
        "binds clear" { return "Unbinds one relation (remove bind entirely or clear only input token)." }
        "binds modifier list" { return "Lists modifiers on one bind, or all modifiers across all binds." }
        "binds modifier add" { return "Adds one or more modifiers to a bind." }
        "binds modifier remove" { return "Removes one or more modifiers (or all) from a bind." }
        "binds modifier rename" { return "Renames a modifier across all binds, or only one selected bind." }
        "binds unbind" { return "Removes bind links by input token (DCS or IL2 format)." }
        "binds tuning models" { return "Lists the axis response models (DCS, IL-2 Korea, generic) and their parameters." }
        "ffb models" { return "Lists the games whose force feedback settings JoyPro manages and their settings (DCS ffDiffs per device and aircraft, IL-2 startup.cfg game-wide)." }
        "ffb show" { return "Lists the stored force feedback settings; with --device and --game, what that device gets in the game (and aircraft)." }
        "ffb set" { return "Sets force feedback settings of a device (or * for game-wide ones) for a game or one aircraft; written on the next export of that game." }
        "ffb clear" { return "Removes a scope's force feedback settings, or single values with --param; the game file's values then stay as they are." }
        "ffb telemetry status" { return "JoyPro's own telemetry force feedback (JoyPro.Ffb): the settings, per game whether its telemetry is set up and the game runs, the JoyPro.Ffb process and its last status line." }
        "ffb telemetry effects" { return "Lists the effect names of the built-in force feedback graph (buffet, gforce, gear, flaps, speedbrake, runway, guns, hits, shake, spring) that the per-game switches refer to." }
        "ffb telemetry config show" { return "Shows the telemetry force feedback settings file (<data>\Ffb\Telemetry\telemetry-ffb.json) with every default filled in." }
        "ffb telemetry config set" { return "Sets telemetry force feedback settings: global (enabled, device, strength, max-force = the hard force cap, invert-x/y, tick-rate, timeout), per game (enabled, port, graph, strength, url, poll-hz), per aircraft (graph, strength) or per effect (enabled, strength)." }
        "ffb telemetry config clear" { return "Resets a scope of the telemetry force feedback settings to the defaults (a game, an aircraft, an effect, or everything)." }
        "ffb telemetry setup dcs|il2|il2korea|warthunder" { return "Sets a game up to send telemetry to JoyPro: DCS gets Saved Games\DCS*\Scripts\Hooks\JoyProFfb.lua (Export.lua is not touched), IL-2 one addrN line in [KEY = telemetrydevice] of data\startup.cfg; War Thunder needs nothing. Backed up; --remove undoes it; --dry-run lists the changes." }
        "binds tuning show" { return "Shows the axis tuning of a bind per scope (base, game, plane, family) and, with --game, what that game (and plane) uses." }
        "binds tuning set" { return "Sets axis tuning for one scope: base (all games), game:<game>, plane:<game>:<plane> or family:<family>. A game without its own tuning gets the base curve fitted to its model." }
        "binds tuning clear" { return "Removes the tuning of one scope; the scope inherits again." }
        "binds tuning preview" { return "Prints the response curve a game (and plane) applies to the bind: stick position -> what the game uses." }
        "binds detent show" { return "Detent mode of a throttle relation: its settings, the calibration of the bound device axis, the aircraft grouped by where their afterburner starts (game data or the relation's own value) and per game the curve it gets and how exactly the game expresses it." }
        "binds detent set" { return "Switches detent mode on or off, sets what aircraft without afterburner get (full power at the detent or the relation's curve), the margin below the afterburner, and an aircraft's own afterburner start (--target <game>:<plane>=ab:0.78|full|linear|default)." }
        "binds detent calibrate" { return "Stores where the bound device axis has its idle, afterburner detent, full and cut-off positions (raw axis 0..1; the GUI's wizard reads them live). Every throttle relation on that axis uses it." }
        "binds detent uncalibrate" { return "Removes the detent calibration of the bound device axis." }
        "binds detent gates" { return "Lists the shipped throttle gate data (DB\Detents\throttle-gates.json): per aircraft where its afterburner and idle cut-off are on the game's throttle axis, with source and confidence." }
        "binds detent merge" { return "Merges per-aircraft throttle relations of one device axis into one relation in detent mode: the device's detent and each aircraft's afterburner start are read from the old curves. Without --apply only the plan; profile history undo takes a merge back." }
        "binds export" { return "Exports binds to JSON." }
        "binds import" { return "Imports binds from JSON." }
        "binds edit" { return "Opens binds JSON in an external editor for round-trip editing." }
        "sync status" { return "Per game the config files (or registry values) JoyPro manages, their fingerprint and whether the game changed them since JoyPro's last export, accept or apply (docs/sync-watcher.md)." }
        "sync accept" { return "Marks the game's current config as known without importing anything." }
        "support bundle" { return "Writes the support file (docs/logging.md ""Support file""), everything a maintainer needs in one zip: summary with versions, system, the games' environment, the devices, every part with its size and what was trimmed; JoyPro's logs (GUI, engine, tray and watcher, force feedback) of at least 14 days and back to the oldest history version of the open profile, at most 100 MB, the oldest trimmed first; crash reports and the last errors with stack traces; JoyPro's settings and state files, the versions of the game lists and installed packs; devices and the vJoy / HidHide state; per game the watcher's record, the list of JoyPro's backups and the newest set, for MSFS the profiles JoyPro reads. With --profile and --include-profile true (the default when --profile is given) also the open profile, its history (the newest versions up to --history-cap-mb, default 50, plus the whole index) and the games' current bind files with JoyPro's .jp / .joypro-meta.json files. Every text is scrubbed (user folder -> %USERPROFILE%), the profile copies inside their strings; originals are only read; nothing is uploaded. --list only prints the parts and entries." }
        "support unpack" { return "For maintainers: unpacks a support file to <folder>\support, the profile to <folder>\profile and its history to <folder>\data\ProfileHistory\<id of that path>, so engine profile history list / show / restore work on the copy with JOYPRO_DATA_DIR=<folder>\data (docs/logging.md ""Restoring a version from a support file""). Nothing of this PC's JoyPro is touched." }
        "sync diff" { return "Reads the game's config with its own import into a scratch profile and lists what changed that JoyPro manages (added, removed, changed-tuning, changed-modifiers) with the candidate relations." }
        "sync apply" { return "Applies the user's choice per change (a relation, a new relation or ignore) to the profile; then the config counts as known." }
        "vjoy status" { return "Virtual joystick mode (docs/virtual-joystick.md): vJoy and HidHide installed, the vJoy devices (configuration and DirectInput name), HidHide's state, JoyPro's HidHide session (stale when its owner is gone), the games native/virtual, the mapping table and what vJoy must offer. Read-only." }
        "vjoy mode" { return "Switches a game between its physical devices (native), the virtual joystick (virtual, vJoy) and the virtual Xbox 360 pad (xbox, ViGEmBus: for games that only read XInput or one device; the game's gamepad layout pins actions to their controls, the rest fills sticks, triggers, 14 buttons and the D-pad, what does not fit is listed). Virtual gives every input the game's relations use (axes, buttons, hats, modifier buttons) a fixed vJoy slot; export, import and sync of the game then work on the vJoy slots." }
        "vjoy plan" { return "Shows the mapping table and the vJoy configuration it needs without saving anything." }
        "vjoy map" { return "Saves the mapping table in the profile: existing slots never move, new inputs get free slots (an axis keeps its own axis where free); --prune drops unused slots, --reset starts over." }
        "vjoy settings" { return "Which vJoy devices JoyPro may use (--first-device/--last-device), the GUI's auto start, and whether HidHide hides the physical devices." }
        "vjoy exe" { return "Lists (and adds/removes) the game executables HidHide blocks in virtual mode: found in the game's installation, plus your own." }
        "vjoy forwarder-config" { return "Writes the mapping table for JoyPro.VJoy.exe, the process that forwards the physical inputs to vJoy." }
        "vjoy configure-vjoy" { return "Runs vJoyConfig (one administrator prompt) for the vJoy devices that lack axes, buttons or hats for the table; never makes a device smaller." }
        "vjoy apply-hidhide" { return "Hides the mapped physical devices from the virtual-mode games only (HidHide block list; an existing allow list of the user is kept), never hides vJoy, parks dormant entries. One administrator prompt; the journal is written first." }
        "vjoy restore-hidhide" { return "Undoes exactly JoyPro's HidHide changes and gives HidHide its previous state back (one administrator prompt)." }
        "vjoy recover" { return "Restores a HidHide session whose owner (the GUI) is gone, e.g. after a crash; --force also while the owner runs." }
        default {
            # Registry games (engine <game> paths|catalog|scan|import|export|refresh-db) share one command family.
            if ($key -match "^\S+ (paths|catalog|scan|import|export|refresh-db)$") {
                switch ($Matches[1]) {
                    "paths" { return "Lists the config files of this game found on this machine." }
                    "catalog" { return "Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv)." }
                    "scan" { return "Reads the game's control file and shows the joystick binds it contains, without changing the profile." }
                    "import" { return "Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list)." }
                    "export" { return "Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first." }
                    "refresh-db" { return "Updates this game's action catalog from the game or its files." }
                }
            }
            return "Runs this command operation."
        }
    }
}

function Get-ArgumentTokensForCommandLine {
    param(
        [string]$CommandLine
    )

    $tokens = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $matches = [System.Text.RegularExpressions.Regex]::Matches(
        $CommandLine,
        "--[a-z0-9-]+|<[^>]+>|\[filter\]",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    foreach ($match in $matches) {
        $token = $match.Value
        if ([string]::Equals($token, "[filter]", [System.StringComparison]::OrdinalIgnoreCase)) {
            $token = "filter"
        }
        if ($seen.Add($token)) {
            [void]$tokens.Add($token)
        }
    }

    return $tokens
}

function Get-ArgumentDocLine {
    param(
        [string]$Token
    )

    $normalized = (To-NonNullString $Token).Trim().ToLowerInvariant()
    switch ($normalized) {
        "filter" { return "Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'." }
        "<path.pr0file>" { return "Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file." }
        "<saved-games-path>" { return "Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta." }
        "<dcs-install-path>" { return "Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta." }
        "<il2-root-path>" { return "Path to IL2 root folder. Example: D:\\Games\\IL-2 Sturmovik Great Battles." }
        "<path.json>" { return "Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json." }
        "<path.rl|path.json>" { return "Path to relation export target (.rl legacy binary or .json). Example: C:\\JoyPro\\exports\\selection.rl." }
        "<old-device>" { return "Source device name to replace. Example: LEFT VPC Throttle {B6781E40-0000-0000-0000-504944564944}." }
        "<new-device>" { return "Target device name to assign. Example: RIGHT VPC Throttle {C7122B40-0000-0000-0000-504944564944}." }
        "<relation-name>" { return "Name of target relation. Example: Gear Toggle." }
        "<name>" { return "Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle." }
        "<aircraft-key>" { return "MSFS aircraft key (package name, DB\\MSFS\\aircraft.tsv). Example: asobo-aircraft-c152." }
        "<postfix>" { return "Suffix appended for split output relation names. Example: CarrierOps." }
        "<group1>" { return "First group/tag name. Example: Startup." }
        "<old-group>" { return "Existing group name to rename. Example: Startup." }
        "<new-group>" { return "New group name to apply. Example: Core Startup." }
        "<bind-or-relation>" { return "Relation name or bind selector. Example: Trim Pitch." }
        "<device-name>" { return "Device name token. Example: VKB Gladiator NXT {B4280440-0000-0000-0000-504944564944}." }
        "<mod1>" { return "First modifier key. Example: LShift." }
        "<old-modifier>" { return "Existing modifier name to rename. Example: LAlt." }
        "<new-modifier>" { return "New modifier name to apply. Example: LAlt_Hold." }
        "<csv>" { return "Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle." }
        "<n>" { return "Integer numeric value. Example: 45." }
        "<0..200>" { return "Integer score threshold from 0 to 200. Example: 98." }
        "<path>" { return "Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png." }
        "<device::input|input>" { return "Input selector. Example: VKB Gladiator NXT::JOY_BTN1 or JOY_BTN1." }
        "<value>" { return "Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0." }
        "--profile" { return "Global option: load this .pr0file before running the command." }
        "--out" { return "Global option: write result output to file path instead of terminal." }
        "--editor" { return "Global option: editor command for relations edit and binds edit (use {file} placeholder)." }
        "--text" { return "Global option: render human-readable text instead of JSON output." }
        "--status" { return "Global option: emit live status/progress updates to stderr while command runs (true/false)." }
        "--progress" { return "Global option: emit only the machine-readable progress lines of long commands to stderr (true/false)." }
        "--benchmark" { return "Global option: emit timing summary lines for command phases to stderr (true/false)." }
        "--load-corrections" { return "Global option: enable/disable load-time profile correction mechanics." }
        "--auto-add-db-items" { return "Global option: allow corrected DB nodes to be auto-added while loading profile." }
        "--remap-missing-devices" { return "Global option: allow missing profile device names to be remapped to closest matches." }
        "--confirm-device-remap" { return "Global option: prompt (true) or auto-accept (false) device remaps." }
        "--correction-mode" { return "Global option: correction strictness (strict, balanced, aggressive)." }
        "--correction-auto-threshold" { return "Global option: numeric score threshold (0..200) to auto-apply relation-item remaps." }
        "--confirm-corrections" { return "Global option: prompt (true) or auto-accept (false) medium-confidence item remaps." }
        "--correction-report" { return "Global option: write load correction report to .json or .txt/.log path." }
        "--path" { return "Optional explicit path override. Use when you prefer option form over positional path." }
        "--install-path" { return "DCS install path override (root folder that contains Mods\\aircraft)." }
        "--restore" { return "dcs strip-defaults / device-luas: put the original DCS files back (same as the restore action)." }
        "--skip-ui-layer" { return "dcs strip-defaults: leave Config\\Input\\UiLayer unpatched (true/false; the flag alone means true)." }
        "--remember" { return "dcs strip-defaults / device-luas: keep the switch for automatic re-apply after DCS updates (default true)." }
        "--saved-games" { return "Saved Games\\DCS folder whose Mods are included (default: every Saved Games\\DCS* folder found)." }
        "--luae" { return "Path to DCS's luae.exe for the load check (default: the install's bin\\luae.exe)." }
        "--no-load-check" { return "dcs strip-defaults: skip the check with DCS's own Lua loader after patching (tests only)." }
        "--include-disconnected" { return "dcs verify-clean: also check devices that only have files, through a fake stick of their type (true/false)." }
        "--include-sim-defaults" { return "MSFS import/scan: also the sim's default presets of devices for which the sim's records (profile_00) do not show whether it uses them." }
        "--max-listed" { return "dcs verify-clean: how many remaining default binds are listed (counts are always complete)." }
        "--backup-path" { return "Override backup folder path for dcs defaults cut/restore. Default: <saved-games>\\BACKUP_JOYSTICK_DEFAULTS." }
        "--ocr" { return "Use OCR image matching click flow instead of fixed XML anchor click offsets (true/false)." }
        "--launch" { return "Start DCS automatically before refresh automation (true/false)." }
        "--keep-running" { return "Leave DCS running after refresh command completes (true/false)." }
        "--dry-run" { return "Plan mode only: do not click, launch, or change files; report what would run." }
        "--menu-wait-seconds" { return "Non-OCR wait time before click automation starts. Example: --menu-wait-seconds 45." }
        "--ocr-menu-timeout-seconds" { return "OCR max wait for DCS main menu template detection. Example: --ocr-menu-timeout-seconds 600." }
        "--settle-seconds" { return "Post-click settle wait for DCS file writes. Example: --settle-seconds 60." }
        "--modules-to-scan" { return "Additional module loop count for dcs refresh id export cycle. Example: --modules-to-scan 5." }
        "--clicks-xml" { return "Path to Clicks.xml anchor file for non-OCR refresh clicks." }
        "--clicks-dir" { return "Folder containing OCR template images (cog1.0.png, controls1.0.png, etc.)." }
        "--dcs-exe" { return "Explicit DCS executable path override. Example: --dcs-exe 'D:\\Games\\DCS World\\bin\\DCS_updater.exe'." }
        "--default-stick" { return "Preferred joystick name for clean profile extraction if that .diff.lua exists." }
        "--preview-clicks" { return "Generate click preview data/image while running refresh commands." }
        "--preview-image" { return "Output .png path for click preview image overlay." }
        "--gui-scale" { return "Override GUI scale multiplier used for high-resolution XML click anchor offsets." }
        "--window-left" { return "Manual DCS window left coordinate override (integer)." }
        "--window-top" { return "Manual DCS window top coordinate override (integer)." }
        "--window-width" { return "Manual DCS window width override (integer > 0)." }
        "--window-height" { return "Manual DCS window height override (integer > 0)." }
        "--name" { return "Name value for commands that create/merge entities. Example: --name 'Merged Startup'." }
        "--value" { return "Text value used by mass rename operations. Example: --value TEMP_." }
        "--old" { return "Source device name for devices exchange. Example: --old 'LEFT VPC Throttle'." }
        "--new" { return "Target device name for devices exchange. Example: --new 'RIGHT VPC Throttle'." }
        "--postfix" { return "Suffix value used by relations mass split. Example: --postfix CarrierOps." }
        "--relation" { return "Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'." }
        "--relations" { return "Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'." }
        "--query" { return "Select relations by substring query for mass operations. Example: --query Startup." }
        "--plane" { return "Include one plane/module per flag. Example: --plane A-10C_2." }
        "--planes" { return "Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet." }
        "--device" { return "Include one device per flag. Example: --device LEFT VPC Throttle." }
        "--devices" { return "Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle." }
        "--ignore-device" { return "Exclude one device per flag. Example: --ignore-device Keyboard." }
        "--ignore-devices" { return "Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse." }
        "--include-defaults" { return "Include defaults in DCS import/scan." }
        "--import-inverted" { return "Include axis inversion settings." }
        "--import-slider" { return "Include DCS slider axis setting." }
        "--import-curvature" { return "Include axis curvature settings." }
        "--import-deadzone" { return "Include axis deadzone settings." }
        "--import-saturation-x" { return "Include DCS saturation X setting." }
        "--import-saturation-y" { return "Include DCS saturation Y setting." }
        "--confirm" { return "Controls suggestion confirmation (true) vs auto-accept (false)." }
        "--merge" { return "Merge import results into existing in-memory profile." }
        "--save-profile" { return "After import, save profile to this .pr0file path." }
        "--save-last" { return "After import, save to last profile path." }
        "--mode" { return "Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite." }
        "--fail-on-warnings" { return "For profile validate: treat warnings as failure (non-zero exit) in addition to errors." }
        "--keep-keyboard-defaults" { return "DCS export only: keep existing keyboard defaults/keyboard diff files untouched (true/false)." }
        "--nuke-untouched-devices" { return "DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false)." }
        "--aircraft" { return "MSFS export only: also write the relations limited to this aircraft (key from DB\\MSFS\\aircraft.tsv); the sim's presets apply to every aircraft." }
        "--keyboard" { return "Include keyboard clean-profile checks for supported DB commands." }
        "--no-keyboard" { return "Disable keyboard clean-profile checks for supported DB commands." }
        "--strict-names" { return "For DB summary/list/check: require exact clean filename match (no alias fallback accepted)."}
        "--missing" { return "For db list, show only missing entries." }
        "--game" { return "Game filter value. Example: DCS or IL2Game." }
        "--games" { return "Game filter values as CSV. Example: DCS,IL2Game,StarCitizen." }
        "--connected" { return "For devices list, show connected devices only." }
        "--profile-devices" { return "For devices list, show profile-referenced devices only." }
        "--include-off" { return "For devices inputs, include `_OFF` release variants for joystick axis/button/POV tokens." }
        "--layout-map" { return "Map joystick name to layout/image file path. Example: --layout-map 'VKB Gladiator NXT=C:\\JoyPro\\LayoutImages\\VKB.layout'." }
        "--layout-maps" { return "CSV version of --layout-map entries. Example: --layout-maps 'StickA=C:\\A.layout,StickB=C:\\B.layout'." }
        "--include-overview" { return "For layout render: include overview image(s) in export root that show relation names on each input token (true/false)." }
        "--include-planes" { return "For layout render: include per-game/per-plane output images showing command descriptions (true/false)." }
        "--skip-missing-layouts" { return "For layout render: skip unmapped/missing joystick layouts as warnings (true) or fail the command (false)." }
        "--axis" { return "Set relation type to axis (explicit override)." }
        "--button" { return "Set relation type to button (explicit override)." }
        "--type" { return 'Set relation type via value token (`axis` or `button`).' }
        "--node" { return "Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet." }
        "--from" { return "Copy structure from existing relation(s)." }
        "--group" { return "Attach relation group tag. Example: --group Startup." }
        "--input" { return "Input token. Example: --input JOY_BTN1 or --input joy0_b0." }
        "--modifier" { return "Modifier key token. Example: --modifier LShift." }
        "--clear-modifiers" { return "Clears all modifiers from the selected bind." }
        "--clear-input" { return "Clears bind input token(s) (button/axis) while keeping the bind object." }
        "--inverted" { return "Set axis inversion flag on selected bind (true/false)." }
        "--slider" { return "Set axis slider flag on selected bind (true/false)." }
        "--curvature" { return "Set curvature list. Example: --curvature 0.1,0.2,0.3." }
        "--deadzone" { return "Set deadzone numeric value. Example: --deadzone 0.15." }
        "--saturation-x" { return "Set saturation X numeric value. Example: --saturation-x 0.9." }
        "--saturation-y" { return "Set saturation Y numeric value. Example: --saturation-y 0.8." }
        "--curve-points" { return "A curve with any number of stops (x:y pairs in 0..1, x increasing), or none to drop them (X-Plane's response curve). Example: --curve-points 0:0,0.4:0.3,1:1." }
        "--invert-x" { return "Set DCS FFB invert X flag (true/false)." }
        "--invert-y" { return "Set DCS FFB invert Y flag (true/false)." }
        "--swap-axis" { return "Set DCS FFB swap-axis flag (true/false)." }
        "--double-tap" { return "Set double-tap behavior flag (true/false)." }
        "--scope" { return "Axis tuning scope: base, game:<game>, plane:<game>:<plane> or family:<family>. Example: --scope game:IL2Korea." }
        "--param" { return "A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10." }
        "--points" { return "Number of preview samples from -1 to 1. Example: --points 21." }
        "--input-only" { return "For binds clear, clear only input tokens instead of removing whole bind." }
        "--id" { return "Node ID. Example: --id d3007pnilu3007cd16vd1vpnilvu0." }
        "--all" { return "Apply operation to all matches (or select all relations/devices where supported)." }
        "--format" { return "Format selector. Example values: dcs/il2 for binds unbind, rl/json for relations mass export." }
        "<config-path>" { return "Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini." }
        "<source-path>" { return "Path to read actions from (game install or control file); optional." }
        "--replace" { return "For game import: start from an empty relation list instead of merging into the loaded profile." }
        "--markdown" { return "For games support: print the Markdown page (docs/games/support.md) instead of JSON." }
        "--html" { return "For games support: print the page as one HTML file (what the GUI's Supported games opens)." }
        "--notes" { return "For games support: read the verification notes from this JSON file instead of the copy built into engine.exe. Example: docs\games\support-notes.json." }
        "--choices" { return "For sync apply: JSON file with the user's choice per change id: {""Choices"":[{""Id"":""c1a2b3c4d5"",""Target"":""Gear Toggle""},{""Id"":""...""," + """Target"":""new"",""Name"":""My Relation""},{""Id"":""..."",""Target"":""ignore""}]}." }
        "<config>" { return "The game's config (file, folder or HKCU registry key) as engine sync status lists it; default: the one the export uses." }
        "<file.json>" { return "Path to a JSON file. Example: C:\JoyPro\sync-choices.json." }
        "<file.zip>" { return "Path of the zip to write. Example: C:\JoyPro\JoyPro2-support.zip." }
        "--list" { return "For support bundle: only print what the zip would hold; nothing is written." }
        "--days" { return "For support bundle: logs and crash reports of at least the last N days (default 14; older ones when the open profile's history goes further back)." }
        "--include-profile" { return "For support bundle: true puts the open profile (--profile), its history, the games' current bind files and JoyPro 1's profiles (Documents\JoyPro\*.pr0file) in; false leaves them out. Default: true when --profile is given." }
        "--history-cap-mb" { return "For support bundle: the most megabytes of stored history copies (default 50); the newest versions go in, summary.txt names the ones left out. Example: 50." }
        "--to" { return "The target: the new position (relations item move), the file or folder to write (telemetry export), the folder to unpack into (support unpack). Example: C:\Cases\case1." }
        "<folder>" { return "Folder path. Example: C:\Cases\case1." }
        default { return "Command argument. See examples for typical values." }
    }
}

function Get-CommandInteractionNotes {
    param(
        [string]$CommandKey
    )

    $notes = New-Object System.Collections.Generic.List[string]
    $key = (To-NonNullString $CommandKey).Trim().ToLowerInvariant()

    if ($key -eq "dcs scan" -or $key -eq "dcs import" -or $key -eq "il2 scan" -or $key -eq "il2 import") {
        [void]$notes.Add("If no device include filter is given, all devices are considered.")
        [void]$notes.Add("Ignore filters (--ignore-device, --ignore-devices) remove matches even if included.")
        [void]$notes.Add("--confirm false auto-accepts closest suggested device names.")
    }
    if ($key -eq "dcs import" -or $key -eq "il2 import") {
        [void]$notes.Add("--save-profile and --save-last are valid on import commands only.")
    }
    if ($key -eq "dcs export" -or $key -eq "il2 export") {
        [void]$notes.Add("Import-only filters/settings are not valid for export commands.")
    }
    if ($key -eq "dcs export" -or $key -eq "il2 export") {
        [void]$notes.Add("--mode is valid for DCS export and IL2 export.")
    }
    if ($key -eq "dcs export") {
        [void]$notes.Add("--keep-keyboard-defaults controls whether keyboard files are preserved untouched.")
        [void]$notes.Add("--nuke-untouched-devices deletes non-exported device files (except keyboard when keep-keyboard-defaults is true).")
    }
    if ($key -eq "dcs defaults cut") {
        [void]$notes.Add("If paths are omitted, the command tries to auto-detect DCS Saved Games and install locations.")
        [void]$notes.Add("Copies `default.lua` to backup and moves non-default joystick files into backup.")
        [void]$notes.Add("Default backup location is `<saved-games>\\BACKUP_JOYSTICK_DEFAULTS` unless --backup-path is provided.")
    }
    if ($key -eq "dcs defaults restore") {
        [void]$notes.Add("If paths are omitted, the command tries to auto-detect DCS Saved Games and install locations.")
        [void]$notes.Add("Restores files by copying backup aircraft input folders back into `Mods\\aircraft`.")
        [void]$notes.Add("Default backup location is `<saved-games>\\BACKUP_JOYSTICK_DEFAULTS` unless --backup-path is provided.")
    }
    if ($key -eq "dcs strip-defaults") {
        [void]$notes.Add("Off by default. Patched DCS files fail the integrity check of multiplayer servers that enforce it.")
        [void]$notes.Add("Same marker (-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]) and backup name as the community strip script; either one restores the other's files.")
        [void]$notes.Add("apply remembers the switch; JoyPro re-applies it at its start (auto) and before every DCS list refresh. restore switches it off.")
        [void]$notes.Add("Never touches Saved Games\\DCS\\Config\\Input. Refused while DCS or DCS_updater runs.")
    }
    if ($key -eq "dcs device-luas") {
        [void]$notes.Add("Files another Lua file of the module refers to stay; so does the UI layer (VR controllers).")
        [void]$notes.Add("The moved files and manifest.json are in <data>\\DcsDefaults\\DeviceLuas. Rebuild the DCS clean profiles afterwards (the GUI does it).")
    }
    if ($key -eq "dcs verify-clean") {
        [void]$notes.Add("Read only: works on a temporary copy of Config\\Input. Needs the DCS installation (luae.exe and Scripts).")
        [void]$notes.Add("Defaults Left On JoyPro Devices counts remaining defaults on files JoyPro wrote (.jp next to them); after an export it should be 0.")
    }
    if ($key -eq "dcs refresh clean") {
        [void]$notes.Add("Requires DCS Saved Games and install paths (auto-detect is attempted when omitted).")
        [void]$notes.Add("Without --ocr, XML click anchors from Clicks.xml are used; with --ocr, template image matching is used.")
        [void]$notes.Add("--dry-run skips launch/click/file changes and reports planned steps only.")
        [void]$notes.Add("--default-stick prefers <name>.diff.lua when rebuilding CleanProfile output.")
        [void]$notes.Add("--window-left/top/width/height and --gui-scale control non-OCR click coordinate resolution.")
    }
    if ($key -eq "dcs refresh id") {
        [void]$notes.Add("Requires DCS Saved Games and install paths (auto-detect is attempted when omitted).")
        [void]$notes.Add("Exports per-module HTML from DCS and copies the largest non-keyboard/mouse/trackir HTML per plane into DB\\DCS.")
        [void]$notes.Add("--modules-to-scan adds extra loop count on top of detected install/instance module counts.")
        [void]$notes.Add("--dry-run skips launch/click/file changes and reports planned steps only.")
    }
    if ($key -eq "dcs refresh preview-clicks") {
        [void]$notes.Add("When --ocr false, outputs XML-anchor click landing points for the active/overridden window.")
        [void]$notes.Add("When --ocr true, attempts live template detection and reports found/missing click targets.")
        [void]$notes.Add("Writes an annotated preview image (`--preview-image` or temp path).")
    }
    if ($key -eq "dcs ffb list") {
        [void]$notes.Add("Without --device/--devices, lists all discovered profile/connected/FFB devices.")
        [void]$notes.Add("--connected and --profile-devices mirror the device list filtering behavior.")
        [void]$notes.Add("--confirm false auto-accepts closest device suggestion when a requested name is fuzzy.")
    }
    if ($key -eq "dcs ffb set") {
        [void]$notes.Add("At least one setting flag is required: --invert-x, --invert-y, or --swap-axis.")
        [void]$notes.Add("If the target device setting does not exist yet, it is created.")
    }
    if ($key -eq "dcs ffb clear") {
        [void]$notes.Add("Use --all to remove every stored DCS FFB device setting.")
        [void]$notes.Add("You can clear a single device by positional name or --device/--devices.")
    }
    if ($key -eq "profile validate") {
        [void]$notes.Add("Validation always reports both warnings and errors.")
        [void]$notes.Add("Default exit behavior fails only when errors exist.")
        [void]$notes.Add("Set --fail-on-warnings true for CI or strict runs where warnings should also fail the command.")
    }
    if ($key -eq "db planes") {
        [void]$notes.Add("--game is valid only with db planes.")
    }
    if ($key -eq "games paths") {
        [void]$notes.Add("Without filters, reports all supported games.")
        [void]$notes.Add("Use --game/--games to narrow output to selected game(s).")
    }
    if ($key -eq "db list") {
        [void]$notes.Add("--missing narrows list to missing clean-profile entries.")
        [void]$notes.Add("--strict-names marks alias-matched clean files as mismatches in `db check` and surfaces alias states in output.")
    }
    if ($key -eq "db summary" -or $key -eq "db check") {
        [void]$notes.Add("Alias-matched clean files are shown separately from missing files.")
        [void]$notes.Add("Use --strict-names to fail if any clean files only match through legacy alias mapping.")
    }
    if ($key -eq "devices list") {
        [void]$notes.Add("No flag shows both connected and profile devices.")
        [void]$notes.Add("--connected and --profile-devices let you limit one side.")
    }
    if ($key -eq "devices inputs") {
        [void]$notes.Add("Outputs canonical joystick and keyboard token catalogs in JSON (default) or text (`--text`).")
        [void]$notes.Add("Use `--include-off` to include `_OFF` release variants for joystick inputs.")
        [void]$notes.Add('Keyboard section includes DCS-style tokens and IL2 `key_*` tokens for `binds set --format il2`.')
    }
    if ($key -eq "devices exchange") {
        [void]$notes.Add("Accepts positional <old-device> <new-device> or named options --old/--new.")
        [void]$notes.Add("Use --confirm false to auto-accept closest device suggestions for fuzzy names.")
        [void]$notes.Add("Updates binds, modifiers, and profile device metadata keys in one operation.")
    }
    if ($key -eq "devices remove") {
        [void]$notes.Add("Removes one target device at a time.")
        [void]$notes.Add("Removes matching bind assignments and modifier references, then prunes profile metadata entries for that device.")
    }
    if ($key -eq "relations add") {
        [void]$notes.Add("You need at least one source: --node, --id/--game, or --from.")
        [void]$notes.Add("Type is inferred from source relation (--from) or node IDs when possible.")
        [void]$notes.Add("Use --axis / --button / --type only when you want an explicit override.")
    }
    if ($key -eq "relations group list") {
        [void]$notes.Add("Without a relation name, lists all groups and where they are used.")
    }
    if ($key -eq "relations group remove" -or $key -eq "binds modifier remove") {
        [void]$notes.Add("Use --all to clear all values instead of specifying individual names.")
    }
    if ($key -eq "relations group rename") {
        [void]$notes.Add("Renames group usage across all relations that contain the old name.")
    }
    if ($key -eq "relations item add" -or $key -eq "relations item enable" -or $key -eq "relations item disable") {
        [void]$notes.Add("At least one target (--node or --id) is required.")
    }
    if ($key -eq "relations item remove") {
        [void]$notes.Add("Use target selectors or --all.")
    }
    if ($key -like "relations mass *") {
        [void]$notes.Add("Selection supports explicit --relation/--relations, filter --query, and --all.")
    }
    if ($key -eq "relations mass merge" -or $key -eq "relations mass split") {
        [void]$notes.Add("Selected relations must all be axis or all button.")
    }
    if ($key -eq "relations mass split") {
        [void]$notes.Add("Planes can be passed as PlaneName or Game:PlaneName.")
    }
    if ($key -eq "relations mass export") {
        [void]$notes.Add("Default format is .rl unless extension is .json or --format json is set.")
    }
    if ($key -eq "binds modifier list") {
        [void]$notes.Add("Without a bind/relation selector, lists all modifier names and usage counts.")
    }
    if ($key -eq "binds modifier rename") {
        [void]$notes.Add("If you omit bind/relation selector, rename is applied across all binds.")
    }
    if ($key -like "vjoy *") {
        [void]$notes.Add("Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).")
    }
    if ($key -eq "vjoy configure-vjoy" -or $key -eq "vjoy apply-hidhide" -or $key -eq "vjoy restore-hidhide" -or $key -eq "vjoy recover") {
        [void]$notes.Add("Changes the PC (one Windows administrator prompt); --dry-run shows the commands and changes nothing. Exit code 2 = the prompt was declined.")
    }
    if ($key -eq "binds set") {
        [void]$notes.Add("If relation has no bind yet, this command creates one.")
        [void]$notes.Add("Axis tuning options apply only to axis relations.")
        [void]$notes.Add("--format is only meaningful when --input is provided.")
    }
    if ($key -eq "binds clear") {
        [void]$notes.Add("Default behavior removes the entire bind from that relation.")
        [void]$notes.Add("Use --input-only to keep bind metadata and clear only button/axis input.")
    }
    if ($key -eq "binds unbind") {
        [void]$notes.Add("Positional value supports device::input shorthand.")
        [void]$notes.Add("If matches are ambiguous, use --all or narrow with --device / --game.")
    }

    return $notes
}

function Get-ExampleCommand {
    param(
        [string]$CommandKey
    )

    $key = (To-NonNullString $CommandKey).Trim().ToLowerInvariant()
    switch ($key) {
        "summary" { return @("engine summary --text") }
        "games support" { return @("engine games support --text", "engine games support --markdown --notes docs\games\support-notes.json --out docs\games\support.md", "engine games support --html --out 'C:\JoyPro\supported-games.html'") }
        "profile summary" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile summary --text") }
        "profile load" { return @("engine profile load 'C:\JoyPro\profiles\pilot_main.pr0file'") }
        "profile save" { return @("engine profile save 'C:\JoyPro\profiles\pilot_main.pr0file'") }
        "profile new" { return @("engine profile new 'C:\JoyPro\profiles\new_setup.pr0file'") }
        "profile save-last" { return @("engine profile save-last") }
        "profile validate" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile validate --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile validate --fail-on-warnings true --text") }
        "dcs scan" { return @("engine dcs scan 'C:\Users\Pilot\Saved Games\DCS.openbeta' --plane 'A-10C_2' --confirm false --text") }
        "dcs import" { return @("engine dcs import 'C:\Users\Pilot\Saved Games\DCS.openbeta' --device 'VKB Gladiator NXT' --merge --save-profile 'C:\JoyPro\profiles\dcs_import.pr0file' --text") }
        "dcs export" { return @("engine dcs export 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text") }
        "dcs defaults cut" { return @("engine dcs defaults cut 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --text") }
        "dcs defaults restore" { return @("engine dcs defaults restore 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --text") }
        "dcs strip-defaults" { return @("engine dcs strip-defaults apply --dry-run --text", "engine dcs strip-defaults apply --skip-ui-layer --text", "engine dcs strip-defaults status --text", "engine dcs strip-defaults --restore --text") }
        "dcs device-luas" { return @("engine dcs device-luas list --text", "engine dcs device-luas remove --text", "engine dcs device-luas restore --text") }
        "dcs verify-clean" { return @("engine dcs verify-clean 'C:\Users\Pilot\Saved Games\DCS' --text", "engine dcs verify-clean --planes 'FA-18C_hornet' --include-disconnected true --text") }
        "dcs refresh clean" { return @("engine dcs refresh clean 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --ocr false --preview-clicks --preview-image 'C:\JoyPro\logs\dcs_refresh_clean_preview.png' --confirm false --text") }
        "dcs refresh id" { return @("engine dcs refresh id 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --ocr true --modules-to-scan 0 --confirm false --text") }
        "dcs refresh preview-clicks" { return @("engine dcs refresh preview-clicks --ocr false --window-left 0 --window-top 0 --window-width 2560 --window-height 1440 --preview-image 'C:\JoyPro\logs\dcs_click_preview.png' --text") }
        "dcs ffb list" { return @("engine dcs ffb list --text", "engine dcs ffb list --device 'VKB Gladiator NXT' --confirm false --text") }
        "dcs ffb set" { return @("engine dcs ffb set 'VKB Gladiator NXT' --invert-x true --invert-y false --swap-axis false --text") }
        "dcs ffb clear" { return @("engine dcs ffb clear 'VKB Gladiator NXT' --text", "engine dcs ffb clear --all --text") }
        "il2 scan" { return @("engine il2 scan 'D:\Games\IL-2 Sturmovik Great Battles' --ignore-device 'Keyboard' --text") }
        "il2 import" { return @("engine il2 import 'D:\Games\IL-2 Sturmovik Great Battles' --devices 'VKB Gladiator NXT,LEFT VPC Throttle' --save-last --text") }
        "il2 export" { return @("engine il2 export 'D:\Games\IL-2 Sturmovik Great Battles' --mode merge-overwrite --text") }
        "db summary" { return @("engine db summary --keyboard --text") }
        "db list" { return @("engine db list 'A-10' --missing --text") }
        "db check" { return @("engine db check --text") }
        "db games" { return @("engine db games --text") }
        "db planes" { return @("engine db planes --game DCS 'A-10' --text") }
        "devices list" { return @("engine devices list --connected --text", "engine devices list --profile-devices --text") }
        "devices inputs" { return @("engine devices inputs --text", "engine devices inputs --include-off --text") }
        "devices exchange" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' devices exchange 'LEFT VPC Throttle' 'RIGHT VPC Throttle' --confirm false --text") }
        "devices remove" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' devices remove 'LEFT VPC Throttle' --confirm false --text") }
        "relations list" { return @("engine relations list 'Gear' --text") }
        "relations show" { return @("engine relations show 'Gear Toggle' --text") }
        "relations add" { return @("engine relations add 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0 --group 'Startup' --text", "engine relations add 'Pitch Axis' --type axis --node IL2Game:mc_pitch --text") }
        "relations group list" { return @("engine relations group list --text", "engine relations group list 'Gear Toggle' --text") }
        "relations group add" { return @("engine relations group add 'Gear Toggle' 'Startup' 'Common' --text") }
        "relations group remove" { return @("engine relations group remove 'Gear Toggle' 'Startup' --text", "engine relations group remove 'Gear Toggle' --all --text") }
        "relations group rename" { return @("engine relations group rename 'Startup' 'Core Startup' --text") }
        "relations item add" { return @("engine relations item add 'Gear Toggle' --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --planes F-16C_50 --text") }
        "relations item remove" { return @("engine relations item remove 'Gear Toggle' --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --text") }
        "relations item enable" { return @("engine relations item enable 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50 --text") }
        "relations item disable" { return @("engine relations item disable 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50 --text") }
        "relations mass delete" { return @("engine relations mass delete --query 'Temporary' --text", "engine relations mass delete --all --text") }
        "relations mass duplicate" { return @("engine relations mass duplicate --relations 'Trim Pitch,Trim Roll' --text") }
        "relations mass merge" { return @("engine relations mass merge 'Merged Trim' --relations 'Trim Pitch,Trim Roll' --text") }
        "relations mass split" { return @("engine relations mass split 'Carrier' --plane 'DCS:F/A-18C_hornet' --query 'Startup' --text") }
        "relations mass add-prefix" { return @("engine relations mass add-prefix 'A10_' --query 'A-10' --text") }
        "relations mass add-postfix" { return @("engine relations mass add-postfix '_ALT' --relations 'Trim Pitch,Trim Roll' --text") }
        "relations mass remove-part" { return @("engine relations mass remove-part 'TEMP_' --query 'TEMP_' --text") }
        "relations mass export" { return @("engine relations mass export 'C:\\JoyPro\\exports\\selection.rl' --query 'Startup' --text", "engine relations mass export 'C:\\JoyPro\\exports\\selection.json' --all --format json --text") }
        "relations export" { return @("engine relations export 'C:\JoyPro\exports\relations.json'") }
        "relations import" { return @("engine relations import 'C:\JoyPro\exports\relations.json'") }
        "relations edit" { return @("engine --editor 'code --wait {file}' relations edit") }
        "binds list" { return @("engine binds list 'Throttle' --text") }
        "binds show" { return @("engine binds show 'Gear Toggle' --text") }
        "binds set" { return @("engine binds set 'Gear Toggle' --device 'VKB Gladiator NXT' --input JOY_BTN1 --text", "engine binds set 'Pitch Axis' --input JOY_Y --inverted true --curvature 0.1,0.2 --deadzone 0.05 --saturation-x 0.9 --saturation-y 0.9 --text") }
        "binds clear" { return @("engine binds clear 'Gear Toggle' --text", "engine binds clear 'Gear Toggle' --input-only --text") }
        "binds modifier list" { return @("engine binds modifier list --text", "engine binds modifier list 'Gear Toggle' --text") }
        "binds modifier add" { return @("engine binds modifier add 'Gear Toggle' LShift RCtrl --text") }
        "binds modifier remove" { return @("engine binds modifier remove 'Gear Toggle' LShift --text", "engine binds modifier remove 'Gear Toggle' --all --text") }
        "binds modifier rename" { return @("engine binds modifier rename 'LAlt' 'LAlt_Hold' --text", "engine binds modifier rename 'LAlt' 'LAlt_Hold' 'Gear Toggle' --text") }
        "binds unbind" { return @("engine binds unbind 'VKB Gladiator NXT::JOY_BTN1' --format dcs --text", "engine binds unbind --input joy0_b0 --format il2 --all --text") }
        "binds tuning models" { return @("engine binds tuning models --text") }
        "ffb models" { return @("engine ffb models --text") }
        "ffb show" { return @("engine ffb show --device 'VPforce Rhino FFB Joystick {...}' --game DCS --plane F-16C_50 --text") }
        "ffb set" { return @("engine ffb set --device 'VPforce Rhino FFB Joystick {...}' --game DCS --plane F-16C_50 --param trimmer=0.8 --param shake=0.3 --text", "engine ffb set --device * --game IL2Korea --param enabled=false --text") }
        "ffb clear" { return @("engine ffb clear --device * --game IL2Korea --text") }
        "ffb telemetry status" { return @("engine ffb telemetry status --text") }
        "ffb telemetry effects" { return @("engine ffb telemetry effects --text") }
        "ffb telemetry config show" { return @("engine ffb telemetry config show --text") }
        "ffb telemetry config set" { return @("engine ffb telemetry config set --param enabled=true --param max-force=0.5 --text", "engine ffb telemetry config set --game DCS --effect buffet --param strength=0.7 --text", "engine ffb telemetry config set --game DCS --plane F-16C_50 --param graph=viper.jpffb.json --text") }
        "ffb telemetry config clear" { return @("engine ffb telemetry config clear --game DCS --plane F-16C_50 --text") }
        "ffb telemetry setup dcs|il2|il2korea|warthunder" { return @("engine ffb telemetry setup dcs --dry-run --text", "engine ffb telemetry setup il2 --text", "engine ffb telemetry setup il2 --remove --text") }
        "binds tuning show" { return @("engine binds tuning show 'Pitch Axis' --game IL2Korea --text") }
        "binds tuning set" { return @("engine binds tuning set 'Pitch Axis' --scope game:IL2Korea --param sensitivity=22 --param deadZone0=3 --text", "engine binds tuning set 'Pitch Axis' --scope plane:DCS:F-16C_50 --curvature 0.2 --deadzone 0.02 --text") }
        "binds tuning clear" { return @("engine binds tuning clear 'Pitch Axis' --scope game:IL2Korea --text") }
        "binds tuning preview" { return @("engine binds tuning preview 'Pitch Axis' --game DCS --plane F-16C_50 --points 11 --text") }
        "binds detent show" { return @("engine binds detent show 'Throttle' --text") }
        "binds detent set" { return @("engine binds detent set 'Throttle' --on --no-ab full --text", "engine binds detent set 'Throttle' --target DCS:F-16C_50=ab:0.775 --target DCS:A-10C=full --text") }
        "binds detent calibrate" { return @("engine binds detent calibrate 'Throttle' --idle 0.02 --detent 0.79 --full none --cutoff none --source manual --text") }
        "binds detent uncalibrate" { return @("engine binds detent uncalibrate 'Throttle' --text") }
        "binds detent gates" { return @("engine binds detent gates --game DCS --text", "engine binds detent gates --game DCS --plane F-14 --text") }
        "binds detent merge" { return @("engine binds detent merge --text", "engine binds detent merge --relations 'ThrottleLeft,ThrottleLeft29,ThrottleLeftNonAB' --into ThrottleLeft --apply --text") }
        "binds export" { return @("engine binds export 'C:\JoyPro\exports\binds.json'") }
        "binds import" { return @("engine binds import 'C:\JoyPro\exports\binds.json'") }
        "binds edit" { return @("engine --editor 'code --wait {file}' binds edit") }
        "sync status" { return @("engine sync status --text", "engine sync status --game DCS") }
        "sync accept" { return @("engine sync accept --game WarThunder") }
        "support bundle" { return @("engine support bundle --list --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' --out 'C:\JoyPro\JoyPro2-support.zip' support bundle --text", "engine support bundle --game MSFS2024 --include-profile false --out 'C:\JoyPro\JoyPro2-support.zip'") }
        "support unpack" { return @("engine support unpack 'C:\Cases\JoyPro2-support-20261026-170635.zip' --to 'C:\Cases\case1' --text") }
        "sync diff" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' sync diff --game DCS --text") }
        "sync apply" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' sync apply --game DCS --choices 'C:\JoyPro\sync-choices.json'") }
        "vjoy status" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy status --text") }
        "vjoy mode" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode DCS virtual --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode DCS native --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode HLLVietnam xbox --text") }
        "vjoy plan" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy plan --text") }
        "vjoy map" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy map --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy map --prune --dry-run --text") }
        "vjoy settings" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy settings --first-device 2 --last-device 3 --text") }
        "vjoy exe" { return @("engine vjoy exe DCS --text", "engine vjoy exe DCS --add 'D:\DCS World\bin-mt\DCS.exe' --text") }
        "vjoy forwarder-config" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy forwarder-config --out 'C:\JoyPro\forwarder.json'") }
        "vjoy configure-vjoy" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy configure-vjoy --dry-run --text") }
        "vjoy apply-hidhide" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy apply-hidhide --dry-run --text") }
        "vjoy restore-hidhide" { return @("engine vjoy restore-hidhide --dry-run --text", "engine vjoy restore-hidhide --text") }
        "vjoy recover" { return @("engine vjoy recover --text") }
        default {
            if ($key -match "^(\S+) (paths|catalog|scan|import|export|refresh-db)$") {
                $area = $Matches[1]
                switch ($Matches[2]) {
                    "paths" { return @("engine " + $area + " paths --text") }
                    "catalog" { return @("engine " + $area + " catalog --text") }
                    "scan" { return @("engine " + $area + " scan --text") }
                    "import" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' " + $area + " import --text") }
                    "export" { return @("engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' " + $area + " export --dry-run --text", "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' " + $area + " export --mode merge-overwrite --text") }
                    "refresh-db" { return @("engine " + $area + " refresh-db --text") }
                }
            }
            return @("engine --help")
        }
    }
}

function Get-GlobalOptionInteractionNotes {
    param(
        [string]$OptionToken
    )

    $notes = New-Object System.Collections.Generic.List[string]
    $option = (To-NonNullString $OptionToken).Trim().ToLowerInvariant()

    switch ($option) {
        "--text" {
            [void]$notes.Add("Without this flag, output defaults to JSON.")
        }
        "--status" {
            [void]$notes.Add("Status lines are written to stderr so stdout payload format remains unchanged.")
            [void]$notes.Add("Useful for GUI progress indicators on long-running commands.")
            [void]$notes.Add("Also prints the machine-readable progress lines of --progress.")
        }
        "--progress" {
            [void]$notes.Add("Format: [engine-progress] <current>/<total> <phase>: <item>; current is the item being worked on (1-based), total is ? when unknown, the phase never contains a colon.")
            [void]$notes.Add("Written by the DB refreshes (DCS per module, aircraft and written file; DCS clean profiles; IL-2; Star Citizen; every game's refresh-db), the DCS import (per file) and the DCS export (per aircraft).")
            [void]$notes.Add("[engine-cancel] available|unavailable|cancelled: with JOYPRO_CANCEL_FILE set, a frontend may create that file to stop the command; the engine stops only before it writes (exit code 3, nothing written).")
            [void]$notes.Add("The progress also goes into the background log (category Progress), with or without this option.")
        }
        "--benchmark" {
            [void]$notes.Add("Benchmark lines are written to stderr with per-phase timing in milliseconds.")
            [void]$notes.Add("Also enables status output.")
        }
        "--load-corrections" {
            [void]$notes.Add("If set to false, correction behavior is disabled for that run.")
        }
        "--auto-add-db-items" {
            [void]$notes.Add("Useful only when --load-corrections true.")
        }
        "--remap-missing-devices" {
            [void]$notes.Add("Useful only when --load-corrections true.")
        }
        "--confirm-device-remap" {
            [void]$notes.Add("Set false for non-interactive runs that should auto-accept remaps.")
        }
        "--correction-mode" {
            [void]$notes.Add("Works with load corrections. strict is conservative, aggressive is more permissive.")
        }
        "--correction-auto-threshold" {
            [void]$notes.Add("Overrides mode-based auto-apply score with explicit numeric threshold.")
            [void]$notes.Add("Applies to relation-item remaps only; title/category refresh for existing IDs is always automatic.")
        }
        "--confirm-corrections" {
            [void]$notes.Add("When false and no explicit auto-threshold is set, medium-confidence corrections are auto-applied.")
            [void]$notes.Add("When --correction-auto-threshold is set, scores below that threshold are skipped unless confirmed interactively.")
        }
        "--correction-report" {
            [void]$notes.Add("Use `.json` for machine-readable output and `.txt`/`.log` for readable summaries.")
        }
    }

    return $notes
}

function Get-GlobalOptionExample {
    param(
        [string]$OptionToken
    )

    $option = (To-NonNullString $OptionToken).Trim().ToLowerInvariant()
    switch ($option) {
        "--profile" { return "engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' summary --text" }
        "--out" { return "engine db games --text --out 'C:\JoyPro\logs\db_games.txt'" }
        "--editor" { return "engine --editor 'code --wait {file}' relations edit" }
        "--text" { return "engine relations list --text" }
        "--status" { return "engine --status true dcs import 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text" }
        "--progress" { return "engine --progress true dcs refresh id --headless-lua true --text" }
        "--benchmark" { return "engine --benchmark true dcs export 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text" }
        "--load-corrections" { return "engine --load-corrections false profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--auto-add-db-items" { return "engine --auto-add-db-items false profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--remap-missing-devices" { return "engine --remap-missing-devices true profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--confirm-device-remap" { return "engine --confirm-device-remap false profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--correction-mode" { return "engine --correction-mode strict profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--correction-auto-threshold" { return "engine --correction-auto-threshold 98 profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--confirm-corrections" { return "engine --confirm-corrections false profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        "--correction-report" { return "engine --correction-report 'C:\JoyPro\logs\load_corrections.json' profile load 'C:\JoyPro\profiles\legacy.pr0file'" }
        default { return "engine --help" }
    }
}

function Build-ExpandedGlobalOptionDocs {
    param(
        [string[]]$GlobalOptions
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $tick = [char]96
    foreach ($optionLine in $GlobalOptions) {
        $trimmed = (To-NonNullString $optionLine).Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($trimmed -notmatch "^(--[a-z0-9-]+)\s*(<[^>]+>)?") {
            continue
        }

        $optionToken = $Matches[1]
        $valueToken = $Matches[2]
        $heading = if ([string]::IsNullOrWhiteSpace($valueToken)) { $optionToken } else { "$optionToken $valueToken" }

        [void]$lines.Add("### " + $tick + $heading + $tick)
        [void]$lines.Add("What it does: $(Get-ArgumentDocLine -Token $optionToken)")
        if (-not [string]::IsNullOrWhiteSpace($valueToken)) {
            [void]$lines.Add("Value format: $(Get-ArgumentDocLine -Token $valueToken)")
        }

        $notes = Get-GlobalOptionInteractionNotes -OptionToken $optionToken
        if ($notes.Count -gt 0) {
            [void]$lines.Add("")
            [void]$lines.Add("How it interacts:")
            foreach ($note in $notes) {
                [void]$lines.Add("- $note")
            }
        }

        [void]$lines.Add("")
        [void]$lines.Add("Example:")
        [void]$lines.Add('```powershell')
        [void]$lines.Add((Get-GlobalOptionExample -OptionToken $optionToken))
        [void]$lines.Add('```')
        [void]$lines.Add("")
    }

    return ($lines -join "`r`n")
}

function Build-ExpandedCommandDocs {
    param(
        [string[]]$Commands
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $tick = [char]96
    foreach ($command in $Commands) {
        $syntax = (To-NonNullString $command).Trim()
        if ([string]::IsNullOrWhiteSpace($syntax)) {
            continue
        }

        $key = Get-CommandKey -CommandLine $syntax
        [void]$lines.Add("### " + $tick + $syntax + $tick)
        [void]$lines.Add("What it does: $(Get-CommandSummaryForDocs -CommandKey $key)")
        [void]$lines.Add("")
        [void]$lines.Add("Arguments and example values:")

        $argTokens = Get-ArgumentTokensForCommandLine -CommandLine $syntax
        if ($argTokens.Count -eq 0) {
            [void]$lines.Add("- No command-specific arguments.")
        }
        else {
            foreach ($token in $argTokens) {
                [void]$lines.Add("- " + $tick + $token + $tick + ": " + (Get-ArgumentDocLine -Token $token))
            }
        }

        $notes = Get-CommandInteractionNotes -CommandKey $key
        if ($notes.Count -gt 0) {
            [void]$lines.Add("")
            [void]$lines.Add("How arguments interact:")
            foreach ($note in $notes) {
                [void]$lines.Add("- $note")
            }
        }

        [void]$lines.Add("")
        [void]$lines.Add("Examples:")
        [void]$lines.Add('```powershell')
        $examples = Get-ExampleCommand -CommandKey $key
        foreach ($example in $examples) {
            [void]$lines.Add($example)
        }
        [void]$lines.Add('```')
        [void]$lines.Add("")
    }

    return ($lines -join "`r`n")
}

function Build-WikiContent {
    param(
        [string[]]$GlobalOptions,
        [string[]]$Commands
    )

    $globalText = ($GlobalOptions | ForEach-Object { $_ }) -join "`r`n"
    $commandsText = ($Commands | ForEach-Object { $_ }) -join "`r`n"
    $expandedGlobalOptions = Build-ExpandedGlobalOptionDocs -GlobalOptions $GlobalOptions
    $expandedCommandDocs = Build-ExpandedCommandDocs -Commands $Commands

    $template = @'
# JoyPro Engine CLI Wiki

This guide explains what JoyPro Engine is, why you would use it, and how to operate it for DCS, IL2, and Star Citizen control management.

`engine` is a command-line tool for building and maintaining one consistent control setup across games, aircraft, and joystick devices.

This page includes:
- A practical explanation of what problem JoyPro solves.
- A mental model of how the tool works internally.
- Plain command descriptions and workflows.
- Auto-generated command surface from current `engine --help`.

- Generated from current local `engine --help` output.
- Source of truth for command list: `engine --help`
- Regenerate: `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1`

## What The Program Does

DCS, IL2, and Star Citizen control files are powerful but hard to maintain long-term because:
- Controls are spread across many files per aircraft and per device.
- Device names and GUIDs can change when hardware or USB ports change.
- Rebinding and cleanup can be repetitive when you own multiple aircraft.
- Migrating setups between PCs or installs is error-prone.

JoyPro Engine solves this by giving you a single master mapping model and import/export tools around it:
- Import game bindings into one central file (`.pr0file`).
- Inspect and edit mappings structurally (relations, binds, modifiers, groups).
- Clean up or unbind in bulk using CLI operations.
- Export back to game-compatible formats when you are ready.

In short: JoyPro is useful when you want consistent controls over time, not one-off manual edits in game menus.

## Super Simple Explanation

Imagine each game control is a small toy block.

- A `node` is one toy block from one game and one plane.
- A `relation` is a box where you put toy blocks that mean the same thing (for example "Wheel Brake").
- A `bind` is a sticker on that box that says "press this joystick button".
- A `.pr0file` is your big toy shelf where all boxes and stickers are saved.

JoyPro helps you:
1. Collect toy blocks from games (import).
2. Put matching blocks into boxes (relations).
3. Put joystick stickers on boxes (binds/modifiers).
4. Send the result back into games (export).

## Core Concepts

- `Profile (.pr0file)`: Your master control library. It stores all logical actions and hardware assignments in one file.
- `Relation`: One logical action bucket that can group multiple equivalent game commands (`nodes`) across different planes and even different games.
- `Bind`: The physical input assignment attached to a relation (device + input token + optional modifiers). If a relation has a physical input attached, you effectively have a bind for that relation.
- `Node`: One concrete in-game command target. JoyPro models each game input command as a node, and a relation can contain multiple nodes.
- `Groups`: Simple tags for organizing relations (`Startup`, `Combat`, `Navigation`, etc.).
- `DB`: JoyPro's local reference data (games, aircraft/modules, metadata used by checks/listing).

How the tool works in principle:
1. Read game/profile data and model each mappable in-game command as a node.
2. Group nodes that mean the same action into one relation, including across multiple planes and games.
3. Attach hardware inputs to relations, creating/maintaining binds for those actions.
4. Manage and organize relations/binds (groups, enable/disable, add/remove/unbind) and save all of it in one `.pr0file`.
5. Export back to game-specific formats when you want to apply the setup in DCS/IL2.

## Quick Start (From Zero)

1. Verify engine and view command help:
```powershell
engine --help
engine summary
```

2. Check what devices are connected and what devices are already referenced:
```powershell
engine devices list --connected --text
engine devices list --profile-devices --text
engine devices inputs --text
```

3. Import controls from DCS or IL2:
```powershell
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --text
engine il2 import "D:\IL-2 Sturmovik Great Battles" --text
```

4. Review what was created:
```powershell
engine relations list --text
engine binds list --text
```

5. Save your master control library:
```powershell
engine profile save "C:\profiles\my_setup.pr0file"
```

## Full Command Surface (Auto-Generated)

### Global Options
```text
__GLOBAL_OPTIONS__
```

### Commands
```text
__COMMANDS__
```

## Global Options (Detailed)

__GLOBAL_OPTIONS_DETAILED__

## Command Reference (Every Command)

__COMMANDS_DETAILED__

## How Output Works

- Default output is JSON (script/automation friendly).
- Add `--text` for human-readable terminal output.

Use `--text` when you are manually checking results.
Use default JSON when piping into tools or scripts.

## Load Correction Mechanics

JoyPro Engine can apply load-time correction mechanics while loading profiles.

Default behavior:
- `--load-corrections true` (default)
- `--auto-add-db-items true` (default)
- `--remap-missing-devices true` (default)
- `--confirm-device-remap true` (default)
- `--correction-mode balanced` (default)
- `--correction-auto-threshold` unset (mode-based default)
- `--confirm-corrections true` (default)

What this does:
- For active planes only, checks each relation node against DB data during profile load.
- If the same node ID still exists for that plane, only metadata is refreshed (title/category) and no remap scoring is used.
- If the node ID is missing for that active plane, tries scored replacement candidates.
- Repairs modifier reformer strings from known modifier key combos.
- Normalizes joystick naming and cleans stale joystick metadata maps.
- Detects devices referenced by binds/modifiers that are not currently connected and suggests closest replacements.
- Uses confidence scoring for ID/name drift recovery and records every correction decision.
- Uses plane-family alias fallback only when the original plane no longer exists in current DB data.

How to disable globally for one run:
```powershell
engine --load-corrections false summary
```

How to keep corrections but auto-accept device remaps (non-interactive):
```powershell
engine --confirm-device-remap false profile load "C:\profiles\my_setup.pr0file"
```

How to change correction strictness:
```powershell
engine --correction-mode strict summary
engine --correction-mode balanced summary
engine --correction-mode aggressive summary
```

How to set explicit auto-apply threshold:
```powershell
engine --correction-auto-threshold 98 profile load "C:\profiles\my_setup.pr0file"
```

How to write a correction report:
```powershell
engine --correction-report "C:\logs\joypro_corrections.json" summary
engine --correction-report "C:\logs\joypro_corrections.txt" summary
```

## Command Families Explained

### `summary` / `profile`

Use these to open/save your master mapping file (`.pr0file`).

- `engine summary`
  Prints quick counts (relations, binds, modifiers).
- `engine profile load <path.pr0file>`
  Loads an existing master mapping file into memory.
- `engine profile save <path.pr0file>`
  Writes current in-memory state to a `.pr0file`.
- `engine profile save-last`
  Saves to JoyPro's "last used" profile location.

### `devices`

Use this to compare physically connected hardware and what your profile currently references.

- `engine devices list --connected --text`
  Shows currently connected joystick devices.
- `engine devices list --profile-devices --text`
  Shows devices referenced by loaded profile.

### `db`

Use this to inspect JoyPro's bundled DB content.

- `engine db summary`
  DCS clean-profile health summary.
- `engine db list [filter]`
  DCS module list (optionally filtered).
- `engine db check`
  Validation check for required DCS clean-profile files.
- `engine db games [filter]`
  Lists available canonical game IDs from DB (for example `DCS`, `IL2Game`, `StarCitizen`).
- `engine db planes [filter] [--game <name>]`
  Lists available plane/module names from DB, optionally narrowed by game.

### `dcs`

Use these commands for DCS Saved Games input data.

- `engine dcs scan ...`
  Parse and preview import result without applying profile changes.
- `engine dcs import ...`
  Parse and apply to the in-memory master mapping.
- `engine dcs export ...`
  Write output files back in DCS-compatible format (`.diff.lua` + `.jp` backup).
- `engine dcs defaults cut ...`
  Back up joystick defaults and move stick-specific game-folder defaults into backup storage.
- `engine dcs defaults restore ...`
  Restore joystick defaults from backup storage back into DCS game-folder aircraft input folders.

### `il2`

Use these commands for IL2 input data.

- `engine il2 scan ...`
  Parse and preview IL2 input candidates.
- `engine il2 import ...`
  Apply parsed controls into the in-memory master mapping.
- `engine il2 export ...`
  Export profile controls into IL2-compatible outputs (mode-dependent).

### `relations`

Use these commands to manage logical actions and where they map in game data.

- `list` / `show`
  Inspect relations.
- `add`
  Create relation from nodes or from other relations (type can be inferred or set explicitly).
- `group list/add/remove/rename`
  Organize relations by tags.
- `item add/remove/enable/disable`
  Add or control specific node targets.
- `export/import/edit`
  Roundtrip relations as JSON or edit externally.

### `binds`

Use these commands to manage hardware mappings and modifiers.

- `list` / `show`
  Inspect current binds.
- `set` / `clear`
  Create, replace, tune, or remove one relation bind directly.
- `modifier list/add/remove/rename`
  Add or remove key modifiers for a bind.
- `unbind`
  Remove bind links by input token (DCS or IL2 format supported).
- `export/import/edit`
  Roundtrip binds as JSON or edit externally.

## Filter Rules And Import Behavior

- DCS/IL2 import flags are valid for `scan` and `import`, not `export`.
- If you do not pass `--device` or `--devices`, all devices are considered.
- `--ignore-device` and `--ignore-devices` exclude devices from candidates.
- If include and ignore match the same device, ignore wins.
- `engine db games` gives canonical game names for `engine db planes --game`.

## Device Name Resolution (`--confirm`)

When a device name does not exactly match, engine can suggest the closest match.

- Default (`--confirm true`, or omitted):
  Ask for confirmation (`y/N`) before applying the closest match.
- `--confirm false`:
  Auto-accept closest match without prompting.

Match preference order:
1. Devices already in profile.
2. Currently connected devices.
3. Game data files (`DCS Config\Input` or `IL2 data\input\devices.txt`).

## DCS Workflows And Examples

### Basic scan/import
```powershell
# Preview what engine would import
engine dcs scan "C:\Users\<you>\Saved Games\DCS.openbeta" --text

# Import everything found
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --text
```

### Plane filtering
```powershell
# Import A-10C_2 only, including defaults
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --plane "A-10C_2" --include-defaults --text

# Import A-10C_2 and F/A-18C including defaults
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --plane "A-10C_2" --plane "FA-18C_hornet" --include-defaults --text
```

### Device include/ignore filtering
```powershell
# Include only these devices
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "VKB" --device "LEFT VPC Throttle" --text

# Import all devices except these
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --ignore-devices "Keyboard,VKB" --text
```

### Merge into existing profile
```powershell
engine --profile "C:\profiles\base.pr0file" dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --merge --save-profile "C:\profiles\merged.pr0file" --text
```

### Axis option toggles
```powershell
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" `
  --import-inverted --import-slider --import-curvature --import-deadzone --import-saturation-x --import-saturation-y --text
```

### Export modes and cleanup
```powershell
# Clean export (replace each exported target file with JoyPro output only)
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode clean --text

# Merge and keep existing when IDs collide
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge --text

# Merge and overwrite existing when IDs collide (default)
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge-overwrite --text

# Merge and allow multiple binds on collisions
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode add --text

# Preserve keyboard defaults and nuke untouched joystick files
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge-overwrite --keep-keyboard-defaults true --nuke-untouched-devices true --text
```

## IL2 Workflows And Examples

```powershell
# Scan all IL2 devices
engine il2 scan "D:\IL-2 Sturmovik Great Battles" --text

# Import only selected devices
engine il2 import "D:\IL-2 Sturmovik Great Battles" --device "VKB" --device "LEFT VPC Throttle" --text

# Import everything except ignored devices
engine il2 import "D:\IL-2 Sturmovik Great Battles" --ignore-devices "VKB,LEFT VPC Throttle" --text

# Save to a profile
engine il2 import "D:\IL-2 Sturmovik Great Battles" --save-profile "C:\profiles\il2_import.pr0file" --text
```

## Device Suggestion Examples

```powershell
# Ask before accepting closest match (default)
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "LEFT VPC Throtle" --text

# Auto-accept closest match
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "LEFT VPC Throtle" --confirm false --text

# Same behavior also works for ignored-device filters
engine il2 scan "D:\IL-2 Sturmovik Great Battles" --ignore-device "Tst Stick" --confirm false --text
```

## Relations And Binds Examples

```powershell
# List and inspect
engine relations list --text
engine relations show "My Relation" --text
engine binds list --text
engine binds show "My Relation" --text

# Add relation + node (type inferred from node ID unless you override)
engine relations add "Gear Toggle" --node DCS:d3007pnilu3007cd16vd1vpnilvu0 --text

# Manage groups
engine relations group list --text
engine relations group add "Gear Toggle" "Startup" "Common" --text
engine relations group remove "Gear Toggle" "Startup" --text
engine relations group rename "Startup" "Core Startup" --text

# Manage modifiers
engine binds modifier list --text
engine binds modifier add "Gear Toggle" LShift --text
engine binds modifier rename "LShift" "LShift_Hold" --text
engine binds modifier remove "Gear Toggle" LShift_Hold --text

# Set/replace bind and tune axis values
engine binds set "Gear Toggle" --device "VKB Gladiator NXT" --input JOY_BTN1 --text
engine binds set "Pitch Axis" --input JOY_Y --inverted true --curvature 0.1,0.2 --deadzone 0.05 --saturation-x 0.9 --saturation-y 0.9 --text
engine binds clear "Gear Toggle" --input-only --text
engine binds clear "Gear Toggle" --text

# Manage node items
engine relations item add "Gear Toggle" --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet --text
engine relations item disable "Gear Toggle" --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --planes F-16C_50 --text

# Unbind by DCS or IL2 style token
engine binds unbind --input JOY_BTN1 --format dcs --all --text
engine binds unbind --input joy0_b0 --format il2 --all --text
```

## DB And Devices Examples

```powershell
engine db summary --text
engine db games --text
engine db planes --text
engine db planes --game DCS "A-10" --text
engine db planes --game IL2Game --text
engine db check --text
engine devices list --connected --text
engine devices list --profile-devices --text
```

## Common Beginner Path

Recommended first workflow:
1. `engine devices list --connected --text`
2. `engine dcs scan "<saved-games-path>" --text` or `engine il2 scan "<il2-root>" --text`
3. Import with simple filters (`--plane`, `--device`, `--ignore-device`) if needed.
4. `engine relations list --text` and `engine binds list --text` to verify.
5. Save profile with `engine profile save <file.pr0file>`.
6. Export to game folder once you are happy.

## Keeping This Wiki Up To Date

1. Regenerate after command/parser/help changes:
   `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1`
2. Validate in CI/local:
   `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1 -CheckOnly`
3. Optional full regression:
   `powershell -ExecutionPolicy Bypass -File .\tests\engine_smoke_test.ps1 -Configuration Debug -Platform x64`
'@

    $content = $template.Replace("__GLOBAL_OPTIONS__", $globalText)
    $content = $content.Replace("__COMMANDS__", $commandsText)
    $content = $content.Replace("__GLOBAL_OPTIONS_DETAILED__", $expandedGlobalOptions)
    $content = $content.Replace("__COMMANDS_DETAILED__", $expandedCommandDocs)
    return $content
}

$engineDir = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $engineDir "docs\ENGINE_CLI_WIKI.md"
}

$engineExe = Resolve-EngineExe -EngineDir $engineDir -Configuration $Configuration -Platform $Platform -EngineExePath $EngineExePath
$helpOutput = & $engineExe --help 2>&1
Assert-True ($LASTEXITCODE -eq 0) "Failed to run engine --help for wiki generation."

$helpLines = @()
if ($helpOutput -is [System.Array]) {
    $helpLines = $helpOutput | ForEach-Object { $_.ToString() }
} elseif ($null -ne $helpOutput) {
    $helpLines = @($helpOutput.ToString())
}

$sections = Extract-HelpSections -HelpLines $helpLines
$content = Build-WikiContent -GlobalOptions $sections.GlobalOptions -Commands $sections.Commands

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
}

if ($CheckOnly) {
    if (-not (Test-Path $OutputPath)) {
        throw "Wiki file missing: $OutputPath. Run update script without -CheckOnly."
    }

    $existing = Get-Content -Raw $OutputPath
    $existingNorm = ($existing -replace "`r`n", "`n").TrimEnd("`n")
    $contentNorm = ($content -replace "`r`n", "`n").TrimEnd("`n")
    if ($existingNorm -ne $contentNorm) {
        throw "Wiki is out of date. Regenerate with tools/update_cli_wiki.ps1."
    }

    Write-Host "Wiki is up to date: $OutputPath"
    exit 0
}

Set-Content -Path $OutputPath -Value $content -Encoding UTF8
Write-Host "Updated wiki: $OutputPath"

