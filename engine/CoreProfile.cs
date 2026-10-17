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
