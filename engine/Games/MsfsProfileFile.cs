using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // One input combination of an MSFS action (<Primary> or <Secondary>): KEY codes with their Information texts.
    internal sealed class MsfsCombo
    {
        public List<int> Codes = new List<int>();
        public List<string> Infos = new List<string>();
        // Other lines inside <Primary>/<Secondary> (the SDK allows an <Axis/> there), kept as they are.
        public List<string> ExtraLines = new List<string>();

        public string Signature
        {
            get { return string.Join("+", Codes.Select(c => c.ToString(CultureInfo.InvariantCulture))); }
        }
    }

    internal sealed class MsfsAction
    {
        public string Name;
        public string Context;
        public int StartLine = -1;
        public int EndLine = -1;
        public string Indent = "\t\t";
        public string Attributes = string.Empty;
        public int Flag;
        public List<MsfsCombo> Combos = new List<MsfsCombo>();
        public List<string> OtherLines = new List<string>();
        public bool Changed;

        public bool IsNew
        {
            get { return StartLine < 0; }
        }

        // The per-action axis settings (<Axis .../> without AxisName directly in <Action>, used with flag 4096): its
        // index in OtherLines, or -1.
        public int OverrideAxisIndex
        {
            get { return OtherLines.FindIndex(l => MsfsProfileFile.IsOverrideAxisLine(l)); }
        }

        public string OverrideAxisLine
        {
            get
            {
                int index = OverrideAxisIndex;
                return index < 0 ? null : OtherLines[index];
            }
        }
    }

    // Line-based editor for MSFS 2020/2024 input profiles (an XML fragment: <Version/>, <FriendlyName>, <Device>
    // with <Context> blocks of <Action> elements). Only changed actions are re-rendered; all other lines keep
    // their bytes. 2024 files list every action of their contexts; 2020 files only bound ones.
    internal sealed class MsfsProfileFile
    {
        private static readonly Regex DeviceLine = new Regex("^\\s*<Device\\s+(?<attrs>[^>]*)>", RegexOptions.Compiled);
        private static readonly Regex ContextOpen = new Regex("^\\s*<Context\\s+ContextName=\"(?<name>[^\"]+)\"\\s*(?<self>/?)>\\s*$", RegexOptions.Compiled);
        private static readonly Regex ContextClose = new Regex("^\\s*</Context>\\s*$", RegexOptions.Compiled);
        private static readonly Regex ActionOpen = new Regex("^(?<indent>\\s*)<Action\\s+ActionName=\"(?<name>[^\"]+)\"(?<attrs>[^>]*?)(?<self>/?)>\\s*$", RegexOptions.Compiled);
        private static readonly Regex KeyLine = new Regex("<KEY\\s+Information=\"(?<info>[^\"]*)\"\\s*>(?<code>\\d+)</KEY>", RegexOptions.Compiled);
        private static readonly Regex FlagAttribute = new Regex("\\sFlag=\"(?<flag>\\d+)\"", RegexOptions.Compiled);

        private sealed class ContextBlock
        {
            public string Name;
            public int OpenLine;
            public int CloseLine;
        }

        private static readonly Regex AxisElement = new Regex("^(?<indent>\\s*)<Axis\\s(?<attrs>[^>]*?)/>\\s*$", RegexOptions.Compiled);

        // The device axes in the order the sim writes them (DIJOYSTATE2 slots of JoyPro's axis tokens).
        public static readonly string[] AxisNames = { "X", "Y", "Z", "rX", "rY", "rZ", "SliderX", "SliderY" };

        private List<string> lines;
        private string newline;
        private bool bom;
        private readonly List<ContextBlock> contexts = new List<ContextBlock>();
        private int deviceCloseLine = -1;
        private int deviceLine = -1;
        private int aircraftInfoLine = -1;
        private int axesOpenLine = -1;
        private int axesCloseLine = -1;
        // Device axis name -> line of its <Axis AxisName=".."/> in <Axes> (the Sensitivity page).
        private readonly Dictionary<string, int> deviceAxisLines = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> replacedLines = new Dictionary<int, string>();
        private readonly List<KeyValuePair<string, string>> addedAxisLines = new List<KeyValuePair<string, string>>();

        public string FilePath { get; private set; }
        public string DeviceName { get; private set; }
        public string FriendlyName { get; private set; }
        public Guid DeviceGuid { get; private set; }
        public bool HasDeviceGuid { get; private set; }
        public bool Is2024 { get; private set; }
        // A preset as packages ship them (root <DefaulftInput>, MSFS 2024 Community package): it lists only bound
        // actions, so new actions are added like in 2020 files.
        public bool IsPreset { get; private set; }
        public string Category { get; private set; }

        // The 2024 category of the file: <AircraftInfo CategoryName> of a user profile, else the folder of a preset
        // (InputProfiles\Categories\<Airplane|...|Transversal>); upper case, empty for 2020 files.
        public string CategoryKey
        {
            get
            {
                string category = Category.Length > 0 ? Category : (IsPreset ? MsfsCommunityPackage.PresetCategory(FilePath) : string.Empty);
                return category.ToUpperInvariant();
            }
        }

        // The sim's "General" binds (cameras, views, pause, ...): the Transversal category of MSFS 2024.
        public bool IsGeneralCategory
        {
            get { return CategoryKey == "TRANSVERSAL" || CategoryKey == "GENERAL"; }
        }

        public List<MsfsAction> Actions { get; private set; }
        // Where the file came from (import reads JoyPro's package and the user's own profiles side by side).
        public MsfsProfileSourceKind Source { get; set; }

        public static bool LooksLikeProfile(string path)
        {
            try
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
                {
                    char[] buffer = new char[1024];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    string head = new string(buffer, 0, read);
                    return head.Contains("<Device ") && (head.Contains("<Version") || head.Contains("<FriendlyName"));
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool TryLoad(string path, List<string> errors, out MsfsProfileFile file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("MSFS input profile not found: " + path);
                return false;
            }

            byte[] bytes;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (MemoryStream copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    bytes = copy.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A Store install keeps its data encrypted: a file that lost the mark cannot be read (409).
                errors.Add("Could not read the MSFS input profile " + path + ": " + (SafeFileIo.IsAppxNotEncrypted(ex) ? SafeFileIo.Explain(ex, path).Message : ex.Message));
                return false;
            }
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            // UTF-8 (the sim's files), or UTF-16 with its byte order mark (read only: Save writes UTF-8).
            string text = MsfsUserProfiles.DecodeText(bytes);
            // The reader works line by line: XML written on one line (not seen from the sim so far) gets a line per element.
            if (text.IndexOf('\n') < 0 && text.Contains("<Device "))
                text = Regex.Replace(text, ">\\s*<", ">\n<");
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            file = new MsfsProfileFile
            {
                FilePath = path,
                lines = text.Split(new[] { newline }, StringSplitOptions.None).ToList(),
                newline = newline,
                bom = bom,
                DeviceName = string.Empty,
                FriendlyName = string.Empty,
                Category = string.Empty,
                Actions = new List<MsfsAction>()
            };

            if (!file.Parse())
            {
                errors.Add("Not an MSFS input profile (no <Device> element): " + path);
                file = null;
                return false;
            }
            return true;
        }

        private bool Parse()
        {
            bool deviceFound = false;
            ContextBlock context = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Contains("<DefaulftInput"))
                    IsPreset = true;
                Match device = DeviceLine.Match(line);
                if (device.Success)
                {
                    deviceFound = true;
                    deviceLine = i;
                    string attrs = device.Groups["attrs"].Value;
                    DeviceName = WebUtility.HtmlDecode(Attribute(attrs, "DeviceName"));
                    Guid guid;
                    HasDeviceGuid = Guid.TryParse(Attribute(attrs, "GUID"), out guid) && guid != Guid.Empty;
                    DeviceGuid = guid;
                    Is2024 = Is2024 || attrs.Contains("HWVer=");
                    continue;
                }
                // MSFS 2024 writes Version -1, HWVer and AircraftInfo; late MSFS 2020 files also have CompositeID.
                if (line.Contains("<Version Num=\"-1\""))
                {
                    Is2024 = true;
                    continue;
                }
                Match friendly = Regex.Match(line, "<FriendlyName[^>]*>(?<name>[^<]*)</FriendlyName>");
                if (friendly.Success)
                {
                    FriendlyName = WebUtility.HtmlDecode(friendly.Groups["name"].Value).Trim();
                    continue;
                }
                if (line.Contains("<AircraftInfo"))
                {
                    Is2024 = true;
                    aircraftInfoLine = i;
                    Category = Attribute(line, "CategoryName");
                    continue;
                }
                // <Axes> of the device (outside any context): one <Axis AxisName=".."/> per physical axis.
                if (context == null && axesOpenLine < 0 && Regex.IsMatch(line, "^\\s*<Axes\\s*/?>\\s*$"))
                {
                    axesOpenLine = i;
                    if (line.Contains("/>"))
                    {
                        axesCloseLine = i;
                        continue;
                    }
                    int end = i + 1;
                    while (end < lines.Count && lines[end].Trim() != "</Axes>")
                    {
                        string name = Attribute(lines[end], "AxisName");
                        if (AxisElement.IsMatch(lines[end]) && name.Length > 0 && !deviceAxisLines.ContainsKey(name))
                            deviceAxisLines[name] = end;
                        end++;
                    }
                    axesCloseLine = Math.Min(end, lines.Count - 1);
                    i = axesCloseLine;
                    continue;
                }
                if (line.Trim() == "</Device>")
                {
                    deviceCloseLine = i;
                    continue;
                }

                Match open = ContextOpen.Match(line);
                if (open.Success)
                {
                    context = new ContextBlock { Name = open.Groups["name"].Value, OpenLine = i, CloseLine = open.Groups["self"].Value.Length > 0 ? i : -1 };
                    contexts.Add(context);
                    if (context.CloseLine == i)
                        context = null;
                    continue;
                }
                if (ContextClose.IsMatch(line))
                {
                    if (context != null)
                        context.CloseLine = i;
                    context = null;
                    continue;
                }

                Match action = ActionOpen.Match(line);
                if (!action.Success)
                    continue;

                MsfsAction parsed = new MsfsAction
                {
                    Name = action.Groups["name"].Value,
                    Context = context == null ? string.Empty : context.Name,
                    StartLine = i,
                    EndLine = i,
                    Indent = action.Groups["indent"].Value,
                    Attributes = action.Groups["attrs"].Value
                };
                Match flag = FlagAttribute.Match(parsed.Attributes);
                int flagValue;
                parsed.Flag = flag.Success && int.TryParse(flag.Groups["flag"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out flagValue) ? flagValue : 0;
                if (action.Groups["self"].Value.Length == 0)
                {
                    int end = i + 1;
                    while (end < lines.Count && lines[end].Trim() != "</Action>")
                        end++;
                    parsed.EndLine = Math.Min(end, lines.Count - 1);
                    ParseChildren(parsed);
                    i = parsed.EndLine;
                }
                Actions.Add(parsed);
            }
            return deviceFound;
        }

        private void ParseChildren(MsfsAction action)
        {
            MsfsCombo current = null;
            for (int i = action.StartLine + 1; i < action.EndLine; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed == "<Primary>" || trimmed == "<Secondary>")
                {
                    current = new MsfsCombo();
                    continue;
                }
                if (trimmed == "</Primary>" || trimmed == "</Secondary>")
                {
                    if (current != null && current.Codes.Count > 0)
                        action.Combos.Add(current);
                    current = null;
                    continue;
                }
                Match key = KeyLine.Match(trimmed);
                if (current != null && key.Success)
                {
                    current.Codes.Add(int.Parse(key.Groups["code"].Value, CultureInfo.InvariantCulture));
                    current.Infos.Add(key.Groups["info"].Value);
                    continue;
                }
                if (current == null && trimmed.Length > 0)
                    action.OtherLines.Add(lines[i]);
                else if (current != null && trimmed.Length > 0)
                    current.ExtraLines.Add(lines[i]);
            }
        }

        // ---- axis settings ---------------------------------------------------------------------------------------

        internal static bool IsOverrideAxisLine(string line)
        {
            return line != null && AxisElement.IsMatch(line) && Attribute(line, "AxisName").Length == 0;
        }

        // Attribute values of an <Axis/> line (missing attributes are left out).
        public static Dictionary<string, int> AxisValues(string line)
        {
            Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.Ordinal);
            if (line == null)
                return values;
            foreach (Match match in Regex.Matches(line, "\\b(?<name>Axis\\w+)=\"(?<value>-?\\d+)\""))
            {
                int value;
                if (int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    values[match.Groups["name"].Value] = value;
            }
            return values;
        }

        // The line with the given attribute values: existing attributes are changed in place, missing ones are added
        // before "/>" (in the given order), everything else keeps its bytes.
        public static string WithAxisValues(string line, IList<KeyValuePair<string, int>> values)
        {
            string result = line;
            foreach (KeyValuePair<string, int> value in values)
            {
                string text = value.Value.ToString(CultureInfo.InvariantCulture);
                Regex attribute = new Regex("\\b" + value.Key + "=\"[^\"]*\"");
                if (attribute.IsMatch(result))
                    result = attribute.Replace(result, value.Key + "=\"" + text + "\"", 1);
                else
                {
                    int close = result.LastIndexOf("/>", StringComparison.Ordinal);
                    result = result.Substring(0, close).TrimEnd() + " " + value.Key + "=\"" + text + "\"" + result.Substring(close);
                }
            }
            return result;
        }

        public static string NewAxisLine(string indent, string axisName, IList<KeyValuePair<string, int>> values)
        {
            StringBuilder sb = new StringBuilder(indent + "<Axis");
            if (axisName != null)
                sb.Append(" AxisName=\"").Append(axisName).Append('"');
            foreach (KeyValuePair<string, int> value in values)
                sb.Append(' ').Append(value.Key).Append("=\"").Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append("/>");
            return sb.ToString();
        }

        // The current <Axis AxisName=".."/> line of a device axis (with JoyPro's pending change), or null.
        public string DeviceAxisLine(string axisName)
        {
            int line;
            if (deviceAxisLines.TryGetValue(axisName, out line))
            {
                string replaced;
                return replacedLines.TryGetValue(line, out replaced) ? replaced : lines[line];
            }
            foreach (KeyValuePair<string, string> added in addedAxisLines)
                if (added.Key == axisName)
                    return added.Value;
            return null;
        }

        public int ChangedAxes { get; private set; }

        // The attributes the file's device axis lines use (empty when it has none): early MSFS 2020 files (version
        // 1238) only write AxisSensitivy and AxisDeadZone.
        public HashSet<string> DeviceAxisAttributes
        {
            get
            {
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (int line in deviceAxisLines.Values)
                    names.UnionWith(AxisValues(lines[line]).Keys);
                return names;
            }
        }

        // Sets the Sensitivity page of a device axis; a missing <Axis> line is added in the sim's order.
        public void SetDeviceAxis(string axisName, IList<KeyValuePair<string, int>> values)
        {
            int line;
            if (deviceAxisLines.TryGetValue(axisName, out line))
            {
                string current = replacedLines.ContainsKey(line) ? replacedLines[line] : lines[line];
                string updated = WithAxisValues(current, values);
                if (updated == current)
                    return;
                replacedLines[line] = updated;
                ChangedAxes++;
                return;
            }
            int index = addedAxisLines.FindIndex(a => a.Key == axisName);
            if (index >= 0)
            {
                addedAxisLines[index] = new KeyValuePair<string, string>(axisName, WithAxisValues(addedAxisLines[index].Value, values));
                return;
            }
            string indent = deviceAxisLines.Count > 0 ? AxisElement.Match(lines[deviceAxisLines.Values.First()]).Groups["indent"].Value
                : axesOpenLine >= 0 ? LeadingWhitespace(lines[axesOpenLine]) + "\t" : "\t\t";
            addedAxisLines.Add(new KeyValuePair<string, string>(axisName, NewAxisLine(indent, axisName, values)));
            ChangedAxes++;
        }

        // Added device axis lines that go before the given line of <Axes> (in the order X, Y, Z, rX, .. SliderY).
        private IEnumerable<string> AddedAxisLinesBefore(int line)
        {
            foreach (KeyValuePair<string, string> added in addedAxisLines.OrderBy(a => Array.IndexOf(AxisNames, a.Key)))
            {
                int order = Array.IndexOf(AxisNames, added.Key);
                int before = axesCloseLine;
                foreach (KeyValuePair<string, int> existing in deviceAxisLines.OrderBy(e => e.Value))
                {
                    int existingOrder = Array.IndexOf(AxisNames, existing.Key);
                    if (existingOrder > order)
                    {
                        before = existing.Value;
                        break;
                    }
                }
                if (before == line)
                    yield return added.Value;
            }
        }

        private static string Attribute(string text, string name)
        {
            Match match = Regex.Match(text, "\\b" + name + "=\"(?<value>[^\"]*)\"");
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        public MsfsAction Find(string name)
        {
            return Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        }

        public IEnumerable<string> ContextNames
        {
            get { return contexts.Select(c => c.Name); }
        }

        // For 2020 files, which only list bound actions: a new action goes into its context (created if needed).
        public MsfsAction AddAction(string context, string name, int flag)
        {
            MsfsAction action = new MsfsAction { Name = name, Context = context, Flag = flag, Attributes = " Flag=\"" + flag.ToString(CultureInfo.InvariantCulture) + "\"", Changed = true };
            Actions.Add(action);
            return action;
        }

        public int ChangedActions
        {
            get { return Actions.Count(a => a.Changed); }
        }

        public static string ChildIndent(MsfsAction action)
        {
            return action.Indent + (action.Indent.Contains("\t") || action.Indent.Length == 0 ? "\t" : "  ");
        }

        private static string LeadingWhitespace(string line)
        {
            return line.Substring(0, line.Length - line.TrimStart().Length);
        }

        private List<string> RenderAction(MsfsAction action)
        {
            string attributes = FlagAttribute.IsMatch(action.Attributes)
                ? FlagAttribute.Replace(action.Attributes, " Flag=\"" + action.Flag.ToString(CultureInfo.InvariantCulture) + "\"")
                : action.Attributes + " Flag=\"" + action.Flag.ToString(CultureInfo.InvariantCulture) + "\"";
            string open = action.Indent + "<Action ActionName=\"" + action.Name + "\"" + attributes;
            List<string> output = new List<string>();
            if (action.Combos.Count == 0 && action.OtherLines.Count == 0)
            {
                // 2020 files drop unbound actions; 2024 files keep them as empty elements.
                if (Is2024)
                    output.Add(open + "/>");
                return output;
            }

            string child = ChildIndent(action);
            string grandChild = child + (child.Contains("\t") ? "\t" : "  ");
            output.Add(open + ">");
            output.AddRange(action.OtherLines);
            for (int i = 0; i < action.Combos.Count && i < 2; i++)
            {
                string tag = i == 0 ? "Primary" : "Secondary";
                output.Add(child + "<" + tag + ">");
                for (int k = 0; k < action.Combos[i].Codes.Count; k++)
                    output.Add(grandChild + "<KEY Information=\"" + action.Combos[i].Infos[k] + "\">" + action.Combos[i].Codes[k].ToString(CultureInfo.InvariantCulture) + "</KEY>");
                output.AddRange(action.Combos[i].ExtraLines);
                output.Add(child + "</" + tag + ">");
            }
            output.Add(action.Indent + "</Action>");
            return output;
        }

        public List<string> Render()
        {
            Dictionary<int, MsfsAction> changedByStart = Actions.Where(a => a.Changed && !a.IsNew).ToDictionary(a => a.StartLine);
            List<MsfsAction> added = Actions.Where(a => a.IsNew).ToList();
            List<string> output = new List<string>();
            // New device axis lines: into <Axes> (a self-closing <Axes/> becomes a block), or a new <Axes> block after
            // <Device> / <AircraftInfo> when the file has none.
            bool axesBlockMissing = addedAxisLines.Count > 0 && (axesOpenLine < 0 || axesOpenLine == axesCloseLine);
            int axesBlockAfter = axesOpenLine >= 0 ? -1 : (aircraftInfoLine == deviceLine + 1 ? aircraftInfoLine : deviceLine);
            for (int i = 0; i < lines.Count; i++)
            {
                MsfsAction action;
                if (changedByStart.TryGetValue(i, out action))
                {
                    output.AddRange(RenderAction(action));
                    i = action.EndLine;
                    continue;
                }
                if (axesBlockMissing && (i == axesOpenLine || i == axesBlockAfter))
                {
                    string indent = i == axesOpenLine ? LeadingWhitespace(lines[i]) : "\t";
                    if (i != axesOpenLine)
                        output.Add(lines[i]);
                    output.Add(indent + "<Axes>");
                    foreach (KeyValuePair<string, string> newAxis in addedAxisLines.OrderBy(a => Array.IndexOf(AxisNames, a.Key)))
                        output.Add(newAxis.Value);
                    output.Add(indent + "</Axes>");
                    continue;
                }
                if (!axesBlockMissing && addedAxisLines.Count > 0 && i > axesOpenLine && i <= axesCloseLine)
                    output.AddRange(AddedAxisLinesBefore(i));
                string replaced;
                if (replacedLines.TryGetValue(i, out replaced))
                {
                    output.Add(replaced);
                    continue;
                }

                ContextBlock closing = contexts.FirstOrDefault(c => c.CloseLine == i && c.CloseLine != c.OpenLine);
                if (closing != null)
                {
                    foreach (MsfsAction newAction in added.Where(a => a.Context == closing.Name))
                        output.AddRange(RenderAction(newAction));
                }
                if (i == deviceCloseLine)
                {
                    foreach (IGrouping<string, MsfsAction> group in added.Where(a => !contexts.Any(c => c.Name == a.Context && c.CloseLine != c.OpenLine)).GroupBy(a => a.Context))
                    {
                        output.Add("\t<Context ContextName=\"" + group.Key + "\">");
                        foreach (MsfsAction newAction in group)
                        {
                            newAction.Indent = "\t\t";
                            output.AddRange(RenderAction(newAction));
                        }
                        output.Add("\t</Context>");
                    }
                }
                output.Add(lines[i]);
            }
            return output;
        }

        public void Save(string path)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(string.Join(newline, Render()));
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (bom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                stream.Write(body, 0, body.Length);
            }
        }
    }
}
