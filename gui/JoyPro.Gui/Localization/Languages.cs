using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Gui.Localization;

// One language JoyPro's GUI can show (docs/localization.md). Code: the BCP 47 tag and the file name of its strings
// (Localization/Strings/<code>.json); NativeName: what the language picker shows; Fallback: the language a missing
// string comes from before English (Montenegrin from Serbian Latin, for example).
public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, PluralRule Plural, string? Fallback = null)
{
    public override string ToString() => NativeName == EnglishName ? NativeName : NativeName + " (" + EnglishName + ")";
}

// The plural forms of a language (CLDR cardinal rules for whole numbers; the number of forms a translation lists).
public enum PluralRule
{
    // Japanese, Korean, Chinese: one form.
    None,
    // English, German, ...: n == 1 | other.
    OneOther,
    // French: 0 and 1 | other.
    ZeroOneOther,
    // Icelandic, Macedonian: n % 10 == 1 and n % 100 != 11 | other.
    OneEndingOther,
    // Russian, Ukrainian, Belarusian, Serbian, Croatian, Bosnian: one | few (2-4) | other.
    EastSlavic,
    // Czech, Slovak: 1 | 2-4 | other.
    CzechSlovak,
    // Polish: 1 | 2-4 (not 12-14) | other.
    Polish,
    // Slovenian: n % 100 == 1 | == 2 | 3-4 | other.
    Slovenian,
    // Lithuanian: one | few | other.
    Lithuanian,
    // Latvian: zero | one | other.
    Latvian,
    // Romanian: 1 | 0 and 2-19 | other.
    Romanian,
    // Irish: 1 | 2 | 3-6 | 7-10 | other.
    Irish,
    // Maltese: 1 | 2 | 0, 3-10 | 11-19 | other.
    Maltese
}

public static class Languages
{
    // English first (the source text), then by English name.
    public static IReadOnlyList<LanguageInfo> All { get; } = BuildAll();

    private static IReadOnlyList<LanguageInfo> BuildAll()
    {
        List<LanguageInfo> others =
        [
            new("bg", "Bulgarian", "Български", PluralRule.OneOther),
            new("hr", "Croatian", "Hrvatski", PluralRule.EastSlavic),
            new("cs", "Czech", "Čeština", PluralRule.CzechSlovak),
            new("da", "Danish", "Dansk", PluralRule.OneOther),
            new("nl", "Dutch", "Nederlands", PluralRule.OneOther),
            new("et", "Estonian", "Eesti", PluralRule.OneOther),
            new("fi", "Finnish", "Suomi", PluralRule.OneOther),
            new("fr", "French", "Français", PluralRule.ZeroOneOther),
            new("de", "German", "Deutsch", PluralRule.OneOther),
            new("el", "Greek", "Ελληνικά", PluralRule.OneOther),
            new("hu", "Hungarian", "Magyar", PluralRule.OneOther),
            new("ga", "Irish", "Gaeilge", PluralRule.Irish),
            new("it", "Italian", "Italiano", PluralRule.OneOther),
            new("lv", "Latvian", "Latviešu", PluralRule.Latvian),
            new("lt", "Lithuanian", "Lietuvių", PluralRule.Lithuanian),
            new("mt", "Maltese", "Malti", PluralRule.Maltese),
            new("pl", "Polish", "Polski", PluralRule.Polish),
            new("pt", "Portuguese", "Português", PluralRule.OneOther),
            new("ro", "Romanian", "Română", PluralRule.Romanian),
            new("sk", "Slovak", "Slovenčina", PluralRule.CzechSlovak),
            new("sl", "Slovenian", "Slovenščina", PluralRule.Slovenian),
            new("es", "Spanish", "Español", PluralRule.OneOther),
            new("sv", "Swedish", "Svenska", PluralRule.OneOther),
            new("nb", "Norwegian", "Norsk bokmål", PluralRule.OneOther),
            new("is", "Icelandic", "Íslenska", PluralRule.OneEndingOther),
            new("sq", "Albanian", "Shqip", PluralRule.OneOther),
            new("bs", "Bosnian", "Bosanski", PluralRule.EastSlavic, "hr"),
            new("sr-Latn", "Serbian (Latin)", "Srpski (latinica)", PluralRule.EastSlavic, "hr"),
            new("sr-Cyrl", "Serbian (Cyrillic)", "Српски (ћирилица)", PluralRule.EastSlavic, "sr-Latn"),
            new("mk", "Macedonian", "Македонски", PluralRule.OneEndingOther),
            new("ru", "Russian", "Русский", PluralRule.EastSlavic),
            new("uk", "Ukrainian", "Українська", PluralRule.EastSlavic),
            new("be", "Belarusian", "Беларуская", PluralRule.EastSlavic, "ru"),
            new("tr", "Turkish", "Türkçe", PluralRule.OneOther),
            new("ja", "Japanese", "日本語", PluralRule.None),
            new("ko", "Korean", "한국어", PluralRule.None),
            new("zh-Hans", "Chinese (Simplified)", "简体中文", PluralRule.None),
            new("zh-Hant", "Chinese (Traditional)", "繁體中文", PluralRule.None, "zh-Hans")
        ];
        List<LanguageInfo> all = [new("en", "English", "English", PluralRule.OneOther)];
        all.AddRange(others.OrderBy(l => l.EnglishName, StringComparer.Ordinal));
        return all;
    }

