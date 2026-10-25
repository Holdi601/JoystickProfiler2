#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // A model the user may pick, with how likely it is (0..100) and why.
    public sealed class RankedCandidate
    {
        public RefDevice Model;
        public int Score;
        public string Why = string.Empty;

        public override string ToString()
        {
            return (Model == null ? "?" : Model.Id) + " " + Score.ToString(CultureInfo.InvariantCulture);
        }
    }

    // What JoyPro has to ask the user about a connected device before it can show the right picture (docs/
    // device-reference.md, "Which one is it?"):
    //   grip    - a base that takes exchangeable grips and does not say which one is on it;
    //   model   - one USB id for several products and nothing else tells them apart;
    //   unknown - not in the catalog: find it in the catalog, else pick the kind of device.
    public sealed class DeviceQuestion
    {
        public string Kind = string.Empty;
        public RefDevice Base;
        public List<RankedCandidate> Candidates = new List<RankedCandidate>();
        // For "unknown": the device class guessed from the inputs (DeviceClasses).
        public string GuessedClass = string.Empty;

        public string Title
        {
            get
            {
                switch (Kind)
                {
                    case "grip": return "Which grip is on your " + (Base == null ? "base" : ShortName(Base)) + "?";
                    case "model": return "Which one is it?";
                    default: return "Which device is this?";
                }
            }
        }

        private static string ShortName(RefDevice device)
        {
            string name = device.Name;
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name.Substring(0, paren) : name;
        }
    }

    public static class DeviceChoices
    {
        // The grips (or models) ranked by how well they fit what the device reports. Tolerant on purpose: driver
        // software (VPC Configurator, VKBDevCfg, the Rhino configurator) adds buttons through shift layers and remaps
        // them, so more buttons than the grip has - most of all a multiple of it - still fits; hats reported as
        // buttons still fit; a missing axis counts against a grip but does not exclude it.
        public static List<RankedCandidate> Rank(IEnumerable<RefDevice> models, DeviceProbe probe, DeviceCatalog catalog, RefDevice identified)
        {
            string hand = probe == null ? string.Empty : DeviceIdentifier.HandFromName(probe.ProductName + " " + probe.InstanceName);
            List<RankedCandidate> ranked = new List<RankedCandidate>();
            foreach (RefDevice model in (models ?? Enumerable.Empty<RefDevice>()).Where(m => m != null).GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                List<string> why = new List<string>();
                int score = SignatureScore(model, probe, catalog, why);
                if (hand.Length > 0)
                {
                    if (model.Hand == hand)
                    {
                        score += 10;
                        why.Add(hand + " hand");
                    }
                    else if (model.Hand == "left" || model.Hand == "right")
                    {
                        // The device's name gives the hand ("L-VPC ..."): a grip for the other hand hardly ever fits.
                        score -= 40;
                        why.Add("made for the " + model.Hand + " hand");
                    }
                    else
                    {
                        score += 5;
                    }
                }
                else
                {
                    score += 5;
                }
                if (identified != null && string.Equals(identified.Id, model.Id, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                    why.Add("USB id and name");
                }
                // The USB id the grip is known to report (Virpil sets it per grip type) counts; another one counts against it.
                string vidPid = probe == null ? string.Empty : DeviceCatalog.NormalizeVidPid(probe.VidPid);
                if (vidPid.Length > 0 && model.VidPids.Count > 0)
                {
                    if (model.VidPids.Contains(vidPid))
                    {
                        score += 12;
                        why.Add("USB id " + vidPid);
                    }
                    else
                    {
                        score -= 15;
                        why.Add("reports another USB id");
                    }
                }
                // A model with a reference photo is a product on sale (and easier to recognize).
                if (DevicePhotoDb.UsablePhotos(model).Count > 0)
                    score += 3;
                // Home-made grips are the rarest answer.
                if (model.Id.IndexOf("custom", StringComparison.OrdinalIgnoreCase) >= 0 || model.Id.IndexOf("diy", StringComparison.OrdinalIgnoreCase) >= 0)
                    score -= 10;
                if (string.Equals(model.Confidence, "high", StringComparison.OrdinalIgnoreCase))
                    score += 2;
                ranked.Add(new RankedCandidate { Model = model, Score = Math.Max(1, Math.Min(100, score)), Why = string.Join(", ", why) });
            }
            return ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Model.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 0..85 from buttons, hats and axes (the hand and the identification add the rest).
        private static int SignatureScore(RefDevice model, DeviceProbe probe, DeviceCatalog catalog, List<string> why)
        {
            RefDevice baseModel = catalog == null ? null : catalog.BaseOf(model);
            IEnumerable<RefControl> controls = model.AllControls.Concat(baseModel == null ? Enumerable.Empty<RefControl>() : baseModel.AllControls);
            DeviceSignature sig = DeviceSignature.Of(controls.Select(c => c.Input));
            bool known = sig.MaxButton > 0 || sig.Hats > 0 || (sig.Axes != null && sig.Axes.Count > 0);
            if (!known || probe == null)
            {
                why.Add("inputs not on record");
                return 40;
            }
            int score = 0;
            // Buttons 0..40.
            int b = sig.MaxButton, bd = probe.Buttons;
            if (bd < 0 || b == 0)
                score += 20;
            else if (bd == b)
            {
                score += 40;
                why.Add(b.ToString(CultureInfo.InvariantCulture) + " buttons");
            }
            else if (bd > b && bd % b == 0)
            {
                score += 34;
                why.Add(bd.ToString(CultureInfo.InvariantCulture) + " buttons = " + (bd / b).ToString(CultureInfo.InvariantCulture) + " layers of " + b.ToString(CultureInfo.InvariantCulture));
            }
            else if (bd > b)
            {
                score += 15 + (int)Math.Round(17.0 * b / bd);
                why.Add(b.ToString(CultureInfo.InvariantCulture) + " of " + bd.ToString(CultureInfo.InvariantCulture) + " buttons");
            }
            else
            {
                score += (int)Math.Round(10.0 * bd / b);
                why.Add("has " + b.ToString(CultureInfo.InvariantCulture) + " buttons, the device reports " + bd.ToString(CultureInfo.InvariantCulture));
            }
            // Hats 0..20 (some drivers report hats as buttons).
            int h = sig.Hats, hd = probe.Hats;
            if (hd < 0)
                score += 10;
            else if (h == hd)
                score += 20;
            else if (hd > h)
                score += 14;
            else
                score += 8;
            // Axes 0..25.
            if (sig.Axes == null || sig.Axes.Count == 0 || probe.Axes == null)
                score += 12;
            else
            {
                HashSet<string> axes = new HashSet<string>(probe.Axes.Select(a => NormalizeAxis(a)), StringComparer.OrdinalIgnoreCase);
                int present = sig.Axes.Count(a => axes.Contains(NormalizeAxis(a)));
                score += (int)Math.Round(25.0 * present / sig.Axes.Count);
                if (present < sig.Axes.Count)
                    why.Add((sig.Axes.Count - present).ToString(CultureInfo.InvariantCulture) + " axis/axes missing");
            }
            return score;
        }

        private static string NormalizeAxis(string axis)
        {
            string a = (axis ?? string.Empty).Trim().ToUpperInvariant();
            return a.StartsWith("JOY_", StringComparison.Ordinal) ? a : "JOY_" + a;
        }

        // What the user has to confirm about a connected device; null when its model is certain. A grip counts as
        // certain only when the device's name says which grip it is (VKB names the grip, a Virpil base or a Rhino
        // does not), or when the user picked it before (the host passes the remembered choice instead of asking).
        public static DeviceQuestion Question(DeviceMatch match, DeviceProbe probe, DeviceCatalog catalog)
        {
            if (match == null || catalog == null)
                return null;
            if (match.Model == null && match.Choices.Count > 0)
            {
                RefDevice baseModel = match.BaseModel ?? catalog.BaseOf(match.Choices[0]);
                return new DeviceQuestion { Kind = baseModel != null ? "grip" : "model", Base = baseModel, Candidates = Rank(match.Choices, probe, catalog, null) };
            }
            if (match.Model == null)
            {
                return new DeviceQuestion { Kind = "unknown", GuessedClass = DeviceClasses.Guess(probe) };
            }
            // Found only by a part of its name ("... Rudder" is not the Thrustmaster TFRP): a guess, not the model. The
            // device counts as unknown; the guess is offered first.
            if (match.Method == "partial")
            {
                return new DeviceQuestion { Kind = "unknown", GuessedClass = DeviceClasses.Guess(probe), Candidates = Rank(new[] { match.Model }, probe, catalog, null) };
            }
            RefDevice model = match.Model;
            RefDevice basis = match.BaseModel ?? catalog.BaseOf(model);
            if (model.IsGrip && basis != null)
            {
                List<RefDevice> grips = catalog.GripsOf(basis.Id);
                if (!grips.Any(g => string.Equals(g.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
                    grips.Add(model);
                if (grips.Count >= 2 && !NameSaysGrip(model, basis, probe, grips))
                    return new DeviceQuestion { Kind = "grip", Base = basis, Candidates = Rank(grips, probe, catalog, model) };
                return null;
            }
            // One USB id, several products, and the name did not decide (V2 / V3 of pedals ...).
            if (match.Method == "vidpid" && probe != null)
            {
                string vidPid = DeviceCatalog.NormalizeVidPid(probe.VidPid);
                List<RefDevice> same = catalog.Devices.Where(d => d.Kind != "base" && !d.IsGrip && vidPid.Length > 0 && d.VidPids.Contains(vidPid)).ToList();
                if (same.Count >= 2)
                    return new DeviceQuestion { Kind = "model", Candidates = Rank(same, probe, catalog, model) };
            }
            return null;
        }

        // Whether the device's own name tells this grip apart from the other grips of the base: a word of the grip's
        // name that is not in the base's name ("Modern Combat" for the VKB MCG); the exact product name only counts
        // when no other grip and not the base carry it.
        public static bool NameSaysGrip(RefDevice grip, RefDevice basis, DeviceProbe probe, List<RefDevice> grips)
        {
            if (probe == null)
                return false;
            string name = DeviceCatalog.Normalize(probe.ProductName + " " + DeviceProbe.RemoveGuid(probe.InstanceName));
            string product = DeviceCatalog.Normalize(probe.Name);
            bool exactOnlyHere = grip.Names.Any(n => DeviceCatalog.Normalize(n) == product) &&
                                 !basis.Names.Any(n => DeviceCatalog.Normalize(n) == product) &&
                                 !grips.Where(g => !string.Equals(g.Id, grip.Id, StringComparison.OrdinalIgnoreCase)).Any(g => g.Names.Any(n => DeviceCatalog.Normalize(n) == product));
            if (exactOnlyHere)
                return true;
            HashSet<string> baseWords = Words(basis.Name);
            baseWords.UnionWith(Words(basis.Vendor));
            foreach (string word in Words(grip.Name))
            {
                if (word.Length < 3 || baseWords.Contains(word) || Generic.Contains(word))
                    continue;
                if (name.Contains(word))
                    return true;
            }
            return false;
        }

        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "grip", "stick", "joystick", "flightstick", "with", "the", "and", "for", "layout", "configurator", "edition", "classic",
            "buttons", "button", "right", "left", "hand", "vpc", "virpil", "vkb", "vkbsim", "thrustmaster", "winwing", "winctrl", "on",
            "rhino", "base", "adapter", "rev", "version", "new", "old", "discontinued", "constellation"
        };

        private static HashSet<string> Words(string text)
        {
            HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = string.Empty;
            foreach (char c in (text ?? string.Empty).ToLowerInvariant() + " ")
            {
                if (char.IsLetterOrDigit(c))
                {
                    current += c;
                    continue;
                }
                if (current.Length > 0)
                    words.Add(current);
                current = string.Empty;
            }
            return words;
        }
    }
}
