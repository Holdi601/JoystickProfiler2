using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JoyPro.LocTool;

// The strings files of gui/JoyPro.Gui/Localization/Strings (<code>.json): read, written sorted (stable diffs), and
// checked (a translation keeps the English text's placeholders).
public static class LocFiles
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    public static Dictionary<string, string[]> Read(string file)
    {
        Dictionary<string, string[]> strings = new(StringComparer.Ordinal);
        if (!File.Exists(file))
            return strings;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
                strings[p.Name] = [p.Value.GetString() ?? string.Empty];
            else if (p.Value.ValueKind == JsonValueKind.Array && PluralKeys.Add(p.Name) | true)
                strings[p.Name] = p.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray();
        }
        return strings;
    }

    public static void Write(string file, IReadOnlyDictionary<string, string[]> strings)
    {
        JsonWriterOptions options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, options))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string[]> entry in strings.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (entry.Value.Length == 1 && !entry.Key.StartsWith("_plural:", StringComparison.Ordinal) && !IsPluralArray(entry))
                    writer.WriteString(entry.Key, entry.Value[0]);
                else
                {
                    writer.WriteStartArray(entry.Key);
                    foreach (string form in entry.Value)
                        writer.WriteStringValue(form);
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndObject();
        }
        string text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, new UTF8Encoding(false));
    }

    // Plural texts are stored as arrays even with one form (Japanese): kept by the caller marking them; a single
    // form array is written as an array when the existing file had one.
    private static bool IsPluralArray(KeyValuePair<string, string[]> entry) => PluralKeys.Contains(entry.Key);

    public static HashSet<string> PluralKeys { get; } = new(StringComparer.Ordinal);

    // A translation with the English text's leading and trailing white space (fragments joined in code: " · ", "\n").
    public static string KeepEdges(string key, string form)
    {
        if (form.Length == 0)
            return form;
        string lead = key[..(key.Length - key.TrimStart().Length)];
        string trail = key[key.TrimEnd().Length..];
        return lead + form.Trim() + trail;
    }

    // The placeholder numbers of a text ("{0} of {1}" -> {0, 1}).
    public static SortedSet<int> Placeholders(string text) =>
        new(Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)));

    // Problems of a translation: placeholders lost or added, a translation that breaks string.Format.
    public static List<string> Check(string key, string[] forms)
    {
        List<string> problems = [];
        SortedSet<int> expected = Placeholders(key);
        foreach (string form in forms)
        {
            if (form.Length == 0)
                continue;
            SortedSet<int> got = Placeholders(form);
            // a plural form may leave out the count ({0}) ("one file" for 1)
            if (!got.SetEquals(expected) && !(forms.Length > 1 && got.IsSubsetOf(expected)))
                problems.Add($"placeholders {string.Join(",", expected)} != {string.Join(",", got)}: {form}");
            try
            {
                _ = string.Format(form, Enumerable.Range(0, Math.Max(expected.Count == 0 ? 0 : expected.Max + 1, got.Count == 0 ? 0 : got.Max + 1)).Select(i => (object)i).ToArray());
            }
            catch (FormatException)
            {
                // A text without placeholders is never formatted; braces in it are fine.
                if (expected.Count > 0)
                    problems.Add("not a valid format text: " + form);
            }
        }
        return problems;
    }
}
