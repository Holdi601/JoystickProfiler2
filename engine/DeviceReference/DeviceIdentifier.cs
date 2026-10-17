#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // What Windows tells about a connected device: USB VID:PID, the HID product string, the DirectInput instance name
    // and (when known) its buttons, hats and axes. Buttons / Hats below 0 mean unknown.
    public sealed class DeviceProbe
    {
        public string VidPid = string.Empty;
        public string ProductName = string.Empty;
        public string InstanceName = string.Empty;
        public int Buttons = -1;
        public int Hats = -1;
        // DirectInput axis tokens (JOY_X ... JOY_SLIDER2); null = unknown.
        public List<string> Axes;

        public string Name
        {
            get
            {
                string product = (ProductName ?? string.Empty).Trim();
                return product.Length > 0 ? product : RemoveGuid(InstanceName);
            }
        }

        public static string RemoveGuid(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int open = text.LastIndexOf('{');
            return open > 0 && text.EndsWith("}", StringComparison.Ordinal) ? text.Substring(0, open).Trim() : text;
        }
    }

    public sealed class DeviceMatch
    {
        // The model (a grip when the base takes several and one was told apart); null = not in the catalog.
        public RefDevice Model;
        public RefDevice BaseModel;
        // Several grips fit the base and none could be told apart: the user picks one (best fit first). With Model set
        // (the stock grip of the base, shown) these are the alternatives.
        public List<RefDevice> Choices = new List<RefDevice>();
        // How it was found, in words ("USB id 231D:0125 and product name", "button count 29 of 3 grips").
        public string Reason = string.Empty;
        // exact (USB id and name), vidpid, name, signature (grip told apart by its inputs), partial (name contains),
        // choice (grip needed), none.
        public string Method = "none";

        public bool Found { get { return Model != null; } }
    }

    // Finds the catalog model of a device: USB VID:PID first, then the exact HID name; grips on a base that reports one
    // USB id for several grips are told apart by an exact product name, the hand ("L-VPC ..."), and the inputs the
    // device reports (highest button, axes, hats). Mirrors the engine's roster (EngineDeviceKb.BuildRoster).
    public static class DeviceIdentifier
    {
        public static DeviceMatch Identify(DeviceCatalog catalog, DeviceProbe probe)
        {
            DeviceMatch match = new DeviceMatch();
            if (catalog == null || probe == null)
                return match;
            List<RefDevice> kb = catalog.Devices;
            string vidPid = DeviceCatalog.NormalizeVidPid(probe.VidPid);
            string name = DeviceCatalog.Normalize(probe.Name);
            string instance = DeviceCatalog.Normalize(DeviceProbe.RemoveGuid(probe.InstanceName));

            List<RefDevice> byId = vidPid.Length > 0 ? kb.Where(k => k.VidPids.Contains(vidPid)).ToList() : new List<RefDevice>();
            List<RefDevice> byName = kb.Where(k => k.Names.Any(n => NameEquals(n, name, instance))).ToList();
            List<RefDevice> candidates;
            string method;
            if (byId.Count > 0 && byName.Count > 0 && byId.Intersect(byName).Any())
            {
                candidates = byId.Intersect(byName).Concat(byId.Where(k => k.IsGrip || k.Kind == "base")).Distinct().ToList();
                method = "exact";
            }
            else if (byId.Count > 0)
            {
                candidates = byId;
                method = "vidpid";
            }
            else if (byName.Count > 0)
            {
                candidates = byName;
                method = "name";
            }
            else
            {
                candidates = kb.Where(k => k.Kind != "base" && k.Names.Concat(new[] { k.Name }).Select(DeviceCatalog.Normalize)
                        .Any(n => n.Length >= 6 && name.Length >= 6 && (name.Contains(n) || n.Contains(name))))
                    .OrderByDescending(k => k.Names.Concat(new[] { k.Name }).Select(DeviceCatalog.Normalize).Max(n => name.Contains(n) ? n.Length : 0))
                    .Take(1).ToList();
                method = candidates.Count > 0 ? "partial" : "none";
            }
            if (candidates.Count == 0)
            {
                match.Reason = "not in the device catalog";
                return match;
            }

            List<RefDevice> grips = candidates.Where(c => c.IsGrip).ToList();
            foreach (RefDevice b in candidates.Where(c => c.Kind == "base"))
                grips.AddRange(kb.Where(k => string.Equals(k.Base, b.Id, StringComparison.OrdinalIgnoreCase) && !grips.Contains(k)));

            List<RefDevice> named = grips.Where(g => g.Names.Any(n => DeviceCatalog.Normalize(n) == name && name.Length > 0)).ToList();
            if (named.Count == 1)
                grips = named;
            string hand = HandFromName(probe.ProductName + " " + probe.InstanceName);
            if (hand.Length > 0 && grips.Any(g => g.Hand == hand))
                grips = grips.Where(g => g.Hand == hand || g.Hand == "none" || g.Hand == "either" || g.Hand == "both" || g.Hand.Length == 0).ToList();

            // The stock product of a base that also takes other grips (Thrustmaster's Warthog stick on its base, which
            // also takes the F/A-18C and F-16C grips with the same USB id and name): shown first, the grips offered.
            List<RefDevice> stock = candidates.Where(c => !c.IsGrip && c.Kind != "base" && c.Names.Any(n => DeviceCatalog.Normalize(n) == name && name.Length > 0)).ToList();
            if (stock.Count == 0 && method == "vidpid")
                stock = candidates.Where(c => !c.IsGrip && c.Kind != "base").ToList();
            if (stock.Count == 1 && grips.Count > 0)
            {
                List<RefDevice> all = stock.Concat(grips).Distinct().ToList();
                List<RefDevice> fitting = BestBySignature(all, probe, catalog);
                RefDevice shown = fitting.Count == 1 ? fitting[0] : stock[0];
                match.Model = shown;
                match.BaseModel = catalog.BaseOf(shown);
                match.Method = fitting.Count == 1 ? "signature" : method;
                match.Reason = fitting.Count == 1 ? "told apart from " + all.Count + " models by the inputs it reports (" + Describe(probe) + ")"
                    : Reason(method, vidPid) + "; " + grips.Count + " other grip(s) report the same, pick yours if it is one of them";
                match.Choices = fitting.Count == 1 ? new List<RefDevice>() : all;
                return match;
            }
            if (grips.Count > 1)
            {
                List<RefDevice> fitting = BestBySignature(grips, probe, catalog);
                if (fitting.Count == 1)
                {
                    match.Model = fitting[0];
                    match.BaseModel = catalog.BaseOf(fitting[0]);
                    match.Method = "signature";
                    match.Reason = "told apart from " + grips.Count + " grips by the inputs it reports (" + Describe(probe) + ")";
                    return match;
                }
                match.Choices = (fitting.Count > 1 ? fitting.Concat(grips.Except(fitting)) : grips).Distinct().ToList();
                match.BaseModel = candidates.FirstOrDefault(c => c.Kind == "base") ?? catalog.BaseOf(grips[0]);
                match.Method = "choice";
                match.Reason = grips.Count + " grips fit this base; pick the one on it";
                return match;
            }
            if (grips.Count == 1)
            {
                match.Model = grips[0];
                match.BaseModel = catalog.BaseOf(grips[0]);
                match.Method = named.Count == 1 ? "exact" : method;
                match.Reason = Reason(match.Method, vidPid);
                return match;
            }

            // Several models with one USB id (V2/V3 of a product): exact product name, inputs, then confidence decide.
            List<RefDevice> ranked = candidates
                .OrderByDescending(c => c.Names.Any(n => DeviceCatalog.Normalize(n) == name && name.Length > 0) ? 1 : 0)
                .ThenByDescending(c => SignatureScore(c, probe, catalog))
                .ThenByDescending(c => ConfidenceRank(c.Confidence))
                .ThenByDescending(c => c.Controls.Count(x => x.Input.Length > 0))
                .ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            match.Model = ranked[0];
            match.BaseModel = catalog.BaseOf(ranked[0]);
            match.Method = method;
            match.Reason = Reason(method, vidPid) + (ranked.Count > 1 ? " (" + ranked.Count + " models share it; best fit)" : string.Empty);
            return match;
        }

        private static string Reason(string method, string vidPid)
        {
            switch (method)
            {
                case "exact": return "USB id " + vidPid + " and product name";
                case "vidpid": return "USB id " + vidPid;
                case "name": return "product name";
                case "partial": return "part of the product name";
                default: return method;
            }
        }

        private static string Describe(DeviceProbe probe)
        {
            List<string> parts = new List<string>();
            if (probe.Buttons >= 0)
                parts.Add(probe.Buttons + " buttons");
            if (probe.Hats >= 0)
                parts.Add(probe.Hats + " hats");
            if (probe.Axes != null)
                parts.Add(probe.Axes.Count + " axes");
            return string.Join(", ", parts);
        }

        private static bool NameEquals(string catalogName, string name, string instance)
        {
            string n = DeviceCatalog.Normalize(catalogName);
            return n.Length > 0 && (n == name || n == instance);
        }

        // The grips whose inputs fit what the device reports, the closest first: a grip fits when its highest button
        // (with the base's own buttons) is not above the device's buttons, its hats not above the device's hats and its
        // axes all present. Closest = fewest buttons the device has beyond the grip's. Only grips with known numbers
        // can be told apart; when the closest is not unique all fitting ones are returned.
        public static List<RefDevice> BestBySignature(List<RefDevice> grips, DeviceProbe probe, DeviceCatalog catalog)
        {
            if (probe == null || (probe.Buttons < 0 && probe.Axes == null && probe.Hats < 0))
                return new List<RefDevice>();
            List<KeyValuePair<RefDevice, int>> fits = new List<KeyValuePair<RefDevice, int>>();
            foreach (RefDevice grip in grips)
            {
                RefDevice baseModel = catalog == null ? null : catalog.BaseOf(grip);
                IEnumerable<RefControl> controls = grip.AllControls.Concat(baseModel == null ? Enumerable.Empty<RefControl>() : baseModel.AllControls);
                if (!controls.Any(c => c.Input.Length > 0))
                    continue;
                int distance;
                if (Fits(DeviceSignature.Of(controls.Select(c => c.Input)), probe, out distance))
                    fits.Add(new KeyValuePair<RefDevice, int>(grip, distance));
            }
            if (fits.Count == 0)
                return new List<RefDevice>();
            int best = fits.Min(f => f.Value);
            List<RefDevice> closest = fits.Where(f => f.Value == best).Select(f => f.Key).ToList();
            return closest.Count == 1 ? closest : fits.OrderBy(f => f.Value).Select(f => f.Key).ToList();
        }

        // Fit of one model to the probe for ranking (higher = better; 0 = unknown, negative = does not fit).
        public static int SignatureScore(RefDevice model, DeviceProbe probe, DeviceCatalog catalog)
        {
            RefDevice baseModel = catalog == null ? null : catalog.BaseOf(model);
            IEnumerable<RefControl> controls = model.AllControls.Concat(baseModel == null ? Enumerable.Empty<RefControl>() : baseModel.AllControls);
            if (!controls.Any(c => c.Input.Length > 0) || probe == null)
                return 0;
            int distance;
            return Fits(DeviceSignature.Of(controls.Select(c => c.Input)), probe, out distance) ? 1000 - Math.Min(distance, 999) : -1;
        }

        public static bool Fits(DeviceSignature signature, DeviceProbe probe, out int distance)
        {
            distance = 0;
            if (probe.Buttons >= 0)
            {
                if (signature.MaxButton > probe.Buttons)
                    return false;
                distance += probe.Buttons - signature.MaxButton;
            }
            if (probe.Hats >= 0)
            {
                if (signature.Hats > probe.Hats)
                    return false;
                distance += (probe.Hats - signature.Hats) * 2;
            }
            if (probe.Axes != null && signature.Axes != null)
            {
                HashSet<string> axes = new HashSet<string>(probe.Axes.Select(a => (a ?? string.Empty).ToUpperInvariant()));
                if (signature.Axes.Any(a => !axes.Contains(a)))
                    return false;
                distance += axes.Count - signature.Axes.Count;
            }
            return true;
        }

        public static string HandFromName(string name)
        {
            string n = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (Regex.IsMatch(n, @"^(l-|l\s)|\bleft\b"))
                return "left";
            if (Regex.IsMatch(n, @"^(r-|r\s)|\bright\b"))
                return "right";
            return string.Empty;
        }

        private static int ConfidenceRank(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant())
            {
                case "high": return 3;
                case "medium": return 2;
                case "low": return 1;
                default: return 0;
            }
        }
    }
}
