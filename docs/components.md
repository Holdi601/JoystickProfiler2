# Optional components (one-click install)

Some JoyPro features need free third-party programs that are **not part of JoyPro and not shipped with it**. JoyPro
detects them and, only when the user asks, downloads the official installer from the upstream project, checks it and
runs it. Nothing is downloaded or installed at startup, in the background or without the user's click and consent.

| Component | What it is | Needed for | Not needed for |
|---|---|---|---|
| vJoy | virtual joystick driver | Virtual Joystick page: games set to "vJoy" | everything else |
| HidHide | hides chosen game controllers from chosen programs (Nefarius) | Virtual Joystick page: "Hide the real devices" | everything else |
| ViGEmBus | virtual Xbox 360 controller driver (Nefarius) | Virtual Joystick page: games set to "Xbox pad" | everything else |
| Local AI model (llama.cpp + GGUF) | optional language model for "Group by description" | unusual wording in Group by description | the offline rules (always work) |
| .NET Framework 4.8 | Windows component | the engine (needed by JoyPro) | - |
| BYOJoystick (VTOL VR mod, with the free VTOL VR Mod Loader) | HOTAS support inside VTOL VR | VTOL VR beyond rudder and toe brakes | every other game |

BYOJoystick is not downloaded by JoyPro: it has no license (all rights reserved by its author) and its only official
binary source is the Steam Workshop. The VTOL VR card's **Install BYOJoystick** shows what it is, the source, the size
Steam lists and the license situation; after the user's tick JoyPro backs up the user's BYOJ files and opens Steam on
the Mod Loader (when missing) and the Workshop item, where the user subscribes; Steam downloads and verifies it.
**Check again** shows the SHA-256 of the installed `BYOJoystick.dll` and builds JoyPro's target list from it
(docs/games/vtol-vr.md "One-click install").

Force feedback (FFB) needs no extra component. Tesseract is used only by the development test kit (tools/e2e), never
by JoyPro.

## Where it is offered

- **Settings > Components**: "Needed by JoyPro" (.NET Framework 4.8 and the engine files, `Install Missing` as
  before) and "Optional": one card per component, plus a card for the local AI model whose `Set up` button opens its own
  section (Relations page > Group by description > Local AI model). Its download keeps its own consent flow
  (`engine llm download ... --accept-license`, pinned revision + SHA-256, docs/natural-language-grouping.md).
- **Virtual Joystick page**, Overview tab: "What this page needs" shows the same vJoy, HidHide and ViGEmBus cards (the
  same objects, so a running install shows on both pages).

Each card says in plain words what the component does and why JoyPro needs it, shows its status (not installed /
installed and running / installed but the driver is not running: restart Windows / older than the version JoyPro offers
/ newer than the tested version), the source, version and size, how it is checked, and its license in an expander.
`Install <name>` (or `Update to <version>`) is enabled only after the tick "I have read what <name> does and its
license, and I want JoyPro to download and install it". The tick is cleared after an install, so every install has its
own consent. `Project page` opens the upstream release page, `Windows apps` (when installed) opens Windows' Installed
apps (`ms-settings:appsfeatures`) for repair or uninstall with the component's own uninstaller. JoyPro never reinstalls
the same version and never downgrades.

Detection is read-only and runs when Settings or the Virtual Joystick page opens and on `Check again`: Windows'
installed-apps list (HKLM `...\Uninstall`, 64- and 32-bit views: DisplayName, DisplayVersion, InstallLocation) and the
driver's service through the Service Control Manager with query rights only (installed = the service exists, running =
it runs). vJoy also needs `x64\vJoyInterface.dll` in its folder, as the engine's own detection does.

## Sources, versions and verification

All three are pinned in `gui/JoyPro.Gui/Services/Components/ComponentCatalog.cs`: exact release asset URL, size,
SHA-256 and the organization (`O=`) of the installer's Authenticode signing certificate. The upstream GitHub releases
publish no checksums for these assets (`digest` is empty in the release API), so the SHA-256 values were computed from
the official assets on 2026-10-11 and the signatures were checked then (valid, timestamped).

