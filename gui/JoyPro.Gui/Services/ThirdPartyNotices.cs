using System.IO;
using System.Reflection;

namespace JoyPro.Gui.Services;

// The third-party notices for an About / Licenses view: Resources\ThirdPartyNotices.txt, embedded into JoyPro.Gui.dll
// (a short form of THIRD_PARTY_NOTICES.md in the repository root; the release package also carries the full file and
// the license texts in <install>\licenses). Keep both files in step when a package or download source changes.
public static class ThirdPartyNotices
{
    public const string ResourceName = "JoyPro.Gui.ThirdPartyNotices.txt";

    private static string? text;

    public static string Text => text ??= Load();

    private static string Load()
    {
        using Stream? stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
            return "The third-party notices are missing from this build. See THIRD_PARTY_NOTICES.md in the JoyPro folder.";
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
