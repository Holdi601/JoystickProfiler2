using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Services.Components;

// How an installer takes its unattended switches and reports its result.
public enum InstallerKind
{
    // Inno Setup (vJoy): /SILENT = progress window only, no questions; exit 0 ok, 2/5 cancelled, 8 restart first.
    InnoSetup,

    // Advanced Installer EXE around an MSI (HidHide, ViGEmBus): MSI switches pass through; exit 0 ok, 3010/1641 restart,
    // 1602 cancelled, 1618 another setup running.
    AdvancedInstallerMsi
}

// One optional third-party component JoyPro can download and install on request (docs/components.md). Everything is
// pinned: the exact upstream release asset, its size and SHA-256, and the organization in the installer's Authenticode
// signature. A newer upstream release is offered only after it was checked and pinned here (JoyPro update).
public sealed record ComponentSpec
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    // Plain language for the Components card: what it is, and why JoyPro needs it.
    public required string WhatItIs { get; init; }

    public required string WhyJoyProNeedsIt { get; init; }

    // Where in JoyPro it is used ("Virtual Joystick page: vJoy mode").
    public required string UsedBy { get; init; }

    public required string Version { get; init; }

    public required string DownloadUrl { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    // O= of the installer's signing certificate; anything else is refused.
    public required string SignerOrganization { get; init; }

    public required string ProjectUrl { get; init; }

    public required string ReleasePageUrl { get; init; }

    public required string Author { get; init; }

    public required string LicenseName { get; init; }

    public required string LicenseUrl { get; init; }

    public required string LicenseText { get; init; }

    public required InstallerKind Kind { get; init; }

    public required string InstallerArguments { get; init; }

    public required bool IsDriver { get; init; }

    // Detection: a DisplayName in Windows' installed-apps list (contains, case-insensitive), the Windows service of the
    // driver, and optionally a file inside the install folder that must exist.
    public required string DisplayNameContains { get; init; }

    public required string ServiceName { get; init; }

    public string RequiredFile { get; init; } = string.Empty;

    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? T("{0} MB", (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
            : T("{0} KB", Math.Max(1, bytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public static class ComponentCatalog
{
    public const string VJoyId = "vjoy";
    public const string HidHideId = "hidhide";
    public const string ViGEmBusId = "vigembus";

    private const string MitLicense = """
        Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
        documentation files (the "Software"), to deal in the Software without restriction, including without limitation
        the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
        to permit persons to whom the Software is furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all copies or substantial portions of
        the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
        THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
        CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
        IN THE SOFTWARE.
        """;

    private const string Bsd3License = """
        Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
        following conditions are met:

        1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
           following disclaimer.

        2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
           following disclaimer in the documentation and/or other materials provided with the distribution.

        3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
           products derived from this software without specific prior written permission.

        THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
        WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
        PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
        INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
        SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
        THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
        IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
        """;

    // vJoy: jshafer817/vJoy v2.1.9.1, the build JoyPro.VJoy is written and verified against (JOYSTICK_POSITION_V2).
    // The newer BrunnerInnovation/vJoy v2.2.2.0 (Win10/11 only) switched vJoyInterface.dll to JOYSTICK_POSITION_V3
    // (16 bytes longer), which JoyPro's forwarder does not write yet - see docs/components.md.
    public static ComponentSpec VJoy { get; } = new()
    {
        Id = VJoyId,
        Name = "vJoy",
        WhatItIs = "A free driver that adds virtual joysticks to Windows. Games see them like a real stick.",
        WhyJoyProNeedsIt = "For games set to \"vJoy\" on the Virtual Joystick page: JoyPro copies the inputs of all your sticks, throttles and panels onto one virtual joystick, so the game sees one device whose buttons never move.",
        UsedBy = "Virtual Joystick page (games set to vJoy)",
        Version = "2.1.9.1",
        DownloadUrl = "https://github.com/jshafer817/vJoy/releases/download/v2.1.9.1/vJoySetup.exe",
        FileName = "vJoySetup_2.1.9.1.exe",
        SizeBytes = 11482272,
        Sha256 = "f103ced4e7ff7ccb49c8415a542c56768ed4da4fea252b8f4ffdac343074654a",
        SignerOrganization = "On-site Dental Systems (Justin Shafer)",
        ProjectUrl = "https://github.com/jshafer817/vJoy",
        ReleasePageUrl = "https://github.com/jshafer817/vJoy/releases/tag/v2.1.9.1",
        Author = "Shaul Eizikovich; Windows 10 build signed by Justin Shafer",
        LicenseName = "MIT",
        LicenseUrl = "https://github.com/jshafer817/vJoy/blob/master/LICENSE.txt",
        LicenseText = "MIT License\n\nCopyright (c) 2017 Shaul Eizikovich\n\n" + MitLicense,
        Kind = InstallerKind.InnoSetup,
        InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        IsDriver = true,
        DisplayNameContains = "vJoy",
        ServiceName = "vjoy",
        RequiredFile = @"x64\vJoyInterface.dll"
    };

    public static ComponentSpec HidHide { get; } = new()
    {
        Id = HidHideId,
        Name = "HidHide",
        WhatItIs = "A free driver by Nefarius that hides chosen game controllers from chosen programs.",
        WhyJoyProNeedsIt = "With a virtual joystick or Xbox pad the game must not see your real devices as well (that gives double input). JoyPro hides them from that game only, and only while JoyPro's virtual joystick runs; every other program keeps seeing them.",
        UsedBy = "Virtual Joystick page (\"Hide the real devices\")",
        Version = "1.5.230",
        DownloadUrl = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe",
        FileName = "HidHide_1.5.230_x64.exe",
        SizeBytes = 8078016,
        Sha256 = "f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6",
        SignerOrganization = "Nefarius Software Solutions e.U.",
        ProjectUrl = "https://github.com/nefarius/HidHide",
        ReleasePageUrl = "https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0",
        Author = "Nefarius Software Solutions e.U. (Benjamin Hoeglinger-Stelzer), Eric Korff de Gidts",
        LicenseName = "MIT",
        LicenseUrl = "https://github.com/nefarius/HidHide/blob/master/LICENSE",
        LicenseText = "MIT License\n\nCopyright (c) 2020 Eric Korff de Gidts\nCopyright (c) 2021-2024 Benjamin Hoeglinger-Stelzer\n\n" + MitLicense,
        Kind = InstallerKind.AdvancedInstallerMsi,
        InstallerArguments = "/passive /norestart",
        IsDriver = true,
        DisplayNameContains = "HidHide",
        ServiceName = "HidHide"
    };

    public static ComponentSpec ViGEmBus { get; } = new()
    {
        Id = ViGEmBusId,
        Name = "ViGEmBus",
        WhatItIs = "A free driver by Nefarius that adds a virtual Xbox 360 controller to Windows.",
        WhyJoyProNeedsIt = "For games set to \"Xbox pad\" on the Virtual Joystick page (games that only read Xbox controllers, such as Hell Let Loose: Vietnam): JoyPro drives the virtual pad's sticks, triggers and buttons with your HOTAS.",
        UsedBy = "Virtual Joystick page (games set to Xbox pad)",
        Version = "1.22.0",
        DownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe",
        FileName = "ViGEmBus_1.22.0_x64_x86_arm64.exe",
        SizeBytes = 6278576,
        Sha256 = "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a",
        SignerOrganization = "Nefarius Software Solutions e.U.",
        ProjectUrl = "https://github.com/nefarius/ViGEmBus",
        ReleasePageUrl = "https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0",
        Author = "Nefarius Software Solutions e.U. (the project is retired: 1.22.0 is its last release)",
        LicenseName = "BSD 3-Clause",
        LicenseUrl = "https://github.com/nefarius/ViGEmBus/blob/master/LICENSE",
        LicenseText = "BSD 3-Clause License\n\nCopyright (c) 2016-2020, Nefarius Software Solutions e.U.\nAll rights reserved.\n\n" + Bsd3License,
        Kind = InstallerKind.AdvancedInstallerMsi,
        InstallerArguments = "/passive /norestart",
        IsDriver = true,
        DisplayNameContains = "ViGEm Bus",
        ServiceName = "ViGEmBus"
    };

    public static IReadOnlyList<ComponentSpec> All { get; } = [VJoy, HidHide, ViGEmBus];

    public static ComponentSpec? Find(string id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
