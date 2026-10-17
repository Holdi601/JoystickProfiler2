# STAR WARS: Squadrons (EA / Motive, Frostbite, 2020)

Status: **not installed here; not integrated.** Best route: **vJoy, one device** (virtual joystick + HidHide).

- **Input:** DirectInput sticks plus XInput pads. Axes appear as `DX_X_AXIS` ... (WEB).
- **Devices:** up to about 3-4, listed by name: `GstInput.JoystickDevice1 Throttle - HOTAS Warthog`,
  `GstInput.JoystickDevice2 ...`. In practice the game:
  - skips some devices (TWCS, T.16000M, CH, MFG reported);
  - mixes up Z axes between devices;
  - loses throttle binds after a restart;
  - reads devices that are not selected (input bleed).
  (WEB, EA forum and Steam threads.)
- **Binds:**
  - Steam: `Documents\STAR WARS Squadrons Steam\settings\ProfileOptions_profile_synced`;
  - EA app: `Documents\STAR WARS Squadrons\settings\`.
  - Text format of the Battlefield family, plus `deviceid`, `identifier`, `altbutton` and `modifier`:
    `GstKeyBinding.IncomStarshipInputConcepts.ConceptYaw.6.axis 8`, `....deviceid 0` (0 = JoystickDevice1).
  - Half axes are seen both as axis codes (8/10 = Axis1XPos/Neg) and as button codes (46/47 = Z+/Z−).
  - The "none" button code moved from 86 to 174 between patches.
- **Cloud:** the `_synced` file syncs with EA. After a hand edit the game asked whether to upload the local version.
- **Anti-cheat:** Easy Anti-Cheat (community reports).
- **Deadzone:** a fixed deadzone of about 15-20 % on DX X/Y. Users cancel it with a curve.

**Why vJoy:** one vJoy device avoids the device list, the Z mix-up and the bleed (write `JoystickDevice1 = vJoy Device`,
`deviceid 0`).

**To integrate later:**
- The `BattlefieldProfile` writer handles the lines.
- The numbering needs a survey like Battlefield's (`BattlefieldJoystickSurvey.ps1` with Squadrons concepts).
- Write only while the game is closed, and check after one launch that EA's cloud copy did not win.
