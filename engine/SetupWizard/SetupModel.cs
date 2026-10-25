#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The Setup wizard (docs/setup-wizard.md): games -> devices and roles -> priorities -> plan -> review. This folder is
// plain C# 7.3 without engine dependencies: the engine runs it (engine setup plan / apply), the GUI links the files to
// read and edit a plan and its tests run the planner on fake devices and a fake cockpit database.
namespace JoyPro.Setup
{
    // One device of the user's setup with the role the wizard gives it.
    public sealed class SetupDevice
    {
        public string Device = string.Empty;      // JoyPro device name ("VKB-Sim Gunfighter ... {GUID}"), the key
        public string Model = string.Empty;       // catalog model name, or what Windows reports
        public string ModelId = string.Empty;     // device catalog id (DB\Devices), empty for unknown devices
        public string Kind = string.Empty;        // catalog kind (stick, throttle, hotas, base, pedals, collective, yoke, quadrant, panel,
                                                  // mfd, buttonbox, controller) or keyboard, virtual, wheel, gamepad, other
        public string Hand = "none";              // right, left, either, none
        public bool Connected = true;
        public bool Generic;                      // not in the catalog: layout from what Windows reports
        public bool ForceFeedback;
        public bool NeedsGrip;                    // a base that takes several grips: the user picks one
        public string VidPid = string.Empty;
        public int Buttons;
        public int Hats;
        public int Axes;
        public int ProfileUses;                   // binds of the profile on this product (the user's own layout)
        public string Role = string.Empty;        // the role the plan uses (proposed, or the user's choice)
        public string ProposedRole = string.Empty;
        public string Reason = string.Empty;      // why this role was proposed
        public List<SetupGripChoice> Grips = new List<SetupGripChoice>();
        public string Grip = string.Empty;        // the grip the user picked (catalog id)
    }

