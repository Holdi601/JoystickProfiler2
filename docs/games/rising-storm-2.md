# Rising Storm 2: Vietnam (Tripwire / Antimatter, Unreal Engine 3)

Status: **not installed here; not integrated.** Best route: **virtual Xbox pad** (ViGEm) plus the game's own gamepad
binds.

- **Input:** keyboard/mouse and an XInput pad (`XboxTypeS_*` keys). No DirectInput joystick support (WEB, Steam
  threads 2017-2019).
- **Helicopters:**
  - cyclic = `aTurn` / `aLookup`, yaw = `aStrafe`, collective = `aBaseY`;
  - since patch 1.09 an absolute collective: `Axis aHeloCollective` plus `bAbsoluteCollective=true` in `ROGame.ini`
    `[ROPlayerController]`.
- **Binds:** `Documents\My Games\Rising Storm 2\ROGame\Config\ROInput.ini`, section `[ROGame.ROPlayerInput]`. Lines
  look like `Bindings=(Name="XboxTypeS_LeftY",Command="Axis aBaseY Speed=1.0 DeadZone=0.4",Control=False,...)`.
  `bEnableXBoxController=true`.
- **Anti-cheat:** Easy Anti-Cheat (WEB).

**JoyPro plan** (when the game is on the PC):
- an integration like Hell Let Loose: Vietnam: catalog rows `Pad_*` with the Unreal pad key;
- the export writes the `XboxTypeS_*` binds of the helicopter commands into ROInput.ini (plain text, UE3 `Bindings=`);
- `engine vjoy mode RS2 xbox` puts the HOTAS on those pad controls.

Needs a real ROInput.ini (the UE3 trigger axis names are unconfirmed).
