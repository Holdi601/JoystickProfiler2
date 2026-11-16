---
title: Download and install
status: full
order: 2
summary: Get JoyPro 2 from GitHub, unzip it, start it. What it needs, where it keeps its data, and how updates work.
---
JoyPro 2 is free and open source (GPL-3.0). There is no account, no sign-up and no telemetry.

:::video install "Download, unzip, start"
:::

:::layer beginner
## Three steps

1. Open the [releases page on GitHub](https://github.com/Holdi601/JoystickProfiler2/releases/latest) and download
   `JoyPro2-<version>-win-x64.zip` (under **Assets**).
2. Right-click the zip, **Extract All...**, and choose a folder you can write to, for example `C:\JoyPro2` or a folder
   in your Documents. (Not `C:\Program Files`: JoyPro updates itself in its own folder.)
3. Open the folder and double-click **JoyPro.Gui** (the program with the joystick icon; with file extensions shown it
   reads **JoyPro.Gui.exe**).

Windows says **Windows protected your PC**? Click **More info**, then **Run anyway**. Windows asks this once for a
program downloaded from the internet that it does not know yet.

On the first start JoyPro looks for your games and shows them in a window: see
[[page:getting-started/first-start.html|First start]]. New to JoyPro? Press **Done, and set everything up for me**
there, and follow [[page:getting-started/quick-start.html|Quick start: make my stick work]].

Then a welcome asks what you want to do and starts the tour that fits you. **JoyPro 2 - Tour** in the same folder opens
that welcome again any time. Want big text and short sentences? Tick **Easy words: big text, short sentences** in the
welcome ([[page:how-do-i/easy-words-and-help.html|Easy words, tours and F1]]).

### What you need

- Windows 10 or 11, 64 bit.
- The .NET Framework 4.8 for JoyPro's engine. Windows 10 and 11 already have it; if it is missing, JoyPro offers the
  installer from Microsoft.
- Nothing else: the zip holds everything (the program does not need .NET 9 installed separately).

### Updates

JoyPro checks GitHub for a newer version when it starts (Settings: **Check for updates at start**). A newer version
opens a window with what changed and **Update now** / **Later** / **Skip this version**. Your profiles and settings
stay.
:::

:::layer advanced
## Where JoyPro keeps things

Your profiles are in your Documents folder, in `JoyPro2`. Problem reports go there too. An update replaces only the
program folder: your profiles, settings, command lists, backups and history stay. The full list of folders is under
Technical internals.

### Optional components

Settings > **Components** installs, with one click each and your consent: **vJoy** (virtual joystick), **HidHide**
(hides physical devices from a game) and **ViGEmBus** (virtual Xbox pad), for the [[gui:vjoy|Virtual Joystick]] mode.
Settings > **Local AI** can download an optional AI model for the assistant; everything works without it.

### Going online

JoyPro goes online only for: the update check (GitHub), the makers' product photos of your devices (only after you
said yes per device), game covers missing on your PC (can be switched off), the optional AI model, the optional
components and a missing .NET Framework installer. These sites see your IP address. Details:
[[doc:privacy|Privacy]].
:::

:::layer technical
## All of JoyPro's folders

| What | Where |
|---|---|
| The program | the folder you unzipped (with `engine\engine.exe`, `vjoy\`, `tray\`, the data JoyPro ships in `DB\`) |
| Your profiles | `Documents\JoyPro2` (or wherever you save them) |
| Settings | `%APPDATA%\JoyPro2` (`ui-settings.json`, `export-settings.json`, `hud.json` ...) |
| Game command lists built on your PC | `%LOCALAPPDATA%\JoyPro2\GameData` |
| Game file backups | `%LOCALAPPDATA%\JoyPro2\GameBackups` (the newest 30 per game) |
| Profile history (undo across restarts) | `%LOCALAPPDATA%\JoyPro2\ProfileHistory` |
| Logs | `%LOCALAPPDATA%\JoyPro2\Logs` (delete themselves after a few days) |
| Problem reports | `Documents\JoyPro2\Problem Reports` |

## Inside the package

- `JoyPro.Gui.exe`: the Avalonia app (.NET 9, self-contained).
- `JoyPro 2 - Tour.cmd`: starts `JoyPro.Gui.exe --welcome`, the welcome with its tours (hands it to a running JoyPro).
- `engine\engine.exe`: the engine (.NET Framework 4.8). The GUI runs it for every action; you can also run it in a
  console ([[page:cli/index.html|engine command line]]).
- `vjoy\JoyPro.VJoy.exe` (virtual joystick forwarder), `JoyPro.Ffb.exe` (telemetry force feedback),
  `tray\JoyPro.Tray.exe` (watcher while the GUI is closed).
- The update downloads the release zip, checks it against the published `.sha256` file and installs it.
- Build from source: `dotnet build gui/JoyPro.Gui/JoyPro.Gui.csproj` (also builds the engine; needs the .NET Framework
  4.8 build tools), see the repository's README.
:::
