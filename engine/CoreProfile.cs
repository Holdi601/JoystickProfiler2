using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace JoyPro
{
    [Serializable]
    public sealed class Pr0file
    {
        public Dictionary<string, Relation> Relations;
        public Dictionary<string, Bind> Binds;
        public string LastSelectedDCSInstance;
        public Dictionary<string, string> JoystickAliases;
        public Dictionary<string, string> JoystickFileImages;
        public string JoystickLayoutExport;
        public Dictionary<string, Dictionary<string, string>> PlaneAliases;
        public Dictionary<string, string> JoysticksPGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, string>> modifierNameChanges = new List<KeyValuePair<string, string>>();
        public Dictionary<string, ForceFeedbackS> JoystickFFB = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
        [OptionalField]
        public string DatabaseFingerprint;
        // Force feedback settings per "<device>|<scope>" (Engine.Axis.FfbSettings).
        [OptionalField]
        public Dictionary<string, string> FfbOverrides;
        // Virtual joystick mode (engine vjoy, docs/virtual-joystick.md): games in virtual mode and the vJoy mapping table, as JSON.
        [OptionalField]
        public string VirtualJoystick;

        // Detent calibration per device axis ("<device>|<axis>" -> Engine.Axis.DetentCalibration.Format()), see
        // docs/axis-curves.md "Detent mode".
        [OptionalField]
        public Dictionary<string, string> AxisCalibrations;

        // "Lay out with AI" (docs/layout-with-ai.md): the preferences the user described, as JSON (JoyPro.Setup.LayoutPreference).
        [OptionalField]
        public string LayoutPreferences;

        // Actions the user keeps on in several relations on purpose ("Keep both", engine relations duplicates):
        // "game|id|plane|relation<TAB>relation", the check does not warn about them while exactly these relations have it.
        [OptionalField]
        public List<string> KeptDuplicates;

        // Binds an "Only add" export did not write because the game uses their input for another function, one JSON
        // object per line (ExportSkipMarks); they clear at the next export that writes the bind or finds the input free.
        [OptionalField]
        public List<string> ExportSkipMarks;

        // Games this profile imported from or exported to (EngineSync hooks): the GUI asks "Import first?" before the
        // first export to any other game (engine profile game-state).
        [OptionalField]
        public List<string> SyncedGames;

        // Suggestions the user dismissed ("Never") for a relation, "relation<TAB>game<TAB>id<TAB>plane": they are not
        // proposed again (engine relations suggest, Auto Group). Before 2.5.0 a dismissed suggestion was an action of
        // the relation with no aircraft ticked; such actions move here when the profile is opened (EngineDismissedSuggestions).
        [OptionalField]
        public List<string> DismissedSuggestions;

        public Pr0file()
        {
            Relations = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            Binds = new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
            LastSelectedDCSInstance = string.Empty;
            JoystickAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JoystickFileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JoystickLayoutExport = string.Empty;
            PlaneAliases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            DatabaseFingerprint = string.Empty;
        }
    }

    [Serializable]
    public sealed class MetaSave
    {
        public string lastOpenedLocation = string.Empty;
    }
}
