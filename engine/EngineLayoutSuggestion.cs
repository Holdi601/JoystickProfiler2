using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    public sealed class LayoutSuggestResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string CatalogPath { get; set; }
        public List<string> LegacyLayoutDirectories { get; set; }
        public List<string> LegacyOutputDirectories { get; set; }
        public List<string> RequestedDevices { get; set; }
        public int CatalogEntries { get; set; }
        public int CandidateLayouts { get; set; }
        public int AppliedMappings { get; set; }
        public List<LayoutDeviceSuggestion> Suggestions { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public LayoutSuggestResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            CatalogPath = string.Empty;
            LegacyLayoutDirectories = new List<string>();
            LegacyOutputDirectories = new List<string>();
            RequestedDevices = new List<string>();
            CatalogEntries = 0;
            CandidateLayouts = 0;
            AppliedMappings = 0;
            Suggestions = new List<LayoutDeviceSuggestion>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class LayoutDeviceSuggestion
    {
        public string Device { get; set; }
        public string DeviceGuid { get; set; }
        public string TemplateName { get; set; }
        public string LayoutPath { get; set; }
        public string BackdropPath { get; set; }
        public string Source { get; set; }
        public int Score { get; set; }
        public string MatchReason { get; set; }
        public string LayoutMapSpec { get; set; }
        public int MarkerCount { get; set; }
        public int ButtonMarkers { get; set; }
        public int AxisMarkers { get; set; }
        public int PovMarkers { get; set; }
        public int SliderMarkers { get; set; }
        public List<LayoutMarkerSuggestion> Markers { get; set; }
        public List<LayoutTableSuggestion> Tables { get; set; }
        public string Vendor { get; set; }
        public string Model { get; set; }
        public string VidPid { get; set; }
        public string Kind { get; set; }
        public string SearchQuery { get; set; }
        public string SearchUrl { get; set; }

        public LayoutDeviceSuggestion()
        {
            Vendor = string.Empty;
            Model = string.Empty;
            VidPid = string.Empty;
            Kind = string.Empty;
            SearchQuery = string.Empty;
            SearchUrl = string.Empty;
            Device = string.Empty;
            DeviceGuid = string.Empty;
            TemplateName = string.Empty;
            LayoutPath = string.Empty;
            BackdropPath = string.Empty;
            Source = string.Empty;
            Score = 0;
            MatchReason = string.Empty;
            LayoutMapSpec = string.Empty;
            MarkerCount = 0;
            ButtonMarkers = 0;
            AxisMarkers = 0;
            PovMarkers = 0;
            SliderMarkers = 0;
            Markers = new List<LayoutMarkerSuggestion>();
            Tables = new List<LayoutTableSuggestion>();
        }
    }

    public sealed class LayoutMarkerSuggestion
    {
        public string Token { get; set; }
        public string Type { get; set; }
        public string Modifier { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public string Color { get; set; }

        public LayoutMarkerSuggestion()
        {
            Token = string.Empty;
            Type = string.Empty;
            Modifier = string.Empty;
            Color = "#22C55E";
        }
    }

    public sealed class LayoutTableSuggestion
    {
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Columns { get; set; }
        public string Description { get; set; }

        public LayoutTableSuggestion()
        {
            Name = string.Empty;
            Description = string.Empty;
            Columns = 1;
        }
    }

    internal static class EngineLayoutSuggestion
    {
        public static LayoutSuggestResult Suggest(
            List<string> deviceFilters,
            bool includeConnected,
            bool includeProfileDevices,
            List<string> legacyLayoutDirectories,
            List<string> legacyOutputDirectories,
            string catalogPath,
            bool applyToProfile,
            int minScoreToApply,
            string userBackdrop = "")
        {
            LayoutSuggestResult result = new LayoutSuggestResult();
            result.CatalogPath = NormalizePath(catalogPath);
            result.LegacyLayoutDirectories = NormalizeDistinctPaths(legacyLayoutDirectories);
            result.LegacyOutputDirectories = NormalizeDistinctPaths(legacyOutputDirectories);

            if (result.LegacyLayoutDirectories.Count == 0)
                result.LegacyLayoutDirectories = DiscoverDefaultLayoutDirectories();
            else
                AddExistingPath(result.LegacyLayoutDirectories, JoyPro.DeviceReference.DeviceCatalog.ImportedLayoutFolder());
            if (result.LegacyOutputDirectories.Count == 0)
                result.LegacyOutputDirectories = DiscoverDefaultOutputDirectories();

            List<string> discoveredDevices = CollectDevices(deviceFilters, includeConnected, includeProfileDevices, result.Warnings);
            result.RequestedDevices = discoveredDevices;
            if (discoveredDevices.Count == 0)
            {
                result.Errors.Add("No devices available to suggest layouts for. Provide --device/--devices or enable --connected/--profile-devices.");
                return result;
            }

            List<LayoutTemplateCandidate> candidates = BuildTemplateCandidates(
                result.CatalogPath,
                result.LegacyLayoutDirectories,
                result.LegacyOutputDirectories,
                result.Warnings,
                out int catalogEntries);
            result.CatalogEntries = catalogEntries;
            result.CandidateLayouts = candidates.Count;
            if (candidates.Count == 0)
                result.Warnings.Add("No .layout template candidates found. Falling back to auto-generated layout suggestions.");

            if (!string.IsNullOrWhiteSpace(userBackdrop) && discoveredDevices.Count != 1)
            {
                result.Errors.Add("--backdrop needs exactly one device (--device <name>).");
                return result;
            }

            Games.GameDeviceDirectory devices = Games.GameDeviceDirectory.Discover(result.Warnings);
            int applied = 0;
            for (int i = 0; i < discoveredDevices.Count; i++)
            {
                string device = discoveredDevices[i];
                MatchContext context = BuildMatchContext(device, devices);
                LayoutDeviceSuggestion suggestion;
                if (!string.IsNullOrWhiteSpace(userBackdrop))
                {
                    suggestion = BuildUserBackdropSuggestion(context, devices, userBackdrop, result.Warnings);
                }
                else
                {
                    LayoutTemplateMatch best = SelectBestCandidate(context, candidates);
                    bool useFallback = best == null || best.Score < 45;
                    suggestion = useFallback
                        ? BuildBackdropFallbackSuggestion(context, devices, best, result.LegacyOutputDirectories, result.Warnings)
                        : BuildSuggestion(device, context, best, result.Warnings);
                }
                suggestion.Vendor = context.Identity.Vendor;
                suggestion.Model = context.Identity.DisplayName;
                suggestion.VidPid = context.Identity.VidPid;
                suggestion.Kind = context.Identity.Kind;
                suggestion.SearchQuery = context.Identity.SearchQuery;
                suggestion.SearchUrl = context.Identity.SearchUrl;
                result.Suggestions.Add(suggestion);

                if (applyToProfile &&
                    !string.IsNullOrWhiteSpace(suggestion.LayoutPath) &&
                    suggestion.Score >= minScoreToApply)
                {
                    // The template's name is no alias (JoyPro 1 kept it there): device aliases are the user's (EngineDeviceAliases).
                    InternalDataManagement.JoystickFileImages[device] = suggestion.LayoutPath;
                    applied++;
                }
            }

            result.AppliedMappings = applied;
            result.Suggestions = result.Suggestions
                .OrderByDescending(v => v.Score)
                .ThenBy(v => v.Device, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        // The real templates (catalog and the user's layout folders) for these devices; a device without a template that
        // scores at least minScore is left out. Never generates a layout (unlike Suggest).
        public static Dictionary<string, LayoutDeviceSuggestion> Templates(IEnumerable<string> devices, Games.GameDeviceDirectory directory, int minScore, List<string> warnings)
        {
            Dictionary<string, LayoutDeviceSuggestion> found = new Dictionary<string, LayoutDeviceSuggestion>(StringComparer.OrdinalIgnoreCase);
            List<LayoutTemplateCandidate> candidates = BuildTemplateCandidates(string.Empty, DiscoverDefaultLayoutDirectories(), DiscoverDefaultOutputDirectories(), warnings, out int _);
            if (candidates.Count == 0)
                return found;
            foreach (string device in devices)
            {
                MatchContext context = BuildMatchContext(device, directory);
                LayoutTemplateMatch best = SelectBestCandidate(context, candidates);
                if (best != null && best.Score >= minScore)
                    found[device] = BuildSuggestion(device, context, best, warnings);
            }
            return found;
        }

        public static string ToText(LayoutSuggestResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Layout Suggest");
            sb.AppendLine("Catalog: " + (string.IsNullOrWhiteSpace(result.CatalogPath) ? "<default>" : result.CatalogPath));
            sb.AppendLine("Catalog Entries: " + result.CatalogEntries.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Candidate Layouts: " + result.CandidateLayouts.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Requested Devices: " + result.RequestedDevices.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < result.RequestedDevices.Count; i++)
                sb.AppendLine("  - " + result.RequestedDevices[i]);
            sb.AppendLine("Applied Mappings: " + result.AppliedMappings.ToString(CultureInfo.InvariantCulture));

            sb.AppendLine("Suggestions: " + result.Suggestions.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < result.Suggestions.Count; i++)
            {
                LayoutDeviceSuggestion item = result.Suggestions[i];
                sb.AppendLine("  - Device: " + item.Device);
                sb.AppendLine("    Model: " + (string.IsNullOrWhiteSpace(item.Model) ? "<unknown>" : item.Model) +
                              (string.IsNullOrWhiteSpace(item.VidPid) ? string.Empty : " (" + item.VidPid + ")") +
                              (string.IsNullOrWhiteSpace(item.Kind) ? string.Empty : ", " + item.Kind));
                sb.AppendLine("    Template: " + (string.IsNullOrWhiteSpace(item.TemplateName) ? "<none>" : item.TemplateName));
                sb.AppendLine("    Layout: " + (string.IsNullOrWhiteSpace(item.LayoutPath) ? "<none>" : item.LayoutPath));
                sb.AppendLine("    Backdrop: " + (string.IsNullOrWhiteSpace(item.BackdropPath) ? "<none>" : item.BackdropPath));
                sb.AppendLine("    Score: " + item.Score.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("    Match: " + (string.IsNullOrWhiteSpace(item.MatchReason) ? "<none>" : item.MatchReason));
                sb.AppendLine("    Source: " + (string.IsNullOrWhiteSpace(item.Source) ? "<none>" : item.Source));
                sb.AppendLine(
                    "    Markers: total=" + item.MarkerCount.ToString(CultureInfo.InvariantCulture) +
                    ", button=" + item.ButtonMarkers.ToString(CultureInfo.InvariantCulture) +
                    ", axis=" + item.AxisMarkers.ToString(CultureInfo.InvariantCulture) +
                    ", pov=" + item.PovMarkers.ToString(CultureInfo.InvariantCulture) +
                    ", slider=" + item.SliderMarkers.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(item.LayoutMapSpec))
                    sb.AppendLine("    Map: " + item.LayoutMapSpec);
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            return sb.ToString();
        }

        private static List<string> CollectDevices(
            List<string> deviceFilters,
            bool includeConnected,
            bool includeProfileDevices,
            List<string> warnings)
        {
            HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in deviceFilters ?? new List<string>())
            {
                string value = NormalizeJoystickName(raw);
                if (value.Length > 0)
                    devices.Add(value);
            }

            if (devices.Count == 0 && includeConnected)
            {
                foreach (string value in DirectInputDeviceDiscovery.EnumerateConnectedDevices(warnings))
                {
                    string normalized = NormalizeJoystickName(value);
                    if (normalized.Length > 0)
                        devices.Add(normalized);
                }
            }

            if (includeProfileDevices)
            {
                foreach (string value in CollectProfileDevices())
                {
                    string normalized = NormalizeJoystickName(value);
                    if (normalized.Length > 0)
                        devices.Add(normalized);
                }
            }

            return devices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> CollectProfileDevices()
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> pair in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value == null)
                    continue;
                string device = NormalizeJoystickName(pair.Value.Joystick);
                if (device.Length > 0)
                    output.Add(device);
            }

            foreach (KeyValuePair<string, Modifier> pair in InternalDataManagement.AllModifiers ?? new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value == null)
                    continue;
                string device = NormalizeJoystickName(pair.Value.device);
                if (device.Length > 0)
                    output.Add(device);
            }

            foreach (string value in InternalDataManagement.LocalJoysticks ?? new string[0])
            {
                string device = NormalizeJoystickName(value);
                if (device.Length > 0)
                    output.Add(device);
            }

            foreach (KeyValuePair<string, string> pair in InternalDataManagement.JoystickFileImages ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                string device = NormalizeJoystickName(pair.Key);
                if (device.Length > 0)
                    output.Add(device);
            }

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<LayoutTemplateCandidate> BuildTemplateCandidates(
            string catalogPath,
            List<string> layoutDirectories,
            List<string> outputDirectories,
            List<string> warnings,
            out int catalogEntries)
        {
            catalogEntries = 0;
            Dictionary<string, LayoutTemplateCandidate> byPath = new Dictionary<string, LayoutTemplateCandidate>(StringComparer.OrdinalIgnoreCase);
            List<LayoutCatalogEntry> catalog = LoadCatalog(catalogPath, warnings);
            catalogEntries = catalog.Count;

            for (int i = 0; i < catalog.Count; i++)
            {
                LayoutCatalogEntry entry = catalog[i];
                string layoutPath = ResolveTemplatePath(entry.LayoutFile, layoutDirectories);
                if (string.IsNullOrWhiteSpace(layoutPath))
                    continue;

                LayoutTemplateCandidate candidate = GetOrCreateCandidate(byPath, layoutPath);
                if (string.IsNullOrWhiteSpace(candidate.Name))
                    candidate.Name = entry.Name;
                if (string.IsNullOrWhiteSpace(candidate.Source))
                    candidate.Source = "catalog";
                candidate.LayoutPath = layoutPath;
                if (string.IsNullOrWhiteSpace(candidate.BackdropPath) && !string.IsNullOrWhiteSpace(entry.BackdropFile))
                    candidate.BackdropPath = ResolveBackdropPath(entry.BackdropFile, outputDirectories, layoutDirectories);

                AddCandidateAlias(candidate, entry.Name);
                foreach (string alias in entry.Aliases ?? new List<string>())
                    AddCandidateAlias(candidate, alias);
                foreach (string guid in entry.ProductGuids ?? new List<string>())
                    AddGuid(candidate.ProductGuids, guid);
                foreach (string guid in entry.InstanceGuids ?? new List<string>())
                    AddGuid(candidate.InstanceGuids, guid);
                foreach (string vidPid in entry.VidPids ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(vidPid))
                        candidate.VidPids.Add(vidPid.Trim().ToUpperInvariant());
                }
                if (string.IsNullOrWhiteSpace(candidate.Vendor))
                    candidate.Vendor = !string.IsNullOrWhiteSpace(entry.Vendor) ? entry.Vendor.Trim() : DeviceModelIdentity.VendorFromName(entry.Name);
            }

            for (int d = 0; d < layoutDirectories.Count; d++)
            {
                string dir = layoutDirectories[d];
                if (!Directory.Exists(dir))
                    continue;

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.layout", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    warnings.Add("layout suggest: could not scan '" + dir + "': " + ex.Message);
                    continue;
                }

                for (int i = 0; i < files.Length; i++)
                {
                    string path = NormalizePath(files[i]);
                    LayoutTemplateCandidate candidate = GetOrCreateCandidate(byPath, path);
                    if (string.IsNullOrWhiteSpace(candidate.Name))
                        candidate.Name = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(candidate.Source))
                        candidate.Source = "legacy-layout-dir";
                    if (string.IsNullOrWhiteSpace(candidate.LayoutPath))
                        candidate.LayoutPath = path;
                    if (string.IsNullOrWhiteSpace(candidate.Vendor))
                        candidate.Vendor = DeviceModelIdentity.VendorFromName(candidate.Name.Replace('_', ' ').Replace('-', ' '));
                    AddCandidateAlias(candidate, candidate.Name);
                }
            }

            foreach (LayoutTemplateCandidate candidate in byPath.Values)
                LoadTemplateMetadata(candidate, warnings);

            return byPath.Values
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.LayoutPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static LayoutTemplateCandidate GetOrCreateCandidate(Dictionary<string, LayoutTemplateCandidate> byPath, string layoutPath)
        {
            if (!byPath.TryGetValue(layoutPath, out LayoutTemplateCandidate candidate))
            {
                candidate = new LayoutTemplateCandidate();
                candidate.LayoutPath = layoutPath;
                byPath[layoutPath] = candidate;
            }

            return candidate;
        }

        private static Dictionary<string, List<string>> catalogNamesByFile;

        // The names the device layout catalog gives a layout file ("VPC_Panel2.layout"), for telling a layout template's
        // name from a device alias (EngineDeviceAliases).
        internal static List<string> CatalogNamesOfLayoutFile(string layoutFile)
        {
            if (catalogNamesByFile == null)
            {
                Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (LayoutCatalogEntry entry in LoadCatalog(string.Empty, new List<string>()))
                {
                    string file = Path.GetFileName(entry.LayoutFile.Trim());
                    if (!map.TryGetValue(file, out List<string> names))
                        map[file] = names = new List<string>();
                    if (!string.IsNullOrWhiteSpace(entry.Name))
                        names.Add(entry.Name.Trim());
                }
                catalogNamesByFile = map;
            }
            return catalogNamesByFile.TryGetValue((layoutFile ?? string.Empty).Trim(), out List<string> found) ? found : new List<string>();
        }

        private static List<LayoutCatalogEntry> LoadCatalog(string catalogPath, List<string> warnings)
        {
            string path = ResolveCatalogPath(catalogPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new List<LayoutCatalogEntry>();

            try
            {
                string json = File.ReadAllText(path);
                LayoutCatalogDocument doc = EngineSerialization.FromJson<LayoutCatalogDocument>(json);
                if (doc == null || doc.Entries == null)
                    return new List<LayoutCatalogEntry>();
                return doc.Entries
                    .Where(v => v != null && !string.IsNullOrWhiteSpace(v.LayoutFile))
                    .ToList();
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: failed to parse catalog '" + path + "': " + ex.Message);
                return new List<LayoutCatalogEntry>();
            }
        }

        private static string ResolveCatalogPath(string catalogPath)
        {
            string raw = NormalizePath(catalogPath);
            if (!string.IsNullOrWhiteSpace(raw) && File.Exists(raw))
                return raw;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "LayoutCatalog", "device-layout-catalog.json"),
                Path.Combine(Environment.CurrentDirectory, "JoyPro2", "engine", "LayoutCatalog", "device-layout-catalog.json"),
                Path.Combine(Environment.CurrentDirectory, "LayoutCatalog", "device-layout-catalog.json")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string path = NormalizePath(candidates[i]);
                if (File.Exists(path))
                    return path;
            }

            return string.Empty;
        }

        private static List<string> DiscoverDefaultLayoutDirectories()
        {
            List<string> output = new List<string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            AddExistingPath(output, Path.Combine(baseDir, "LayoutTemplates"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "LayoutTemplates"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "LayoutImages"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "..", "..", "LayoutImages"));
            // The user's JoyPro 1 layouts the GUI took over ("Use my old JoyPro layouts...").
            AddExistingPath(output, JoyPro.DeviceReference.DeviceCatalog.ImportedLayoutFolder());
            return NormalizeDistinctPaths(output);
        }

        private static List<string> DiscoverDefaultOutputDirectories()
        {
            List<string> output = new List<string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "JoyProOutput"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "LayoutImages"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "..", "..", "LayoutImages"));
            AddExistingPath(output, Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", "LayoutImages"));
            AddExistingPath(output, Path.Combine(baseDir, "LayoutTemplates"));
            AddExistingPath(output, Path.Combine(baseDir, "..", "..", "..", "..", "..", "LayoutImages"));
            return NormalizeDistinctPaths(output);
        }

        private static void AddExistingPath(List<string> target, string path)
        {
            string normalized = NormalizePath(path);
            if (normalized.Length == 0)
                return;
            if (!Directory.Exists(normalized))
                return;
            if (!target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                target.Add(normalized);
        }

        private static string ResolveTemplatePath(string rawPath, List<string> layoutDirectories)
        {
            string path = (rawPath ?? string.Empty).Trim();
            if (path.Length == 0)
                return string.Empty;

            string normalized = NormalizePath(path);
            if (File.Exists(normalized))
                return normalized;

            for (int i = 0; i < layoutDirectories.Count; i++)
            {
                string candidate = NormalizePath(Path.Combine(layoutDirectories[i], path));
                if (File.Exists(candidate))
                    return candidate;
            }

            return string.Empty;
        }

        private static string ResolveBackdropPath(string rawPath, List<string> outputDirectories, List<string> layoutDirectories)
        {
            string path = (rawPath ?? string.Empty).Trim();
            if (path.Length == 0)
                return string.Empty;

            string normalized = NormalizePath(path);
            if (File.Exists(normalized))
                return normalized;

            for (int i = 0; i < outputDirectories.Count; i++)
            {
                string candidate = NormalizePath(Path.Combine(outputDirectories[i], path));
                if (File.Exists(candidate))
                    return candidate;
            }

            for (int i = 0; i < layoutDirectories.Count; i++)
            {
                string candidate = NormalizePath(Path.Combine(layoutDirectories[i], path));
                if (File.Exists(candidate))
                    return candidate;
            }

            return string.Empty;
        }

        private static void LoadTemplateMetadata(LayoutTemplateCandidate candidate, List<string> warnings)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.LayoutPath))
                return;

            if (!EngineLayoutRenderer.TryLoadLayoutTemplate(candidate.LayoutPath, out LayoutFile layout, out string error))
            {
                warnings.Add("layout suggest: cannot load layout '" + candidate.LayoutPath + "': " + error);
                return;
            }

            try
            {
                candidate.ImageWidth = layout.backup == null ? 0 : layout.backup.Width;
                candidate.ImageHeight = layout.backup == null ? 0 : layout.backup.Height;
                // The device the layout was made for (JoyPro 1 stored "LEFT VPC Stick WarBRD {GUID}"): its name without
                // the GUID is the layout's best alias. The GUID differs on another PC or after a Windows reinstall.
                string madeFor = RemoveGuid(layout.Joystick ?? string.Empty);
                if (madeFor.Length > 0 && !string.Equals(madeFor, "Joystick", StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidateAlias(candidate, madeFor);
                    string madeForVendor = DeviceModelIdentity.VendorFromName(madeFor);
                    if (madeForVendor.Length > 0 && (candidate.Vendor.Length == 0 || candidate.Source == "legacy-layout-dir"))
                        candidate.Vendor = madeForVendor;
                }
                try
                {
                    candidate.Modified = File.GetLastWriteTimeUtc(candidate.LayoutPath);
                }
                catch (Exception)
                {
                    candidate.Modified = DateTime.MinValue;
                }
                candidate.Markers.Clear();
                foreach (KeyValuePair<string, System.Windows.Point> pair in layout.Positions ?? new Dictionary<string, System.Windows.Point>())
                {
                    string token = (pair.Key ?? string.Empty).Trim();
                    if (token.Length == 0)
                        continue;
                    LayoutMarkerSuggestion marker = new LayoutMarkerSuggestion();
                    marker.Token = token;
                    marker.Type = InferMarkerType(token);
                    marker.Modifier = ExtractModifier(token);
                    marker.X = (float)pair.Value.X;
                    marker.Y = (float)pair.Value.Y;
                    marker.Color = ResolveMarkerColor(marker.Type, marker.Modifier);
                    candidate.Markers.Add(marker);
                }

                if (string.IsNullOrWhiteSpace(candidate.BackdropPath) && File.Exists(candidate.LayoutPath))
                    candidate.BackdropPath = candidate.LayoutPath;
            }
            finally
            {
                if (layout != null && layout.backup != null)
                    layout.backup.Dispose();
            }
        }

        private static LayoutDeviceSuggestion BuildSuggestion(string device, MatchContext context, LayoutTemplateMatch match, List<string> warnings)
        {
            LayoutDeviceSuggestion suggestion = new LayoutDeviceSuggestion();
            suggestion.Device = device;
            suggestion.DeviceGuid = context.Guid;

            if (match == null || match.Candidate == null)
            {
                suggestion.Score = 0;
                suggestion.MatchReason = "No matching layout candidate found.";
                return suggestion;
            }

            LayoutTemplateCandidate candidate = match.Candidate;
            suggestion.TemplateName = string.IsNullOrWhiteSpace(candidate.Name)
                ? Path.GetFileNameWithoutExtension(candidate.LayoutPath)
                : candidate.Name;
            suggestion.LayoutPath = candidate.LayoutPath;
            suggestion.BackdropPath = candidate.BackdropPath;
            suggestion.Source = candidate.Source;
            suggestion.Score = match.Score;
            suggestion.MatchReason = match.Reason;
            suggestion.LayoutMapSpec = "--layout-map \"" + device + "=" + candidate.LayoutPath + "\"";
            suggestion.Markers = candidate.Markers
                .OrderBy(v => v.Y)
                .ThenBy(v => v.X)
                .Take(80)
                .ToList();
            suggestion.MarkerCount = candidate.Markers.Count;
            suggestion.ButtonMarkers = candidate.Markers.Count(v => string.Equals(v.Type, "button", StringComparison.OrdinalIgnoreCase));
            suggestion.AxisMarkers = candidate.Markers.Count(v => string.Equals(v.Type, "axis", StringComparison.OrdinalIgnoreCase));
            suggestion.PovMarkers = candidate.Markers.Count(v => string.Equals(v.Type, "pov", StringComparison.OrdinalIgnoreCase));
            suggestion.SliderMarkers = candidate.Markers.Count(v => string.Equals(v.Type, "slider", StringComparison.OrdinalIgnoreCase));
            suggestion.Tables = BuildTableSuggestions(candidate);

            if (string.IsNullOrWhiteSpace(suggestion.LayoutPath) || !File.Exists(suggestion.LayoutPath))
                warnings.Add("layout suggest: best candidate for '" + device + "' had no usable layout file.");

            return suggestion;
        }

        // No layout of this product: a picture of the product found locally (file name with the model words), else a
        // generated placeholder. Pictures of other products are never used; the user can supply one with --backdrop.
        private static LayoutDeviceSuggestion BuildBackdropFallbackSuggestion(
            MatchContext context,
            Games.GameDeviceDirectory devices,
            LayoutTemplateMatch lowConfidenceMatch,
            List<string> outputDirectories,
            List<string> warnings)
        {
            string device = context.Device;
            LayoutDeviceSuggestion suggestion = new LayoutDeviceSuggestion();
            suggestion.Device = device;
            suggestion.DeviceGuid = ExtractGuid(device);

            DeviceAutoProfile profile = BuildAutoProfile(context, devices, warnings);
            string modelName = context.Identity.DisplayName.Length > 0 ? context.Identity.DisplayName : RemoveGuid(device);
            string backdropSource = ResolveBackdropSource(context, lowConfidenceMatch, outputDirectories);
            bool synthetic = false;
            List<LayoutMarkerSuggestion> presetMarkers = null;
            if (backdropSource.Length > 0)
            {
                suggestion.TemplateName = modelName + " (auto markers)";
                suggestion.Source = "fallback-local-picture";
                suggestion.Score = 50;
                suggestion.MatchReason = "No layout of this product yet; found a local picture named after it (" + Path.GetFileName(backdropSource) + ").";
            }
            else if (TryCreateReferenceBackdrop(device, devices, out string referencePath, out presetMarkers, out string referenceModel, warnings))
            {
                // JoyPro's own drawing of the identified model (docs/device-reference.md), markers on its real parts.
                backdropSource = referencePath;
                suggestion.TemplateName = "JoyPro drawing of " + referenceModel;
                suggestion.Source = "reference-schematic";
                suggestion.Score = 70;
                suggestion.MatchReason = "No layout of this product yet; JoyPro's drawing of the " + referenceModel + " with a marker on every input the device catalog knows.";
            }
            else if (TryCreateSyntheticBackdrop(device, profile, out string syntheticPath, warnings))
            {
                backdropSource = syntheticPath;
                synthetic = true;
                suggestion.TemplateName = "Placeholder for " + modelName;
                suggestion.Source = "fallback-placeholder";
                suggestion.Score = 30;
                suggestion.MatchReason = "No picture of " + modelName + " yet. Use 'Search Picture', then 'Use Picture' with the saved file or the image address.";
            }
            else
            {
                suggestion.TemplateName = "Picture Missing";
                suggestion.Source = "fallback";
                suggestion.Score = 10;
                suggestion.MatchReason = "No picture of " + modelName + " found.";
                return suggestion;
            }

            return FinishAutoLayout(suggestion, device, backdropSource, profile, synthetic, warnings, presetMarkers);
        }

        // --backdrop <file or image URL>: the user's picture of the device, with markers from its real inputs.
        private static LayoutDeviceSuggestion BuildUserBackdropSuggestion(MatchContext context, Games.GameDeviceDirectory devices, string userBackdrop, List<string> warnings)
        {
            string device = context.Device;
            LayoutDeviceSuggestion suggestion = new LayoutDeviceSuggestion();
            suggestion.Device = device;
            suggestion.DeviceGuid = ExtractGuid(device);
            string modelName = context.Identity.DisplayName.Length > 0 ? context.Identity.DisplayName : RemoveGuid(device);
            suggestion.TemplateName = modelName;
            suggestion.Source = "user-picture";
            suggestion.Score = 100;
            suggestion.MatchReason = "Picture chosen by the user.";

            string source = userBackdrop.Trim().Trim('"');
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                string cacheDir = ResolveAutoLayoutDirectory(warnings);
                string target = string.IsNullOrWhiteSpace(cacheDir) ? string.Empty : NormalizePath(Path.Combine(cacheDir, BuildAutoLayoutFileName(device) + "_picture.png"));
                if (target.Length == 0 || !TryDownloadBackdropImage(source, target, warnings))
                {
                    suggestion.Score = 0;
                    suggestion.MatchReason = "The picture could not be downloaded (see warnings). Save it and pass the file instead.";
                    return suggestion;
                }
                source = target;
            }
            else if (!File.Exists(source))
            {
                suggestion.Score = 0;
                suggestion.MatchReason = "Picture file not found: " + source;
                return suggestion;
            }

            return FinishAutoLayout(suggestion, device, source, BuildAutoProfile(context, devices, warnings), false, warnings);
        }

        private static LayoutDeviceSuggestion FinishAutoLayout(LayoutDeviceSuggestion suggestion, string device, string backdropSource, DeviceAutoProfile profile, bool synthetic, List<string> warnings, List<LayoutMarkerSuggestion> presetMarkers = null)
        {
            if (!TryGenerateAutoLayout(
                    device,
                    backdropSource,
                    profile,
                    presetMarkers,
                    out string generatedLayoutPath,
                    out string resolvedBackdropPath,
                    out List<LayoutMarkerSuggestion> markers,
                    out int imageWidth,
                    out int imageHeight,
                    warnings))
            {
                suggestion.Score = Math.Min(suggestion.Score, 20);
                suggestion.BackdropPath = NormalizePath(backdropSource);
                suggestion.MatchReason += " The layout could not be generated (see warnings).";
                return suggestion;
            }

            suggestion.LayoutPath = generatedLayoutPath;
            suggestion.BackdropPath = resolvedBackdropPath;
            suggestion.LayoutMapSpec = "--layout-map \"" + device + "=" + generatedLayoutPath + "\"";
            suggestion.Markers = markers
                .OrderBy(v => v.Y)
                .ThenBy(v => v.X)
                .Take(80)
                .ToList();
            suggestion.MarkerCount = markers.Count;
            suggestion.ButtonMarkers = markers.Count(v => string.Equals(v.Type, "button", StringComparison.OrdinalIgnoreCase));
            suggestion.AxisMarkers = markers.Count(v => string.Equals(v.Type, "axis", StringComparison.OrdinalIgnoreCase));
            suggestion.PovMarkers = markers.Count(v => string.Equals(v.Type, "pov", StringComparison.OrdinalIgnoreCase));
            suggestion.SliderMarkers = markers.Count(v => string.Equals(v.Type, "slider", StringComparison.OrdinalIgnoreCase));
            suggestion.Tables = BuildTableSuggestions(
                imageWidth,
                imageHeight,
                hasModifiers: false,
                includeSecondary: profile.Axes > 0 || profile.Povs > 0 || profile.Sliders > 0);
            if (!profile.Exact)
                suggestion.MatchReason += " Markers are guessed from the name; connect the device to get its real buttons and axes.";
            else if (synthetic)
                suggestion.MatchReason += " Markers show the device's real buttons and axes.";
            return suggestion;
        }

        // Real inputs of a connected device; otherwise a guess from the device name.
        private static DeviceAutoProfile BuildAutoProfile(MatchContext context, Games.GameDeviceDirectory devices, List<string> warnings)
        {
            Games.GameDevice connected = devices == null ? null : devices.FindByJoyProName(context.Device);
            if (connected != null && devices.EnsureCapabilities(connected, warnings))
            {
                List<int> slots = connected.AxisSlots.Distinct().ToList();
                DeviceAutoProfile exact = new DeviceAutoProfile(
                    context.Identity.Kind,
                    Math.Min(128, connected.ButtonCount),
                    slots.Count(v => v < 6),
                    connected.PovCount,
                    slots.Count(v => v >= 6));
                exact.AxisSlots = slots;
                exact.Exact = true;
                return exact;
            }
            return InferDeviceAutoProfile(context.Device);
        }

        // A local picture whose file name carries this product's model words (never just the device kind).
        private static string ResolveBackdropSource(MatchContext context, LayoutTemplateMatch lowConfidenceMatch, List<string> outputDirectories)
        {
            if (lowConfidenceMatch != null && lowConfidenceMatch.Candidate != null && lowConfidenceMatch.Score >= 35)
            {
                string candidateBackdrop = NormalizePath(lowConfidenceMatch.Candidate.BackdropPath);
                if (!string.IsNullOrWhiteSpace(candidateBackdrop) && File.Exists(candidateBackdrop))
                    return candidateBackdrop;
            }

            List<string> modelTokens = context.Tokens.Where(t => t.Length > 1 && !GenericTokens.Contains(t)).ToList();
            if (modelTokens.Count == 0)
                return string.Empty;

            string bestPath = string.Empty;
            int bestScore = 0;
            foreach (string dir in outputDirectories ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    continue;

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(v =>
                            v.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            v.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            v.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            v.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace('-', ' ');
                    string fileVendor = DeviceModelIdentity.VendorFromName(fileName);
                    if (fileVendor.Length > 0 && context.Identity.Vendor.Length > 0 && !string.Equals(fileVendor, context.Identity.Vendor, StringComparison.OrdinalIgnoreCase))
                        continue;
                    HashSet<string> fileTokens = SplitTokens(fileName);
                    int score = modelTokens.Count(t => fileTokens.Contains(t));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPath = NormalizePath(file);
                    }
                }
            }

            // At least half of the model words must appear, so one shared word does not pick another product.
            return bestScore * 2 >= modelTokens.Count ? bestPath : string.Empty;
        }

        private static bool TryDownloadBackdropImage(string remoteUrl, string targetPath, List<string> warnings)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(remoteUrl);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
                req.Accept = "image/png,image/jpeg,image/gif,image/bmp,image/*;q=0.8";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    using (Bitmap image = new Bitmap(buffer))
                    {
                        if (image.Width < 200 || image.Height < 150)
                        {
                            warnings.Add("layout suggest: the picture is too small (" + image.Width + "x" + image.Height + "); use a larger one.");
                            return false;
                        }

                        string directory = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        image.Save(JoyPro.Engine.TestSandbox.EnsureWritable(targetPath, "Image save"), System.Drawing.Imaging.ImageFormat.Png);
                        return File.Exists(targetPath);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: picture download failed (" + remoteUrl + "): " + ex.Message +
                             " The address must point to the image itself ('Copy image address' in the browser); WebP images are not supported, save those as PNG/JPG and pass the file.");
                return false;
            }
        }

        // A connected device the device catalog knows by USB id or exact name: JoyPro's drawing of the model as the
        // backdrop, with a marker on each input at its part (a hat direction on its side of the hat).
        private static bool TryCreateReferenceBackdrop(
            string device,
            Games.GameDeviceDirectory devices,
            out string backdropPath,
            out List<LayoutMarkerSuggestion> markers,
            out string modelName,
            List<string> warnings)
        {
            backdropPath = string.Empty;
            markers = null;
            modelName = string.Empty;
            Games.GameDevice connected = devices == null ? null : devices.FindByJoyProName(device);
            if (connected == null)
                return false;
            try
            {
                devices.EnsureCapabilities(connected, warnings);
                JoyPro.DeviceReference.DeviceProbe probe = new JoyPro.DeviceReference.DeviceProbe
                {
                    VidPid = connected.HasVidPid ? Games.GameDeviceDirectory.VidPidKey(connected.VendorId, connected.ProductId) : string.Empty,
                    ProductName = connected.HidProductString ?? string.Empty,
                    InstanceName = connected.InstanceName ?? string.Empty,
                    Buttons = connected.CapabilitiesKnown ? connected.ButtonCount : -1,
                    Hats = connected.CapabilitiesKnown ? connected.PovCount : -1,
                    Axes = connected.CapabilitiesKnown ? (connected.AxisSlots ?? new List<int>()).Where(a => a >= 0 && a <= 7).Select(Games.GameInputTokens.AxisToken).ToList() : null
                };
                JoyPro.DeviceReference.DeviceCatalog catalog = Program.LoadReferenceCatalog();
                JoyPro.DeviceReference.DeviceMatch match = JoyPro.DeviceReference.DeviceIdentifier.Identify(catalog, probe);
                if (match.Model == null || match.Method == "partial" || !match.Model.AllControls.Any(c => c.Input.Length > 0))
                    return false;
                JoyPro.DeviceReference.DeviceSchematic schematic = JoyPro.DeviceReference.SchematicBuilder.Build(match.Model, match.BaseModel);
                string autoLayoutDir = ResolveAutoLayoutDirectory(warnings);
                if (string.IsNullOrWhiteSpace(autoLayoutDir))
                    return false;
                string outputPath = NormalizePath(Path.Combine(autoLayoutDir, BuildAutoLayoutFileName(device) + "_drawing.png"));
                int width, height;
                using (Bitmap image = EngineSchematicBitmap.Render(schematic, 1400, dark: true))
                {
                    image.Save(JoyPro.Engine.TestSandbox.EnsureWritable(outputPath, "Image save"), System.Drawing.Imaging.ImageFormat.Png);
                    width = image.Width;
                    height = image.Height;
                }
                markers = new List<LayoutMarkerSuggestion>();
                foreach (JoyPro.DeviceReference.SchematicInput input in schematic.Markers.SelectMany(m => m.Inputs).Where(i => i.Input.Length > 0))
                {
                    string kind = JoyPro.DeviceReference.DeviceVocabulary.InputKind(input.Input);
                    string type = kind == "hat" ? "pov" : kind == "axis" ? (input.Input.StartsWith("JOY_SLIDER", StringComparison.OrdinalIgnoreCase) ? "slider" : "axis") : "button";
                    if (markers.Any(m => string.Equals(m.Token, input.Input, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    AddAutoMarker(markers, input.Input.ToUpperInvariant(), type, (float)(input.X * width), (float)(input.Y * height));
                }
                backdropPath = outputPath;
                modelName = match.Model.Name;
                return markers.Count > 0;
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: JoyPro's drawing of '" + device + "' failed: " + ex.Message);
                markers = null;
                return false;
            }
        }

        private static bool TryCreateSyntheticBackdrop(
            string device,
            DeviceAutoProfile profile,
            out string backdropPath,
            List<string> warnings)
        {
            backdropPath = string.Empty;
            string autoLayoutDir = ResolveAutoLayoutDirectory(warnings);
            if (string.IsNullOrWhiteSpace(autoLayoutDir))
                return false;

            string safeName = BuildAutoLayoutFileName(device);
            string outputPath = NormalizePath(Path.Combine(autoLayoutDir, safeName + "_backdrop.png"));
            if (string.IsNullOrWhiteSpace(outputPath))
                return false;

            try
            {
                int width = profile.Family.Equals("Panel", StringComparison.OrdinalIgnoreCase) ? 1920 : 1600;
                int height = profile.Family.Equals("Panel", StringComparison.OrdinalIgnoreCase) ? 1080 : 900;

                using (Bitmap image = new Bitmap(width, height))
                using (Graphics g = Graphics.FromImage(image))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    using (LinearGradientBrush background = new LinearGradientBrush(
                        new Rectangle(0, 0, width, height),
                        Color.FromArgb(18, 24, 38),
                        Color.FromArgb(5, 8, 16),
                        110f))
                    {
                        g.FillRectangle(background, 0, 0, width, height);
                    }

                    using (Pen gridPen = new Pen(Color.FromArgb(28, 148, 163, 184), 1f))
                    {
                        for (int x = 0; x < width; x += 80)
                            g.DrawLine(gridPen, x, 0, x, height);
                        for (int y = 0; y < height; y += 80)
                            g.DrawLine(gridPen, 0, y, width, y);
                    }

                    string familyLabel = profile.Family + " Auto Layout";
                    string deviceLabel = RemoveGuid(device);
                    using (Font title = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Point))
                    using (Font subtitle = new Font("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Point))
                    using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(230, 241, 245, 249)))
                    using (SolidBrush subtitleBrush = new SolidBrush(Color.FromArgb(196, 189, 198, 214)))
                    {
                        g.DrawString(familyLabel, title, titleBrush, new PointF(22f, 20f));
                        g.DrawString(deviceLabel, subtitle, subtitleBrush, new PointF(24f, 62f));
                        g.DrawString("Markers are auto-generated. Drag markers in Layout Studio and save as needed.",
                            subtitle,
                            subtitleBrush,
                            new PointF(24f, 88f));
                    }

                    Rectangle deviceBox = new Rectangle(
                        (int)(width * 0.05),
                        (int)(height * 0.20),
                        (int)(width * 0.58),
                        (int)(height * 0.72));
                    using (SolidBrush panelBrush = new SolidBrush(Color.FromArgb(38, 30, 41, 59)))
                    using (Pen panelPen = new Pen(Color.FromArgb(90, 147, 197, 253), 2f))
                    using (GraphicsPath panelPath = CreateRoundedRectPath(deviceBox, 28))
                    {
                        g.FillPath(panelBrush, panelPath);
                        g.DrawPath(panelPen, panelPath);
                    }

                    Rectangle legendBox = new Rectangle(
                        (int)(width * 0.68),
                        (int)(height * 0.04),
                        (int)(width * 0.29),
                        (int)(height * 0.42));
                    using (SolidBrush legendBrush = new SolidBrush(Color.FromArgb(42, 15, 23, 42)))
                    using (Pen legendPen = new Pen(Color.FromArgb(96, 56, 189, 248), 1.6f))
                    using (GraphicsPath legendPath = CreateRoundedRectPath(legendBox, 18))
                    {
                        g.FillPath(legendBrush, legendPath);
                        g.DrawPath(legendPen, legendPath);
                    }

                    image.Save(JoyPro.Engine.TestSandbox.EnsureWritable(outputPath, "Image save"), System.Drawing.Imaging.ImageFormat.Png);
                }
                backdropPath = outputPath;
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: failed to create synthetic backdrop for '" + device + "': " + ex.Message);
                return false;
            }
        }

        private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
        {
            int safeRadius = Math.Max(0, radius);
            int diameter = safeRadius * 2;
            GraphicsPath path = new GraphicsPath();

            if (safeRadius == 0)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static bool TryGenerateAutoLayout(
            string device,
            string backdropSourcePath,
            DeviceAutoProfile profile,
            List<LayoutMarkerSuggestion> presetMarkers,
            out string generatedLayoutPath,
            out string resolvedBackdropPath,
            out List<LayoutMarkerSuggestion> markers,
            out int imageWidth,
            out int imageHeight,
            List<string> warnings)
        {
            generatedLayoutPath = string.Empty;
            resolvedBackdropPath = string.Empty;
            markers = new List<LayoutMarkerSuggestion>();
            imageWidth = 0;
            imageHeight = 0;

            if (!TryLoadBackdropBitmap(backdropSourcePath, out Bitmap backdrop, out resolvedBackdropPath, warnings))
                return false;

            try
            {
                imageWidth = backdrop.Width;
                imageHeight = backdrop.Height;
                markers = presetMarkers ?? BuildAutoMarkerSuggestions(profile, imageWidth, imageHeight);

                string autoLayoutDir = ResolveAutoLayoutDirectory(warnings);
                if (string.IsNullOrWhiteSpace(autoLayoutDir))
                    return false;

                string fileName = BuildAutoLayoutFileName(device);
                generatedLayoutPath = NormalizePath(Path.Combine(autoLayoutDir, fileName + ".layout"));

                LayoutFile layout = new LayoutFile();
                layout.Joystick = RemoveGuid(device);
                layout.Font = "Arial";
                layout.Size = 12;
                layout.KneeboardPostfix = string.Empty;
                layout.Positions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
                layout.backup = (Bitmap)backdrop.Clone();

                for (int i = 0; i < markers.Count; i++)
                {
                    LayoutMarkerSuggestion marker = markers[i];
                    if (marker == null || string.IsNullOrWhiteSpace(marker.Token))
                        continue;
                    layout.Positions[marker.Token] = new System.Windows.Point(marker.X, marker.Y);
                }

                try
                {
                    using (FileStream stream = File.Open(generatedLayoutPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        BinaryFormatter formatter = new BinaryFormatter();
                        formatter.Binder = new LegacyJoyProSerializationBinder();
                        formatter.Serialize(stream, layout);
                    }
                }
                finally
                {
                    if (layout.backup != null)
                        layout.backup.Dispose();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: auto-layout generation failed for '" + device + "': " + ex.Message);
                return false;
            }
            finally
            {
                backdrop.Dispose();
            }

            return File.Exists(generatedLayoutPath);
        }

        private static bool TryLoadBackdropBitmap(
            string sourcePath,
            out Bitmap backdrop,
            out string resolvedBackdropPath,
            List<string> warnings)
        {
            backdrop = null;
            resolvedBackdropPath = string.Empty;
            string fullPath = NormalizePath(sourcePath);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return false;

            try
            {
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext == ".png" || ext == ".bmp" || ext == ".jpg" || ext == ".jpeg")
                {
                    backdrop = new Bitmap(fullPath);
                    resolvedBackdropPath = fullPath;
                    return true;
                }

                if (EngineLayoutRenderer.TryLoadLayoutTemplate(fullPath, out LayoutFile layout, out string error))
                {
                    try
                    {
                        if (layout != null && layout.backup != null)
                        {
                            backdrop = (Bitmap)layout.backup.Clone();
                            resolvedBackdropPath = fullPath;
                            return true;
                        }
                    }
                    finally
                    {
                        if (layout != null && layout.backup != null)
                            layout.backup.Dispose();
                    }
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    warnings.Add("layout suggest: failed to load backdrop source '" + fullPath + "': " + error);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("layout suggest: failed to load backdrop source '" + fullPath + "': " + ex.Message);
            }

            return false;
        }

        private static string ResolveAutoLayoutDirectory(List<string> warnings)
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(global::JoyPro.Logging.JoyProDataFolder.Root(), "AutoLayouts"));
            candidates.Add(Path.Combine(Path.GetTempPath(), "JoyPro2", "AutoLayouts"));

            for (int i = 0; i < candidates.Count; i++)
            {
                string path = NormalizePath(candidates[i]);
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                try
                {
                    Directory.CreateDirectory(path);
                    if (Directory.Exists(path))
                        return path;
                }
                catch
                {
                    // try next
                }
            }

            warnings.Add("layout suggest: failed to create auto layout cache directory.");
            return string.Empty;
        }

        private static string BuildAutoLayoutFileName(string device)
        {
            string guid = ExtractGuid(device).Replace("-", string.Empty).ToLowerInvariant();
            if (guid.Length > 10)
                guid = guid.Substring(0, 10);

            string baseName = RemoveGuid(device);
            StringBuilder sb = new StringBuilder();
            foreach (char c in baseName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (c == ' ' || c == '-' || c == '_')
                {
                    if (sb.Length == 0 || sb[sb.Length - 1] != '_')
                        sb.Append('_');
                }
            }

            string safe = sb.ToString().Trim('_');
            if (safe.Length == 0)
                safe = "device";
            return guid.Length == 0 ? "auto_" + safe : "auto_" + safe + "_" + guid;
        }

        private static List<LayoutMarkerSuggestion> BuildAutoMarkerSuggestions(DeviceAutoProfile profile, int imageWidth, int imageHeight)
        {
            List<LayoutMarkerSuggestion> markers = new List<LayoutMarkerSuggestion>();
            float width = imageWidth <= 0 ? 1600f : imageWidth;
            float height = imageHeight <= 0 ? 900f : imageHeight;

            // Axes and sliders in a compact top strip.
            string[] axisTokens = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ" };
            List<string> axes = profile.AxisSlots != null
                ? profile.AxisSlots.Where(v => v >= 0 && v < 6).OrderBy(v => v).Select(v => axisTokens[v]).ToList()
                : axisTokens.Take(Math.Max(0, Math.Min(profile.Axes, axisTokens.Length))).ToList();
            for (int i = 0; i < axes.Count; i++)
            {
                float x = (float)(width * (0.10 + (0.10 * i)));
                AddAutoMarker(markers, axes[i], "axis", x, (float)(height * 0.08));
            }

            string[] sliderTokens = { "JOY_SLIDER1", "JOY_SLIDER2" };
            List<string> sliders = profile.AxisSlots != null
                ? profile.AxisSlots.Where(v => v == 6 || v == 7).OrderBy(v => v).Select(v => sliderTokens[v - 6]).ToList()
                : sliderTokens.Take(Math.Max(0, Math.Min(profile.Sliders, sliderTokens.Length))).ToList();
            for (int i = 0; i < sliders.Count; i++)
            {
                float x = (float)(width * (0.10 + (0.12 * i)));
                AddAutoMarker(markers, sliders[i], "slider", x, (float)(height * 0.16));
            }

            // POV hats in a directional cluster.
            int povCount = Math.Max(0, Math.Min(profile.Povs, 3));
            string[] povDirs = { "U", "R", "D", "L" };
            for (int pov = 0; pov < povCount; pov++)
            {
                float centerX = (float)(width * (0.16 + (0.14 * pov)));
                float centerY = (float)(height * 0.26);
                float radius = (float)Math.Max(12, Math.Min(width, height) * 0.022);
                for (int d = 0; d < povDirs.Length; d++)
                {
                    string dir = povDirs[d];
                    float x = centerX;
                    float y = centerY;
                    if (dir == "U")
                        y -= radius;
                    else if (dir == "D")
                        y += radius;
                    else if (dir == "L")
                        x -= radius;
                    else if (dir == "R")
                        x += radius;

                    AddAutoMarker(markers, "JOY_BTN_POV" + (pov + 1).ToString(CultureInfo.InvariantCulture) + "_" + dir, "pov", x, y);
                }
            }

            // Main button grid.
            int buttonCount = profile.Exact ? Math.Min(128, profile.Buttons) : Math.Max(8, Math.Min(128, profile.Buttons));
            float left = (float)(width * 0.06);
            float top = (float)(height * 0.34);
            float gridWidth = (float)(width * 0.60);
            float gridHeight = (float)(height * 0.60);
            int cols = Math.Max(6, Math.Min(12, (int)Math.Ceiling(Math.Sqrt(buttonCount * 1.15))));
            int rows = Math.Max(1, (int)Math.Ceiling(buttonCount / (double)cols));
            for (int i = 0; i < buttonCount; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float x = left + ((col + 0.5f) * (gridWidth / cols));
                float y = top + ((row + 0.5f) * (gridHeight / rows));
                AddAutoMarker(
                    markers,
                    "JOY_BTN" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    "button",
                    x,
                    y);
            }

            return markers;
        }

        private static void AddAutoMarker(List<LayoutMarkerSuggestion> target, string token, string type, float x, float y)
        {
            LayoutMarkerSuggestion marker = new LayoutMarkerSuggestion();
            marker.Token = token;
            marker.Type = type;
            marker.Modifier = string.Empty;
            marker.X = x;
            marker.Y = y;
            marker.Color = ResolveMarkerColor(type, string.Empty);
            target.Add(marker);
        }

        private static DeviceAutoProfile InferDeviceAutoProfile(string device)
        {
            string normalized = NormalizeToken(RemoveGuid(device));

            if (normalized.Contains("crosswind") ||
                normalized.Contains("rudder") ||
                normalized.Contains("pedal") ||
                normalized.Contains("t-rudder") ||
                normalized.Contains("proflightpedals") ||
                normalized.Contains("velocityonerudder"))
                return new DeviceAutoProfile("Pedals", 8, 3, 0, 0);

            if (normalized.Contains("xbox") ||
                normalized.Contains("xbone") ||
                normalized.Contains("gamepad") ||
                normalized.Contains("dualshock") ||
                normalized.Contains("dualsense") ||
                normalized.Contains("ps4") ||
                normalized.Contains("ps5"))
                return new DeviceAutoProfile("Gamepad", 16, 6, 1, 0);

            if (normalized.Contains("panel") ||
                normalized.Contains("mfd") ||
                normalized.Contains("buttonbox") ||
                normalized.Contains("switchpanel") ||
                normalized.Contains("multifunctionpanel") ||
                normalized.Contains("radiopanel") ||
                normalized.Contains("autopilotpanel") ||
                normalized.Contains("streamdeck"))
                return new DeviceAutoProfile("Panel", 56, 0, 0, 0);

            if (normalized.Contains("collective") ||
                normalized.Contains("throttle") ||
                normalized.Contains("stecs") ||
                normalized.Contains("omni") ||
                normalized.Contains("quadrant") ||
                normalized.Contains("mt50") ||
                normalized.Contains("t50") ||
                normalized.Contains("twcs") ||
                normalized.Contains("orion") ||
                normalized.Contains("warthogthrottle") ||
                normalized.Contains("x56throttle") ||
                normalized.Contains("x52throttle") ||
                normalized.Contains("chprothrottle") ||
                normalized.Contains("bravothrottle") ||
                normalized.Contains("velocityoneflightuniversalcontrolsystem"))
                return new DeviceAutoProfile("Throttle", 56, 5, 1, 2);

            if (normalized.Contains("gunfighter") ||
                normalized.Contains("gladiator") ||
                normalized.Contains("vpcalpha") ||
                normalized.Contains("constellation") ||
                normalized.Contains("warthogstick") ||
                normalized.Contains("fighterstick") ||
                normalized.Contains("f16ex") ||
                normalized.Contains("f18grip") ||
                normalized.Contains("ursa") ||
                normalized.Contains("warbrd") ||
                normalized.Contains("mongoose") ||
                normalized.Contains("joystick") ||
                normalized.Contains("flightstick") ||
                normalized.Contains("sidestick"))
                return new DeviceAutoProfile("Stick", 96, 6, 1, 0);

            if (normalized.Contains("x56"))
            {
                if (normalized.Contains("throttle"))
                    return new DeviceAutoProfile("Throttle", 68, 5, 1, 2);
                return new DeviceAutoProfile("Stick", 42, 6, 1, 0);
            }

            if (normalized.Contains("warthog"))
            {
                if (normalized.Contains("throttle"))
                    return new DeviceAutoProfile("Throttle", 50, 4, 1, 2);
                return new DeviceAutoProfile("Stick", 42, 6, 1, 0);
            }

            if (normalized.Contains("yoke"))
                return new DeviceAutoProfile("Yoke", 36, 6, 1, 2);

            return new DeviceAutoProfile("Stick", 64, 6, 1, 1);
        }

        private static List<LayoutTableSuggestion> BuildTableSuggestions(LayoutTemplateCandidate candidate)
        {
            int width = candidate.ImageWidth <= 0 ? 1600 : candidate.ImageWidth;
            int height = candidate.ImageHeight <= 0 ? 900 : candidate.ImageHeight;
            bool hasModifiers = candidate.Markers.Any(v => !string.IsNullOrWhiteSpace(v.Modifier));
            bool includeSecondary = candidate.Markers.Count > 24;
            return BuildTableSuggestions(width, height, hasModifiers, includeSecondary);
        }

        private static List<LayoutTableSuggestion> BuildTableSuggestions(
            int imageWidth,
            int imageHeight,
            bool hasModifiers,
            bool includeSecondary)
        {
            List<LayoutTableSuggestion> tables = new List<LayoutTableSuggestion>();
            int width = imageWidth <= 0 ? 1600 : imageWidth;
            int height = imageHeight <= 0 ? 900 : imageHeight;

            LayoutTableSuggestion primary = new LayoutTableSuggestion();
            primary.Name = "Primary Controls";
            primary.X = Math.Max(10, (int)(width * 0.68));
            primary.Y = Math.Max(10, (int)(height * 0.04));
            primary.Width = Math.Max(240, (int)(width * 0.30));
            primary.Height = Math.Max(180, (int)(height * 0.42));
            primary.Columns = 1;
            primary.Description = "Auto legend for primary button/axis labels.";
            tables.Add(primary);

            if (includeSecondary)
            {
                LayoutTableSuggestion secondary = new LayoutTableSuggestion();
                secondary.Name = "Secondary Controls";
                secondary.X = Math.Max(10, (int)(width * 0.68));
                secondary.Y = Math.Max(20, (int)(height * 0.48));
                secondary.Width = Math.Max(240, (int)(width * 0.30));
                secondary.Height = Math.Max(120, (int)(height * 0.24));
                secondary.Columns = 1;
                secondary.Description = "Additional labels (POV / sliders / less-used buttons).";
                tables.Add(secondary);
            }

            if (hasModifiers)
            {
                LayoutTableSuggestion modifier = new LayoutTableSuggestion();
                modifier.Name = "Modifier Layer";
                modifier.X = Math.Max(10, (int)(width * 0.68));
                modifier.Y = Math.Max(20, (int)(height * 0.74));
                modifier.Width = Math.Max(240, (int)(width * 0.30));
                modifier.Height = Math.Max(100, (int)(height * 0.20));
                modifier.Columns = 1;
                modifier.Description = "Modifier-specific labels detected from token prefixes.";
                tables.Add(modifier);
            }

            return tables;
        }

        private static LayoutTemplateMatch SelectBestCandidate(MatchContext context, List<LayoutTemplateCandidate> candidates)
        {
            LayoutTemplateMatch best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                LayoutTemplateCandidate candidate = candidates[i];
                LayoutTemplateMatch match = ScoreCandidate(context, candidate);
                if (match == null)
                    continue;

                if (best == null || match.Score > best.Score)
                {
                    best = match;
                    continue;
                }

                if (match.Score == best.Score)
                {
                    // Several layouts of the same device (the user improved his layout over the years): the newest.
                    int bestAliasCount = best.Candidate.Aliases.Count;
                    int candidateAliasCount = candidate.Aliases.Count;
                    if (candidateAliasCount > bestAliasCount ||
                        (candidateAliasCount == bestAliasCount && candidate.Modified > best.Candidate.Modified))
                        best = match;
                }
            }

            return best;
        }

        // Words that say what kind of device it is, not which product; they never count as a model match.
        private static readonly HashSet<string> GenericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "joystick", "stick", "throttle", "rudder", "pedals", "pedal", "flight", "pro", "sim", "hotas", "controller",
            "game", "gaming", "usb", "hid", "base", "grip", "edition", "series", "panel", "left", "right", "device",
            "system", "the", "and", "for", "with", "family", "layout", "mod", "legacy", "premium", "standard", "v1", "v2",
            "v3", "mk", "combat", "modern", "space", "twist", "arm",
            // vendor names: they say who made it, not which product it is
            "vpc", "virpil", "vkb", "vkbsim", "thrustmaster", "tm", "logitech", "saitek", "winwing", "mfg", "honeycomb",
            "moza", "ch", "vpforce", "turtle", "beach", "madcatz", "microsoft", "sony", "elgato", "bodnar", "leo"
        };

        // A layout only fits a device when it shows the same product (VID:PID, a known alias or the same model words
        // of the same vendor). Layouts of another vendor never match, even for the same kind of device.
        private static LayoutTemplateMatch ScoreCandidate(MatchContext context, LayoutTemplateCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.LayoutPath))
                return null;

            string vendor = context.Identity.Vendor;
            if (vendor.Length > 0 && candidate.Vendor.Length > 0 && !string.Equals(vendor, candidate.Vendor, StringComparison.OrdinalIgnoreCase))
                return null;

            int score = 0;
            List<string> reasons = new List<string>();
            if (context.Guid.Length > 0 && candidate.InstanceGuids.Contains(context.Guid))
            {
                score += 140;
                reasons.Add("this device (instance guid)");
            }
            if (context.Identity.VidPid.Length > 0 && candidate.VidPids.Contains(context.Identity.VidPid))
            {
                score += 150;
                reasons.Add("same product (VID:PID " + context.Identity.VidPid + ")");
            }

            foreach (string alias in candidate.Aliases)
            {
                if (context.Names.Contains(NormalizeToken(alias)) || SameNameOtherHandPrefix(context, alias))
                {
                    score += 120;
                    reasons.Add("known name '" + alias + "'");
                    break;
                }
            }

            List<string> shared = context.Tokens
                .Where(t => !GenericTokens.Contains(t) && t.Length > 1 && candidate.TokenSet.Contains(t))
                .ToList();
            if (shared.Count > 0)
            {
                score += Math.Min(60, shared.Count * 20);
                reasons.Add("model words " + string.Join("/", shared));
            }

            if (score > 0 && vendor.Length > 0 && string.Equals(vendor, candidate.Vendor, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
                reasons.Add("same vendor");
            }

            if (score <= 0)
                return null;

            LayoutTemplateMatch match = new LayoutTemplateMatch();
            match.Candidate = candidate;
            match.Score = Math.Min(200, score);
            match.Reason = string.Join(", ", reasons);
            return match;
        }

        // The same device name with the hand written another way: Virpil firmware named a left device "LEFT VPC Stick
        // WarBRD" and later "L-VPC Stick WarBRD". A different hand ("RIGHT ..." for "L-...") is another device.
        private static bool SameNameOtherHandPrefix(MatchContext context, string alias)
        {
            string aliasHand;
            string aliasRest = StripHandPrefix(alias, out aliasHand);
            if (aliasRest.Length == 0)
                return false;
            string deviceHand;
            string deviceRest = StripHandPrefix(RemoveGuid(context.Device), out deviceHand);
            if (deviceRest.Length == 0 || !string.Equals(NormalizeToken(aliasRest), NormalizeToken(deviceRest), StringComparison.Ordinal))
                return false;
            return aliasHand.Length == 0 || deviceHand.Length == 0 || aliasHand == deviceHand;
        }

        // "LEFT VPC Stick WarBRD" -> "VPC Stick WarBRD" with hand "left"; "L-VPC ..." and "R-VPC ..." alike.
        internal static string StripHandPrefix(string name, out string hand)
        {
            hand = string.Empty;
            string text = (name ?? string.Empty).Trim();
            Match m = Regex.Match(text, @"^(left|right|l|r)(\s*-\s*|\s+|_)(?=\S)", RegexOptions.IgnoreCase);
            if (!m.Success)
                return text;
            hand = char.ToLowerInvariant(m.Groups[1].Value[0]) == 'l' ? "left" : "right";
            return text.Substring(m.Length).Trim();
        }

        private static MatchContext BuildMatchContext(string device, Games.GameDeviceDirectory devices)
        {
            MatchContext context = new MatchContext();
            context.Device = (device ?? string.Empty).Trim();
            context.Guid = ExtractGuid(context.Device);
            context.Identity = DeviceModelIdentity.Resolve(context.Device, devices);
            string instanceName = RemoveGuid(context.Device);
            context.Normalized = NormalizeToken(instanceName);
            foreach (string name in new[] { instanceName, context.Identity.Model, context.Identity.DisplayName })
            {
                string normalized = NormalizeToken(name);
                if (normalized.Length > 0)
                    context.Names.Add(normalized);
                context.Tokens.UnionWith(SplitTokens(name));
            }
            return context;
        }

        private static string ExtractGuid(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int open = text.LastIndexOf('{');
            int close = text.LastIndexOf('}');
            if (open < 0 || close <= open)
                return string.Empty;
            string guid = text.Substring(open + 1, close - open - 1).Trim();
            return guid.ToUpperInvariant();
        }

        private static string RemoveGuid(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int open = text.LastIndexOf('{');
            if (open > 0)
                return text.Substring(0, open).Trim();
            return text;
        }

        private static HashSet<string> SplitTokens(string value)
        {
            HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string normalized = NormalizeToken(RemoveGuid(value));
            if (normalized.Length == 0)
                return tokens;

            StringBuilder current = new StringBuilder();
            string raw = RemoveGuid(value).ToLowerInvariant();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                    continue;
                }
                FlushToken(tokens, current);
            }
            FlushToken(tokens, current);

            if (tokens.Count == 0 && normalized.Length > 0)
                tokens.Add(normalized);
            return tokens;
        }

        private static void FlushToken(HashSet<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
                return;
            string value = current.ToString().Trim();
            if (value.Length > 0)
                tokens.Add(value);
            current.Clear();
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string text = value.Trim().ToLowerInvariant();
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string NormalizeJoystickName(string value)
        {
            string device = (value ?? string.Empty).Trim();
            if (device.Length == 0)
                return string.Empty;
            if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return device;
        }

        private static void AddCandidateAlias(LayoutTemplateCandidate candidate, string alias)
        {
            string value = (alias ?? string.Empty).Trim();
            if (value.Length == 0)
                return;
            if (!candidate.Aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
                candidate.Aliases.Add(value);

            foreach (string token in SplitTokens(value))
                candidate.TokenSet.Add(token);
        }

        private static void AddGuid(HashSet<string> target, string guid)
        {
            string value = (guid ?? string.Empty).Trim();
            if (value.Length == 0)
                return;
            value = value.Trim('{', '}').ToUpperInvariant();
            if (value.Length > 0)
                target.Add(value);
        }

        private static string InferMarkerType(string token)
        {
            string value = (token ?? string.Empty).ToUpperInvariant();
            if (value.Contains("POV") || value.Contains("HAT"))
                return "pov";
            if (value.Contains("SLIDER"))
                return "slider";
            if (value.Contains("JOY_") &&
                (value.Contains("_X") || value.Contains("_Y") || value.Contains("_Z") || value.Contains("RX") || value.Contains("RY") || value.Contains("RZ")))
                return "axis";
            if (value.Contains("AXIS"))
                return "axis";
            return "button";
        }

        private static string ExtractModifier(string token)
        {
            string value = (token ?? string.Empty).Trim();
            int split = value.IndexOf('+');
            if (split <= 0)
                return string.Empty;
            return value.Substring(0, split).Trim();
        }

        private static string ResolveMarkerColor(string markerType, string modifier)
        {
            if (!string.IsNullOrWhiteSpace(modifier))
            {
                int hash = Math.Abs(modifier.ToLowerInvariant().GetHashCode());
                string[] palette =
                {
                    "#38BDF8",
                    "#F59E0B",
                    "#A78BFA",
                    "#34D399",
                    "#FB7185",
                    "#22D3EE"
                };
                return palette[hash % palette.Length];
            }

            string type = (markerType ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "axis")
                return "#38BDF8";
            if (type == "pov")
                return "#A855F7";
            if (type == "slider")
                return "#FBBF24";
            return "#22C55E";
        }

        private static List<string> NormalizeDistinctPaths(IEnumerable<string> paths)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths ?? new List<string>())
            {
                string path = NormalizePath(raw);
                if (path.Length == 0)
                    continue;
                if (Directory.Exists(path))
                    output.Add(path);
            }

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch
            {
                return path.Trim().Trim('"');
            }
        }

        private sealed class LayoutTemplateCandidate
        {
            public string Name;
            public string LayoutPath;
            public string BackdropPath;
            public string Source;
            public List<string> Aliases;
            public HashSet<string> TokenSet;
            public HashSet<string> ProductGuids;
            public HashSet<string> InstanceGuids;
            public HashSet<string> VidPids;
            public string Vendor;
            public List<LayoutMarkerSuggestion> Markers;
            public int ImageWidth;
            public int ImageHeight;
            public DateTime Modified;

            public LayoutTemplateCandidate()
            {
                Name = string.Empty;
                LayoutPath = string.Empty;
                BackdropPath = string.Empty;
                Source = string.Empty;
                Aliases = new List<string>();
                TokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ProductGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                InstanceGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                VidPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Vendor = string.Empty;
                Markers = new List<LayoutMarkerSuggestion>();
                ImageWidth = 0;
                ImageHeight = 0;
            }
        }

        private sealed class MatchContext
        {
            public string Device;
            public string Guid;
            public string Normalized;
            public HashSet<string> Tokens;
            public DeviceModelIdentity Identity;
            public HashSet<string> Names;

            public MatchContext()
            {
                Device = string.Empty;
                Guid = string.Empty;
                Normalized = string.Empty;
                Tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Identity = new DeviceModelIdentity();
                Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private sealed class LayoutTemplateMatch
        {
            public LayoutTemplateCandidate Candidate;
            public int Score;
            public string Reason;

            public LayoutTemplateMatch()
            {
                Candidate = null;
                Score = 0;
                Reason = string.Empty;
            }
        }

        private sealed class DeviceAutoProfile
        {
            public string Family;
            public int Buttons;
            public int Axes;
            public int Povs;
            public int Sliders;
            // DIJOYSTATE2 slots of a connected device (0 X .. 5 Rz, 6/7 sliders); null when guessed from the name.
            public List<int> AxisSlots;
            public bool Exact;

            public DeviceAutoProfile(string family, int buttons, int axes, int povs, int sliders)
            {
                Family = string.IsNullOrWhiteSpace(family) ? "Generic" : family.Trim();
                Buttons = Math.Max(0, buttons);
                Axes = Math.Max(0, axes);
                Povs = Math.Max(0, povs);
                Sliders = Math.Max(0, sliders);
            }
        }

        private sealed class LayoutCatalogDocument
        {
            public string Version { get; set; }
            public List<LayoutCatalogEntry> Entries { get; set; }

            public LayoutCatalogDocument()
            {
                Version = "1";
                Entries = new List<LayoutCatalogEntry>();
            }
        }

        private sealed class LayoutCatalogEntry
        {
            public string Name { get; set; }
            public string LayoutFile { get; set; }
            public string BackdropFile { get; set; }
            public List<string> Aliases { get; set; }
            public List<string> ProductGuids { get; set; }
            public List<string> InstanceGuids { get; set; }
            // USB "VID:PID" (hex) of the products this layout shows, and the vendor ("VKB", "Virpil", ...).
            public List<string> VidPids { get; set; }
            public string Vendor { get; set; }

            public LayoutCatalogEntry()
            {
                VidPids = new List<string>();
                Vendor = string.Empty;
                Name = string.Empty;
                LayoutFile = string.Empty;
                BackdropFile = string.Empty;
                Aliases = new List<string>();
                ProductGuids = new List<string>();
                InstanceGuids = new List<string>();
            }
        }
    }
}
