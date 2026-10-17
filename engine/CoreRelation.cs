using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace JoyPro
{
    [Serializable]
    public sealed class RelationItem
    {
        public string ID;
        public Dictionary<string, bool> AIRCRAFT;
        public DCSInput[] AllInputs;
        public string Game;
        public OtherGameInput[] OtherInputs;

        public RelationItem()
        {
            ID = string.Empty;
            Game = "DCS";
            AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            AllInputs = new DCSInput[0];
            OtherInputs = new OtherGameInput[0];
        }

        public RelationItem(string id, string game, Relation relation)
            : this()
        {
            ID = id ?? string.Empty;
            Game = string.IsNullOrWhiteSpace(game) ? "DCS" : game;
        }

        public RelationItem(string id, string plane, string game)
            : this()
        {
            ID = id ?? string.Empty;
            Game = string.IsNullOrWhiteSpace(game) ? "DCS" : game;
            if (!string.IsNullOrWhiteSpace(plane))
                AIRCRAFT[plane] = true;
        }

        public RelationItem Copy()
        {
            RelationItem copy = new RelationItem();
            copy.ID = ID;
            copy.Game = Game;
            copy.AIRCRAFT = new Dictionary<string, bool>(AIRCRAFT ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            copy.AllInputs = AllInputs == null ? new DCSInput[0] : AllInputs.Select(i => i == null ? null : i.Copy()).ToArray();
            copy.OtherInputs = OtherInputs == null ? new OtherGameInput[0] : OtherInputs.Select(i => i == null ? null : i.Copy()).ToArray();
            return copy;
        }

        public PlaneState GetStateAircraft(string plane)
        {
            if (AIRCRAFT == null)
                AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(plane))
                return PlaneState.ERROR;
            if (!AIRCRAFT.ContainsKey(plane))
                return PlaneState.NOT_EXISTENT;
            return AIRCRAFT[plane] ? PlaneState.ACTIVE : PlaneState.DISABLED;
        }

        public bool SetAircraftActivity(string plane, bool activity)
        {
            if (string.IsNullOrWhiteSpace(plane))
                return false;
            if (AIRCRAFT == null)
                AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            AIRCRAFT[plane] = activity;
            return true;
        }

        public void DeleteAircraftFromActivity(string plane)
        {
            if (string.IsNullOrWhiteSpace(plane) || AIRCRAFT == null)
                return;
            if (AIRCRAFT.ContainsKey(plane))
                AIRCRAFT.Remove(plane);
        }

        public List<string> GetActiveAircraftList()
        {
            if (AIRCRAFT == null || AIRCRAFT.Count == 0)
                return new List<string>();

            return AIRCRAFT
                .Where(kvp => kvp.Value && !string.IsNullOrWhiteSpace(kvp.Key))
                .Select(kvp => kvp.Key)
                .OrderBy(v => v)
                .ToList();
        }
    }

    [Serializable]
    public sealed class Relation
    {
        public string NAME;
        public List<RelationItem> NODES;
        public bool ISAXIS;
        public Bind bind;
        public List<string> Groups;
        public int? elCount = 0;

        public int ElementCount
        {
            get
            {
                if (elCount == null)
                    RecalculateElementCount();
                return elCount ?? 0;
            }
        }

        public Relation()
        {
            NAME = string.Empty;
            NODES = new List<RelationItem>();
            Groups = new List<string>();
        }

        public Relation Copy()
        {
            Relation relation = new Relation();
            relation.NAME = NAME;
            relation.ISAXIS = ISAXIS;
            relation.Groups = Groups == null ? new List<string>() : new List<string>(Groups);
            relation.NODES = NODES == null
                ? new List<RelationItem>()
                : NODES.Where(n => n != null).Select(n => n.Copy()).ToList();
            if (bind != null)
                relation.bind = bind.Copy(relation);
            relation.RecalculateElementCount();
            return relation;
        }

        public void RecalculateElementCount()
        {
            if (NODES == null)
            {
                elCount = 0;
                return;
            }

            int count = 0;
            for (int i = 0; i < NODES.Count; i++)
            {
                RelationItem node = NODES[i];
                if (node == null || node.AIRCRAFT == null)
                    continue;
                count += node.AIRCRAFT.Values.Count(active => active);
            }
            elCount = count;
        }

        public bool AddNode(RelationItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ID))
                return false;
            IncludeRelationItem(item);
            RecalculateElementCount();
            return true;
        }

        public bool AddNode(string id, string game, bool axis, string plane = "", bool relWinAdd = false)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            string normalizedGame = string.IsNullOrWhiteSpace(game) ? "DCS" : game;
            if (NODES == null)
                NODES = new List<RelationItem>();
            if (Groups == null)
                Groups = new List<string>();

            if (NODES.Count == 0)
            {
                ISAXIS = axis;
            }
            else if (ISAXIS != axis)
            {
                return false;
            }

            RelationItem existing = NODES.FirstOrDefault(
                node => node != null &&
                        string.Equals(node.ID, id, StringComparison.Ordinal) &&
                        string.Equals(EmptyToDcs(node.Game), normalizedGame, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new RelationItem(id, normalizedGame, this);
                NODES.Add(existing);
            }

            if (!string.IsNullOrWhiteSpace(plane))
            {
                existing.SetAircraftActivity(plane.Trim(), true);
            }

            // An old id of a game split into versions (MSFS, XPlane) becomes one node per version.
            if (JoyPro.Engine.Games.LegacyGameIds.IsLegacy(normalizedGame))
                JoyPro.Engine.Games.LegacyGameIds.MigrateRelation(this);
            RecalculateElementCount();
            return true;
        }

        public void IncludeRelationItem(RelationItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ID))
                return;

            if (NODES == null)
                NODES = new List<RelationItem>();
            if (item.AIRCRAFT == null)
                item.AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            RelationItem existing = NODES.FirstOrDefault(
                node => node != null &&
                        string.Equals(node.ID, item.ID, StringComparison.Ordinal) &&
                        string.Equals(EmptyToDcs(node.Game), EmptyToDcs(item.Game), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                NODES.Add(item);
                if (JoyPro.Engine.Games.LegacyGameIds.IsLegacy(item.Game))
                    JoyPro.Engine.Games.LegacyGameIds.MigrateRelation(this);
                RecalculateElementCount();
                return;
            }

            foreach (KeyValuePair<string, bool> plane in item.AIRCRAFT)
            {
                if (string.IsNullOrWhiteSpace(plane.Key))
                    continue;
                if (plane.Value)
                    existing.SetAircraftActivity(plane.Key, true);
            }

            RecalculateElementCount();
        }

        private static string EmptyToDcs(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "DCS" : value;
        }
    }

    [Serializable]
    public sealed class Bind
    {
        public Relation Rl;
        public string Joystick;
        public string JAxis;
        public string JButton;
        public string Reformer_depr;
        public bool? Inverted;
        public bool? Slider;
        public double Deadzone;
        public List<double> Curvature;
        public double SaturationX;
        public double SaturationY;
        public List<string> AllReformers;
        public string AdditionalImportInfo;
        public string AliasJoystick;
        public string JoyBackup = string.Empty;
        public bool doubleTap = false;
        // Axis tuning per scope ("game:IL2Korea", "plane:DCS:F-16C_50", "family:il2") as AxisTuning.Format strings;
        // the fields above are the base every scope without its own tuning inherits (see Engine.Axis.BindTuning).
        [OptionalField]
        public Dictionary<string, string> TuningOverrides;

        public static readonly bool Inverted_Default = false;
        public static readonly bool Slider_Default = false;
        public static readonly List<double> Curvature_Default = new List<double> { 0.0 };
        public static readonly double SaturationX_Default = 1.0;
        public static readonly double SaturationY_Default = 1.0;
        public static readonly double Deadzone_Default = 0.0;

        public Bind()
        {
            Joystick = string.Empty;
            JAxis = string.Empty;
            JButton = string.Empty;
            Reformer_depr = string.Empty;
            Inverted = Inverted_Default;
            Slider = Slider_Default;
            Deadzone = Deadzone_Default;
            Curvature = new List<double>(Curvature_Default);
            SaturationX = SaturationX_Default;
            SaturationY = SaturationY_Default;
            AllReformers = new List<string>();
            AdditionalImportInfo = string.Empty;
            AliasJoystick = string.Empty;
        }

        public Bind(Relation relation)
            : this()
        {
            Rl = relation;
            if (relation != null)
                relation.bind = this;
        }

        public Bind Copy(Relation relation)
        {
            Bind bind = new Bind(relation);
            bind.Joystick = Joystick ?? string.Empty;
            bind.JoyBackup = bind.Joystick;
            bind.JAxis = JAxis ?? string.Empty;
            bind.JButton = JButton ?? string.Empty;
            bind.Reformer_depr = Reformer_depr ?? string.Empty;
            bind.Inverted = Inverted;
            bind.Slider = Slider;
            bind.Deadzone = Deadzone;
            bind.Curvature = Curvature == null || Curvature.Count == 0
                ? new List<double> { 0.0 }
                : new List<double>(Curvature);
            bind.SaturationX = SaturationX;
            bind.SaturationY = SaturationY;
            bind.AllReformers = AllReformers == null ? new List<string>() : new List<string>(AllReformers);
            bind.AdditionalImportInfo = AdditionalImportInfo ?? string.Empty;
            bind.AliasJoystick = AliasJoystick ?? string.Empty;
            bind.doubleTap = doubleTap;
            bind.TuningOverrides = TuningOverrides == null ? null : new Dictionary<string, string>(TuningOverrides, StringComparer.OrdinalIgnoreCase);
            return bind;
        }

        public bool ReformerInBind(string reformerName)
        {
            if (string.IsNullOrWhiteSpace(reformerName) || AllReformers == null)
                return false;

            for (int i = 0; i < AllReformers.Count; i++)
            {
                string reformer = AllReformers[i];
                if (string.IsNullOrWhiteSpace(reformer))
                    continue;

                string[] parts = reformer.Split('§');
                if (parts.Length > 0 && string.Equals(parts[0], reformerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
