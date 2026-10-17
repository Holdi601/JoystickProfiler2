# Kerbal Space Program 1 and 2

Status: **neither is installed here; not integrated.**

## KSP 1 (Unity 2019.4, old Input Manager): native candidate
- **Devices:** several at once. An axis has a PRIMARY and a SECONDARY slot. Unity numbers joysticks 1-8, with up to 20
  buttons and about 20 axes each (WEB, Unity docs).
- **Binds:** `<KSP install>\settings.cfg`, ConfigNode text. KSP writes it on exit.
  ```
  INPUT_DEVICES
  {
      Thrustmaster T.16000M = 0          // Unity joystick name = enumeration index
  }
  AXIS_PITCH
  {
      PRIMARY { name = Thrustmaster T.16000M  axis = 1  inv = False  sensitivity = 1  deadzone = 0.05  scale = 1  group = 0  modeMask = -1 }
      SECONDARY { ... }
  }
  PITCH_DOWN { primary = W  secondary = Joystick1Button6  group = 0  modeMask = 5  modeMaskSec = -1 }
  ```
- **Weak points:**
  - buttons use Unity KeyCodes with the device index baked in (`JoystickNButtonM`), so they move when the device order
    changes;
  - binds are lost when a device was off at launch.
- No anti-cheat.
- **JoyPro plan:**
  - native integration: axes by device name + Unity axis index, buttons by `JoystickNButtonM`;
  - `INPUT_DEVICES` gives the index;
  - virtual mode (one vJoy device) is the fix for the index shifting.
- **Open before writing:** Unity's axis numbering on Windows (which index is Rz / slider). That needs one real
  settings.cfg with known binds.
- AFBW Revived (mod, SDL, XML config) is an alternative the user can choose.

## KSP 2 (Unity Input System): not worth it
- Joystick support is missing or broken: "only a single two-axis stick" (WEB). Development stopped in 2024.
- **Binds:** `%USERPROFILE%\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Global\InputBindings.json`
  (Input System overrides such as `<Joystick>/stick/left`).
- **Route:** virtual Xbox pad (ViGEm), or the Inputbinder BepInEx mod. JoyPro does not integrate it.
