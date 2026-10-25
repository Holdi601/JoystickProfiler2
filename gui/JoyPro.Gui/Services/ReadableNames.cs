using System;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.Gui.Localization;
using System.Linq;

namespace JoyPro.Gui.Services;

// Inputs and controls in plain words for the Auto-Assign, Setup wizard and Lay out texts: never an input token
// ("JOY_BTN_POV1_D") or a control id ("stick.hat.tms.press"), always "Hat 1 down", "TMS hat press" or the printed label
// (engine/DeviceReference/DeviceVocabulary.cs). The input words go through the translations ("Button {0}", "Hat {0} down").
public static class ReadableNames
{
    // "JOY_BTN25" -> "Button 25", "JOY_BTN_POV1_D" -> "Hat 1 down", "JOY_X" -> "X axis"; a keyboard key stays.
    public static string Input(string? input) => Loc.Translate(DeviceVocabulary.InputName(input ?? string.Empty));

    // The printed label when there is one, else the control in plain words ("TMS hat press", "Base button 25") in the
    // app's language: the part ("TMS hat", "Base toggle {0}") and its direction ("press") each through the translations.
    public static string Control(string? control, string? label = null)
    {
        string text = (label ?? string.Empty).Trim();
        if (text.Length > 0)
            return text;
        string c = (control ?? string.Empty).Trim();
        if (c.Length == 0)
            return string.Empty;
        string english = DeviceVocabulary.Describe(c, string.Empty);
        if (Loc.Current == Languages.English)
            return english;
        return Vocabulary(english);
    }

    // A control name of DeviceVocabulary ("Base toggle 1 down", "Trigger first stage", "Stick roll") in the app's language:
    // the whole name when the translations know it, else its part and its direction word.
    // Only names of the vocabulary (Models/EngineTexts.cs) are translated: a catalog label ("right side of right lever")
    // stays as printed.
    public static string Vocabulary(string english)
    {
        string text = english ?? string.Empty;
        if (IsName(text))
            return Loc.Translate(text);
        foreach (string direction in Directions)
        {
            if (text.Length > direction.Length + 1 && text.EndsWith(" " + direction, StringComparison.Ordinal))
            {
                string part = text.Substring(0, text.Length - direction.Length - 1);
                return IsName(part) ? Loc.Translate(part) + " " + Loc.Translate(direction) : text;
            }
        }
        Match position = Regex.Match(text, @"^(.+) position (\d+)$");
        if (position.Success && IsName(position.Groups[1].Value))
            return Loc.Translate(position.Groups[1].Value) + " " + Loc.Translate("position " + position.Groups[2].Value);
        return text;
    }

    private static readonly Regex[] Names = JoyPro.Gui.Models.EngineTexts.VocabularyNames
        .Select(n => new Regex("^" + Regex.Escape(n).Replace(@"\{0}", @"\d+") + "$", RegexOptions.Compiled)).ToArray();

    private static bool IsName(string text) => Names.Any(r => r.IsMatch(text));

    // DeviceVocabulary.DirectionName's words, longest first.
    private static readonly string[] Directions = JoyPro.Gui.Models.EngineTexts.VocabularyDirections
        .Where(d => d.IndexOf('{') < 0).OrderByDescending(d => d.Length).ToArray();

    // "Hat 1 down (TMS hat down)": the input, and its control when that says more.
    public static string Where(string? input, string? control, string? label = null)
    {
        string name = Input(input);
        string part = Control(control, label);
        if (part.Length == 0 || string.Equals(part, DeviceVocabulary.InputName(input ?? string.Empty), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, name, StringComparison.OrdinalIgnoreCase))
            return name;
        return name.Length == 0 ? part : name + " (" + part + ")";
    }

    // A device name without its instance id ("{...}").
    public static string Device(string? device) => Regex.Replace(device ?? string.Empty, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty).Trim();
}
