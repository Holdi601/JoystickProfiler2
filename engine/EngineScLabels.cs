using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.CockpitPictures;

namespace JoyPro.Engine
{
    // Star Citizen's own names of its controls ("Pitch" for v_pitch, docs/games/star-citizen.md "Action names"). The
    // command list (AllBinds.xml) names each action's text key ("<!--@ui_CIPitch//true-->"); the English texts are in
    // Data\Localization\english\global.ini inside Data.p4k. DB\StarCitizen\labels.tsv keeps the texts of exactly those keys
    // ("key<TAB>text"), built on this PC from the installed game (read only, never while the game runs) by
    // engine starcitizen labels and after each db refresh-starcitizen. Without it, a readable form of the id is shown.
    internal static class EngineScLabels
    {
        public const string FileName = "labels.tsv";

        private static readonly Regex KeyInComment = new Regex("<action name=\"[^\"]+\"><!--@([^/]*)//", RegexOptions.Compiled);

        public sealed class Result
        {
            public string Archive = string.Empty;
            public string File = string.Empty;
            public int Keys;
            public int Labels;
            public List<string> Warnings = new List<string>();
            public string Error = string.Empty;
        }

        // key (without '@', any case) -> the game's English text; empty when no file was built yet.
        public static Dictionary<string, string> Load()
        {
            Dictionary<string, string> labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GameDataPaths.DbFile("StarCitizen", FileName);
                if (!System.IO.File.Exists(path))
                    return labels;
                foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && tab < line.Length - 1)
                        labels[line.Substring(0, tab).Trim()] = line.Substring(tab + 1).Trim();
                }
            }
            catch (IOException)
            {
            }
            return labels;
        }

        // The text keys AllBinds.xml names.
        public static HashSet<string> KeysOf(string allBindsXml)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in KeyInComment.Matches(allBindsXml ?? string.Empty))
            {
                string key = m.Groups[1].Value.Trim();
                if (key.Length > 0)
                    keys.Add(key);
            }
            return keys;
        }

        // Reads global.ini from the game's Data.p4k and writes labels.tsv for the keys of the command list.
        public static Result Build(string installPath, bool dryRun)
        {
            Result result = new Result();
            string allBinds = GameDataPaths.DbFile("StarCitizen", "AllBinds.xml");
            if (!System.IO.File.Exists(allBinds))
            {
                result.Error = "JoyPro has no Star Citizen command list yet (engine db refresh-starcitizen).";
                return result;
            }
            HashSet<string> keys = KeysOf(System.IO.File.ReadAllText(allBinds, Encoding.UTF8));
            result.Keys = keys.Count;
            string root = installPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                GameInstall install = GameInstalls.Find("StarCitizen", result.Warnings);
                root = install != null && install.Installed ? install.InstallPath : string.Empty;
            }
            string archive = StarCitizenCockpitPictures.ArchivePath(root);
            if (archive.Length == 0)
            {
                result.Error = "Data.p4k of Star Citizen was not found" + (string.IsNullOrWhiteSpace(root) ? " (the game is not installed)." : " in " + root + ".");
                return result;
            }
            result.Archive = archive;
            string running = StarCitizenP4k.RunningGame();
            if (running.Length > 0)
            {
                result.Error = "Star Citizen is running (" + running + "): close it first; JoyPro reads its files only while it is closed.";
                return result;
            }
            byte[] ini;
            try
            {
                using (StarCitizenP4k p4k = StarCitizenP4k.Open(archive, Path.Combine(GameInstalls.DataRoot(), "Cache", "StarCitizen"), Program.UnpackZstd))
                    ini = p4k.Read("Data\\Localization\\english\\global.ini");
            }
            catch (Exception ex)
            {
                result.Error = "global.ini could not be read from " + archive + ": " + ex.Message;
                return result;
            }
            if (ini == null)
            {
                result.Error = "Data.p4k has no Data\\Localization\\english\\global.ini.";
                return result;
            }
            StarCitizenTexts texts = StarCitizenTexts.Parse(ini);
            StringBuilder sb = new StringBuilder();
            sb.Append("# Star Citizen's English names of the controls of AllBinds.xml (from global.ini in Data.p4k, built on this PC ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("). key<TAB>text\r\n");
            foreach (string key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string text = Clean(texts.Text(key));
                if (text.Length == 0)
                    continue;
                sb.Append(key).Append('\t').Append(text).Append("\r\n");
                result.Labels++;
            }
            result.File = GameDataPaths.WritableDbFile("StarCitizen", FileName);
            if (!dryRun)
                System.IO.File.WriteAllText(result.File, sb.ToString(), new UTF8Encoding(false));
            return result;
        }

        // One line, no tabs, no markup the game uses for colours ("<EM4>...</EM4>").
        internal static string Clean(string text)
        {
            string t = Regex.Replace(text ?? string.Empty, "<[^>]+>", string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            return Regex.Replace(t, @"\s+", " ").Trim();
        }
    }
}
