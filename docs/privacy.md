# Privacy

JoyPro has no account, no telemetry and no analytics. Your profiles, game configurations, logs and crash reports stay
on your PC. A problem report is a zip file that JoyPro only creates when you press "Create Report"; you decide whether
to send it. User name, e-mail addresses and passwords are removed from it.

**Telemetry recording** (Settings > Telemetry recording, off by default; [telemetry-recording.md](telemetry-recording.md))
records your games' telemetry into files in a folder you pick. It connects to nothing on the internet: it only listens
on 127.0.0.1, reads the games' local files or shared memory and asks the game's own local server (War Thunder), and
only while the game runs. Nothing is uploaded. War Thunder's chat is never read; kill messages in a recording name
other players, as the game's HUD does. Delete recordings in the same section.

JoyPro connects to the internet only for the things listed below. Each connection shows your **IP address** to the
site it contacts (as any browser visit does). The site's own privacy policy applies to it.

| What | When | Site | What is sent |
|---|---|---|---|
| Update check | "Check for updates" (Settings > About JoyPro and updates, More menu), or at start when "Check for updates at start" is on; the download only after "Update now" ([updates](updates.md)) | GitHub (`api.github.com`, release downloads from `github.com`) | IP address, user agent `JoyPro2/<version>` |
| Device photos | only after you said yes for that device (see "Device photos" below), when the Layout page or a device card on the Start page shows it | the maker's own web site named in JoyPro's photo list (`DB\DevicePhotos`), e.g. VKB, Virpil, Thrustmaster, Winwing, Logitech, MOZA | IP address; a photo is kept in `%LOCALAPPDATA%\JoyPro2\DeviceImages` and loaded only once |
| Cockpit photos | only after you said yes for that aircraft on the Cockpits page (see "Cockpit photos" below) | the site named in JoyPro's cockpit database (`DB\Cockpits`), mostly Wikimedia Commons (`upload.wikimedia.org`) and official pages | IP address; a photo is kept in `%LOCALAPPDATA%\JoyPro2\DeviceImages` and loaded only once |
| Cockpit picture packs | when you open an aircraft on the Cockpits page that has no pictures from your games: the list of packs (`manifest.json`, once per session) unless Settings > Privacy > "Cockpit pictures from JoyPro's releases" is "Never"; a pack only after you press Download (or with "Always download"), also for "Download pictures for all my aircraft" / one game ([cockpits](cockpits.md#picture-packs)) | GitHub release downloads (`github.com`, releases `cockpit-pictures-v1`, `cockpit-pictures-wt-v1` and `cockpit-pictures-wt-v1-part2` of JoyPro's repository) | IP address, user agent `JoyPro2/<version>`; the pictures are kept in `<data>\GameData\CockpitPictures` |
| Game covers | once per game, when Steam has no cover of it on this PC (a game not in your Steam library, or not on Steam) and "Game covers from the internet" is ticked (Settings > Privacy, its own setting, on by default; unticked = no download, and no downloaded cover is shown) | Steam's store image server (`shared.akamai.steamstatic.com`) for Steam games and FSX; for games not on Steam the publisher's site: Roberts Space Industries (`robertsspaceindustries.com`, `cdn.robertsspaceindustries.com`) for Star Citizen, `www.falcon-bms.com` for Falcon BMS, Lockheed Martin's `www.prepar3d.com` for Prepar3D. The full list: `engine media sources` | IP address, user agent `JoyPro2`; pictures are kept in `%LOCALAPPDATA%\JoyPro\Media\Covers` and loaded only once (a failed one is tried again a day later) |
| BYOJoystick mod for VTOL VR (optional) | only after you tick the consent and press Install BYOJoystick on the VTOL VR card | JoyPro connects to nothing itself: it opens the Steam client on the Workshop page (and the VTOL VR Mod Loader's page when it is missing); Steam downloads the mod after you subscribe | what Steam sends for any download (Steam's privacy policy) |
| Layout picture from a web address | only when you enter a picture address in the Layout editor | the address you entered | IP address |
| Local AI model (optional) | only after you tick "I accept" and press Download (Relations > Group all my actions automatically > By description) | GitHub (llama.cpp), Hugging Face (model file) | IP address |
| Optional drivers (vJoy, HidHide, ViGEmBus) | only after you tick "I have read ..." and press Install on a Components card (Settings > Components, Virtual Joystick page; docs/components.md) | GitHub release downloads (`github.com`: jshafer817/vJoy, nefarius/HidHide, nefarius/ViGEmBus) | IP address, user agent `JoyPro2/<version>` |
| .NET Framework 4.8 installer | only when it is missing and you press Install | Microsoft (`go.microsoft.com`) | IP address |
| Report a problem | only when you press "Open GitHub Issue Page" | GitHub, in your browser | what you type there |

### Device photos (asked per device)

JoyPro ships no product photo. Its photo list (`DB\DevicePhotos`) holds only the address of each photo on the maker's
own web site. When JoyPro recognizes one of your devices and such a photo exists, it asks once for that device, on the
Layout page and on the device's card on the Start page:

> JoyPro found the product photo of your <model> on <maker's site>. Download it and show it here? (It is only stored on
> this PC.)  **Yes** · **No** · **Always for all my devices** · **Never**

- Until you say yes, nothing is downloaded and JoyPro shows its own drawing of the device or the outline of its kind.
- The answer is kept per device model (`%LOCALAPPDATA%\JoyPro\DeviceReference\settings.json`). The "Maker's photo of
  this device" switch on the Layout page changes it for the shown device.
- Settings > Privacy > "Makers' product photos": **Ask for each device** (default), **Always download** or **Never**, and
  "Ask again for every device" (forgets the answers). Never also takes back every yes. This replaces the old
  "Manufacturer photos" switch (off there counts as Never).
- The engine never downloads a device photo; with Never the GUI asks it not even to name the photo addresses
  (`engine hud devices --no-device-photos`).

### Cockpit photos

The Cockpits page (docs/cockpits.md) links real photos of cockpits. JoyPro ships only their addresses. Before it loads
any photo of an aircraft it asks:

> JoyPro has photos of the <aircraft> cockpit on <sites>. Download them and show them here? (They are only stored on
> this PC.)  **Yes** · **No** · **Always for cockpits** · **Never**

- Until you say yes, nothing is downloaded; JoyPro's own drawings and the "Open the page" links (your browser) work
  without it.
- The answers are kept with the device photo answers (`cockpit:<aircraft>`, "Always" / "Never" as `cockpit:*`).
  Settings > Privacy > "Makers' product photos: Never" also stops cockpit photos; "Ask again for every device" forgets
  the cockpit answers too.
- The engine never downloads a cockpit photo.

Game covers are a separate setting (Settings > Privacy > "Game covers from the internet"), see the table.

Everything else is local:

- Force feedback telemetry talks to the games on your own PC only (`127.0.0.1`: DCS and IL-2 over UDP, War Thunder's
  local web server on port 8111).
- The local AI model runs on your PC (`llama-server` on `127.0.0.1`); your requests never leave the PC.
- Game covers are read from your own Steam library first (Steam's cache on this PC); only the missing ones are
  downloaded (see the table). DCS aircraft pictures are read from your DCS installation.
- Command lists are built from your installed games on this PC (docs/games/README.md). For War Thunder JoyPro reads the
  game's packed archives (`gui.vromfs.bin`, `lang.vromfs.bin`) by default, read only and offline; Settings > Games turns
  that off (then JoyPro's own list).

GitHub: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement ·
Hugging Face: https://huggingface.co/privacy ·
Valve (Steam): https://store.steampowered.com/privacy_agreement/ ·
Cloud Imperium (RSI): https://robertsspaceindustries.com/en/privacy ·
Lockheed Martin: https://www.lockheedmartin.com/en-us/contact/privacy-center.html

For the operator of a public download page or shop: these connections belong in the site's privacy notice
(Datenschutzerklärung) as well.