    public sealed class SetupGripChoice
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Image = string.Empty;
    }

    // One physical input of a device: what DirectInput calls it and what it is (device vocabulary, docs/devices.md).
    public sealed class SetupInput
    {
        public string Device = string.Empty;
        public string Input = string.Empty;       // JOY_BTN5, JOY_X, JOY_BTN_POV1_U ...
        public string Control = string.Empty;     // stick.hat.tms.up, throttle.axis.main, panel.toggle.2.up ...
        public string Label = string.Empty;       // printed label of panel switches
        public string Type = "momentary";         // momentary, latched, rotary, analog
        public bool IsAxis;
    }

    // A function of the knowledge map (DB\Functions\slots.json + slot_controls.tsv): "TMS up", "Gear up", "Pitch".
    public sealed class SetupFunction
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Kind = string.Empty;        // cockpit, hotas, axis, lights, mfd-osb, ufc-key, starcitizen, ns430 ...
        public bool IsAxis;
        public string Side = "any";               // stick, throttle, collective, pedals, panel, any
        public List<string> Prefs = new List<string>();   // controls in order of preference ('*' = any part)
        public string PairGroup = string.Empty;   // latched on/off pair (gear:in / gear:out)
        public string PairRole = string.Empty;
        public List<string> Labels = new List<string>();  // panel label keywords
        public string Frequency = "occasional";   // constant, frequent, occasional, rare
        public string Group = string.Empty;       // functions on one hat / 2D stick (tms:up, tdc_axes:x)
        public string MapFunction = string.Empty; // the function of the knowledge map (function-map.json) this slot belongs to
        public string MapCategory = string.Empty; // its category there (flight, engine, weapons, countermeasures, sensors ...)
        public List<string> Phases = new List<string>();  // flight phases (SetupPhases: pilot, tol, nav, a2a, a2g ...)
        public string Crit = string.Empty;        // time criticality: instant, seconds, minutes, idle
    }

    // One game command of one aircraft that does a function (DB\Functions\<Game>.tsv row).
    public sealed class SetupCommand
    {
        public string Function = string.Empty;
        public string Game = "DCS";
        public string Aircraft = string.Empty;    // DCS aircraft, or the game id for games with one config
        public string Action = string.Empty;      // the game's command id
        public string Hotas = string.Empty;       // where it is in the real aircraft: stick, cyclic, throttle, collective, panel ...
        public string Frequency = string.Empty;
        public string Status = string.Empty;      // confirmed, corrected, new, substitute
    }

    public sealed class SetupAircraft
    {
        public string Key = string.Empty;         // catalog name (F-16C_50, StarCitizen)
        public string Game = "DCS";
        public string Type = string.Empty;        // fixed_wing, rotary_wing, spaceship, layer, ground
        public string VariantOf = string.Empty;
        public string Era = string.Empty;
        public string Role = string.Empty;        // fighter, attack, multirole, transport, helicopter_attack ... (aircraft.json)

        // DCS input layers (Default, NS430, voice) serve every aircraft of the game at once.
        public bool SharedLayer { get { return Type == "layer" || Type == "ground"; } }

        public string Class
        {
            get
            {
                switch (Type)
                {
                    case "rotary_wing": return SetupClasses.Rotary;
                    case "spaceship": return SetupClasses.Space;
                    default: return SetupClasses.Fixed;
                }
            }
        }
    }

    public static class SetupClasses
    {
        public const string Fixed = "fixed";
        public const string Rotary = "rotary";
        public const string Space = "space";

        public static string Title(string cls)
        {
            switch (cls)
            {
                case Rotary: return "Helicopters";
                case Space: return "Spaceships";
                default: return "Fixed wing";
            }
        }
    }

    // A command node of a relation.
    public sealed class SetupNode
    {
        public string Game = "DCS";
        public string Action = string.Empty;
        public string Aircraft = string.Empty;
    }

    // A relation the profile already has (its binds are kept unless the user asks to lay them out again).
    public sealed class SetupExistingRelation
    {
        public string Name = string.Empty;
        public bool IsAxis;
        public string Function = string.Empty;    // the function its commands belong to (catalog vote), empty if unknown
        public List<SetupNode> Nodes = new List<SetupNode>();
        public string Device = string.Empty;      // bind, mapped onto a device of the setup (empty = unbound)
        public string Input = string.Empty;
        public string Modifier = string.Empty;    // modifier name of the bind
        public bool OnSetup;                      // the bind is on a device of this setup (else keyboard, vJoy, a missing device)
    }

    public sealed class SetupModifierChoice
    {
        public string Name = string.Empty;
        public string Device = string.Empty;
        public string Input = string.Empty;
        public int Uses;
    }

    public sealed class SetupOptions
    {
        public List<string> Games = new List<string>();
        // Aircraft in order of importance; aircraft in one tier count the same. Unlisted aircraft come after all tiers.
        public List<List<string>> Priorities = new List<List<string>>();
        public int Modifiers;                     // the user's preference: 0 = none, 1, 2, N
        public bool PreferFewer = true;           // use as few of them as possible
        public double Realism = 0.5;              // 0 = group by function, 1 = like the real aircraft's HOTAS
        public int Seed = 1;
        public bool ReplaceExisting;              // lay out the profile's own binds on this setup again
        public int ForcedModifierLimit = 2;       // modifiers JoyPro may add although the user asked for fewer
        // Phase layers on a latched mode switch (docs/auto-assign.md#phase-layers): off, on, auto (when the grips are too
        // few for the functions of one combat phase and the setup has a mode switch).
        public string PhaseLayers = "auto";
        // Preferences in the user's words, read by the phrase engine (docs/layout-with-ai.md): extra rules for the score.
        public List<SetupRule> Rules = new List<SetupRule>();
        // Only the profile's relations, no new relations from the knowledge map (Lay out with AI without "add missing").
        public bool ExistingOnly;
        // With ReplaceExisting: lay out only these bound relations again (null = all); the others keep their binds.
        public HashSet<string> ReplaceOnly;
    }

    // A preference rule: functions (by id pattern, category or phase, of aircraft classes) get a bonus on controls that
    // match a pattern, or are kept off them. Made by the phrase engine (LayoutPhrases).
    public sealed class SetupRule
    {
        public string Id = string.Empty;          // the phrase rule that made it (trim-hat, space-yaw-twist ...)
        public string Functions = string.Empty;   // regex over function ids ("^trim"), empty = all
        public string Category = string.Empty;    // flight, weapons, systems, views, displays; empty = any
        public string Class = string.Empty;       // fixed, rotary, space; empty = any
        public string Controls = string.Empty;    // regex over the control vocabulary (stick.hat., \.axis\.twist$)
        public string Role = string.Empty;        // device role the control must belong to (left-stick ...); empty = any
        public double Bonus;                      // added to the comfort (0..1 scale; the user's previous control is +4)
        public bool Forbid;                       // the matching functions never go on these controls
        public bool Only;                         // the matching functions go only on these controls
    }

    public sealed class SetupRequest
    {
        public List<SetupDevice> Devices = new List<SetupDevice>();
        public List<SetupInput> Inputs = new List<SetupInput>();
        public Dictionary<string, SetupFunction> Functions = new Dictionary<string, SetupFunction>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SetupAircraft> Aircraft = new Dictionary<string, SetupAircraft>(StringComparer.OrdinalIgnoreCase);
        public List<SetupCommand> Commands = new List<SetupCommand>();
        public List<SetupExistingRelation> Existing = new List<SetupExistingRelation>();
        public List<SetupModifierChoice> ExistingModifiers = new List<SetupModifierChoice>();
        // Modifier support per game (engine games support): yes, partial, no. Games not listed: yes.
        public Dictionary<string, string> ModifierSupport = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Commands the user switched off in a relation ("Game|Action|Aircraft"): never added again.
        public HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public ICockpitPositions Cockpits;
        public SetupOptions Options = new SetupOptions();
        public List<string> Warnings = new List<string>();
        public List<string> Notes = new List<string>();
    }

    // ---- The plan ----

    public sealed class SetupPlan
    {
        public int Version = 1;
        public List<string> Games = new List<string>();
        public List<List<string>> Priorities = new List<List<string>>();
        public int ModifiersWanted;
        public bool PreferFewer;
        public double Realism;
        public int Seed;
        public string PhaseLayers = string.Empty;
        public string CockpitSource = string.Empty;
        public List<SetupDevice> Devices = new List<SetupDevice>();
        public List<string> Rules = new List<string>();
        public List<SetupInput> Inputs = new List<SetupInput>();
        public List<SetupPlanModifier> Modifiers = new List<SetupPlanModifier>();
        public List<SetupPlanRelation> Relations = new List<SetupPlanRelation>();
        public List<SetupNotPlaced> NotPlaced = new List<SetupNotPlaced>();
        public List<SetupQuality> Quality = new List<SetupQuality>();
        public List<string> Notes = new List<string>();
        public List<string> Warnings = new List<string>();

        public IEnumerable<SetupPlanRelation> Placed { get { return Relations.Where(r => r.Device.Length > 0 && !r.Removed); } }
    }

    public sealed class SetupPlanRelation
    {
        public string Name = string.Empty;
        public string Function = string.Empty;
        public string FunctionName = string.Empty;
        public string Category = string.Empty;
        public bool IsAxis;
        public bool Existing;                     // the profile already has a relation of this name
        public bool Kept;                         // an existing bind that stays (not part of the new layout)
        public bool Removed;                      // the user removed it in the review
        public List<SetupNode> Nodes = new List<SetupNode>();      // nodes to add (empty for existing relations)
        public List<string> Aircraft = new List<string>();         // its aircraft (input sharing: no aircraft in common)
        public List<string> Overlap = new List<string>();          // aircraft keys it occupies (layers: the whole game)
        public double Importance;
        public string Device = string.Empty;
        public string Input = string.Empty;
        public string Control = string.Empty;
        public string Label = string.Empty;
        public string Modifier = string.Empty;
        public double Score;
        public double RealMatch;                  // 0..1: how much the position is the real aircraft's
        public string Phase = string.Empty;       // a2a / a2g: its function is used only in that combat phase
        public string Reason = string.Empty;
    }

    public sealed class SetupPlanModifier
    {
        public string Name = string.Empty;
        public string Device = string.Empty;
        public string Input = string.Empty;
        public string Control = string.Empty;
        public bool Existing;
        public bool Forced;                       // added although the user asked for fewer
        public bool Switch;                       // a latched mode switch position (a DCS switch modifier): a phase layer
        public string Phase = string.Empty;       // the phase of that layer (a2a, a2g)
        public string Reason = string.Empty;
    }

    public sealed class SetupNotPlaced
    {
        public string Relation = string.Empty;
        public string Function = string.Empty;
        public string Category = string.Empty;
        public double Importance;
        public List<string> Aircraft = new List<string>();
        public string Reason = string.Empty;
    }

    public sealed class SetupQuality
    {
        public string Aircraft = string.Empty;
        public string Game = string.Empty;
        public int Tier = -1;                     // index in the priority list, -1 = not listed
        public double Weight;
        public double Score;                      // 0..100: how close every function is to its best position
        public int Placed;
        public int Total;
        public int EssentialPlaced;               // flight controls, weapons, countermeasures, sensors
        public int EssentialTotal;
    }

    internal static class SetupText
    {
        public static string Num(double value)
        {
            return Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
        }

        public static string Join(IEnumerable<string> items, int max)
        {
            List<string> list = items.Where(i => !string.IsNullOrEmpty(i)).ToList();
            if (list.Count <= max)
                return string.Join(", ", list);
            return string.Join(", ", list.Take(max)) + " +" + (list.Count - max).ToString(CultureInfo.InvariantCulture);
        }
    }
}
