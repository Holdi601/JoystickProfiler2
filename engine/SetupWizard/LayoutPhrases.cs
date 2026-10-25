#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // "Lay out with AI" (docs/layout-with-ai.md): the user's preferences in plain words ("throttle and left-hand stick",
    // "for space games use the left stick and its twist axis instead of pedals", "trim on the hat, curves softer for
    // helis") become preferences JoyPro understands. Each one says in words what JoyPro will do (Template + Args, translated
    // by the GUI) and compiles into the planner's options and rules (Auto Assign's planner) or into changes of axis
    // tuning, force feedback and the virtual joystick. Offline and deterministic; the local AI model only rewrites what
    // the rules do not understand into the phrases below (EngineLayout). Plain C# 7.3: the engine runs it, the GUI tests
    // link it.
    public sealed class LayoutPreference
    {
        public string Id = string.Empty;          // devices, left-stick-space, function-on, curve, ffb, vjoy, modifiers ...
        public string Key = string.Empty;         // the same key = the same preference (a newer one replaces it)
        public string Text = string.Empty;        // the user's words
        public string Template = string.Empty;    // what JoyPro does, English with {0}.. (the GUI translates it)
        public List<string> Args = new List<string>();
        public string Class = string.Empty;       // fixed, rotary, space; empty = every aircraft
        public string Game = string.Empty;        // a game id when the words name one
        public string Function = string.Empty;    // regex over function ids (function-on, curve axes)
        public string Controls = string.Empty;    // regex over the control vocabulary
        public string Role = string.Empty;        // device role (left-stick, throttle, main-stick, collective, pedals)
        public string Value = string.Empty;       // number or word (curve delta, ffb factor, vjoy mode, modifier count)
        public bool Negative;                     // "no ... on ...", "without pedals"
        public string Source = "rules";           // rules, ai

        public string Describe()
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, Template, Args.Cast<object>().ToArray());
            }
            catch (FormatException)
            {
                return Template;
            }
        }
    }

    public sealed class LayoutUnderstanding
    {
        public List<LayoutPreference> Preferences = new List<LayoutPreference>();
        public List<string> NotUnderstood = new List<string>();
    }

    // What the preferences ask beyond the binds: axis curves, force feedback, the virtual joystick.
    public sealed class LayoutTuningIntent
    {
        public string Axes = string.Empty;        // regex over function ids (pitch, roll ...)
        public string Class = string.Empty;
        public string Game = string.Empty;
        public double CurveDelta;                 // added to the curvature
        public bool Linear;                       // curvature 0
        public double DeadzoneDelta;
        public string Preference = string.Empty;  // the preference's key
    }

    public sealed class LayoutFfbIntent
    {
        public string Class = string.Empty;
        public string Game = string.Empty;
        public string Kind = string.Empty;        // force, shake, off, on
        public double Factor = 1;
        public string Preference = string.Empty;
    }

    public sealed class LayoutVJoyIntent
    {
        public string Class = string.Empty;
        public string Game = string.Empty;
        public string Mode = string.Empty;        // native, virtual, xbox
        public string Preference = string.Empty;
    }

    public sealed class LayoutCompiled
    {
        public List<SetupRule> Rules = new List<SetupRule>();
        public Dictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.Ordinal);   // device -> role
        public int? Modifiers;
        public bool? PreferFewer;
        public string PhaseLayers = string.Empty;
        public double? Realism;
        public bool? KeepLayout;
        public bool FreshLayout;                  // "from scratch": every bound relation is laid out again
        public List<List<string>> Priorities = new List<List<string>>();
        public List<LayoutTuningIntent> Tuning = new List<LayoutTuningIntent>();
        public List<LayoutFfbIntent> Ffb = new List<LayoutFfbIntent>();
        public List<LayoutVJoyIntent> VJoy = new List<LayoutVJoyIntent>();
        public List<string> Notes = new List<string>();   // English templates of what could not be done ("no left-hand stick found")
    }

    public static class LayoutPhrases
    {
        // ---- vocabulary ----

        private const string RotaryWords = @"\b(heli|helis|helicopters?|choppers?|rotorcraft|rotary wing|hubschraubern?)\b";
        private const string SpaceWords = @"\b(space|spaceships?|space ?games?|space ?sims?|space ?ships?|star citizen|elite( dangerous)?|x4)\b";
        private const string FixedWords = @"\b(jets?|planes?|airplanes?|fixed wing|fighters?|warbirds?|flugzeugen?)\b";

        // Function words -> function ids (regex), and an English name for the summary.
        private static readonly List<KeyValuePair<string, string[]>> FunctionWords = new List<KeyValuePair<string, string[]>>
        {
            Fw(@"\btrim(mer|ming)?\b", @"^(trim_|trimleft|trimright|trimnoseup|trimreset)", "trim"),
            Fw(@"\b(cms|counter ?measures?|chaff|flares?)\b", @"^cms_", "countermeasures"),
            Fw(@"\btms\b|target management", @"^target_management_system", "TMS"),
            Fw(@"\bdms\b|display management|sensor select", @"^(a10_dms|sensor_select)", "DMS / sensor select"),
            Fw(@"\b(landing )?gear\b", @"^(gear|space_gear)", "landing gear"),
            Fw(@"\bflaps?\b", @"^flaps", "flaps"),
            Fw(@"\b(air ?brakes?|speed ?brakes?)\b", @"^(airbrake|airbakeextend|airbrakeretract)", "airbrake"),
            Fw(@"\b(weapon release|pickle)\b", @"^launchweapon", "weapon release"),
            Fw(@"\b(guns?|cannon)\b", @"^trigger", "gun trigger"),
            Fw(@"\bradar\b", @"^radar_", "radar"),
            Fw(@"\bzoom\b", @"^zoom", "zoom"),
            Fw(@"\b(lights?|lighting)\b", @"^lights_", "lights"),
            Fw(@"\b(yaw|rudder)\b", @"^(rudder|space_yaw)$", "yaw / rudder"),
            Fw(@"\b(strafe|strafing|translation)\b", @"^space_strafe", "strafe"),
            Fw(@"\b(toe ?brakes?|wheel ?brakes?|brakes)\b", @"^wheelbrake", "wheel brakes"),
            Fw(@"\b(slew|tdc|cursor)\b", @"^(tdc_|hcu_)", "slew / TDC"),
            Fw(@"\b(comms?|radios?|ptt|push to talk)\b", @"^(comm\d|easycomms)", "comms"),
            Fw(@"\bkneeboard\b", @"^kneeboard", "kneeboard"),
            Fw(@"\bauto ?pilot\b", @"^autopilot", "autopilot"),
            Fw(@"\bmaster arm\b", @"^master_arm", "master arm"),
            Fw(@"\bafter ?burner\b", @"^afterburner", "afterburner"),
            Fw(@"\b(shields?)\b", @"^space_shield", "shields"),
            Fw(@"\b(power triangle|power)\b", @"^space_power", "power management"),
            Fw(@"\b(boost|afterburn)\b", @"^space_boost", "boost"),
            Fw(@"\bpitch\b", @"^pitch$", "pitch"),
            Fw(@"\broll\b", @"^roll$", "roll")
        };

        // Control words -> control regex, English name.
        private static readonly List<KeyValuePair<string, string[]>> ControlWords = new List<KeyValuePair<string, string[]>>
        {
            Fw(@"\b(twist|twist axis|z ?axis|twisting)\b", @"\.axis\.twist$", "twist axis"),
            Fw(@"\b(mini ?sticks?|thumb ?sticks?|slew sticks?|analog ?sticks?)\b", @"ministick", "mini-stick"),
            Fw(@"\b(hats?|coolie|pov)\b", @"\.(hat|coolie)\.", "hat"),
            Fw(@"\b(sliders?|wheels?|rotar(y|ies)|dials?|knobs?)\b", @"\.axis\.(slider|wheel|rotary)", "slider / wheel"),
            Fw(@"\bpinky\b", @"\.pinky\.", "pinky lever"),
            Fw(@"\b(panels?|button ?box(es)?)\b", @"^(panel|mfd)\.", "panel"),
            Fw(@"\b(base)\b", @"\.base\.", "base buttons"),
            Fw(@"\b(toggles?|switch(es)?)\b", @"(toggle|switch)", "switch"),
            Fw(@"\b(triggers?)\b", @"\.trigger\.", "trigger")
        };

        private static KeyValuePair<string, string[]> Fw(string words, string regex, string name)
        {
            return new KeyValuePair<string, string[]>(words, new[] { regex, name });
        }

        // ---- reading ----

        // Splits the text into clauses (sentences, commas, semicolons, " and then ", new lines) and reads each one.
        // games: game id -> its names in lower case (for "in DCS", "for Star Citizen"); aircraft: catalog keys.
        public static LayoutUnderstanding Read(string text, IDictionary<string, string[]> games, IEnumerable<string> aircraft)
        {
            LayoutUnderstanding result = new LayoutUnderstanding();
            List<string> planes = (aircraft ?? Enumerable.Empty<string>()).ToList();
            foreach (string raw in Regex.Split(text ?? string.Empty, @"[\r\n;.!?]+|,(?![^(]*\))"))
            {
                string clause = raw.Trim();
                if (clause.Length == 0)
                    continue;
                int before = result.Preferences.Count;
                ReadClause(clause, games, planes, result.Preferences);
                if (result.Preferences.Count == before)
                    result.NotUnderstood.Add(clause);
            }
            return result;
        }

        // German -> the English words the rules read (the user is German; docs/layout-with-ai.md "German"). Longer
        // phrases first; umlauts also as ae / oe / ue / ss. The German test set: engine/tests/fixtures/phrases/layout-de.json.
        private static readonly KeyValuePair<Regex, string>[] GermanWords = new[]
        {
            De(@"\b(wie im (echten|realen) (flugzeug|jet|cockpit|vorbild)|wie in echt|realistisch\w*)\b", "like the real aircraft"),
            De(@"\b(gleiche (taste|funktion)n? (ueberall|überall|in jedem (flugzeug|spiel))|einheitlich\w*)\b", "same button everywhere"),
            De(@"\b(von vorne?|neu anfangen|alles neu|komplett neu|von grund auf)\b", "from scratch"),
            De(@"\b(behalte|lass|lasse) meine (belegung|binds|tasten|knöpfe|knoepfe)\b", "keep my layout"),
            De(@"\bnur (die )?unbelegten\b", "only unbound"),
            De(@"\b(weltraum-?spielen?|weltraum-?sims?|weltraum-?simulationen?|im weltraum|weltraum|all-?spielen?)\b", "space games"),
            De(@"\b(raumschiff(e|en)?)\b", "spaceships"),
            De(@"\b(hubschraubern?|helikoptern?)\b", "helicopters"),
            De(@"\b(kampfjets?|kampfflugzeug(e|en)?|flugzeug(e|en)?|flieger[ns]?)\b", "planes"),
            De(@"\b(moduswahlschalter|modus-?schalter|modiwahlschalter|betriebsarten-?schalter|master-?mode-?schalter|modus-?wahlschalter)\b", "mode switch"),
            De(@"\b(phasen-?ebenen?|phasen-?layer)\b", "phase layers"),
            De(@"\b(modifikator(en)?|modifiern?|umschalt-?tasten?|shift-?tasten?)\b", "modifiers"),
            De(@"\b(virtuelle[rnms]? (joystick|gerät|geraet)|virtuell)\b", "virtual joystick"),
            De(@"\b(echte[nr]? geräte|echte[nr]? geraete|nativ)\b", "native"),
            De(@"\b(kraft-?rückkopplung|kraft-?rueckkopplung|ffb)\b", "ffb"),
            De(@"\b(rütteln|ruetteln|schütteln|schuetteln|vibrationen?|wackeln)\b", "shake"),
            De(@"\b(tot-?zonen?|tote zonen?)\b", "dead zone"),
            De(@"\b(kennlinien?|kurven?)\b", "curves"),
            De(@"\b(empfindlicher)\b", "more sensitive"),
            De(@"\b(weicher\w*|sanfter\w*|flacher\w*)\b", "softer"),
            De(@"\b(härter\w*|haerter\w*|direkter\w*|steiler\w*|schärfer\w*|schaerfer\w*)\b", "harder"),
            De(@"\b(linear\w*)\b", "linear"),
            De(@"\b(größer\w*|groesser\w*)\b", "bigger"),
            De(@"\b(kleiner\w*)\b", "smaller"),
            De(@"\b(stärker\w*|staerker\w*)\b", "stronger"),
            De(@"\b(schwächer\w*|schwaecher\w*)\b", "weaker"),
            De(@"\b(ein (bisschen|wenig)|etwas|leicht)\b", "a bit"),
            De(@"\b(viel|deutlich|sehr)\b", "much"),
            De(@"\bweniger\b", "less"),
            De(@"\bmehr\b", "more"),
            // Devices and controls.
            De(@"\b(linke[rnms]?) (stick|joystick|knüppel|knueppel|steuerknüppel|steuerknueppel)\b", "left stick"),
            De(@"\b(stick|joystick|knüppel|knueppel) (links|in der linken hand|für die linke hand|fuer die linke hand)\b", "left stick"),
            De(@"\b(rechte[rnms]?) (stick|joystick|knüppel|knueppel|steuerknüppel|steuerknueppel)\b", "right stick"),
            De(@"\blinke[rnms]? hand\b", "left hand"),
            De(@"\brechte[rnms]? hand\b", "right hand"),
            De(@"\b(steuerknüppel|steuerknueppel|knüppel|knueppel)\b", "stick"),
            De(@"\b(gashebels?|schubhebels?|leistungshebels?|gas)\b", "throttle"),
            De(@"\b(ruder-?pedalen?|pedalen?)\b", "pedals"),
            De(@"\b(kollektiv-?hebel|kollektiv|pitch-?hebel)\b", "collective"),
            De(@"\b(dreh-?achse|twist-?achse|drehung|drehen)\b", "twist"),
            De(@"\b(hut-?schalter|hat-?schalter|coolie-?hat|kreuz-?schalter|coolie)\b", "hat"),
            De(@"\b(daumen-?sticks?|mini-?sticks?|analog-?sticks?)\b", "mini stick"),
            De(@"\b(schiebe-?regler|schieber|dreh-?regler|dreh-?knöpfe?|dreh-?knoepfe?|räder|raeder|rad)\b", "slider"),
            De(@"\b(kipp-?schalter|schalter)\b", "switch"),
            De(@"\b(abzug|trigger)\b", "trigger"),
            De(@"\b(kleiner finger|pinky-?hebel)\b", "pinky"),
            De(@"\b(tasten-?feld|button-?box|panele?)\b", "panel"),
            De(@"\b(sockel|basis)\b", "base"),
            // Functions.
            De(@"\b(höhen-?trimmung|hoehen-?trimmung|quer-?trimmung|seiten-?trimmung|trimmung|trimmen|trimm|trimmer)\b", "trim"),
            De(@"\b(fahrwerk)\b", "landing gear"),
            De(@"\b(lande-?klappen|klappen)\b", "flaps"),
            De(@"\b(luft-?bremsen?|brems-?klappen?)\b", "airbrake"),
            De(@"\b(rad-?bremsen?|bremsen)\b", "wheel brakes"),
            De(@"\b(täuschkörper|taeuschkoerper|düppel|dueppel|gegenmaßnahmen|gegenmassnahmen|fackeln|leuchtkörper)\b", "countermeasures"),
            De(@"\b(waffen-?auslösung|waffen-?ausloesung|waffe auslösen|waffe ausloesen|bomben abwerfen)\b", "weapon release"),
            De(@"\b(bord-?kanone|kanone)\b", "gun"),
            De(@"\b(funk(gerät|geraet)?|sprechtaste)\b", "radio"),
            De(@"\b(lichter|licht|beleuchtung)\b", "lights"),
            De(@"\b(seiten-?ruder|gieren|gier-?achse)\b", "yaw"),
            De(@"\b(höhen-?ruder|hoehen-?ruder|nick-?achse|nicken)\b", "pitch"),
            De(@"\b(quer-?ruder|roll-?achse|rollen)\b", "roll"),
            De(@"\b(nach-?brenner)\b", "afterburner"),
            De(@"\b(schilde?)\b", "shields"),
            De(@"\b(kniebrett)\b", "kneeboard"),
            De(@"\b(seitwärts|seitwaerts|translation|strafen)\b", "strafe"),
            // Small words.
            De(@"\b(anstatt|statt|anstelle)( von)?( de[rnm]| die| das)?\b", "instead of"),
            De(@"\b(auf|an) (de[nm]|die|das|der|einen|einem|eine|meinen|meinem|meine|meiner)\b", "on the"),
            De(@"\b(auf)\b", "on"),
            De(@"\b(kein|keine|keinen|keiner|keinem)\b", "no"),
            De(@"\bohne\b", "without"),
            De(@"\b(niemals|nie)\b", "never"),
            De(@"\bnicht\b", "not"),
            De(@"\bnur\b", "only"),
            De(@"\bund\b", "and"),
            De(@"\b(für|fuer|bei)( de[nm]| die| das| alle)?\b", "for"),
            De(@"\bmit\b", "with"),
            De(@"\b(zuerst|vorrangig)\b", "first"),
            De(@"\b(hauptsächlich|hauptsaechlich|meistens)\b", "mostly"),
            De(@"\bich fliege\b", "i fly"),
            De(@"\b(nimm|benutze|verwende|nutze)\b", "use"),
            De(@"\b(einen|einem|eine|ein)\b", "a"),
            De(@"\b(zwei)\b", "two"),
            De(@"\b(überall|ueberall)\b", "everywhere"),
            De(@"\b(der|die|das|den|dem|des)\b", "the")
        };

        private static KeyValuePair<Regex, string> De(string pattern, string english)
        {
            return new KeyValuePair<Regex, string>(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant), english);
        }

        // German words in the text: they are read as their English meaning. The German words do not occur in the English
        // phrases the rules read, so an English text stays as it is.
        internal static string Germanless(string lower)
        {
            string c = lower;
            foreach (KeyValuePair<Regex, string> word in GermanWords)
                c = word.Key.Replace(c, word.Value);
            return c;
        }

        internal static string Normalize(string clause)
        {
            string c = " " + Germanless((clause ?? string.Empty).ToLowerInvariant().Trim()) + " ";
            c = Regex.Replace(c, @"left[- ]hand(ed)?", "left hand");
            c = Regex.Replace(c, @"right[- ]hand(ed)?", "right hand");
            c = Regex.Replace(c, @"\bjoy ?stick\b", "stick");
            c = Regex.Replace(c, @"\bforce[- ]feedback\b", "ffb");
            c = Regex.Replace(c, @"\bv ?joy\b", "vjoy");
            c = Regex.Replace(c, @"\s+", " ");
            return c;
        }

        private static void ReadClause(string clause, IDictionary<string, string[]> games, List<string> planes, List<LayoutPreference> prefs)
        {
            string c = Normalize(clause);
            string cls = Regex.IsMatch(c, RotaryWords) ? SetupClasses.Rotary : Regex.IsMatch(c, SpaceWords) ? SetupClasses.Space : Regex.IsMatch(c, FixedWords) ? SetupClasses.Fixed : string.Empty;
            string game = GameOf(c, games);
            if (game.Length > 0 && cls == SetupClasses.Space && Regex.IsMatch(c, @"star citizen|elite|x4") && !Regex.IsMatch(c, @"\bspace\b"))
                cls = string.Empty;
            Func<string, LayoutPreference> make = id => new LayoutPreference { Id = id, Text = clause.Trim(), Class = cls, Game = game };
            int count = prefs.Count;

            // Curves: "curves softer for helis", "linear pitch in DCS", "bigger dead zone".
            Match curve = Regex.Match(c, @"\b(curves?|curvature|response|sensitiv\w*|dead ?zones?|linear|expo)\b");
            if (curve.Success && !Regex.IsMatch(c, @"\bffb\b"))
            {
                AddCurve(c, make, prefs);
                return;
            }

            // Force feedback: "softer FFB for helis", "less shake", "no FFB in IL-2".
            if (Regex.IsMatch(c, @"\bffb\b|\bshak(e|ing)\b|\bvibration\b|\brumble\b"))
            {
                AddFfb(c, make, prefs);
                return;
            }

            // The virtual joystick: "vJoy for Star Citizen", "native for DCS", "xbox pad for X".
            if (Regex.IsMatch(c, @"\bvjoy\b|virtual (joystick|device)|\bxbox\b|\bgamepad emulation\b|\bnative\b|physical devices"))
            {
                string mode = Regex.IsMatch(c, @"\bxbox\b|gamepad") ? "xbox" : Regex.IsMatch(c, @"\bnative\b|physical devices|\bno vjoy\b|without vjoy") ? "native" : "virtual";
                LayoutPreference p = make("vjoy");
                p.Value = mode;
                p.Key = "vjoy|" + Scope(p);
                p.Template = mode == "native" ? "{0}: the games see your devices directly (no virtual joystick)." : mode == "xbox" ? "{0}: a virtual Xbox pad (ViGEmBus)." : "{0}: the virtual joystick (vJoy) with your devices hidden.";
                p.Args.Add(ScopeName(p));
                if (p.Class.Length == 0 && p.Game.Length == 0)
                {
                    p.Template = mode == "native" ? "Every game sees your devices directly (no virtual joystick)." : mode == "xbox" ? "Every game: a virtual Xbox pad (ViGEmBus)." : "Every game: the virtual joystick (vJoy) with your devices hidden.";
                    p.Args.Clear();
                }
                prefs.Add(p);
                return;
            }

            // Modifiers and the mode switch.
            if (Regex.IsMatch(c, @"\b(modifiers?|shift( buttons?)?)\b") && !Regex.IsMatch(c, @"\bmode switch\b"))
            {
                LayoutPreference p = make("modifiers");
                p.Key = "modifiers";
                if (Regex.IsMatch(c, @"\b(no|without|zero|none|keine)\b"))
                    p.Value = "0";
                else if (Regex.IsMatch(c, @"\b(two|2)\b"))
                    p.Value = "2";
                else if (Regex.IsMatch(c, @"\b(one|1|a single|a)\b"))
                    p.Value = "1";
                else if (Regex.IsMatch(c, @"\bfew\b"))
                    p.Value = "few";
                else
                    p.Value = "2";
                p.Template = p.Value == "0" ? "No modifier buttons." : p.Value == "few" ? "As few modifier buttons as possible." : "At most {0} modifier button(s).";
                if (p.Value != "0" && p.Value != "few")
                    p.Args.Add(p.Value);
                prefs.Add(p);
            }
            if (Regex.IsMatch(c, @"\bmode switch\b|\bphase layers?\b|\bmaster mode switch\b"))
            {
                LayoutPreference p = make("phase-layers");
                p.Key = "phase-layers";
                p.Value = Regex.IsMatch(c, @"\b(no|without|off|never)\b") ? "off" : "on";
                p.Template = p.Value == "off" ? "No mode switch (no phase layers)." : "A latched switch becomes a mode switch: up = air to air, down = air to ground.";
                prefs.Add(p);
            }

            // Realism.
            if (Regex.IsMatch(c, @"like (in )?the real (aircraft|jets?|cockpit|planes?|thing|hotas)|\brealistic\b|real hotas"))
            {
                LayoutPreference p = make("realism");
                p.Key = "realism";
                p.Value = "1";
                p.Template = "Every button does what the real aircraft's button at that place does.";
                prefs.Add(p);
            }
            else if (Regex.IsMatch(c, @"same (button|function)s? (everywhere|in every (aircraft|plane|game))|group(ed)? by function|consistent"))
            {
                LayoutPreference p = make("realism");
                p.Key = "realism";
                p.Value = "0";
                p.Template = "The same function is on the same button in every aircraft.";
                prefs.Add(p);
            }

            // From scratch.
            if (Regex.IsMatch(c, @"from scratch|start over|everything (new|again)|completely new|all new|re-?lay ?out everything"))
            {
                LayoutPreference p = make("fresh");
                p.Key = "fresh";
                p.Value = "1";
                p.Template = "Every bind is laid out again (your previous controls still count).";
                prefs.Add(p);
            }

            // Keep my layout.
            if (Regex.IsMatch(c, @"keep my (layout|binds|buttons)|(don'?t|do not|never) (move|change) my (binds|buttons|layout)|only (the )?unbound"))
            {
                LayoutPreference p = make("keep");
                p.Key = "keep";
                p.Value = "1";
                p.Template = "Your binds stay; only unbound relations get a place.";
                prefs.Add(p);
            }

            // Priorities: "the F-16 first", "mostly the Hornet".
            Match first = Regex.Match(c, @"(?:mostly|mainly|first of all|main aircraft is|priority( is| on)?|i fly) (?:the )?(?<a>[a-z0-9\-/ ]{2,30}?)(?: first| most|$| and )|(?:the )?(?<b>[a-z0-9\-/]{2,20}) first");
            if (first.Success)
            {
                string name = (first.Groups["a"].Success && first.Groups["a"].Value.Length > 0 ? first.Groups["a"].Value : first.Groups["b"].Value).Trim();
                string plane = FindAircraft(name, planes);
                if (plane.Length > 0)
                {
                    LayoutPreference p = make("priority");
                    p.Key = "priority";
                    p.Value = plane;
                    p.Template = "{0} first: its functions get the best places.";
                    p.Args.Add(plane);
                    prefs.Add(p);
                }
            }

            // Devices: "throttle and left-hand stick", "no pedals", "use the left stick instead of the throttle".
            AddDevices(c, make, prefs);

            // "<function> on (the) <control> (of the <device>)", "twist instead of pedals", "no <function> on <control>".
            AddPlacements(c, make, prefs);

            if (prefs.Count == count)
                return;
        }

        private static void AddCurve(string c, Func<string, LayoutPreference> make, List<LayoutPreference> prefs)
        {
            LayoutPreference p = make("curve");
            List<string> axes = new List<string>();
            List<string> names = new List<string>();
            foreach (string axis in new[] { "pitch", "roll", "yaw|rudder", "throttle", "collective" })
            {
                if (Regex.IsMatch(c, @"\b(" + axis + @")\b"))
                {
                    axes.Add(axis == "yaw|rudder" ? "rudder|space_yaw" : axis == "throttle" ? "throttle\\w*" : axis == "collective" ? "throttle\\w*nonab" : axis);
                    names.Add(axis == "yaw|rudder" ? "yaw" : axis);
                }
            }
            if (axes.Count == 0)
            {
                axes.AddRange(new[] { "pitch", "roll" });
                names.AddRange(new[] { "pitch", "roll" });
            }
            p.Function = "^(" + string.Join("|", axes) + ")$";
            bool deadzone = Regex.IsMatch(c, @"dead ?zones?");
            double amount = Regex.IsMatch(c, @"\b(much|a lot|way|very)\b") ? 2 : Regex.IsMatch(c, @"\b(a bit|slightly|little|a little)\b") ? 0.5 : 1;
            if (deadzone)
            {
                bool smaller = Regex.IsMatch(c, @"\b(smaller|less|lower|no|tiny|reduce)\b");
                p.Value = (smaller ? -0.02 * amount : 0.02 * amount).ToString("0.###", CultureInfo.InvariantCulture);
                p.Key = "deadzone|" + p.Function + "|" + Scope(p);
                p.Template = smaller ? "{0}: smaller dead zone for {1}." : "{0}: bigger dead zone for {1}.";
            }
            else if (Regex.IsMatch(c, @"\blinear\b|no curves?|without curves?"))
            {
                p.Value = "linear";
                p.Key = "curve|" + p.Function + "|" + Scope(p);
                p.Template = "{0}: linear {1} (no curve).";
            }
            else
            {
                bool harder = Regex.IsMatch(c, @"\b(harder|stiffer|steeper|more direct|more sensitive|sharper|less curve|flatter)\b");
                p.Value = ((harder ? -0.1 : 0.15) * amount).ToString("0.###", CultureInfo.InvariantCulture);
                p.Key = "curve|" + p.Function + "|" + Scope(p);
                p.Template = harder ? "{0}: more direct curves for {1} (curvature {2})." : "{0}: softer curves for {1} (curvature +{2}).";
            }
            p.Args.Add(ScopeName(p));
            p.Args.Add(string.Join(", ", names));
            if (!deadzone && p.Value != "linear")
                p.Args.Add(p.Value);
            prefs.Add(p);
        }

        private static void AddFfb(string c, Func<string, LayoutPreference> make, List<LayoutPreference> prefs)
        {
            LayoutPreference p = make("ffb");
            bool shake = Regex.IsMatch(c, @"\bshak(e|ing)\b|\bvibration\b|\brumble\b");
            bool off = Regex.IsMatch(c, @"\b(no|without|off|disable|turn off)\b") && !Regex.IsMatch(c, @"\b(less|softer|weaker)\b");
            bool more = Regex.IsMatch(c, @"\b(more|stronger|harder|heavier|firmer|increase)\b");
            double amount = Regex.IsMatch(c, @"\b(much|a lot|way|very)\b") ? 2 : Regex.IsMatch(c, @"\b(a bit|slightly|little)\b") ? 0.5 : 1;
            if (off)
            {
                p.Value = "off";
                p.Key = "ffb|" + (shake ? "shake" : "force") + "|" + Scope(p);
                p.Template = shake ? "{0}: no force feedback shake." : "{0}: force feedback off.";
            }
            else
            {
                double factor = more ? 1 + 0.3 * amount : 1 - 0.3 * Math.Min(amount, 1.5);
                if (shake)
                    factor = more ? 1 + 0.5 * amount : Math.Max(0.1, 1 - 0.5 * amount);
                p.Value = factor.ToString("0.##", CultureInfo.InvariantCulture);
                p.Key = "ffb|" + (shake ? "shake" : "force") + "|" + Scope(p);
                p.Template = shake ? (more ? "{0}: more force feedback shake (x{1})." : "{0}: less force feedback shake (x{1}).") : (more ? "{0}: stronger force feedback forces (x{1})." : "{0}: softer force feedback forces (x{1}).");
                p.Args.Add(ScopeName(p));
                p.Args.Add(p.Value);
                p.Function = shake ? "shake" : "force";
                prefs.Add(p);
                return;
            }
            p.Function = shake ? "shake" : "force";
            p.Args.Add(ScopeName(p));
            prefs.Add(p);
        }

        private static readonly Regex LeftStickWords = new Regex(@"\b(left hand stick|left stick|left hand|left side stick|second stick|left joystick|stick (on|in) the left)\b", RegexOptions.Compiled);

        private static void AddDevices(string c, Func<string, LayoutPreference> make, List<LayoutPreference> prefs)
        {
            bool left = LeftStickWords.IsMatch(c);
            bool throttle = Regex.IsMatch(c, @"\bthrottle\b") && !Regex.IsMatch(c, @"\bthrottle (axis|curve)\b");
            bool pedals = Regex.IsMatch(c, @"\b(pedals?|rudder pedals)\b");
            bool collective = Regex.IsMatch(c, @"\bcollective\b");
            bool negative = Regex.IsMatch(c, @"\b(no|without|don'?t use|do not use|ignore|leave out|not the)\b");
            bool instead = Regex.IsMatch(c, @"\binstead of\b");
            bool only = Regex.IsMatch(c, @"\bonly\b");
            bool placement = Regex.IsMatch(c, @"\b(on|onto) (the|my|a)\b");

            // "throttle and left-hand stick": both left-hand devices take part (throttle for aircraft, left stick for space).
            if (left && throttle && !instead && !negative)
            {
                LayoutPreference p = make("devices");
                p.Key = "devices|left";
                p.Value = "throttle+left-stick";
                p.Template = "Left hand: the throttle in aircraft and helicopters, the left-hand stick in space games.";
                p.Class = string.Empty;
                prefs.Add(p);
                return;
            }
            // "use the left stick instead of the throttle" / "left stick for everything".
            if (left && (instead && throttle || Regex.IsMatch(c, @"for everything|for all|everywhere")) && !negative)
            {
                LayoutPreference p = make("devices");
                p.Key = "devices|left";
                p.Value = "left-stick";
                p.Template = "Left hand: the left-hand stick in every game (the throttle is left out).";
                p.Class = string.Empty;
                prefs.Add(p);
                return;
            }
            // "for space games use the left stick" (without a function word: the space ships' left hand).
            if (left && !placement && !FunctionWords.Any(f => Regex.IsMatch(c, f.Key)) && !negative)
            {
                LayoutPreference p = make("devices");
                p.Key = "devices|left-space";
                p.Value = "left-stick-space";
                p.Template = p.Class == SetupClasses.Space || p.Class.Length == 0 ? "Space games: translation and the ship's buttons on the left-hand stick." : "{0}: the left-hand stick.";
                if (p.Class.Length > 0 && p.Class != SetupClasses.Space)
                    p.Args.Add(ScopeName(p));
                prefs.Add(p);
            }
            // "no pedals", "without pedals", "ignore the collective".
            if (negative && (pedals || collective) && !Regex.IsMatch(c, @"instead of"))
            {
                LayoutPreference p = make("devices");
                p.Key = "devices|" + (pedals ? "pedals" : "collective");
                p.Value = pedals ? "no-pedals" : "no-collective";
                p.Negative = true;
                p.Template = pedals ? "No pedals: rudder / yaw on the stick's twist axis." : "No collective: the helicopters' collective on the throttle.";
                p.Class = string.Empty;
                prefs.Add(p);
            }
            // "only stick and throttle": every other flight device is left out.
            if (only && !negative && Regex.IsMatch(c, @"\bstick\b") && (throttle || left))
            {
                LayoutPreference p = make("devices");
                p.Key = "devices|only";
                p.Value = "only:" + (throttle ? "throttle" : string.Empty) + (left ? "+left" : string.Empty) + (pedals ? "+pedals" : string.Empty);
                p.Template = "Only the devices you named; the others are left out.";
                p.Class = string.Empty;
                prefs.Add(p);
            }
        }

        private static void AddPlacements(string c, Func<string, LayoutPreference> make, List<LayoutPreference> prefs)
        {
            bool left = LeftStickWords.IsMatch(c);
            // "use the left stick and its twist axis instead of pedals" / "twist for yaw" / "yaw on the twist".
            bool twist = Regex.IsMatch(c, @"\btwist");
            bool insteadOfPedals = Regex.IsMatch(c, @"instead of (the )?(pedals|rudder)|rather than (the )?pedals|no pedals");
            if (twist && (insteadOfPedals || Regex.IsMatch(c, @"\b(yaw|rudder)\b")))
            {
                LayoutPreference p = make("function-on");
                p.Function = @"^(rudder|space_yaw)$";
                p.Controls = @"\.axis\.twist$";
                p.Role = left ? SetupRoles.LeftStick : string.Empty;
                p.Key = "function-on|" + p.Function + "|" + Scope(p);
                p.Template = left ? "{0}: yaw on the left-hand stick's twist axis instead of the pedals." : "{0}: yaw / rudder on the twist axis instead of the pedals.";
                p.Args.Add(ScopeName(p));
                prefs.Add(p);
                return;
            }

            Match on = Regex.Match(c, @"^\s*(?<neg>no\b|not\b|never\b|don'?t put\b)?\s*(?<what>.*?)\b(on|onto|to) (the |my |a |an )?(?<where>.+)$");
            if (!on.Success)
                return;
            string what = on.Groups["what"].Value;
            string where = on.Groups["where"].Value;
            bool negative = on.Groups["neg"].Success && on.Groups["neg"].Value.Length > 0 || Regex.IsMatch(c, @"\b(off|away from)\b");
            foreach (KeyValuePair<string, string[]> function in FunctionWords)
            {
                if (!Regex.IsMatch(what, function.Key))
                    continue;
                KeyValuePair<string, string[]> control = ControlWords.FirstOrDefault(cw => Regex.IsMatch(where, cw.Key));
                string role = Regex.IsMatch(where, @"left hand|left stick") ? SetupRoles.LeftStick
                    : Regex.IsMatch(where, @"\bthrottle\b") ? SetupRoles.Throttle
                    : Regex.IsMatch(where, @"\bcollective\b") ? SetupRoles.Collective
                    : Regex.IsMatch(where, @"\b(main stick|right stick|right hand|stick|cyclic)\b") ? SetupRoles.MainStick
                    : Regex.IsMatch(where, @"\bpedals\b") ? SetupRoles.Pedals : string.Empty;
                if (control.Key == null && role.Length == 0)
                    continue;
                LayoutPreference p = make("function-on");
                p.Function = function.Value[0];
                p.Controls = control.Key == null ? string.Empty : control.Value[0];
                p.Role = role;
                p.Negative = negative;
                p.Key = "function-on|" + p.Function + "|" + Scope(p);
                if (control.Key != null && role.Length > 0)
                    p.Template = negative ? "{0}: {1} never on the {2} of the {3}." : "{0}: {1} on the {2} of the {3}.";
                else
                    p.Template = negative ? "{0}: {1} never on the {2}." : "{0}: {1} on the {2}.";
                p.Args.Add(ScopeName(p));
                p.Args.Add(function.Value[1]);
                p.Args.Add(control.Key != null ? control.Value[1] : RoleName(role));
                if (control.Key != null && role.Length > 0)
                    p.Args.Add(RoleName(role));
                prefs.Add(p);
            }
        }

        private static string RoleName(string role)
        {
            switch (role)
            {
                case SetupRoles.LeftStick: return "left-hand stick";
                case SetupRoles.Throttle: return "throttle";
                case SetupRoles.Collective: return "collective";
                case SetupRoles.Pedals: return "pedals";
                case SetupRoles.MainStick: return "main stick";
                default: return "device";
            }
        }

        private static string GameOf(string c, IDictionary<string, string[]> games)
        {
            if (games == null)
                return string.Empty;
            foreach (KeyValuePair<string, string[]> game in games.OrderByDescending(g => g.Value.Max(n => n.Length)))
            {
                foreach (string name in game.Value.Where(n => n.Length >= 2))
                {
                    if (Regex.IsMatch(c, @"(^|[^a-z0-9])" + Regex.Escape(name.ToLowerInvariant()) + @"($|[^a-z0-9])"))
                        return game.Key;
                }
            }
            return string.Empty;
        }

        private static string FindAircraft(string name, List<string> planes)
        {
            string wanted = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
            if (wanted.Length < 2)
                return string.Empty;
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "hornet", "fa18c" }, { "viper", "f16c" }, { "tomcat", "f14" }, { "warthog", "a10c" }, { "apache", "ah64d" }, { "huey", "uh1h" }, { "eagle", "f15c" },
                { "harrier", "av8b" }, { "phantom", "f4e" }, { "chinook", "ch47f" }, { "hip", "mi8mt" }, { "hind", "mi24p" }, { "shark", "ka50" }, { "blackshark", "ka50" }
            };
            string alias;
            if (aliases.TryGetValue(wanted, out alias))
                wanted = alias;
            Func<string, string> key = p => Regex.Replace(p.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
            string exact = planes.FirstOrDefault(p => key(p) == wanted);
            if (exact != null)
                return exact;
            return planes.Where(p => key(p).StartsWith(wanted, StringComparison.Ordinal)).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
        }

        private static string Scope(LayoutPreference p)
        {
            return (p.Class.Length > 0 ? p.Class : "*") + "|" + (p.Game.Length > 0 ? p.Game : "*");
        }

        // "Helicopters", "Spaceships", "DCS", "Every aircraft" (English; the GUI translates the scope words it knows).
        public static string ScopeName(LayoutPreference p)
        {
            string cls = p.Class.Length > 0 ? SetupClasses.Title(p.Class) : string.Empty;
            if (p.Game.Length > 0)
                return cls.Length > 0 ? cls + " in " + p.Game : p.Game;
            return cls.Length > 0 ? cls : "Every aircraft";
        }

        // A newer preference with the same key replaces the older one; the order of the first mention stays.
        public static List<LayoutPreference> Merge(IEnumerable<LayoutPreference> saved, IEnumerable<LayoutPreference> fresh)
        {
            List<LayoutPreference> list = (saved ?? Enumerable.Empty<LayoutPreference>()).Where(p => p != null).ToList();
            foreach (LayoutPreference p in fresh ?? Enumerable.Empty<LayoutPreference>())
            {
                int index = list.FindIndex(s => s.Key == p.Key);
                if (index >= 0)
                    list[index] = p;
                else
                    list.Add(p);
            }
            return list;
        }

        // ---- compiling into the planner's options ----

        public static LayoutCompiled Compile(IEnumerable<LayoutPreference> preferences, List<SetupDevice> devices)
        {
            LayoutCompiled compiled = new LayoutCompiled();
            devices = devices ?? new List<SetupDevice>();
            SetupRoles.Propose(devices);
            Func<string, SetupDevice> byRole = role => devices.FirstOrDefault(d => d.Role == role);
            Func<string, IEnumerable<SetupDevice>> candidates = kind => devices.Where(d => d.Kind == kind);
            foreach (LayoutPreference p in preferences ?? Enumerable.Empty<LayoutPreference>())
            {
                switch (p.Id)
                {
                    case "devices":
                        CompileDevices(p, devices, compiled);
                        break;
                    case "function-on":
                    {
                        // A strong wish: more than the user's previous control (+4) and the knowledge map's position.
                        SetupRule rule = new SetupRule
                        {
                            Id = p.Negative ? "never-" + Short(p) : Short(p),
                            Functions = p.Function,
                            Class = p.Class,
                            Controls = p.Controls,
                            Role = p.Role,
                            Bonus = p.Negative ? 0 : 10,
                            Forbid = p.Negative
                        };
                        if (p.Role.Length > 0 && byRole(p.Role) == null && !compiled.Roles.ContainsValue(p.Role))
                        {
                            compiled.Notes.Add("No " + RoleName(p.Role) + " in your setup for: " + p.Describe());
                            if (p.Controls.Length == 0)
                                break;
                            rule.Role = string.Empty;
                        }
                        compiled.Rules.Add(rule);
                        // The left stick's twist for yaw: up/down translation leaves the twist.
                        if (!p.Negative && p.Function.Contains("space_yaw") && p.Controls.Contains("twist"))
                        {
                            compiled.Rules.Add(new SetupRule { Id = "strafe-off-twist", Functions = "^space_strafe_ver$", Class = p.Class, Controls = @"\.axis\.twist$", Forbid = true });
                            compiled.Rules.Add(new SetupRule { Id = "strafe-vertical", Functions = "^space_strafe_ver$", Class = p.Class, Controls = @"\.axis\.(slider|ministick\.y|rotary|wheel)", Role = p.Role, Bonus = 1.5 });
                        }
                        break;
                    }
                    case "modifiers":
                        if (p.Value == "few")
                        {
                            compiled.PreferFewer = true;
                        }
                        else
                        {
                            int value;
                            if (int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                            {
                                compiled.Modifiers = Math.Max(0, Math.Min(2, value));
                                compiled.PreferFewer = value == 0;
                            }
                        }
                        break;
                    case "phase-layers":
                        compiled.PhaseLayers = p.Value;
                        break;
                    case "realism":
                        compiled.Realism = p.Value == "1" ? 1.0 : 0.0;
                        break;
                    case "keep":
                        compiled.KeepLayout = true;
                        break;
                    case "fresh":
                        compiled.FreshLayout = true;
                        break;
                    case "priority":
                        compiled.Priorities.Insert(0, new List<string> { p.Value });
                        break;
                    case "curve":
                    {
                        double value;
                        LayoutTuningIntent intent = new LayoutTuningIntent { Axes = p.Function, Class = p.Class, Game = p.Game, Preference = p.Key };
                        if (p.Value == "linear")
                            intent.Linear = true;
                        else if (double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        {
                            if (p.Key.StartsWith("deadzone", StringComparison.Ordinal))
                                intent.DeadzoneDelta = value;
                            else
                                intent.CurveDelta = value;
                        }
                        compiled.Tuning.Add(intent);
                        break;
                    }
                    case "ffb":
                    {
                        double factor;
                        LayoutFfbIntent intent = new LayoutFfbIntent { Class = p.Class, Game = p.Game, Preference = p.Key, Kind = p.Value == "off" ? "off" : p.Function };
                        if (p.Value != "off" && double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out factor))
                            intent.Factor = factor;
                        if (p.Value == "off" && p.Function == "shake")
                        {
                            intent.Kind = "shake";
                            intent.Factor = 0;
                        }
                        compiled.Ffb.Add(intent);
                        break;
                    }
                    case "vjoy":
                        compiled.VJoy.Add(new LayoutVJoyIntent { Class = p.Class, Game = p.Game, Mode = p.Value, Preference = p.Key });
                        break;
                }
            }
            return compiled;
        }

        private static string Short(LayoutPreference p)
        {
            string text = Regex.Replace(p.Function, @"[^a-z0-9]+", "-").Trim('-');
            return (text.Length > 24 ? text.Substring(0, 24) : text) + "-" + Regex.Replace(p.Controls + p.Role, @"[^a-z0-9]+", "-").Trim('-');
        }

        private static void CompileDevices(LayoutPreference p, List<SetupDevice> devices, LayoutCompiled compiled)
        {
            Func<Func<SetupDevice, bool>, SetupDevice> best = filter => devices.Where(filter).OrderByDescending(d => d.Connected).ThenByDescending(d => d.ProfileUses).ThenBy(d => d.Device, StringComparer.Ordinal).FirstOrDefault();
            SetupDevice left = best(d => (d.Kind == "stick" || d.Kind == "hotas" || d.Kind == "base") && SetupRoles.IsLeftHand(d));
            SetupDevice throttle = best(d => d.Kind == "throttle" || d.Kind == "quadrant");
            switch (p.Value)
            {
                case "throttle+left-stick":
                    if (left == null)
                        compiled.Notes.Add("No left-hand stick found: the throttle is the left hand everywhere.");
                    else
                        compiled.Roles[left.Device] = SetupRoles.LeftStick;
                    if (throttle == null)
                        compiled.Notes.Add("No throttle found: the left-hand stick is the left hand everywhere.");
                    else
                        compiled.Roles[throttle.Device] = SetupRoles.Throttle;
                    break;
                case "left-stick":
                    if (left == null)
                    {
                        compiled.Notes.Add("No left-hand stick found.");
                        break;
                    }
                    compiled.Roles[left.Device] = SetupRoles.LeftStick;
                    if (throttle != null)
                        compiled.Roles[throttle.Device] = SetupRoles.Unused;
                    break;
                case "left-stick-space":
                    if (left == null)
                        compiled.Notes.Add("No left-hand stick found: space games use the throttle.");
                    else
                        compiled.Roles[left.Device] = SetupRoles.LeftStick;
                    break;
                case "no-pedals":
                    foreach (SetupDevice pedals in devices.Where(d => d.Kind == "pedals"))
                        compiled.Roles[pedals.Device] = SetupRoles.Unused;
                    compiled.Rules.Add(new SetupRule { Id = "yaw-on-twist", Functions = @"^(rudder|space_yaw)$", Controls = @"\.axis\.twist$", Bonus = 3 });
                    break;
                case "no-collective":
                    foreach (SetupDevice collective in devices.Where(d => d.Kind == "collective"))
                        compiled.Roles[collective.Device] = SetupRoles.Unused;
                    break;
                default:
                    if (p.Value.StartsWith("only:", StringComparison.Ordinal))
                    {
                        bool keepThrottle = p.Value.Contains("throttle"), keepLeft = p.Value.Contains("+left"), keepPedals = p.Value.Contains("+pedals");
                        foreach (SetupDevice device in devices)
                        {
                            bool handDevice = device.Kind == "throttle" || device.Kind == "quadrant" || device.Kind == "pedals" || device.Kind == "collective" || device.Kind == "panel" || device.Kind == "mfd" ||
                                              ((device.Kind == "stick" || device.Kind == "hotas" || device.Kind == "base") && SetupRoles.IsLeftHand(device));
                            if (!handDevice)
                                continue;
                            bool keep = (keepThrottle && device == throttle) || (keepLeft && device == left) || (keepPedals && device.Kind == "pedals");
                            if (!keep)
                                compiled.Roles[device.Device] = SetupRoles.Unused;
                        }
                    }
                    break;
            }
        }
    }
}
