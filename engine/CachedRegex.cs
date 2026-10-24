using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Regex.Match/Matches/IsMatch with the pattern built once and kept: the static Regex methods share one small cache
    // behind a lock, which made the DCS file parsing build patterns again and again and serialized parallel parsing.
    internal static class CachedRegex
    {
        private static readonly ConcurrentDictionary<string, Regex> Patterns = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        public static Regex Get(string pattern, RegexOptions options = RegexOptions.None)
        {
            string key = ((int)options).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + pattern;
            return Patterns.GetOrAdd(key, _ => new Regex(pattern, options));
        }

        public static Match Match(string input, string pattern, RegexOptions options = RegexOptions.None)
        {
            return Get(pattern, options).Match(input);
        }

        public static MatchCollection Matches(string input, string pattern, RegexOptions options = RegexOptions.None)
        {
            return Get(pattern, options).Matches(input);
        }

        public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None)
        {
            return Get(pattern, options).IsMatch(input);
        }
    }
}
