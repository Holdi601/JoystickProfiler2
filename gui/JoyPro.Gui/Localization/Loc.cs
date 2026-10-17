using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JoyPro.Gui.Localization;

// The GUI's texts in the user's language (docs/localization.md). Gettext style: the English text is the key, a text
// without a translation stays English. XAML: Text="{l:T 'Bind here'}"; C#: T("Bind here"), T("{0} binds", n),
// Plural("{0} bind", "{0} binds", n). The strings of a language are Localization/Strings/<code>.json (embedded), a
// JSON object of English text -> translation (a list of forms for a plural text, keyed by its singular).
// Engine texts the GUI shows: Translate(text) also finds a translated pattern ("Exported {0} binds.") for a line.
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, LocText> Observables = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Table> Tables = new(StringComparer.OrdinalIgnoreCase);
    private static LanguageInfo current = Languages.English;
    private static string choice = string.Empty;
    private static List<Table> chain = [];
    private static List<PatternEntry>? patterns;

    // Tests and the language tool point this at the source folder; empty: the strings embedded in the GUI.
    public static string? StringsFolderOverride { get; set; }

    // Raised on the UI thread's caller after the language changed (the XAML texts follow on their own).
    public static event EventHandler? LanguageChanged;

    public static LanguageInfo Current => current;

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    // The language as chosen in Settings: a code, or empty for the Windows display language.
    public static string Choice => choice;

    // Empty or "system": the Windows display language (or English when JoyPro does not have it).
    public static void SetLanguage(string? code)
    {
        string wanted = (code ?? string.Empty).Trim();
        if (string.Equals(wanted, "system", StringComparison.OrdinalIgnoreCase))
            wanted = string.Empty;
        LanguageInfo language = wanted.Length == 0
            ? Languages.ForCulture(CultureInfo.CurrentUICulture)
            : Languages.Find(wanted) ?? Languages.ForCultureName(wanted) ?? Languages.English;
        bool changed;
        lock (Gate)
        {
            choice = wanted.Length == 0 ? string.Empty : language.Code;
            changed = !ReferenceEquals(language, current) || chain.Count == 0 && language != Languages.English;
            current = language;
            chain = BuildChain(language);
            patterns = null;
            Culture = TryCulture(language.Code);
        }
        if (!changed)
            return;
        foreach (LocText text in Observables.Values)
            text.Refresh();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    // Reloads the string files (the language tool after a merge, tests).
    public static void Reload()
    {
        lock (Gate)
        {
            Tables.Clear();
            chain = BuildChain(current);
            patterns = null;
        }
        foreach (LocText text in Observables.Values)
            text.Refresh();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    // Marks a text in a static table as translatable (gettext's N_): it stays English here and is translated where it
    // is shown, T(value). The string tool collects it like T("...").
    public static string N(string text) => text;

    // The translation of an English text (the text itself when there is none).
    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        List<Table> tables = chain;
        foreach (Table table in tables)
        {
            if (table.Strings.TryGetValue(text, out string[]? forms) && forms.Length > 0 && !string.IsNullOrEmpty(forms[0]))
                return forms[0];
        }
        return text;
    }

    // The translation of an English format text ("{0} binds on {1}"), filled in with the arguments. A translation whose
    // placeholders do not fit (a broken file) falls back to the English text.
    public static string T(string format, params object?[] args)
    {
        if (args == null || args.Length == 0)
            return T(format);
        return SafeFormat(T(format), format, args);
    }

    // A text JoyPro wrote in the language used before (a status line kept in a field): the same text in the language
    // now. A text that is no translation of the previous language is translated as English (or stays as it is).
    public static string Retranslate(string? text, LanguageInfo previous)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        if (!ReferenceEquals(previous, Languages.English))
        {
            List<Table> before;
            lock (Gate)
                before = BuildChain(previous);
            foreach (Table table in before)
            {
                foreach (KeyValuePair<string, string[]> pair in table.Strings)
                {
                    if (pair.Value.Length > 0 && string.Equals(pair.Value[0], text, StringComparison.Ordinal))
                        return T(pair.Key);
                }
            }
        }
        return T(text);
    }

    // A text with a count: the language's plural form of the count. args: the format's arguments ({0} is the count
    // when none are given). English: singular for 1, plural otherwise.
    public static string Plural(string singular, string plural, long count, params object?[] args)
    {
        object?[] values = args == null || args.Length == 0 ? [count] : args;
        List<Table> tables = chain;
        foreach (Table table in tables)
        {
            if (table.Strings.TryGetValue(singular, out string[]? forms) && forms.Length > 0)
            {
                int index = Math.Min(Languages.PluralIndex(table.Language.Plural, count), forms.Length - 1);
                string form = forms[index];
                if (!string.IsNullOrEmpty(form))
                    return SafeFormat(form, count == 1 ? singular : plural, values);
            }
        }
        return SafeFormat(count == 1 ? singular : plural, count == 1 ? singular : plural, values);
    }

    // An English text from elsewhere (an engine status line, an error the GUI shows): its translation, or the
    // translation of a pattern it matches ("Exported 12 binds." <- "Exported {0} binds."), or the text itself.
    public static string Translate(string? english)
    {
        if (string.IsNullOrEmpty(english) || current == Languages.English)
            return english ?? string.Empty;
        string direct = T(english);
        if (!ReferenceEquals(direct, english) && direct != english)
            return direct;
        foreach (PatternEntry pattern in Patterns())
        {
            Match match = pattern.Regex.Match(english);
            if (!match.Success)
                continue;
            object?[] args = new object?[pattern.Count];
            for (int i = 0; i < pattern.Count; i++)
                args[i] = match.Groups["a" + i].Value;
            return SafeFormat(pattern.Translation, english, args);
        }
        return english;
    }

    // Multi-line text (an engine report): line by line.
    public static string TranslateLines(string? english)
    {
        if (string.IsNullOrEmpty(english) || current == Languages.English)
            return english ?? string.Empty;
        string[] lines = english.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(Translate));
    }

    // The live text of a key for XAML ({l:T}): one shared object per key, so a page made again binds the same one.
    public static LocText Observable(string key) => Observables.GetOrAdd(key, k => new LocText(k));

    // Whether a language's own file has this text (the language tool and the tests).
    public static bool Has(string code, string key)
    {
        Table? table = LoadTable(code);
        return table != null && table.Strings.ContainsKey(key);
    }

    public static IReadOnlyDictionary<string, string[]> StringsOf(string code)
    {
        Table? table = LoadTable(code);
        return table?.Strings ?? new Dictionary<string, string[]>();
    }

    private static string SafeFormat(string format, string englishFormat, object?[] args)
    {
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, englishFormat, args);
            }
            catch (FormatException)
            {
                return englishFormat;
            }
        }
    }

    private static CultureInfo TryCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static List<Table> BuildChain(LanguageInfo language)
    {
        List<Table> list = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (LanguageInfo? l = language; l != null && l != Languages.English && seen.Add(l.Code); l = Languages.Find(l.Fallback))
        {
            Table? table = LoadTable(l.Code);
            if (table != null)
                list.Add(table);
        }
        return list;
    }

    private static Table? LoadTable(string code)
    {
        LanguageInfo? language = Languages.Find(code);
        if (language == null || language == Languages.English)
            return null;
        lock (Gate)
        {
            if (Tables.TryGetValue(language.Code, out Table? cached))
                return cached;
            Table table = new(language, ReadStrings(language.Code));
            Tables[language.Code] = table;
            return table;
        }
    }

    private static Dictionary<string, string[]> ReadStrings(string code)
    {
        try
        {
            Stream? stream = null;
            string? folder = StringsFolderOverride;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                string file = Path.Combine(folder, code + ".json");
                if (File.Exists(file))
                    stream = File.OpenRead(file);
            }
            else
            {
                stream = typeof(Loc).Assembly.GetManifestResourceStream("JoyPro.Gui.Localization." + code + ".json");
            }
            if (stream == null)
                return new Dictionary<string, string[]>(StringComparer.Ordinal);
            using (stream)
                return ParseStrings(stream);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string[]>(StringComparer.Ordinal);
        }
    }

    // A strings file: {"English": "Translation", "{0} bind": ["{0} form 1", "{0} form 2"], ...}. Keys starting with
    // "_" are comments.
    public static Dictionary<string, string[]> ParseStrings(Stream stream)
    {
        Dictionary<string, string[]> strings = new(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return strings;
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
                continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    strings[property.Name] = [property.Value.GetString() ?? string.Empty];
                    break;
                case JsonValueKind.Array:
                    strings[property.Name] = property.Value.EnumerateArray()
                        .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty)
                        .ToArray();
                    break;
            }
        }
        return strings;
    }

    private static List<PatternEntry> Patterns()
    {
        List<PatternEntry>? list = patterns;
        if (list != null)
            return list;
        list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Table table in chain)
        {
            foreach ((string key, string[] forms) in table.Strings)
            {
                if (forms.Length == 0 || key.IndexOf('{') < 0 || !seen.Add(key))
                    continue;
                PatternEntry? entry = PatternEntry.TryCreate(key, forms[0]);
                if (entry != null)
                    list.Add(entry);
            }
        }
        // The most specific (longest literal text) pattern first.
        list.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
        patterns = list;
        return list;
    }

    private sealed record Table(LanguageInfo Language, Dictionary<string, string[]> Strings);

    private sealed class PatternEntry
    {
        private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

        private PatternEntry(Regex regex, int count, string translation, int literalLength)
        {
            Regex = regex;
            Count = count;
            Translation = translation;
            LiteralLength = literalLength;
        }

        public Regex Regex { get; }
        public int Count { get; }
        public string Translation { get; }
        public int LiteralLength { get; }

        public static PatternEntry? TryCreate(string key, string translation)
        {
            MatchCollection holes = Placeholder.Matches(key);
            if (holes.Count == 0)
                return null;
            StringBuilder pattern = new("^");
            int last = 0, literal = 0, count = 0;
            HashSet<int> used = [];
            foreach (Match hole in holes)
            {
                string text = key[last..hole.Index];
                literal += text.Length;
                pattern.Append(Regex.Escape(text));
                int index = int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!used.Add(index))
                    return null;
                pattern.Append("(?<a").Append(index).Append(">.+?)");
                count = Math.Max(count, index + 1);
                last = hole.Index + hole.Length;
            }
            string tail = key[last..];
            literal += tail.Length;
            pattern.Append(Regex.Escape(tail)).Append('$');
            // A pattern of placeholders only ("{0}: {1}") would match any text.
            if (literal < 4 || !key.Any(char.IsLetter) || used.Count != count)
                return null;
            return new PatternEntry(new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), count, translation, literal);
        }
    }
}

// The live text of one key: the current translation, again on every language change. XAML binds to Value (Avalonia
// listens through a weak event, so a page that is thrown away is not kept alive); one object per key, a bounded set.
public sealed class LocText : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly System.ComponentModel.PropertyChangedEventArgs ValueChanged = new(nameof(Value));

    public LocText(string key)
    {
        Key = key;
        Value = Loc.T(key);
    }

    public string Key { get; }

    public string Value { get; private set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        string value = Loc.T(Key);
        if (value == Value)
            return;
        Value = value;
        PropertyChanged?.Invoke(this, ValueChanged);
    }
}
