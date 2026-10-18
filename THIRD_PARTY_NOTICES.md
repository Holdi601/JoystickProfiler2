# Third-party notices

JoyPro 2 contains, or loads at run time, software and data made by other people. This file lists every part that a
release package (`tools/publish_release.ps1`: self-contained `win-x64` zip) ships, and every part JoyPro downloads or
calls on the user's request but does not ship. The license texts follow at the end of the file.

JoyPro itself is free software under the **GNU General Public License, version 3 or later** (`GPL-3.0-or-later`,
[`LICENSE`](LICENSE), [`NOTICE.md`](NOTICE.md)). Every component below may be combined with it; see
[section 6](#6-compatibility-with-joypros-license-gpl-30-or-later).

Status: inventory of 2026-10-17. Versions come from `dotnet list package --include-transitive` and the file versions
of `engine/lib/*.dll`. Update this file whenever a package, a native DLL or a download source changes (see
[section 7](#7-keeping-this-file-correct)).

The same text, without the license texts, is built into the GUI as `gui/JoyPro.Gui/Resources/ThirdPartyNotices.txt`
(for an About / Licenses dialog).

## 1. Shipped in the release package

### 1.1 GUI (`JoyPro.Gui.exe`, .NET 9) and its helper processes (`ffb\`, `tray\`, `vjoy\`)

| Component | Version | License | Copyright | Files in the package |
|---|---|---|---|---|
| Avalonia (Avalonia, Avalonia.Base, .Controls, .Controls.DataGrid, .Controls.ColorPicker, .Desktop, .Dialogs, .Markup, .Markup.Xaml, .MicroCom, .OpenGL, .Vulkan, .Metal, .Skia, .Themes.Fluent, .Themes.Simple, .Win32, .Win32.Automation, .Remote.Protocol, .DesignerSupport, .Native, .X11, .FreeDesktop) | 11.3.12 | MIT | Copyright 2013-2026 © The AvaloniaUI Project | `Avalonia.*.dll` |
| Avalonia.Angle.Windows.Natives (ANGLE, OpenGL ES on Direct3D) | 2.1.25547.20250602 | BSD-3-Clause (ANGLE) | Copyright 2018 The ANGLE Project Authors | `runtimes\win-x64\native\av_libglesv2.dll` |
| SkiaSharp + SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT (wrapper); Skia BSD-3-Clause and further notices | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation; Skia: Copyright Google Inc. | `SkiaSharp.dll`, `runtimes\win-x64\native\libSkiaSharp.dll` |
| HarfBuzzSharp + HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT (wrapper); HarfBuzz "Old MIT" | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation; HarfBuzz: Google, Mozilla, Red Hat, Behdad Esfahbod and others | `HarfBuzzSharp.dll`, `runtimes\win-x64\native\libHarfBuzzSharp.dll` |
| CommunityToolkit.Mvvm | 8.2.1 | MIT | (c) .NET Foundation and Contributors | `CommunityToolkit.Mvvm.dll` |
| MicroCom.Runtime | 0.11.0 | MIT | Copyright 2021 © Nikita Tsukanov | `MicroCom.Runtime.dll` |
| Nefarius.ViGEm.Client (ViGEm.NET; talks to the ViGEmBus driver for the virtual Xbox 360 pad) | 1.21.256 | MIT | Copyright (c) 2018-2023 Nefarius Software Solutions e.U. and Contributors (https://github.com/ViGEm/ViGEm.NET) | `vjoy\Nefarius.ViGEm.Client.dll` |
| Tmds.DBus.Protocol | 0.21.2 | MIT | Copyright Tom Deseyn | `Tmds.DBus.Protocol.dll` (Linux only, shipped as part of Avalonia) |
| System.IO.Pipelines | 8.0.0 | MIT | Copyright (c) .NET Foundation and Contributors | part of the runtime folder |
| .NET 9 runtime and Windows Desktop runtime (self-contained publish: `Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App` for the WinForms tray host) | 9.0.x | MIT, with the .NET third-party notices | Copyright (c) .NET Foundation and Contributors | `System.*.dll`, `Microsoft.*.dll`, `coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`, `clrjit.dll`, ... in the root, `ffb\`, `tray\` and `vjoy\` |

The Linux, macOS and WebAssembly native assets of SkiaSharp and HarfBuzzSharp are not needed on Windows; a
`win-x64` publish only copies the Windows ones.

### 1.2 Engine (`engine\engine.exe`, .NET Framework 4.8)

| Component | Version | License | Copyright | Files |
|---|---|---|---|---|
| ZstdSharp (C# port of Zstandard) | 0.8.7 | MIT; Zstandard itself BSD-3-Clause (dual BSD/GPLv2, used under BSD) | Copyright (c) 2021-present Oleg Stepanischev; Zstandard: Copyright (c) Meta Platforms, Inc. and affiliates | `engine\ZstdSharp.dll` |
| System.Buffers | 4.0.3 (file 4.6.28619.01) | MIT | Copyright (c) .NET Foundation and Contributors | `engine\System.Buffers.dll` |
| System.Memory | 4.0.1.2 (file 4.6.31308.01) | MIT | Copyright (c) .NET Foundation and Contributors | `engine\System.Memory.dll` |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 | MIT | Copyright (c) .NET Foundation and Contributors | `engine\System.Runtime.CompilerServices.Unsafe.dll` |
| System.Threading.Tasks.Extensions | 4.2.0.1 (file 4.6.28619.01) | MIT | Copyright (c) .NET Foundation and Contributors | `engine\System.Threading.Tasks.Extensions.dll` |

The engine uses .NET Framework 4.8 and DirectInput (`dinput8.dll`), both part of Windows; they are not shipped. When
.NET Framework 4.8 is missing, the GUI downloads Microsoft's official installer on request and runs it only after
checking Microsoft's Authenticode signature.

### 1.3 Icons, fonts and pictures

| Part | Origin | License |
|---|---|---|
| GUI icons (`gui/JoyPro.Gui/Styles/Icons.axaml`, 45 path geometries) | Most shapes are the path data of Material Design Icons (Pictogrammers, https://pictogrammers.com/library/mdi/); joystick, axis curve, sun, moon and keycap are drawn for JoyPro | Material Design Icons: Pictogrammers Free License (Apache-2.0 based; see section 4.4) |
| Application icons (`gui/JoyPro.Gui/Assets/joypro2_gui.ico`, `engine/assets/engine_console.ico`, `branding/*`), the JoyPro mark, the title "JOYPRO 2" and the game badges (`gui/JoyPro.Gui/Styles/Branding.axaml`, `Views/Controls/GameBadge.cs`, `docs/branding/*`) | Drawn for JoyPro as path data (`branding/generate-branding.py`, `branding/generate-icons.ps1`); the mark redraws the original JoyPro logo. No font is used: the title's letters and the "2" / ">_" are JoyPro's own shapes. The game badges use the Material Design Icons motifs listed above (airplane, rocket) and a helicopter drawn for JoyPro | JoyPro's own |
| Fonts | None shipped. The GUI names Windows system fonts (Bahnschrift, Consolas) and the Avalonia default font; they are not redistributed | — |
| Device pictures on the layout pages | JoyPro's own drawings of the devices (`DB/DeviceDrawings`) and from the device reference (`DB/Devices`), the user's own pictures, or (after the user's yes) the manufacturer's photo loaded on the user's PC. The former bundled `LayoutTemplates/*.layout` pictures of other people were removed (2026-10-11) | JoyPro's own / the user's |
| Device product pictures (`EngineOnly_Runtime/DB/Devices/*.json`, `"image"` URLs) | Not shipped: downloaded from the manufacturer's (or a shop's) web site into `%LOCALAPPDATA%\JoyPro2\DeviceImages` when the GUI shows the device | Copyright of the respective manufacturer; see audit finding P5 |
| Game covers and DCS aircraft pictures | Not shipped: read from the user's own Steam library cache / Steam's store CDN and from the user's DCS installation at run time | Copyright of the respective game publisher |

### 1.4 Game names and data

JoyPro names the games and devices it supports to say what it works with (nominative use). All game names, aircraft
names and device names are trademarks of their owners; JoyPro is not affiliated with, endorsed or sponsored by any of
them.

JoyPro ships **no game data**. Each game's command list (and DCS's clean profiles) is built on the user's PC from the
user's own installation, the first time the game is set up or with "Update" on its card. What ships is JoyPro's own
work: its function vocabulary and groupings (`DB/Functions`), its device knowledge base (`DB/Devices`), the model list
(`DB/Llm`) and its key conversion tables (`TOOLS/Conversions`, e.g. X4's DirectInput key names and Elite's key names,
interoperability data). See `docs/games/README.md`.

Suggested About-page wording:

> DCS World is a trademark of Eagle Dynamics SA. Microsoft Flight Simulator and Xbox are trademarks of the Microsoft
> group of companies. Star Citizen is a trademark of Cloud Imperium Rights LLC. IL-2 Sturmovik is a trademark of
> 1C Entertainment / 1C Game Studios. X4 and Egosoft are trademarks of Egosoft GmbH. Elite Dangerous is a trademark of
> Frontier Developments plc. War Thunder is a trademark of Gaijin Network Ltd. Arma is a trademark of Bohemia
> Interactive a.s. X-Plane is a trademark of Laminar Research. Falcon BMS is a project of Benchmark Sims. VTOL VR,
> Nuclear Option, Hell Let Loose: Vietnam and Wardogs are the property of their developers and publishers. VKB, Virpil
> (VPC), VPforce, Winwing, Thrustmaster, Logitech, Saitek, Honeycomb, MOZA, Brunner, CH Products, Turtle Beach and
> Real Simulator are trademarks of their owners. All other names are the property of their respective owners. JoyPro
> is an independent product and is not affiliated with or endorsed by any of them.

### 1.5 Credits for file formats

- War Thunder `.vromfs.bin` reading (`engine/Games/WarThunderVromfs.cs`) follows the format as published in Gaijin
  Entertainment's open-source Dagor Engine (https://github.com/GaijinEntertainment/DagorEngine, BSD-3-Clause:
  `prog/engine/ioSys/vromfsLoad.cpp`, `prog/dagorInclude/supp/dag_zstdObfuscate.h`). No Dagor source code is copied;
  the constants needed to read the format are taken from it.
- BC7 texture decoding for the cockpit pictures (`engine/CockpitPictures/Bc7.cs`) is a C# port of bcdec by Sergii
  Kudlai (https://github.com/iOrange/bcdec, MIT License or The Unlicense at the user's choice; used under MIT,
  Copyright (c) 2022 Sergii Kudlai). The port is compiled into `engine\engine.exe`; license text in section 4.6.
- DCS World `.edm` model reading for the cockpit pictures (`engine/CockpitPictures/EdmFile.cs`) follows the format
  descriptions of Blender_ioEDM (https://github.com/ndevenish/Blender_ioEDM, MIT License, Copyright (c) 2016 Nicholas
  Devenish) and DCS-EDM-Blender-Importer (https://github.com/devozdemirhasancan/DCS-EDM-Blender-Importer, MIT License,
  Copyright (c) 2026 devozdemirhasancan and contributors), used as a layout reference; no code is copied. License
  texts in section 4.6.
- War Thunder cockpit models and textures for the cockpit pictures (`engine/CockpitPictures/WarThunder*.cs`: GRP2,
  DynModel, GeomNodeTree, DDSx/DxP2, binary BLK) follow the formats of the open-source Dagor Engine (BSD-3-Clause, see
  above); no Dagor source code is copied. Its packed index buffers are decoded by a C# port of meshoptimizer's
  `meshopt_decodeIndexSequence` (https://github.com/zeux/meshoptimizer, MIT License, Copyright (c) 2016-2020 Arseny
  Kapoulkine), compiled into `engine\engine.exe`; license text in section 4.1.

## 2. Downloaded only after the user agrees (not shipped)

"Group by description" can use a local language model. JoyPro shows size and license and downloads nothing before the
user ticks "I accept" (`engine llm download ... --accept-license`). The files come straight from the publisher, are
pinned by revision and SHA-256, and are stored under `%LOCALAPPDATA%\JoyPro2\Llm`.

| Download | Source | License |
|---|---|---|
| llama.cpp (`llama-server`) Windows build, CPU or Vulkan flavor | https://github.com/ggml-org/llama.cpp/releases | MIT, Copyright (c) 2023-2026 The ggml authors (the release zips carry no license file; the MIT text is in section 4.1). The Vulkan zip includes `libomp.dll`, LLVM OpenMP runtime, Apache-2.0 WITH LLVM-exception |
| llama.cpp CUDA flavor + `cudart-llama-bin-win-cuda-*.zip` | same | llama.cpp: MIT; the CUDA runtime DLLs (cudart, cuBLAS) are NVIDIA software under the NVIDIA CUDA Toolkit EULA (redistributable files, https://docs.nvidia.com/cuda/eula/) |
| Qwen2.5-0.5B-Instruct GGUF (Q4_K_M) | https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF | Apache-2.0, Copyright Alibaba Cloud / Qwen team |
| Qwen2.5-1.5B-Instruct GGUF (Q4_K_M) | https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF | Apache-2.0 |
| Qwen3-4B GGUF (Q4_K_M) | https://huggingface.co/Qwen/Qwen3-4B-GGUF | Apache-2.0 |
| Qwen3-8B GGUF (Q4_K_M) | https://huggingface.co/Qwen/Qwen3-8B-GGUF | Apache-2.0 |
| .NET Framework 4.8 installer (only when missing) | https://go.microsoft.com/fwlink/?linkid=2088631 | Microsoft .NET Framework license (Windows component) |
| JoyPro updates | GitHub releases of this repository | GPL-3.0-or-later (JoyPro's own license, `LICENSE`) |
| Cockpit picture packs (Cockpits page, after the user's yes or with "Always download" in Settings > Privacy; `docs/cockpits.md` "Picture packs") | GitHub releases `cockpit-pictures-v1`, `cockpit-pictures-wt-v1` and `cockpit-pictures-wt-v1-part2` of this repository (release assets, never in the repository or the release package) | Renders and screenshots of the games' own cockpits, made with JoyPro from the games' files. The cockpit designs, textures and names belong to their respective owners (the game's publisher, the aircraft module's or package's developer; `CREDITS.txt` in each pack). Not covered by JoyPro's GPL |

Do not add Qwen2.5-3B or Qwen2.5-72B (Qwen Research / Qwen license, not Apache-2.0) or any model without a license
check; `EngineOnly_Runtime/DB/Llm/models.json` carries the license per model and the consent text shows it.

### 2.1 Drivers installed on request (Settings > Components; not shipped)

The GUI's Components card (docs/components.md) downloads these official installers only after the user ticked "I have
read ... and want JoyPro to download and install it" and pressed Install. Each is pinned by URL, size, SHA-256 and the
organization in its Authenticode signature, checked before it runs, and run with one Windows UAC prompt. JoyPro does
not ship, modify or redistribute them; the installers carry their own license terms, and the card shows the license
below before the user agrees. They are stored only temporarily under `%LOCALAPPDATA%\JoyPro2\Components` and deleted
after a successful install.

| Download | Source | Signer | License |
|---|---|---|---|
| vJoy 2.1.9.1 (`vJoySetup.exe`) | https://github.com/jshafer817/vJoy/releases/tag/v2.1.9.1 | On-site Dental Systems (Justin Shafer) | MIT, Copyright (c) 2017 Shaul Eizikovich (section 4.1) |
| HidHide 1.5.230 (`HidHide_1.5.230_x64.exe`) | https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0 | Nefarius Software Solutions e.U. | MIT, Copyright (c) 2020 Eric Korff de Gidts, (c) 2021-2024 Benjamin Höglinger-Stelzer (section 4.1) |
| ViGEmBus 1.22.0 (`ViGEmBus_1.22.0_x64_x86_arm64.exe`) | https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0 | Nefarius Software Solutions e.U. | BSD-3-Clause, Copyright (c) 2016-2020 Nefarius Software Solutions e.U. (section 4.2) |

## 3. Used if the user has installed them (not shipped; the drivers of section 2.1 can be installed on request)

| Software | How JoyPro uses it | License |
|---|---|---|
| vJoy (`C:\Program Files\vJoy\x64\vJoyInterface.dll`) | loaded by `vjoy\JoyPro.VJoy.exe` for virtual joystick mode | MIT, Copyright (c) 2017 Shaul Eizikovich (shauleiz/vJoy and the njz3, jshafer817, BrunnerInnovation forks). Installed by the user, or on request through section 2.1 |
| ViGEmBus driver | the virtual Xbox 360 pad of virtual joystick mode ("Xbox pad", `vjoy\JoyPro.VJoy.exe` through Nefarius.ViGEm.Client) | BSD-3-Clause, Copyright (c) 2016-2023 Nefarius Software Solutions e.U. and Contributors. Installed by the user, or on request through section 2.1 |
| HidHide (`HidHideCLI.exe`) | run by the engine to hide physical devices during virtual joystick mode | MIT, Copyright (c) 2020 Eric Korff de Gidts, (c) 2021-2024 Benjamin Höglinger-Stelzer (Nefarius), https://github.com/nefarius/HidHide. Installed by the user, or on request through section 2.1 |
| Oodle data compression (`oo2core_*_win64.dll`, Epic Games Tools / RAD Game Tools) | loaded by the engine only during `engine hllv refresh-db --oodle-dll <path>` to read Hell Let Loose: Vietnam's own compressed game files: the game's own copy without a question, another installed game's copy only after the user's yes (asked once, changeable in Settings > Games), loaded from where it is; without it JoyPro uses its own table of the pad keys | proprietary; part of the user's installed games. JoyPro never ships, copies or downloads it. Loading it is covered by the additional permission (GPL-3.0 section 7) in `NOTICE.md` |
| DCS World `luae.exe` / Lua runtime | the user's DCS installation evaluates DCS's own input Lua files when the catalog is refreshed | part of DCS World, Eagle Dynamics |
| The games themselves | JoyPro reads and writes their control configuration files in the user's profile | each game's EULA |

## 4. License texts

### 4.1 MIT License

Applies to Avalonia, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Mvvm, MicroCom.Runtime, Tmds.DBus.Protocol,
System.IO.Pipelines, System.Buffers, System.Memory, System.Runtime.CompilerServices.Unsafe,
System.Threading.Tasks.Extensions, the .NET runtime, ZstdSharp, Nefarius.ViGEm.Client and llama.cpp, each with the copyright line listed above.
bcdec, Blender_ioEDM and DCS-EDM-Blender-Importer are MIT too; their texts with the copyright lines are in section 4.6.

```
MIT License

Copyright (c) <year> <copyright holders>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 4.2 BSD 3-Clause License (ANGLE; Zstandard; Skia; Dagor Engine format constants; ViGEmBus, not shipped)

```
Copyright 2018 The ANGLE Project Authors. All rights reserved.
(Zstandard: Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.)
(Skia: Copyright (c) 2011 Google Inc. All rights reserved.)
(Dagor Engine: Copyright (c) Gaijin Entertainment. All rights reserved.)

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

    Redistributions of source code must retain the above copyright notice,
    this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

    Neither the name of the copyright holders nor the names of their
    contributors may be used to endorse or promote products derived from this
    software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

### 4.3 Bundled third-party notice files (copy into the package)

SkiaSharp, HarfBuzzSharp and the .NET runtime come with their own long notice files (Skia's dependencies: libpng,
libjpeg-turbo, expat, ICU, FreeType, HarfBuzz and others). They must travel with the binaries. `tools/publish_release.ps1`
copies them into `<package>\licenses\`:

- `SkiaSharp-THIRD-PARTY-NOTICES.txt` (from the `skiasharp.nativeassets.win32` 2.88.9 package)
- `HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` (from `harfbuzzsharp.nativeassets.win32` 8.3.1.1)
- `ANGLE-LICENSE.txt` (from `avalonia.angle.windows.natives`)
- `CommunityToolkit-ThirdPartyNotices.txt` (from `communitytoolkit.mvvm` 8.2.1)
- `dotnet-ThirdPartyNotices.txt` (from the `microsoft.netcore.app.runtime.win-x64` runtime pack)
- this file as `THIRD_PARTY_NOTICES.md`
- JoyPro's own `LICENSE` (as `LICENSE.txt`) and `NOTICE.md` in the package root

### 4.4 Material Design Icons (Pictogrammers)

The icon path data is used under the Pictogrammers Free License, https://pictogrammers.com/docs/general/license/,
which licenses the icons under the Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0). Commercial use is
allowed. Attribution: "Icons: Material Design Icons by Pictogrammers (pictogrammers.com), Apache License 2.0".

### 4.5 Apache License 2.0

Applies to the Qwen model weights (downloaded on consent) and to the Material Design Icons path data. Full text: https://www.apache.org/licenses/LICENSE-2.0.txt. JoyPro does not modify or
redistribute the model files.

### 4.6 bcdec, Blender_ioEDM, DCS-EDM-Blender-Importer (MIT License)

Used by the cockpit pictures (`engine/CockpitPictures`, section 1.5).

bcdec (https://github.com/iOrange/bcdec), used under its MIT alternative:

```
MIT License

Copyright (c) 2022 Sergii Kudlai

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Blender_ioEDM (https://github.com/ndevenish/Blender_ioEDM):

```
MIT License

Copyright (c) 2016 Nicholas Devenish

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

DCS-EDM-Blender-Importer (https://github.com/devozdemirhasancan/DCS-EDM-Blender-Importer):

```
MIT License

Copyright (c) 2026 devozdemirhasancan and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 5. Development and test only (never in the release package)

| Component | Version | License | Used by |
|---|---|---|---|
| xunit, xunit.runner.visualstudio, xunit.* | 2.9.2 / 2.8.2 | Apache-2.0 | `gui/*.Tests` |
| Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.*, Microsoft.CodeCoverage | 17.11.1 | MIT | tests |
| coverlet.collector | 6.0.2 | MIT | tests |
| Newtonsoft.Json (transitive of the test SDK) | 13.0.1 | MIT | tests |
| Avalonia.Diagnostics | 11.3.12 | MIT | Debug builds only (excluded from Release) |
| Avalonia.BuildServices | 11.3.2 | MIT | build time only (Avalonia's anonymous build telemetry; opt out with `AVALONIA_TELEMETRY_OPTOUT=1`) |
| Tesseract OCR (if installed) | — | Apache-2.0 | the maintainer's in-game test scripts (not in the public repository) |
| ooz (parts, ported to C# in `tools/wt-oodle/OodleLz.cs`) | 2016 (github.com/powzix/ooz) | GPL-3.0-or-later, Copyright (C) 2016 Powzix | the maintainer's War Thunder Oodle helper `tools/wt-oodle` (a separate program the engine starts; builds the War Thunder picture packs) and its unit tests |
| Python 3 with Pillow (if installed) | — | PSF License; Pillow: MIT-CMU (HPND) | helper scripts in `tools/` and `branding/` (run by hand, never shipped) |
| Windows PowerShell 5.1 | — | part of Windows | `engine/tests` smoke test, `tools/*.ps1` |
| SimConnect (part of Microsoft Flight Simulator) | — | Microsoft; not redistributed | the maintainer's in-game test tools load the `SimConnect.dll` of the user's MSFS install (not in the public repository) |

## 6. Compatibility with JoyPro's license (GPL-3.0-or-later)

JoyPro is licensed under GPL-3.0-or-later. Checked on 2026-10-12 for every NuGet package of every project in
`JoyPro.sln` (`engine`, `JoyPro.Gui`, `JoyPro.Gui.Tests`, `JoyPro.Ffb.Core`, `JoyPro.Ffb.Core.Tests`, `JoyPro.Ffb`,
`JoyPro.Watch`, `JoyPro.Tray`, `JoyPro.Logging`, `JoyPro.VJoy`), the DLLs in `engine/lib`, the icons and fonts, the
downloads and the tools used by the tests:

| Component | License | With GPL-3.0-or-later |
|---|---|---|
| Avalonia, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Mvvm, MicroCom.Runtime, Tmds.DBus.Protocol, System.IO.Pipelines, .NET runtime and libraries, System.Buffers/Memory/Runtime.CompilerServices.Unsafe/Threading.Tasks.Extensions, ZstdSharp, Nefarius.ViGEm.Client | MIT | compatible (permissive) |
| ANGLE, Skia, Zstandard (used under its BSD option), Dagor Engine format constants | BSD-3-Clause | compatible (permissive) |
| HarfBuzz | Old MIT | compatible (permissive) |
| Material Design Icons path data | Pictogrammers Free License / Apache-2.0 | compatible with GPLv3 (Apache-2.0 is compatible with GPLv3, not with GPLv2; JoyPro uses v3 or later) |
| Fonts | none shipped (Windows system fonts by name) | not applicable |
| .NET Framework 4.8, DirectInput, Windows Desktop (WinForms) | Windows components | GPLv3 "System Libraries"; nothing is shipped |
| Lua | no Lua runtime is shipped or linked; DCS World's own `luae.exe` runs as a separate process from the user's install | not applicable (separate program) |
| vJoy `vJoyInterface.dll` (user's install) | MIT | compatible |
| HidHide CLI, ViGEmBus driver (user's install, separate programs / driver) | MIT / BSD-3-Clause | compatible; not linked |
| Oodle `oo2core_*_win64.dll` (another game's copy on the user's PC) | proprietary | not distributed; loading it is allowed by the additional permission in `NOTICE.md` (GPL-3.0 section 7) |
| llama.cpp `llama-server` (download on consent, separate process) | MIT | compatible; separate program |
| NVIDIA CUDA runtime DLLs (download on consent, loaded by `llama-server`, not by JoyPro) | NVIDIA CUDA Toolkit EULA | not JoyPro's concern: separate program, never shipped |
| Qwen model weights (download on consent) | Apache-2.0 | data for a separate program; never shipped |
| xunit, Microsoft.NET.Test.Sdk, coverlet.collector, Newtonsoft.Json, Avalonia.Diagnostics, Avalonia.BuildServices | Apache-2.0 / MIT | development only; compatible anyway |
| PowerShell, Python, Pillow, Tesseract | various | tools that run the tests and scripts; not linked, not shipped |

Result: nothing is incompatible. JoyPro's binaries may be distributed under GPL-3.0-or-later together with the
notices in this file.

## 7. Keeping this file correct

- Adding or upgrading a NuGet package: run `dotnet list <project> package --include-transitive` for the changed
  project and update this file, `gui/JoyPro.Gui/Resources/ThirdPartyNotices.txt` and the notice file list in
  `tools/publish_release.ps1`. Only take packages whose license is compatible with GPL-3.0-or-later (MIT, BSD,
  Apache-2.0, LGPL, GPL-3.0); never GPL-2.0-only code.
- A new download (model, runtime, tool): put its license in `EngineOnly_Runtime/DB/Llm/models.json` (or the
  downloader) so the consent text shows it, and add it to section 2.
- A new picture or icon: only own work, Pictogrammers MDI, or something with a written license; note it here.
- A new game: build its command list on the user's PC from the user's installation; never ship extracted game files.
