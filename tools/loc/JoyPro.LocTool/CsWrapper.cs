using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.LocTool;

// Rewrites user texts in C# to the localization (docs/localization.md): "Ready" -> T("Ready"), $"Exported {n} binds"
// and "Exported " + n + " binds" -> T("Exported {0} binds", n). Only texts that go to a user-visible place: not ids,
// keys, modes, paths, names (data), log lines, JSON fields, static tables (made before the language is known), and not
// interpolations with escaped braces or alignments. What it leaves is listed for a manual look.
public static class CsWrapper
{
    private static readonly Regex DenyOwner = new(
        @"^(_?[iI]d|.*Id|[kK]ey|.*Key|Keywords|[mM]ode|ThemeMode|OverlayCorner.*|Corner|Exporter|Tag|[kK]ind|Category|Step|Shift|Method|[sS]ource|[tT]ype|[sS]tate|[vV]alue|[pP]ath|.*Path|.*File|[fF]ile|[fF]older|.*Folder|[uU]rl|.*Url|Uri|[aA]rgs|Arguments|[cC]ommand|[sS]ection|[fF]ilter|[pP]attern|[fF]ormat|[tT]oken|[rR]ole|[hH]and|[eE]xe|.*Name|[nN]ame|Exes|Bands|AssistantExamples|.*Examples|XboxButtonNames|FailedAt|tableName|Fingerprint|Glyph|Icon|FontFamily|Font)$",
        RegexOptions.Compiled);

    private static readonly Regex DenyCall = new(
        @"^(Read.*|Get.*|Json.*|Write.*|Parse.*|Try.*|Info|Warn|Error|Debug|Trace|Log.*|.*Log|Equals|Contains.*|StartsWith|EndsWith|Replace|Split|IndexOf|Matches|Match|IsMatch|Regex|Combine|Path|SetValue|Push|Select|Where|OrderBy|ThenBy|Any|All|First.*|Single.*|Last.*|Count|ToDictionary|GroupBy|Distinct|Except|Intersect|Union|Concat|Join|Format|ProcessStartInfo|Exists|Open.*|Create.*Key|Delete.*|nameof|ContainsKey|Remove|Insert|Find.*|Run.*|Execute.*|Invoke.*|Argument.*|Arg|Flag|Option|Add)\($",
        RegexOptions.Compiled);

    // Calls whose arguments are user texts although the name says nothing.
    private static readonly HashSet<string> AllowCall = new(StringComparer.Ordinal)
    {
        "ConfirmAsync(", "NextStepItem(", "Done(", "ReportVisualAssign(", "Text(", "AppendLine(", "Append(", "SetStatus(",
        "ShowStatus(", "Notify(", "Fail(", "AskAsync(", "Ask(", "MessageAsync(", "ShowAsync(", "Report("
    };

    public sealed record Result(string Content, int Wrapped, List<FoundText> Left);

    public static Result Wrap(string file, string content)
    {
        List<FoundText> found = LocExtractor.ScanCSharp(file, content).Where(f => !f.Wrapped && f.Start >= 0).ToList();
        List<FoundText> take = [];
        List<FoundText> left = [];
        foreach (FoundText f in found)
        {
            if (Accept(f, content, out _))
                take.Add(f);
            else
                left.Add(f);
        }
        // A text inside another one's span (a ternary in a concatenation) waits for the next pass: its code is copied
        // into the outer T(...) as an argument and wrapped there.
        take = take.Where(f => !take.Any(o => !ReferenceEquals(o, f) && o.Start <= f.Start && f.End <= o.End)).ToList();
        StringBuilder sb = new(content);
        foreach (FoundText f in take.OrderByDescending(f => f.Start))
        {
            string call = "T(" + Literal(f.Text) + string.Concat(f.Args.Select(a => ", " + a)) + ")";
            sb.Remove(f.Start, f.End - f.Start).Insert(f.Start, call);
        }
        return new Result(sb.ToString(), take.Count, left);
    }

    public static bool Accept(FoundText f, string content, out string why)
    {
        why = string.Empty;
        if (f.Unsafe) { why = "unsafe interpolation"; return false; }
        if (f.Text.Contains("Copyright", StringComparison.Ordinal)) { why = "license"; return false; }
        string owner = f.Owner;
        if (owner.Length == 0) { why = "no owner"; return false; }
        if (owner.EndsWith('('))
        {
            if (AllowCall.Contains(owner)) return true;
            if (owner == "Add(")
            {
                // problems.Add("..."), lines.Add(...): a text; args.Add("--x"): an argument
                string before = content[Math.Max(0, f.Start - 60)..f.Start];
                if (Regex.IsMatch(before, @"(?i)arg\w*\s*\.\s*Add\s*\(\s*$|(keys|ids|names|set|seen|known)\s*\.\s*Add\s*\(\s*$"))
                {
                    why = "argument list";
                    return false;
                }
                return true;
            }
            if (DenyCall.IsMatch(owner)) { why = "call " + owner; return false; }
            // a constructor or other call: only when it is clearly a text (a sentence or a capitalized phrase)
            return f.Text.Contains(' ');
        }
        if (owner.StartsWith("return:", StringComparison.Ordinal))
        {
            string member = owner["return:".Length..];
            if (DenyOwner.IsMatch(member)) { why = "returns an id"; return false; }
            return true;
        }
        if (DenyOwner.IsMatch(owner)) { why = "owner " + owner; return false; }
        // static tables and constants are made before the language is set
        if (InStaticInitializer(content, f.Start)) { why = "static"; return false; }
        return true;
    }

    private static readonly Regex Declaration = new(@"^[ \t]*(?:(?:public|private|internal|protected|static|readonly|const|override|virtual|async|partial|sealed|new)\s+)+[^\n]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    // A static (or const) field initializer: "static readonly X[] Table = [ ... "text" ... ];"
    private static bool InStaticInitializer(string content, int pos)
    {
        MatchCollection decls = Declaration.Matches(content[..pos]);
        if (decls.Count == 0)
            return false;
        string decl = decls[^1].Value;
        if (!Regex.IsMatch(decl, @"\b(static|const)\b"))
            return false;
        // the initializer is still open (no ';' since the declaration)
        if (content.IndexOf(';', decls[^1].Index, pos - decls[^1].Index) >= 0)
            return false;
        // a method ("static string Describe(int n) {" / "=> ..."), not a field; a tuple-typed field "(string A, ...)[] X =" is one
        if (Regex.IsMatch(decl, @"\w+\s*\([^)]*\)\s*(=>|\{|$)") && !Regex.IsMatch(decl, @"\)\s*\[\]\s*\w+\s*="))
            return false;
        return Regex.IsMatch(decl, @"[A-Za-z_]\w*\s*=(?!>)");
    }

    public static string Literal(string text)
    {
        StringBuilder sb = new("\"");
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (c < 32)
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
