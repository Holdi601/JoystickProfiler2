# Tom Clancy's H.A.W.X (2009) and H.A.W.X 2 (2010), Ubisoft

Status: **H.A.W.X 2 is installed here (Steam 48180, 2026-10-11) but was not started: it needs Ubisoft Connect and an
Ubisoft account (see below). Not integrated.** Best route: **vJoy, one device.**

## H.A.W.X 2 on this PC (read-only, 2026-10-11)
- **Ubisoft Connect is required** [OBS, SRC]: Steam's install script (`48180_install.vdf`, "Run Process" >
  `gamelauncher`) runs `redist\GameLauncher\UbisoftGameLauncherInstaller.exe` on the first start, and `HAWX2.exe`
  talks to the Ubisoft launcher ("Orbit": `ubiorbitapi_r2.dll`, `MgOrbitdllGetLoginDetails`, "Orbit session created.
  Start Ubisoft Launcher"). Ubisoft Connect is not installed on this PC, so the first start means an installer with its
  licence terms and an Ubisoft account login. **The user has to do that once** (install Ubisoft Connect, log in, start
  H.A.W.X 2 once and close it). JoyPro's test kit never accepts terms or logs in, so nothing was started.
- **Files** [OBS]: `<install>\Data_PC\Settings\settings.ini` (written by the installer's auto-detection:
  `[Input] MouseSensitivity = 0.5`, `DeadZone = 0.2`; `[Video] windowed = false` ...) and `setting.lua`, which
  registers the settings the game stores: `Input.InvertYAxis`, `Input.InvertYaw`, `Input.InvertThrottle`,
  `Input.InvertCamera` (int), `Input.MouseSensitivity`, `Input.DeadZone` (float), `Input.EnableExpertControls`,
  `Camera.Mode`.
- **Binds** [SRC]: the player profile goes through the Ubisoft launcher's save games (`MgOrbitdllGetSavegameWriter`,
  "Current player's profiles in Ubi Launcher", `profile_list.bin`, `Saved Games\...\Profiles\`). The joystick shows as
  "Joystick - <product name>" (one device). No text file holds the binds, so JoyPro cannot write them: bind the vJoy
  device in the game's controls menu.
- **Still to check after the user's first start** (live, menus only): that the controls menu lists the vJoy device
  ("Joystick - vJoy Device"), where the profile file lands, and whether `settings.ini` keeps a changed `DeadZone`.

- **Input:** DirectInput sticks + XInput pads [INF]. **Only one device**: "will not recognize more than one", so with
  an X-55 or a Thrustmaster set you get stick OR throttle, never both (WEB, Steam threads, both games).
- **Binds:**
  - H.A.W.X: `%USERPROFILE%\Saved Games\Tom Clancy's H.A.W.X\Profiles\`. The settings are an ini; the binds are in a
    binary profile file without an extension that nobody has decoded (WEB).
  - H.A.W.X 2: `%USERPROFILE%\Saved Games\Tom Clancy's H.A.W.X 2\Profiles` and `<install>\Data_PC\Settings\settings.ini`
    (`[Input] DeadZone`, `MouseSensitivity`).
- **DRM:** Ubisoft Connect (H.A.W.X 2 launched with always-online DRM). No anti-cheat.

**JoyPro:**
- **Virtual joystick:** the whole HOTAS on one vJoy device, real devices hidden by HidHide. Bind it in the game.
- Nothing to write (binary profile). JoyPro could at most set the dead zone in H.A.W.X 2's settings.ini.
