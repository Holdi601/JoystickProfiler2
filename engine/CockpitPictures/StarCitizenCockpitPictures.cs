#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // Star Citizen: the cockpit pictures of every flyable ship, made from the game's Data.p4k (read only, never while the
    // game runs; encrypted entries are not read). The ships come from the DataForge database (Game2.dcb): each ship's
    // default loadout names its pilot seat and dashboard items (their geometry, their interaction points with the
    // game's own texts), the object containers hold the cockpit interior; the hull model's hardpoints place them. The
    // pictures show the hull's cockpit around the pilot's seat, the dashboard, the seat and the controls; the stick and
    // the throttle of the dashboard or seat (by their part names) get pictures of their own; the callouts are the
    // dashboard's interaction points (named as the game names them) and the grips' buttons.
    internal static class StarCitizenCockpitPictures
    {
        public sealed class Job
        {
            public string GameRoot = string.Empty;
            public string OutRoot = string.Empty;
            public string CacheFolder = string.Empty;
            public List<string> Only = new List<string>();
            public bool Force;
            public int Width = 1600;
            public string ViewFilter = string.Empty;
            public Func<byte[], int, int, byte[]> Zstd;
            public Action<int, int, string, string> Progress;
            public Func<bool> Cancelled;
            // Tests: an archive already open.
            public StarCitizenP4k Archive;
        }

        public sealed class SetResult
        {
            public string Plane = string.Empty;
            public List<string> Planes = new List<string>();
            public string Folder = string.Empty;
            public int Views;
            public int Callouts;
            public bool Skipped;
            public string Error = string.Empty;
            public List<string> Notes = new List<string>();
        }

        // The Data.p4k of the installation (LIVE first, then the other channels).
        public static string ArchivePath(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return string.Empty;
            foreach (string c in new[] { root, Path.Combine(root, "LIVE"), Path.Combine(root, "StarCitizen", "LIVE") })
            {
                string f = Path.Combine(c, "Data.p4k");
                if (File.Exists(f))
                    return f;
            }
            foreach (string dir in Directory.GetDirectories(root))
            {
                string f = Path.Combine(dir, "Data.p4k");
                if (File.Exists(f))
                    return f;
            }
            return string.Empty;
        }

        public static string GameVersion(string archive)
        {
            try
            {
                string manifest = Path.Combine(Path.GetDirectoryName(archive) ?? string.Empty, "build_manifest.id");
                if (File.Exists(manifest))
                {
                    string text = File.ReadAllText(manifest);
                    Match b = Regex.Match(text, "\"Branch\"\\s*:\\s*\"([^\"]+)\"");
                    Match n = Regex.Match(text, "\"RequestedP4ChangeNum\"\\s*:\\s*\"([^\"]+)\"");
                    if (b.Success || n.Success)
                        return (b.Success ? b.Groups[1].Value : "Star Citizen") + (n.Success ? " " + n.Groups[1].Value : string.Empty);
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------ the ships

        public sealed class LoadoutEntry
        {
            public string Port = string.Empty;
            public string Class = string.Empty;
            public List<LoadoutEntry> Children = new List<LoadoutEntry>();
        }

        public sealed class Container
        {
            public string File = string.Empty;
            public double[] Matrix = Mat4.Identity();   // in the ship
        }

        public sealed class Ship
        {
            public string Class = string.Empty;
            public string Name = string.Empty;
            public string Definition = string.Empty;      // the vehicle implementation XML (encrypted)
            public string Proxy = string.Empty;           // the physics grid model (its folder holds the hull)
            public List<Container> Containers = new List<Container>();
            public List<LoadoutEntry> Loadout = new List<LoadoutEntry>();
            public string LoadoutFile = string.Empty;     // a loadout kept in an XML file (encrypted)
        }

        private static readonly Regex NotFlyable = new Regex("(^|_)(AI|Template|Unmanned|PU|EA|SM|Derelict|Wreck|Wreckage|Teach|Tutorial|TEMP|Temp|Boarded|ShipShowdown|Showdown|Indestructible|Display|NoInterior|Hijacked|Crashed|Mission|Swarm|Collector|Destructable|Destructible|Dummy|Test|Prop|Hangar)(_|$)|^probe_|^Orbital_|^EAObjective|^Spaceship_", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<Ship> Ships(DataForge df, StarCitizenTexts texts)
        {
            List<Ship> ships = new List<Ship>();
            foreach (int r in df.RecordsUnder("libs/foundry/records/entities/spaceships/"))
            {
                string rec = df.RecordName(r);
                if (!rec.StartsWith("EntityClassDefinition.", StringComparison.Ordinal))
                    continue;
                string cls = rec.Substring("EntityClassDefinition.".Length);
                if (NotFlyable.IsMatch(cls))
                    continue;
                Dictionary<string, object> e = df.Read(r, 16);
                Dictionary<string, object> v = DataForge.Component(e, "VehicleComponentParams");
                if (v == null)
                    continue;
                Ship s = new Ship { Class = cls, Definition = DataForge.Str(v, "vehicleDefinition") };
                s.Name = texts.Text(DataForge.Str(v, "vehicleName"));
                if (s.Name.Length == 0)
                    s.Name = cls.Replace('_', ' ');
                s.Proxy = DataForge.Str(DataForge.Obj(DataForge.Obj(v, "physicsGrid"), "physicsProxy"), "path");
                foreach (object oc in DataForge.List(v, "objectContainers"))
                {
                    string file = DataForge.Str(oc, "fileName");
                    if (file.Length == 0)
                        continue;
                    s.Containers.Add(new Container { File = file, Matrix = QuatT(DataForge.Obj(oc, "Offset")) });
                }
                Dictionary<string, object> lo = DataForge.Obj(DataForge.Component(e, "SEntityComponentDefaultLoadoutParams"), "loadout");
                if (DataForge.TypeOf(lo) == "SItemPortLoadoutXMLParams")
                    s.LoadoutFile = DataForge.Str(lo, "loadoutPath");
                else
                    s.Loadout = Entries(lo);
                ships.Add(s);
            }
            return ships;
        }

        private static List<LoadoutEntry> Entries(Dictionary<string, object> loadout)
        {
            List<LoadoutEntry> list = new List<LoadoutEntry>();
            foreach (object o in DataForge.List(loadout, "entries"))
            {
                string cls = DataForge.Str(o, "entityClassName");
                string port = DataForge.Str(o, "itemPortName");
                if (cls.Length == 0 || port.Length == 0)
                    continue;
                list.Add(new LoadoutEntry { Port = port, Class = cls, Children = Entries(DataForge.Obj(o, "loadout")) });
            }
            return list;
        }

        // A QuatT (Rotation: Ang3 in degrees, Position: Vec3) as a matrix.
        private static double[] QuatT(Dictionary<string, object> q)
        {
            if (q == null)
                return Mat4.Identity();
            Dictionary<string, object> p = DataForge.Obj(q, "Position"), a = DataForge.Obj(q, "Rotation");
            double[] m = Mat4.Translation(DataForge.Num(p, "x"), DataForge.Num(p, "y"), DataForge.Num(p, "z"));
            double ax = DataForge.Num(a, "x") * Math.PI / 180, ay = DataForge.Num(a, "y") * Math.PI / 180, az = DataForge.Num(a, "z") * Math.PI / 180;
            if (Math.Abs(ax) + Math.Abs(ay) + Math.Abs(az) > 1e-9)
                m = Mat4.Multiply(m, Euler(ax, ay, az));
            return m;
        }

        // CryEngine's Ang3 (x pitch, y roll, z yaw): Rz * Ry * Rx.
        private static double[] Euler(double x, double y, double z)
        {
            double[] rx = Mat4.Rotation(new[] { Math.Cos(x / 2), Math.Sin(x / 2), 0, 0 });
            double[] ry = Mat4.Rotation(new[] { Math.Cos(y / 2), 0, Math.Sin(y / 2), 0 });
            double[] rz = Mat4.Rotation(new[] { Math.Cos(z / 2), 0, 0, Math.Sin(z / 2) });
            return Mat4.Multiply(rz, Mat4.Multiply(ry, rx));
        }

        // ------------------------------------------------------------------ building

        private sealed class Context
        {
            public Job Job;
            public StarCitizenP4k P4k;
            public DataForge Df;
            public StarCitizenTexts Texts;
            public Dictionary<string, IvoModel> Models = new Dictionary<string, IvoModel>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<StarCitizenMaterial>> Mtls = new Dictionary<string, List<StarCitizenMaterial>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TextureImage> Textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> LayerColors = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Dictionary<string, object>> Items = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Encrypted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<CalloutCommand> FlightActions = new List<CalloutCommand>();
            public List<string> SeatNames;
            public List<string> DashNames;
        }

        public static List<SetResult> Build(Job job)
        {
            List<SetResult> results = new List<SetResult>();
            string running = StarCitizenP4k.RunningGame();
            if (running.Length > 0)
            {
                results.Add(new SetResult { Plane = "Star Citizen", Error = "Star Citizen is running (" + running + "): close Star Citizen first" });
                return results;
            }
            string archive = job.Archive != null ? job.Archive.Path : ArchivePath(job.GameRoot);
            if (job.Archive == null && archive.Length == 0)
            {
                results.Add(new SetResult { Plane = "Star Citizen", Error = "Data.p4k was not found in the game folder" });
                return results;
            }
            Context cx = new Context { Job = job };
            StarCitizenP4k p4k = job.Archive;
            try
            {
                if (p4k == null)
                {
                    if (job.Progress != null)
                        job.Progress(0, 1, "Star Citizen, reading the archive index", Path.GetFileName(archive));
                    p4k = StarCitizenP4k.Open(archive, job.CacheFolder, job.Zstd);
                }
                cx.P4k = p4k;
                if (job.Progress != null)
                    job.Progress(0, 1, "Star Citizen, reading the game database", "Game2.dcb");
                byte[] dcb = p4k.Read("Data\\Game2.dcb");
                if (dcb == null)
                {
                    results.Add(new SetResult { Plane = "Star Citizen", Error = "Game2.dcb was not found in Data.p4k" });
                    return results;
                }
                cx.Df = DataForge.Parse(dcb);
                byte[] ini = null;
                try
                {
                    ini = p4k.Read("Data\\Localization\\english\\global.ini");
                }
                catch (StarCitizenP4k.EncryptedEntryException)
                {
                }
                cx.Texts = StarCitizenTexts.Parse(ini);
                try
                {
                    cx.FlightActions = FlightActions(CryXml.Parse(p4k.Read("Data\\Libs\\Config\\defaultProfile.xml")), cx.Texts);
                }
                catch (Exception)
                {
                }
                string version = job.Archive != null ? string.Empty : GameVersion(archive);
                List<Ship> ships = Ships(cx.Df, cx.Texts);
                GC.Collect();
                // One set per cockpit: the ships (paint and edition variants) with the same hull, seat and dashboard.
                List<List<Ship>> groups = new List<List<Ship>>();
                Dictionary<string, List<Ship>> byKey = new Dictionary<string, List<Ship>>(StringComparer.OrdinalIgnoreCase);
                foreach (Ship s in ships)
                {
                    LoadoutEntry seat, dash;
                    PilotParts(cx, s, out seat, out dash);
                    string key = s.Proxy + "|" + (seat == null ? s.Class : seat.Class) + "|" + (dash == null ? string.Empty : dash.Class);
                    List<Ship> g;
                    if (!byKey.TryGetValue(key, out g))
                    {
                        byKey[key] = g = new List<Ship>();
                        groups.Add(g);
                    }
                    g.Add(s);
                }
                long dcbTime = File.Exists(archive) ? File.GetLastWriteTimeUtc(archive).Ticks : 0;
                int i = 0;
                foreach (List<Ship> g in groups)
                {
                    i++;
                    if (job.Cancelled != null && job.Cancelled())
                        break;
                    // The base ship first (the shortest class name: AEGS_Gladius before AEGS_Gladius_Valiant).
                    List<Ship> ordered = g.OrderBy(s => s.Class.Length).ThenBy(s => s.Class, StringComparer.OrdinalIgnoreCase).ToList();
                    Ship first = ordered[0];
                    List<string> names = ordered.Select(s => s.Name).Concat(ordered.Select(s => s.Class)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (job.Only.Count > 0 && !job.Only.Any(o => names.Any(n => n.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)))
                        continue;
                    string key = Regex.Replace(first.Class, "[^A-Za-z0-9_\\-]", "_");
                    string folder = Path.Combine(job.OutRoot, key);
                    SetResult r = new SetResult { Plane = names[0], Planes = names, Folder = folder };
                    results.Add(r);
                    string manifest = Path.Combine(folder, PictureWriter.ManifestName);
                    if (!job.Force && job.ViewFilter.Length == 0 && File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest).Ticks > dcbTime && dcbTime > 0 &&
                        File.ReadAllText(manifest).Contains("\"generator\":" + PictureWriter.GeneratorVersion.ToString(CultureInfo.InvariantCulture)))
                    {
                        r.Skipped = true;
                        continue;
                    }
                    if (job.Progress != null)
                        job.Progress(i, groups.Count, "Star Citizen, cockpit pictures", names[0]);
                    try
                    {
                        BuildOne(cx, first, folder, version, r);
                    }
                    catch (Exception ex)
                    {
                        r.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                    // Memory: the models and textures of one ship are not shared much with the next (a hull is tens of
                    // megabytes); keep only the small models (seats, dashboards of a family).
                    foreach (string big in cx.Models.Where(x => x.Value != null && x.Value.VertexCount > 60000).Select(x => x.Key).ToList())
                        cx.Models.Remove(big);
                    if (cx.Models.Count > 200)
                        cx.Models.Clear();
                    if (cx.Textures.Count > 150)
                        cx.Textures.Clear();
                    if (cx.Items.Count > 3000)
                        cx.Items.Clear();
                    GC.Collect();
                }
            }
            catch (Exception ex)
            {
                results.Add(new SetResult { Plane = "Star Citizen", Error = ex.GetType().Name + ": " + ex.Message });
            }
            finally
            {
                if (p4k != null && job.Archive == null)
                    p4k.Dispose();
            }
            // Which encrypted entries the pictures would have used (not read): a list next to the sets.
            try
            {
                List<string> enc = cx.Encrypted.Concat(p4k == null ? new string[0] : p4k.EncryptedAsked.ToArray()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (enc.Count > 0 && job.Only.Count == 0)
                {
                    Directory.CreateDirectory(job.OutRoot);
                    File.WriteAllLines(Path.Combine(job.OutRoot, "unread-entries.txt"), new[] { "Entries of Data.p4k the cockpit pictures would have used but did not read (stored encrypted):" }.Concat(enc));
                }
            }
            catch (Exception)
            {
            }
            DcsCockpitPictures.WriteIndex(job.OutRoot, results.Where(x => x.Folder.Length > 0).Select(x => new DcsCockpitPictures.SetResult { Plane = x.Plane, Planes = x.Planes, Folder = x.Folder, Error = x.Error }).ToList());
            return results;
        }

        // The pilot's seat and dashboard of the default loadout.
        private static void PilotParts(Context cx, Ship s, out LoadoutEntry seat, out LoadoutEntry dash)
        {
            // (only ports that can hold a seat or a dashboard are looked at: items are big records)
            List<LoadoutEntry> all = Flatten(s.Loadout).Where(e => Regex.IsMatch(e.Port + " " + e.Class, "seat|dashboard|cockpit|pilot|console|controller_flight", RegexOptions.IgnoreCase)).ToList();
            List<LoadoutEntry> seats = all.Where(e => Item(cx, e.Class) != null && DataForge.Component(Item(cx, e.Class), "SCItemSeatParams") != null).ToList();
            Func<LoadoutEntry, int> seatRank = e =>
            {
                string p = e.Port.ToLowerInvariant();
                if (Regex.IsMatch(p, "co_?pilot|gunner|turret|passenger|bed|engineer|crane|tractor|salvage|mining|remote|captain|jump"))
                    return 3;
                if (p.Contains("pilot"))
                    return 0;
                if (p.Contains("seat"))
                    return 1;
                return 2;
            };
            seat = seats.Where(e => !Regex.IsMatch(e.Port + " " + e.Class, "escape|pod|bed|bunk|toilet|bench|medical|jumpseat", RegexOptions.IgnoreCase)).OrderBy(seatRank).FirstOrDefault();
            List<LoadoutEntry> dashes = all.Where(e => Item(cx, e.Class) != null && DataForge.Component(Item(cx, e.Class), "SCItemSeatDashboardParams") != null && DataForge.Component(Item(cx, e.Class), "SCItemSeatParams") == null).ToList();
            dash = dashes.OrderBy(seatRank).FirstOrDefault();
            // Ships whose seats are default items of the (encrypted) vehicle definition: the ship's own pilot seat and
            // dashboard items by their names (DRAK_Cutlass_Seat_Pilot for DRAK_Cutlass_Black), at the usual hardpoints.
            if (seat == null)
            {
                string cls = ByShipName(cx, s.Class, "seat");
                if (cls.Length > 0)
                {
                    seat = new LoadoutEntry { Port = "hardpoint_seat_pilot", Class = cls };
                    s.Loadout.Add(seat);
                }
            }
            // A seat that is not also the dashboard: the ship's pilot dashboard by name.
            if (dash == null && seat != null && DataForge.Component(Item(cx, seat.Class), "SCItemSeatDashboardParams") == null)
            {
                string d = ByShipName(cx, s.Class, "dashboard");
                if (d.Length > 0)
                {
                    dash = new LoadoutEntry { Port = "hardpoint_dashboard_pilot", Class = d };
                    s.Loadout.Add(dash);
                }
            }
        }


        // The pilot seat (or dashboard) item named after the ship: the longest ship name prefix with such an item.
        private static string ByShipName(Context cx, string shipClass, string kind)
        {
            if (cx.SeatNames == null)
            {
                cx.SeatNames = new List<string>();
                cx.DashNames = new List<string>();
                foreach (int r in cx.Df.RecordsUnder("libs/foundry/records/entities/scitem/ships/"))
                {
                    string n = cx.Df.RecordName(r);
                    if (!n.StartsWith("EntityClassDefinition.", StringComparison.Ordinal))
                        continue;
                    n = n.Substring("EntityClassDefinition.".Length);
                    if (Regex.IsMatch(n, "access|co_?pilot|turret|tractor|passenger|bed|engineer|gunner|addon|btn|support|crane|salvage|mining|escape|jump", RegexOptions.IgnoreCase))
                        continue;
                    if (n.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0)
                        cx.SeatNames.Add(n);
                    else if (n.IndexOf("dashboard", StringComparison.OrdinalIgnoreCase) >= 0)
                        cx.DashNames.Add(n);
                }
            }
            List<string> pool = kind == "seat" ? cx.SeatNames : cx.DashNames;
            string prefix = shipClass;
            while (prefix.Length > 0)
            {
                string found = pool.Where(n => n.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.IndexOf("pilot", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(n => n.Length).FirstOrDefault();
                if (found != null)
                    return found;
                int u = prefix.LastIndexOf('_');
                if (u <= 4)
                    break;
                prefix = prefix.Substring(0, u);
            }
            return string.Empty;
        }

        private static IEnumerable<LoadoutEntry> Flatten(List<LoadoutEntry> list)
        {
            foreach (LoadoutEntry e in list)
            {
                yield return e;
                foreach (LoadoutEntry c in Flatten(e.Children))
                    yield return c;
            }
        }

        private static Dictionary<string, object> Item(Context cx, string cls)
        {
            Dictionary<string, object> e;
            if (cx.Items.TryGetValue(cls, out e))
                return e;
            int r = cx.Df.Find("EntityClassDefinition." + cls);
            e = r < 0 ? null : cx.Df.Read(r, 9);
            cx.Items[cls] = e;
            return e;
        }

        private static string GeometryOf(Dictionary<string, object> item)
        {
            Dictionary<string, object> g = DataForge.Component(item, "SGeometryResourceParams");
            return DataForge.Str(DataForge.Obj(DataForge.Obj(DataForge.Obj(g, "Geometry"), "Geometry"), "Geometry"), "path");
        }

        private static readonly Regex HullSuffix = new Regex("(_local_?physics_?grid|_physics_?grid|_local_?grid|_no_?coll?ide(_mesh)?|_no_?colide(_mesh)?|_nocollide|_collision|_phys(ics)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The hull model: next to the ship's physics grid model, named like it (aegs_gladius_physics_grid.cgf ->
        // aegs_gladius.cga), else named like the ship class; it must hold the pilot seat's hardpoint.
        private static IvoModel Hull(Context cx, Ship s, string seatPort, out string path)
        {
            path = string.Empty;
            List<string> folders = new List<string>();
            List<string> stems = new List<string>();
            string proxy = s.Proxy.Replace('/', '\\');
            if (proxy.Length > 0)
            {
                string f = Path.GetDirectoryName(proxy) ?? string.Empty;
                folders.Add(f);
                string parent = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(parent))
                    folders.Add(parent);
                stems.Add(HullSuffix.Replace(Path.GetFileNameWithoutExtension(proxy), string.Empty));
            }
            string c = s.Class;
            while (c.Length > 0)
            {
                stems.Add(c);
                int u = c.LastIndexOf('_');
                if (u <= 4)
                    break;
                c = c.Substring(0, u);
            }
            foreach (string f in folders.ToList())
            {
                folders.Add(Path.Combine(f, "exterior"));
                folders.Add(Path.Combine(f, "interior"));
            }
            string found = string.Empty;
            Func<IEnumerable<string>, IvoModel> first = list =>
            {
                foreach (string cand in list.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!cx.P4k.Exists(cand))
                        continue;
                    IvoModel m = Model(cx, cand);
                    if (m != null && Hardpoint(m, seatPort) != null)
                    {
                        found = cand;
                        return m;
                    }
                }
                return null;
            };
            // The model named like the ship next to its physics grid model (or in its exterior folder).
            IvoModel hull = first(folders.SelectMany(folder => stems.Select(stem => Path.Combine(folder, stem + ".cga"))));
            // Else a model of that name anywhere in the maker's folder (Objects\Spaceships\Ships\DRAK\...).
            Match maker = Regex.Match(proxy, "^(?:data\\\\)?objects\\\\spaceships\\\\ships\\\\[^\\\\]+", RegexOptions.IgnoreCase);
            if (hull == null && maker.Success)
            {
                HashSet<string> wanted = new HashSet<string>(stems.Select(x => x + ".cga"), StringComparer.OrdinalIgnoreCase);
                hull = first(cx.P4k.List(maker.Value).Where(n => wanted.Contains(Path.GetFileName(n))));
            }
            // Else the big models of those folders (not seats or dashboards: they name the seat's hardpoint too).
            if (hull == null)
            {
                hull = first(folders.SelectMany(folder => cx.P4k.List(folder)
                    .Where(n => n.EndsWith(".cga", StringComparison.OrdinalIgnoreCase) && n.Substring(StarCitizenP4k.Normalize(folder).Length + 1).IndexOf('\\') < 0 &&
                                !Regex.IsMatch(Path.GetFileName(n), "_lod\\d|seat|dashboard|scitem", RegexOptions.IgnoreCase))
                    .OrderByDescending(n => cx.P4k.SizeOf(n)).Take(4)));
            }
            path = found;
            return hull;
        }

        private static IvoModel Model(Context cx, string path)
        {
            string p = StarCitizenP4k.Normalize(path);
            IvoModel m;
            if (cx.Models.TryGetValue(p, out m))
                return m;
            m = null;
            try
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".cga" || ext == ".cgf")
                {
                    byte[] model = cx.P4k.Read(p);
                    byte[] mesh = model == null ? null : cx.P4k.Read(p + "m");
                    if (model != null)
                    {
                        m = IvoModel.Read(model, mesh);
                        if (m.Problem.Length > 0 && m.Problem != "no mesh" && (m.Positions == null || m.Indices == null))
                        {
                            lock (cx.Unread)
                                cx.Unread.Add(Path.GetFileName(p) + " (" + m.Problem + ")");
                        }
                    }
                }
                else if (ext.Length > 0)
                {
                    lock (cx.Unread)
                        cx.Unread.Add(Path.GetFileName(p) + " (" + ext + " models are not read)");
                }
            }
            catch (StarCitizenP4k.EncryptedEntryException ex)
            {
                cx.Encrypted.Add(ex.Entry);
                m = null;
            }
            cx.Models[p] = m;
            return m;
        }

        // One placed model: its matrix in the ship, the item it is (for its interaction points), its material file.
        private sealed class Placed
        {
            public string Path = string.Empty;
            public IvoModel Model;
            public double[] Matrix = Mat4.Identity();
            public string Material = string.Empty;
            public string Class = string.Empty;
            public Dictionary<string, object> Item;
            public string Role = string.Empty;     // hull, interior, seat, dashboard, item
            public List<int> Meshes = new List<int>();
            public List<int> MeshObjects = new List<int>();   // the mesh object of each scene mesh
        }

        private const double Reach = 4.0;      // what is drawn around the pilot (metres from the seat)

        private static void BuildOne(Context cx, Ship ship, string folder, string version, SetResult r)
        {
            Job job = cx.Job;
            LoadoutEntry seatEntry, dashEntry;
            PilotParts(cx, ship, out seatEntry, out dashEntry);
            lock (cx.Unread)
                cx.Unread.Clear();
            if (ship.LoadoutFile.Length > 0)
            {
                string lf = StarCitizenP4k.Normalize(ship.LoadoutFile);
                if (cx.P4k.IsEncrypted(lf))
                    cx.Encrypted.Add(lf);
                r.Notes.Add("the default loadout is in " + Path.GetFileName(ship.LoadoutFile) + " (encrypted, not read)");
            }
            if (seatEntry == null)
            {
                r.Error = "no pilot seat in the default loadout";
                return;
            }
            string hullPath;
            IvoModel hull = Hull(cx, ship, seatEntry.Port, out hullPath);
            if (hull == null)
            {
                r.Error = "the hull model with the pilot seat's hardpoint " + seatEntry.Port + " was not found";
                return;
            }
            // The pilot's frame: the seat hardpoint.
            double[] seatWorld = Hardpoint(hull, seatEntry.Port).World;
            Vec3 seatAt = Mat4.Point(seatWorld, 0, 0, 0);
            List<Placed> placed = new List<Placed>();
            placed.Add(new Placed { Path = hullPath, Model = hull, Matrix = Mat4.Identity(), Material = hull.Material, Role = "hull" });
            // The items of the loadout near the seat (and their own items).
            Action<List<LoadoutEntry>, Placed, int> walk = null;
            walk = (entries, parent, depth) =>
            {
                foreach (LoadoutEntry e in entries)
                {
                    double[] port = PortMatrix(cx, parent, e.Port);
                    if (port == null)
                        continue;
                    Vec3 at = Mat4.Point(port, 0, 0, 0);
                    if ((at - seatAt).Length > Reach + 0.5)
                        continue;
                    Dictionary<string, object> item = Item(cx, e.Class);
                    string geo = item == null ? string.Empty : GeometryOf(item);
                    Placed p = new Placed { Path = geo, Matrix = port, Class = e.Class, Item = item, Role = e == seatEntry ? "seat" : e == dashEntry ? "dashboard" : "item" };
                    if (geo.Length > 0)
                    {
                        p.Model = Model(cx, geo);
                        if (p.Model != null)
                        {
                            p.Material = p.Model.Material;
                            placed.Add(p);
                        }
                    }
                    if (depth < 4 && p.Model != null)
                        walk(e.Children, p, depth + 1);
                }
            };
            walk(ship.Loadout, placed[0], 0);
            Placed seat = placed.FirstOrDefault(p => p.Role == "seat");
            Placed dash = placed.FirstOrDefault(p => p.Role == "dashboard");
            if (seat == null)
                r.Notes.Add("the pilot seat's model could not be read");
            // The cockpit interior of the object containers.
            int interiorModels = 0, brushCount = 0;
            foreach (Container oc in ship.Containers)
                interiorModels += Interior(cx, oc, seatAt, placed, ref brushCount);
            // The eye: above the seat's sitting point (the actor attachment bone), a seated pilot's eye height.
            string sitBone = seat == null ? string.Empty : DataForge.Str(DataForge.Obj(DataForge.Component(seat.Item, "SCItemSeatParams"), "actorAttachment"), "boneName");
            double[] sit = seatWorld;
            if (seat != null)
            {
                IvoModel.Node sb = sitBone.Length > 0 ? seat.Model.Find(sitBone) : null;
                if (sb == null)
                    sb = seat.Model.Nodes.FirstOrDefault(n => Regex.IsMatch(n.Name, "sit_?pos|seat_?pos|pilot_pos", RegexOptions.IgnoreCase));
                if (sb != null)
                    sit = Mat4.Multiply(seat.Matrix, sb.World);
                else
                    r.Notes.Add("the seat has no sitting point; the eye is placed above the seat");
            }
            // Everything into the pilot's frame (seat origin, seat forward = +x of the scene).
            double[] toPilot = InverseRigid(seatWorld);
            // The pilot looks at the dashboard: a seat frame facing away from it (a bike's seat) is turned round.
            // The grips (stick, throttle) tell best where the pilot faces; else the dashboard, else the ship's nose.
            List<Vec3> gripsAt = placed.Where(x => x.Role != "hull" && x.Role != "interior" && x.Model != null)
                .SelectMany(x => GripNodes(x.Model).Select(g => Mat4.Point(Mat4.Multiply(Mat4.Multiply(toPilot, x.Matrix), g.World), 0, 0, 0)))
                .Where(v => v.Length < 2.5).ToList();
            double sitY = Mat4.Point(Mat4.Multiply(toPilot, sit), 0, 0, 0).Y;
            Vec3 ahead = dash != null ? Mat4.Point(Mat4.Multiply(toPilot, dash.Matrix), 0, 0, 0) : Mat4.Direction(toPilot, 0, 1, 0);
            bool turn = gripsAt.Count > 0 ? gripsAt.Average(v => v.Y) < sitY - 0.05 : ahead.Y < -0.05;
            if (turn)
                toPilot = Mat4.Multiply(Mat4.Rotation(new[] { 0.0, 0, 0, 1 }), toPilot);
            Vec3 sitLocal = Mat4.Point(Mat4.Multiply(toPilot, sit), 0, 0, 0);
            Vec3 eyeCry = new Vec3(sitLocal.X, sitLocal.Y + 0.06, sitLocal.Z + 0.74);
            Vec3 head = ToScene(eyeCry);
            CockpitScene scene = new CockpitScene();
            Dictionary<string, int> materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int part = 0, missingTextures = 0;
            string onlyRoles = Environment.GetEnvironmentVariable("JOYPRO_SC_DEBUG_ROLES") ?? string.Empty;
            foreach (Placed p in placed)
            {
                if (p.Model == null || p.Model.Positions == null || p.Model.Indices == null)
                    continue;
                if (onlyRoles.Length > 0 && onlyRoles.IndexOf(p.Role, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                List<StarCitizenMaterial> mats = Materials(cx, p.Material);
                double[] toScene = Mat4.Multiply(toPilot, p.Matrix);
                // The hull: only what is around the pilot (the rest of the ship is not drawn).
                double reach = p.Role == "hull" || p.Role == "interior" ? 3.2 : Reach;
                foreach (IvoModel.Subset s in p.Model.Subsets)
                {
                    StarCitizenMaterial mat = s.Material < mats.Count ? mats[s.Material] : null;
                    if (mat != null && mat.Kind == "skip")
                        continue;
                    if (s.IndexCount < 3)
                        continue;
                    double[] m = Mat4.Multiply(toScene, p.Model.ObjectMatrix(s.Object));
                    SceneMesh sm = MeshOf(p.Model, s, m, eyeCry, reach);
                    if (sm == null)
                        continue;
                    string mkey = p.Material + "#" + s.Material.ToString(CultureInfo.InvariantCulture);
                    int mi;
                    if (!materialIndex.TryGetValue(mkey, out mi))
                    {
                        bool missing;
                        SceneMaterial scm = SceneMaterialOf(cx, mat, out missing);
                        if (missing)
                            missingTextures++;
                        mi = scene.Materials.Count;
                        scene.Materials.Add(scm);
                        materialIndex[mkey] = mi;
                    }
                    sm.Material = mi;
                    IvoModel.Node node = p.Model.NodeOfObject(s.Object);
                    sm.Name = (p.Class.Length > 0 ? p.Class : Path.GetFileNameWithoutExtension(p.Path)) + "/" + (node == null ? s.Object.ToString(CultureInfo.InvariantCulture) : node.Name);
                    sm.Part = ++part;
                    sm.Node = placed.IndexOf(p);
                    p.Meshes.Add(scene.Meshes.Count);
                    p.MeshObjects.Add(s.Object);
                    scene.Meshes.Add(sm);
                }
            }
            if (scene.Meshes.Count == 0)
            {
                r.Error = "no cockpit geometry could be read";
                return;
            }
            Func<Placed, IvoModel.Node, Vec3> nodeAt = (p, n) => ToScene(Mat4.Point(Mat4.Multiply(Mat4.Multiply(toPilot, p.Matrix), n.World), 0, 0, 0));
            // Grips: the stick and the throttle of the dashboard, the seat and the other cockpit items.
            List<CockpitGrip> grips = new List<CockpitGrip>();
            foreach (Placed p in placed.Where(x => x.Role != "hull" && x.Role != "interior"))
            {
                foreach (IvoModel.Node g in GripNodes(p.Model))
                {
                    List<int> meshes = new List<int>();
                    for (int k = 0; k < p.Meshes.Count; k++)
                    {
                        IvoModel.Node owner = p.Model.NodeOfObject(p.MeshObjects[k]);
                        if (owner != null && p.Model.IsUnder(owner, g))
                            meshes.Add(p.Meshes[k]);
                    }
                    if (meshes.Count == 0)
                        continue;
                    Vec3 mn, mx;
                    scene.Bounds(meshes, out mn, out mx);
                    double size = (mx - mn).Length;
                    Vec3 centre = (mn + mx) * 0.5;
                    if (size < 0.04 || size > 1.2 || (centre - head).Length > 2.0)
                        continue;
                    string device = Regex.IsMatch(g.Name, "throttle|thrust", RegexOptions.IgnoreCase) ? "throttle" : Regex.IsMatch(g.Name, "yoke", RegexOptions.IgnoreCase) ? "yoke" : "stick";
                    if (grips.Any(x => x.Meshes.Intersect(meshes).Any()))
                        continue;
                    CockpitGrip cg = new CockpitGrip { Device = device, Meshes = meshes, Pivot = centre, Animation = new ControlAnimation { Source = "model node", Name = g.Name } };
                    foreach (CockpitCallout c in GripParts(p, g, nodeAt))
                        cg.Parts.Add(c);
                    grips.Add(cg);
                }
            }
            // Each grip lists the game's flight movement actions (defaultProfile.xml): any of them binds to any button.
            if (cx.FlightActions.Count > 0)
            {
                foreach (CockpitGrip g in grips)
                {
                    CockpitCallout gripPart = new CockpitCallout
                    {
                        Id = "grip",
                        Label = CockpitViews.Title(g.Device) + " grip: the game's flight controls (bind them to any button)",
                        Position = Highest(scene, g.Meshes),
                        Kind = "part"
                    };
                    gripPart.Commands.AddRange(cx.FlightActions.Select(a => new CalloutCommand { Name = a.Name, Category = a.Category, Id = a.Id }));
                    g.Parts.Add(gripPart);
                }
            }
            // The eye behind and above the grips (a seat's sitting point can be far forward: a prone pilot).
            head = EyeBehindGrips(head, grips);
            // Without grips: not further than 1.1 m behind the dashboard's middle.
            if (grips.Count == 0 && dash != null && dash.Meshes.Count > 0)
            {
                Vec3 dmn, dmx;
                scene.Bounds(dash.Meshes, out dmn, out dmx);
                Vec3 dc = (dmn + dmx) * 0.5;
                if (head.X < dc.X - 1.1)
                    head.X = dc.X - 0.9;
            }
            string debugObj = Environment.GetEnvironmentVariable("JOYPRO_SC_DEBUG_OBJ") ?? string.Empty;
            if (debugObj.Length > 0)
                WriteObj(Path.Combine(debugObj, ship.Class + ".obj"), scene, head, grips);
            // The callouts: the items' interaction points (the game's own texts).
            List<CockpitCallout> callouts = new List<CockpitCallout>();
            HashSet<int> gripMeshes = new HashSet<int>(grips.SelectMany(g => g.Meshes));
            foreach (Placed p in placed.Where(x => x.Item != null))
                callouts.AddRange(InteractionCallouts(cx, p, nodeAt, head));
            // The screens of the dashboard (and its screen items).
            foreach (Placed p in placed.Where(x => x.Role == "dashboard" || (x.Role == "item" && Regex.IsMatch(x.Class, "screen|display|mfd", RegexOptions.IgnoreCase))))
            {
                foreach (IvoModel.Node n in p.Model.Nodes.Where(n => Regex.IsMatch(n.Name, "^(screen|ui_screen|screen_mfd|mfd)", RegexOptions.IgnoreCase) && !Regex.IsMatch(n.Name, "glass|frame|bezel|button|btn|_ip_|^\\$", RegexOptions.IgnoreCase)))
                {
                    Vec3 at = nodeAt(p, n);
                    string label = ScreenLabel(n.Name);
                    // One callout per screen (a screen has several nodes: the surface, the display, the helper).
                    if ((at - head).Length > 2.5 || callouts.Any(c => (c.Position - at).Length < (c.Kind == "display" ? 0.15 : 0.03) || (c.Kind == "display" && c.Label == label)))
                        continue;
                    callouts.Add(new CockpitCallout { Id = "screen:" + n.Name, Label = label, Position = at, Kind = "display" });
                }
            }
            foreach (CockpitCallout c in callouts)
            {
                string area = CockpitViews.AreaOf(c.Position, head);
                c.Panel = area;
                c.PanelTitle = AreaTitle(area);
            }
            // Interaction points behind the pilot's head (the seat's own, cabin doors) are not cockpit controls.
            callouts.RemoveAll(c => c.Panel == "rear");
            // The screens are named on the overview (a picture of a dark screen alone says nothing); the buttons and
            // switches get panel pictures.
            List<CockpitCallout> screens = callouts.Where(c => c.Kind == "display").ToList();
            List<CockpitViewPlan> views = CockpitViews.Plan(scene, head, callouts.Where(c => c.Kind != "display").ToList(), grips, job.Width);
            CockpitViewPlan overview = views.FirstOrDefault(v => v.Kind == "overview");
            // The pilot's own seat is left out of the overview (its back and head rest would hide the cockpit).
            // (of a seat that is the dashboard as well only its parts beside and behind the eye: back, head rest, cushion)
            HashSet<int> seatMeshes = new HashSet<int>();
            foreach (Placed p in placed.Where(x => x.Role == "seat"))
            {
                bool seatDash = DataForge.Component(p.Item, "SCItemSeatDashboardParams") != null;
                foreach (int m in p.Meshes.Where(m => !grips.Any(g => g.Meshes.Contains(m))))
                {
                    Vec3 mn, mx;
                    scene.Bounds(new[] { m }, out mn, out mx);
                    if (!seatDash || (mn.X + mx.X) / 2 < head.X + 0.2)
                        seatMeshes.Add(m);
                }
            }
            if (overview != null && seatMeshes.Count > 0)
                overview.Meshes = Enumerable.Range(0, scene.Meshes.Count).Where(m => !seatMeshes.Contains(m)).ToList();
            // What lies right in front of the camera (the back of a seat that is the dashboard too) is cut away; the
            // camera looks ahead, not steeply down at the floor between the grips.
            if (overview != null)
            {
                RenderCamera cam = overview.Camera;
                cam.Near = Math.Max(cam.Near, 0.5);
                if (cam.Forward.Y < -0.6)
                {
                    Vec3 flat = new Vec3(cam.Forward.X, 0, cam.Forward.Z).Normalized;
                    if (flat.Length > 0.5)
                    {
                        cam.Forward = (flat * 0.8 + new Vec3(0, -0.6, 0)).Normalized;
                        cam.Right = Vec3.Cross(cam.Forward, new Vec3(0, 1, 0)).Normalized;
                        cam.Up = Vec3.Cross(cam.Right, cam.Forward).Normalized;
                    }
                }
            }
            // Framed to what the pilot works with: the grips and the dashboard (its meshes in front of the eye), not
            // to every interaction point of the cabin.
            if (overview != null)
            {
                List<Vec3> frame = grips.Select(g => g.Pivot).ToList();
                if (dash != null && dash.Meshes.Count > 0)
                {
                    Vec3 mn, mx;
                    scene.Bounds(dash.Meshes, out mn, out mx);
                    mn = Vec3.Max(mn, head + new Vec3(-0.2, -1.2, -1.4));
                    mx = Vec3.Min(mx, head + new Vec3(2.0, 0.8, 1.4));
                    if (mn.X < mx.X && mn.Y < mx.Y && mn.Z < mx.Z)
                    {
                        frame.Add(mn);
                        frame.Add(mx);
                        frame.Add(new Vec3(mn.X, mx.Y, mn.Z));
                        frame.Add(new Vec3(mx.X, mn.Y, mx.Z));
                    }
                }
                if (frame.Count >= 2 && overview.Camera.FovY > 72)
                    NuclearOptionCockpitPictures.Refit(overview.Camera, frame, head);
            }
            if (overview != null && screens.Count > 0)
            {
                foreach (CockpitCallout s in screens)
                {
                    overview.Callouts.Add(new CockpitCallout { Id = s.Id, Label = s.Label, Position = s.Position, Kind = s.Kind, N = overview.Callouts.Count + 1 });
                    overview.Links.Add(string.Empty);
                }
            }
            if (job.ViewFilter.Length > 0)
                views = views.Where(v => v.Id.IndexOf(job.ViewFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            // Notes: what the set is made from and what could not be read.
            if (dash == null)
                r.Notes.Add(dashEntry == null ? "no dashboard in the default loadout" : "the dashboard's model could not be read");
            if (grips.Count == 0)
                r.Notes.Add("no stick or throttle found by part name");
            if (interiorModels == 0 && ship.Containers.Count > 0)
                r.Notes.Add("no interior models in the object containers near the seat");
            if (brushCount > 0)
                r.Notes.Add(brushCount.ToString(CultureInfo.InvariantCulture) + " brush models of the object containers (cockpit walls of bigger ships) are not drawn: their placement is not readable");
            if (ship.Definition.Length > 0 && cx.P4k.IsEncrypted(ship.Definition))
            {
                cx.Encrypted.Add(StarCitizenP4k.Normalize(ship.Definition));
                r.Notes.Add("the vehicle definition " + Path.GetFileName(ship.Definition) + " is encrypted (not read): the hull model is found by its name");
            }
            if (missingTextures > 0)
                r.Notes.Add(missingTextures.ToString(CultureInfo.InvariantCulture) + " materials without a readable texture (drawn in their colour)");
            List<string> unread;
            lock (cx.Unread)
                unread = cx.Unread.ToList();
            if (unread.Count > 0)
                r.Notes.Add("not read: " + string.Join(", ", unread.Take(6)) + (unread.Count > 6 ? " ..." : string.Empty));
            PictureWriter.SetInfo info = new PictureWriter.SetInfo
            {
                Game = "StarCitizen",
                Plane = r.Plane,
                Planes = r.Planes,
                Module = ship.Class,
                Source = "Data.p4k: " + Path.GetFileName(hullPath) + (dash != null ? ", " + Path.GetFileName(dash.Path) : string.Empty) + (seat != null ? ", " + Path.GetFileName(seat.Path) : string.Empty),
                GameVersion = version,
                Notes = r.Notes,
                // Interaction points on bare walls make poor panel pictures: only clear ones stay (the overview and the
                // grips carry the ship).
                PanelKeep = 70
            };
            PictureWriter.Write(scene, views, folder, info, (cur, total, title) =>
            {
                if (job.Progress != null)
                    job.Progress(cur, total, "Star Citizen, " + r.Plane + " pictures", title);
            }, job.ViewFilter.Length == 0);
            r.Views = views.Count;
            r.Callouts = screens.Count + views.Sum(v => v.Kind == "overview" ? 0 : v.Callouts.Count);
        }

        private static Vec3 Highest(CockpitScene scene, List<int> meshes)
        {
            Vec3 best = new Vec3(0, double.MinValue, 0);
            foreach (int m in meshes)
            {
                float[] p = scene.Meshes[m].Positions;
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    if (p[i + 1] > best.Y)
                        best = new Vec3(p[i], p[i + 1], p[i + 2]);
                }
            }
            return best;
        }

        // The game's flight movement actions of defaultProfile.xml (actionmap spaceship_movement): the action name (as
        // in actionmaps.xml), its English text and the map's text. Mouse-only actions are left out.
        public static List<CalloutCommand> FlightActions(CryXmlNode profile, StarCitizenTexts texts)
        {
            List<CalloutCommand> list = new List<CalloutCommand>();
            if (profile == null)
                return list;
            foreach (CryXmlNode map in profile.Descendants("actionmap").Where(m => m.Get("name") == "spaceship_movement"))
            {
                string category = texts.Text(map.Get("UILabel"));
                if (category.Length == 0)
                    category = "Flight movement";
                foreach (CryXmlNode a in map.Children.Where(c => string.Equals(c.Tag, "action", StringComparison.OrdinalIgnoreCase)))
                {
                    string id = a.Get("name");
                    if (id.Length == 0 || id.EndsWith("_mouse", StringComparison.OrdinalIgnoreCase) || (a.Get("mouse").Length > 0 && a.Get("joystick").Length == 0 && a.Get("keyboard").Length == 0))
                        continue;
                    string label = texts.Text(a.Get("UILabel"));
                    list.Add(new CalloutCommand { Id = id, Name = label.Length > 0 ? label : Humanize(id.StartsWith("v_", StringComparison.Ordinal) ? id.Substring(2) : id), Category = category });
                }
            }
            return list;
        }

        // The eye: behind a stick or throttle by at least 0.3 m and above it by at least 0.28 m (scene axes).
        public static Vec3 EyeBehindGrips(Vec3 head, List<CockpitGrip> grips)
        {
            if (grips.Count == 0)
                return head;
            CockpitGrip g = grips.OrderBy(x => x.Device == "throttle" ? 1 : 0).ThenBy(x => (x.Pivot - head).Length).First();
            Vec3 h = head;
            if (h.X > g.Pivot.X - 0.3)
                h.X = g.Pivot.X - 0.3;
            if (h.Y < g.Pivot.Y + 0.28)
                h.Y = g.Pivot.Y + 0.28;
            // Not much further from a stick or yoke than an arm reaches.
            if (g.Device != "throttle")
            {
                // (a stick on the seat's arm rest is beside the pilot: the eye is right behind it)
                bool beside = Math.Abs(g.Pivot.Z - head.Z) > 0.15;
                if (h.X < g.Pivot.X - (beside ? 0.5 : 0.75))
                    h.X = g.Pivot.X - (beside ? 0.4 : 0.65);
                if (h.Y > g.Pivot.Y + 0.8)
                    h.Y = g.Pivot.Y + 0.7;
            }
            return h;
        }

        // A debugging aid: the scene as a Wavefront OBJ (one group per mesh), the head and grips as comments.
        private static void WriteObj(string file, CockpitScene scene, Vec3 head, List<CockpitGrip> grips)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (StreamWriter w = new StreamWriter(file))
            {
                w.WriteLine("# head " + head.X.ToString(CultureInfo.InvariantCulture) + " " + head.Y.ToString(CultureInfo.InvariantCulture) + " " + head.Z.ToString(CultureInfo.InvariantCulture));
                foreach (CockpitGrip g in grips)
                    w.WriteLine("# grip " + g.Device + " " + g.Pivot);
                int baseIndex = 1;
                foreach (SceneMesh m in scene.Meshes)
                {
                    SceneMaterial mat = m.Material >= 0 && m.Material < scene.Materials.Count ? scene.Materials[m.Material] : null;
                    byte[] c = mat == null ? new byte[] { 128, 128, 128 } : mat.Color;
                    w.WriteLine("g " + m.Name.Replace(' ', '_') + " " + c[0] + " " + c[1] + " " + c[2]);
                    for (int i = 0; i + 2 < m.Positions.Length; i += 3)
                        w.WriteLine("v " + m.Positions[i].ToString("0.####", CultureInfo.InvariantCulture) + " " + m.Positions[i + 1].ToString("0.####", CultureInfo.InvariantCulture) + " " + m.Positions[i + 2].ToString("0.####", CultureInfo.InvariantCulture));
                    for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                        w.WriteLine("f " + (m.Indices[t] + baseIndex) + " " + (m.Indices[t + 1] + baseIndex) + " " + (m.Indices[t + 2] + baseIndex));
                    baseIndex += m.Positions.Length / 3;
                }
            }
        }

        // CryEngine (x right, y forward, z up) to the scene (x forward, y up, z right).
        public static Vec3 ToScene(Vec3 c)
        {
            return new Vec3(c.Y, c.Z, c.X);
        }

        private static double[] InverseRigid(double[] m)
        {
            double[] r = Mat4.Identity();
            for (int c = 0; c < 3; c++)
                for (int row = 0; row < 3; row++)
                    r[c * 4 + row] = m[row * 4 + c];
            Vec3 t = new Vec3(m[12], m[13], m[14]);
            Vec3 it = Mat4.Direction(r, t.X, t.Y, t.Z);
            r[12] = -it.X;
            r[13] = -it.Y;
            r[14] = -it.Z;
            return r;
        }

        // A hardpoint of the hull: the bone of the port's name, else (for the pilot's seat and dashboard named by
        // convention) a bone like it ("hardpoint_seat_pilot" -> "hardpoint_pilot_seat", "hardpoint_seat_pilot_01").
        public static IvoModel.Node Hardpoint(IvoModel hull, string port)
        {
            IvoModel.Node n = hull.Find(port);
            if (n != null)
                return n;
            string kind = port.IndexOf("dashboard", StringComparison.OrdinalIgnoreCase) >= 0 ? "dashboard" : port.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0 ? "seat" : string.Empty;
            if (kind.Length == 0 || port.IndexOf("pilot", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            List<IvoModel.Node> like = hull.Nodes.Where(x => x.Name.StartsWith("hardpoint", StringComparison.OrdinalIgnoreCase) && x.Name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                             !Regex.IsMatch(x.Name, "co_?pilot|access|turret|gunner|passenger|engineer|crane|tractor", RegexOptions.IgnoreCase)).ToList();
            return like.Where(x => x.Name.IndexOf("pilot", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.Name.Length).FirstOrDefault()
                   ?? (like.Count == 1 ? like[0] : null);
        }

        // A port of a placed item (or of the hull): the item's helper bone of that name (the port definition's helper).
        private static double[] PortMatrix(Context cx, Placed parent, string port)
        {
            if (parent.Model == null)
                return null;
            IvoModel.Node n = parent.Role == "hull" ? Hardpoint(parent.Model, port) : parent.Model.Find(port);
            if (n == null && parent.Item != null)
            {
                foreach (object pd in DataForge.List(DataForge.Component(parent.Item, "SItemPortContainerComponentParams"), "Ports"))
                {
                    if (!string.Equals(DataForge.Str(pd, "Name"), port, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string helper = DataForge.Str(DataForge.Obj(DataForge.Obj(DataForge.Obj(pd, "AttachmentImplementation"), "Helper"), "Helper"), "Name");
                    if (helper.Length > 0)
                        n = parent.Model.Find(helper);
                }
            }
            return n == null ? null : Mat4.Multiply(parent.Matrix, n.World);
        }

        // The object container's models near the seat: its GeomEntity entities and item entities with a geometry.
        private static int Interior(Context cx, Container oc, Vec3 seatAt, List<Placed> placed, ref int brushCount)
        {
            string file = StarCitizenP4k.Normalize(oc.File);
            byte[] pak;
            try
            {
                pak = cx.P4k.Read(file);
            }
            catch (StarCitizenP4k.EncryptedEntryException)
            {
                cx.Encrypted.Add(file);
                return 0;
            }
            if (pak == null)
                return 0;
            Dictionary<string, byte[]> files;
            try
            {
                files = StarCitizenP4k.ReadZip(pak, cx.P4k.Zstd);
            }
            catch (InvalidDataException)
            {
                return 0;
            }
            int n = 0;
            foreach (KeyValuePair<string, byte[]> f in files.Where(x => x.Key.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)))
            {
                // (The container's brush models, SocBrushModels, are not drawn: their placement is not in the file.)
                brushCount += SocBrushModels(f.Value).Count;
                foreach (CryXmlNode entities in SocEntities(f.Value))
                {
                    foreach (CryXmlNode e in entities.Children.Where(x => string.Equals(x.Tag, "Entity", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (e.Get("HiddenInGame") == "1")
                            continue;
                        string geo = string.Empty;
                        Dictionary<string, object> item = null;
                        CryXmlNode res = e.Descendants("Geometry").FirstOrDefault(g => g.Get("path").Length > 0);
                        if (res != null)
                            geo = res.Get("path");
                        string cls = e.Get("EntityClass");
                        if (geo.Length == 0 && cls.Length > 0 && cls != "GeomEntity")
                        {
                            item = Item(cx, cls);
                            if (item != null)
                                geo = GeometryOf(item);
                        }
                        if (geo.Length == 0 || !Regex.IsMatch(geo, "\\.(cga|cgf)$", RegexOptions.IgnoreCase))
                            continue;
                        double[] local = EntityMatrix(e);
                        double[] m = Mat4.Multiply(oc.Matrix, local);
                        Vec3 at = Mat4.Point(m, 0, 0, 0);
                        double radius = 0;
                        double.TryParse(e.Get("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                        if ((at - seatAt).Length - radius > 3.2)
                            continue;
                        IvoModel model = Model(cx, geo);
                        if (model == null)
                            continue;
                        string mtl = e.Get("Material");
                        placed.Add(new Placed { Path = geo, Model = model, Matrix = m, Material = mtl.Length > 0 ? mtl : model.Material, Class = cls == "GeomEntity" ? string.Empty : cls, Item = item, Role = item != null ? "item" : "interior" });
                        n++;
                    }
                }
            }
            return n;
        }

        // The brush model names of a .soc (chunk 0x0010: a count at byte 4, then 256-byte names).
        public static List<string> SocBrushModels(byte[] soc)
        {
            List<string> list = new List<string>();
            if (soc == null || soc.Length < 16 || soc[0] != 'C' || soc[1] != 'r' || soc[2] != 'C' || soc[3] != 'h')
                return list;
            int n = BitConverter.ToInt32(soc, 8), table = BitConverter.ToInt32(soc, 12);
            for (int i = 0; i < n && table + 16 * (i + 1) <= soc.Length; i++)
            {
                int p = table + 16 * i;
                int type = BitConverter.ToUInt16(soc, p), size = BitConverter.ToInt32(soc, p + 8), off = BitConverter.ToInt32(soc, p + 12);
                if (type != 0x0010 || off < 0 || size < 8 || off + size > soc.Length)
                    continue;
                int count = BitConverter.ToInt32(soc, off + 4);
                for (int k = 0; k < count && off + 8 + 256 * (k + 1) <= off + size; k++)
                {
                    int s = off + 8 + 256 * k, e = s;
                    while (e < s + 256 && soc[e] != 0)
                        e++;
                    string name = System.Text.Encoding.UTF8.GetString(soc, s, e - s);
                    if (Regex.IsMatch(name, "\\.(cgf|cga)$", RegexOptions.IgnoreCase))
                        list.Add(name);
                }
            }
            return list;
        }

        // The entity lists (CryXmlB "Entities") of an object container's .soc (a CryEngine chunk file).
        public static List<CryXmlNode> SocEntities(byte[] soc)
        {
            List<CryXmlNode> list = new List<CryXmlNode>();
            if (soc == null || soc.Length < 16)
                return list;
            byte[] magic = System.Text.Encoding.ASCII.GetBytes("CryXmlB");
            for (int i = 0; i + 44 <= soc.Length; i++)
            {
                if (soc[i] != 'C' || soc[i + 1] != 'r' || soc[i + 2] != 'y' || soc[i + 3] != 'X')
                    continue;
                bool same = true;
                for (int k = 0; k < magic.Length && same; k++)
                    same = soc[i + k] == magic[k];
                if (!same)
                    continue;
                try
                {
                    CryXmlNode n = CryXml.Parse(soc, i);
                    if (n != null && string.Equals(n.Tag, "Entities", StringComparison.OrdinalIgnoreCase))
                        list.Add(n);
                    i += 44;
                }
                catch (Exception)
                {
                }
            }
            return list;
        }

        // An entity's Pos, Rotate (w, x, y, z) and Scale.
        private static double[] EntityMatrix(CryXmlNode e)
        {
            double[] p = CryXml.Numbers(e.Get("Pos"));
            double[] q = CryXml.Numbers(e.Get("Rotate"));
            double[] s = CryXml.Numbers(e.Get("Scale"));
            double[] m = Mat4.Translation(p.Length > 0 ? p[0] : 0, p.Length > 1 ? p[1] : 0, p.Length > 2 ? p[2] : 0);
            if (q.Length == 4)
                m = Mat4.Multiply(m, Mat4.Rotation(new[] { q[0], q[1], q[2], q[3] }));
            if (s.Length == 3 && (Math.Abs(s[0] - 1) > 1e-6 || Math.Abs(s[1] - 1) > 1e-6 || Math.Abs(s[2] - 1) > 1e-6))
                m = Mat4.Multiply(m, Mat4.Scale(s[0], s[1], s[2]));
            return m;
        }

        // One sub-mesh in the scene: its triangles near the eye (the rest of a hull is not drawn), smooth normals from
        // the triangles.
        private static SceneMesh MeshOf(IvoModel model, IvoModel.Subset s, double[] m, Vec3 eyeCry, double reach)
        {
            float[] pos = model.Positions;
            float[] uvs = model.Uvs;
            int[] idx = model.Indices;
            Dictionary<int, int> remap = new Dictionary<int, int>();
            List<float> p3 = new List<float>(), uv = new List<float>();
            List<int> tris = new List<int>();
            Vec3[] cache = new Vec3[3];
            int[] src = new int[3];
            for (int t = s.FirstIndex; t + 2 < s.FirstIndex + s.IndexCount; t += 3)
            {
                bool near = false;
                for (int k = 0; k < 3; k++)
                {
                    int v = idx[t + k];
                    src[k] = v;
                    // (the pilot's frame in CryEngine axes, then the scene's axes)
                    Vec3 w = Mat4.Point(m, pos[3 * v], pos[3 * v + 1], pos[3 * v + 2]);
                    if ((w - eyeCry).Length < reach)
                        near = true;
                    cache[k] = ToScene(w);
                }
                if (!near)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int mapped;
                    if (!remap.TryGetValue(src[k], out mapped))
                    {
                        mapped = remap.Count;
                        remap[src[k]] = mapped;
                        p3.Add((float)cache[k].X);
                        p3.Add((float)cache[k].Y);
                        p3.Add((float)cache[k].Z);
                        if (uvs != null)
                        {
                            uv.Add(uvs[2 * src[k]]);
                            uv.Add(uvs[2 * src[k] + 1]);
                        }
                    }
                    tris.Add(mapped);
                }
            }
            if (tris.Count < 3)
                return null;
            SceneMesh sm = new SceneMesh { Positions = p3.ToArray(), Uvs = uvs == null ? null : uv.ToArray(), Indices = tris.ToArray() };
            sm.Normals = SmoothNormals(sm.Positions, sm.Indices);
            return sm;
        }

        // Per-vertex normals: the area-weighted mean of the triangles' normals (the game splits its vertices at hard
        // edges); their side does not matter (the renderer shades the side facing the camera).
        public static float[] SmoothNormals(float[] p, int[] idx)
        {
            float[] n = new float[p.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                double ux = p[3 * b] - p[3 * a], uy = p[3 * b + 1] - p[3 * a + 1], uz = p[3 * b + 2] - p[3 * a + 2];
                double vx = p[3 * c] - p[3 * a], vy = p[3 * c + 1] - p[3 * a + 1], vz = p[3 * c + 2] - p[3 * a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int v in new[] { a, b, c })
                {
                    n[3 * v] += (float)nx;
                    n[3 * v + 1] += (float)ny;
                    n[3 * v + 2] += (float)nz;
                }
            }
            for (int v = 0; v + 2 < n.Length; v += 3)
            {
                double l = Math.Sqrt(n[v] * n[v] + n[v + 1] * n[v + 1] + n[v + 2] * n[v + 2]);
                if (l > 1e-20)
                {
                    n[v] = (float)(n[v] / l);
                    n[v + 1] = (float)(n[v + 1] / l);
                    n[v + 2] = (float)(n[v + 2] / l);
                }
                else
                {
                    n[v + 1] = 1;
                }
            }
            return n;
        }

        // ------------------------------------------------------------------ materials

        private static List<StarCitizenMaterial> Materials(Context cx, string mtl)
        {
            List<StarCitizenMaterial> list;
            if (string.IsNullOrEmpty(mtl))
                return new List<StarCitizenMaterial>();
            if (cx.Mtls.TryGetValue(mtl, out list))
                return list;
            list = new List<StarCitizenMaterial>();
            string path = StarCitizenP4k.Normalize(mtl.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase) ? mtl : mtl + ".mtl");
            try
            {
                byte[] d = cx.P4k.Read(path);
                if (d != null)
                    list = StarCitizenMaterial.Parse(CryXml.Parse(d));
            }
            catch (StarCitizenP4k.EncryptedEntryException ex)
            {
                cx.Encrypted.Add(ex.Entry);
            }
            catch (Exception)
            {
            }
            cx.Mtls[mtl] = list;
            return list;
        }

        private static readonly byte[] ScreenColour = { 22, 30, 38 };

        private static SceneMaterial SceneMaterialOf(Context cx, StarCitizenMaterial mat, out bool missing)
        {
            missing = false;
            SceneMaterial sm = new SceneMaterial { Name = mat == null ? string.Empty : mat.Name };
            if (mat == null)
            {
                sm.Color = new byte[] { 120, 122, 126 };
                return sm;
            }
            if (mat.Kind == "screen")
            {
                sm.Color = (byte[])ScreenColour.Clone();
                sm.SelfIllumination = true;
                return sm;
            }
            byte[] colour = null;
            if (mat.LayerPath.Length > 0)
                colour = LayerColour(cx, mat);
            if (mat.Texture.Length > 0)
            {
                TextureImage img = Texture(cx, mat.Texture, 512);
                // A texture that is nearly all white is a mask or a tint map the game colours at run time (drawn as it
                // is, the cockpit was blown-out white): the layer's colour or a neutral grey instead.
                if (img != null && 0.299 * img.Mean[0] + 0.587 * img.Mean[1] + 0.114 * img.Mean[2] >= 225)
                {
                    sm.Color = Tame(colour ?? new byte[] { 150, 152, 156 });
                    return sm;
                }
                if (img != null)
                {
                    sm.Albedo = img;
                    sm.AlphaTest = mat.AlphaTest;
                    sm.Color = new[] { img.Mean[0], img.Mean[1], img.Mean[2] };
                    return sm;
                }
                missing = true;
            }
            if (colour == null)
            {
                colour = new[] { StarCitizenMaterial.Srgb(mat.Diffuse[0]), StarCitizenMaterial.Srgb(mat.Diffuse[1]), StarCitizenMaterial.Srgb(mat.Diffuse[2]) };
                // A white diffuse is a multiplier for a texture the file does not give (or an encrypted one): a neutral
                // grey, not the blown-out white surfaces of the first pictures.
                if (colour.All(x => x >= 230))
                    colour = new byte[] { 150, 152, 156 };
            }
            sm.Color = Tame(colour);
            return sm;
        }

        // An untextured colour at most as bright as a light grey panel (lit from the camera it was drawn pure white).
        internal static byte[] Tame(byte[] c)
        {
            double y = 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2];
            if (y <= 185)
                return c;
            double k = 185 / y;
            return new[] { (byte)(c[0] * k), (byte)(c[1] * k), (byte)(c[2] * k) };
        }

        // A layered (HardSurface) material's colour: its first layer's texture colour times the layer tint.
        private static byte[] LayerColour(Context cx, StarCitizenMaterial mat)
        {
            string key = mat.LayerPath + "|" + (mat.LayerTint == null ? "-" : string.Join(",", mat.LayerTint.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))));
            byte[] c;
            if (cx.LayerColors.TryGetValue(key, out c))
                return c;
            double[] lin = { 0.5, 0.5, 0.5 };
            List<StarCitizenMaterial> layer = Materials(cx, mat.LayerPath);
            StarCitizenMaterial lm = layer.FirstOrDefault();
            if (lm != null)
            {
                lin = (double[])lm.Diffuse.Clone();
                if (lm.Texture.Length > 0)
                {
                    TextureImage img = Texture(cx, lm.Texture, 32);
                    if (img != null)
                        lin = new[] { StarCitizenMaterial.Linear(img.Mean[0]) * lm.Diffuse[0], StarCitizenMaterial.Linear(img.Mean[1]) * lm.Diffuse[1], StarCitizenMaterial.Linear(img.Mean[2]) * lm.Diffuse[2] };
                }
            }
            if (mat.LayerTint != null)
            {
                for (int k = 0; k < 3; k++)
                    lin[k] *= mat.LayerTint[k];
            }
            c = Tame(new[] { StarCitizenMaterial.Srgb(lin[0]), StarCitizenMaterial.Srgb(lin[1]), StarCitizenMaterial.Srgb(lin[2]) });
            cx.LayerColors[key] = c;
            return c;
        }

        private static TextureImage Texture(Context cx, string path, int maxSize)
        {
            string key = path + "@" + maxSize.ToString(CultureInfo.InvariantCulture);
            TextureImage t;
            if (cx.Textures.TryGetValue(key, out t))
                return t;
            try
            {
                t = StarCitizenTextures.Load(cx.P4k, path, maxSize);
            }
            catch (StarCitizenP4k.EncryptedEntryException ex)
            {
                cx.Encrypted.Add(ex.Entry);
                t = null;
            }
            catch (Exception)
            {
                t = null;
            }
            cx.Textures[key] = t;
            return t;
        }

        // ------------------------------------------------------------------ grips and callouts

        private static readonly Regex NotAGrip = new Regex("^(btn|ip|\\$|led|ik|ui|screen|hud|light|lgt|decal|glow|helper|hardpoint|anim|socket|cam|rtt)|_ik_|ik_attach|_ip$|button|trigger|_hat|_geo$|_mesh$|_lod|proxy|shadow", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GripName = new Regex("joystick|flight_?stick|(^|_)stick(_|$)|yoke|throttle|thrust_?lever|(^|_)thruster_control", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ButtonName = new Regex("button|btn|trigger|hat|thumb|switch|pinky|ring|toggle|wheel|rocker|slider", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The grips of an item: nodes named like a stick or a throttle (the topmost of a chain of such names).
        public static List<IvoModel.Node> GripNodes(IvoModel model)
        {
            List<IvoModel.Node> list = new List<IvoModel.Node>();
            if (model == null)
                return list;
            foreach (IvoModel.Node n in model.Nodes)
            {
                if (!GripName.IsMatch(n.Name) || NotAGrip.IsMatch(n.Name))
                    continue;
                bool throttle = Regex.IsMatch(n.Name, "throttle|thrust", RegexOptions.IgnoreCase);
                // An ancestor that is a grip of the same kind already covers it.
                bool covered = false;
                int p = n.Parent, guard = 0;
                while (p >= 0 && p < model.Nodes.Count && guard++ < 256)
                {
                    IvoModel.Node a = model.Nodes[p];
                    if (GripName.IsMatch(a.Name) && !NotAGrip.IsMatch(a.Name) && Regex.IsMatch(a.Name, "throttle|thrust", RegexOptions.IgnoreCase) == throttle)
                    {
                        covered = true;
                        break;
                    }
                    p = a.Parent;
                }
                if (!covered)
                    list.Add(n);
            }
            return list;
        }

        // The buttons of a grip (its nodes named like buttons, triggers, hats): their names as labels.
        private static List<CockpitCallout> GripParts(Placed p, IvoModel.Node grip, Func<Placed, IvoModel.Node, Vec3> nodeAt)
        {
            List<CockpitCallout> parts = new List<CockpitCallout>();
            foreach (IvoModel.Node n in p.Model.Nodes)
            {
                if (n == grip || !p.Model.IsUnder(n, grip) || !ButtonName.IsMatch(n.Name) || Regex.IsMatch(n.Name, "(^|_)(ik|ip|led)(_|$)|^\\$|ik_attach|_geo$|helper", RegexOptions.IgnoreCase))
                    continue;
                Vec3 at = nodeAt(p, n);
                if (parts.Any(x => (x.Position - at).Length < 0.006))
                    continue;
                parts.Add(new CockpitCallout { Id = n.Name, Label = PartLabel(n.Name, grip.Name), Position = at, Kind = Regex.IsMatch(n.Name, "trigger", RegexOptions.IgnoreCase) ? "trigger" : Regex.IsMatch(n.Name, "hat", RegexOptions.IgnoreCase) ? "hat" : "button", Animation = new ControlAnimation { Source = "model node", Name = n.Name } });
            }
            return parts;
        }

        // "Control_Joystick_Button_02" -> "Button 2"; "Control_Joystick_trigger" -> "Trigger".
        public static string PartLabel(string name, string grip)
        {
            string s = name;
            foreach (string strip in new[] { "control_joystick", "control_throttle", "control_yoke", "joystick", "throttle", "yoke", "control", "stick" })
                s = Regex.Replace(s, "(^|_)" + strip + "(_|$)", "_", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "(^|_)btn(_|$)", "_button_", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "_0+(\\d)", "_$1");
            return Humanize(s);
        }

        public static string Humanize(string s)
        {
            string t = Regex.Replace(s ?? string.Empty, "^[\\$]+", string.Empty);
            t = Regex.Replace(t, "^(IP|btn)_", string.Empty, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "([a-z])([A-Z])", "$1 $2");
            t = Regex.Replace(t, "[_\\-]+", " ").Trim();
            t = Regex.Replace(t, "\\s+", " ");
            if (t.Length == 0)
                return s ?? string.Empty;
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        private static string ScreenLabel(string node)
        {
            string s = Regex.Replace(node, "(^|_)(ui|screen|mfd|display|geo|rtt|helper)(?=_|$)", "_", RegexOptions.IgnoreCase);
            string where = Regex.Replace(Humanize(s).ToLowerInvariant(), "\\b(display|displays|screen)\\b", string.Empty);
            where = Regex.Replace(where, "\\s+", " ").Trim();
            return where.Any(char.IsLetterOrDigit) ? "Screen, " + where : "Screen";
        }

        private static string AreaTitle(string area)
        {
            switch (area)
            {
                case "glareshield": return "Upper dashboard";
                case "front-left": return "Dashboard, left";
                case "front-center": return "Dashboard, centre";
                case "front-right": return "Dashboard, right";
                case "left-console": return "Left console";
                case "right-console": return "Right console";
                case "center-pedestal": return "Centre console";
                case "overhead": return "Overhead";
                case "left-side": return "Left side";
                case "right-side": return "Right side";
                case "rear": return "Behind the seat";
                default: return CockpitViews.Title(area);
            }
        }

        // The interaction points of an item (dashboard buttons and switches): at their helper bones, named with the
        // game's text of their interaction.
        private static List<CockpitCallout> InteractionCallouts(Context cx, Placed p, Func<Placed, IvoModel.Node, Vec3> nodeAt, Vec3 head)
        {
            List<CockpitCallout> list = new List<CockpitCallout>();
            Dictionary<string, object> ia = DataForge.Obj(DataForge.Component(p.Item, "SEntityInteractableParams"), "Interactable");
            if (ia == null)
                return list;
            Dictionary<string, string> shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (object si in DataForge.List(ia, "SharedInteractions"))
            {
                string n = DataForge.Str(si, "Name");
                if (n.Length > 0 && !shared.ContainsKey(n))
                    shared[n] = cx.Texts.Text(DataForge.Str(si, "DisplayName"));
            }
            foreach (object ip in DataForge.List(ia, "InteractionPoints"))
            {
                string helper = DataForge.Str(ip, "HelperName");
                if (helper.Length == 0)
                    continue;
                IvoModel.Node n = p.Model.Find(helper) ?? p.Model.Find(helper.TrimStart('$'));
                if (n == null)
                    continue;
                Vec3 at = nodeAt(p, n);
                if ((at - head).Length > 2.6)
                    continue;
                List<string> labels = new List<string>();
                foreach (object it in DataForge.List(ip, "Interactions"))
                {
                    string l = cx.Texts.Text(DataForge.Str(it, "DisplayName"));
                    if (l.Length == 0)
                    {
                        Dictionary<string, object> sh = DataForge.Obj(it, "Interaction");
                        l = cx.Texts.Text(DataForge.Str(sh, "DisplayName"));
                    }
                    if (l.Length > 0 && !labels.Contains(l, StringComparer.OrdinalIgnoreCase))
                        labels.Add(l);
                }
                string name = Humanize(helper);
                string sharedText;
                if (labels.Count == 0 && shared.TryGetValue(helper.TrimStart('$').Replace("IP_", string.Empty), out sharedText) && sharedText.Length > 0)
                    labels.Add(sharedText);
                if (labels.Count == 0)
                    labels.Add(name);
                string bone = DataForge.List(ip, "HighlightBehaviors").Select(h => DataForge.List(DataForge.Obj(h, "HighlightBehaviorNodeParams"), "boneNames").OfType<string>().FirstOrDefault()).FirstOrDefault(b => !string.IsNullOrEmpty(b)) ?? string.Empty;
                string kind = Regex.IsMatch(bone + " " + helper, "switch|toggle", RegexOptions.IgnoreCase) ? "switch" : Regex.IsMatch(bone + " " + helper, "lever|handle", RegexOptions.IgnoreCase) ? "lever" : Regex.IsMatch(bone + " " + helper, "knob|dial|rotary", RegexOptions.IgnoreCase) ? "knob" : "button";
                if (list.Any(c => (c.Position - at).Length < 0.01 && c.Label == labels[0]))
                    continue;
                list.Add(new CockpitCallout { Id = (p.Class.Length > 0 ? p.Class + ":" : string.Empty) + helper, Label = string.Join(" / ", labels.Take(3)), Position = at, Kind = kind, Device = p.Class, GameKind = kind, Animation = new ControlAnimation { Source = "interaction point", Name = helper } });
            }
            return list;
        }
    }
}
