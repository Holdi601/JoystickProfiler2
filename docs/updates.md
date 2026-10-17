# Version and updates

## Where JoyPro shows its version

- The bottom right corner of the window: `v2.0.0`. When a newer version was found it says `v2.0.0 · update 2.1.0`.
  A click opens Settings > **About JoyPro and updates**.
- Settings > **About JoyPro and updates**: version, build date and commit (`Built 2026-10-17 from commit a6a72cc,
  .NET 9.0.x`), the license, the third-party notices, the project and release pages.
- The top bar's **More** menu: **Check for updates** and **About JoyPro**.

The build date comes from `JoyPro.Gui.csproj` (`AssemblyMetadata BuildDate`, UTC day of the build), the commit from
the informational version (`2.0.0+<sha>`, set by the SDK from git). The version itself is `<Version>` in
`JoyPro.Gui.csproj`; `tools/publish_release.ps1` names the package after it.

## How an update works

1. **Check**: at start (Settings: **Check for updates at start**, on by default) and with **Check for updates**,
   JoyPro asks GitHub Releases of the public repository for the latest release
   (`https://api.github.com/repos/<owner>/<repo>/releases/latest`; default `Holdi601/JoystickProfiler2`, changeable
   under "Where updates come from (for experts)"). Drafts and pre-releases are never offered (GitHub leaves them out
   of `latest`). Versions compare as Semantic Versioning (`v2.1.0` > `2.1.0-beta.2` > `2.0.0`).
2. **Ask**: a newer version opens a dialog over the page with what changed (the release notes) and
   **Update now** / **Later** / **Skip this version**. Later asks again at the next start. A skipped version is not
   offered at start any more (and not shown in the corner); **Check for updates** still shows it, and Settings has
   **Offer it again**. A manual check always says what it found: up to date, no release, could not check (no
   internet, GitHub's rate limit), or a release without a package (then only **Release page**).
3. **Download and check**: the release's `JoyPro2-<version>-win-x64.zip` goes to `<data>\Updates\<version>`
   (`%LOCALAPPDATA%\JoyPro2\Updates`), with a progress bar. Its SHA-256 must match the release's `.sha256` file;
   otherwise the download is deleted, nothing is installed and the dialog says so ("does not match its checksum ...
   JoyPro stays as it is").
4. **Install and restart**: the package is unpacked, JoyPro writes `install-update.ps1` next to it, starts it and
   closes (also with "Close to tray" on). The script waits for JoyPro to exit, stops JoyPro's engine and tray helper
   of this install, copies the files over the install folder (asking for administrator rights only when the folder is
   not writable), copies DB files only where the release's file is newer (a refreshed command list stays), logs to
   `<data>\Updates\update.log` and starts JoyPro again. Profiles and settings are not in the install folder and stay.

Publishing a release (not done by the app): `tools/publish_release.ps1` builds the zip and its `.sha256` (a fresh build without debug symbols; it fails when a file of the package names the build machine's folders); both go
to a GitHub release tagged `v<version>` of the public repository.

## Testing without GitHub

`JOYPRO_UPDATE_FEED` replaces the GitHub address with your own `releases/latest` JSON: a file path, a `file://`
address or an `http(s)://` address. Its asset addresses may be `file://` too. The GUI tests set it to a file that
does not exist, so no test asks GitHub. `UpdateFeedTests` build fake releases (`FakeReleaseFeed`, a localhost
`LocalReleaseServer`) and cover no release, the same version, newer, no package, a broken answer, no internet,
GitHub's 403, a bad checksum, the download and unpack, and the copy script on a temp install (newer DB files kept).
The journey scenario `settings-updates` renders every state of the dialog.
