#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // Whether a model's picture places every input it has (docs/device-reference.md, "Complete devices"): every part of
    // the catalog entry with an input (a button, each hat with its directions and press, an axis, a mini-stick, a slider,
    // an encoder, a toe brake ...) has a position on the best picture JoyPro ships for it, or is listed there as not a
    // physical control ("not physical: ..."; VKB / Virpil logical buttons, shift layers). The best picture is the one
    // with the most placed parts: JoyPro's own drawing first.
    public sealed class DeviceCompleteness
    {
        public const string NotPhysical = "not physical";

        public string Model = string.Empty;
        public string Picture = string.Empty;
        public int Parts;
        public int Placed;
        public List<string> Missing = new List<string>();
        public List<string> Virtual = new List<string>();

        public bool Complete { get { return Parts > 0 && Missing.Count == 0; } }

        public static DeviceCompleteness Of(RefDevice model, RefDevice baseModel, IEnumerable<DeviceLayoutSet> sets)
        {
            DeviceSchematic schematic = SchematicBuilder.Build(model, baseModel);
            List<string> parts = schematic.Markers.Where(m => m.HasInputs).Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            DeviceCompleteness best = null;
            foreach (DeviceLayoutSet set in sets.Where(s => string.Equals(s.Model, model.Id, StringComparison.OrdinalIgnoreCase)))
            {
                DeviceCompleteness c = new DeviceCompleteness { Model = model.Id, Picture = System.IO.Path.GetFileName(set.File), Parts = parts.Count };
                foreach (string part in parts)
                {
                    string reason;
                    if (set.Markers.ContainsKey(part))
                        c.Placed++;
                    else if (set.Unplaced.TryGetValue(part, out reason) && reason.StartsWith(NotPhysical, StringComparison.OrdinalIgnoreCase))
                        c.Virtual.Add(part);
                    else
                        c.Missing.Add(part);
                }
                if (best == null || c.Missing.Count < best.Missing.Count ||
                    (c.Missing.Count == best.Missing.Count && set.PhotoKind == "lineart"))
                    best = c;
            }
            return best ?? new DeviceCompleteness { Model = model.Id, Parts = parts.Count, Missing = parts };
        }
    }
}
