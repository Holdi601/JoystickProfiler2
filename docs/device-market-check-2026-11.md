# Device market check, November 2026 (before 2.7)

The release rule: before every release, devices that are new on the market go into `DB/Devices` with their USB id
(VID:PID), the Windows HID name, the button/axis/hat layout and photos of the exact product
([device-reference.md](device-reference.md), "Growing the catalog"). This check ran on 2026-11-14 and covers products
released or announced from about June 2025 to November 2026.

## What was checked

- **Vendors:** WinWing/WinCtrl, VKB, Virpil, Thrustmaster (incl. TCA), Logitech/Saitek, Honeycomb, MOZA,
  Turtle Beach VelocityOne, VPforce, TEK Creations, Octavi, Brunner, RealSimulator, Slaw, MFG, Virtual Fly, Fulcrum,
  Yawman, HORI, PXN, Simucube, Hoowalt, WINGFLEX, Meridian GMT.
- **Where:** the makers' shops and manuals, plus simFlight, FSElite, msfsaddons and fsnews for release dates.
- **USB ids, only from sources that show a real device:**
  - linux-hardware.org probes, which carry the USB string;
  - the Linux kernel's `hid-winwing.c`;
  - MobiFlight's device definitions (`MobiFlightConnector/Joysticks/*.joystick.json`), which give the USB id, the
    instance name and the input numbers with labels;
  - Windows HID dumps in GitHub issues (joystick-mapper #24 and #25);
  - game input files with DirectInput product GUIDs (Ace Combat 7 `Input.ini`);
  - hardware-tested tools: msfs-linux-companion, WinctrlConfig, winctrl-xplane-plugin.
  - Not used: the SDL gamecontrollerdb and usb.ids, which list no WinWing devices, and AI-written device tables such
    as OpenFlight's "estimated" ids and HIDMaestro.
- **Photos:** only the maker's own pictures of the exact product. Each one was looked at before it was recorded, and
  its SHA-256 was checked against a fresh download.

## Added models (10)

| Model | Kind | USB id | HID name | Inputs | Photos |
|---|---|---|---|---|---|
| `wingflex-a320-fcu-cube` | panel | A316:C787 | FCU Cube | 27 buttons, 2 axes (MobiFlight numbers + labels) | 2 |
| `wingflex-a320-efis-cube` | panel | A516:C987 | EFIS Cube | 34 buttons | 2 |
| `wingflex-a320-rmp-combo-cube` | panel | A616:5750 | RMP Cube | 29 buttons | 2 |
| `wingflex-a320-overhead-panel` | panel | A716:CB87 | OVHD Cube | 159 buttons, 1 axis | 1 (medium: the shop sells it as "A320 OVERHEAD PANEL") |
| `wingflex-dap500` | panel | C687:B216 | WINGFLEX DAP500 | 20 buttons (light sensor axis left out) | 2 |
| `wingflex-gmp` | panel | C687:B217 | WINGFLEX GMP | 22 buttons, gear lever as buttons 23/24 + X axis | 1 |
| `moza-ma3f-airbus-sidestick` | grip on AB9/AB6 | (through the base, 346E:1000) | – | hat, takeover button, trigger, no numbers (MOZA Cockpit numbering is not published) | 4 |
| `hoowalt-d301-collective` | collective | not found | not found | collective + twist throttle, 2 buttons, 2 toggles, no numbers | 3 |
| `iidb-a32x-tiller` | panel | 99DB:0102 (CPT), 99DB:0103 (FO) | IIDB Steering Tiller A32x CPT / FO | PEDAL DISC = button 1; the tiller axis letter is unknown | none |

The new vendor file is `DB/Devices/wingflex.json`. Its hosts are wingflex.com and the shop's own Shopify folder,
`cdn.shopify.com/s/files/1/0802/6266/0351/`. The Hoowalt and MOZA photos use the shops' own `/cdn/shop/` addresses
(same bytes as the CDN). Each photo has its hash, size and licence note recorded.

`AB9 base` identification now offers the MA3F next to the MH16, MHG and MA3X (`identify --vidpid 346E:1000`).

## USB ids and names added to existing models

**WinWing / WinCtrl**

| Model | Added | Source |
|---|---|---|
| F/A-18 Combat Ready Panel | 4098:BE05 | linux-hardware.org |
| Super Taurus start-up panel | 4098:BE03, name "WINWING F18 STARTUP PANEL" | linux-hardware.org |
| F-16 ICP | 4098:BF06, name "ICP" | linux-hardware.org |
| MFD1 | 4098:BEE0 (C). L/R order verified: BEE1 = L, BEE2 = R | linux-hardware.org |
| Super Libra base | 4098:BE11 "JOYSTICK BASE1 + F18 GRIP" | linux-hardware.org |
| Super Taurus base | 4098:BE22 "WINWING THROTTLE BASE1 + F18 HANDLE" | linux-hardware.org |
| Orion 2 base + ViperAce grip | 4098:BE48 "JOYSTICK BASE2 + JGRIP-F16" | linux-hardware.org |
| Orion 2 throttle + StrikeAce F-15E grip | 4098:BD65 (TGRIP-15E) | kernel `hid-winwing.c` |
| 32 NWS | 4098:B962 = R; names "WINCTRL 32 NWS L/R" | MobiFlight |
| URSA MINOR Combat Throttle | name "WINCTRL URSA MINOR Combat Throttle", so a unit without the EX module is found by name | – |

The Windows names of the Airbus/Boeing panels come from MobiFlight: FCU/EFIS, PFP 3N/4/7, MCDU, PDC 3M/3N, ECAM, AGP,
TCAS, RMP, PTO2 and PAP3.

The URSA MINOR stick and throttle ids (BC29 = L, BC2A = R, Combat Throttle B970) were already fixed on main
(2f90bc3f). This check confirmed them independently: linux-hardware.org probes, the AC7 `Input.ini` GUID and the
joystick-mapper dumps.

**MOZA**

| Model | Added | Source |
|---|---|---|
| MA3F FCD | 346E:1305 | MobiFlight |
| MB7F FCD | 346E:1306 | MobiFlight |
| MGX1000 | 346E:1304 | MobiFlight |
| R9 | 346E:0002 | linux-hardware.org |
| R5 | 346E:0004, in addition to the earlier 0014 | linux-hardware.org |
| SR-P | 346E:0003 and 346E:0011 | linux-hardware.org |

**Other vendors**

| Model | Added | Source |
|---|---|---|
| Octavi IFR-1 | 04D8:E6D6 "Octavi" | MobiFlight |
| VKB GNX SEM with THQ modules | 231D:2214, 2224, 2234 and their names | linux-hardware.org and MobiFlight |
| VKB STECS MTS | Windows names (MINI PLUS, STANDARD [STEM], MAX STEM FSM.GA) | MobiFlight |
| VKB STECS STG Max L | Windows name | MobiFlight |
| Virpil WarBRD base | 3344:40CC, 40D5 | linux-hardware.org |
| Virpil MT-50CM3 base | 3344:4391, 8390 | linux-hardware.org |
| Virpil Control Panel #2 | 3344:025B | linux-hardware.org |
| Virpil MT-50CM3 throttle | 3344:0194 | linux-hardware.org |
| Logitech flight panels | MobiFlight names | MobiFlight |
| Honeycomb Sierra | MobiFlight name | MobiFlight |

## Still unknown

- **WinWing URSA MINOR Space L/R:** no dump of a real Space stick shows its own USB id. Main lists 4098:BC2B / BC2C,
  derived from SimAppPro's grip ids 0x0B / 0x0C and marked "not seen on a real device". Owners' Elite binds show a
  Space L reporting BC29, the Fighter id. BC2B/BC2C were removed (never guess an id); the Space sticks keep the
  Fighter ids BC29/BC2A plus their names. Add their own id once someone sends a support file.
- **URSA MINOR Combat Throttle without the EX module:** its own USB id is not known (the B970 dump is the Metal.EX).
- **URSA MINOR Combat Throttle EX module:** inputs 62-81 and X/Y in analog mode are not mapped yet. A SimAppPro
  numbering of them is in joystick-mapper issue #25.
- **Not published anywhere:**
  - Hoowalt D301 USB id, name and numbers;
  - MOZA MA3F sidestick button numbers (MOZA Cockpit);
  - IIDB tiller axis letter.
- **Not found:** WINGFLEX photos of the plain "OVHD Cube" name. The overhead photo is the A320 OVERHEAD PANEL.

## Seen but not added

| Product | Why not |
|---|---|
| Thrustmaster TCA Sky Quadrant | Announced for Q4 2026; no inputs, no maker page. |
| WinCtrl Cyber Taurus FFB Yoke, WinCtrl OVHD32 SOP | Announced only. |
| PXN NAVOS HOTAS / Yoke | Announced only. |
| Meridian GMT Horizon+, Latitude+, X-Ray | Pre-order for Q1 2027; no input counts. |
| Hoowalt D303 cyclic | Pre-order. |
| Other Hoowalt items (FFB pedals, B737 FFB yoke, D107 V2 quadrant, D202 tiller, trim wheel) | Not checked yet. Next pass. |
| Virtual Fly YOKO Force Neo | The maker says it is not an HID device (VF Force Hub only), so JoyPro cannot read it. |
| Thrustmaster T.Flight Hotas 5 MSFS Edition, T.Flight Hotas NEO ACE COMBAT 8 Edition | Editions of models in the catalog; USB ids not found. |
| Virpil VPC Apache-64 Collective Grip | Older gap (2024). Numbering not found; next pass. |

Data to fix in a later pass (from the maker specs, numbering missing):

- `thrustmaster-tca-sky-yoke` has no controls. Thrustmaster's spec: 24 buttons, 7 axes, 8-way hat, mini stick, 2
  analog triggers.
- `winwing-32-sidestick-grip` has no controls.
- `moza-mfy` lists 50 controls; MOZA's July 2026 text gives the MFY Pro 26 programmable inputs.

## Checks run

- `python tools/device_catalog.py check` on the drafts: 0 problems. The checker now understands hosts with a path
  prefix (shared CDN folders), as the engine does.
- `engine devices reference check`: 488 models, 0 problems.
- `engine layout callouts --check-all` on the new models: 0 problems.
- `engine devices reference identify` on the new and changed ids: all exact matches. Name-only matches:
  "WINCTRL URSA MINOR Combat Throttle", the Space sticks. The AB9 base offers the MA3F.
- GUI tests, run in a test sandbox: DeviceReferenceTests, CuratedDeviceLayoutsTests, DeviceCardsTests,
  DeviceLineArtTests, DevicePhotoListTests, DeviceCompletenessTests, DeviceAliasTests, CalloutLayoutTests,
  DeviceIconTests and ReferencePhotoStoreTests. 125 passed, 0 failed.

## Sources

- linux-hardware.org USB probes: `https://linux-hardware.org/?id=usb:<vid>-<pid>` (4098-bc29, -bc2a, -be03, -be05,
  -be11, -be22, -be48, -bee0, -bee1, -bee2, -bf06; 231d-2214; 3344-40cc, -40d5, -4391, -8390, -025b, -0194;
  346e-0002, -0003, -0004, -0011)
- Linux kernel `drivers/hid/hid-winwing.c` (TGRIP-15E 0xbd65)
- MobiFlight-Connector `src/MobiFlightConnector/Joysticks` (winwing, moza, octavi, vkbsim, wingflex, iidb, logitech,
  honeycomb), commit 43ef5bf3 (2026-11-14)
- joystick-mapper issues #24 and #25 (Windows HID dumps of the URSA MINOR Combat Joystick R and Combat Throttle
  Metal.EX)
- clemthi/ac7-ursa-minor `ac7/Input.ini` (DirectInput product GUID BC2A4098)
- f3nr1l/WinctrlConfig `rust/src/protocol.rs` (URSA grip ids from the firmware catalog)
- Kyr4l/msfs-linux-companion `docs/TESTED.md`
- rswilem/winctrl-xplane-plugin `src/include/utils/usbdevice/usbdevice.cpp` (its code comments swap Fighter L/R)
- Maker pages:
  - wingflex.com/products/{a320-fcu-cube, a320-efis-cube, a320-rmp-combo-cube, a320-overhead-panel, dap-500, gmp}
  - mozaracing.com/products/ma3f-airbus-sidestick
  - hoowalt.com/products/flight-simulator-helicopter-collective-with-throttle-axis-compatible-with-msfs-x-plane-and-dcs
- Release news: simFlight (WINGFLEX Cube series 2025-04-11; Hoowalt D301 2026-08-18), msfsaddons (MOZA MA3F
  2026-10-22), FSElite (Thrustmaster FSExpo 2026, WinCtrl FSWeekend 2026, PXN NAVOS)