| Component | Upstream | Version / asset | Size | SHA-256 | Signer (O=) | License |
|---|---|---|---|---|---|---|
| vJoy | https://github.com/jshafer817/vJoy | v2.1.9.1 `vJoySetup.exe` | 11,482,272 | `f103ced4e7ff7ccb49c8415a542c56768ed4da4fea252b8f4ffdac343074654a` | On-site Dental Systems (Justin Shafer) (DigiCert EV) | MIT, (c) 2017 Shaul Eizikovich |
| HidHide | https://github.com/nefarius/HidHide | v1.5.230.0 `HidHide_1.5.230_x64.exe` | 8,078,016 | `f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6` | Nefarius Software Solutions e.U. (DigiCert) | MIT, (c) 2020 Eric Korff de Gidts, (c) 2021-2024 Benjamin Hoeglinger-Stelzer |
| ViGEmBus | https://github.com/nefarius/ViGEmBus | v1.22.0 `ViGEmBus_1.22.0_x64_x86_arm64.exe` | 6,278,576 | `89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a` | Nefarius Software Solutions e.U. (DigiCert) | BSD-3-Clause, (c) 2016-2020 Nefarius Software Solutions e.U. |

Verification before the installer runs, all of it required:

1. size equals the pinned size;
2. SHA-256 equals the pinned value (computed from the same open file handle);
3. Windows' Authenticode check (`WinVerifyTrust`: file unchanged since signing, trusted chain; an expired certificate
   counts only with a valid timestamp, as with these three);
4. the signing certificate's organization equals the pinned one exactly.

The verified file stays open with write and delete denied from the hash check until the installer has ended, so the
file that runs is the file that was checked. A file that fails any check is deleted and nothing runs. A file already
downloaded is used again only when it still has the pinned size and checksum.

### Why jshafer817/vJoy 2.1.9.1 and not BrunnerInnovation/vJoy 2.2.2.0

The original project (shauleiz/vJoy, Shaul Eizikovich) has had no release since 2.1.8.39 (May 2018); njz3/vJoy 2.2.x
(2020) is unsigned for current Windows. Two signed continuations exist:

- **jshafer817/vJoy v2.1.9.1** (July 2019): signed Windows 10/11 build of the original 2.1.9 code; the version the
  JoyPro docs name, the one on the development PC (vJoy Device Driver 2.1.9.1, Windows 11), and the one JoyPro's
  forwarder (`gui/JoyPro.VJoy`, `JOYSTICK_POSITION_V2`) and the in-game test kit were verified with.
