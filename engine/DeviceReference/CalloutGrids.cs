#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // Key grids of a callout sheet (docs/device-reference.md, "Tables and lines"): keys, buttons or switches of one
    // kind that sit together on the picture like a keypad (an MCDU's keys, a row of toggles, a bank of buttons) get ONE
    // table laid out like the keys themselves (grid rows and columns as on the device) with ONE leader line to the
    // cluster's centre, instead of a table and a line per key. Names do not matter (A, CLR, DEP ARR as well as B1..B8).
    //   1. Candidates: parts with a known spot and at most three inputs, no axis; kinds: keys (button, key, osb),
    //      switches (switch, rocker), encoders. Each kind is clustered on its own (mixed panels: one grid per cluster).
    //   2. Clusters: single linkage on the picture (pixels, aspect kept) with the kind's typical spacing: the median
    //      distance between neighbouring sites (parts within 1.5 % of the picture's diagonal are one site: a toggle's
    //      positions placed as buttons of their own); two keys belong together when they are at most 1.55 of it apart.
    //      A spacing above 7 % of the diagonal (a grip's buttons around it) makes no grids.
    //   3. The grid of a cluster (four keys or more): the two main directions of the neighbour links (a keypad on a
    //      photo taken at an angle is a slanted grid), the keys' coordinates along them, rows and columns where the
    //      coordinates jump by more than half a key pitch. Two keys in one cell, or a sparse grid (under 30 % of its
    //      cells used) of eight keys or more: the columns follow the order within each row. A sparse small cluster is
    //      no keypad. Gaps of more than two pitches split a cluster into blocks, then the widest gaps until no block has
    //      more than 6 columns or 8 rows; each block is its own table.
    // Deterministic; the user can split a grid back into single tables (CalloutSheet.Ungrouped).
    internal static class KeyGrids
    {
        private const int MinParts = 4, MaxColumns = 6, MaxRows = 8;
        private const double Link = 1.55, Neighbour = 1.35;

        private sealed class Key
        {
            public SchematicMarker Part;
            public List<SchematicInput> Inputs;
            public double X, Y, A, B;
            public int Row, Col;
        }

        public static List<CalloutTable> Find(List<SchematicMarker> parts, IDictionary<string, double[]> known, double pw, double ph, ICollection<string> ungrouped)
        {
            List<CalloutTable> grids = new List<CalloutTable>();
            if (known == null || known.Count == 0)
                return grids;
            HashSet<string> split = new HashSet<string>(ungrouped ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            List<Tuple<string, Key>> candidates = new List<Tuple<string, Key>>();
            foreach (SchematicMarker m in parts)
            {
                double[] xy;
                if (split.Contains(m.Id) || !known.TryGetValue(m.Id, out xy) || xy == null || xy.Length < 2)
                    continue;
                List<SchematicInput> inputs = m.Inputs.Where(i => i.Input.Length > 0)
                    .GroupBy(i => i.Input, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .OrderBy(CalloutLayouter.DirectionOrder).ThenBy(i => DeviceVocabulary.NaturalKey(i.Input), StringComparer.Ordinal).ToList();
                string kind = KindOf(m, inputs);
                if (kind == null)
                    continue;
                candidates.Add(Tuple.Create(kind, new Key { Part = m, Inputs = inputs, X = xy[0] * pw, Y = xy[1] * ph }));
            }
            foreach (IGrouping<string, Tuple<string, Key>> kind in candidates.GroupBy(c => c.Item1).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<Key> keys = kind.Select(c => c.Item2).ToList();
                if (keys.Count < MinParts)
                    continue;
                // Parts on (nearly) the same spot (a toggle's positions placed as buttons of their own) are one site; the
                // pitch is the sites' typical spacing.
                double same = Math.Sqrt(pw * pw + ph * ph) * 0.015;
                List<Key> sites = new List<Key>();
                foreach (Key k in keys)
                {
                    if (!sites.Any(s => Distance(s, k) <= same))
                        sites.Add(k);
                }
                if (sites.Count < 2)
                    continue;
                double pitch = Math.Max(same, Median(sites.Select(k => sites.Where(o => !ReferenceEquals(o, k)).Min(o => Distance(k, o)))));
                // Keys far apart (a grip's buttons around it) are no keypad: a keypad's keys sit a few percent of the
                // picture apart.
                if (pitch <= 0.5 || pitch > Math.Sqrt(pw * pw + ph * ph) * 0.07)
                    continue;
                foreach (List<Key> cluster in Clusters(keys, pitch * Link))
                {
                    if (cluster.Count < MinParts)
                        continue;
                    foreach (List<Key> block in Fit(cluster, pitch))
                        grids.Add(Table(kind.Key, block, pw, ph));
                }
            }
            return grids;
        }

        private static string KindOf(SchematicMarker m, List<SchematicInput> inputs)
        {
            if (inputs.Count == 0 || inputs.Count > 3 || inputs.Any(i => DeviceVocabulary.InputKind(i.Input) == "axis"))
                return null;
            switch (m.Type)
            {
                case "button":
                case "key":
                case "osb":
                    return inputs.Count <= 2 ? "key" : null;
                case "switch":
                case "rocker":
                    return "switch";
                case "encoder":
                    return "encoder";
            }
            return null;
        }

        private static double Distance(Key a, Key b)
        {
            return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0)
                return 0;
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        // Single linkage: keys within `reach` of each other end up in one cluster (reading order kept).
        private static List<List<Key>> Clusters(List<Key> keys, double reach)
        {
            int[] parent = Enumerable.Range(0, keys.Count).ToArray();
            Func<int, int> root = null;
            root = i => parent[i] == i ? i : (parent[i] = root(parent[i]));
            for (int i = 0; i < keys.Count; i++)
            {
                for (int j = i + 1; j < keys.Count; j++)
                {
                    if (Distance(keys[i], keys[j]) <= reach)
                        parent[root(i)] = root(j);
                }
            }
            return Enumerable.Range(0, keys.Count).GroupBy(root)
                .Select(g => g.Select(i => keys[i]).ToList())
                .OrderBy(c => c.Min(k => k.Y)).ThenBy(c => c.Min(k => k.X))
                .ToList();
        }

        // The grid of a cluster, split into blocks of at most MaxColumns x MaxRows; empty when it is no keypad.
        private static List<List<Key>> Fit(List<Key> cluster, double pitch)
        {
            List<List<Key>> blocks = new List<List<Key>>();
            List<double> angles = new List<double>();
            List<Tuple<Key, Key, double>> links = new List<Tuple<Key, Key, double>>();
            for (int i = 0; i < cluster.Count; i++)
            {
                for (int j = i + 1; j < cluster.Count; j++)
                {
                    double d = Distance(cluster[i], cluster[j]);
                    if (d <= pitch * Neighbour && d > pitch * 0.4)
                    {
                        double angle = Math.Atan2(cluster[j].Y - cluster[i].Y, cluster[j].X - cluster[i].X);
                        if (angle < 0)
                            angle += Math.PI;
                        if (angle >= Math.PI)
                            angle -= Math.PI;
                        angles.Add(angle);
                        links.Add(Tuple.Create(cluster[i], cluster[j], angle));
                    }
                }
            }
            double first = Peak(angles, double.NaN);
            double second = Peak(angles, first);
            if (double.IsNaN(first))
                first = 0;
            if (double.IsNaN(second))
                second = first + Math.PI / 2;
            double[] e1 = { Math.Cos(first), Math.Sin(first) }, e2 = { Math.Cos(second), Math.Sin(second) };
            double[] u = Math.Abs(e1[0]) >= Math.Abs(e2[0]) ? e1 : e2, v = ReferenceEquals(u, e1) ? e2 : e1;
            if (u[0] < 0 || (u[0] == 0 && u[1] < 0))
                u = new[] { -u[0], -u[1] };
            if (v[1] < 0 || (v[1] == 0 && v[0] < 0))
                v = new[] { -v[0], -v[1] };
            double det = u[0] * v[1] - u[1] * v[0];
            if (Math.Abs(det) < 0.25)
            {
                v = new[] { -u[1], u[0] };
                if (v[1] < 0)
                    v = new[] { -v[0], -v[1] };
                det = u[0] * v[1] - u[1] * v[0];
            }
            double cx = cluster.Average(k => k.X), cy = cluster.Average(k => k.Y);
            foreach (Key k in cluster)
            {
                double dx = k.X - cx, dy = k.Y - cy;
                k.A = (dx * v[1] - dy * v[0]) / det;
                k.B = (u[0] * dy - u[1] * dx) / det;
            }
            double alongU = Math.Atan2(u[1], u[0]), alongV = Math.Atan2(v[1], v[0]);
            double pa = Median(links.Where(l => AngleGap(l.Item3, alongU) < 0.26).Select(l => Math.Abs(l.Item1.A - l.Item2.A)));
            double pb = Median(links.Where(l => AngleGap(l.Item3, alongV) < 0.26).Select(l => Math.Abs(l.Item1.B - l.Item2.B)));
            if (pa <= 0)
                pa = pitch;
            if (pb <= 0)
                pb = pitch;
            int rows = Band(cluster, k => k.B, pb * 0.5, (k, i) => k.Row = i);
            int cols = Band(cluster, k => k.A, pa * 0.5, (k, i) => k.Col = i);
            // Two keys in one cell, or keys scattered over many columns (an uneven panel): the columns follow the order
            // within each row instead (the rows stay as on the device).
            bool ranked = false;
            bool collision = cluster.GroupBy(k => k.Row * 1000 + k.Col).Any(g => g.Count() > 1);
            if (collision || cluster.Count < 0.3 * rows * cols)
            {
                // A few scattered buttons (a grip) are no keypad; a dense panel is.
                if (!collision && cluster.Count < 8)
                    return blocks;
                ranked = true;
                cols = 0;
                foreach (IGrouping<int, Key> row in cluster.GroupBy(k => k.Row))
                {
                    int c = 0;
                    foreach (Key k in row.OrderBy(k => k.A))
                        k.Col = c++;
                    cols = Math.Max(cols, c);
                }
            }
            if (cluster.Count < 0.3 * rows * cols)
                return blocks;
            // Blocks: split at the widest gaps between columns (rows) until each block is small enough; a gap of more
            // than two key pitches splits anyway (a digit pad next to the letters).
            List<List<int>> colGroups = Split(Enumerable.Range(0, cols).ToList(), c => ranked ? c : cluster.Where(k => k.Col == c).Average(k => k.A), ranked ? double.MaxValue : pa * 2.2, MaxColumns);
            List<List<int>> rowGroups = Split(Enumerable.Range(0, rows).ToList(), r => cluster.Where(k => k.Row == r).Average(k => k.B), pb * 2.2, MaxRows);
            foreach (List<int> rowGroup in rowGroups)
            {
                foreach (List<int> colGroup in colGroups)
                {
                    List<Key> block = cluster.Where(k => rowGroup.Contains(k.Row) && colGroup.Contains(k.Col)).ToList();
                    if (block.Count < 2)
                        continue;
                    // Rows and columns of the block numbered from 0, empty ones left out.
                    List<int> usedRows = block.Select(k => k.Row).Distinct().OrderBy(r => r).ToList();
                    List<int> usedCols = block.Select(k => k.Col).Distinct().OrderBy(c => c).ToList();
                    foreach (Key k in block)
                    {
                        k.Row = usedRows.IndexOf(k.Row);
                        k.Col = usedCols.IndexOf(k.Col);
                    }
                    blocks.Add(block.OrderBy(k => k.Row).ThenBy(k => k.Col).ToList());
                }
            }
            return blocks;
        }

        // Splits consecutive bands (columns or rows, in order) into groups: at every gap wider than `wide`, then at the
        // widest gap of a group until no group has more than `most` bands.
        private static List<List<int>> Split(List<int> bands, Func<int, double> centre, double wide, int most)
        {
            List<List<int>> groups = new List<List<int>>();
            List<int> current = new List<int>();
            for (int i = 0; i < bands.Count; i++)
            {
                if (current.Count > 0 && centre(bands[i]) - centre(bands[i - 1]) > wide)
                {
                    groups.Add(current);
                    current = new List<int>();
                }
                current.Add(bands[i]);
            }
            if (current.Count > 0)
                groups.Add(current);
            for (int guard = 0; guard < 64; guard++)
            {
                int index = groups.FindIndex(g => g.Count > most);
                if (index < 0)
                    break;
                List<int> group = groups[index];
                // The widest gap, among the cuts that leave both parts within a sensible size.
                int cut = -1;
                double widest = double.MinValue;
                for (int i = 1; i < group.Count; i++)
                {
                    double gap = centre(group[i]) - centre(group[i - 1]);
                    // Prefer cuts near the middle when the gaps are alike.
                    double score = gap - Math.Abs(i - group.Count / 2.0) * 1e-6;
                    if (Math.Min(i, group.Count - i) >= Math.Min(2, group.Count / 2) && score > widest)
                    {
                        widest = score;
                        cut = i;
                    }
                }
                if (cut < 0)
                    cut = group.Count / 2;
                groups[index] = group.Take(cut).ToList();
                groups.Insert(index + 1, group.Skip(cut).ToList());
            }
            return groups;
        }

        // The direction most links run along (angles 0..pi), away from `away` by 30 degrees when given; NaN = none.
        private static double Peak(List<double> angles, double away)
        {
            const int bins = 36;
            double width = Math.PI / bins;
            double[] score = new double[bins];
            foreach (double a in angles)
            {
                if (!double.IsNaN(away) && AngleGap(a, away) < Math.PI / 6)
                    continue;
                int b = Math.Min(bins - 1, (int)(a / width));
                score[b] += 2;
                score[(b + 1) % bins] += 1;
                score[(b + bins - 1) % bins] += 1;
            }
            int best = -1;
            for (int b = 0; b < bins; b++)
            {
                if (score[b] > 0 && (best < 0 || score[b] > score[best] + 1e-9))
                    best = b;
            }
            if (best < 0)
                return double.NaN;
            double centre = (best + 0.5) * width;
            // The mean of the links near the peak (doubled angles: 0 and pi are the same direction).
            double sx = 0, sy = 0;
            foreach (double a in angles)
            {
                if (AngleGap(a, centre) < 0.21 && (double.IsNaN(away) || AngleGap(a, away) >= Math.PI / 6))
                {
                    sx += Math.Cos(2 * a);
                    sy += Math.Sin(2 * a);
                }
            }
            double mean = Math.Atan2(sy, sx) / 2;
            return mean < 0 ? mean + Math.PI : mean;
        }

        // The gap between two directions (0..pi/2), both taken mod pi.
        private static double AngleGap(double a, double b)
        {
            double d = Math.Abs(a - b) % Math.PI;
            return Math.Min(d, Math.PI - d);
        }

        // Groups the keys into bands along a coordinate (a new band where it jumps by more than `gap`): returns the count.
        private static int Band(List<Key> keys, Func<Key, double> along, double gap, Action<Key, int> set)
        {
            int band = 0;
            double last = double.NaN;
            foreach (Key k in keys.OrderBy(along))
            {
                if (!double.IsNaN(last) && along(k) - last > gap)
                    band++;
                set(k, band);
                last = along(k);
            }
            return band + 1;
        }

        private static CalloutTable Table(string kind, List<Key> block, double pw, double ph)
        {
            CalloutTable table = new CalloutTable
            {
                Id = "grid:" + block[0].Part.Id,
                Grid = true,
                Columns = block.Max(k => k.Col) + 1,
                ShowModifier = false,
                Known = true,
                SpotX = CalloutSheet.Clamp01(block.Average(k => k.X) / pw),
                SpotY = CalloutSheet.Clamp01(block.Average(k => k.Y) / ph)
            };
            string word = kind == "switch" ? "Switches" : kind == "encoder" ? "Encoders" : block.All(k => k.Part.Type == "key") ? "Keys" : "Buttons";
            // The words all keys' names start with ("SELECT JETT", "PARK BRK"), else the first and the last key.
            List<string[]> words = block.Select(k => CalloutLayouter.PartTitle(k.Part).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            int common = 0;
            while (words.All(w => w.Length > common) && words.All(w => string.Equals(w[common], words[0][common], StringComparison.OrdinalIgnoreCase)))
                common++;
            string shared = string.Join(" ", words[0].Take(common));
            string firstRest = string.Join(" ", words[0].Skip(common)), lastRest = string.Join(" ", words[words.Count - 1].Skip(common));
            table.Title = shared.Length >= 3
                ? word + " " + shared + (firstRest.Length > 0 && lastRest.Length > 0 ? " " + firstRest + " - " + lastRest : string.Empty)
                : word + " " + Name(block[0].Part) + (block.Count > 1 ? " - " + Name(block[block.Count - 1].Part) : string.Empty);
            foreach (Key k in block)
            {
                table.Parts.Add(k.Part.Id);
                // The lines of a key with several inputs: up / dn / push, else the inputs' own names (BRT - / BRT +).
                List<string> subs = k.Inputs.Select(i => Short(CalloutLayouter.RowLabel(k.Part, i))).ToList();
                if (subs.Distinct(StringComparer.OrdinalIgnoreCase).Count() < subs.Count)
                    subs = k.Inputs.Select((i, n) => i.Name.Length > 0 ? i.Name : (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                for (int s = 0; s < k.Inputs.Count; s++)
                {
                    SchematicInput input = k.Inputs[s];
                    string label = k.Inputs.Count == 1 ? Name(k.Part)
                        : subs[s].StartsWith(Name(k.Part), StringComparison.OrdinalIgnoreCase) ? subs[s] : Name(k.Part) + " " + subs[s];
                    table.Rows.Add(new CalloutRow
                    {
                        Input = input.Input,
                        Label = label.Length > 14 ? label.Substring(0, 14).TrimEnd() : label,
                        Icon = CalloutLayouter.RowIcon(input),
                        Part = k.Part.Id,
                        GridRow = k.Row,
                        GridCol = k.Col,
                        Sub = s
                    });
                }
            }
            return table;
        }

        // The key's name on its cell: its short name or printed label, at most 10 letters.
        private static string Name(SchematicMarker m)
        {
            string name = (m.Short ?? string.Empty).Trim();
            if (name.Length == 0)
                name = CalloutLayouter.PartTitle(m);
            return name.Length > 10 ? name.Substring(0, 10).TrimEnd() : name;
        }

        private static string Short(string direction)
        {
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "up": return "up";
                case "down": return "dn";
                case "center": case "mid": return "mid";
                case "press": return "push";
                case "left": return "L";
                case "right": return "R";
                case "forward": return "fwd";
                case "back": return "aft";
                case "clockwise": return "cw";
                case "counter-clockwise": return "ccw";
            }
            return direction ?? string.Empty;
        }
    }
}
