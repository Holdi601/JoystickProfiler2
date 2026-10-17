using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace JoyPro.Gui.Views.Controls;

public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string>(nameof(SourceText), string.Empty);

    public static readonly StyledProperty<string> QueryProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string>(nameof(Query), string.Empty);

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    static HighlightTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightTextBlock>((ctrl, _) => ctrl.RebuildInlines());
        QueryProperty.Changed.AddClassHandler<HighlightTextBlock>((ctrl, _) => ctrl.RebuildInlines());
    }

    private void RebuildInlines()
    {
        Inlines?.Clear();

        string text = SourceText ?? string.Empty;
        string query = Query ?? string.Empty;
        if (text.Length == 0)
            return;

        List<(int Start, int Length)> segments = FindHighlightSegments(text, query);
        if (segments.Count == 0)
        {
            Inlines?.Add(new Run(text));
            return;
        }

        int cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            (int start, int length) = segments[i];
            if (start > cursor)
                Inlines?.Add(new Run(text.Substring(cursor, start - cursor)));

            Run run = new Run(text.Substring(start, length))
            {
                Background = new SolidColorBrush(Color.Parse("#F59E0B")),
                Foreground = new SolidColorBrush(Color.Parse("#111827"))
            };
            Inlines?.Add(run);
            cursor = start + length;
        }

        if (cursor < text.Length)
            Inlines?.Add(new Run(text.Substring(cursor)));
    }

    private static List<(int Start, int Length)> FindHighlightSegments(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
            return [];

        string[] terms = query
            .Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            return [];

        List<(int Start, int Length)> matches = [];
        for (int t = 0; t < terms.Length; t++)
        {
            string term = terms[t];
            int index = 0;
            while (index < text.Length)
            {
                int found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                matches.Add((found, term.Length));
                index = found + Math.Max(term.Length, 1);
            }
        }

        if (matches.Count == 0)
            return [];

        matches = matches
            .OrderBy(v => v.Start)
            .ThenByDescending(v => v.Length)
            .ToList();

        List<(int Start, int Length)> merged = [matches[0]];
        for (int i = 1; i < matches.Count; i++)
        {
            (int start, int length) = matches[i];
            (int mStart, int mLength) = merged[merged.Count - 1];
            int mEnd = mStart + mLength;
            int end = start + length;
            if (start <= mEnd)
            {
                merged[merged.Count - 1] = (mStart, Math.Max(mEnd, end) - mStart);
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }
}
