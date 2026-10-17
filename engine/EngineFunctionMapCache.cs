using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // The mapped action index on disk (<data>\Cache\function-map.bin): every action of every game list with its
    // functions, so an engine run (one per GUI search) does not read and map every list again. The fingerprint holds
    // the size and time of every file the index comes from (the game lists, DB\Functions, function-map.json) and of the
    // engine itself; any change rebuilds the cache. A cache that cannot be read or written is simply not used.
    internal static class EngineFunctionMapCache
    {
        private const string Magic = "JoyProFunctionMap/1";

        public static string CacheFile()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Cache", "function-map.bin");
        }

        public static bool Disabled
        {
            get { return string.Equals(Environment.GetEnvironmentVariable("JOYPRO_FUNCTION_MAP_CACHE"), "off", StringComparison.OrdinalIgnoreCase); }
        }

        public static string Fingerprint(EngineFunctionMap.Map map)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            List<string> files = new List<string> { map.Source, typeof(EngineFunctionMapCache).Assembly.Location };
            string functions = EngineNlGroup.FunctionsFolder();
            if (Directory.Exists(functions))
                files.AddRange(Directory.GetFiles(functions).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            files.AddRange(EngineRelationSuggest.SourceFiles());
            sb.Append(EngineRelationSuggest.IndexOverrideFolder()).Append('\n');
            foreach (string file in files)
            {
                FileInfo info = new FileInfo(file);
                sb.Append(file).Append('|');
                if (info.Exists)
                    sb.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static bool TryRead(EngineFunctionMap.Map map, string fingerprint, out List<EngineRelationSuggest.Command> commands)
        {
            commands = null;
            string file = CacheFile();
            if (Disabled || !File.Exists(file))
                return false;
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadString() != Magic || reader.ReadString() != fingerprint)
                        return false;
                    int stringCount = reader.ReadInt32();
                    string[] strings = new string[stringCount];
                    for (int i = 0; i < stringCount; i++)
                        strings[i] = reader.ReadString();
                    int count = reader.ReadInt32();
                    List<EngineRelationSuggest.Command> list = new List<EngineRelationSuggest.Command>(count);
                    List<EngineFunctionMap.Entry> entries = new List<EngineFunctionMap.Entry>(count);
                    Dictionary<int, string[]> tokenMemo = new Dictionary<int, string[]>();
                    for (int i = 0; i < count; i++)
                    {
                        EngineRelationSuggest.Command c = new EngineRelationSuggest.Command
                        {
                            Game = strings[reader.ReadInt32()],
                            Plane = strings[reader.ReadInt32()],
                            Id = strings[reader.ReadInt32()],
                            Title = strings[reader.ReadInt32()],
                            Category = strings[reader.ReadInt32()],
                            IsAxis = reader.ReadBoolean()
                        };
                        list.Add(c);
                        EngineFunctionMap.Entry entry = new EngineFunctionMap.Entry { Command = c, Class = strings[reader.ReadInt32()] };
                        int tokenKey = reader.ReadInt32();
                        string[] tokens;
                        if (!tokenMemo.TryGetValue(tokenKey, out tokens))
                        {
                            string joined = strings[tokenKey];
                            tokens = joined.Length == 0 ? new string[0] : joined.Split(' ');
                            tokenMemo[tokenKey] = tokens;
                        }
                        entry.TitleTokens = tokens;
                        int assignments = reader.ReadByte();
                        for (int a = 0; a < assignments; a++)
                        {
                            string id = strings[reader.ReadInt32()];
                            EngineFunctionMap.Function f;
                            string dir = strings[reader.ReadInt32()];
                            double weight = reader.ReadSingle();
                            string source = reader.ReadBoolean() ? "catalog" : "rule";
                            string term = strings[reader.ReadInt32()];
                            bool inTitle = reader.ReadBoolean();
                            if (!map.ById.TryGetValue(id, out f))
                                return false;
                            entry.Functions.Add(new EngineFunctionMap.Assignment { Function = f, Dir = dir, Weight = Math.Round(weight, 2), Source = source, Term = term, InTitle = inTitle });
                        }
                        entries.Add(entry);
                    }
                    foreach (EngineFunctionMap.Entry entry in entries)
                    {
                        string key = entry.Command.Game + "|" + entry.Command.Id + "|" + entry.Command.Plane;
                        if (map.ByKey.ContainsKey(key))
                            continue;
                        map.ByKey[key] = entry;
                        map.Entries.Add(entry);
                        foreach (EngineFunctionMap.Assignment a in entry.Functions)
                        {
                            List<EngineFunctionMap.Entry> byFunction;
                            if (!map.ByFunction.TryGetValue(a.Function.Id, out byFunction))
                                map.ByFunction[a.Function.Id] = byFunction = new List<EngineFunctionMap.Entry>();
                            byFunction.Add(entry);
                        }
                    }
                    commands = list;
                    return true;
                }
            }
            catch (Exception)
            {
                map.Entries.Clear();
                map.ByKey.Clear();
                map.ByFunction.Clear();
                return false;
            }
        }

        public static void TryWrite(EngineFunctionMap.Map map, string fingerprint, List<EngineRelationSuggest.Command> commands)
        {
            if (Disabled)
                return;
            string file = CacheFile();
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
                List<string> strings = new List<string>();
                Func<string, int> id = s =>
                {
                    s = s ?? string.Empty;
                    int value;
                    if (!ids.TryGetValue(s, out value))
                    {
                        value = strings.Count;
                        ids[s] = value;
                        strings.Add(s);
                    }
                    return value;
                };
                // Every command in index order; the map's entry for it (the first command of a key carries it).
                List<int[]> rows = new List<int[]>(commands.Count);
                List<EngineFunctionMap.Entry> entries = new List<EngineFunctionMap.Entry>(commands.Count);
                foreach (EngineRelationSuggest.Command c in commands)
                {
                    EngineFunctionMap.Entry entry;
                    if (!map.ByKey.TryGetValue(c.Game + "|" + c.Id + "|" + c.Plane, out entry) || entry.Command != c)
                        entry = new EngineFunctionMap.Entry { Command = c };
                    entries.Add(entry);
                    rows.Add(new[] { id(c.Game), id(c.Plane), id(c.Id), id(c.Title), id(c.Category), id(entry.Class), id(string.Join(" ", entry.TitleTokens)) });
                    foreach (EngineFunctionMap.Assignment a in entry.Functions)
                    {
                        id(a.Function.Id);
                        id(a.Dir);
                        id(a.Term);
                    }
                }
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(fingerprint);
                    writer.Write(strings.Count);
                    foreach (string s in strings)
                        writer.Write(s);
                    writer.Write(commands.Count);
                    for (int i = 0; i < commands.Count; i++)
                    {
                        int[] r = rows[i];
                        for (int j = 0; j < 5; j++)
                            writer.Write(r[j]);
                        writer.Write(commands[i].IsAxis);
                        writer.Write(r[5]);
                        writer.Write(r[6]);
                        List<EngineFunctionMap.Assignment> functions = entries[i].Functions;
                        int n = Math.Min(255, functions.Count);
                        writer.Write((byte)n);
                        for (int a = 0; a < n; a++)
                        {
                            writer.Write(ids[functions[a].Function.Id]);
                            writer.Write(ids[functions[a].Dir ?? string.Empty]);
                            writer.Write((float)functions[a].Weight);
                            writer.Write(functions[a].Source == "catalog");
                            writer.Write(ids[functions[a].Term ?? string.Empty]);
                            writer.Write(functions[a].InTitle);
                        }
                    }
                }
                if (File.Exists(file))
                    File.Delete(file);
                File.Move(temp, file);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
