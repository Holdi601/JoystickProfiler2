#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Readable names of DCS commands (shared by the engine and the GUI).
    //
    // Some modules name their commands by a localization key instead of English text: "Input.SpitfireLFMkIX.pitch_minus",
    // "29Fulcrum:Inp:AB Lock Latch (Both) - Depress/Release", categories "29Fulcrum:Cat:HUD". DCS shows them through
    // gettext: Scripts/Input/Data.lua loads the input files with "_ = InputUtils.localizeInputString", which is
    // i_18n.dtranslate('input', s), and the English text lives in <DCS>\l10n\en\LC_MESSAGES\input.mo. The DB refresh
    // translates them the same way (EngineDcsRefresh), and a DB built before that is translated when it is read.
    //
    // Resolution order for a title (Resolve): the DB title when it is readable -> the English DCS text of the key ->
    // the title the profile stored -> the same command's title in a sibling DB file (_easy, CW) -> a readable form of the
    // key ("Flaps 1"). Never the raw key or the raw command id.
    public static class DcsCommandNames
    {
        private static readonly Regex InputKey = new Regex(@"^Input\.[^.\s]+\.\S+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PrefixedKey = new Regex(@"^[A-Za-z0-9_\-]+:(?<kind>Inp|Cat):(?<text>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // DCS command ids as JoyPro stores them: "d3114pnilunilcd63vd0.7vpnilvunil", "a2001cdnil".
        private static readonly Regex CommandId = new Regex(@"^[ad](?:nil|-?[\d.]+)(?:(?:p|u|cd|vd|vp|vu)(?:nil|-?[\d.]+))*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // The English input names of the DCS install: the "input" gettext domain DCS translates command names with.
        public static readonly string[] EnglishCatalogFiles =
        {
            Path.Combine("l10n", "en", "LC_MESSAGES", "input.mo"),
        };

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> CatalogByInstall = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // A localization key ("Input.X.y", "X:Inp:...", "X:Cat:...").
        public static bool IsLocalizationKey(string text)
        {
            string value = (text ?? string.Empty).Trim();
            return value.Length > 0 && (InputKey.IsMatch(value) || PrefixedKey.IsMatch(value));
        }

        // A localization key alone or in a "a; b" list (DCS joins several categories that way).
        public static bool HasLocalizationKey(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && SplitList(text.Trim()).Any(IsLocalizationKey);
        }

        public static bool LooksLikeCommandId(string text)
        {
            string value = (text ?? string.Empty).Trim();
            return value.Length > 1 && CommandId.IsMatch(value) && (value.IndexOf("nil", StringComparison.Ordinal) >= 0 || value.IndexOf("cd", StringComparison.Ordinal) >= 0);
        }

        // A title the user must never see as it is: empty, the command id, a localization key (also inside a "; " list).
        public static bool IsRawTitle(string title, string id = null)
        {
            string value = (title ?? string.Empty).Trim();
            if (value.Length == 0)
                return true;
            if (!string.IsNullOrEmpty(id) && string.Equals(value, id.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (LooksLikeCommandId(value))
                return true;
            return SplitList(value).Any(IsLocalizationKey);
        }

        // "Input.SpitfireLFMkIX.flaps_1" -> "Flaps 1", "29Fulcrum:Inp:AB Lock" -> "AB Lock"; other text stays.
        public static string ReadableKey(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.IndexOf("; ", StringComparison.Ordinal) >= 0)
                return string.Join("; ", SplitList(value).Select(ReadableKey));
            Match prefixed = PrefixedKey.Match(value);
            if (prefixed.Success)
            {
                string rest = prefixed.Groups["text"].Value.Trim();
                return rest.Length == 0 ? value : rest;
            }
            if (!InputKey.IsMatch(value))
                return value;
            string last = value.Substring(value.LastIndexOf('.') + 1).Replace('_', ' ').Trim();
            return last.Length == 0 ? value : char.ToUpperInvariant(last[0]) + last.Substring(1);
        }

        // DCS's English text of the localization keys in a name or category ("a; b" lists part by part), or null.
        // Text that already is English stays as it is (the same rule as the DB refresh, EngineDcsRefresh).
        public static string English(string text, IDictionary<string, string> catalog)
        {
            if (catalog == null || catalog.Count == 0 || string.IsNullOrWhiteSpace(text))
                return null;
            string value = text.Trim();
            List<string> parts = SplitList(value);
            bool any = false;
            for (int i = 0; i < parts.Count; i++)
            {
                string translated;
                if (IsLocalizationKey(parts[i]) && catalog.TryGetValue(parts[i], out translated) && !string.IsNullOrWhiteSpace(translated))
                {
                    parts[i] = translated.Trim();
                    any = true;
                }
            }
            return any ? string.Join("; ", parts) : null;
        }

        // The title to show (see the class comment). siblingTitle is asked only when needed.
        public static string Resolve(string dbTitle, string id, IDictionary<string, string> catalog = null, string storedTitle = null, Func<string> siblingTitle = null)
        {
            if (!IsRawTitle(dbTitle, id))
                return dbTitle.Trim();
            string english = English(dbTitle, catalog);
            if (!string.IsNullOrWhiteSpace(english) && !IsRawTitle(english, id))
                return english;
            if (!IsRawTitle(storedTitle, id))
                return storedTitle.Trim();
            english = English(storedTitle, catalog);
            if (!string.IsNullOrWhiteSpace(english) && !IsRawTitle(english, id))
                return english;
            string sibling = siblingTitle == null ? null : siblingTitle();
            if (!IsRawTitle(sibling, id))
                return sibling.Trim();
            foreach (string candidate in new[] { dbTitle, storedTitle })
            {
                if (IsLocalizationKey(candidate) || SplitList(candidate ?? string.Empty).Any(IsLocalizationKey))
                    return ReadableKey(candidate);
            }
            return string.Empty;
        }

        // A category to show: English text of a key, else a readable form; plain text stays.
        public static string ResolveCategory(string category, IDictionary<string, string> catalog = null, string storedCategory = null)
        {
            string value = (category ?? string.Empty).Trim();
            if (value.Length == 0)
                value = (storedCategory ?? string.Empty).Trim();
            if (!SplitList(value).Any(IsLocalizationKey))
                return value;
            string english = English(value, catalog);
            if (!string.IsNullOrWhiteSpace(english) && !SplitList(english).Any(IsLocalizationKey))
                return english;
            if (!string.IsNullOrWhiteSpace(storedCategory) && !SplitList(storedCategory).Any(IsLocalizationKey))
                return storedCategory.Trim();
            return ReadableKey(english ?? value);
        }

        // A search text that names a command by its key or id rather than by words: one token with "_", "." or ":"
        // ("flaps_1", "Input.SpitfireLFMkIX.pitch_minus", "29Fulcrum:Inp:Eject") or a DCS command id.
        public static bool LooksLikeKeyQuery(string query)
        {
            string q = (query ?? string.Empty).Trim();
            if (PrefixedKey.IsMatch(q))
                return true;
            if (q.Length < 3 || q.IndexOf(' ') >= 0)
                return false;
            return q.IndexOf('_') >= 0 || q.IndexOf('.') >= 0 || q.IndexOf(':') >= 0 || LooksLikeCommandId(q);
        }

        // Whether a command with this (readable) title is named by a key the search text points at: the title itself is
        // the key (a list built before the refresh translated keys), or DCS's English catalog has a key with this text.
        // The key is matched whole, by its last part ("pitch_minus") or, from 5 letters on, anywhere in it.
        public static bool KeyMatches(string query, string title, IDictionary<string, string> catalog)
        {
            string q = (query ?? string.Empty).Trim();
            if (!LooksLikeKeyQuery(q) || string.IsNullOrWhiteSpace(title))
                return false;
            if (IsLocalizationKey(title) && KeyContains(title.Trim(), q))
                return true;
            foreach (string key in KeysFor(title, catalog))
            {
                if (KeyContains(key, q))
                    return true;
            }
            return false;
        }

        private static bool KeyContains(string key, string q)
        {
            return string.Equals(key, q, StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith("." + q, StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith(":" + q, StringComparison.OrdinalIgnoreCase) ||
                   (q.Length >= 5 && key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDictionary<string, string>, Dictionary<string, List<string>>> ReverseCatalogs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<IDictionary<string, string>, Dictionary<string, List<string>>>();

        // The localization keys whose English text is this title (empty without a catalog).
        public static IReadOnlyList<string> KeysFor(string title, IDictionary<string, string> catalog)
        {
            if (catalog == null || catalog.Count == 0 || string.IsNullOrWhiteSpace(title))
                return new string[0];
            Dictionary<string, List<string>> reverse = ReverseCatalogs.GetValue(catalog, c =>
            {
                Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> entry in c)
                {
                    if (!IsLocalizationKey(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                        continue;
                    List<string> keys;
                    if (!map.TryGetValue(entry.Value.Trim(), out keys))
                        map[entry.Value.Trim()] = keys = new List<string>();
                    keys.Add(entry.Key);
                }
                return map;
            });
            List<string> found;
            return reverse.TryGetValue(title.Trim(), out found) ? (IReadOnlyList<string>)found : new string[0];
        }

        private static List<string> SplitList(string value)
        {
            return (value ?? string.Empty).Split(new[] { "; " }, StringSplitOptions.None).Select(p => p.Trim()).ToList();
        }

        // The English input names of a DCS install (cached per install; empty when there is none).
        public static Dictionary<string, string> EnglishCatalog(string installPath)
        {
            string root = (installPath ?? string.Empty).Trim();
            if (root.Length == 0 || !Directory.Exists(root))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            lock (Sync)
            {
                Dictionary<string, string> cached;
                if (CatalogByInstall.TryGetValue(root, out cached))
                    return cached;
                Dictionary<string, string> catalog = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string relative in EnglishCatalogFiles)
                {
                    foreach (KeyValuePair<string, string> entry in ReadMoFile(Path.Combine(root, relative)))
                    {
                        if (!catalog.ContainsKey(entry.Key))
                            catalog[entry.Key] = entry.Value;
                    }
                }
                CatalogByInstall[root] = catalog;
                return catalog;
            }
        }

        internal static void ClearCache()
        {
            lock (Sync)
                CatalogByInstall.Clear();
        }

        // A gettext .mo file: msgid -> msgstr (the header, contexts and empty translations are skipped).
        public static Dictionary<string, string> ReadMoFile(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] data;
            try
            {
                if (!File.Exists(path))
                    return result;
                data = File.ReadAllBytes(path);
            }
            catch
            {
                return result;
            }
            return ParseMo(data);
        }

        public static Dictionary<string, string> ParseMo(byte[] data)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data == null || data.Length < 28)
                return result;
            bool little;
            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic == 0x950412de)
                little = true;
            else if (magic == 0xde120495)
                little = false;
            else
                return result;
            Func<int, uint> u32 = offset =>
            {
                if (offset < 0 || offset + 4 > data.Length)
                    return 0;
                return little
                    ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
                    : (uint)(data[offset + 3] | data[offset + 2] << 8 | data[offset + 1] << 16 | data[offset] << 24);
            };
            uint count = u32(8);
            uint originals = u32(12);
            uint translations = u32(16);
            for (uint i = 0; i < count; i++)
            {
                long o = originals + 8L * i;
                long t = translations + 8L * i;
                if (o + 8 > data.Length || t + 8 > data.Length)
                    break;
                uint keyLength = u32((int)o), keyOffset = u32((int)o + 4);
                uint valueLength = u32((int)t), valueOffset = u32((int)t + 4);
                if ((long)keyOffset + keyLength > data.Length || (long)valueOffset + valueLength > data.Length || keyLength == 0)
                    continue;
                string key = Encoding.UTF8.GetString(data, (int)keyOffset, (int)keyLength);
                if (key.IndexOf('\u0004') >= 0)
                    continue;
                int nul = key.IndexOf('\0');
                if (nul >= 0)
                    key = key.Substring(0, nul);
                string value = Encoding.UTF8.GetString(data, (int)valueOffset, (int)valueLength);
                nul = value.IndexOf('\0');
                if (nul >= 0)
                    value = value.Substring(0, nul);
                if (value.Length > 0 && !result.ContainsKey(key))
                    result[key] = value;
            }
            return result;
        }
    }
}
