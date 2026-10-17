using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.LocTool;

// Rewrites the literal texts of an .axaml file to the localization (docs/localization.md), keeping the file's own
// formatting: Content="Export" -> Content="{l:T 'Export'}", Text="{Binding N, StringFormat='Size: {0}'}" ->
// Text="{l:T 'Size: {0}', Arg0={Binding N}}". Adds xmlns:l to the root element. Texts that are not user texts
// (LocExtractor.IsUiText) and texts in a MultiBinding stay; element content (<TextBlock>text</TextBlock>) is reported
// for a manual change.
public static class AxamlWrapper
{
    private static readonly string[] Properties =
    [
        "Text", "Content", "Header", "ToolTip.Tip", "Watermark", "PlaceholderText", "Title", "Label", "OnContent", "OffContent",
        "Description", "AutomationProperties.Name", "AutomationProperties.HelpText"
    ];

    private static readonly Regex Attribute = new(
        @"(?<pre>\s)(?<name>" + string.Join("|", Properties.Select(Regex.Escape)) + @")=""(?<value>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex SetterValue = new(
        @"(?<pre><Setter\s+Property=""(?:" + string.Join("|", Properties.Select(Regex.Escape)) + @")""\s+Value="")(?<value>[^""]*)""",
        RegexOptions.Compiled);

    public sealed record Result(string Content, int Wrapped, List<string> Skipped);

    public static Result Wrap(string content)
    {
        int wrapped = 0;
        List<string> skipped = [];
        string Replace(string raw)
        {
            string value = WebUtility.HtmlDecode(raw);
            if (value.StartsWith("{}", StringComparison.Ordinal))
                value = value[2..];
            else if (value.StartsWith('{'))
            {
                string? converted = ConvertStringFormat(value);
                if (converted == null)
                    return raw;
                wrapped++;
                return XmlEscape(converted);
            }
            if (!LocExtractor.IsUiText(value, lenient: true))
                return raw;
            wrapped++;
            return XmlEscape("{l:T " + LocExtractor.QuoteMarkup(value) + "}");
        }

        string result = Attribute.Replace(content, m => m.Groups["pre"].Value + m.Groups["name"].Value + "=\"" + Replace(m.Groups["value"].Value) + "\"");
        result = SetterValue.Replace(result, m => m.Groups["pre"].Value + Replace(m.Groups["value"].Value) + "\"");
        if (wrapped > 0 && !result.Contains("xmlns:l=\"using:JoyPro.Gui.Localization\"", StringComparison.Ordinal))
        {
            Match root = Regex.Match(result, @"(?<indent>\r?\n[ \t]*)xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""");
            if (root.Success)
                result = result.Insert(root.Index + root.Length, root.Groups["indent"].Value + "xmlns:l=\"using:JoyPro.Gui.Localization\"");
            else
            {
                Match first = Regex.Match(result, @"xmlns=""https://github.com/avaloniaui""");
                result = result.Insert(first.Index + first.Length, " xmlns:l=\"using:JoyPro.Gui.Localization\"");
            }
        }
        foreach (Match m in Regex.Matches(result, @"<(TextBlock|Run|Button|CheckBox)[^>]*>(?<text>[^<]*\p{L}{2,}[^<]*)</"))
            skipped.Add("element text: " + m.Groups["text"].Value.Trim());
        return new Result(result, wrapped, skipped);
    }

    // {Binding X, StringFormat='Size: {0}'} -> {l:T 'Size: {0}', Arg0={Binding X}}; null when it is not that.
    internal static string? ConvertStringFormat(string binding)
    {
        Match m = Regex.Match(binding, @"^\{(?<kind>Binding|CompiledBinding|ReflectionBinding)\b(?<args>.*)\}$", RegexOptions.Singleline);
        if (!m.Success)
            return null;
        string args = m.Groups["args"].Value;
        Match format = Regex.Match(args, @",?\s*StringFormat\s*=\s*(?:'(?<q>(?:[^'\\]|\\.)*)'|(?<u>\{\}[^,}]*(?:\{\d+[^}]*\}[^,}]*)*))");
        if (!format.Success)
            return null;
        string text = format.Groups["q"].Success ? LocExtractor.UnquoteMarkup("'" + format.Groups["q"].Value + "'") : format.Groups["u"].Value;
        if (text.StartsWith("{}", StringComparison.Ordinal))
            text = text[2..];
        if (!LocExtractor.IsUiText(text))
            return null;
        string rest = args.Remove(format.Index, format.Length).Trim().TrimStart(',').Trim();
        string inner = "{" + m.Groups["kind"].Value + (rest.Length > 0 ? " " + rest : string.Empty) + "}";
        return "{l:T " + LocExtractor.QuoteMarkup(text) + ", Arg0=" + inner + "}";
    }

    private static string XmlEscape(string value)
    {
        StringBuilder sb = new(value.Length + 16);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
