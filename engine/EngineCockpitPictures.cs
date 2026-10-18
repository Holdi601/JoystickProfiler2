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
    //   cockpits pictures build [--game DCS|XPlane12|MSFS2020|IL2Game|NuclearOption|all] [--aircraft <name>[,<name>...]] [--force] [--out <folder>]
    //   cockpits pictures status [--out <folder>]
    internal static partial class Program
    {
        private static readonly string[] PictureGames = { "DCS", "XPlane12", "MSFS2020", "NuclearOption", "IL2Game", "WarThunder" };

        private static int CockpitsPictures(List<string> rest, string outPath, bool textOutput)
        {
            string action = rest.Count == 0 ? "status" : rest[0].ToLowerInvariant();
            List<string> args = rest.Skip(1).ToList();
            string root = PopOptionValue(args, "--out");
            if (string.IsNullOrWhiteSpace(root))
                root = CockpitPictureSet.Root();
            switch (action)
            {
                case "build":
                    return CockpitsPicturesBuild(args, root, textOutput);
                case "status":
                case "list":
                    return CockpitsPicturesStatus(root, textOutput);
                default:
                    return Fail("Unknown cockpits pictures action: " + action + " (build, status)");
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
                Console.WriteLine(EngineSerialization.ToJson(new { Root = root, Games = games }));
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
                Console.WriteLine(EngineSerialization.ToJson(new { Root = root, Games = report, Warnings = warnings }));
            return 0;
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
