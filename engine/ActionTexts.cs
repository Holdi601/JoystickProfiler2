#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // JoyPro's own names, categories and explanations of a game's actions (shared by the engine and the GUI;
    // docs/relation-builder.md "Readable names"): DB\Functions\ActionTexts\<GameId>.tsv, lines "id<TAB>name<TAB>category
    // <TAB>explanation". Written for JoyPro (never copied from a game), so they ship with JoyPro.
    //   - the name stands in for a title the game gives no words for: empty, the id itself, the id in words ("Rb pitch")
    //     or a key of the game's string table ("inprb_pitch"). IL-2 Korea keeps its own text in encrypted archives, so
    //     there the table always names the action (the catalog only has borrowed IL-2 Great Battles titles or the id).
    //   - the category is the section of the game's own controls menu;
    //   - the explanation is the "what it does" line of the catalog, search and relation editor.
    // Also the check for words nobody can read in a title (UnexplainedAbbreviation), used by the coverage report and the
    // quality gate.
    public static class ActionTexts
    {
        public sealed class Entry
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Category = string.Empty;
            public string Explanation = string.Empty;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<string, Entry>> Cache = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);

        // Games whose own action text cannot be read on this PC: the table's name always wins.
        private static readonly HashSet<string> TableNamesWin = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IL2Korea" };

        // Games whose table only holds corrections of the game's own titles (shorthand written out, "EFB Back" ->
        // "Electronic Flight Bag (EFB): Back", "IFCS x" -> what it is): the table's name always wins, the category stays
        // the game's.
        private static readonly HashSet<string> CorrectionGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSFS2024", "StarCitizen", "XPlane11", "XPlane12" };

        public static void Reset()
        {
            lock (Sync)
                Cache.Clear();
        }

        // <DB>\Functions\ActionTexts.
        public static string FolderUnder(string functionsFolder)
        {
            return Path.Combine(functionsFolder ?? string.Empty, "ActionTexts");
        }

        // The file of a game ("IL2" is IL-2 Great Battles' DB folder name).
        public static string FileOf(string folder, string game)
        {
            string g = (game ?? string.Empty).Trim();
            if (string.Equals(g, "IL2", StringComparison.OrdinalIgnoreCase))
                g = "IL2Game";
            return Path.Combine(folder ?? string.Empty, g + ".tsv");
        }

        public static Dictionary<string, Entry> Table(string folder, string game)
        {
            string file = FileOf(folder, game);
            lock (Sync)
            {
                Dictionary<string, Entry> table;
                if (Cache.TryGetValue(file, out table))
                    return table;
                table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(file))
                    {
                        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            if (raw.Length == 0 || raw[0] == '#')
                                continue;
                            string[] parts = raw.Split('\t');
                            string id = parts[0].Trim();
                            if (id.Length == 0 || table.ContainsKey(id))
                                continue;
                            table[id] = new Entry
                            {
                                Id = id,
                                Name = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                                Category = parts.Length > 2 ? parts[2].Trim() : string.Empty,
                                Explanation = parts.Length > 3 ? parts[3].Trim() : string.Empty
                            };
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                Cache[file] = table;
                return table;
            }
        }

        public static Entry Find(string folder, string game, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            Entry entry;
            return Table(folder, game).TryGetValue(id.Trim(), out entry) ? entry : null;
        }

        // A title that says nothing a person can read: empty, the id, the id in words ("Rb pitch", "Cam switch mc far",
        // "Ext action1"), a string-table key ("inprb_pitch") or one of the readable-from-id forms older JoyPro versions
        // wrote ("RB pitch", "Camera switch MC far", "Extra action 1").
        public static bool IsWeakTitle(string id, string title)
        {
            string t = (title ?? string.Empty).Trim();
            if (t.Length == 0)
                return true;
            string raw = (id ?? string.Empty).Trim();
            // The whole id and its last part ("pilot.site_dist_plus" -> "site_dist_plus", "map|action" -> "action").
            int cut = raw.LastIndexOfAny(new[] { '.', '|', '/' });
            foreach (string part in cut >= 0 && cut < raw.Length - 1 ? new[] { raw, raw.Substring(cut + 1) } : new[] { raw })
            {
                string i = Letters(part);
                if (i.Length == 0)
                    continue;
                string n = Letters(t);
                if (n == i || (n.StartsWith("inp", StringComparison.Ordinal) && n.Substring(3) == i))
                    return true;
                if (n.Replace("camera", "cam").Replace("extraaction", "extaction") == i)
                    return true;
            }
            return false;
        }

        // The title to show: the game's own unless it is weak (or the game's text cannot be read here, or the table corrects
        // the game's titles); then the table's name; else the title as it is.
        public static string BestTitle(string folder, string game, string id, string title)
        {
            Entry entry = Find(folder, game, id);
            string g = game ?? string.Empty;
            if (entry != null && entry.Name.Length > 0 && (TableNamesWin.Contains(g) || CorrectionGames.Contains(g) || IsWeakTitle(id, title)))
                return entry.Name;
            return title ?? string.Empty;
        }

        public static bool NameWins(string game)
        {
            return TableNamesWin.Contains(game ?? string.Empty);
        }

        private static string Letters(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in text ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ---- unreadable words in titles ----------------------------------------------------------------------------

        // Short English words (2-4 letters) and the shorthand everyone in a cockpit reads; anything else this short in a
        // title is an abbreviation.
        private static readonly HashSet<string> ShortWords = new HashSet<string>(
            ("a ab abs act add aft age ago aid aim air all also alt am an and anti any app apu arc are area arm arms art as ask at atc " +
             "aux auto away axe axes axis back bag bail ball band bank bar bars base bay be beam bed beep bell belt best beta big bin " +
             "bit bite blip blow blue boat body bolt bomb bone book boom boot bore both bow bowl box boxs bump bus busy but buy by " +
             "cab cage cake call calm cam camp can cap car carb card care cart case cash cast cat cell chat chin chip chop city " +
             "clip club coal coat code coil cold come com comm cone cook cool cop cope copy core cost cowl crab crew crop cross " +
             "cry cue cup cut dam dark dash data date day dead deal dear deck deep deer deg demo deny desk dial did die dig dim dip " +
             "dirt disc dish disk dive do dock does dog done door dot down drag draw drip drop drum dry dual due dull dump dust " +
             "duty each ear earn ease east easy eat echo edge edit egg eject else end ends ent era even ever exit eye eyes face " +
             "fact fade fail fair fall fan far farm fast fat fear feed feel feet fell felt few fig file fill film find fine fire " +
             "firm fish fist fit fits five fix flag flap flat flaw fled flew flex flip flir flow fly foam fog foil fold folk " +
             "food foot for fore form fort four fov free from fuel full fun fund fuse gain game gap gas gate gave gaze gear get " +
             "gift give glad glow glue go goal goes gold golf gone good got gps grab gray grey grid grip grow gun guns guy gyro " +
             "had hail hair half hall halt hand hang hard harm hat hate have haze he head heal hear heat heel held helo help her " +
             "here hero hide high hill him hint hip his hit hold hole home hood hook hop hope horn host hot hour how hub hud hue " +
             "hull hunt hurt ice icon idea idle if ill in inch info inn into ion iron is it item its jam jet job jog join joke " +
             "jump just keep kept key keys kick kill kind king kit kite knee knew knob knot know lag lake lamp land lane lap " +
             "large last late law lay lazy lead leaf lean leap left leg legs lend lens less let lie life lift like limb lime " +
             "line link lip list lit live load loan lock log long look loop lose loss lost lot loud love low luck lung made " +
             "mag mags mail main make male man many map mark mash mass mast mat mate max may me meal mean meat meet melt " +
             "menu mesh met mid mile milk mill min mind mine mint miss mist mix mode mold more most move much mud mug must " +
             "mute my nail name nap nav near neat neck need net new news next nice nine no node none noon nor norm nose not " +
             "note now nut oak odd of off oil ok old on once one ones only onto open or oral our out oval over own pace pack " +
             "pad page paid pain pair pal pan park part pass past path pay peak pen per pick pie pile pin pine pink pipe pit " +
             "plan play plot plow plug plus pod pods poke pole poll pool poor pop port pose post pot pour pray prep pre prev " +
             "prop pull pump pure push put race rack radar rag raid rail rain ram ran rank rare rate raw ray read real rear " +
             "red reel rest rib rice rich ride rig ring rise risk road roam rock rod role roll roof room root rope rose rot " +
             "row rub rule run rush rust sad safe said sail salt same sand sat save saw say scan sea seal seat see seek seem " +
             "seen self sell send sent set sew shed ship shoe shop shot show shut sick side sign silo sing sink sir sit site " +
             "six size skid ski skip sky slab slap sled slew slid slim slip slot slow snap snow so soak sock soft soil sold " +
             "sole solo some son song soon sort soul spin spot spy stab star stay stem step stir stop such suit sum sun sure " +
             "swap sway swim tab tag tail take tale talk tall tan tank tap tape task taxi tea team tear tech tell temp ten " +
             "tend tens term test text than that the them then they thin this thus tick tide tie tilt time tin tiny tip tire " +
             "to toe tog told toll tone too took tool top tops torn toss tour tow town toy trap tray tree trim trip true tub " +
             "tube tune turn twin two type ugly unit up upon urge us use used user van vane vary vast vent very vest view vine " +
             "void volt vote wait wake walk wall want war warm warn wash wave wax way we weak wear web week well went were " +
             "west wet what when who whom why wide wife wild will win wind wing wipe wire wise wish with wolf won wood wool " +
             "word wore work worm wrap yard yaw year yell yes yet you your zero zone zoom " +
             "khz mhz hz ft ms km kg nm mm cm psi hpa deg kts kt mph kph sync mach non bug logo zulu heli tug pip fwd ping blah burp " +
             "clap gasp rude pins ammo null mic tint baro hi lo cw ccw nope oops wave vs").Split(' '),
            StringComparer.OrdinalIgnoreCase);

        // Cockpit acronyms that are the real labels (HUD, MFD, RPM...): written in capitals they are not unexplained.
        private static readonly HashSet<string> KnownAcronyms = new HashSet<string>(
            ("AA AAA ADF ADI AG AGL AI AOA AP APU ATC AUX AVTR CAS CDI CDU CMDS CMS COM COMM CRS DC DED DG DME DMS DSS ECM EFIS " +
             "EGT ELT EPU ESP EVA EWS FADEC FCR FD FFB FLIR FMS FOV FSD FSS GPS GPU HDG HMCS HMD HOTAS HSI HUD IAS ICP IFCS IFF ILS " +
             "INS IR IRST JFS LMFD MFD MFDS MCDU MSL NAV NDB NVG OBS OSB PC PFD PFL PTT PWR RCS RDR RMFD RPM RWR SAS SOI SPD SRV " +
             "TACAN TAS TCAS TGP TMS TMFD FMFD TQS TV TWS UFC UHF UI VHF VNAV VOR VR VS VSI VTOL VVI XPDR " +
             "GCU RMU UFD MMFD MKR LOC APR BC FPLN EXEC ECAM PROG DIR DES DEP CRZ CLB AHRS IPD EM CNI DTC RTO JATO TA RA VFR IFR ID MDA " +
             "INTC ARR PERF PRED SEC SETA NV EW WPN TSD EOTS GPWS NWS EMP SCM DNA AR FPS HMS IVC LED CCIP CCRP DLZ GA FPL CLR GMT UTC INIT APT DX").Split(' '),
            StringComparer.OrdinalIgnoreCase);

        private static readonly Regex Token = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]{2,4}(?![A-Za-z])", RegexOptions.CultureInvariant);

        // The first word of a title nobody can read: a 2-4 letter abbreviation that is a piece of the id ("Rb pitch" for
        // rb_pitch, "Trt mount" for turret.trt_mount_d) or a lower-case word without vowels ("dn", "spd"). Capitals are
        // fine when they are a known cockpit label, or when the title is not made from the id. Letters right after a
        // number are units and ordinals (25kHz, 1st). DCS titles are the module's own: only lower-case words count there.
        public static string UnexplainedAbbreviation(string game, string id, string title)
        {
            string t = (title ?? string.Empty).Trim();
            if (t.Length == 0)
                return null;
            HashSet<string> pieces = IdPieces(id);
            bool dcs = string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
            bool fromId = IdLike(t, pieces);
            foreach (Match m in Token.Matches(t))
            {
                string word = m.Value;
                if (ShortWords.Contains(word) || word.Trim('x', 'X').Length == 0)
                    continue;
                int end = m.Index + word.Length;
                // A type designation ("Bf-110", "Rb05", "Gsh-23", "Ju 88"), part of a hyphenated word ("Re-fuel",
                // "pre-sel") or a word whose letters the console mangled ("Fr?quence").
                if (end < t.Length && (char.IsDigit(t[end]) || t[end] > '\u007F' || t[end] == '-'))
                    continue;
                if (m.Index > 0 && (t[m.Index - 1] > '\u007F' || t[m.Index - 1] == '-'))
                    continue;
                if (word.Length <= 3 && char.IsUpper(word[0]) && end + 1 < t.Length && t[end] == ' ' && char.IsDigit(t[end + 1]) && !Expansions.ContainsKey(word))
                    continue;
                // The plural of a cockpit label ("HMDs").
                if (word.Length > 2 && word.EndsWith("s", StringComparison.Ordinal) && word.Substring(0, word.Length - 1).All(char.IsUpper) && KnownAcronyms.Contains(word.Substring(0, word.Length - 1)))
                    continue;
                string lower = word.ToLowerInvariant();
                if (word.All(char.IsUpper))
                {
                    if (dcs || KnownAcronyms.Contains(word) || !fromId || !pieces.Contains(lower))
                        continue;
                    return word;
                }
                if (pieces.Contains(lower) || !Regex.IsMatch(lower, "[aeiouy]"))
                    return word;
            }
            return null;
        }

        // Shorthand of the games' own titles written out for the catalog (not for DCS, whose titles are the module's own):
        // a whole word, any case; the first letter keeps its case. Known cockpit labels written like words get their
        // capitals back ("Fms" -> "FMS", "Ui" -> "UI").
        private static readonly Dictionary<string, string> Expansions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "dn", "down" }, { "incr", "increase" }, { "decr", "decrease" }, { "inc", "increase" }, { "dec", "decrease" }, { "stby", "standby" },
            { "btns", "buttons" }, { "xmt", "transmit" }, { "tx", "transmit" }, { "spd", "speed" }, { "hdg", "heading" }, { "crs", "course" },
            { "hvr", "hover" }, { "elev", "elevation" }, { "ovrd", "override" }, { "sel", "select" }, { "freq", "frequency" }, { "brg", "bearing" },
            { "flt", "flight" }, { "sens", "sensitivity" }, { "proc", "procedure" }, { "grd", "ground" }, { "togl", "toggle" }, { "tog", "toggle" },
            { "dist", "distance" }, { "thtl", "throttle" }, { "trt", "turret" }, { "ign", "ignition" }, { "att", "attitude" }, { "eng", "engine" },
            { "gov", "governor" }, { "cal", "calibration" }, { "std", "standard" }, { "orni", "ornithopter" }, { "med", "medium" }, { "rel", "relative" },
            { "prec", "precision" }, { "gen", "general" }, { "mov", "movie" }, { "lmb", "left mouse button" }, { "rmb", "right mouse button" },
            { "ads", "aim down sights" }, { "qs", "quick select" }, { "gp", "gamepad" }, { "kp", "keypad" }, { "pl", "player" }, { "ext", "external" },
            { "msl", "missile" }, { "obi", "course (OBI)" }, { "goto", "go to" }, { "var", "reference" }, { "neg", "negative" }, { "pos", "positive" },
            { "vjoy", "virtual joystick" }, { "zgt", "zero-g traversal" }, { "oper", "operate" }, { "mgv", "ground vehicle" }
        };

        // Shorthand of more than one word.
        private static readonly KeyValuePair<Regex, string>[] Phrases =
        {
            new KeyValuePair<Regex, string>(new Regex(@"\bMac Cready\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "MacCready")
        };

        public static string ExpandAbbreviations(string game, string title)
        {
            string t = title ?? string.Empty;
            if (t.Length == 0 || string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
                return t;
            foreach (KeyValuePair<Regex, string> phrase in Phrases)
                t = phrase.Key.Replace(t, phrase.Value);
            return Token.Replace(t, m =>
            {
                string word = m.Value;
                int end = m.Index + word.Length;
                // Parts of a hyphenated word ("Re-fuel") and type designations stay.
                if (end < t.Length && (t[end] == '-' || char.IsDigit(t[end])))
                    return word;
                if (m.Index > 0 && t[m.Index - 1] == '-')
                    return word;
                string expanded;
                bool caps = word.All(char.IsUpper);
                if (!caps && word.Length >= 2 && KnownAcronyms.Contains(word) && !ShortWords.Contains(word))
                    return word.ToUpperInvariant();
                // A label in capitals stays a label (HDG, CRS); the shorthand written as a word is spelled out.
                if (caps && word.Length > 1 && KnownAcronyms.Contains(word))
                    return word;
                if (!Expansions.TryGetValue(word, out expanded))
                    return word;
                if (caps && word.Length > 1)
                    return expanded.ToUpperInvariant();
                return char.IsUpper(word[0]) ? char.ToUpperInvariant(expanded[0]) + expanded.Substring(1) : expanded;
            });
        }

        // "Pitch X", "Turret Y axis": one word and an axis letter, which does not say what it steers.
        public static bool IsWordPlusAxis(string title)
        {
            return Regex.IsMatch((title ?? string.Empty).Trim(), @"^[A-Za-z]+\s+(?:[XYZ]|R[XYZ])(?:\s+axis)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private static HashSet<string> IdPieces(string id)
        {
            HashSet<string> pieces = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(id ?? string.Empty, "[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.CultureInvariant))
                pieces.Add(m.Value.ToLowerInvariant());
            return pieces;
        }

        // Every word of the title is a piece of the id: a title made from the id.
        private static bool IdLike(string title, HashSet<string> pieces)
        {
            List<string> words = Regex.Matches(title, "[A-Za-z]+|[0-9]+", RegexOptions.CultureInvariant).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToList();
            return words.Count > 0 && words.All(pieces.Contains);
        }
    }
}
