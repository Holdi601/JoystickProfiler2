# Flyout (Stonext Games, Unity, Early Access)

Status: **not installed here; not integrated.** Best route: **vJoy, one device.**

- **Input:** any joystick is accepted (most likely Unity's old Input Manager, reading the axes of all joysticks
  merged). Stick and throttle are not told apart: "the game does not know that my stick and throttle are two separate
  devices". The community rule is "only one game controller connected" (WEB, Steam threads 2023/2024).
- Gear and trim became bindable in 2026 (QOL update, 2026-07-31).
- **Binds:** not found on the web. User data lives in `%USERPROFILE%\AppData\LocalLow\Stonext Games\Flyout\`. The
  binds are probably Unity PlayerPrefs (registry `HKCU\Software\Stonext Games\Flyout`) or a file there [INF].
- No anti-cheat.

**JoyPro:**
- The virtual joystick (one vJoy device, HidHide) gives the game the single device it can handle. The binds are set
  in the game.
- An integration needs a look at the real bind storage after one start of the game (read-only).
