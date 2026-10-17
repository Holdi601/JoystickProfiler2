using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    internal enum BlkChildKind
    {
        Param,
        Block,
        Blank,
        Other
    }

    internal sealed class BlkParam
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }

        public string Render()
        {
            return Name + ":" + Type + "=" + Value;
        }
    }

    // A direct child of a block with the raw lines it was read from.
    internal sealed class BlkChild
    {
        public BlkChildKind Kind { get; set; }
        public string Name { get; set; }
        public List<string> RawLines { get; set; }
        public List<BlkParam> Params { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }

        public BlkChild()
        {
            RawLines = new List<string>();
            Params = new List<BlkParam>();
        }

        public BlkParam Get(string name)
        {
            return Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        }
    }

    internal sealed class BlkBlock
    {
        public string Name { get; set; }
        public int HeaderLine { get; set; }
        public int CloseLine { get; set; }
        public string Indent { get; set; }
        public bool Inline { get; set; }
    }

    // Line-based editor for Dagor text BLK files (War Thunder). Block names repeat, so children are kept as an
    // ordered list; a block body is only rewritten when it is replaced, everything else stays byte-identical.
    internal sealed class BlkDocument
    {
        private static readonly Regex InlineBlock = new Regex(@"^(?<indent>\s*)(?<name>[A-Za-z0-9_.\-]+)\s*\{\s*\}\s*$", RegexOptions.Compiled);
        private static readonly Regex OpenBlock = new Regex(@"^(?<indent>\s*)(?<name>[A-Za-z0-9_.\-]+)\s*\{\s*$", RegexOptions.Compiled);
        private static readonly Regex ParamLine = new Regex(@"^\s*(?<name>[A-Za-z0-9_.\-]+):(?<type>[a-z0-9]+)=(?<value>.*?)\s*$", RegexOptions.Compiled);

        private List<string> lines;
        private string newline;
        private bool bom;

        public int ChangedBlocks { get; private set; }

        public static bool TryLoad(string path, List<string> errors, out BlkDocument document)
        {
            document = null;
            if (!File.Exists(path))
            {
                errors.Add("War Thunder file not found: " + path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = bom ? 3 : 0;
            if (bytes.Length > offset && (bytes[offset] == 0 || bytes[offset] == 1 || bytes[offset] > 0x7F && bytes[offset] < 0xC0))
            {
                errors.Add("The file is a binary BLK; JoyPro only edits text BLK files.");
                return false;
            }

            string text = new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
            document = new BlkDocument
            {
                bom = bom,
                newline = text.Contains("\r\n") ? "\r\n" : "\n"
            };
            document.lines = text.Split(new[] { document.newline }, StringSplitOptions.None).ToList();
            return true;
        }

        public BlkBlock Root
        {
            get { return new BlkBlock { Name = string.Empty, HeaderLine = -1, CloseLine = lines.Count, Indent = string.Empty }; }
        }

        public BlkBlock FindBlock(params string[] path)
        {
            BlkBlock current = Root;
            foreach (string name in path)
            {
                BlkChild child = Children(current).FirstOrDefault(c => c.Kind == BlkChildKind.Block && c.Name == name);
                if (child == null)
                    return null;
                current = ToBlock(child);
            }
            return current;
        }

        private BlkBlock ToBlock(BlkChild child)
        {
            Match inline = InlineBlock.Match(lines[child.StartLine]);
            return new BlkBlock
            {
                Name = child.Name,
                HeaderLine = child.StartLine,
                CloseLine = child.EndLine,
                Indent = Regex.Match(lines[child.StartLine], @"^\s*").Value,
                Inline = inline.Success
            };
        }

        public List<BlkChild> Children(BlkBlock block)
        {
            List<BlkChild> children = new List<BlkChild>();
            if (block.Inline)
                return children;

            int end = block.CloseLine;
            for (int i = block.HeaderLine + 1; i < end; i++)
            {
                string line = lines[i];
                BlkChild child = new BlkChild { StartLine = i, EndLine = i };
                if (line.Trim().Length == 0)
                {
                    child.Kind = BlkChildKind.Blank;
                }
                else if (InlineBlock.IsMatch(line))
                {
                    child.Kind = BlkChildKind.Block;
                    child.Name = InlineBlock.Match(line).Groups["name"].Value;
                }
                else if (OpenBlock.IsMatch(line))
                {
                    child.Kind = BlkChildKind.Block;
                    child.Name = OpenBlock.Match(line).Groups["name"].Value;
                    child.EndLine = FindClose(i);
                    for (int j = i + 1; j < child.EndLine; j++)
                    {
                        Match param = ParamLine.Match(lines[j]);
                        if (param.Success && Depth(i + 1, j) == 0)
                            child.Params.Add(new BlkParam { Name = param.Groups["name"].Value, Type = param.Groups["type"].Value, Value = param.Groups["value"].Value });
                    }
                }
                else if (ParamLine.IsMatch(line))
                {
                    Match param = ParamLine.Match(line);
                    child.Kind = BlkChildKind.Param;
                    child.Name = param.Groups["name"].Value;
                    child.Params.Add(new BlkParam { Name = child.Name, Type = param.Groups["type"].Value, Value = param.Groups["value"].Value });
                }
                else
                {
                    child.Kind = BlkChildKind.Other;
                }

                for (int j = child.StartLine; j <= child.EndLine; j++)
                    child.RawLines.Add(lines[j]);
                children.Add(child);
                i = child.EndLine;
            }
            return children;
        }

        // Depth of nested blocks between two lines, used to skip params of grandchildren.
        private int Depth(int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
                depth += Count(lines[i], '{') - Count(lines[i], '}');
            return depth;
        }

        private int FindClose(int header)
        {
            int depth = 0;
            for (int i = header; i < lines.Count; i++)
            {
                depth += Count(lines[i], '{') - Count(lines[i], '}');
                if (depth == 0)
                    return i;
            }
            return lines.Count - 1;
        }

        private static int Count(string line, char c)
        {
            int count = 0;
            bool quoted = false;
            foreach (char ch in line)
            {
                if (ch == '"')
                    quoted = !quoted;
                else if (!quoted && ch == c)
                    count++;
            }
            return count;
        }

        public string ChildIndent(BlkBlock block)
        {
            BlkChild first = Children(block).FirstOrDefault(c => c.Kind != BlkChildKind.Blank);
            if (first != null)
                return Regex.Match(first.RawLines[0], @"^\s*").Value;
            return block.Indent + "  ";
        }

        // Replaces the body of a block with the given child line lists (an inline "name{}" block is expanded).
        public void ReplaceBody(BlkBlock block, List<List<string>> children)
        {
            List<string> body = children.SelectMany(c => c).ToList();
            if (block.Inline)
            {
                if (body.Count == 0)
                    return;
                List<string> replacement = new List<string> { block.Indent + block.Name + "{" };
                replacement.AddRange(body);
                replacement.Add(block.Indent + "}");
                lines.RemoveAt(block.HeaderLine);
                lines.InsertRange(block.HeaderLine, replacement);
            }
            else
            {
                List<string> current = lines.GetRange(block.HeaderLine + 1, block.CloseLine - block.HeaderLine - 1);
                if (current.SequenceEqual(body))
                    return;
                lines.RemoveRange(block.HeaderLine + 1, block.CloseLine - block.HeaderLine - 1);
                lines.InsertRange(block.HeaderLine + 1, body);
            }
            ChangedBlocks++;
        }

        // Like War Thunder writes them: "name{}" when empty, otherwise the block followed by a blank line.
        public static List<string> RenderBlock(string indent, string name, IEnumerable<BlkParam> parameters)
        {
            List<BlkParam> list = parameters.ToList();
            if (list.Count == 0)
                return new List<string> { indent + name + "{}" };
            List<string> output = new List<string> { indent + name + "{" };
            output.AddRange(list.Select(p => indent + "  " + p.Render()));
            output.Add(indent + "}");
            output.Add(string.Empty);
            return output;
        }

        // Sets params directly inside a block given as raw lines; other lines keep their text. A param that is not
        // there yet goes first when "first" is set, otherwise before the closing brace.
        public static List<string> SetParams(List<string> rawLines, IEnumerable<BlkParam> values, bool first)
        {
            Match inline = InlineBlock.Match(rawLines[0]);
            List<string> output = inline.Success
                ? new List<string> { inline.Groups["indent"].Value + inline.Groups["name"].Value + "{", inline.Groups["indent"].Value + "}" }
                : new List<string>(rawLines);
            string blockIndent = Regex.Match(output[0], @"^\s*").Value;
            string paramIndent = blockIndent + "  ";

            foreach (BlkParam value in values)
            {
                int found = -1;
                int depth = 0;
                for (int i = 1; i < output.Count - 1; i++)
                {
                    Match param = ParamLine.Match(output[i]);
                    if (depth == 0 && param.Success && param.Groups["name"].Value == value.Name)
                    {
                        found = i;
                        break;
                    }
                    depth += Count(output[i], '{') - Count(output[i], '}');
                }

                if (found >= 0)
                {
                    string indent = Regex.Match(output[found], @"^\s*").Value;
                    output[found] = indent + value.Render();
                }
                else
                {
                    output.Insert(first ? 1 : output.Count - 1, paramIndent + value.Render());
                }
            }
            return output;
        }

        // Removes the params with these names directly inside a block given as raw lines; other lines keep their text.
        public static List<string> RemoveParams(List<string> rawLines, IEnumerable<string> names)
        {
            HashSet<string> remove = new HashSet<string>(names, StringComparer.Ordinal);
            List<string> output = new List<string> { rawLines[0] };
            int depth = 0;
            for (int i = 1; i < rawLines.Count; i++)
            {
                bool last = i == rawLines.Count - 1;
                Match param = ParamLine.Match(rawLines[i]);
                if (!last && depth == 0 && param.Success && remove.Contains(param.Groups["name"].Value))
                    continue;
                if (!last)
                    depth += Count(rawLines[i], '{') - Count(rawLines[i], '}');
                output.Add(rawLines[i]);
            }
            return output;
        }

        // The params directly inside a block.
        public List<BlkParam> Params(BlkBlock block)
        {
            return Children(block).Where(c => c.Kind == BlkChildKind.Param).SelectMany(c => c.Params).ToList();
        }

        // Sets params of the block at the path (e.g. content, settings); the block and missing parents are created at
        // the end of their parent. Only the given params change; returns false when they already had these values.
        public bool SetBlockParams(string[] path, IList<BlkParam> values)
        {
            if (path == null || path.Length == 0 || values == null || values.Count == 0)
                return false;
            EnsureBlock(path);
            BlkBlock parent = path.Length == 1 ? Root : FindBlock(path.Take(path.Length - 1).ToArray());
            List<BlkChild> children = Children(parent);
            BlkChild child = children.First(c => c.Kind == BlkChildKind.Block && c.Name == path[path.Length - 1]);
            List<string> updated = SetParams(child.RawLines, values, false);
            if (updated.SequenceEqual(child.RawLines))
                return false;
            ReplaceBody(parent, children.Select(c => ReferenceEquals(c, child) ? updated : c.RawLines).ToList());
            return true;
        }

        private BlkBlock EnsureBlock(string[] path)
        {
            BlkBlock found = FindBlock(path);
            if (found != null)
                return found;
            BlkBlock parent = path.Length == 1 ? Root : EnsureBlock(path.Take(path.Length - 1).ToArray());
            List<BlkChild> children = Children(parent);
            List<List<string>> body = children.Select(c => c.RawLines).ToList();
            int at = body.Count;
            while (at > 0 && children[at - 1].Kind == BlkChildKind.Blank)
                at--;
            body.Insert(at, new List<string> { ChildIndent(parent) + path[path.Length - 1] + "{}" });
            ReplaceBody(parent, body);
            return FindBlock(path);
        }

        public void Save(string path)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(string.Join(newline, lines));
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (bom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                stream.Write(body, 0, body.Length);
            }
        }
    }
}