- **BrunnerInnovation/vJoy v2.2.2.0** (December 2023, Windows 10/11 only, EV-signed by Brunner Elektronik AG): a fork
  of njz3/vJoy with force-feedback work; Brunner sponsored the signing and says it does not intend to maintain it
  further. Its SDK switches to `USE_JOYSTICK_API_VERSION 3`: `UpdateVJD` copies a `JOYSTICK_POSITION_V3`, 16 bytes
  longer than the V2 structure JoyPro.VJoy passes. Not offered until the forwarder writes the V3 layout and is tested
  with it on a real install. A user who installed it on their own keeps it (JoyPro shows it as "newer than the tested
  version" and never downgrades).

### ViGEmBus is retired

Nefarius retired ViGEm in 2023 and archived the repository; 1.22.0 is the last release. It still works and is what
JoyPro's forwarder uses (Nefarius.ViGEm.Client). The card says so in its author line.

## The install, step by step

```
tick "I have read ..."  ->  Install
  download   pinned URL -> <data>\Components\<file>.part -> <file>     (progress bar, Cancel download)
  verify     size, SHA-256, Authenticode, signer organization          (any failure: file deleted, nothing runs)
  install    the official installer, elevated (Verb "runas"): one Windows UAC prompt, never bypassed
  check      detect again; report
```

`<data>` is `%LOCALAPPDATA%\JoyPro2` (`JOYPRO_DATA_DIR` in tests). The installer is deleted after a successful
install. One component installs at a time (Windows runs one setup at a time anyway).

Unattended switches (the setup still shows its progress window; no questions):

| Installer | Switches | Exit codes |
|---|---|---|
| vJoy (Inno Setup) | `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-` | 0 ok; 2, 5 cancelled; 8 restart Windows first; other: failed |
| HidHide, ViGEmBus (Advanced Installer EXE around an MSI; MSI switches pass through) | `/passive /norestart` | 0 ok; 3010, 1641 ok, restart needed; 1602 cancelled; 1618 another setup is running; other: failed |

Windows may additionally ask whether to install a driver from the publisher ("Would you like to install this device
software?"); the card's progress text tells the user to choose Install. A declined UAC prompt (Win32 error 1223) is
reported as "the administrator prompt was declined" and nothing changes; the next click needs no new download.

**Restart**: JoyPro never restarts Windows. When the setup reports a restart (3010/1641), or the driver's service exists
but does not run after the setup, or the setup ended but the driver is not visible yet, the card says so in a yellow
box ("Restart Windows before you use it. Save your work first; JoyPro does not restart the PC by itself.").

After a successful install the Virtual Joystick page reads its status again (`engine vjoy status`) when it was open.

## Code

| Part | File |
|---|---|
| Catalog (pins, texts, licenses) | `gui/JoyPro.Gui/Services/Components/ComponentCatalog.cs` |
| Detection | `gui/JoyPro.Gui/Services/Components/ComponentProbe.cs` |
| Download, verify, elevated run, result | `gui/JoyPro.Gui/Services/Components/ComponentInstaller.cs` |
| Authenticode + signer organization | `gui/JoyPro.Gui/Services/AuthenticodeVerifier.cs` (also used for .NET 4.8: Microsoft Corporation) |
| Card model / view | `gui/JoyPro.Gui/Models/ComponentItem.cs`, `gui/JoyPro.Gui/Views/Controls/ComponentCard.axaml` |
| View model | `gui/JoyPro.Gui/ViewModels/MainWindowViewModel.Components.cs` |

The engine is not involved: it keeps detecting vJoy, HidHide and ViGEmBus for `engine vjoy status` and never installs
anything.

**Updating a pin**: download the new official asset, check the signature (`Get-AuthenticodeSignature`: Valid, same
publisher), compute `Get-FileHash -Algorithm SHA256`, check the switches and exit codes of the new installer, update
`ComponentCatalog.cs`, this file and THIRD_PARTY_NOTICES.md, and test the install on a PC (see below).

## Tests

`gui/JoyPro.Gui.Tests/ComponentInstallerTests.cs`. Every GUI test run replaces detection, download, signature check
and installer runner with fakes (`TestEnvironment`): nothing is detected on the PC, downloaded or installed, and no UAC
prompt can appear (the test that runs every view-model command included). The tests cover consent, the happy path and
the order of the steps, refusals (checksum, size, invalid signature, valid signature of another publisher), a declined
UAC prompt, reuse and re-download of a cached file, the file lock while the installer runs, download failure and
cancel, MSI and Inno exit codes, restart handling, the pinned catalog, signer parsing, version comparison, the card
(tick, update, no reinstall/downgrade, one install at a time, Windows apps), the view model (check on opening Settings,
the local AI model link) and .NET 4.8 through the same seams. `RenderComponentCards` draws both places offscreen
(opt-in, `JOYPRO_GUI_PARITY_PREVIEW`).

### Live test (manual, on request)

On a PC (or VM) without the component, for each of vJoy, HidHide, ViGEmBus:

1. Settings > Components: the card shows "Not installed", the license expander shows the license, Install is disabled
   until the tick.
2. Tick, Install: progress bar during the download; one UAC prompt (publisher shown by Windows: the signer above);
   possibly Windows' driver prompt; the setup's progress window, no questions.
3. The card shows "Installed <version> and running", or the restart box; after a restart `Check again` shows running.
4. Virtual Joystick page: the same card is green; Refresh shows the component in the engine status.
5. Decline the UAC prompt once: the card says so, nothing is installed.
6. `Windows apps` opens Installed apps; uninstall from there and `Check again` shows "Not installed".

On the development PC all three are already installed (vJoy 2.1.9.1, HidHide 1.5.230, ViGEmBus 1.22.0): there the live
check is detection only (all green, no Install button), unless a component is uninstalled first.
