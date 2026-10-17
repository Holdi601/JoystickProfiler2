using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace JoyPro.LocTool;

// The localization tool (docs/localization.md):
//   dotnet run --project tools/loc/JoyPro.LocTool -- <command> [options]
//   extract  [--out file]                 the GUI's string inventory (texts in .axaml and .cs) as JSON
//   report   [--wrapped]                  per language: how many inventory texts have no translation
//   missing  --lang de [--out file] [--wrapped] [--limit n] [--skip n]
//                                         the untranslated texts of a language as {"English": ""} to fill in
//   merge    --lang de --in file          adds the translations of a filled-in file to Strings/de.json
//   check                                 placeholders and format texts of every translation (exit 1 on problems)
//   cyrillic                             makes Strings/sr-Cyrl.json from Strings/sr-Latn.json (transliteration)
//   unwrapped [--file x]                 the texts not yet going through the localization, with their places
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("commands: extract, report, missing, merge, check, cyrillic, unwrapped (see Program.cs)");
            return 2;
        }
        string repo = FindRepo();
        string gui = Path.Combine(repo, "gui", "JoyPro.Gui");
        string strings = Path.Combine(gui, "Localization", "Strings");
        string command = args[0];
        Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
        bool wrappedOnly = options.ContainsKey("wrapped");

        List<InventoryEntry> Inventory() => LocExtractor.Inventory(LocExtractor.ScanTree(gui))
            .Where(e => !wrappedOnly || e.Wrapped).ToList();

        switch (command)
        {
            case "extract":
            {
                List<InventoryEntry> inventory = Inventory();
                string json = JsonSerializer.Serialize(inventory.Select(e => new { text = e.Text, plural = e.Plural, kind = e.Kind, wrapped = e.Wrapped, uses = e.Uses, at = e.Locations }),
                    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                Output(options, json);
                Console.Error.WriteLine($"{inventory.Count} texts ({inventory.Count(e => e.Wrapped)} wrapped), {inventory.Sum(e => e.Text.Length)} characters");
                return 0;
            }
            case "report":
            {
                List<InventoryEntry> inventory = Inventory();
                Console.WriteLine($"{inventory.Count} texts in the inventory ({inventory.Count(e => e.Wrapped)} wrapped)");
                foreach (string file in Directory.GetFiles(strings, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    Dictionary<string, string[]> table = LocFiles.Read(file);
                    int missing = inventory.Count(e => !table.ContainsKey(e.Text));
                    int missingWrapped = inventory.Count(e => e.Wrapped && !table.ContainsKey(e.Text));
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-8} {table.Count,6} strings, missing {missing,5} (wrapped {missingWrapped})");
                }
                return 0;
            }
            case "missing":
            {
                string lang = Required(options, "lang");
                Dictionary<string, string[]> table = LocFiles.Read(Path.Combine(strings, lang + ".json"));
                IEnumerable<InventoryEntry> missing = Inventory().Where(e => !table.ContainsKey(e.Text));
                if (options.TryGetValue("skip", out string? skip))
                    missing = missing.Skip(int.Parse(skip));
                if (options.TryGetValue("limit", out string? limit))
                    missing = missing.Take(int.Parse(limit));
                Dictionary<string, object> todo = new();
                foreach (InventoryEntry e in missing)
                    todo[e.Text] = e.Plural != null ? new[] { e.Text, e.Plural } : string.Empty;
                Output(options, JsonSerializer.Serialize(todo, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                Console.Error.WriteLine($"{todo.Count} texts without a {lang} translation");
                return 0;
            }
            case "merge":
            {
                string lang = Required(options, "lang");
                string file = Path.Combine(strings, lang + ".json");
                Dictionary<string, string[]> table = LocFiles.Read(file);
                Dictionary<string, string[]> incoming = LocFiles.Read(Required(options, "in"));
                int added = 0, problems = 0;
                foreach ((string key, string[] forms) in incoming)
                {
                    if (forms.All(f => f.Length == 0))
                        continue;
                    List<string> issues = LocFiles.Check(key, forms);
                    if (issues.Count > 0)
                    {
                        problems++;
                        Console.Error.WriteLine($"skipped \"{key}\": {string.Join("; ", issues)}");
                        continue;
                    }
                    // --keep: a translation already in the file (made by hand) stays
                    if (options.ContainsKey("keep") && table.ContainsKey(key))
                        continue;
                    table[key] = forms.Select(f => LocFiles.KeepEdges(key, f)).ToArray();
                    added++;
                }
                LocFiles.Write(file, table);
                Console.WriteLine($"{lang}: {added} translations merged, {problems} skipped, {table.Count} in total");
                return problems > 0 ? 1 : 0;
            }
            case "chunks":
            {
                // The untranslated texts of a language in files of about --size characters: [{"en": ..., "ctx": ...}]
                string lang = Required(options, "lang");
                string dir = Required(options, "dir");
                int size = options.TryGetValue("size", out string? s) ? int.Parse(s) : 12000;
                Dictionary<string, string[]> table = LocFiles.Read(Path.Combine(strings, lang + ".json"));
                List<InventoryEntry> missing = Inventory().Where(e => !table.ContainsKey(e.Text)).ToList();
                Directory.CreateDirectory(dir);
                int index = 0, chars = 0;
                List<object> chunk = [];
                void Flush()
                {
                    if (chunk.Count == 0)
                        return;
                    index++;
                    JsonSerializerOptions compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                    File.WriteAllText(Path.Combine(dir, $"chunk-{index:00}.json"), "[\n" + string.Join(",\n", chunk.Select(c => JsonSerializer.Serialize(c, compact))) + "\n]\n", new UTF8Encoding(false));
                    chunk = [];
                    chars = 0;
                }
                foreach (InventoryEntry e in missing)
                {
                    chunk.Add(new { en = e.Text, plural = e.Plural, ctx = Context(e) });
                    chars += e.Text.Length;
                    if (chars >= size)
                        Flush();
                }
                Flush();
                Console.WriteLine($"{missing.Count} texts in {index} chunks in {dir}");
                return 0;
            }
            case "validate":
            {
                // A filled-in chunk ({"English": "translation"}) against its todo chunk: every text there, placeholders kept.
                Dictionary<string, string[]> done = LocFiles.Read(Required(options, "in"));
                List<string> problems = [];
                if (options.TryGetValue("todo", out string? todoFile))
                {
                    using JsonDocument todo = JsonDocument.Parse(File.ReadAllText(todoFile));
                    foreach (JsonElement item in todo.RootElement.EnumerateArray())
                    {
                        string en = item.GetProperty("en").GetString() ?? string.Empty;
                        if (!done.TryGetValue(en, out string[]? forms) || forms.All(f => f.Length == 0))
                            problems.Add("missing: " + en);
                    }
                }
                JoyPro.Gui.Localization.LanguageInfo? language = options.TryGetValue("lang", out string? code) ? JoyPro.Gui.Localization.Languages.Find(code) : null;
                foreach ((string key, string[] forms) in done)
                {
                    foreach (string issue in LocFiles.Check(key, forms))
                        problems.Add($"\"{key}\": {issue}");
                    if (language != null && LocFiles.PluralKeys.Contains(key) && forms.Length != JoyPro.Gui.Localization.Languages.PluralCount(language.Plural))
                        problems.Add($"\"{key}\": {forms.Length} plural forms, {code} needs {JoyPro.Gui.Localization.Languages.PluralCount(language.Plural)}");
                }
                foreach (string p in problems)
                    Console.WriteLine(p);
                Console.WriteLine(problems.Count == 0 ? "OK" : problems.Count + " problems");
                return problems.Count == 0 ? 0 : 1;
            }
            case "wrap-axaml":
            {
                // The literal texts of .axaml files to {l:T '...'} (--file part of a path; all files without it); --dry
                // only counts.
                options.TryGetValue("file", out string? only);
                int total = 0;
                foreach (string file in LocExtractor.SourceFiles(gui).Where(f => f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)))
                {
                    if (only != null && !file.Replace('\\', '/').Contains(only, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string content = File.ReadAllText(file);
                    AxamlWrapper.Result result = AxamlWrapper.Wrap(content);
                    total += result.Wrapped;
                    Console.WriteLine($"{Path.GetRelativePath(gui, file)}: {result.Wrapped} texts");
                    foreach (string skipped in result.Skipped)
                        Console.WriteLine("  manual: " + skipped);
                    if (!options.ContainsKey("dry") && result.Wrapped > 0)
                        File.WriteAllText(file, result.Content, new UTF8Encoding(content.Length > 0 && File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })));
                }
                Console.WriteLine($"{total} texts wrapped");
                return 0;
            }
            case "wrap-cs":
            {
                // User texts in .cs files to T("...", args) (--file part of a path; --dry only lists); --left file: what
                // stayed, with the reason.
                options.TryGetValue("file", out string? only);
                int total = 0;
                StringBuilder leftReport = new();
                foreach (string file in LocExtractor.SourceFiles(gui).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    string rel = Path.GetRelativePath(gui, file).Replace('\\', '/');
                    if (only != null && !rel.Contains(only, StringComparison.OrdinalIgnoreCase))
                        continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    string content = File.ReadAllText(file);
                    CsWrapper.Result result = CsWrapper.Wrap(rel, content);
                    total += result.Wrapped;
                    foreach (FoundText f in result.Left)
                    {
                        CsWrapper.Accept(f, content, out string why);
                        leftReport.Append(rel).Append(':').Append(f.Line).Append("  [").Append(why).Append("]  ").AppendLine(f.Text.Replace("\n", "\\n"));
                    }
                    if (result.Wrapped > 0)
                        Console.WriteLine($"{rel}: {result.Wrapped}");
                    if (!options.ContainsKey("dry") && result.Wrapped > 0)
                        File.WriteAllText(file, result.Content, new UTF8Encoding(bom));
                }
                if (options.TryGetValue("left", out string? leftFile))
                    File.WriteAllText(leftFile, leftReport.ToString(), new UTF8Encoding(false));
                Console.WriteLine($"{total} texts wrapped");
                return 0;
            }
            case "check":
            {
                int problems = 0;
                foreach (string file in Directory.GetFiles(strings, "*.json"))
                {
                    foreach ((string key, string[] forms) in LocFiles.Read(file))
                    {
                        foreach (string issue in LocFiles.Check(key, forms))
                        {
                            problems++;
                            Console.WriteLine($"{Path.GetFileName(file)}: \"{key}\": {issue}");
                        }
                    }
                }
                Console.WriteLine(problems == 0 ? "no problems" : problems + " problems");
                return problems == 0 ? 0 : 1;
            }
            case "cyrillic":
            {
                Dictionary<string, string[]> latin = LocFiles.Read(Path.Combine(strings, "sr-Latn.json"));
                Dictionary<string, string[]> cyrillic = latin.ToDictionary(e => e.Key, e => e.Value.Select(SerbianScript.ToCyrillic).ToArray(), StringComparer.Ordinal);
                LocFiles.Write(Path.Combine(strings, "sr-Cyrl.json"), cyrillic);
                Console.WriteLine($"sr-Cyrl: {cyrillic.Count} strings");
                return 0;
            }
            case "unwrapped":
            {
                List<FoundText> found = LocExtractor.ScanTree(gui).Where(f => !f.Wrapped).ToList();
                if (options.TryGetValue("file", out string? only))
                    found = found.Where(f => f.File.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
                StringBuilder sb = new();
                foreach (FoundText f in found)
                    sb.Append(f.File).Append(':').Append(f.Line).Append("  [").Append(f.Kind).Append(f.Owner.Length > 0 ? " " + f.Owner : "").Append(f.Unsafe ? " UNSAFE" : "").Append("]  ").AppendLine(f.Text.Replace("\n", "\\n"));
                Output(options, sb.ToString());
                Console.Error.WriteLine($"{found.Count} texts not localized yet");
                return 0;
            }
            default:
                Console.Error.WriteLine("unknown command " + command);
                return 2;
        }
    }

    // What a text is, for the translator: a button label, a tooltip, a status line... and where.
    private static string Context(InventoryEntry e)
    {
        string kind = e.Kind switch
        {
            "xaml:Content" => "label",
            "xaml:ToolTip.Tip" => "tooltip",
            "xaml:Header" => "header",
            "xaml:Watermark" or "xaml:PlaceholderText" => "placeholder",
            "xaml:Title" => "window title",
            "cs-format" => "code, built",
            "cs" => "code",
            _ when e.Kind.StartsWith("xaml", StringComparison.Ordinal) => "page text",
            _ => e.Kind
        };
        string at = e.Locations.Count > 0 ? e.Locations[0] : "?";
        return kind + " @" + at[(at.LastIndexOf('/') + 1)..];
    }

    private static void Output(Dictionary<string, string> options, string text)
    {
        if (options.TryGetValue("out", out string? file))
            File.WriteAllText(file, text, new UTF8Encoding(false));
        else
            Console.Out.Write(text);
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : throw new ArgumentException("--" + name + " is needed");

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            string name = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            options[name] = value;
        }
        return options;
    }

    private static string FindRepo()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "gui", "JoyPro.Gui")))
                    return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("gui/JoyPro.Gui not found above the current folder");
    }
}
