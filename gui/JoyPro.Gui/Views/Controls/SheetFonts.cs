using System.Collections.Concurrent;
using Avalonia.Media;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Views.Controls;

// The fonts of the drawn sheets (callout tables, manual pages): their own first font, then the fallbacks of the
// language shown (Languages.FallbackFonts: its CJK fonts, the symbol fonts), one FontFamily per stack.
internal static class SheetFonts
{
    private static readonly ConcurrentDictionary<string, FontFamily> Cache = new();

    public static FontFamily Get(string ownFonts)
    {
        string stack = ownFonts + Languages.FallbackFonts(Loc.Current);
        return Cache.GetOrAdd(stack, s => new FontFamily(s));
    }
}
