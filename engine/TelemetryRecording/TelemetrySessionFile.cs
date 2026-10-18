#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace JoyPro.TelemetrySessions
{
    // A recorded telemetry session (docs/telemetry-recording.md): one *.jptel file per flight / mission / round.
    //
    //   file   = "JPTEL1\r\n" block*
    //   block  = magic "PBLK" (4) | kind (1) | raw length (u32) | stored length (u32) | crc32 of the stored bytes (u32) | stored bytes
    //            stored bytes = the block's bytes, deflate-compressed (RFC 1951)
    //   kinds  = 1 header (key=value lines), 2 data (text lines, below), 3 raw (binary records), 4 summary (key=value lines)
    //
    // Data lines (UTF-8, '\t' between fields; texts escaped: \\ \t \n \r):
    //   C <idx> <key> <unit> <title> <category> <min> <max>     a channel (defined before its first value)
    //   F <t> <idx>=<value> ...                                  a frame: the values that changed; "<idx>=" = the value is gone
    //   K <t> <idx>=<value> ...                                  a key frame (the first frame of every block): every value
    //                                                            present; a channel it does not list is gone
    //   E <t> <kind> <text>                                      an event (TelemetryEventKinds)
    // Raw records: name length (u8) | name (UTF-8) | t (f64) | length (u32) | bytes.
    // <t> is seconds since the session started (the receive clock), so gaps and pauses are kept.
    //
    // Append-only and crash-safe: blocks are written whole and flushed every few seconds; a block cut off by a crash
    // fails its length or crc check and is dropped by the reader (and cut by Recover, which then writes the summary).
    // Plain C# 7.3 without dependencies: the engine (net48) and JoyPro.Ffb.Core (net9) compile this same file.
    public static class TelemetrySessionFormat
    {
        public const string Format = "joypro-telemetry-session";
        public const int Version = 1;
        public const string Extension = ".jptel";
        public static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("JPTEL1\r\n");
        public const uint BlockMagic = 0x4B4C4250; // "PBLK" little endian
        public const int BlockHeaderSize = 17;
        public const byte KindHeader = 1;
        public const byte KindData = 2;
        public const byte KindRaw = 3;
        public const byte KindSummary = 4;

        // Upper bound for one block: a reader refuses larger lengths (a corrupt header must not allocate gigabytes).
        public const int MaxBlockBytes = 64 * 1024 * 1024;

        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text.IndexOfAny(new[] { '\\', '\t', '\n', '\r' }) < 0)
                return text;
            StringBuilder sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
                return text ?? string.Empty;
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char n = text[++i];
                switch (n)
                {
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        public static string FormatNumber(double value)
        {
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("G10", CultureInfo.InvariantCulture);
        }

        public static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static string FormatTime(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseTime(string text)
        {
            DateTime value;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value)
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : DateTime.MinValue;
        }

        public static byte[] KeyValueBytes(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                if (string.IsNullOrEmpty(pair.Key))
                    continue;
                sb.Append(Escape(pair.Key.Replace("=", "_"))).Append('=').Append(Escape(pair.Value ?? string.Empty)).Append('\n');
            }
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        public static Dictionary<string, string> ParseKeyValues(byte[] bytes)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            string text = Encoding.UTF8.GetString(bytes);
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                result[Unescape(line.Substring(0, eq))] = Unescape(line.Substring(eq + 1));
            }
            return result;
        }

        // ---------------------------------------------------------------- crc32 (IEEE, as zip)

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        public static byte[] Compress(byte[] data, int count)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (DeflateStream deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
                    deflate.Write(data, 0, count);
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] stored, int rawLength)
        {
            byte[] result = new byte[rawLength];
            using (MemoryStream input = new MemoryStream(stored))
            using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < rawLength)
                {
                    int n = deflate.Read(result, read, rawLength - read);
                    if (n <= 0)
                        throw new InvalidDataException("A block of the recording is shorter than its header says.");
                    read += n;
                }
            }
            return result;
        }
    }

    // One channel of a session: what a value is (the source's descriptor).
    public sealed class TelemetrySessionChannel
    {
        public int Index;
        public string Key;
        public string Unit;
        public string Title;
        public string Category;
        public double Min;
        public double Max;
    }

    // Where a standard quantity (TelemetryNav: lon, lat, alt, roll, ...) is in the session's keys.
    public sealed class TelemetrySessionNav
    {
        public string Target;
        public string Key;
        public double Scale = 1;
        public double Offset;

        public string Format()
        {
            return Key + "*" + TelemetrySessionFormat.FormatNumber(Scale) + "+" + TelemetrySessionFormat.FormatNumber(Offset);
        }

        public static TelemetrySessionNav Parse(string target, string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            TelemetrySessionNav nav = new TelemetrySessionNav { Target = target, Key = text };
            int star = text.LastIndexOf('*');
            if (star > 0)
            {
                string rest = text.Substring(star + 1);
                int plus = rest.IndexOf('+', 1);
                double scale;
                double offset = 0;
                string scaleText = plus > 0 ? rest.Substring(0, plus) : rest;
                if (TelemetrySessionFormat.TryParseNumber(scaleText, out scale) &&
                    (plus < 0 || TelemetrySessionFormat.TryParseNumber(rest.Substring(plus + 1), out offset)))
                {
                    nav.Key = text.Substring(0, star);
                    nav.Scale = scale;
                    nav.Offset = offset;
                }
            }
            return nav;
        }
    }

    // Writes one session file. Not thread-safe: the recorder's writer thread owns it.
    public sealed class TelemetrySessionWriter : IDisposable
    {
        public const int DefaultBlockBytes = 256 * 1024;

        private readonly FileStream _file;
        private readonly Dictionary<string, int> _channels = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<double> _last = new List<double>();
        private readonly List<bool> _present = new List<bool>();
        private readonly StringBuilder _data = new StringBuilder(64 * 1024);
        private readonly MemoryStream _raw = new MemoryStream();
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false);
        private readonly HashSet<string> _aircraft = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _aircraftOrder = new List<string>();
        private readonly HashSet<string> _maps = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _mapOrder = new List<string>();
        private bool _blockStarted;
        private bool _finished;

        public TelemetrySessionWriter(string path, IDictionary<string, string> header)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            // Others may read while it is written (the GUI's session list); nobody else may write or delete it.
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            _file.Write(TelemetrySessionFormat.FileMagic, 0, TelemetrySessionFormat.FileMagic.Length);
            Dictionary<string, string> head = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "format", TelemetrySessionFormat.Format },
                { "version", TelemetrySessionFormat.Version.ToString(CultureInfo.InvariantCulture) }
            };
            foreach (KeyValuePair<string, string> pair in header)
            {
                if (pair.Key != "format" && pair.Key != "version")
                    head[pair.Key] = pair.Value;
            }
            WriteBlock(TelemetrySessionFormat.KindHeader, TelemetrySessionFormat.KeyValueBytes(head));
            // The keys of the position (nav.lat + nav.lon, or nav.u + nav.v): the summary counts the frames that have it.
            _positionKeys = PositionKeys(head);
            string aircraft;
            if (head.TryGetValue("info.aircraft", out aircraft))
                NoteAircraft(aircraft);
            string map;
            if (head.TryGetValue("info.map", out map))
                NoteMap(map);
            _file.Flush(true);
        }

        public string Path { get; private set; }
        public int BlockBytes { get; set; } = DefaultBlockBytes;
        public long Frames { get; private set; }
        public long Events { get; private set; }
        public long RawPackets { get; private set; }
        public double LastTime { get; private set; }
        public long BytesWritten { get { return _file.Position; } }
        public int ChannelCount { get { return _channels.Count; } }

        // Frames that had a position (see PositionKeys): 0 = no ACMI track possible.
        public long PositionFrames { get; private set; }

        private readonly string[] _positionKeys;

        private static string[] PositionKeys(Dictionary<string, string> head)
        {
            string lat, lon, u, v;
            TelemetrySessionNav navLat = head.TryGetValue("nav.lat", out lat) ? TelemetrySessionNav.Parse("lat", lat) : null;
            TelemetrySessionNav navLon = head.TryGetValue("nav.lon", out lon) ? TelemetrySessionNav.Parse("lon", lon) : null;
            if (navLat != null && navLon != null)
                return new[] { navLat.Key, navLon.Key };
            TelemetrySessionNav navU = head.TryGetValue("nav.u", out u) ? TelemetrySessionNav.Parse("u", u) : null;
            TelemetrySessionNav navV = head.TryGetValue("nav.v", out v) ? TelemetrySessionNav.Parse("v", v) : null;
            return navU != null && navV != null ? new[] { navU.Key, navV.Key } : new string[0];
        }

        // Defines a channel (once per key; a second call keeps the first definition).
        public int Channel(string key, string unit, string title, string category, double min, double max)
        {
            int index;
            if (_channels.TryGetValue(key, out index))
                return index;
            index = _channels.Count;
            _channels[key] = index;
            _last.Add(double.NaN);
            _present.Add(false);
            StartBlockIfNeeded();
            _data.Append("C\t").Append(index.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(TelemetrySessionFormat.Escape(key)).Append('\t')
                .Append(TelemetrySessionFormat.Escape(unit)).Append('\t')
                .Append(TelemetrySessionFormat.Escape(title)).Append('\t')
                .Append(TelemetrySessionFormat.Escape(category)).Append('\t')
                .Append(TelemetrySessionFormat.FormatNumber(min)).Append('\t')
                .Append(TelemetrySessionFormat.FormatNumber(max)).Append('\n');
            return index;
        }

        public bool HasChannel(string key)
        {
            return _channels.ContainsKey(key);
        }

        // One frame: the values of every key present at time t (seconds since the session start). Keys not defined
        // yet get a channel without a description. Only changes are written (a full frame at each block start).
        public void Frame(double t, IEnumerable<KeyValuePair<string, double>> values)
        {
            StartBlockIfNeeded();
            bool full = _data.Length == 0 || !_blockHasFrame;
            List<int> seen = _seenScratch;
            seen.Clear();
            int lineStart = _data.Length;
            _data.Append(full ? "K\t" : "F\t").Append(FormatT(t));
            int fields = 0;
            foreach (KeyValuePair<string, double> pair in values)
            {
                if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                    continue;
                int index;
                if (!_channels.TryGetValue(pair.Key, out index))
                {
                    // A channel line must come before the frame that uses it: cut the frame, define, start again.
                    string partial = _data.ToString(lineStart, _data.Length - lineStart);
                    _data.Length = lineStart;
                    index = Channel(pair.Key, string.Empty, string.Empty, string.Empty, 0, 1);
                    lineStart = _data.Length;
                    _data.Append(partial);
                }
                seen.Add(index);
                if (!full && _present[index] && _last[index] == pair.Value)
                    continue;
                _last[index] = pair.Value;
                _present[index] = true;
                _data.Append('\t').Append(index.ToString(CultureInfo.InvariantCulture)).Append('=').Append(TelemetrySessionFormat.FormatNumber(pair.Value));
                fields++;
            }
            // Keys that were there before and are gone now.
            _seenSet.Clear();
            foreach (int i in seen)
                _seenSet.Add(i);
            for (int i = 0; i < _present.Count; i++)
            {
                if (_present[i] && !_seenSet.Contains(i))
                {
                    _present[i] = false;
                    _last[i] = double.NaN;
                    if (!full)
                    {
                        _data.Append('\t').Append(i.ToString(CultureInfo.InvariantCulture)).Append('=');
                        fields++;
                    }
                }
            }
            _data.Append('\n');
            _blockHasFrame = true;
            Frames++;
            if (_positionKeys.Length == 2)
            {
                int a, b;
                if (_channels.TryGetValue(_positionKeys[0], out a) && _channels.TryGetValue(_positionKeys[1], out b) && _present[a] && _present[b] &&
                    !(_last[a] == 0 && _last[b] == 0))
                    PositionFrames++;
            }
            LastTime = Math.Max(LastTime, t);
            FlushIfFull();
        }

        private readonly List<int> _seenScratch = new List<int>();
        private readonly HashSet<int> _seenSet = new HashSet<int>();
        private bool _blockHasFrame;

        public void Event(double t, string kind, string text)
        {
            StartBlockIfNeeded();
            _data.Append("E\t").Append(FormatT(t)).Append('\t').Append(TelemetrySessionFormat.Escape(kind ?? string.Empty)).Append('\t')
                .Append(TelemetrySessionFormat.Escape(text ?? string.Empty)).Append('\n');
            Events++;
            LastTime = Math.Max(LastTime, t);
            if (kind == "aircraft")
                NoteAircraft(text);
            else if (kind == "map")
                NoteMap(text);
            FlushIfFull();
        }

        public void Raw(double t, string stream, byte[] data, int offset, int count)
        {
            byte[] name = _utf8.GetBytes(stream ?? string.Empty);
            int nameLength = Math.Min(name.Length, 255);
            _raw.WriteByte((byte)nameLength);
            _raw.Write(name, 0, nameLength);
            _raw.Write(BitConverter.GetBytes(t), 0, 8);
            _raw.Write(BitConverter.GetBytes((uint)count), 0, 4);
            _raw.Write(data, offset, count);
            RawPackets++;
            if (_raw.Length >= BlockBytes)
                FlushRaw();
        }

        // Writes what is buffered as blocks and flushes the file to disk (call every few seconds).
        public void Flush()
        {
            FlushData();
            FlushRaw();
            _file.Flush(true);
        }

        // Ends the session: the remaining blocks and the summary. The writer cannot be used afterwards.
        public void Finish(string endReason, DateTime endedUtc, IDictionary<string, string> extra = null)
        {
            if (_finished)
                return;
            FlushData();
            FlushRaw();
            Dictionary<string, string> summary = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "ended", TelemetrySessionFormat.FormatTime(endedUtc) },
                { "duration", TelemetrySessionFormat.FormatNumber(Math.Round(LastTime, 3)) },
                { "frames", Frames.ToString(CultureInfo.InvariantCulture) },
                { "events", Events.ToString(CultureInfo.InvariantCulture) },
                { "raw", RawPackets.ToString(CultureInfo.InvariantCulture) },
                { "channels", _channels.Count.ToString(CultureInfo.InvariantCulture) },
                { "positionFrames", PositionFrames.ToString(CultureInfo.InvariantCulture) },
                { "aircraft", string.Join("\n", _aircraftOrder) },
                { "maps", string.Join("\n", _mapOrder) },
                { "endReason", endReason ?? string.Empty }
            };
            if (extra != null)
            {
                foreach (KeyValuePair<string, string> pair in extra)
                    summary[pair.Key] = pair.Value;
            }
            WriteBlock(TelemetrySessionFormat.KindSummary, TelemetrySessionFormat.KeyValueBytes(summary));
            _file.Flush(true);
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                try
                {
                    Flush();
                }
                catch (IOException)
                {
                }
            }
            _file.Dispose();
        }

        private void NoteAircraft(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && _aircraft.Add(name))
                _aircraftOrder.Add(name);
        }

        private void NoteMap(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && _maps.Add(name))
                _mapOrder.Add(name);
        }

        private static string FormatT(double t)
        {
            return Math.Round(t, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void StartBlockIfNeeded()
        {
            if (_blockStarted)
                return;
            _blockStarted = true;
            _blockHasFrame = false;
            // A block starts with every channel definition being known from earlier blocks: the reader keeps them.
        }

        private void FlushIfFull()
        {
            if (_data.Length * 2 >= BlockBytes)
                FlushData();
        }

        private void FlushData()
        {
            if (_data.Length == 0)
            {
                _blockStarted = false;
                return;
            }
            byte[] bytes = _utf8.GetBytes(_data.ToString());
            _data.Clear();
            WriteBlock(TelemetrySessionFormat.KindData, bytes);
            _blockStarted = false;
            _blockHasFrame = false;
        }

        private void FlushRaw()
        {
            if (_raw.Length == 0)
                return;
            WriteBlock(TelemetrySessionFormat.KindRaw, _raw.ToArray());
            _raw.SetLength(0);
        }

        private void WriteBlock(byte kind, byte[] bytes)
        {
            byte[] stored = TelemetrySessionFormat.Compress(bytes, bytes.Length);
            byte[] head = new byte[TelemetrySessionFormat.BlockHeaderSize];
            Array.Copy(BitConverter.GetBytes(TelemetrySessionFormat.BlockMagic), 0, head, 0, 4);
            head[4] = kind;
            Array.Copy(BitConverter.GetBytes((uint)bytes.Length), 0, head, 5, 4);
            Array.Copy(BitConverter.GetBytes((uint)stored.Length), 0, head, 9, 4);
            Array.Copy(BitConverter.GetBytes(TelemetrySessionFormat.Crc32(stored, 0, stored.Length)), 0, head, 13, 4);
            _file.Write(head, 0, head.Length);
            _file.Write(stored, 0, stored.Length);
        }
    }

    // What a session file says about itself: from the header and the summary (no frames read).
    public sealed class TelemetrySessionInfo
    {
        public string Path;
        public string FileName;
        public string Game;
        public string GameTitle;
        public string Source;
        public DateTime CreatedUtc;
        public DateTime EndedUtc;
        public double Duration;
        public long Frames;
        public long Events;
        public long RawPackets;
        public int Channels;
        public List<string> Aircraft = new List<string>();
        public List<string> Maps = new List<string>();
        public bool Complete;
        public bool InUse;
        public string EndReason = string.Empty;
        public long SizeBytes;
        public Dictionary<string, string> Header = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Summary = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<TelemetrySessionNav> Nav = new List<TelemetrySessionNav>();

        // The game's telemetry has a position (nav mappings) and, for a finished session, frames with it.
        public bool HasPosition
        {
            get
            {
                bool mapped = Nav.Any(n => n.Target == "lat") && Nav.Any(n => n.Target == "lon") ||
                              Nav.Any(n => n.Target == "u") && Nav.Any(n => n.Target == "v");
                string frames;
                long count;
                if (mapped && Summary.TryGetValue("positionFrames", out frames) && long.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                    return count > 0;
                return mapped;
            }
        }

        public string Info(string key)
        {
            string value;
            return Header.TryGetValue("info." + key, out value) ? value : string.Empty;
        }
    }

    // Reads a session file record by record (frames as a running state, events, raw packets).
    public sealed class TelemetrySessionReader : IDisposable
    {
        public enum RecordKind { None, Frame, Event, Raw }

        private readonly Stream _stream;
        private readonly bool _includeRaw;
        private readonly List<TelemetrySessionChannel> _channels = new List<TelemetrySessionChannel>();
        private readonly List<double> _values = new List<double>();
        private readonly List<int> _changed = new List<int>();
        private readonly List<double> _before = new List<double>();
        private string[] _lines;
        private int _line;
        private byte[] _rawBlock;
        private int _rawPos;

        public TelemetrySessionReader(string path, bool includeRaw = false)
            : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024), includeRaw)
        {
        }

        public TelemetrySessionReader(Stream stream, bool includeRaw = false)
        {
            _stream = stream;
            _includeRaw = includeRaw;
            byte[] magic = new byte[TelemetrySessionFormat.FileMagic.Length];
            if (ReadFully(magic, magic.Length) != magic.Length || !magic.SequenceEqual(TelemetrySessionFormat.FileMagic))
                throw new InvalidDataException("Not a JoyPro telemetry session.");
        }

        public Dictionary<string, string> Header { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Summary { get; private set; }
        public IReadOnlyList<TelemetrySessionChannel> Channels { get { return _channels; } }

        // True when the file ended in a damaged block (a crash during a write): everything before it was read.
        public bool Truncated { get; private set; }

        // Byte offset just after the last whole block (Recover cuts the file there).
        public long ValidLength { get; private set; }

        public RecordKind Kind { get; private set; }
        public double Time { get; private set; }

        // The current value of every channel (NaN = not present), valid after a Frame record.
        public IReadOnlyList<double> Values { get { return _values; } }

        // The channel indexes the current frame changed.
        public IReadOnlyList<int> Changed { get { return _changed; } }

        public string EventKind { get; private set; }
        public string EventText { get; private set; }
        public string RawStream { get; private set; }
        public byte[] RawData { get; private set; }

        public int ChannelIndex(string key)
        {
            int index;
            return key != null && _keyIndex.TryGetValue(key, out index) ? index : -1;
        }

        private readonly Dictionary<string, int> _keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        public double Value(int index)
        {
            return index >= 0 && index < _values.Count ? _values[index] : double.NaN;
        }

        public bool Next()
        {
            while (true)
            {
                if (_lines != null && _line < _lines.Length)
                {
                    string line = _lines[_line++];
                    if (ParseLine(line))
                        return true;
                    continue;
                }
                _lines = null;
                if (_rawBlock != null && _rawPos < _rawBlock.Length)
                {
                    if (ParseRaw())
                        return true;
                    _rawBlock = null;
                    continue;
                }
                _rawBlock = null;
                byte kind;
                byte[] bytes;
                if (!ReadBlock(out kind, out bytes, decode: kind_ => kind_ != TelemetrySessionFormat.KindRaw || _includeRaw))
                {
                    Kind = RecordKind.None;
                    return false;
                }
                if (bytes == null)
                    continue;
                switch (kind)
                {
                    case TelemetrySessionFormat.KindHeader:
                        Header = TelemetrySessionFormat.ParseKeyValues(bytes);
                        break;
                    case TelemetrySessionFormat.KindSummary:
                        Summary = TelemetrySessionFormat.ParseKeyValues(bytes);
                        break;
                    case TelemetrySessionFormat.KindData:
                        _lines = Encoding.UTF8.GetString(bytes).Split('\n');
                        _line = 0;
                        break;
                    case TelemetrySessionFormat.KindRaw:
                        _rawBlock = bytes;
                        _rawPos = 0;
                        break;
                }
            }
        }

        // Reads the header block (the first one).
        public void ReadHeader()
        {
            byte kind;
            byte[] bytes;
            if (ReadBlock(out kind, out bytes, k => true) && kind == TelemetrySessionFormat.KindHeader && bytes != null)
                Header = TelemetrySessionFormat.ParseKeyValues(bytes);
        }

        // Walks the block headers (seeking over the data, decoding only the header and summary); true when the file has a
        // summary (then it is in Summary). Without one (a session in progress or cut off), lastDataTime is the last time
        // in the last whole data block.
        public bool SkipToSummary(out double lastDataTime)
        {
            lastDataTime = 0;
            long lastData = -1;
            byte kind;
            byte[] bytes;
            while (true)
            {
                long blockStart = _stream.Position;
                if (!ReadBlock(out kind, out bytes, k => k == TelemetrySessionFormat.KindSummary || k == TelemetrySessionFormat.KindHeader))
                    break;
                if (kind == TelemetrySessionFormat.KindSummary && bytes != null)
                    Summary = TelemetrySessionFormat.ParseKeyValues(bytes);
                else if (kind == TelemetrySessionFormat.KindHeader && bytes != null)
                    Header = TelemetrySessionFormat.ParseKeyValues(bytes);
                else if (kind == TelemetrySessionFormat.KindData)
                    lastData = blockStart;
            }
            if (Summary == null && lastData >= 0)
            {
                _stream.Seek(lastData, SeekOrigin.Begin);
                if (ReadBlock(out kind, out bytes, k => true) && bytes != null)
                {
                    foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
                    {
                        if (line.Length > 2 && (line[0] == 'F' || line[0] == 'K' || line[0] == 'E') && line[1] == '\t')
                        {
                            int end = line.IndexOf('\t', 2);
                            double t;
                            if (TelemetrySessionFormat.TryParseNumber(end > 0 ? line.Substring(2, end - 2) : line.Substring(2), out t))
                                lastDataTime = Math.Max(lastDataTime, t);
                        }
                    }
                }
            }
            return Summary != null;
        }

        private bool ReadBlock(out byte kind, out byte[] bytes, Func<byte, bool> decode)
        {
            kind = 0;
            bytes = null;
            long start = _stream.Position;
            byte[] head = new byte[TelemetrySessionFormat.BlockHeaderSize];
            int got = ReadFully(head, head.Length);
            if (got == 0)
                return false;
            if (got < head.Length || BitConverter.ToUInt32(head, 0) != TelemetrySessionFormat.BlockMagic)
            {
                Truncated = true;
                return false;
            }
            kind = head[4];
            uint rawLength = BitConverter.ToUInt32(head, 5);
            uint storedLength = BitConverter.ToUInt32(head, 9);
            uint crc = BitConverter.ToUInt32(head, 13);
            if (rawLength > TelemetrySessionFormat.MaxBlockBytes || storedLength > TelemetrySessionFormat.MaxBlockBytes)
            {
                Truncated = true;
                return false;
            }
            bool wanted = decode(kind);
            if (!wanted && _stream.CanSeek)
            {
                if (start + head.Length + storedLength > _stream.Length)
                {
                    Truncated = true;
                    return false;
                }
                _stream.Seek(storedLength, SeekOrigin.Current);
                ValidLength = _stream.Position;
                return true;
            }
            byte[] stored = new byte[storedLength];
            if (ReadFully(stored, stored.Length) != stored.Length || TelemetrySessionFormat.Crc32(stored, 0, stored.Length) != crc)
            {
                Truncated = true;
                return false;
            }
            ValidLength = _stream.Position;
            if (!wanted)
                return true;
            try
            {
                bytes = TelemetrySessionFormat.Decompress(stored, (int)rawLength);
            }
            catch (InvalidDataException)
            {
                Truncated = true;
                return false;
            }
            return true;
        }

        private int ReadFully(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read;
        }

        private bool ParseLine(string line)
        {
            if (line.Length < 2 || line[1] != '\t')
                return false;
            string[] f = line.Split('\t');
            double t;
            switch (line[0])
            {
                case 'C':
                {
                    int index;
                    if (f.Length < 3 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index > 1000000)
                        return false;
                    double min = 0, max = 1;
                    if (f.Length > 6)
                        TelemetrySessionFormat.TryParseNumber(f[6], out min);
                    if (f.Length > 7)
                        TelemetrySessionFormat.TryParseNumber(f[7], out max);
                    while (_channels.Count <= index)
                    {
                        _channels.Add(null);
                        _values.Add(double.NaN);
                    }
                    string channelKey = TelemetrySessionFormat.Unescape(f[2]);
                    _keyIndex[channelKey] = index;
                    _channels[index] = new TelemetrySessionChannel
                    {
                        Index = index,
                        Key = channelKey,
                        Unit = f.Length > 3 ? TelemetrySessionFormat.Unescape(f[3]) : string.Empty,
                        Title = f.Length > 4 ? TelemetrySessionFormat.Unescape(f[4]) : string.Empty,
                        Category = f.Length > 5 ? TelemetrySessionFormat.Unescape(f[5]) : string.Empty,
                        Min = min,
                        Max = max
                    };
                    return false;
                }
                case 'F':
                case 'K':
                {
                    if (f.Length < 2 || !TelemetrySessionFormat.TryParseNumber(f[1], out t))
                        return false;
                    _changed.Clear();
                    bool key = line[0] == 'K';
                    if (key)
                    {
                        // A key frame lists every value present: whatever it does not list is gone. Changed = only
                        // the values that differ from before.
                        _before.Clear();
                        _before.AddRange(_values);
                        for (int i = 0; i < _values.Count; i++)
                            _values[i] = double.NaN;
                    }
                    for (int i = 2; i < f.Length; i++)
                    {
                        string pair = f[i];
                        int eq = pair.IndexOf('=');
                        int index;
                        if (eq <= 0 || !int.TryParse(pair.Substring(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index >= _values.Count)
                            continue;
                        double value;
                        if (eq == pair.Length - 1)
                            value = double.NaN;
                        else if (!TelemetrySessionFormat.TryParseNumber(pair.Substring(eq + 1), out value))
                            continue;
                        _values[index] = value;
                        if (!key)
                            _changed.Add(index);
                    }
                    if (key)
                    {
                        for (int i = 0; i < _values.Count; i++)
                        {
                            double old = i < _before.Count ? _before[i] : double.NaN;
                            double now = _values[i];
                            if (!(old == now || double.IsNaN(old) && double.IsNaN(now)))
                                _changed.Add(i);
                        }
                    }
                    Kind = RecordKind.Frame;
                    Time = t;
                    return true;
                }
                case 'E':
                {
                    if (f.Length < 3 || !TelemetrySessionFormat.TryParseNumber(f[1], out t))
                        return false;
                    Kind = RecordKind.Event;
                    Time = t;
                    EventKind = TelemetrySessionFormat.Unescape(f[2]);
                    EventText = f.Length > 3 ? TelemetrySessionFormat.Unescape(string.Join("\t", f, 3, f.Length - 3)) : string.Empty;
                    return true;
                }
            }
            return false;
        }

        private bool ParseRaw()
        {
            byte[] b = _rawBlock;
            if (_rawPos >= b.Length)
                return false;
            int nameLength = b[_rawPos];
            if (_rawPos + 1 + nameLength + 12 > b.Length)
                return false;
            RawStream = Encoding.UTF8.GetString(b, _rawPos + 1, nameLength);
            int p = _rawPos + 1 + nameLength;
            Time = BitConverter.ToDouble(b, p);
            uint length = BitConverter.ToUInt32(b, p + 8);
            p += 12;
            if (p + length > b.Length)
                return false;
            RawData = new byte[length];
            Array.Copy(b, p, RawData, 0, (int)length);
            _rawPos = p + (int)length;
            Kind = RecordKind.Raw;
            return true;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
