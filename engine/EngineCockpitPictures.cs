using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.CockpitPictures;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // engine cockpits pictures ...: the cockpit pictures made on this PC from the user's own games (docs/cockpits.md
    // "Pictures from your games"). They are kept under <data>\GameData\CockpitPictures\<game> and never shipped.
    //   cockpits pictures build [--game DCS|XPlane12|MSFS2020|IL2Game|NuclearOption|WarThunder|StarCitizen|all] [--aircraft <name>[,<name>...]] [--force] [--out <folder>]
    //   cockpits pictures status [--out <folder>]
    internal static partial class Program
    {
        private static readonly string[] PictureGames = { "DCS", "XPlane12", "MSFS2020", "NuclearOption", "IL2Game", "WarThunder", "StarCitizen" };

        private static int CockpitsPictures(List<string> rest, string outPath, bool textOutput)
        {
            string action = rest.Count == 0 ? "status" : rest[0].ToLowerInvariant();
            List<string> args = rest.Skip(1).ToList();
            string root = PopOptionValue(args, "--out");
            // (--out is also the engine's general output option: it may arrive as outPath)
            if (string.IsNullOrWhiteSpace(root))
                root = !string.IsNullOrWhiteSpace(outPath) ? outPath : CockpitPictureSet.Root();
            switch (action)
            {
                case "build":
                    return CockpitsPicturesBuild(args, root, textOutput);
                case "status":
                case "list":
                    return CockpitsPicturesStatus(root, textOutput);
                case "check":
                    return CockpitsPicturesCheck(args, root, textOutput);
                default:
                    return Fail("Unknown cockpits pictures action: " + action + " (build, status, check)");
            }
        }

        private static int CockpitsPicturesStatus(string root, bool textOutput)
        {
            List<CockpitPictureSet.GameStatus> status = CockpitPictureSet.Status(root);
            List<string> warnings = new List<string>();
            var games = PictureGames.Select(g =>
            {
                GameInstall install = GameInstalls.Find(g, warnings);
                CockpitPictureSet.GameStatus s = status.FirstOrDefault(x => string.Equals(x.Game, g, StringComparison.OrdinalIgnoreCase));
                return new { Game = g, Name = CockpitManualPages.GameName(g), Installed = install.Installed, Sets = s == null ? 0 : s.Sets, Built = s == null ? string.Empty : s.Built };
            }).ToList();
            if (textOutput)
            {
                Console.WriteLine("Cockpit pictures in " + root);
                foreach (var g in games)
                    Console.WriteLine("  " + g.Name + ": " + (g.Installed ? "installed" : "not installed") + ", " + g.Sets.ToString(CultureInfo.InvariantCulture) + " cockpits" + (g.Built.Length > 0 ? " (built " + g.Built + ")" : string.Empty));
            }
            else
            {
                Console.WriteLine(EngineSerialization.ForConsole(EngineSerialization.ToJson(new { Root = root, Games = games })));
            }
            return 0;
        }

        private static int CockpitsPicturesBuild(List<string> args, string root, bool textOutput)
        {
            string gameOption = PopOptionValue(args, "--game") ?? "all";
            string aircraft = PopOptionValue(args, "--aircraft") ?? string.Empty;
            bool force = PopFlag(args, "--force");
            List<string> only = aircraft.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            List<string> games = string.Equals(gameOption, "all", StringComparison.OrdinalIgnoreCase)
                ? PictureGames.ToList()
                : gameOption.Split(',').Select(s => GameInstalls.Resolve(s.Trim())).Where(s => s.Length > 0).ToList();
            if (games.Count == 0 || games.Any(g => !PictureGames.Contains(g, StringComparer.OrdinalIgnoreCase)))
                return Fail("cockpits pictures build --game takes " + string.Join(", ", PictureGames) + " or all.");
            List<string> warnings = new List<string>();
            List<object> report = new List<object>();
            string work = Path.Combine(Path.GetTempPath(), "JoyPro-cockpit-pictures-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            EngineProgress.SetCancelable(true);
            try
            {
                foreach (string game in games)
                {
                    GameInstall install = GameInstalls.Find(game, warnings);
                    if (!install.Installed || string.IsNullOrWhiteSpace(install.InstallPath) || !Directory.Exists(install.InstallPath))
                    {
                        report.Add(new { Game = game, Built = 0, Skipped = 0, Failed = 0, Note = CockpitManualPages.GameName(game) + " is not installed." });
                        continue;
                    }
                    string outRoot = Path.Combine(root, game);
                    if (game == "DCS")
                    {
                        DcsCockpitPictures.Job job = new DcsCockpitPictures.Job
                        {
                            DcsRoot = install.InstallPath,
                            OutRoot = outRoot,
                            WorkDir = work,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            CommandList = plane => GameDataPaths.DbFile("DCS", plane + ".html"),
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        if (DcsLua.LuaePath(install.InstallPath).Length == 0)
                        {
                            report.Add(new { Game = game, Built = 0, Skipped = 0, Failed = 0, Note = "luae.exe was not found in the DCS folder." });
                            continue;
                        }
                        List<DcsCockpitPictures.SetResult> results = DcsCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "XPlane12")
                    {
                        XPlaneCockpitPictures.Job job = new XPlaneCockpitPictures.Job
                        {
                            XPlaneRoot = install.InstallPath,
                            OutRoot = outRoot,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        List<XPlaneCockpitPictures.SetResult> results = XPlaneCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "MSFS2020")
                    {
                        MsfsCockpitPictures.Job job = new MsfsCockpitPictures.Job
                        {
                            OutRoot = outRoot,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.PackageRoots.AddRange(MsfsCockpitPictures.PackageRoots(install.InstallPath));
                        job.Only.AddRange(only);
                        List<MsfsCockpitPictures.SetResult> results = MsfsCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "NuclearOption")
                    {
                        NuclearOptionCockpitPictures.Job job = new NuclearOptionCockpitPictures.Job
                        {
                            GameRoot = install.InstallPath,
                            OutRoot = outRoot,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        List<NuclearOptionCockpitPictures.SetResult> results = NuclearOptionCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "IL2Game")
                    {
                        Il2CockpitPictures.Job job = new Il2CockpitPictures.Job
                        {
                            DataFolder = Il2CockpitPictures.DataFolder(install.InstallPath),
                            OutRoot = outRoot,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        List<Il2CockpitPictures.SetResult> results = Il2CockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "WarThunder")
                    {
                        // The models are Oodle packed: only with the maintainer's helper (tools\wt-oodle); users get packs.
                        if (WarThunderCockpitPictures.FindOodleHelper().Length == 0)
                        {
                            string note = "War Thunder's cockpit models need the War Thunder Oodle helper (tools\\wt-oodle, a maintainer tool); its pictures come as picture packs.";
                            report.Add(new { Game = game, Built = 0, Skipped = 0, Failed = 0, Note = note });
                            if (textOutput)
                                Console.WriteLine(CockpitManualPages.GameName(game) + ": " + note);
                            continue;
                        }
                        WarThunderCockpitPictures.Job job = new WarThunderCockpitPictures.Job
                        {
                            GameRoot = install.InstallPath,
                            OutRoot = outRoot,
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Zstd = (data, offset, length) =>
                            {
                                using (MemoryStream packed = new MemoryStream(data, offset, length))
                                using (ZstdSharp.DecompressionStream z = new ZstdSharp.DecompressionStream(packed))
                                using (MemoryStream raw = new MemoryStream())
                                {
                                    z.CopyTo(raw);
                                    return raw.ToArray();
                                }
                            },
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        List<WarThunderCockpitPictures.SetResult> results = WarThunderCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    else if (game == "StarCitizen")
                    {
                        // Read only, never while the game runs (StarCitizenCockpitPictures checks it again).
                        string running = StarCitizenP4k.RunningGame();
                        if (running.Length > 0)
                        {
                            string note = "Star Citizen is running (" + running + "): close Star Citizen first.";
                            report.Add(new { Game = game, Built = 0, Skipped = 0, Failed = 0, Note = note });
                            if (textOutput)
                                Console.WriteLine(CockpitManualPages.GameName(game) + ": " + note);
                            continue;
                        }
                        StarCitizenCockpitPictures.Job job = new StarCitizenCockpitPictures.Job
                        {
                            GameRoot = install.InstallPath,
                            OutRoot = outRoot,
                            CacheFolder = Path.Combine(DeviceCatalog.DataRoot(), "Cache", "StarCitizen"),
                            Force = force,
                            ViewFilter = Environment.GetEnvironmentVariable("JOYPRO_PICTURE_VIEWS") ?? string.Empty,
                            Zstd = UnpackZstd,
                            Progress = (cur, total, phase, item) => EngineProgress.Report(cur, total, phase, item),
                            Cancelled = () => EngineProgress.CancelRequested
                        };
                        job.Only.AddRange(only);
                        List<StarCitizenCockpitPictures.SetResult> results = StarCitizenCockpitPictures.Build(job);
                        AddReport(report, game, results.Select(r => new PictureResult { Plane = r.Plane, Folder = r.Folder, Views = r.Views, Callouts = r.Callouts, Skipped = r.Skipped, Error = r.Error, Notes = r.Notes }).ToList(), textOutput);
                    }
                    if (EngineProgress.CancelRequested)
                        break;
                }
            }
            finally
            {
                EngineProgress.SetCancelable(false);
                try
                {
                    if (Directory.Exists(work))
                        Directory.Delete(work, true);
                }
                catch (Exception)
                {
                }
            }
            if (!textOutput)
                Console.WriteLine(EngineSerialization.ForConsole(EngineSerialization.ToJson(new { Root = root, Games = report, Warnings = warnings })));
            return 0;
        }

        // cockpits pictures check [--game G] [--aircraft a,b] [--write] [--drop]: the quality gate on sets already there
        // (screenshots, picture packs, older builds): every picture measured and rated; --write keeps the ratings in
        // pictures.json ("quality" per view and per set), --drop also leaves out the panel pictures below the gate.
        private static int CockpitsPicturesCheck(List<string> args, string root, bool textOutput)
        {
            string gameOption = PopOptionValue(args, "--game") ?? "all";
            string aircraft = PopOptionValue(args, "--aircraft") ?? string.Empty;
            bool write = PopFlag(args, "--write");
            bool drop = PopFlag(args, "--drop");
            List<string> only = aircraft.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            List<object> report = new List<object>();
            if (!Directory.Exists(root))
                return Fail("No cockpit pictures in " + root);
            foreach (string gameFolder in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string game = Path.GetFileName(gameFolder);
                if (!string.Equals(gameOption, "all", StringComparison.OrdinalIgnoreCase) && !gameOption.Split(',').Any(g => string.Equals(g.Trim(), game, StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (string setFolder in Directory.GetDirectories(gameFolder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string manifest = Path.Combine(setFolder, "pictures.json");
                    if (!File.Exists(manifest))
                        continue;
                    string set = Path.GetFileName(setFolder);
                    if (only.Count > 0 && !only.Any(o => set.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    try
                    {
                        report.Add(CheckSet(game, setFolder, write, drop, textOutput));
                    }
                    catch (Exception ex)
                    {
                        report.Add(new { Game = game, Set = set, Error = ex.Message });
                        if (textOutput)
                            Console.WriteLine(game + "/" + set + ": FAILED " + ex.Message);
                    }
                }
            }
            if (!textOutput)
                Console.WriteLine(EngineSerialization.ForConsole(EngineSerialization.ToJson(new { Root = root, Sets = report })));
            return 0;
        }

        private static object CheckSet(string game, string folder, bool write, bool drop, bool textOutput)
        {
            string manifest = Path.Combine(folder, "pictures.json");
            Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(manifest, System.Text.Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null)
                throw new InvalidDataException("pictures.json is not a JSON object");
            bool screenshot = string.Equals(DeviceCatalog.Text(root, "source"), "screenshot", StringComparison.OrdinalIgnoreCase);
            List<object> views = root.ContainsKey("views") ? root["views"] as List<object> : null;
            views = views ?? new List<object>();
            List<object> kept = new List<object>();
            List<int> scores = new List<int>();
            int overview = -1, dropped = 0;
            List<string> worst = new List<string>();
            foreach (Dictionary<string, object> v in views.OfType<Dictionary<string, object>>())
            {
                string kind = DeviceCatalog.Text(v, "kind");
                string image = Path.Combine(folder, DeviceCatalog.Text(v, "image"));
                string mask = DeviceCatalog.Text(v, "mask");
                PictureMetrics m = File.Exists(image) ? MeasureFile(image, mask.Length > 0 ? Path.Combine(folder, mask) : null) : null;
                if (m == null)
                {
                    // A listed picture that is missing (or does not open): reported; --drop leaves it out of the list.
                    worst.Add(DeviceCatalog.Text(v, "id") + " " + (File.Exists(image) ? "does not open" : "picture missing") + (drop ? " (left out)" : string.Empty));
                    if (drop)
                    {
                        dropped++;
                        continue;
                    }
                    kept.Add(v);
                    continue;
                }
                int score = PictureQuality.Rate(m, kind);
                if (drop && kind == "panel" && score < PictureQuality.Keep)
                {
                    dropped++;
                    worst.Add(DeviceCatalog.Text(v, "id") + " " + score.ToString(CultureInfo.InvariantCulture) + " (left out)");
                    continue;
                }
                if (kind == "overview" && overview < 0)
                    overview = score;
                scores.Add(score);
                if (score < PictureQuality.Good)
                    worst.Add(DeviceCatalog.Text(v, "id") + " " + score.ToString(CultureInfo.InvariantCulture) + (m.Problems.Count > 0 ? " (" + string.Join(", ", m.Problems) + ")" : string.Empty));
                v["quality"] = score;
                if (m.Problems.Count > 0)
                    v["problems"] = m.Problems.Cast<object>().ToList();
                else
                    v.Remove("problems");
                // JoyPro's line drawing of a screenshot or of a poor picture is not shown.
                if (screenshot || score < PictureQuality.Good || kind == "overview")
                    v["drawingReadable"] = false;
                kept.Add(v);
            }
            bool low = PictureQuality.LowSet(overview < 0 ? 0 : overview, scores);
            if (write)
            {
                root["views"] = kept;
                root["quality"] = new Dictionary<string, object>
                {
                    { "score", Math.Max(0, overview) },
                    { "mean", scores.Count == 0 ? 0 : (int)Math.Round(scores.Average()) },
                    { "low", low },
                    { "views", scores.Count },
                    { "dropped", dropped },
                    { "merged", 0 },
                    { "checked", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
                };
                File.WriteAllText(manifest, EngineSerialization.ToJson(root), new System.Text.UTF8Encoding(false));
            }
            if (textOutput)
                Console.WriteLine(game + "/" + Path.GetFileName(folder) + ": overview " + overview + ", mean " + (scores.Count == 0 ? 0 : (int)Math.Round(scores.Average())) + (low ? ", LOW QUALITY" : string.Empty) +
                                  (worst.Count > 0 ? "; poor: " + string.Join("; ", worst.Take(8)) + (worst.Count > 8 ? " ..." : string.Empty) : string.Empty));
            return new { Game = game, Set = Path.GetFileName(folder), Overview = overview, Mean = scores.Count == 0 ? 0 : (int)Math.Round(scores.Average()), Low = low, Views = scores.Count, Dropped = dropped, Poor = worst };
        }

        // A picture file (PNG, or JPEG with its mask PNG) measured by the quality gate; null when it cannot be read.
        private static PictureMetrics MeasureFile(string image, string mask)
        {
            if (!File.Exists(image))
                return null;
            try
            {
                return MeasureBitmap(image, mask);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static PictureMetrics MeasureBitmap(string image, string mask)
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(image))
            {
                int w = bmp.Width, h = bmp.Height;
                byte[] rgba = new byte[w * h * 4];
                System.Drawing.Imaging.BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 0; x < w; x++)
                        {
                            int o = (y * w + x) * 4;
                            rgba[o] = row[x * 4 + 2];
                            rgba[o + 1] = row[x * 4 + 1];
                            rgba[o + 2] = row[x * 4];
                            rgba[o + 3] = row[x * 4 + 3];
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                if (mask != null && File.Exists(mask))
                {
                    using (System.Drawing.Bitmap mb = new System.Drawing.Bitmap(mask))
                    {
                        if (mb.Width == w && mb.Height == h)
                        {
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < w; x++)
                                    rgba[(y * w + x) * 4 + 3] = mb.GetPixel(x, y).R;
                        }
                    }
                }
                return PictureQuality.Measure(rgba, w, h, 4);
            }
        }

        internal static byte[] UnpackZstd(byte[] data, int offset, int length)
        {
            using (MemoryStream packed = new MemoryStream(data, offset, length))
            using (ZstdSharp.DecompressionStream z = new ZstdSharp.DecompressionStream(packed))
            using (MemoryStream raw = new MemoryStream())
            {
                z.CopyTo(raw);
                return raw.ToArray();
            }
        }

        private sealed class PictureResult
        {
            public string Plane;
            public string Folder;
            public int Views;
            public int Callouts;
            public bool Skipped;
            public string Error;
            public List<string> Notes;
        }

        private static void AddReport(List<object> report, string game, List<PictureResult> results, bool textOutput)
        {
            int built = results.Count(r => !r.Skipped && string.IsNullOrEmpty(r.Error));
            int skipped = results.Count(r => r.Skipped);
            int failed = results.Count(r => !string.IsNullOrEmpty(r.Error));
            report.Add(new
            {
                Game = game,
                Built = built,
                Skipped = skipped,
                Failed = failed,
                Note = string.Empty,
                Aircraft = results.Select(r => new { r.Plane, r.Views, r.Callouts, r.Skipped, r.Error, r.Notes }).ToList()
            });
            if (textOutput)
            {
                Console.WriteLine(CockpitManualPages.GameName(game) + ": " + built + " built, " + skipped + " up to date, " + failed + " failed");
                foreach (PictureResult r in results)
                {
                    Console.WriteLine("  " + r.Plane + ": " + (r.Skipped ? "up to date" : !string.IsNullOrEmpty(r.Error) ? "FAILED " + r.Error : r.Views + " pictures, " + r.Callouts + " callouts") +
                                      (r.Notes != null && r.Notes.Count > 0 ? " (" + string.Join("; ", r.Notes) + ")" : string.Empty));
                }
            }
        }
    }
}
