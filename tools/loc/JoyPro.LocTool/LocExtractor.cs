using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JoyPro.LocTool;

// One text found in the GUI sources (docs/localization.md). Wrapped: already goes through the localization
// ({l:T '...'}, T("..."), Plural("...", "...", n)); otherwise a literal that looks like a user-visible text.
// Plural: the English plural of a Plural(...) text (Text is the singular).
public sealed record FoundText(string Text, string? Plural, bool Wrapped, string File, int Line, string Kind)
{
    // C#: the source span of the text (a literal, an interpolated string or a whole concatenation) and the code of its
    // values in placeholder order, for the rewrite to T("...", args) (CsWrapper).
    public int Start { get; init; } = -1;
    public int End { get; init; } = -1;
    public string[] Args { get; init; } = [];
    // C#: the property, field or method the text goes to ("StatusText"), or the call it is an argument of.
    public string Owner { get; init; } = string.Empty;
    // C#: an interpolated string with an escaped brace or an alignment: not rewritten automatically.
    public bool Unsafe { get; init; }
}

// The string inventory of the GUI: scans .axaml and .cs files for texts the user sees. Gettext style: the English
// text is the key. Texts built by concatenation or interpolation are reported as the format text the localization
// would use ("Exported {0} binds to {1}."), so a later T(...) of the same code finds the translation.
public static class LocExtractor
{
    // XAML properties that hold text the user reads.
    private static readonly HashSet<string> TextProperties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "ToolTip.Tip", "Watermark", "PlaceholderText", "Title", "Label", "OnContent",
        "OffContent", "Description", "AutomationProperties.Name", "AutomationProperties.HelpText", "Caption",
        "Subtitle", "Hint", "EmptyText", "Message", "InnerLeftContent", "InnerRightContent"
    };

    // Elements whose text content is shown.
    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Button", "Label", "ToolTip.Tip", "SelectableTextBlock",
        "CheckBox", "RadioButton", "ToggleButton", "HyperlinkButton", "MenuItem", "TabItem", "Expander", "ComboBoxItem",
        "ListBoxItem", "HeaderedContentControl"
    };

    // Calls whose string arguments are not user texts.
    private static readonly HashSet<string> SkipCalls = new(StringComparer.Ordinal)
    {
        "Log", "LogToTrace", "AppendSessionLog", "WriteLine", "Write", "Assert", "Fail", "nameof", "Equals", "StartsWith",
        "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Replace", "Split", "Trim", "TrimStart", "TrimEnd", "Combine",
        "GetEnvironmentVariable", "SetEnvironmentVariable", "GetProperty", "TryGetProperty", "GetString", "Regex",
        "IsMatch", "Match", "Matches", "JsonPropertyName", "OnPropertyChanged", "RaisePropertyChanged", "GetFullPath",
        "Exists", "ReadAllText", "WriteAllText", "Delete", "Move", "Copy", "Parse", "TryParse", "ParseExact",
        "GetManifestResourceStream", "Uri", "Load", "Open", "OpenRead", "Find", "TryFindResource", "FindResource",
        "FindControl", "Get", "GetValue", "OpenSubKey", "CreateSubKey", "SetValue", "DeleteValue", "Start", "ProcessStartInfo",
        "AddArgument", "Argument", "Arg", "Flag", "Option", "RunEngine", "RunEngineAsync", "RunCliAsync", "RunAsync",
        "ExecuteAsync", "RunCommandAsync", "Has", "HasFlag", "ContainsKey", "TryGetValue", "Remove", "GetFileName", "ChangeExtension",
        "EnumerateFiles", "GetFiles", "GetDirectories", "EnumerateDirectories", "Color", "Parse", "SolidColorBrush",
        "Geometry", "StreamGeometry", "FontFamily", "ToString", "PadLeft", "PadRight", "Format", "Join", "Concat",
        "CompareOrdinal", "Compare", "Debug", "Trace", "ReadLine", "GetCultureInfo", "CultureInfo", "SelectToken",
        "Element", "Elements", "Attribute", "Attributes", "Descendants", "XName", "XElement", "XAttribute", "Property",
        "WriteString", "WritePropertyName", "WriteStartObject", "WriteBoolean", "WriteNumber", "Mutex", "EventWaitHandle",
        "NamedPipeServerStream", "NamedPipeClientStream", "Throw", "ThrowIfNull", "CallerMemberName"
    };

    // Statement starts after which a string is not a user text.
    private static readonly Regex SkipStatement = new(@"^\s*(case\b|#|\[|throw\b|Debug\.|Trace\.|Console\.|Log\(|AppendSessionLog\(|AppendFfbLog\(|AddVirtualJoystickLog\(|(JoyPro\.Logging\.)?JoyProLog\.|_?[lL]og(ger)?\.)", RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{\d+(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    public static readonly HashSet<string> NoLocFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs", "Services/ThirdPartyNotices.cs", "Services/GameDataLocations.cs", "Services/JoystickCaptureService.cs",
        "Services/CrashReporter.cs", "Services/FfbTelemetryServices.cs", "Services/AuthenticodeVerifier.cs"
    };

    public static List<FoundText> ScanTree(string guiRoot)
    {
        List<FoundText> found = [];
        foreach (string file in SourceFiles(guiRoot))
        {
            string content = File.ReadAllText(file);
            string rel = Path.GetRelativePath(guiRoot, file).Replace('\\', '/');
            if (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                found.AddRange(ScanAxaml(rel, content));
            else
                found.AddRange(ScanCSharp(rel, content));
        }
        return found;
    }

    public static IEnumerable<string> SourceFiles(string guiRoot)
    {
        foreach (string file in Directory.EnumerateFiles(guiRoot, "*.*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(guiRoot, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(".tmp_build/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Localization/", StringComparison.OrdinalIgnoreCase))
                continue;
            // Design concept sheets (BrandingConcepts) are not shown to users.
            if (rel.EndsWith("BrandingConcepts.axaml", StringComparison.OrdinalIgnoreCase))
                continue;
            // Files without user texts: logs and crash records, license texts, folder and key names, report bodies.
            if (NoLocFiles.Contains(rel))
                continue;
            if (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    // ---- AXAML -------------------------------------------------------------------------------------------------

    public static List<FoundText> ScanAxaml(string file, string content)
    {
        List<FoundText> found = [];
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return found;
        }
        foreach (XElement element in doc.Descendants())
        {
            string elementName = element.Name.LocalName;
            bool isSetter = elementName == "Setter";
            string? setterProperty = isSetter ? (string?)element.Attribute("Property") : null;
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                string name = attribute.Name.LocalName;
                if (attribute.Name.Namespace != XNamespace.None)
                    continue;
                bool textProperty = TextProperties.Contains(name) || isSetter && name == "Value" && setterProperty != null && TextProperties.Contains(setterProperty);
                int line = ((IXmlLineInfo)attribute).LineNumber;
                string value = attribute.Value;
                // {l:T ...} anywhere (also inside a binding's StringFormat).
                foreach (FoundText t in WrappedInMarkup(value, file, line))
                    found.Add(t);
                if (!textProperty)
                    continue;
                if (value.StartsWith("{}", StringComparison.Ordinal))
                    value = value[2..];
                else if (value.StartsWith('{'))
                {
                    string? format = StringFormatOf(value);
                    if (format != null && IsUiText(format))
                        found.Add(new FoundText(format, null, false, file, line, "xaml-format:" + (isSetter ? setterProperty : name)));
                    continue;
                }
                if (IsUiText(value, lenient: true))
                    found.Add(new FoundText(Normalize(value), null, false, file, line, "xaml:" + (isSetter ? setterProperty : name)));
            }
            if (TextElements.Contains(elementName))
            {
                foreach (XText text in element.Nodes().OfType<XText>())
                {
                    string value = Regex.Replace(text.Value, @"\s+", " ").Trim();
                    if (IsUiText(value, lenient: true))
                        found.Add(new FoundText(value, null, false, file, ((IXmlLineInfo)text).LineNumber, "xaml:" + elementName));
                }
            }
        }
        return found;
    }

    private static readonly Regex MarkupT = new(@"\{\s*(?:[A-Za-z_]\w*:)?T\s+(?<args>(?:'(?:[^'\\]|\\.)*'|[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}", RegexOptions.Compiled);

    private static IEnumerable<FoundText> WrappedInMarkup(string value, string file, int line)
    {
        foreach (Match m in MarkupT.Matches(value))
        {
            string args = m.Groups["args"].Value;
            string? text = null, plural = null;
            foreach ((string? key, string arg) in SplitMarkupArgs(args))
            {
                if (key == null || key == "Text")
                    text ??= UnquoteMarkup(arg);
                else if (key == "Plural")
                    plural = UnquoteMarkup(arg);
            }
            if (!string.IsNullOrEmpty(text))
                yield return new FoundText(text, plural, true, file, line, "xaml");
        }
    }

    // A markup extension's arguments: positional and Name=value, split at top-level commas.
    private static List<(string? Key, string Value)> SplitMarkupArgs(string args)
    {
        List<(string?, string)> list = [];
        StringBuilder current = new();
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (quoted)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < args.Length)
                    current.Append(args[++i]);
                else if (c == '\'')
                    quoted = false;
                continue;
            }
            if (c == '\'')
                quoted = true;
            else if (c == '{')
                depth++;
            else if (c == '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                list.Add(KeyValue(current.ToString()));
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0)
            list.Add(KeyValue(current.ToString()));
        return list;

        static (string?, string) KeyValue(string part)
        {
            string p = part.Trim();
            Match kv = Regex.Match(p, @"^(?<k>[A-Za-z_][\w.]*)\s*=\s*(?<v>.*)$", RegexOptions.Singleline);
            if (kv.Success && !p.StartsWith('\''))
                return (kv.Groups["k"].Value, kv.Groups["v"].Value.Trim());
            return (null, p);
        }
    }

    public static string UnquoteMarkup(string arg)
    {
        string a = arg.Trim();
        if (a.Length >= 2 && a[0] == '\'' && a[^1] == '\'')
        {
            StringBuilder sb = new();
            for (int i = 1; i < a.Length - 1; i++)
            {
                if (a[i] == '\\' && i + 1 < a.Length - 1)
                    sb.Append(a[++i]);
                else
                    sb.Append(a[i]);
            }
            string text = sb.ToString();
            // '{}{0} ...': the XAML escape of a text that starts with a brace
            return text.StartsWith("{}", StringComparison.Ordinal) ? text[2..] : text;
        }
        return a;
    }

    // Quotes a text for {l:T '...'}: backslash before ' and \; a text starting with '{' gets XAML's "{}" escape (else
    // the XAML compiler reads '{0} ...' as a markup extension).
    public static string QuoteMarkup(string text) =>
        "'" + (text.StartsWith('{') ? "{}" : string.Empty) + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string? StringFormatOf(string binding)
    {
        Match m = Regex.Match(binding, @"StringFormat\s*=\s*(?:'(?<q>(?:[^'\\]|\\.)*)'|(?<u>(?:\{\}|[^,}'])+(?:\{\d+[^}]*\}[^,}']*)*))");
        if (!m.Success)
            return null;
        string value = m.Groups["q"].Success ? UnquoteMarkup("'" + m.Groups["q"].Value + "'") : m.Groups["u"].Value;
        if (value.StartsWith("{}", StringComparison.Ordinal))
            value = value[2..];
        return value.Trim().Length == 0 ? null : value;
    }

    // ---- C# ------------------------------------------------------------------------------------------------------

    private sealed class Literal
    {
        public int Start;   // index of the first character of the literal token ($, @ or ")
        public int End;     // index after the closing quote
        public string Text = string.Empty;   // value; interpolation holes as {0}, {1}, ...
        public int Holes;
        public bool Interpolated;
        public int Depth;   // nesting inside interpolation holes (0: top level)
        public List<(int Start, int End)> HoleExprs = [];   // the code of each hole
        public bool Unsafe;  // {{ }} or an alignment in an interpolated string
    }

    public static List<FoundText> ScanCSharp(string file, string content)
    {
        List<FoundText> found = [];
        List<Literal> literals = [];
        // The code with comments and string contents blanked (same length), for the context of each literal.
        char[] skeleton = content.ToCharArray();
        Lex(content, skeleton, literals);
        string code = new(skeleton);
        literalEnds = literals.Where(l => l.Depth == 0).ToDictionary(l => l.Start, l => l.End);
        literalStarts = literals.Where(l => l.Depth == 0).GroupBy(l => l.End).ToDictionary(g => g.Key, g => g.First().Start);
        int[] lineStarts = LineStarts(content);
        HashSet<Literal> used = [];

        foreach (Literal literal in literals.Where(l => l.Depth == 0))
        {
            if (used.Contains(literal))
                continue;
            int line = LineOf(lineStarts, literal.Start);
            (string? call, int argIndex) = EnclosingCall(code, literal.Start);
            if (call is "T" or "Plural" or "Translate" or "N")
            {
                if (argIndex == 0 || call == "Plural" && argIndex == 1)
                {
                    if (call == "Plural")
                    {
                        if (argIndex == 0)
                        {
                            Literal? second = literals.FirstOrDefault(l => l.Depth == 0 && l.Start > literal.End && EnclosingCall(code, l.Start) is ("Plural", 1) && SameCall(code, literal.Start, l.Start));
                            if (second != null)
                                used.Add(second);
                            found.Add(new FoundText(literal.Text, second?.Text, true, file, line, "cs"));
                        }
                    }
                    else if (call is "T" or "N")
                        found.Add(new FoundText(literal.Text, null, true, file, line, "cs"));
                    continue;
                }
            }
            if (call != null && SkipCalls.Contains(call))
                continue;
            if (SkipStatement.IsMatch(StatementPrefix(code, literal.Start)))
                continue;
            if (IsComparisonOrKey(code, literal))
                continue;

            // A concatenation: the whole chain as one format text.
            (string text, List<Literal> parts, List<(int Start, int End)> args, int spanStart, int spanEnd) = Chain(code, literals, literal);
            foreach (Literal part in parts)
                used.Add(part);
            if (NoLocLine(content, literal.Start))
                continue;
            if (IsUiText(text))
                found.Add(new FoundText(text, null, false, file, line, parts.Count > 1 || literal.Interpolated ? "cs-format" : "cs")
                {
                    Start = spanStart,
                    End = spanEnd,
                    Args = args.Select(a => content[a.Start..a.End].Trim()).ToArray(),
                    Owner = Owner(code, spanStart, call),
                    Unsafe = parts.Any(pt => pt.Unsafe)
                });
        }
        return found;
    }

    // "// nonloc" on the line (or the line before): a text that is not translated (an id, a key name, a file name).
    private static bool NoLocLine(string content, int pos)
    {
        int lineStart = content.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        int lineEnd = content.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = content.Length;
        if (content.AsSpan(lineStart, lineEnd - lineStart).IndexOf("nonloc".AsSpan(), StringComparison.Ordinal) >= 0)
            return true;
        int prevStart = lineStart >= 2 ? content.LastIndexOf('\n', lineStart - 2) + 1 : 0;
        return lineStart > 0 && content.AsSpan(prevStart, lineStart - prevStart).TrimStart().StartsWith("// nonloc".AsSpan(), StringComparison.Ordinal);
    }

    private static bool HasTopLevelComma(string s, int start, int end)
    {
        int depth = 0;
        for (int k = start; k < end && k < s.Length; k++)
        {
            char c = s[k];
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            else if (c == ',' && depth == 0) return true;
        }
        return false;
    }

    private static readonly Regex AssignTarget = new(@"(?<name>[A-Za-z_]\w*)\s*(?:\?\?=|\+=|=(?![=>]))", RegexOptions.Compiled);
    private static readonly Regex ArrowOwner = new(@"(?<name>[A-Za-z_]\w*)\s*(?:\([^()]*\))?\s*=>\s*$", RegexOptions.Compiled);
    private static readonly Regex MemberDecl = new(@"^\s*(?:(?:public|private|internal|protected|static|override|virtual|async|partial|readonly|sealed)\s+)+[\w<>\[\],.? ]+?\s(?<name>[A-Za-z_]\w*)\s*(?:\(|=>|\{|$)", RegexOptions.Compiled | RegexOptions.Multiline);

    // Where a text goes: the assigned property ("StatusText = ..."), the property of an expression body, the method
    // that returns it, or the call it is an argument of.
    private static string Owner(string code, int pos, string? call)
    {
        string prefix = StatementPrefix(code, pos);
        // the innermost call first: "Warning = GetJsonString(fit, "Warning")" is a JSON field, not a text
        if (call != null && call != "[" && call != "new")
            return call + "(";
        Match assign = AssignTarget.Match(prefix);
        if (assign.Success)
            return assign.Groups["name"].Value;
        Match arrow = ArrowOwner.Match(prefix.TrimEnd());
        if (arrow.Success)
            return arrow.Groups["name"].Value;
        // expression-bodied member split over lines: "public string X =>\n    cond ? ..."
        int k = pos - 1;
        while (k >= 0 && code[k] != ';' && code[k] != '}' && code[k] != '{') k--;
        string statement = code.Substring(k + 1, pos - k - 1);
        Match arrow2 = Regex.Match(statement, @"(?<name>[A-Za-z_]\w*)\s*(?:\([^()]*\))?\s*=>");
        if (arrow2.Success)
            return arrow2.Groups["name"].Value;
        if (Regex.IsMatch(prefix, @"\breturn\b"))
        {
            // the enclosing member: the last declaration before pos
            MatchCollection decls = MemberDecl.Matches(code[..pos]);
            if (decls.Count > 0)
                return "return:" + decls[^1].Groups["name"].Value;
        }
        return string.Empty;
    }

    private static bool SameCall(string code, int a, int b)
    {
        int depth = 0;
        for (int i = a; i < b; i++)
        {
            char c = code[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth < 0)
                    return false;
            }
        }
        return depth == 0;
    }

    private static void Lex(string s, char[] skeleton, List<Literal> literals)
    {
        int i = 0;
        LexCode(s, skeleton, literals, ref i, 0, false);
    }

    // Code until the end (or, inside an interpolation hole, until its closing brace).
    private static void LexCode(string s, char[] sk, List<Literal> literals, ref int i, int depth, bool inHole)
    {
        int braces = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                    sk[i++] = ' ';
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    if (s[i] != '\n') sk[i] = ' ';
                    i++;
                }
                if (i < s.Length) { sk[i] = ' '; sk[i + 1] = ' '; i += 2; }
                continue;
            }
            if (c == '\'')
            {
                // char literal
                int start = i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                i++;
                for (int k = start + 1; k < Math.Min(i - 1, s.Length); k++) sk[k] = ' ';
                continue;
            }
            if (c == '"' || (c == '$' || c == '@') && i + 1 < s.Length && (s[i + 1] == '"' || (s[i + 1] == '$' || s[i + 1] == '@') && i + 2 < s.Length && s[i + 2] == '"'))
            {
                LexString(s, sk, literals, ref i, depth);
                continue;
            }
            if (inHole)
            {
                if (c == '{') braces++;
                else if (c == '}')
                {
                    if (braces == 0)
                        return;
                    braces--;
                }
                else if (c == ':' && braces == 0 && ParenBalanced(s, i))
                {
                    // format specifier of the hole: up to the closing brace
                    return;
                }
            }
            i++;
        }
    }

    // Whether a ':' inside a hole is a format specifier (not a ternary's or a named argument's).
    private static bool ParenBalanced(string s, int colon)
    {
        // Look back to the hole's opening brace: a '?' at depth 0 makes it a ternary.
        int depth = 0;
        for (int k = colon - 1; k >= 0; k--)
        {
            char c = s[k];
            if (c == ')' || c == ']') depth++;
            else if (c == '(' || c == '[') { if (depth == 0) return false; depth--; }
            else if (c == '{' && depth == 0) return true;
            else if (c == '?' && depth == 0) return false;
        }
        return true;
    }

    private static void LexString(string s, char[] sk, List<Literal> literals, ref int i, int depth)
    {
        Literal literal = new() { Start = i, Depth = depth };
        bool interpolated = false, verbatim = false;
        while (s[i] == '$' || s[i] == '@')
        {
            if (s[i] == '$') interpolated = true; else verbatim = true;
            i++;
        }
        // raw string literal (""" ... """): skipped as a whole
        if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
        {
            int q = 0;
            while (i < s.Length && s[i] == '"') { q++; i++; }
            if (q >= 3)
            {
                string close = new('"', q);
                int end = s.IndexOf(close, i, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + q;
                for (int k = i; k < end - q && k < s.Length; k++) if (s[k] != '\n') sk[k] = ' ';
                i = end;
                return;
            }
            // "" : an empty string
            literal.End = i;
            literal.Text = string.Empty;
            literals.Add(literal);
            return;
        }
        literal.Interpolated = interpolated;
        i++; // opening quote
        StringBuilder text = new();
        int holes = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"')
                {
                    text.Append('"');
                    sk[i] = ' '; sk[i + 1] = ' ';
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            if (!verbatim && c == '\\' && i + 1 < s.Length)
            {
                char e = s[i + 1];
                text.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => e });
                if (e == 'u' && i + 5 < s.Length)
                {
                    text.Length--;
                    text.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                    for (int k = i; k < i + 6; k++) sk[k] = ' ';
                    i += 6;
                    continue;
                }
                sk[i] = ' '; sk[i + 1] = ' ';
                i += 2;
                continue;
            }
            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{')
                {
                    text.Append('{');
                    literal.Unsafe = true;
                    sk[i] = ' '; sk[i + 1] = ' ';
                    i += 2;
                    continue;
                }
                // a hole: code until its '}' (or the format specifier)
                i++;
                int holeStart = i;
                LexCode(s, sk, literals, ref i, depth + 1, true);
                literal.HoleExprs.Add((holeStart, i));
                if (HasTopLevelComma(s, holeStart, i))
                    literal.Unsafe = true;
                string format = string.Empty;
                if (i < s.Length && s[i] == ':')
                {
                    int fs = i;
                    while (i < s.Length && s[i] != '}') i++;
                    format = s[fs..i];
                    for (int k = fs; k < i; k++) sk[k] = ' ';
                }
                // alignment ",5" stays out of the key, as T(...) would get it as an argument
                text.Append('{').Append(holes++).Append(format).Append('}');
                if (i < s.Length) i++; // '}'
                continue;
            }
            if (interpolated && c == '}' && i + 1 < s.Length && s[i + 1] == '}')
            {
                text.Append('}');
                literal.Unsafe = true;
                sk[i] = ' '; sk[i + 1] = ' ';
                i += 2;
                continue;
            }
            text.Append(c);
            if (c != '\n') sk[i] = ' ';
            i++;
        }
        literal.End = i;
        literal.Text = text.ToString();
        literal.Holes = holes;
        literals.Add(literal);
    }

    // The name of the call whose argument list holds position pos, and which argument it is.
    private static (string? Call, int Arg) EnclosingCall(string code, int pos)
    {
        int depth = 0, arg = 0;
        for (int k = pos - 1; k >= 0; k--)
        {
            char c = code[k];
            if (c == ')' || c == ']' || c == '}') depth++;
            else if (c == '(' || c == '[' || c == '{')
            {
                if (depth > 0) { depth--; continue; }
                if (c != '(')
                    return (c == '[' ? "[" : null, arg);
                int e = k - 1;
                while (e >= 0 && char.IsWhiteSpace(code[e])) e--;
                // generic call Foo<T>(...)
                if (e >= 0 && code[e] == '>')
                {
                    int g = 0;
                    for (; e >= 0; e--)
                    {
                        if (code[e] == '>') g++;
                        else if (code[e] == '<' && --g == 0) { e--; break; }
                    }
                }
                int s = e;
                while (s >= 0 && (char.IsLetterOrDigit(code[s]) || code[s] == '_')) s--;
                string name = code.Substring(s + 1, e - s);
                if (name == "new")
                    return ("new", arg);
                // new Foo(...)
                int b = s;
                while (b >= 0 && char.IsWhiteSpace(code[b])) b--;
                return (name.Length == 0 ? null : name, arg);
            }
            else if (c == ',' && depth == 0) arg++;
            else if (c == ';' && depth == 0) return (null, arg);
        }
        return (null, arg);
    }

    private static string StatementPrefix(string code, int pos)
    {
        int k = pos - 1;
        while (k >= 0 && code[k] != ';' && code[k] != '{' && code[k] != '}')
            k--;
        return code.Substring(k + 1, pos - k - 1);
    }

    // "x" == a, a != "x", case "x", "x" => (a switch pattern), dict["x"], is "x", a ?? "x" is fine.
    private static bool IsComparisonOrKey(string code, Literal literal)
    {
        string before = Before(code, literal.Start, 3);
        string after = After(code, literal.End, 3);
        if (before.EndsWith("==") || before.EndsWith("!=") || before.EndsWith("is") || after.StartsWith("==") || after.StartsWith("!="))
            return true;
        if (after.StartsWith("=>") && !before.EndsWith("=>"))
        {
            // a switch expression arm: "dcs" => "DCS World" (the left side is a pattern)
            return true;
        }
        if (before.EndsWith("[") && after.StartsWith("]"))
            return true;
        if (before.EndsWith("case"))
            return true;
        return false;
    }

    private static string Before(string code, int pos, int n)
    {
        int k = pos - 1;
        while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
        int s = Math.Max(0, k - n + 1);
        string text = code.Substring(s, k - s + 1);
        // keywords: whole word before
        int w = k;
        while (w >= 0 && char.IsLetter(code[w])) w--;
        if (w < k)
            return code.Substring(w + 1, k - w);
        return text;
    }

    private static string After(string code, int pos, int n)
    {
        int k = pos;
        while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
        return code.Substring(k, Math.Min(n, code.Length - k));
    }

    // The concatenation the literal is part of: "Exported " + n + " binds" -> "Exported {0} binds".
    private static (string Text, List<Literal> Parts, List<(int Start, int End)> Args, int Start, int End) Chain(string code, List<Literal> literals, Literal literal)
    {
        Dictionary<int, Literal> byStart = literals.Where(l => l.Depth == 0).ToDictionary(l => l.Start);
        // Walk back over "operand +" pairs to the chain's first operand.
        List<(int Start, int End)> operands = [(literal.Start, literal.End)];
        int pos = literal.Start;
        while (true)
        {
            int k = pos - 1;
            while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
            if (k < 1 || code[k] != '+' || code[k - 1] == '+' || code[k - 1] == '=')
                break;
            int opEnd = k;
            k--;
            while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
            int opStart = OperandStartBack(code, k);
            if (opStart < 0)
                break;
            operands.Insert(0, (opStart, k + 1));
            pos = opStart;
        }
        pos = literal.End;
        while (true)
        {
            int k = pos;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            if (k + 1 >= code.Length || code[k] != '+' || code[k + 1] == '+' || code[k + 1] == '=')
                break;
            k++;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            int opEnd = OperandEndForward(code, k);
            if (opEnd <= k)
                break;
            operands.Add((k, opEnd));
            pos = opEnd;
        }
        if (operands.Count == 1)
            return (literal.Text, [literal], literal.HoleExprs, literal.Start, literal.End);

        StringBuilder text = new();
        List<Literal> parts = [];
        List<(int, int)> args = [];
        int hole = 0;
        bool anyLiteral = false;
        foreach ((int start, int end) in operands)
        {
            if (byStart.TryGetValue(start, out Literal? l) && l.End == end)
            {
                anyLiteral = true;
                parts.Add(l);
                // renumber the literal's own holes
                int offset = hole;
                text.Append(l.Interpolated ? Regex.Replace(EscapeBraces(l), @"\{(\d+)", m => "{" + (int.Parse(m.Groups[1].Value) + offset)) : l.Text.Replace("{", "{{").Replace("}", "}}"));
                hole += l.Holes;
                args.AddRange(l.HoleExprs);
            }
            else
            {
                text.Append('{').Append(hole++).Append('}');
                args.Add((start, end));
            }
        }
        if (!anyLiteral)
            return (literal.Text, [literal], literal.HoleExprs, literal.Start, literal.End);
        string result = text.ToString();
        // a chain of literals only (no operand): plain text again
        if (hole == 0)
            result = result.Replace("{{", "{").Replace("}}", "}");
        return (result, parts, args, operands[0].Start, operands[^1].End);
    }

    private static string EscapeBraces(Literal l) => l.Text;

    private static int OperandStartBack(string code, int k)
    {
        // a literal, an identifier chain with calls/indexers, or a parenthesized expression
        int start = k;
        while (start >= 0)
        {
            char c = code[start];
            if (c == ')' || c == ']')
            {
                int depth = 0;
                for (; start >= 0; start--)
                {
                    if (code[start] == ')' || code[start] == ']') depth++;
                    else if (code[start] == '(' || code[start] == '[')
                    {
                        if (--depth == 0) break;
                    }
                }
                start--;
                continue;
            }
            if (c == '"')
            {
                // the closing quote of a literal: the operand starts at the literal (its contents are blanked)
                if (literalStarts != null && literalStarts.TryGetValue(start + 1, out int literalStart) && start == k)
                    return literalStart;
                return -1;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' || c == '!')
            {
                start--;
                continue;
            }
            break;
        }
        int s = start + 1;
        while (s <= k && char.IsWhiteSpace(code[s])) s++;
        if (s > k)
            return -1;
        // the keyword "return" or "new" is not part of an operand
        string word = code[s..(k + 1)];
        if (word is "return" or "new" or "await")
            return -1;
        // "new Uri(x).Host": the operand starts at "new"
        int b = s - 1;
        while (b >= 0 && char.IsWhiteSpace(code[b])) b--;
        if (b >= 2 && code.Substring(b - 2, 3) == "new" && (b < 3 || !char.IsLetterOrDigit(code[b - 3]) && code[b - 3] != '_'))
            return b - 2;
        return s;
    }

    private static int OperandEndForward(string code, int k)
    {
        int i = k;
        // "new Uri(x).Host", "await X"
        foreach (string keyword in new[] { "new ", "await " })
        {
            if (string.CompareOrdinal(code, i, keyword, 0, keyword.Length) == 0)
            {
                i += keyword.Length;
                while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
            }
        }
        if (i < code.Length && (code[i] == '"' || code[i] == '$' || code[i] == '@'))
        {
            // find the literal's end: the next non-blank after the blanked contents is past the closing quote; the
            // skeleton keeps the closing quote characters blank, so use the literal list instead (caller matches)
            return LiteralEnd(i);
        }
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '(' || c == '[')
            {
                int depth = 0;
                for (; i < code.Length; i++)
                {
                    if (code[i] == '(' || code[i] == '[') depth++;
                    else if (code[i] == ')' || code[i] == ']')
                    {
                        if (--depth == 0) break;
                    }
                }
                i++;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' && i + 1 < code.Length && code[i + 1] == '.' || c == '!')
            {
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    // Set before scanning a file: the top-level literals by start and by end, for the walks over a concatenation.
    [ThreadStatic] private static Dictionary<int, int>? literalEnds;
    [ThreadStatic] private static Dictionary<int, int>? literalStarts;

    private static int LiteralEnd(int start) =>
        literalEnds != null && literalEnds.TryGetValue(start, out int end) ? end : -1;

    private static int[] LineStarts(string content)
    {
        List<int> starts = [0];
        for (int i = 0; i < content.Length; i++)
            if (content[i] == '\n')
                starts.Add(i + 1);
        return starts.ToArray();
    }

    private static int LineOf(int[] starts, int pos)
    {
        int index = Array.BinarySearch(starts, pos);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    // ---- What counts as a user text -------------------------------------------------------------------------------

    private static readonly Regex Word = new(@"\p{L}{2,}", RegexOptions.Compiled);

    // lenient: a literal XAML text property (always shown): lower-case words ("devices connected", "modifiers") count too.
    public static bool IsUiText(string? raw, bool lenient = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string text = raw.Trim();
        string letters = Placeholder.Replace(text, " ");
        if (!Word.IsMatch(letters))
            return false;
        // font family lists
        if (Regex.IsMatch(text, @"\b(Segoe UI|Bahnschrift|Arial|Consolas|Cascadia|Yu Gothic|Meiryo|YaHei|JhengHei|Malgun)\b") && (text.Contains(',') || !text.Contains(' ') || text.Split(' ').Length <= 3))
            return false;
        // command lines, regular expressions, internal notes
        if (text.StartsWith('-') && text.Length > 1 && char.IsLetter(text[1]) || text.StartsWith('/') || text.StartsWith('^')
            || text.Contains("(?<", StringComparison.Ordinal) || text.StartsWith("[gui-", StringComparison.Ordinal)
            || text.StartsWith("diag:", StringComparison.Ordinal) || text.StartsWith("**", StringComparison.Ordinal))
            return false;
        if (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith("avares:", StringComparison.Ordinal)
            || text.StartsWith("http", StringComparison.OrdinalIgnoreCase) || text.StartsWith('#') || text.StartsWith('<')
            || text.StartsWith("{\"", StringComparison.Ordinal) || text.StartsWith("[\"", StringComparison.Ordinal))
            return false;
        if (Regex.IsMatch(text, @"^[A-Za-z]:\\|^\\\\|^%[A-Z_]+%|\.(exe|dll|json|lua|xml|ini|cfg|png|jpg|txt|log|ps1|cmd|bat|zip|axaml|pr0file|jp)$", RegexOptions.IgnoreCase))
            return false;
        string joined = Placeholder.Replace(text, "X").Trim();
        bool hasSpace = joined.Contains(' ');
        if (!hasSpace)
        {
            // an id, a query, a path pattern or a regular expression
            if (joined.IndexOfAny(['_', '/', '?', '=', '&', '\\', '(', '<', '|', '[', ',', ':']) >= 0 && !joined.EndsWith(':'))
                return false;
            // One word: a capitalized word ("Ready", "Export", "Rename...") or "OK"; not an id ("dcs", "WarThunder",
            // "add-prefix", "F-16C", "ui-settings.json").
            string w = letters.Trim().TrimEnd('.', '…', ':', '!', '?').Trim();
            if (w == "OK")
                return true;
            if (lenient && w.Length >= 2 && w.All(char.IsLetter))
                return true;
            if (w.Length < 2 || !char.IsUpper(w[0]))
                return false;
            if (w.Skip(1).Any(ch => !char.IsLower(ch) && ch != '\'' && ch != '-'))
                return false;
            if (w.Contains('-') && !w.Split('-').All(p => p.Length > 0 && char.IsUpper(p[0])))
                return false;
            return true;
        }
        // Several words: not code or a path.
        if (Regex.IsMatch(text, @"[;=]\s*$|=>|&&|\|\||\(\)|::|\$\{|^\w+\(|\\[A-Za-z]+\\"))
            return false;
        // a list of ids ("dcs il2 wt")
        if (!lenient && Regex.IsMatch(letters.Trim(), @"^[a-z0-9_.-]+(\s+[a-z0-9_.-]+)*$") && !Regex.IsMatch(letters, @"\b(the|a|an|to|of|in|on|no|not|is|are|and|or|for|with|from)\b"))
            return false;
        return true;
    }

    private static string Normalize(string value) => value;

    // ---- Inventory --------------------------------------------------------------------------------------------------

    // The distinct texts (keys) of a scan, in first-seen order, merged: a text found wrapped and plain is one key.
    public static List<InventoryEntry> Inventory(IEnumerable<FoundText> found)
    {
        Dictionary<string, InventoryEntry> byKey = new(StringComparer.Ordinal);
        List<InventoryEntry> list = [];
        foreach (FoundText f in found)
        {
            if (!byKey.TryGetValue(f.Text, out InventoryEntry? entry))
            {
                entry = new InventoryEntry(f.Text);
                byKey[f.Text] = entry;
                list.Add(entry);
            }
            entry.Plural ??= f.Plural;
            if (entry.Kind.Length == 0) entry.Kind = f.Kind;
            entry.Wrapped |= f.Wrapped;
            if (entry.Locations.Count < 5)
                entry.Locations.Add(f.File + ":" + f.Line);
            entry.Uses++;
        }
        return list;
    }

}

public sealed class InventoryEntry(string text)
{
    public string Text { get; } = text;
    public string? Plural { get; set; }
    public string Kind { get; set; } = string.Empty;
    public bool Wrapped { get; set; }
    public int Uses { get; set; }
    public List<string> Locations { get; } = [];
}