    public static LanguageInfo English => All[0];

    public static LanguageInfo? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        return All.FirstOrDefault(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // The language of a Windows display language (CultureInfo.CurrentUICulture): the exact tag, the script variant
    // (sr-Latn-RS -> sr-Latn, zh-TW -> zh-Hant), the neutral language, a close relative (Norwegian Nynorsk, Montenegrin),
    // or English.
    public static LanguageInfo ForCulture(CultureInfo? culture)
    {
        for (CultureInfo? c = culture; c != null && !string.IsNullOrEmpty(c.Name); c = c.Parent == c ? null : c.Parent)
        {
            LanguageInfo? found = ForCultureName(c.Name);
            if (found != null)
                return found;
        }
        return English;
    }

    public static LanguageInfo? ForCultureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string n = name.Trim().Replace('_', '-');
        string lower = n.ToLowerInvariant();
        string[] parts = lower.Split('-');
        string lang = parts[0];
        bool Has(string part) => parts.Skip(1).Contains(part);
        switch (lang)
        {
            case "zh":
                // Traditional: the script tag, or Taiwan, Hong Kong, Macao.
                return Find(Has("hant") || Has("tw") || Has("hk") || Has("mo") ? "zh-Hant" : "zh-Hans");
            case "sr":
                // Windows' "sr" without a script and Serbian Cyrillic: Cyrillic; sr-Latn-*: Latin.
                return Find(Has("latn") ? "sr-Latn" : "sr-Cyrl");
            case "cnr":
                return Find("sr-Latn");
            case "no":
            case "nn":
            case "nb":
                return Find("nb");
            default:
                return Find(n) ?? Find(lang);
        }
    }

    // The plural form (index into a translation's forms) of a count, CLDR cardinal rules for whole numbers.
    public static int PluralIndex(PluralRule rule, long count)
    {
        long n = Math.Abs(count);
        long n10 = n % 10, n100 = n % 100;
        return rule switch
        {
            PluralRule.None => 0,
            PluralRule.OneOther => n == 1 ? 0 : 1,
            PluralRule.ZeroOneOther => n <= 1 ? 0 : 1,
            PluralRule.OneEndingOther => n10 == 1 && n100 != 11 ? 0 : 1,
            PluralRule.EastSlavic => n10 == 1 && n100 != 11 ? 0 : n10 is >= 2 and <= 4 && n100 is < 12 or > 14 ? 1 : 2,
            PluralRule.CzechSlovak => n == 1 ? 0 : n is >= 2 and <= 4 ? 1 : 2,
            PluralRule.Polish => n == 1 ? 0 : n10 is >= 2 and <= 4 && n100 is < 12 or > 14 ? 1 : 2,
            PluralRule.Slovenian => n100 == 1 ? 0 : n100 == 2 ? 1 : n100 is 3 or 4 ? 2 : 3,
            PluralRule.Lithuanian => n10 == 1 && n100 is < 11 or > 19 ? 0 : n10 >= 2 && n100 is < 11 or > 19 ? 1 : 2,
            PluralRule.Latvian => n10 == 0 || n100 is >= 11 and <= 19 ? 0 : n10 == 1 ? 1 : 2,
            PluralRule.Romanian => n == 1 ? 0 : n == 0 || n100 is >= 2 and <= 19 ? 1 : 2,
            PluralRule.Irish => n == 1 ? 0 : n == 2 ? 1 : n is >= 3 and <= 6 ? 2 : n is >= 7 and <= 10 ? 3 : 4,
            PluralRule.Maltese => n == 1 ? 0 : n == 2 ? 1 : n == 0 || n100 is >= 3 and <= 10 ? 2 : n100 is >= 11 and <= 19 ? 3 : 4,
            _ => n == 1 ? 0 : 1
        };
    }

    // The UI font stack of a language: Bahnschrift (JoyPro's font; Latin, Greek, Cyrillic), then Segoe UI, then the
    // Windows CJK UI font of that language first (Han characters look different in Japanese, Chinese and Korean).
    public static string FontStack(LanguageInfo language)
    {
        string cjk = language.Code switch
        {
            "ja" => "Yu Gothic UI, Meiryo UI, Microsoft YaHei UI, Malgun Gothic",
            "zh-Hans" => "Microsoft YaHei UI, Microsoft JhengHei UI, Yu Gothic UI, Malgun Gothic",
            "zh-Hant" => "Microsoft JhengHei UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic",
            "ko" => "Malgun Gothic, Yu Gothic UI, Microsoft YaHei UI",
            _ => "Yu Gothic UI, Microsoft YaHei UI, Malgun Gothic"
        };
        return "Bahnschrift, Segoe UI, " + cjk;
    }

    // How many forms a translation of a plural text lists in this language.
    public static int PluralCount(PluralRule rule) => rule switch
    {
        PluralRule.None => 1,
        PluralRule.EastSlavic or PluralRule.CzechSlovak or PluralRule.Polish or PluralRule.Lithuanian or PluralRule.Latvian or PluralRule.Romanian => 3,
        PluralRule.Slovenian => 4,
        PluralRule.Irish or PluralRule.Maltese => 5,
        _ => 2
    };
}
