using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Services;

// The engine's English sentences of Auto-Assign, the Setup wizard and Lay out with AI in the app's language: the whole
// sentence when the translations know it (a pattern with {0} counts), else its clauses one by one (split at "; ", " | ",
// " => " and the first ": ", never inside parentheses or quotes), and the input names in it ("Button 5", "Hat 1 down").
// The English texts are listed for the translations in Models/EngineTexts.cs.
public static class EngineText
{
    private static readonly string[] Separators = ["; ", " | ", " => ", ": "];

    // Only the input names of a text with names in it (a device and its control: "Throttle - HOTAS Warthog Button 15
    // (LTB)"): no sentence pattern may take it apart.
    public static string InputsOnly(string? text) => Loc.Current == Languages.English ? text ?? string.Empty : Inputs(text ?? string.Empty);

    public static string Tr(string? english)
    {
        string text = english ?? string.Empty;
        if (text.Length == 0 || Loc.Current == Languages.English)
            return text;
        return Inputs(Clauses(text, 0));
    }

    private static string Clauses(string text, int depth)
    {
        // A placeholder never takes a whole clause ("{0} functions placed ..." must not swallow "... games; 226").
        // Short generic patterns ("{0} on {1}") do not take a sentence apart either.
        string whole = Loc.Translate(text, value => Separators.All(separator => Split(value, separator, false).Count == 1), 8);
        if (!string.Equals(whole, text, StringComparison.Ordinal) || depth > 6)
            return whole;
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return text;
        // Leading spaces (an indented line) stay.
        int indent = text.Length - text.TrimStart().Length;
        if (indent > 0)
            return text.Substring(0, indent) + Clauses(text.Substring(indent), depth + 1);
        foreach (string separator in Separators)
        {
            List<string> parts = Split(text, separator, separator == ": ");
            if (parts.Count < 2)
                continue;
            StringBuilder sb = new();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                    sb.Append(separator);
                sb.Append(Clauses(parts[i], depth + 1));
            }
            return sb.ToString();
        }
        // A list of control names ("Gear lever up, TMS hat up").
        List<string> names = Split(text, ", ", false);
        List<string> translated = names.Select(ReadableNames.Vocabulary).ToList();
        if (names.Count > 0 && translated.Where((t, i) => t != names[i]).Count() == names.Count)
            return string.Join(", ", translated);
        return text;
    }

    // The parts of text between separators outside parentheses and quotes (first: only at the first one).
    private static List<string> Split(string text, string separator, bool first)
    {
        List<string> parts = [];
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c == '(')
                depth++;
            else if (!quoted && c == ')' && depth > 0)
                depth--;
            else if (!quoted && depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(text.Substring(start, i - start));
                start = i + separator.Length;
                i = start - 1;
                if (first)
                    break;
            }
        }
        parts.Add(text.Substring(start));
        return parts;
    }

    private static readonly Regex InputWords = new(@"\b(?:Hat (?<hat>\d+) (?<dir>up-right|up-left|down-right|down-left|up|down|left|right)|Button (?<button>\d+)|Slider (?<slider>\d+)|(?<axis>[XYZ]) (?<kind>axis|rotation))\b", RegexOptions.Compiled);

    private static readonly Regex Parenthesized = new(@"\(([^()""]+)\)", RegexOptions.Compiled);

    // "Button 5", "Hat 1 down", "Z axis" inside a sentence in the app's language, and control names in parentheses
    // ("(TMS hat down)").
    private static string Inputs(string text) => Parenthesized.Replace(InputButtons(text), m =>
    {
        string inner = m.Groups[1].Value;
        string translated = ReadableNames.Vocabulary(inner);
        return translated == inner ? m.Value : "(" + translated + ")";
    });

    private static string InputButtons(string text) => InputWords.Replace(text, m =>
    {
        if (m.Groups["hat"].Success)
            return Loc.Translate("Hat " + m.Groups["hat"].Value + " " + m.Groups["dir"].Value);
        if (m.Groups["button"].Success)
            return Loc.Translate("Button " + m.Groups["button"].Value);
        if (m.Groups["slider"].Success)
            return Loc.Translate("Slider " + m.Groups["slider"].Value);
        return Loc.Translate(m.Groups["axis"].Value + " " + m.Groups["kind"].Value);
    });
}
