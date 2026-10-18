using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using JoyPro.Gui.Localization;
using Xunit;

namespace JoyPro.Gui.Tests;

// Every character of every language's strings (and every language's own name, as the language picker shows them all)
// must have a glyph in the font chains JoyPro draws that language with, so no language shows boxes ("tofu"): the pages
// and the HUD (Languages.FontStack), the callout sheets and the manual pages (their own font + Languages.FallbackFonts).
// The canvases that draw with the default font rely on Windows' fallback: checked through Avalonia's own match.
// JOYPRO_FONT_REPORT=<file> writes which fonts each language needs.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FontCoverageTests
{
    [Fact]
    public void EveryLanguage_EveryCharacter_HasAGlyphInItsFontChains()
    {
        List<string> problems = [];
        StringBuilder report = new();
        HashSet<int> allNames = [.. Languages.All.SelectMany(l => Codepoints(l.NativeName))];
        AvaloniaRenderThread.Run(() =>
        {
            foreach (LanguageInfo language in Languages.All)
            {
                HashSet<int> chars = CharactersOf(language);
                chars.UnionWith(allNames);
                chars.RemoveWhere(cp => cp < 0x20 || cp is 0x200B or 0x200C or 0x200D or 0xFEFF || (cp <= 0xFFFF && char.IsWhiteSpace((char)cp)));
                string[] stacks =
                [
                    Languages.FontStack(language),
                    "Bahnschrift SemiCondensed, Bahnschrift, Segoe UI, " + Languages.FallbackFonts(language),
                    "Segoe UI, Inter, Arial, " + Languages.FallbackFonts(language),
                ];
                foreach (string stack in stacks)
                {
                    List<IGlyphTypeface> chain = Chain(stack);
                    Assert.True(chain.Count > 0, "no font of '" + stack + "' is installed");
                    List<int> missing = chars.Where(cp => !chain.Any(t => Has(t, cp))).OrderBy(cp => cp).ToList();
                    if (missing.Count > 0)
                        problems.Add(language.Code + " [" + stack + "]: no glyph for " + Describe(missing));
                    if (stack == stacks[0])
                    {
                        Dictionary<string, int> used = chars.GroupBy(cp => chain.FirstOrDefault(t => Has(t, cp))?.FamilyName ?? "(none)").ToDictionary(g => g.Key, g => g.Count());
                        report.AppendLine(language.Code + ": " + chars.Count + " characters: " + string.Join(", ", used.Select(u => u.Key + " " + u.Value)));
                    }
                }
                // The default font of the drawn canvases (curves, graphs, badges): Avalonia finds a font for each.
                CultureInfo culture = CultureInfo.GetCultureInfo(language.Code == "zh-Hans" ? "zh-CN" : language.Code == "zh-Hant" ? "zh-TW" : language.Code == "sr-Latn" ? "sr-Latn-RS" : language.Code == "sr-Cyrl" ? "sr-Cyrl-RS" : language.Code);
                List<int> unmatched = chars.Where(cp => !FontManager.Current.TryMatchCharacter(cp, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, FontFamily.Default, culture, out _)).ToList();
                if (unmatched.Count > 0)
                    problems.Add(language.Code + " [default font]: no glyph for " + Describe(unmatched));
            }
        });
        string? outFile = Environment.GetEnvironmentVariable("JOYPRO_FONT_REPORT");
        if (!string.IsNullOrWhiteSpace(outFile))
            File.WriteAllText(outFile, report + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void FontStack_PutsTheLanguagesCjkFontFirst_AndEndsWithTheSymbolFonts()
    {
        Assert.StartsWith("Bahnschrift, Segoe UI, Yu Gothic UI", Languages.FontStack(Languages.All.Single(l => l.Code == "ja")));
        Assert.StartsWith("Bahnschrift, Segoe UI, Microsoft JhengHei UI", Languages.FontStack(Languages.All.Single(l => l.Code == "zh-Hant")));
        Assert.StartsWith("Bahnschrift, Segoe UI, Malgun Gothic", Languages.FontStack(Languages.All.Single(l => l.Code == "ko")));
        foreach (LanguageInfo language in Languages.All)
            Assert.EndsWith("Segoe UI Symbol, Segoe UI Emoji", Languages.FontStack(language));
    }

    private static string Describe(List<int> codepoints) =>
        string.Join(" ", codepoints.Take(40).Select(c => "U+" + c.ToString("X4", CultureInfo.InvariantCulture) + " '" + char.ConvertFromUtf32(c) + "'")) +
        (codepoints.Count > 40 ? " (+" + (codepoints.Count - 40) + ")" : string.Empty);

    // The installed families of a font stack, in order (a name Windows does not have is left out).
    internal static List<IGlyphTypeface> Chain(string stack)
    {
        List<IGlyphTypeface> chain = [];
        foreach (string name in stack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(name)), out IGlyphTypeface? typeface) &&
                string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                chain.Add(typeface);
        }
        return chain;
    }

    internal static bool Has(IGlyphTypeface typeface, int codepoint) => typeface.TryGetGlyph((uint)codepoint, out ushort glyph) && glyph != 0;

    private static HashSet<int> CharactersOf(LanguageInfo language)
    {
        HashSet<int> chars = [];
        string strings = Path.Combine(RepoRoot(), "gui", "JoyPro.Gui", "Localization", "Strings");
        // English is the keys: every key of one file (German has them all).
        string file = Path.Combine(strings, (language.Code == "en" ? "de" : language.Code) + ".json");
        if (!File.Exists(file))
            return chars;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
        {
            if (language.Code == "en")
            {
                chars.UnionWith(Codepoints(entry.Name));
                continue;
            }
            if (entry.Value.ValueKind == JsonValueKind.String)
                chars.UnionWith(Codepoints(entry.Value.GetString() ?? string.Empty));
            else if (entry.Value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement form in entry.Value.EnumerateArray())
                    chars.UnionWith(Codepoints(form.GetString() ?? string.Empty));
        }
        return chars;
    }

    internal static IEnumerable<int> Codepoints(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]) && !(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])))
                continue;
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
                i++;
            yield return cp;
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
