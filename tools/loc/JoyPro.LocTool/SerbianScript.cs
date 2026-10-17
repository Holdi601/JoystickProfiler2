using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.LocTool;

// Serbian Latin -> Cyrillic (the two scripts map one to one; lj, nj, dž are one letter each). Words that are not
// Serbian stay in Latin letters: placeholders ({0}), names and abbreviations JoyPro keeps (DCS, JoyPro, vJoy, HOTAS,
// any word with an upper-case letter after the first or a digit), file names, paths and web addresses.
public static class SerbianScript
{
    private static readonly Dictionary<string, string> Digraphs = new(StringComparer.Ordinal)
    {
        ["Lj"] = "Љ", ["LJ"] = "Љ", ["lj"] = "љ", ["Nj"] = "Њ", ["NJ"] = "Њ", ["nj"] = "њ",
        ["Dž"] = "Џ", ["DŽ"] = "Џ", ["dž"] = "џ"
    };

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['A'] = "А", ['B'] = "Б", ['V'] = "В", ['G'] = "Г", ['D'] = "Д", ['Đ'] = "Ђ", ['E'] = "Е", ['Ž'] = "Ж", ['Z'] = "З",
        ['I'] = "И", ['J'] = "Ј", ['K'] = "К", ['L'] = "Л", ['M'] = "М", ['N'] = "Н", ['O'] = "О", ['P'] = "П", ['R'] = "Р",
        ['S'] = "С", ['T'] = "Т", ['Ć'] = "Ћ", ['U'] = "У", ['F'] = "Ф", ['H'] = "Х", ['C'] = "Ц", ['Č'] = "Ч", ['Š'] = "Ш",
        ['a'] = "а", ['b'] = "б", ['v'] = "в", ['g'] = "г", ['d'] = "д", ['đ'] = "ђ", ['e'] = "е", ['ž'] = "ж", ['z'] = "з",
        ['i'] = "и", ['j'] = "ј", ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['r'] = "р",
        ['s'] = "с", ['t'] = "т", ['ć'] = "ћ", ['u'] = "у", ['f'] = "ф", ['h'] = "х", ['c'] = "ц", ['č'] = "ч", ['š'] = "ш"
    };

    // Words kept in Latin letters although they look Serbian-spellable.
    private static readonly HashSet<string> KeepLatin = new(StringComparer.Ordinal)
    {
        "JoyPro", "JoyPro2", "DCS", "IL", "vJoy", "HidHide", "ViGEmBus", "HOTAS", "Steam", "GitHub", "Windows", "Star", "Citizen",
        "War", "Thunder", "MSFS", "Arma", "Falcon", "BMS", "Nuclear", "Option", "Korea", "World", "Microsoft", "Flight", "Simulator",
        "Elite", "Dangerous", "Hugging", "Face", "Gaijin", "Lua", "Saved", "Games", "Documents", "Thrustmaster", "Warthog", "Virpil",
        "VKB", "Winwing", "Logitech", "Saitek", "Honeycomb", "TrackIR", "Oodle", "Hell", "Let", "Loose", "Vietnam", "WARDOGS",
        "Sturmovik", "Battle", "Stalingrad", "Great", "Battles", "Cliffs", "Dover", "Plane", "X", "Plane", "XPlane", "Ace", "Combat",
        "Elite:", "Tray", "Spitfire", "VID", "PID", "HID", "USB", "FFB", "DirectInput", "XInput", "AI", "PNG", "PDF", "CSV", "JSON",
        "Lua", "UAC", "IP", "CPU", "MB", "GB", "OK", "PC", "UI", "ID", "Escape", "Esc", "Ctrl", "Alt", "Shift", "Enter", "Tab"
    };

    private static readonly Regex Token = new(@"\{[^}]*\}|https?://\S+|[A-Za-z]:\\\S*|\S+\.(?:exe|dll|json|lua|xml|ini|cfg|png|jpg|txt|log|md|vromfs\.bin|bin)\b|[\p{L}\p{N}_'’-]+|.", RegexOptions.Compiled | RegexOptions.Singleline);

    public static string ToCyrillic(string latin)
    {
        StringBuilder sb = new(latin.Length);
        foreach (Match m in Token.Matches(latin))
        {
            string t = m.Value;
            if (t.Length == 0)
                continue;
            if (!char.IsLetter(t[0]) || KeepAsLatin(t))
            {
                sb.Append(t);
                continue;
            }
            sb.Append(Word(t));
        }
        return sb.ToString();
    }

    private static bool KeepAsLatin(string word)
    {
        if (KeepLatin.Contains(word))
            return true;
        // abbreviations, names with inner capitals (vJoy, HidHide), digits (F-16C, IL-2), letters Serbian does not have
        bool innerUpper = false;
        for (int i = 1; i < word.Length; i++)
            if (char.IsUpper(word[i]) && !(i == 1 && (word[0] is 'L' or 'N' or 'D') && word[1] is 'J' or 'Ž'))
                innerUpper = true;
        if (innerUpper || word.Length >= 2 && word.ToUpperInvariant() == word && word.Length <= 6)
            return true;
        foreach (char c in word)
        {
            if (char.IsDigit(c) || c is 'q' or 'w' or 'x' or 'y' or 'Q' or 'W' or 'X' or 'Y')
                return true;
        }
        // English words the translation kept (throttle, deadzone): letter pairs Serbian does not write
        string lower = word.ToLowerInvariant();
        foreach (string pair in new[] { "th", "ck", "oo", "ee", "ea", "ou", "gh" })
            if (lower.Contains(pair, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string Word(string word)
    {
        StringBuilder sb = new(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            if (i + 1 < word.Length && Digraphs.TryGetValue(word.Substring(i, 2), out string? digraph))
            {
                sb.Append(digraph);
                i++;
                continue;
            }
            sb.Append(Letters.TryGetValue(word[i], out string? letter) ? letter : word[i].ToString());
        }
        return sb.ToString();
    }
}
