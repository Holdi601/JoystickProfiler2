#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Setup
{
    // The plan in words: "engine setup plan --text" and the wizard's summary.
    public static class SetupPlanText
    {
        public static string Write(SetupPlan plan, int maxNotPlaced)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Setup Plan");
            sb.AppendLine("Games: " + (plan.Games.Count == 0 ? "(none)" : string.Join(", ", plan.Games)));
            sb.AppendLine("Priorities: " + (plan.Priorities.Count == 0 ? "(all equal)" : string.Join(" > ", plan.Priorities.Select(t => string.Join(" = ", t)))));
            sb.AppendLine("Modifiers: wanted " + plan.ModifiersWanted.ToString(CultureInfo.InvariantCulture) + (plan.PreferFewer ? " (prefer fewer)" : string.Empty) +
                          ", used " + plan.Modifiers.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Realism: " + Math.Round(plan.Realism * 100).ToString(CultureInfo.InvariantCulture) + " %");
            sb.AppendLine("Cockpit positions: " + plan.CockpitSource);
            sb.AppendLine("Devices:");
            foreach (SetupDevice device in plan.Devices)
            {
                sb.AppendLine("  - " + SetupRoles.Short(device) + " => " + SetupRoles.Title(device.Role) + (device.Role != device.ProposedRole && device.ProposedRole.Length > 0 ? " (your choice; proposed " + device.ProposedRole + ")" : string.Empty) +
                              (device.Reason.Length > 0 && device.Role == device.ProposedRole ? " - " + device.Reason : string.Empty));
            }
            sb.AppendLine("Rules:");
            foreach (string rule in plan.Rules)
                sb.AppendLine("  - " + rule);
            if (plan.Modifiers.Count > 0)
            {
                sb.AppendLine("Modifiers Created:");
                foreach (SetupPlanModifier modifier in plan.Modifiers)
                    sb.AppendLine("  - " + modifier.Name + " = " + DeviceName(plan, modifier.Device) + " " + modifier.Input + (modifier.Control.Length > 0 ? " (" + modifier.Control + ")" : string.Empty) +
                                  (modifier.Forced ? " [added because the inputs are too few]" : string.Empty) + " - " + modifier.Reason);
            }
            sb.AppendLine("Quality:");
            foreach (SetupQuality quality in plan.Quality)
            {
                sb.AppendLine("  - " + quality.Aircraft + (quality.Game != "DCS" && quality.Game != quality.Aircraft ? " (" + quality.Game + ")" : string.Empty) +
                              (quality.Tier >= 0 ? " [priority " + (quality.Tier + 1).ToString(CultureInfo.InvariantCulture) + "]" : string.Empty) + ": " +
                              quality.Score.ToString("0.0", CultureInfo.InvariantCulture) + " % | placed " + quality.Placed.ToString(CultureInfo.InvariantCulture) + "/" + quality.Total.ToString(CultureInfo.InvariantCulture) +
                              " | essential " + quality.EssentialPlaced.ToString(CultureInfo.InvariantCulture) + "/" + quality.EssentialTotal.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("Assignments:");
            foreach (IGrouping<string, SetupPlanRelation> device in plan.Placed.Where(r => !r.Kept).GroupBy(r => r.Device).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine("  " + DeviceName(plan, device.Key) + ":");
                foreach (SetupPlanRelation relation in device.OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => r.Input, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine("    - " + relation.Input + (relation.Modifier.Length > 0 ? " + " + relation.Modifier : string.Empty) + " (" + relation.Control +
                                  (relation.Label.Length > 0 ? " \"" + relation.Label + "\"" : string.Empty) + ") => " + relation.Name + " | " + relation.Category +
                                  " | " + relation.Reason);
                }
            }
            int kept = plan.Relations.Count(r => r.Kept);
            if (kept > 0)
                sb.AppendLine("Kept Binds: " + kept.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Not Placed: " + plan.NotPlaced.Count.ToString(CultureInfo.InvariantCulture));
            foreach (SetupNotPlaced item in plan.NotPlaced.OrderByDescending(n => n.Importance).Take(maxNotPlaced))
                sb.AppendLine("  - " + item.Relation + " [" + item.Category + "] (" + SetupText.Join(item.Aircraft, 3) + "): " + item.Reason);
            if (plan.NotPlaced.Count > maxNotPlaced)
                sb.AppendLine("  ... " + (plan.NotPlaced.Count - maxNotPlaced).ToString(CultureInfo.InvariantCulture) + " less important ones (see --json)");
            if (plan.Notes.Count > 0)
            {
                sb.AppendLine("Notes:");
                foreach (string note in plan.Notes)
                    sb.AppendLine("  - " + note);
            }
            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in plan.Warnings.Distinct())
                    sb.AppendLine("  - " + warning);
            }
            return sb.ToString();
        }

        public static string DeviceName(SetupPlan plan, string device)
        {
            SetupDevice known = plan.Devices.FirstOrDefault(d => d.Device == device);
            return known != null ? SetupRoles.Short(known) : device;
        }
    }
}
