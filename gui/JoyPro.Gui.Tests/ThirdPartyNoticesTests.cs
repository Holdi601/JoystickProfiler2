using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// The third-party notices embedded into JoyPro.Gui.dll for an About / Licenses view (THIRD_PARTY_NOTICES.md).
public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void EmbeddedNoticesNameEveryShippedComponent()
    {
        string text = ThirdPartyNotices.Text;
        Assert.DoesNotContain("missing from this build", text);
        foreach (string part in new[] { "Avalonia", "ANGLE", "SkiaSharp", "HarfBuzz", "CommunityToolkit.Mvvm", "MicroCom", "Tmds.DBus",
                                        ".NET", "ZstdSharp", "Zstandard", "Material Design Icons", "llama.cpp", "Qwen", "vJoy", "HidHide", "ViGEm",
                                        "MIT License", "BSD 3-Clause", "Apache License 2.0", "not affiliated",
                                        "GPL-3.0-or-later", "WITHOUT ANY WARRANTY" })
            Assert.Contains(part, text);
    }
}
