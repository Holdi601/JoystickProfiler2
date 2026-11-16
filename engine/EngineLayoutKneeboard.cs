using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // engine layout kneeboard: the cheat sheets of "layout render" (the Layout page's pictures with every bind) as pages of
    // the game's kneeboard (docs/device-reference.md "Kneeboard"). DCS: <Saved Games>\DCS*\Kneeboard\<unit type>\JoyPro_<aircraft>_<device>.png,
    // which DCS shows on its kneeboard and OpenKneeboard in its "DCS Aircraft" tab; --target folder: any folder (an
    // OpenKneeboard "Folder" tab, other games). JoyPro writes only its own files (name JoyPro_*, its mark inside the PNG):
    // other pictures in these folders are never changed or removed.
    public sealed class KneeboardExportResult
    {
        public string Target { get; set; }
        public string Game { get; set; }
        public List<string> Folders { get; set; }
        public List<KneeboardAircraft> Aircraft { get; set; }
        public int PicturesWritten { get; set; }
        public int PicturesReplaced { get; set; }
        public int OldPicturesRemoved { get; set; }
        public bool DryRun { get; set; }
        public List<string> WrittenFiles { get; set; }
        public List<string> Skipped { get; set; }
        public List<string> Warnings { get; set; }

        public KneeboardExportResult()
        {
            Target = Game = string.Empty;
            Folders = new List<string>();
            Aircraft = new List<KneeboardAircraft>();
            WrittenFiles = new List<string>();
            Skipped = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class KneeboardAircraft
    {
        public string Plane { get; set; }
        // The name on the page's header: the module's own name for DCS ("F-16C bl.50", entry.lua as the GUI shows it), the
        // cockpit database's name, the game's name for a game without aircraft; else the aircraft id.
        public string Title { get; set; }
        public List<string> Folders { get; set; }
        // false: the folder name is the aircraft's own name (JoyPro and the installed DCS do not know the aircraft).
        public bool Known { get; set; }

        public KneeboardAircraft()
        {
            Plane = Title = string.Empty;
            Folders = new List<string>();
            Known = true;
        }
    }

    internal static partial class Program
    {
        internal const string KneeboardFilePrefix = "JoyPro_";
        // Written into every kneeboard PNG (a tEXt chunk): an existing file is only replaced when it carries it.
        internal const string KneeboardMark = "JoyPro kneeboard";

        // layout kneeboard [--target dcs|folder] [--saved-games <folder>] [--folder <folder>] [--game <game>] [--plane <name>]
        //     [--include-easy] [--device <name>] [--layout-map <device>=<file>] [--postfix <text>] [--size <px>]
        //     [--page list|portrait|original] [--picture top|none] [--combine true|false] [--style dark|light] [--remove-old] [--dry-run]
        //     [sheet style and drawing options]
        private static int RunLayoutKneeboard(List<string> args, string outPath, bool textOutput)
        {
            string target = (PopOptionValue(args, "--target") ?? "dcs").Trim().ToLowerInvariant();
            List<string> savedGames = ParseDelimitedOptionValues(PopOptionValues(args, "--saved-games"));
            string folder = PopOptionValue(args, "--folder") ?? string.Empty;
            string game = PopOptionValue(args, "--game") ?? string.Empty;
            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            bool includeEasy = PopFlag(args, "--include-easy");
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            List<string> layoutMapOptions = PopOptionValues(args, "--layout-map");
            layoutMapOptions.AddRange(PopOptionValues(args, "--layout-maps"));
            string postfix = PopOptionValue(args, "--postfix") ?? string.Empty;
            int size = PopOptionInt(args, "--size", 2048);
            string page = (PopOptionValue(args, "--page") ?? string.Empty).Trim().ToLowerInvariant();
            bool combine = PopOptionBoolean(args, "--combine", false, out _);
            string style = PopOptionValue(args, "--style") ?? string.Empty;
            bool removeOld = PopFlag(args, "--remove-old");
            bool dryRun = PopFlag(args, "--dry-run");
            string pictureOption = (PopOptionValue(args, "--picture") ?? "top").Trim().ToLowerInvariant();
            JoyPro.DeviceReference.SheetStyle sheetStyle = PopSheetStyleOptions(args, out string sheetStyleError);
            if (sheetStyleError.Length > 0)
                return Fail(sheetStyleError);
            JoyPro.DeviceReference.DrawingStyle drawingStyle = JoyPro.DeviceReference.DrawingStyle.FromOptions(args,
                JoyPro.DeviceReference.DrawingStyle.FromSettings(JoyPro.DeviceReference.DeviceCatalog.DataRoot(), true));
            if (args.Count > 0)
                return Fail("Unknown layout kneeboard argument(s): " + string.Join(" ", args));

            if (target != "dcs" && target != "folder")
                return Fail("Invalid --target value '" + target + "'. Use dcs or folder.");
            if (page.Length == 0)
                page = "list";
            if (page != "list" && page != "portrait" && page != "original")
                return Fail("Invalid --page value '" + page + "'. Use list, portrait or original.");
            if (pictureOption != "top" && pictureOption != "none")
                return Fail("Invalid --picture value '" + pictureOption + "'. Use top or none.");
            bool withPicture = pictureOption == "top";
            if (size < 512 || size > 8192)
                return Fail("--size must be between 512 and 8192 pixels.");
            if (style.Length > 0 && !string.Equals(style, "dark", StringComparison.OrdinalIgnoreCase) && !string.Equals(style, "light", StringComparison.OrdinalIgnoreCase))
                return Fail("Invalid --style value '" + style + "'. Use dark or light.");
            if (Path.GetInvalidFileNameChars().Any(c => postfix.IndexOf(c) >= 0) || postfix.IndexOf(';') >= 0 || postfix.Length > 40)
                return Fail("--postfix may not contain ; or characters a file name cannot have, and at most 40 characters.");
            if (game.Length == 0)
                game = "DCS";
            if (target == "dcs" && !string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
                return Fail("--target dcs is the DCS kneeboard; use --target folder --folder <folder> for " + game + ".");
            if (target == "folder" && folder.Trim().Length == 0)
                return Fail("--target folder needs --folder <folder>.");
            if (!EngineLayoutRenderer.TryParseLayoutMapSpecs(ParseDelimitedOptionValues(layoutMapOptions), out Dictionary<string, string> layoutMaps, out string mapError))
                return Fail(mapError);

            KneeboardExportResult result = new KneeboardExportResult { Target = target, Game = game, DryRun = dryRun };
            List<string> devices = ParseDelimitedOptionValues(deviceOptions);
            bool isDcs = string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);

            // Where the pictures go: every DCS Saved Games folder (DCS, DCS.openbeta) or the chosen folder.
            List<string> roots = new List<string>();
            if (target == "dcs")
            {
                List<string> bases = savedGames.Count > 0 ? savedGames.Select(KneeboardFullPath).ToList() : DetectDcsSavedGames(result.Warnings);
                foreach (string basis in bases)
                {
                    if (!System.IO.Directory.Exists(basis))
                    {
                        result.Warnings.Add("The DCS Saved Games folder does not exist: " + basis);
                        continue;
                    }
                    roots.Add(Path.Combine(basis, "Kneeboard"));
                }
                if (roots.Count == 0)
                    return Fail("No DCS Saved Games folder found (Saved Games\\DCS or DCS.openbeta). Pass --saved-games <folder>, or use --target folder --folder <folder>.");
            }
            else
            {
                roots.Add(Path.Combine(KneeboardFullPath(folder), SafeKneeboardName(game)));
            }
            result.Folders.AddRange(roots);

            // Which aircraft: the ones asked for, else every aircraft of the game the devices have binds in.
            List<string> requested = ParseDelimitedOptionValues(planeOptions);
            HashSet<string> boundPlanes = PlanesWithBinds(game, devices);
            List<string> planes = new List<string>();
            if (requested.Count > 0)
            {
                foreach (string plane in requested.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string own = boundPlanes.FirstOrDefault(p => string.Equals(p, plane, StringComparison.OrdinalIgnoreCase));
                    if (own == null)
                    {
                        result.Skipped.Add(plane + ": no binds for it in this profile.");
                        continue;
                    }
                    if (isDcs && DcsKneeboardFolders.IsLayer(own))
                    {
                        result.Skipped.Add(own + ": not an aircraft (no kneeboard).");
                        continue;
                    }
                    planes.Add(own);
                }
            }
            else
            {
                foreach (string plane in boundPlanes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (isDcs && DcsKneeboardFolders.IsLayer(plane))
                        continue;
                    // The game-mode copy ("F-16C_50_easy") only when its aircraft has no binds itself, or with --include-easy.
                    string basis = isDcs ? DcsKneeboardFolders.VariantBase(plane) : null;
                    if (basis != null && !includeEasy && boundPlanes.Contains(basis))
                    {
                        result.Skipped.Add(plane + ": the same kneeboard as " + basis + " (add --include-easy to put it there too).");
                        continue;
                    }
                    planes.Add(plane);
                }
            }
            if (planes.Count == 0)
                return Fail("Nothing to put on the kneeboard: " + (requested.Count > 0 ? string.Join(" ", result.Skipped) : "no aircraft of " + game + " has binds on these devices in this profile."));

            Dictionary<string, List<string>> installNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string plane in planes)
            {
                KneeboardAircraft aircraft = new KneeboardAircraft { Plane = plane, Title = KneeboardTitle(game, plane, result.Warnings) };
                if (target == "dcs")
                {
                    bool known;
                    aircraft.Folders = DcsKneeboardFolders.Resolve(plane, out known, name =>
                    {
                        List<string> found;
                        if (!installNames.TryGetValue(name, out found))
                            installNames[name] = found = DcsKneeboardFolders.FromInstall(name, result.Warnings);
                        return found;
                    });
                    aircraft.Known = known;
                    if (!known)
                        result.Warnings.Add("JoyPro does not know the kneeboard folder of '" + plane + "'; it uses the folder '" + aircraft.Folders[0] + "'. If DCS does not show the pages, the folder needs the aircraft's name of the mission editor.");
                }
                else
                {
                    aircraft.Folders = new List<string> { SafeKneeboardName(plane) };
                }
                result.Aircraft.Add(aircraft);
            }

            // The pages per aircraft (file stem -> PNG pages), then written into every kneeboard folder.
            string staging = Path.Combine(Path.GetTempPath(), "JoyPro-kneeboard-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (drawingStyle != null)
                    EngineCalloutRenderer.ExportStyle = drawingStyle;
                else if (sheetStyle != null && sheetStyle.Drawing.Length > 0)
                    EngineCalloutRenderer.ExportStyle = JoyPro.DeviceReference.DrawingStyle.Parse(sheetStyle.Drawing);
                bool lightPage = style.Length == 0 && sheetStyle != null
                    ? !sheetStyle.IsDarkPage(true)
                    : style.Length == 0 ? page == "list" : string.Equals(style, "light", StringComparison.OrdinalIgnoreCase);
                Dictionary<KneeboardAircraft, List<KeyValuePair<string, List<byte[]>>>> pagesOf = new Dictionary<KneeboardAircraft, List<KeyValuePair<string, List<byte[]>>>>();

                if (page == "list")
                {
                    Dictionary<string, string> layoutOf = EngineLayoutRenderer.BuildJoystickLayoutMap(layoutMaps);
                    foreach (KneeboardAircraft aircraft in result.Aircraft)
                    {
                        List<KeyValuePair<string, List<byte[]>>> files = new List<KeyValuePair<string, List<byte[]>>>();
                        List<EngineKneeboardPage.Section> sections = new List<EngineKneeboardPage.Section>();
                        try
                        {
                            foreach (string joystick in KneeboardDevicesOf(game, aircraft.Plane, devices))
                            {
                                string layoutPath = layoutOf.Where(p => SameKneeboardDevice(p.Key, joystick)).OrderBy(p => string.Equals(p.Key, joystick, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                    .Select(p => p.Value).FirstOrDefault() ?? string.Empty;
                                EngineKneeboardPage.Section section = EngineKneeboardPage.Build(joystick, KneeboardDeviceName(joystick), layoutPath, game, aircraft.Plane,
                                    withPicture && !combine, !lightPage, result.Warnings);
                                if (section.Groups.Count > 0)
                                    sections.Add(section);
                            }
                            if (combine && sections.Count > 0)
                            {
                                files.Add(new KeyValuePair<string, List<byte[]>>("All devices",
                                    KneeboardPngs(EngineKneeboardPage.Render(sections, game, aircraft.Plane, "All devices", size, !lightPage, aircraft.Title, KneeboardGameName(game)), postfix)));
                            }
                            else
                            {
                                foreach (EngineKneeboardPage.Section section in sections)
                                    files.Add(new KeyValuePair<string, List<byte[]>>(section.Device,
                                        KneeboardPngs(EngineKneeboardPage.Render(new List<EngineKneeboardPage.Section> { section }, game, aircraft.Plane, section.Device, size, !lightPage, aircraft.Title, KneeboardGameName(game)), postfix)));
                            }
                        }
                        finally
                        {
                            foreach (EngineKneeboardPage.Section section in sections)
                                if (section.Picture != null)
                                    section.Picture.Dispose();
                        }
                        pagesOf[aircraft] = files;
                    }
                }
                else
                {
                    // The cheat sheets, drawn as the Layout page draws them, into a work folder in temp.
                    int pageWidth = page == "portrait" ? (int)Math.Round(size * 0.75) : size;
                    LayoutRenderResult rendered = EngineLayoutRenderer.Render(
                        staging,
                        devices,
                        new List<string> { game },
                        new List<string>(),
                        layoutMaps,
                        false,
                        true,
                        true,
                        new LayoutVisualOptions
                        {
                            Style = sheetStyle,
                            ModifierColorCsv = sheetStyle == null ? string.Empty : sheetStyle.ModifierCsv,
                            FontFamily = sheetStyle == null ? string.Empty : sheetStyle.FontFamily,
                            FontSize = sheetStyle == null ? 0f : (float)sheetStyle.FontSize,
                            TextColor = sheetStyle == null ? string.Empty : sheetStyle.TextColor,
                            OutlineColor = sheetStyle == null ? string.Empty : sheetStyle.OutlineColor,
                            LabelBackground = sheetStyle == null ? string.Empty : sheetStyle.LabelBackground,
                            LightStyle = lightPage,
                            // Drawn at twice the page width, then made smaller: sharp text at kneeboard size.
                            PrintWidth = Math.Min(8000, pageWidth * 2),
                            Combine = combine
                        },
                        planes.Select(p => new KeyValuePair<string, string>(game, p)).ToList());
                    foreach (string warning in rendered.Warnings)
                        AddUniqueKneeboard(result.Warnings, warning);
                    if (rendered.Errors.Count > 0)
                        return Fail("layout kneeboard failed: " + string.Join(" | ", rendered.Errors));
                    foreach (KneeboardAircraft aircraft in result.Aircraft)
                    {
                        string planeFolder = Path.Combine(staging, game, aircraft.Plane);
                        pagesOf[aircraft] = rendered.WrittenFiles
                            .Where(f => string.Equals(Path.GetDirectoryName(f), planeFolder, StringComparison.OrdinalIgnoreCase))
                            .Where(f => combine == string.Equals(Path.GetFileName(f), "All devices.png", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .Select(f => new KeyValuePair<string, List<byte[]>>(StripKneeboardGuid(Path.GetFileNameWithoutExtension(f)), new List<byte[]> { KneeboardPage(f, page, size, postfix) }))
                            .ToList();
                    }
                }

                HashSet<string> writtenNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KneeboardAircraft aircraft in result.Aircraft)
                {
                    List<KeyValuePair<string, List<byte[]>>> files = pagesOf[aircraft];
                    if (files.Count == 0)
                    {
                        result.Skipped.Add(aircraft.Plane + ": no page was made (no bound control on these devices).");
                        continue;
                    }
                    HashSet<string> stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, List<byte[]>> file in files)
                    {
                        string baseStem = KneeboardFilePrefix + SafeKneeboardName(aircraft.Plane) + "_" + SafeKneeboardName(file.Key);
                        string stem = baseStem;
                        for (int n = 2; !stems.Add(stem); n++)
                            stem = baseStem + " " + n.ToString(CultureInfo.InvariantCulture);
                        for (int p = 0; p < file.Value.Count; p++)
                        {
                            string name = stem + (p == 0 ? string.Empty : "_p" + (p + 1).ToString(CultureInfo.InvariantCulture)) + postfix + ".png";
                            foreach (string root in roots)
                            {
                                foreach (string unit in aircraft.Folders)
                                    WriteKneeboardFile(result, Path.Combine(root, unit), name, file.Value[p], dryRun, writtenNow);
                            }
                        }
                        // The pages after the last one of an earlier export with more binds (JoyPro's own, same ending) go.
                        foreach (string root in roots)
                        {
                            foreach (string unit in aircraft.Folders)
                            {
                                string dir = Path.Combine(root, unit);
                                if (!System.IO.Directory.Exists(dir))
                                    continue;
                                foreach (string old in System.IO.Directory.GetFiles(dir, stem + "_p*" + postfix + ".png"))
                                {
                                    string oldPostfix;
                                    if (writtenNow.Contains(old) || !Regex.IsMatch(Path.GetFileName(old), "^" + Regex.Escape(stem) + "_p[0-9]+" + Regex.Escape(postfix) + "\\.png$", RegexOptions.IgnoreCase) ||
                                        !IsKneeboardPicture(old, out oldPostfix) || oldPostfix != postfix)
                                        continue;
                                    if (!dryRun)
                                        File.Delete(old);
                                    result.OldPicturesRemoved++;
                                }
                            }
                        }
                    }
                }

                // --remove-old: JoyPro's own pictures (its name, its mark, the same postfix) of these aircraft folders
                // that this run did not write again (a device that left the profile). Nothing else is removed.
                if (removeOld)
                {
                    foreach (string dir in roots.SelectMany(r => result.Aircraft.SelectMany(a => a.Folders).Distinct(StringComparer.OrdinalIgnoreCase).Select(u => Path.Combine(r, u))))
                    {
                        if (!System.IO.Directory.Exists(dir))
                            continue;
                        foreach (string old in System.IO.Directory.GetFiles(dir, KneeboardFilePrefix + "*" + postfix + ".png"))
                        {
                            string oldPostfix;
                            if (writtenNow.Contains(old) || !IsKneeboardPicture(old, out oldPostfix) || oldPostfix != postfix)
                                continue;
                            if (!dryRun)
                                File.Delete(old);
                            result.OldPicturesRemoved++;
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    if (System.IO.Directory.Exists(staging))
                        Directory.Delete(staging, true);
                }
                catch (Exception ex)
                {
                    JoyPro.Logging.JoyProLog.Debug("Kneeboard", "work folder not removed: " + ex.Message);
                }
            }

            if (result.PicturesWritten == 0)
                return Fail("No kneeboard picture was written: " + string.Join(" ", result.Skipped.Concat(result.Warnings)));
            EmitPayload(textOutput ? KneeboardToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // The devices of a list page: the ones asked for, else every device with a bind in this aircraft.
        private static List<string> KneeboardDevicesOf(string game, string plane, List<string> devices)
        {
            List<string> found = new List<string>();
            foreach (KeyValuePair<string, Bind> entry in MultiBind.WithActiveExtras(InternalDataManagement.AllBinds))
            {
                Bind bind = entry.Value;
                string joystick = bind == null ? string.Empty : (bind.Joystick ?? string.Empty).Trim();
                if (joystick.Length == 0 || string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase) || bind.Rl == null)
                    continue;
                if (devices.Count > 0 && !devices.Any(d => SameKneeboardDevice(d, joystick)))
                    continue;
                if (!EngineLayoutRenderer.RelationHasActivePlane(bind.Rl, game, plane))
                    continue;
                if (!found.Contains(joystick, StringComparer.OrdinalIgnoreCase))
                    found.Add(joystick);
            }
            return found.OrderBy(d => KneeboardDeviceName(d), StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The device's name on a page: the name the user gave it, else its Windows name; never its GUID.
        private static Dictionary<string, string> kneeboardDcsNames;

        // The aircraft's readable name for the page header (KneeboardAircraft.Title).
        internal static string KneeboardTitle(string game, string plane, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(plane) || string.Equals(plane, game, StringComparison.OrdinalIgnoreCase) || plane == "*")
                return KneeboardGameName(game);
            if (string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
            {
                if (kneeboardDcsNames == null)
                {
                    kneeboardDcsNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (EngineMediaAircraft a in EngineMedia.Aircraft(new List<string>()))
                            if (!string.IsNullOrWhiteSpace(a.DisplayName))
                                kneeboardDcsNames[a.Plane] = a.DisplayName.Trim();
                    }
                    catch (Exception)
                    {
                        // No DCS install readable: the cockpit database or the id below.
                    }
                }
                string module;
                if (kneeboardDcsNames.TryGetValue(plane, out module))
                    return module;
            }
            try
            {
                JoyPro.DeviceReference.CockpitCatalog catalog = LoadCockpitCatalog();
                JoyPro.DeviceReference.CockpitVariant variant = catalog == null ? null : catalog.VariantOfPlane(plane);
                if (variant != null && catalog.Resolve(variant).Games.Any(g => string.Equals(g.Plane, plane, StringComparison.OrdinalIgnoreCase) &&
                        (JoyPro.DeviceReference.CockpitCatalog.GameMatches(game, g.Game) || JoyPro.DeviceReference.CockpitCatalog.GameMatches(g.Game, game))) &&
                    !string.IsNullOrWhiteSpace(variant.Name))
                    return variant.Name.Trim();
            }
            catch (Exception)
            {
            }
            return plane;
        }

        // The game's name as JoyPro shows it ("DCS World", "Microsoft Flight Simulator X").
        internal static string KneeboardGameName(string game)
        {
            EngineMediaGame known = EngineMedia.KnownGames().FirstOrDefault(g => string.Equals(g.GameId, game, StringComparison.OrdinalIgnoreCase));
            return known != null && !string.IsNullOrWhiteSpace(known.Name) ? known.Name : game;
        }

        internal static string KneeboardDeviceName(string joystick)
        {
            string name = StripKneeboardGuid(EngineLayoutRenderer.ResolveJoystickAlias(joystick));
            return name.Length > 0 ? name : StripKneeboardGuid(joystick);
        }

        private static List<byte[]> KneeboardPngs(List<Bitmap> pages, string postfix)
        {
            List<byte[]> pngs = new List<byte[]>();
            foreach (Bitmap page in pages)
            {
                using (page)
                using (MemoryStream stream = new MemoryStream())
                {
                    page.Save(stream, ImageFormat.Png);
                    pngs.Add(WithKneeboardMark(stream.ToArray(), postfix));
                }
            }
            return pngs;
        }

        // Writes (or replaces) one of JoyPro's pages; a file of someone else with that name is left as it is.
        private static void WriteKneeboardFile(KneeboardExportResult result, string dir, string name, byte[] png, bool dryRun, HashSet<string> writtenNow)
        {
            string file = Path.Combine(dir, name);
            writtenNow.Add(file);
            bool exists = File.Exists(file);
            if (exists && !IsKneeboardPicture(file))
            {
                result.Skipped.Add(file + ": a picture of someone else with this name; left as it is.");
                return;
            }
            if (!dryRun)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, png);
            }
            result.PicturesWritten++;
            if (exists)
                result.PicturesReplaced++;
            result.WrittenFiles.Add(file);
        }

        private static string KneeboardFullPath(string path)
        {
            return Path.GetFullPath(NormalizePath(path.Trim().Trim('"'))).TrimEnd('\\', '/');
        }

        // Saved Games\DCS and Saved Games\DCS.openbeta (each one with DCS's Config folder), the user's DCS folder first.
        private static List<string> DetectDcsSavedGames(List<string> warnings)
        {
            List<string> found = new List<string>();
            foreach (string path in GamePathDiscovery.DiscoverDcsSavedGamesPaths(warnings))
            {
                string name = Path.GetFileName((path ?? string.Empty).TrimEnd('\\', '/'));
                if (!Regex.IsMatch(name, @"^DCS(\.[A-Za-z0-9_-]+)?$", RegexOptions.IgnoreCase))
                    continue;
                if (!System.IO.Directory.Exists(Path.Combine(path, "Config")) && !System.IO.Directory.Exists(Path.Combine(path, "Kneeboard")))
                    continue;
                if (!found.Contains(path, StringComparer.OrdinalIgnoreCase))
                    found.Add(path);
            }
            return found;
        }

        // The aircraft of a game the devices (all devices when none is named) have an active bind in.
        private static HashSet<string> PlanesWithBinds(string game, List<string> devices)
        {
            HashSet<string> planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in MultiBind.WithActiveExtras(InternalDataManagement.AllBinds))
            {
                Bind bind = entry.Value;
                if (bind == null || bind.Rl == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                if (devices.Count > 0 && !devices.Any(d => SameKneeboardDevice(d, bind.Joystick)))
                    continue;
                foreach (RelationItem node in bind.Rl.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null || !string.Equals((node.Game ?? "DCS").Trim(), game, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        if (plane.Value && !string.IsNullOrWhiteSpace(plane.Key))
                            planes.Add(plane.Key.Trim());
                    }
                }
            }
            return planes;
        }

        private static bool SameKneeboardDevice(string a, string b)
        {
            string left = (a ?? string.Empty).Trim(), right = (b ?? string.Empty).Trim();
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(StripKneeboardGuid(left), StripKneeboardGuid(right), StringComparison.OrdinalIgnoreCase) && (left.IndexOf('{') < 0 || right.IndexOf('{') < 0);
        }

        internal static string StripKneeboardGuid(string device)
        {
            return Regex.Replace((device ?? string.Empty).Trim(), @"\s*\{[0-9A-Fa-f-]{8,}\}\s*$", string.Empty).Trim();
        }

        internal static string SafeKneeboardName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = new string((value ?? string.Empty).Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return clean.Length == 0 ? "device" : clean;
        }

        // The page: a portrait page (DCS stretches every picture to its 3:4 kneeboard) with the sheet as large as fits,
        // or the sheet's own shape; size = the page height (portrait) or the longest side (original). The PNG carries
        // JoyPro's mark.
        internal static byte[] KneeboardPage(string sheetFile, string page, int size, string postfix)
        {
            using (Bitmap source = new Bitmap(sheetFile))
            {
                int width, height;
                if (page == "portrait")
                {
                    height = size;
                    width = (int)Math.Round(size * 0.75);
                }
                else
                {
                    double shrink = Math.Min(1.0, size / (double)Math.Max(source.Width, source.Height));
                    width = Math.Max(1, (int)Math.Round(source.Width * shrink));
                    height = Math.Max(1, (int)Math.Round(source.Height * shrink));
                }
                double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
                int drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
                using (Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(result))
                    {
                        graphics.Clear(source.GetPixel(0, 0));
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        using (ImageAttributes wrap = new ImageAttributes())
                        {
                            wrap.SetWrapMode(WrapMode.TileFlipXY);
                            graphics.DrawImage(source, new Rectangle((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, wrap);
                        }
                    }
                    using (MemoryStream stream = new MemoryStream())
                    {
                        result.Save(stream, ImageFormat.Png);
                        return WithKneeboardMark(stream.ToArray(), postfix);
                    }
                }
            }
        }

        // A tEXt chunk "Software: JoyPro kneeboard; postfix=<postfix>;" right after the PNG header (IHDR): --remove-old
        // only removes the pictures of the same postfix.
        internal static byte[] WithKneeboardMark(byte[] png, string postfix)
        {
            const int afterHeader = 8 + 8 + 13 + 4;
            if (png == null || png.Length < afterHeader)
                return png;
            byte[] data = Encoding.UTF8.GetBytes("Software\0" + KneeboardMark + "; postfix=" + (postfix ?? string.Empty) + ";");
            byte[] type = Encoding.ASCII.GetBytes("tEXt");
            byte[] chunk = new byte[12 + data.Length];
            chunk[0] = (byte)(data.Length >> 24);
            chunk[1] = (byte)(data.Length >> 16);
            chunk[2] = (byte)(data.Length >> 8);
            chunk[3] = (byte)data.Length;
            Buffer.BlockCopy(type, 0, chunk, 4, 4);
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
            uint crc = Crc32(chunk, 4, 4 + data.Length);
            chunk[8 + data.Length] = (byte)(crc >> 24);
            chunk[9 + data.Length] = (byte)(crc >> 16);
            chunk[10 + data.Length] = (byte)(crc >> 8);
            chunk[11 + data.Length] = (byte)crc;
            byte[] result = new byte[png.Length + chunk.Length];
            Buffer.BlockCopy(png, 0, result, 0, afterHeader);
            Buffer.BlockCopy(chunk, 0, result, afterHeader, chunk.Length);
            Buffer.BlockCopy(png, afterHeader, result, afterHeader + chunk.Length, png.Length - afterHeader);
            return result;
        }

        // A picture JoyPro wrote (its mark near the start of the file), and the postfix it was written with.
        internal static bool IsKneeboardPicture(string file)
        {
            string postfix;
            return IsKneeboardPicture(file, out postfix);
        }

        internal static bool IsKneeboardPicture(string file, out string postfix)
        {
            postfix = string.Empty;
            try
            {
                byte[] head = new byte[256];
                int read;
                using (System.IO.FileStream stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    read = stream.Read(head, 0, head.Length);
                string text = Encoding.UTF8.GetString(head, 0, read);
                int at = text.IndexOf(KneeboardMark, StringComparison.Ordinal);
                if (at < 0)
                    return false;
                Match own = Regex.Match(text.Substring(at), "^" + Regex.Escape(KneeboardMark) + "; postfix=([^;]*);");
                postfix = own.Success ? own.Groups[1].Value : string.Empty;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static uint[] crcTable;

        private static uint Crc32(byte[] buffer, int offset, int count)
        {
            if (crcTable == null)
            {
                uint[] table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                crcTable = table;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                crc = crcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static void AddUniqueKneeboard(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value))
                list.Add(value);
        }

        private static string KneeboardToText(KneeboardExportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Kneeboard Export" + (result.DryRun ? " (dry run: nothing written)" : string.Empty));
            sb.AppendLine("Target: " + (result.Target == "dcs" ? "DCS kneeboard" : "folder") + " (" + result.Game + ")");
            sb.AppendLine("Export Path: " + (result.Folders.FirstOrDefault() ?? "<none>"));
            sb.AppendLine("Folders: " + result.Folders.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string folder in result.Folders)
                sb.AppendLine("  - " + folder);
            sb.AppendLine("Aircraft: " + result.Aircraft.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KneeboardAircraft aircraft in result.Aircraft)
                sb.AppendLine("  - " + aircraft.Plane + " -> " + string.Join(", ", aircraft.Folders) + (aircraft.Known ? string.Empty : " (folder name guessed)"));
            sb.AppendLine("Pictures Written: " + result.PicturesWritten.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Pictures Replaced: " + result.PicturesReplaced.ToString(CultureInfo.InvariantCulture));
            if (result.OldPicturesRemoved > 0)
                sb.AppendLine("Old Pictures Removed: " + result.OldPicturesRemoved.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Written Files: " + result.WrittenFiles.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string file in result.WrittenFiles)
                sb.AppendLine("  - " + file);
            if (result.Skipped.Count > 0)
            {
                sb.AppendLine("Skipped:");
                foreach (string skipped in result.Skipped)
                    sb.AppendLine("  - " + skipped);
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            return sb.ToString();
        }
    }
}
