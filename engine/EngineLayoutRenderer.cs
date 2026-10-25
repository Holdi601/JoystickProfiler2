using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace JoyPro.Engine
{
    public sealed class LayoutRenderResult
    {
        public string ExportPath { get; set; }
        public List<string> RequestedJoysticks { get; set; }
        public List<string> RenderedJoysticks { get; set; }
        public Dictionary<string, string> JoystickLayouts { get; set; }
        public List<string> GameFilters { get; set; }
        public List<string> PlaneFilters { get; set; }
        public List<string> Games { get; set; }
        public int PlaneTargetCount { get; set; }
        public int PlaneImagesWritten { get; set; }
        public int OverviewImagesWritten { get; set; }
        // Pages with all devices of one aircraft (or of the overview) under each other (--combine true).
        public int CombinedImagesWritten { get; set; }
        public int TotalImagesWritten
        {
            get { return PlaneImagesWritten + OverviewImagesWritten + CombinedImagesWritten; }
        }
        public List<string> WrittenFiles { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public LayoutRenderResult()
        {
            ExportPath = string.Empty;
            RequestedJoysticks = new List<string>();
            RenderedJoysticks = new List<string>();
            JoystickLayouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            GameFilters = new List<string>();
            PlaneFilters = new List<string>();
            Games = new List<string>();
            PlaneTargetCount = 0;
            PlaneImagesWritten = 0;
            OverviewImagesWritten = 0;
            WrittenFiles = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class LayoutVisualOptions
    {
        public bool DrawMarkers { get; set; }
        public bool DrawLegendTable { get; set; }
        public int MarkerSize { get; set; }
        public int LegendTableWidth { get; set; }
        public int LegendMaxRows { get; set; }
        public float LegendFontSize { get; set; }
        public string ModifierColorCsv { get; set; }
        public string ButtonSymbol { get; set; }
        public string AxisSymbol { get; set; }
        public string PovSymbol { get; set; }
        public string SliderSymbol { get; set; }
        // Label text style; empty/0 = the style stored in the .layout file. Colors: #RRGGBB, #AARRGGBB or "none".
        public string FontFamily { get; set; }
        public float FontSize { get; set; }
        public string TextColor { get; set; }
        public string OutlineColor { get; set; }
        public string LabelBackground { get; set; }
        // Callout sheets (tables and lines): dark (JoyPro 1's grey page) or light (white page), the printed width in
        // pixels (0 = the sheet's own size), and one page per aircraft with all devices (--combine).
        public bool LightStyle { get; set; }
        public int PrintWidth { get; set; }
        public bool Combine { get; set; }
        // The sheet style of the run (--sheet-style and the single colour options; null = none given): the tables, the
        // leader lines, the markers and the page. A device's own style (its sheet's "style", a .layout's ".style.json")
        // replaces it for that device (WithDeviceStyle).
        public JoyPro.DeviceReference.SheetStyle Style { get; set; }

        // The options for a device with a style of its own: its colours, font and modifier colours over these.
        public LayoutVisualOptions WithDeviceStyle(JoyPro.DeviceReference.SheetStyle style)
        {
            if (style == null)
                return this;
            LayoutVisualOptions copy = (LayoutVisualOptions)MemberwiseClone();
            copy.Style = style;
            if (style.FontFamily.Length > 0)
                copy.FontFamily = style.FontFamily;
            if (style.FontSize > 0)
                copy.FontSize = (float)style.FontSize;
            if (style.TextColor.Length > 0)
                copy.TextColor = style.TextColor;
            if (style.OutlineColor.Length > 0)
                copy.OutlineColor = style.OutlineColor;
            if (style.LabelBackground.Length > 0)
                copy.LabelBackground = style.LabelBackground;
            if (style.Modifiers.Count > 0)
                copy.ModifierColorCsv = style.ModifierCsv;
            copy.LightStyle = !style.IsDarkPage(!LightStyle);
            return copy;
        }

        public LayoutVisualOptions()
        {
            PrintWidth = 3508;
            FontFamily = string.Empty;
            FontSize = 0f;
            TextColor = string.Empty;
            OutlineColor = string.Empty;
            LabelBackground = string.Empty;
            DrawMarkers = false;
            DrawLegendTable = false;
            MarkerSize = 10;
            LegendTableWidth = 460;
            LegendMaxRows = 20;
            LegendFontSize = 11f;
            ModifierColorCsv = string.Empty;
            ButtonSymbol = "circle";
            AxisSymbol = "diamond";
            PovSymbol = "triangle";
            SliderSymbol = "bar";
        }
    }

    internal static class EngineLayoutRenderer
    {
        public static bool TryParseLayoutMapSpecs(
            List<string> rawSpecs,
            out Dictionary<string, string> map,
            out string error)
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;
            foreach (string rawSpec in rawSpecs ?? new List<string>())
            {
                string spec = (rawSpec ?? string.Empty).Trim();
                if (spec.Length == 0)
                    continue;

                int split = spec.IndexOf('=');
                if (split <= 0 || split >= spec.Length - 1)
                {
                    error = "Invalid --layout-map value '" + spec + "'. Use: --layout-map \"<joystick>=<path.layout>\".";
                    return false;
                }

                string joystick = spec.Substring(0, split).Trim();
                string path = spec.Substring(split + 1).Trim().Trim('"');
                if (joystick.Length == 0 || path.Length == 0)
                {
                    error = "Invalid --layout-map value '" + spec + "'. Use: --layout-map \"<joystick>=<path.layout>\".";
                    return false;
                }

                map[joystick] = NormalizePath(path);
            }

            return true;
        }

        public static LayoutRenderResult Render(
            string exportPath,
            List<string> joystickFilters,
            List<string> gameFilters,
            List<string> planeFilters,
            Dictionary<string, string> layoutMapOverrides,
            bool includeOverview,
            bool includePlaneExports,
            bool skipMissingLayouts,
            LayoutVisualOptions visualOptions)
        {
            LayoutRenderResult result = new LayoutRenderResult();
            result.ExportPath = NormalizePath(exportPath);
            result.RequestedJoysticks = NormalizeDistinct(joystickFilters);
            result.GameFilters = NormalizeDistinct(gameFilters);
            result.PlaneFilters = NormalizeDistinct(planeFilters);

            if (string.IsNullOrWhiteSpace(result.ExportPath))
            {
                result.Errors.Add("No export path available.");
                return result;
            }

            if (visualOptions == null)
                visualOptions = new LayoutVisualOptions();

            try
            {
                Directory.CreateDirectory(result.ExportPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not create export folder '" + result.ExportPath + "': " + ex.Message);
                return result;
            }

            Dictionary<string, string> joystickToLayout = BuildJoystickLayoutMap(layoutMapOverrides);
            List<string> joysticksToRender = ResolveJoysticksToRender(result.RequestedJoysticks, joystickToLayout);
            if (joysticksToRender.Count == 0)
            {
                result.Errors.Add("No joysticks available to render. Add layout mappings with --layout-map <joystick>=<path.layout>.");
                return result;
            }

            List<KeyValuePair<string, string>> planeTargets = includePlaneExports
                ? BuildPlaneTargets(result.GameFilters, result.PlaneFilters, result.Games, result.Warnings)
                : new List<KeyValuePair<string, string>>();
            result.PlaneTargetCount = planeTargets.Count;
            if (includePlaneExports && planeTargets.Count == 0)
                AddUnique(result.Warnings, "No DB plane targets matched current --game/--plane filters.");

            for (int j = 0; j < joysticksToRender.Count; j++)
            {
                string joystick = joysticksToRender[j];
                if (!joystickToLayout.TryGetValue(joystick, out string layoutPath) || string.IsNullOrWhiteSpace(layoutPath))
                {
                    string warning = "No layout mapping found for joystick '" + joystick + "'.";
                    if (skipMissingLayouts)
                    {
                        AddUnique(result.Warnings, warning);
                        continue;
                    }

                    result.Errors.Add(warning);
                    continue;
                }

                // A page with several views of the device (each with its tables and lines).
                if (EngineMultiView.IsMultiViewFile(layoutPath))
                {
                    RenderMultiViewLayout(result, joystick, layoutPath, planeTargets, includePlaneExports, includeOverview, visualOptions);
                    continue;
                }

                // A device layout with tables and lines (the Layout page's callout sheet): drawn like JoyPro 1's sheets.
                if (EngineCalloutRenderer.IsCalloutLayout(layoutPath))
                {
                    RenderCalloutLayout(result, joystick, layoutPath, planeTargets, includePlaneExports, includeOverview, visualOptions);
                    continue;
                }

                if (!TryLoadLayoutTemplate(layoutPath, out LayoutFile layout, out string error))
                {
                    result.Errors.Add("Could not load layout '" + layoutPath + "' for joystick '" + joystick + "': " + error);
                    continue;
                }

                LayoutVisualOptions deviceOptions = visualOptions.WithDeviceStyle(LoadDeviceStyle(layoutPath, result.Warnings));
                try
                {
                    StampCredit(layout.backup, layout.Credit);
                    currentLabelWidth = layout.LabelWidth;
                    string alias = ResolveJoystickAlias(joystick);
                    Dictionary<string, System.Windows.Point> labels = NormalizeLayoutLabelPositions(layout.Positions);
                    List<LayoutTemplateTableDto> tables = LoadTemplateTables(layoutPath, result.Warnings);
                    List<LayoutTemplateConnectorDto> connectors = LoadTemplateConnectors(layoutPath, result.Warnings);
                    string fontName = string.IsNullOrWhiteSpace(layout.Font) ? "Arial" : layout.Font.Trim();
                    float fontSize = layout.Size <= 0 ? 12f : layout.Size;
                    Color textColor = layout.ToDrawingColor();
                    if (deviceOptions != null)
                    {
                        if (!string.IsNullOrWhiteSpace(deviceOptions.FontFamily))
                            fontName = deviceOptions.FontFamily.Trim();
                        if (deviceOptions.FontSize > 0f)
                            fontSize = deviceOptions.FontSize;
                        if (TryParseColor(deviceOptions.TextColor, out Color overrideColor))
                            textColor = overrideColor;
                    }

                    result.RenderedJoysticks.Add(joystick);
                    result.JoystickLayouts[joystick] = NormalizePath(layoutPath);

                    if (includePlaneExports)
                        RenderPlaneImages(result, layout.backup, labels, tables, connectors, fontName, fontSize, textColor, joystick, alias, planeTargets, deviceOptions);
                    if (includeOverview)
                        RenderOverviewImage(result, layout.backup, labels, tables, connectors, fontName, fontSize, textColor, joystick, alias, deviceOptions);
                }
                finally
                {
                    currentLabelWidth = 0;
                    if (layout != null && layout.backup != null)
                        layout.backup.Dispose();
                }
            }

            if (visualOptions.Combine)
                CombinePages(result, visualOptions);

            result.RenderedJoysticks = NormalizeDistinct(result.RenderedJoysticks);
            result.WrittenFiles = NormalizeDistinct(result.WrittenFiles);
            result.Games = NormalizeDistinct(result.Games);
            if (result.TotalImagesWritten == 0 && result.Errors.Count == 0)
            {
                result.Errors.Add(
                    "layout render produced no images. " +
                    "Ensure joystick layout mappings exist (profile JoystickFileImages or --layout-map).");
            }

            return result;
        }

        public static string ToText(LayoutRenderResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Layout Render");
            sb.AppendLine("Export Path: " + (string.IsNullOrWhiteSpace(result.ExportPath) ? "<none>" : result.ExportPath));
            sb.AppendLine("Requested Joysticks: " + result.RequestedJoysticks.Count);
            for (int i = 0; i < result.RequestedJoysticks.Count; i++)
                sb.AppendLine("  - " + result.RequestedJoysticks[i]);
            sb.AppendLine("Rendered Joysticks: " + result.RenderedJoysticks.Count);
            for (int i = 0; i < result.RenderedJoysticks.Count; i++)
                sb.AppendLine("  - " + result.RenderedJoysticks[i]);
            sb.AppendLine("Games: " + result.Games.Count);
            for (int i = 0; i < result.Games.Count; i++)
                sb.AppendLine("  - " + result.Games[i]);
            sb.AppendLine("Plane Targets: " + result.PlaneTargetCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Plane Images Written: " + result.PlaneImagesWritten.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Overview Images Written: " + result.OverviewImagesWritten.ToString(CultureInfo.InvariantCulture));
            if (result.CombinedImagesWritten > 0)
                sb.AppendLine("Combined Pages Written: " + result.CombinedImagesWritten.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Total Images Written: " + result.TotalImagesWritten.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Written Files: " + result.WrittenFiles.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < result.WrittenFiles.Count; i++)
                sb.AppendLine("  - " + result.WrittenFiles[i]);

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

        private static Dictionary<string, string> BuildJoystickLayoutMap(Dictionary<string, string> overrides)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (InternalDataManagement.JoystickFileImages != null)
            {
                foreach (KeyValuePair<string, string> entry in InternalDataManagement.JoystickFileImages)
                {
                    string key = (entry.Key ?? string.Empty).Trim();
                    string value = (entry.Value ?? string.Empty).Trim();
                    if (key.Length == 0 || value.Length == 0)
                        continue;
                    map[key] = NormalizePath(value);
                }
            }

            foreach (KeyValuePair<string, string> entry in overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                string key = (entry.Key ?? string.Empty).Trim();
                string value = (entry.Value ?? string.Empty).Trim();
                if (key.Length == 0 || value.Length == 0)
                    continue;
                map[key] = NormalizePath(value);
            }
            return map;
        }

        private static List<string> ResolveJoysticksToRender(List<string> requested, Dictionary<string, string> joystickToLayout)
        {
            if (requested != null && requested.Count > 0)
                return NormalizeDistinct(requested);
            if (joystickToLayout != null && joystickToLayout.Count > 0)
                return joystickToLayout.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (InternalDataManagement.LocalJoysticks != null)
            {
                for (int i = 0; i < InternalDataManagement.LocalJoysticks.Length; i++)
                    AddJoystickCandidate(output, InternalDataManagement.LocalJoysticks[i]);
            }
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Value == null ? string.Empty : entry.Value.Joystick);
            foreach (KeyValuePair<string, Modifier> entry in InternalDataManagement.AllModifiers ?? new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Value == null ? string.Empty : entry.Value.device);
            foreach (KeyValuePair<string, string> entry in InternalDataManagement.JoystickAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Key);
            foreach (KeyValuePair<string, string> entry in InternalDataManagement.JoystickFileImages ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Key);
            foreach (KeyValuePair<string, string> entry in InternalDataManagement.LocalJoystickPGUID ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Key);
            foreach (KeyValuePair<string, ForceFeedbackS> entry in InternalDataManagement.JoystickFFB ?? new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase))
                AddJoystickCandidate(output, entry.Key);

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddJoystickCandidate(HashSet<string> output, string value)
        {
            if (output == null)
                return;
            string candidate = (value ?? string.Empty).Trim();
            if (candidate.Length == 0 || string.Equals(candidate, "Keyboard", StringComparison.OrdinalIgnoreCase))
                return;
            output.Add(candidate);
        }

        private static List<KeyValuePair<string, string>> BuildPlaneTargets(
            List<string> gameFilters,
            List<string> planeFilters,
            List<string> discoveredGames,
            List<string> warnings)
        {
            List<KeyValuePair<string, string>> targets = new List<KeyValuePair<string, string>>();
            EngineDbCatalogDocument catalog = EngineDatabase.InspectCatalog(MainStructure.PROGPATH);
            foreach (string error in catalog.Errors ?? new List<string>())
                AddUnique(warnings, error);

            foreach (EngineDbGameCatalogDto game in catalog.Games ?? new List<EngineDbGameCatalogDto>())
            {
                if (game == null || string.IsNullOrWhiteSpace(game.Game))
                    continue;
                if (gameFilters != null && gameFilters.Count > 0 && !gameFilters.Any(filter => GameMatchesFilter(game.Game, filter)))
                    continue;

                discoveredGames.Add(game.Game);
                IEnumerable<string> planes = game.Planes ?? new List<string>();
                if (planeFilters != null && planeFilters.Count > 0)
                {
                    planes = planes.Where(p =>
                        !string.IsNullOrWhiteSpace(p) &&
                        planeFilters.Any(filter =>
                            ContainsIgnoreCase(p, filter) ||
                            string.Equals(p, filter, StringComparison.OrdinalIgnoreCase)));
                }

                foreach (string plane in planes)
                {
                    string value = (plane ?? string.Empty).Trim();
                    if (value.Length == 0)
                        continue;
                    targets.Add(new KeyValuePair<string, string>(game.Game, value));
                }
            }

            return targets
                .Distinct()
                .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void RenderPlaneImages(
            LayoutRenderResult result,
            Bitmap source,
            Dictionary<string, System.Windows.Point> labels,
            List<LayoutTemplateTableDto> tables,
            List<LayoutTemplateConnectorDto> connectors,
            string fontName,
            float fontSize,
            Color textColor,
            string joystick,
            string alias,
            List<KeyValuePair<string, string>> planeTargets,
            LayoutVisualOptions visualOptions)
        {
            for (int i = 0; i < planeTargets.Count; i++)
            {
                string game = planeTargets[i].Key;
                string plane = planeTargets[i].Value;
                string planeDir = Path.Combine(result.ExportPath, game, plane);
                Directory.CreateDirectory(planeDir);
                string outputFile = Path.Combine(planeDir, BuildSafeFileName(alias) + ".png");
                using (Bitmap rendered = RenderLayoutImage(
                    source,
                    labels,
                    tables,
                    connectors,
                    fontName,
                    fontSize,
                    textColor,
                    token => ResolvePlaneLabel(joystick, alias, token, game, plane),
                    visualOptions))
                {
                    rendered.Save(outputFile, ImageFormat.Png);
                }
                result.PlaneImagesWritten++;
                result.WrittenFiles.Add(outputFile);
            }
        }

        private static void RenderOverviewImage(
            LayoutRenderResult result,
            Bitmap source,
            Dictionary<string, System.Windows.Point> labels,
            List<LayoutTemplateTableDto> tables,
            List<LayoutTemplateConnectorDto> connectors,
            string fontName,
            float fontSize,
            Color textColor,
            string joystick,
            string alias,
            LayoutVisualOptions visualOptions)
        {
            string outputFile = Path.Combine(result.ExportPath, BuildSafeFileName(alias) + ".png");
            using (Bitmap rendered = RenderLayoutImage(
                source,
                labels,
                tables,
                connectors,
                fontName,
                fontSize,
                textColor,
                token => ResolveOverviewLabel(joystick, alias, token),
                visualOptions))
            {
                rendered.Save(outputFile, ImageFormat.Png);
            }
            result.OverviewImagesWritten++;
            result.WrittenFiles.Add(outputFile);
        }

        // ------------------------------------------------------------------ callout sheets (tables and lines)

        private static void RenderCalloutLayout(
            LayoutRenderResult result,
            string joystick,
            string layoutPath,
            List<KeyValuePair<string, string>> planeTargets,
            bool includePlaneExports,
            bool includeOverview,
            LayoutVisualOptions options)
        {
            JoyPro.DeviceReference.DeviceLayoutSet set;
            try
            {
                set = JoyPro.DeviceReference.DeviceLayoutSet.Parse(File.ReadAllText(layoutPath, Encoding.UTF8), layoutPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not load layout '" + layoutPath + "' for joystick '" + joystick + "': " + ex.Message);
                return;
            }
            JoyPro.DeviceReference.CalloutSheet sheet = EngineCalloutRenderer.SheetOf(set, false, result.Warnings);
            if (sheet == null)
            {
                result.Errors.Add("The layout '" + layoutPath + "' has no tables for joystick '" + joystick + "'.");
                return;
            }
            string alias = ResolveJoystickAlias(joystick);
            result.RenderedJoysticks.Add(joystick);
            result.JoystickLayouts[joystick] = NormalizePath(layoutPath);
            // The device's own sheet style (its sheet's "style") replaces the run's; its drawing style paints the picture.
            options = options.WithDeviceStyle(sheet.Style);
            JoyPro.DeviceReference.DrawingStyle previousDrawing = EngineCalloutRenderer.ExportStyle;
            if (sheet.Style != null && sheet.Style.Drawing.Length > 0)
                EngineCalloutRenderer.ExportStyle = JoyPro.DeviceReference.DrawingStyle.Parse(sheet.Style.Drawing);
            try
            {
            using (Bitmap picture = EngineCalloutRenderer.LoadPicture(set, sheet, null, !options.LightStyle, result.Warnings))
            {
                if (includePlaneExports)
                {
                    foreach (KeyValuePair<string, string> target in planeTargets)
                    {
                        string planeDir = Path.Combine(result.ExportPath, target.Key, target.Value);
                        Directory.CreateDirectory(planeDir);
                        string file = Path.Combine(planeDir, BuildSafeFileName(alias) + ".png");
                        CalloutRenderOptions planeOptions = CalloutOptions(options, target.Key, target.Value, alias);
                        EngineCalloutRenderer.ApplyStyle(planeOptions, set);
                        using (Bitmap rendered = EngineCalloutRenderer.Render(sheet, picture, CalloutText(joystick, sheet, target.Key, target.Value), planeOptions))
                            rendered.Save(file, ImageFormat.Png);
                        result.PlaneImagesWritten++;
                        result.WrittenFiles.Add(file);
                    }
                }
                if (includeOverview)
                {
                    string file = Path.Combine(result.ExportPath, BuildSafeFileName(alias) + ".png");
                    CalloutRenderOptions overviewOptions = CalloutOptions(options, "All games", "Relation names", alias);
                    EngineCalloutRenderer.ApplyStyle(overviewOptions, set);
                    using (Bitmap rendered = EngineCalloutRenderer.Render(sheet, picture, CalloutText(joystick, sheet, null, null), overviewOptions))
                        rendered.Save(file, ImageFormat.Png);
                    result.OverviewImagesWritten++;
                    result.WrittenFiles.Add(file);
                }
            }
            }
            finally
            {
                EngineCalloutRenderer.ExportStyle = previousDrawing;
            }
        }

        // The own sheet style of a device whose cheat sheet is a .layout file: "<layout>.style.json" next to it.
        internal static JoyPro.DeviceReference.SheetStyle LoadDeviceStyle(string layoutPath, List<string> warnings)
        {
            string file = NormalizePath((layoutPath ?? string.Empty) + ".style.json");
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return null;
            JoyPro.DeviceReference.SheetStyle style = JoyPro.DeviceReference.SheetStyle.Parse(File.ReadAllText(file, Encoding.UTF8));
            if (style == null && warnings != null)
                AddUnique(warnings, "layout render: the sheet style '" + file + "' could not be read.");
            return style;
        }

        // A page with several views (MultiViewLayout): one picture per game and aircraft and the overview, like a sheet.
        private static void RenderMultiViewLayout(
            LayoutRenderResult result,
            string joystick,
            string layoutPath,
            List<KeyValuePair<string, string>> planeTargets,
            bool includePlaneExports,
            bool includeOverview,
            LayoutVisualOptions options)
        {
            JoyPro.DeviceReference.MultiViewLayout layout;
            try
            {
                layout = EngineMultiView.Load(layoutPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not load layout '" + layoutPath + "' for joystick '" + joystick + "': " + ex.Message);
                return;
            }
            string alias = ResolveJoystickAlias(joystick);
            result.RenderedJoysticks.Add(joystick);
            result.JoystickLayouts[joystick] = NormalizePath(layoutPath);
            options = options.WithDeviceStyle(EngineMultiView.OwnStyle(layout));
            if (includePlaneExports)
            {
                foreach (KeyValuePair<string, string> target in planeTargets)
                {
                    string planeDir = Path.Combine(result.ExportPath, target.Key, target.Value);
                    Directory.CreateDirectory(planeDir);
                    string file = Path.Combine(planeDir, BuildSafeFileName(alias) + ".png");
                    using (Bitmap rendered = EngineMultiView.RenderPage(layout, joystick, target.Key, target.Value, CalloutOptions(options, target.Key, target.Value, alias), result.Warnings))
                        rendered.Save(file, ImageFormat.Png);
                    result.PlaneImagesWritten++;
                    result.WrittenFiles.Add(file);
                }
            }
            if (includeOverview)
            {
                string file = Path.Combine(result.ExportPath, BuildSafeFileName(alias) + ".png");
                using (Bitmap rendered = EngineMultiView.RenderPage(layout, joystick, null, null, CalloutOptions(options, "All games", "Relation names", alias), result.Warnings))
                    rendered.Save(file, ImageFormat.Png);
                result.OverviewImagesWritten++;
                result.WrittenFiles.Add(file);
            }
        }

        internal static CalloutRenderOptions CalloutOptions(LayoutVisualOptions options, string game, string plane, string device)
        {
            return new CalloutRenderOptions
            {
                Dark = options == null || !options.LightStyle,
                Style = options == null ? null : options.Style,
                PrintWidth = options == null ? 3508 : Math.Max(0, options.PrintWidth),
                Game = game ?? string.Empty,
                Plane = plane ?? string.Empty,
                Device = device ?? string.Empty
            };
        }

        // What a row of a callout sheet shows: the game's own menu title of the bound command for this game and plane
        // (game null = the relation's name); modifier = the bind with the sheet's modifier ("" = the device's most used
        // modifier).
        internal static Func<string, bool, string> CalloutText(string joystick, string sheetModifier, string game, string plane)
        {
            string modifier = string.IsNullOrWhiteSpace(sheetModifier) ? PrimaryModifierOf(joystick) : sheetModifier.Trim();
            return (input, withModifier) =>
            {
                if (string.IsNullOrWhiteSpace(input) || (withModifier && modifier.Length == 0))
                    return string.Empty;
                string token = withModifier ? modifier + "+" + input : input;
                return game == null
                    ? GetRelationNameForJoystickButton(joystick, token)
                    : GetDescriptionForJoystickButtonGamePlane(joystick, token, game, plane);
            };
        }

        // The same per column of the sheet: 0 = no modifier, 1.. = the sheet's modifier combinations (exactly those
        // modifiers; CalloutSheet.ComboOfColumn), the old single column = the sheet's modifier or the most used one.
        internal static Func<string, int, string> CalloutText(string joystick, JoyPro.DeviceReference.CalloutSheet sheet, string game, string plane)
        {
            string primary = null;
            return (input, column) =>
            {
                if (string.IsNullOrWhiteSpace(input))
                    return string.Empty;
                string combo = sheet == null ? (column > 0 ? null : string.Empty) : sheet.ComboOfColumn(column);
                if (combo == null)
                    combo = primary ?? (primary = PrimaryModifierOf(joystick));
                if (column > 0 && combo.Length == 0)
                    return string.Empty;
                string token = combo.Length > 0 ? combo + "+" + input : input;
                return game == null
                    ? GetRelationNameForJoystickButton(joystick, token)
                    : GetDescriptionForJoystickButtonGamePlane(joystick, token, game, plane);
            };
        }

        // Every game and aircraft the device's binds are active in, with the game's own menu title of each bound
        // input there (token = input or "Modifier+input", as layout labels name them): the Layout page's preview.
        internal static SortedDictionary<string, SortedDictionary<string, string>> TitlesFor(string joystick)
        {
            SortedDictionary<string, SortedDictionary<string, string>> targets = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                Bind bind = entry.Value;
                if (bind == null || bind.Rl == null || !string.Equals((bind.Joystick ?? string.Empty).Trim(), (joystick ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                string token = BuildBindToken(bind);
                if (token.Length == 0)
                    continue;
                foreach (RelationItem node in bind.Rl.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    string game = NormalizeGameName(node.Game);
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key))
                            continue;
                        string title = RelationDescriptionForGamePlane(bind.Rl, game, plane.Key);
                        if (title.Length == 0)
                            continue;
                        string key = game + "\u001F" + plane.Key;
                        SortedDictionary<string, string> titles;
                        if (!targets.TryGetValue(key, out titles))
                        {
                            titles = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            targets[key] = titles;
                        }
                        if (!titles.ContainsKey(token))
                            titles[token] = title;
                    }
                }
            }
            return targets;
        }

        // The modifier most binds of a device use (the second text column of its tables); "" = none.
        internal static string PrimaryModifierOf(string joystick)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                Bind bind = entry.Value;
                if (bind == null || !string.Equals((bind.Joystick ?? string.Empty).Trim(), (joystick ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    string value = (reformer ?? string.Empty).Trim();
                    int split = value.IndexOf('§');
                    string name = split > 0 ? value.Substring(0, split).Trim() : value;
                    if (name.Length == 0)
                        continue;
                    int n;
                    counts.TryGetValue(name, out n);
                    counts[name] = n + 1;
                }
            }
            return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key).FirstOrDefault() ?? string.Empty;
        }

        // One page per folder (the overview, each game/plane) with the sheets of all devices under each other.
        private const string CombinedPageName = "All devices.png";

        private static void CombinePages(LayoutRenderResult result, LayoutVisualOptions options)
        {
            foreach (IGrouping<string, string> folder in result.WrittenFiles
                         .Where(f => !string.Equals(Path.GetFileName(f), CombinedPageName, StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .ToList())
            {
                List<string> files = folder.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count < 2)
                    continue;
                string page = Path.Combine(folder.Key, CombinedPageName);
                try
                {
                    using (Bitmap combined = EngineCalloutRenderer.Combine(files, !options.LightStyle, options.Style))
                        combined.Save(page, ImageFormat.Png);
                    result.CombinedImagesWritten++;
                    result.WrittenFiles.Add(page);
                }
                catch (Exception ex)
                {
                    AddUnique(result.Warnings, "layout render: the page of all devices in '" + folder.Key + "' failed: " + ex.Message);
                }
            }
        }

        private static Bitmap RenderLayoutImage(
            Bitmap source,
            Dictionary<string, System.Windows.Point> labels,
            List<LayoutTemplateTableDto> tables,
            List<LayoutTemplateConnectorDto> connectors,
            string fontName,
            float fontSize,
            Color textColor,
            Func<string, string> resolver,
            LayoutVisualOptions visualOptions)
        {
            LayoutVisualOptions options = visualOptions ?? new LayoutVisualOptions();
            Bitmap output = (Bitmap)source.Clone();
            using (Graphics graphics = Graphics.FromImage(output))
            using (Brush brush = new SolidBrush(textColor))
            using (Font font = CreateFont(fontName, fontSize, options.Style != null && options.Style.IsBold))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                List<LayoutLegendEntry> legendEntries = new List<LayoutLegendEntry>();
                Dictionary<string, Color> modifierPalette = ParseModifierPalette(options.ModifierColorCsv);
                Color labelOutline = TryParseColor(options.OutlineColor, out Color outlineColor) ? outlineColor : Color.Empty;
                Color labelBackground = TryParseColor(options.LabelBackground, out Color backgroundColor) ? backgroundColor : Color.Empty;
                foreach (KeyValuePair<string, System.Windows.Point> entry in labels ?? new Dictionary<string, System.Windows.Point>())
                {
                    string descriptor = resolver == null ? string.Empty : (resolver(entry.Key) ?? string.Empty);
                    string token = (entry.Key ?? string.Empty).Trim();
                    string markerType = InferMarkerType(token);
                    string modifier = ExtractPrimaryModifier(token);
                    Color markerColor = ResolveMarkerColor(markerType, modifier, modifierPalette, options.Style);
                    string markerSymbol = ResolveMarkerSymbol(markerType, options);
                    legendEntries.Add(new LayoutLegendEntry
                    {
                        Token = token,
                        Description = descriptor,
                        Type = markerType,
                        Modifier = modifier,
                        Symbol = markerSymbol,
                        Color = markerColor,
                        Position = new PointF((float)entry.Value.X, (float)entry.Value.Y)
                    });
                }

                if (tables != null && tables.Count > 0)
                    DrawTemplatePanelTables(graphics, output.Width, output.Height, tables, options.Style);

                if (connectors != null && connectors.Count > 0)
                    DrawTemplateConnectors(graphics, output.Width, output.Height, connectors, options.Style);

                for (int i = 0; i < legendEntries.Count; i++)
                {
                    LayoutLegendEntry entry = legendEntries[i];
                    if (options.DrawMarkers)
                        DrawMarkerSymbol(graphics, entry.Type, entry.Symbol, entry.Color, entry.Position.X - 14f, entry.Position.Y + 1f, options.MarkerSize);

                    DrawFittedLabel(graphics, entry, legendEntries, output.Width, font, brush, labelOutline, labelBackground);
                }

                if (tables != null && tables.Count > 0)
                    DrawTemplateListTables(graphics, output.Width, output.Height, legendEntries, tables, options);

                if (options.DrawLegendTable)
                    DrawLegendTable(graphics, output.Width, output.Height, legendEntries, options);

                graphics.Flush();
            }
            return output;
        }

        private static List<LayoutTemplateTableDto> LoadTemplateTables(string layoutPath, List<string> warnings)
        {
            List<LayoutTemplateTableDto> tables = new List<LayoutTemplateTableDto>();
            string sidecarPath = NormalizePath((layoutPath ?? string.Empty) + ".tables.json");
            if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
                return tables;

            try
            {
                string json = File.ReadAllText(sidecarPath);
                if (string.IsNullOrWhiteSpace(json))
                    return tables;

                List<LayoutTemplateTableDto> parsed = EngineSerialization.FromJson<List<LayoutTemplateTableDto>>(json);
                if (parsed == null)
                    return tables;

                for (int i = 0; i < parsed.Count; i++)
                {
                    LayoutTemplateTableDto table = parsed[i];
                    if (table == null)
                        continue;

                    LayoutTemplateTableDto normalized = new LayoutTemplateTableDto();
                    normalized.Name = string.IsNullOrWhiteSpace(table.Name) ? "Table" : table.Name.Trim();
                    normalized.Mode = NormalizeTableMode(table.Mode);
                    normalized.X = Math.Max(0, table.X);
                    normalized.Y = Math.Max(0, table.Y);
                    normalized.Width = Math.Max(120, table.Width);
                    normalized.Height = Math.Max(70, table.Height);
                    normalized.Columns = Math.Max(1, table.Columns);
                    normalized.BackgroundColor = string.IsNullOrWhiteSpace(table.BackgroundColor) ? "#F6F8FB" : table.BackgroundColor.Trim();
                    normalized.BackgroundOpacity = table.BackgroundOpacity < 0f
                        ? 0f
                        : (table.BackgroundOpacity > 1f ? 1f : table.BackgroundOpacity);
                    normalized.DropShadow = table.DropShadow;
                    tables.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "layout render: failed to parse table sidecar '" + sidecarPath + "': " + ex.Message);
            }

            return tables;
        }

        private static List<LayoutTemplateConnectorDto> LoadTemplateConnectors(string layoutPath, List<string> warnings)
        {
            List<LayoutTemplateConnectorDto> connectors = new List<LayoutTemplateConnectorDto>();
            string sidecarPath = NormalizePath((layoutPath ?? string.Empty) + ".connectors.json");
            if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
                return connectors;

            try
            {
                string json = File.ReadAllText(sidecarPath);
                if (string.IsNullOrWhiteSpace(json))
                    return connectors;

                List<LayoutTemplateConnectorDto> parsed = EngineSerialization.FromJson<List<LayoutTemplateConnectorDto>>(json);
                if (parsed == null)
                    return connectors;

                for (int i = 0; i < parsed.Count; i++)
                {
                    LayoutTemplateConnectorDto connector = parsed[i];
                    if (connector == null)
                        continue;

                    List<LayoutTemplateConnectorPointDto> points = (connector.Points ?? new List<LayoutTemplateConnectorPointDto>())
                        .Where(p => p != null)
                        .Select(p => new LayoutTemplateConnectorPointDto
                        {
                            X = Math.Max(0f, p.X),
                            Y = Math.Max(0f, p.Y)
                        })
                        .ToList();
                    if (points.Count < 2)
                        continue;

                    LayoutTemplateConnectorDto normalized = new LayoutTemplateConnectorDto();
                    normalized.Name = string.IsNullOrWhiteSpace(connector.Name) ? "Connector" : connector.Name.Trim();
                    normalized.Color = string.IsNullOrWhiteSpace(connector.Color) ? "#E2E8F0" : connector.Color.Trim();
                    normalized.Width = ClampConnectorWidth(connector.Width);
                    normalized.Points = points;
                    connectors.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "layout render: failed to parse connector sidecar '" + sidecarPath + "': " + ex.Message);
            }

            return connectors;
        }

        private static void DrawTemplatePanelTables(
            Graphics graphics,
            int imageWidth,
            int imageHeight,
            List<LayoutTemplateTableDto> tables,
            JoyPro.DeviceReference.SheetStyle style = null)
        {
            if (graphics == null || tables == null || tables.Count == 0)
                return;

            // "panel" tables are backdrop helpers: light, blank table zones behind marker labels (their own colour; the
            // border in the sheet style's).
            using (Pen borderPen = new Pen(StyleColor(style == null ? null : style.TableBorder, Color.FromArgb(228, 74, 85, 104)), 2f))
            using (Pen rowPen = new Pen(Color.FromArgb(208, 114, 129, 150), 1f))
            using (Pen columnPen = new Pen(Color.FromArgb(192, 114, 129, 150), 1f))
            {
                for (int i = 0; i < tables.Count; i++)
                {
                    LayoutTemplateTableDto table = tables[i];
                    if (table == null || string.Equals(NormalizeTableMode(table.Mode), "list", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int x = Math.Max(0, (int)Math.Round(table.X, MidpointRounding.AwayFromZero));
                    int y = Math.Max(0, (int)Math.Round(table.Y, MidpointRounding.AwayFromZero));
                    int width = Math.Max(120, (int)Math.Round(table.Width, MidpointRounding.AwayFromZero));
                    int height = Math.Max(70, (int)Math.Round(table.Height, MidpointRounding.AwayFromZero));
                    int columns = Math.Max(1, table.Columns);

                    if (x >= imageWidth || y >= imageHeight)
                        continue;

                    if (x + width > imageWidth)
                        width = Math.Max(120, imageWidth - x - 1);
                    if (y + height > imageHeight)
                        height = Math.Max(70, imageHeight - y - 1);

                    Rectangle tableRect = new Rectangle(x, y, width, height);
                    Color baseColor = TryParseColor(table.BackgroundColor, out Color parsedFill)
                        ? parsedFill
                        : Color.FromArgb(255, 246, 248, 251);
                    float opacity = table.BackgroundOpacity;
                    if (float.IsNaN(opacity) || float.IsInfinity(opacity))
                        opacity = 0.93f;
                    opacity = Math.Max(0f, Math.Min(1f, opacity));
                    int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(opacity * 255f, MidpointRounding.AwayFromZero)));
                    using (SolidBrush panelBrush = new SolidBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)))
                    {
                        if (table.DropShadow)
                        {
                            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(78, 15, 23, 42)))
                            {
                                Rectangle shadowRect = new Rectangle(x + 2, y + 2, width, height);
                                graphics.FillRectangle(shadowBrush, shadowRect);
                            }
                        }

                        graphics.FillRectangle(panelBrush, tableRect);
                    }
                    graphics.DrawRectangle(borderPen, tableRect);

                    int rowHeight = 34;
                    int rowCount = Math.Max(1, height / rowHeight);
                    for (int row = 1; row < rowCount; row++)
                    {
                        int rowY = y + (row * rowHeight);
                        if (rowY >= y + height)
                            break;
                        graphics.DrawLine(rowPen, x + 1, rowY, x + width - 1, rowY);
                    }

                    if (columns > 1)
                    {
                        int columnWidth = Math.Max(50, width / columns);
                        for (int column = 1; column < columns; column++)
                        {
                            int dividerX = x + (column * columnWidth);
                            if (dividerX >= x + width)
                                break;
                            graphics.DrawLine(columnPen, dividerX, y + 1, dividerX, y + height - 1);
                        }
                    }
                }
            }
        }

        private static void DrawTemplateListTables(
            Graphics graphics,
            int imageWidth,
            int imageHeight,
            List<LayoutLegendEntry> legendEntries,
            List<LayoutTemplateTableDto> tables,
            LayoutVisualOptions options)
        {
            if (graphics == null || tables == null || tables.Count == 0)
                return;

            List<LayoutLegendEntry> ordered = (legendEntries ?? new List<LayoutLegendEntry>())
                .Where(v => v != null)
                .OrderBy(v => v.Token, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int legendCursor = 0;
            float rowFontSize = options == null || options.LegendFontSize <= 0f ? 11f : options.LegendFontSize;
            int rowHeight = Math.Max(16, (int)Math.Round(rowFontSize + 5f, MidpointRounding.AwayFromZero));
            int headerHeight = Math.Max(24, rowHeight + 2);

            JoyPro.DeviceReference.SheetStyle style = options == null ? null : options.Style;
            string tableFont = style != null && style.FontFamily.Length > 0 ? style.FontFamily : "Segoe UI";
            Color tableBorder = StyleColor(style == null ? null : style.TableBorder, Color.FromArgb(210, 132, 204, 22));
            using (Font headerFont = CreateFont(tableFont, Math.Max(10f, rowFontSize + 1f), true))
            using (Font rowFont = CreateFont(tableFont, Math.Max(9f, rowFontSize - 1f), style != null && style.IsBold))
            using (SolidBrush panelBrush = new SolidBrush(StyleColor(style == null ? null : style.TableBackground, Color.FromArgb(170, 8, 12, 18))))
            using (SolidBrush headerBrush = new SolidBrush(StyleColor(style == null ? null : style.HeaderBackground, Color.FromArgb(214, 101, 163, 13))))
            using (SolidBrush headerTextBrush = new SolidBrush(StyleColor(style == null ? null : style.HeaderText, Color.FromArgb(245, 248, 250))))
            using (SolidBrush rowTextBrush = new SolidBrush(StyleColor(style == null ? null : style.TableText, Color.FromArgb(232, 241, 245, 249))))
            using (Pen borderPen = new Pen(tableBorder, 2f))
            using (Pen dividerPen = new Pen(Color.FromArgb(122, tableBorder), 1f))
            {
                for (int i = 0; i < tables.Count; i++)
                {
                    LayoutTemplateTableDto table = tables[i];
                    if (table == null || !string.Equals(NormalizeTableMode(table.Mode), "list", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int x = Math.Max(0, (int)Math.Round(table.X, MidpointRounding.AwayFromZero));
                    int y = Math.Max(0, (int)Math.Round(table.Y, MidpointRounding.AwayFromZero));
                    int width = Math.Max(120, (int)Math.Round(table.Width, MidpointRounding.AwayFromZero));
                    int height = Math.Max(70, (int)Math.Round(table.Height, MidpointRounding.AwayFromZero));
                    int columns = Math.Max(1, table.Columns);

                    if (x >= imageWidth || y >= imageHeight)
                        continue;

                    if (x + width > imageWidth)
                        width = Math.Max(120, imageWidth - x - 1);
                    if (y + height > imageHeight)
                        height = Math.Max(70, imageHeight - y - 1);

                    Rectangle tableRect = new Rectangle(x, y, width, height);
                    Rectangle headerRect = new Rectangle(x, y, width, headerHeight);
                    graphics.FillRectangle(panelBrush, tableRect);
                    graphics.DrawRectangle(borderPen, tableRect);
                    graphics.FillRectangle(headerBrush, headerRect);
                    graphics.DrawString(
                        string.IsNullOrWhiteSpace(table.Name) ? "Table" : table.Name,
                        headerFont,
                        headerTextBrush,
                        x + 6,
                        y + 3);

                    int bodyTop = y + headerHeight + 3;
                    int bodyHeight = Math.Max(0, (y + height) - bodyTop - 4);
                    if (bodyHeight <= 0)
                        continue;

                    int rowsPerColumn = Math.Max(1, bodyHeight / rowHeight);
                    int effectiveColumns = Math.Max(1, columns);
                    int columnWidth = Math.Max(96, (width - 10) / effectiveColumns);
                    int capacity = rowsPerColumn * effectiveColumns;
                    int localCount = Math.Min(capacity, Math.Max(0, ordered.Count - legendCursor));

                    for (int c = 1; c < effectiveColumns; c++)
                    {
                        int dividerX = x + 5 + (c * columnWidth);
                        graphics.DrawLine(dividerPen, dividerX, bodyTop - 2, dividerX, y + height - 3);
                    }

                    for (int idx = 0; idx < localCount; idx++)
                    {
                        LayoutLegendEntry entry = ordered[legendCursor + idx];
                        int col = idx / rowsPerColumn;
                        int row = idx % rowsPerColumn;
                        float rowX = x + 8 + (col * columnWidth);
                        float rowY = bodyTop + (row * rowHeight);
                        DrawMarkerSymbol(graphics, entry.Type, entry.Symbol, entry.Color, rowX, rowY + 1f, 11);

                        string text = (entry.Description ?? string.Empty).Trim();
                        if (text.Length == 0)
                            text = entry.Token ?? string.Empty;
                        if (text.Length > 30)
                            text = text.Substring(0, 28) + "..";
                        graphics.DrawString(text, rowFont, rowTextBrush, rowX + 18f, rowY + 1f);
                    }

                    legendCursor += localCount;
                }
            }
        }

        private static void DrawTemplateConnectors(
            Graphics graphics,
            int imageWidth,
            int imageHeight,
            List<LayoutTemplateConnectorDto> connectors,
            JoyPro.DeviceReference.SheetStyle style = null)
        {
            if (graphics == null || connectors == null || connectors.Count == 0)
                return;
            // Lines left at the default colour take the sheet style's line colour.
            Color styleLine = StyleColor(style == null ? null : style.LineColor, Color.Empty);

            for (int i = 0; i < connectors.Count; i++)
            {
                LayoutTemplateConnectorDto connector = connectors[i];
                if (connector == null || connector.Points == null || connector.Points.Count < 2)
                    continue;

                Color strokeColor = TryParseColor(connector.Color, out Color parsed)
                    ? parsed
                    : Color.FromArgb(226, 232, 240);
                if (!styleLine.IsEmpty && (string.IsNullOrWhiteSpace(connector.Color) || string.Equals(connector.Color.Trim(), "#E2E8F0", StringComparison.OrdinalIgnoreCase)))
                    strokeColor = styleLine;
                float width = ClampConnectorWidth(connector.Width);
                using (Pen glowPen = new Pen(Color.FromArgb(120, strokeColor), width + 2f))
                using (Pen linePen = new Pen(Color.FromArgb(220, strokeColor), width))
                {
                    linePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    linePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    linePen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    glowPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    glowPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    glowPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                    List<PointF> points = new List<PointF>();
                    for (int p = 0; p < connector.Points.Count; p++)
                    {
                        LayoutTemplateConnectorPointDto point = connector.Points[p];
                        if (point == null)
                            continue;
                        float x = Math.Max(0f, Math.Min(imageWidth - 1, point.X));
                        float y = Math.Max(0f, Math.Min(imageHeight - 1, point.Y));
                        points.Add(new PointF(x, y));
                    }

                    if (points.Count < 2)
                        continue;

                    graphics.DrawLines(glowPen, points.ToArray());
                    graphics.DrawLines(linePen, points.ToArray());
                }
            }
        }

        private sealed class LayoutLegendEntry
        {
            public string Token;
            public string Description;
            public string Type;
            public string Modifier;
            public string Symbol;
            public Color Color;
            public PointF Position;
        }

        private static string NormalizeTableMode(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "list")
                return "list";
            return "panel";
        }

        private static float ClampConnectorWidth(float width)
        {
            if (width < 1f)
                return 1f;
            if (width > 12f)
                return 12f;
            return width;
        }

        private static string InferMarkerType(string token)
        {
            string value = (token ?? string.Empty).ToUpperInvariant();
            if (value.Contains("POV") || value.Contains("HAT"))
                return "pov";
            if (value.Contains("SLIDER"))
                return "slider";
            if (value.Contains("JOY_") &&
                (value.Contains("_X") ||
                 value.Contains("_Y") ||
                 value.Contains("_Z") ||
                 value.Contains("RX") ||
                 value.Contains("RY") ||
                 value.Contains("RZ")))
                return "axis";
            if (value.StartsWith("JOY_"))
                return "button";
            if (value.Contains("AXIS"))
                return "axis";
            return "button";
        }

        private static string ExtractPrimaryModifier(string token)
        {
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;

            int split = value.IndexOf('+');
            if (split <= 0)
                return string.Empty;
            return value.Substring(0, split).Trim();
        }

        private static Dictionary<string, Color> ParseModifierPalette(string csv)
        {
            Dictionary<string, Color> map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (csv ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part.Trim();
                if (token.Length == 0)
                    continue;
                int split = token.IndexOf('=');
                if (split <= 0 || split >= token.Length - 1)
                    continue;
                string key = token.Substring(0, split).Trim();
                string value = token.Substring(split + 1).Trim();
                if (key.Length == 0 || value.Length == 0)
                    continue;
                if (!TryParseColor(value, out Color color))
                    continue;
                map[key] = color;
            }

            return map;
        }

        private static Color ResolveMarkerColor(string markerType, string modifier, Dictionary<string, Color> modifierPalette, JoyPro.DeviceReference.SheetStyle style = null)
        {
            if (!string.IsNullOrWhiteSpace(modifier) &&
                modifierPalette != null &&
                modifierPalette.TryGetValue(modifier.Trim(), out Color mapped))
            {
                return mapped;
            }
            // The sheet style's colour of the input kind.
            Color styled = StyleColor(style == null ? null : style.MarkerColor(markerType), Color.Empty);
            if (!styled.IsEmpty)
                return styled;

            string type = (markerType ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "axis")
                return Color.FromArgb(255, 56, 189, 248);
            if (type == "pov")
                return Color.FromArgb(255, 168, 85, 247);
            if (type == "slider")
                return Color.FromArgb(255, 251, 191, 36);
            return Color.FromArgb(255, 34, 197, 94);
        }

        private static string ResolveMarkerSymbol(string markerType, LayoutVisualOptions options)
        {
            string type = (markerType ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "axis")
                return (options.AxisSymbol ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "pov")
                return (options.PovSymbol ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "slider")
                return (options.SliderSymbol ?? string.Empty).Trim().ToLowerInvariant();
            return (options.ButtonSymbol ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static void DrawMarkerSymbol(Graphics graphics, string markerType, string markerSymbol, Color color, float x, float y, int markerSize)
        {
            if (graphics == null)
                return;

            int size = markerSize < 6 ? 6 : (markerSize > 36 ? 36 : markerSize);
            RectangleF rect = new RectangleF(x, y, size, size);
            using (Brush fill = new SolidBrush(Color.FromArgb(190, color)))
            using (Pen outline = new Pen(Color.FromArgb(255, 12, 18, 34), 1f))
            {
                string type = (markerType ?? string.Empty).Trim().ToLowerInvariant();
                string symbol = (markerSymbol ?? string.Empty).Trim().ToLowerInvariant();
                if (symbol.Length == 0)
                {
                    if (type == "axis")
                        symbol = "diamond";
                    else if (type == "pov")
                        symbol = "triangle";
                    else if (type == "slider")
                        symbol = "bar";
                    else
                        symbol = "circle";
                }

                if (symbol == "diamond")
                {
                    PointF[] diamond =
                    {
                        new PointF(rect.X + rect.Width / 2f, rect.Y),
                        new PointF(rect.Right, rect.Y + rect.Height / 2f),
                        new PointF(rect.X + rect.Width / 2f, rect.Bottom),
                        new PointF(rect.X, rect.Y + rect.Height / 2f)
                    };
                    graphics.FillPolygon(fill, diamond);
                    graphics.DrawPolygon(outline, diamond);
                    return;
                }

                if (symbol == "triangle")
                {
                    PointF[] triangle =
                    {
                        new PointF(rect.X + rect.Width / 2f, rect.Y),
                        new PointF(rect.Right, rect.Bottom),
                        new PointF(rect.X, rect.Bottom)
                    };
                    graphics.FillPolygon(fill, triangle);
                    graphics.DrawPolygon(outline, triangle);
                    return;
                }

                if (symbol == "bar")
                {
                    RectangleF sliderRect = new RectangleF(rect.X, rect.Y + rect.Height * 0.25f, rect.Width, rect.Height * 0.5f);
                    graphics.FillRectangle(fill, sliderRect);
                    graphics.DrawRectangle(outline, sliderRect.X, sliderRect.Y, sliderRect.Width, sliderRect.Height);
                    return;
                }

                if (symbol == "square")
                {
                    graphics.FillRectangle(fill, rect);
                    graphics.DrawRectangle(outline, rect.X, rect.Y, rect.Width, rect.Height);
                    return;
                }

                graphics.FillEllipse(fill, rect);
                graphics.DrawEllipse(outline, rect);
            }
        }

        private static void DrawLegendTable(
            Graphics graphics,
            int imageWidth,
            int imageHeight,
            List<LayoutLegendEntry> legendEntries,
            LayoutVisualOptions options)
        {
            if (graphics == null || legendEntries == null || legendEntries.Count == 0)
                return;

            int tableWidth = options.LegendTableWidth < 220 ? 220 : options.LegendTableWidth;
            tableWidth = Math.Min(tableWidth, Math.Max(240, imageWidth - 30));
            int maxRows = options.LegendMaxRows < 4 ? 4 : options.LegendMaxRows;
            float fontSize = options.LegendFontSize <= 0 ? 11f : options.LegendFontSize;
            int rowHeight = Math.Max(18, (int)Math.Ceiling(fontSize + 6f));
            int rows = Math.Min(maxRows, legendEntries.Count);
            int headerHeight = rowHeight + 4;
            int tableHeight = headerHeight + (rows * rowHeight) + 8;
            int x = Math.Max(8, imageWidth - tableWidth - 10);
            int y = 10;
            if (y + tableHeight > imageHeight - 8)
                y = Math.Max(8, imageHeight - tableHeight - 8);

            JoyPro.DeviceReference.SheetStyle style = options.Style;
            string legendFont = style != null && style.FontFamily.Length > 0 ? style.FontFamily : "Segoe UI";
            using (Brush panel = new SolidBrush(StyleColor(style == null ? null : style.TableBackground, Color.FromArgb(160, 2, 6, 23))))
            using (Pen border = new Pen(StyleColor(style == null ? null : style.TableBorder, Color.FromArgb(200, 125, 211, 252)), 1f))
            using (Font headerFont = CreateFont(legendFont, fontSize + 1f, true))
            using (Font rowFont = CreateFont(legendFont, fontSize, style != null && style.IsBold))
            using (Brush rowBrush = new SolidBrush(StyleColor(style == null ? null : style.TableText, Color.FromArgb(240, 241, 245, 249))))
            {
                graphics.FillRectangle(panel, x, y, tableWidth, tableHeight);
                graphics.DrawRectangle(border, x, y, tableWidth, tableHeight);
                graphics.DrawString("Control Legend", headerFont, rowBrush, x + 8, y + 4);

                List<LayoutLegendEntry> ordered = legendEntries
                    .OrderBy(v => v.Position.Y)
                    .ThenBy(v => v.Position.X)
                    .Take(rows)
                    .ToList();

                for (int i = 0; i < ordered.Count; i++)
                {
                    LayoutLegendEntry entry = ordered[i];
                    int rowTop = y + headerHeight + (i * rowHeight);
                    DrawMarkerSymbol(graphics, entry.Type, entry.Symbol, entry.Color, x + 8, rowTop + 3, 12);
                    string token = (entry.Token ?? string.Empty).Trim();
                    if (token.Length > 24)
                        token = token.Substring(0, 24) + "...";
                    string description = (entry.Description ?? string.Empty).Trim();
                    if (description.Length > 28)
                        description = description.Substring(0, 28) + "...";
                    string label = token.Length == 0 ? description : token + " -> " + description;
                    graphics.DrawString(label, rowFont, rowBrush, x + 24, rowTop + 1);
                }

                int hidden = legendEntries.Count - ordered.Count;
                if (hidden > 0)
                    graphics.DrawString("+" + hidden.ToString(CultureInfo.InvariantCulture) + " more", rowFont, rowBrush, x + 24, y + tableHeight - rowHeight - 1);
            }
        }

        // A label that would run into the next label on its line (a sheet's boxes side by side) or off the picture is drawn
        // smaller (down to 60 %), and cut with "..." if it still does not fit; a label with room is drawn as it is.
        // The label room of the layout being drawn (LayoutFile.LabelWidth; 0 = up to the next label).
        [ThreadStatic] private static float currentLabelWidth;

        private static void DrawFittedLabel(Graphics graphics, LayoutLegendEntry entry, List<LayoutLegendEntry> all, int pictureWidth, Font font, Brush brush, Color outline, Color background)
        {
            string text = entry.Description ?? string.Empty;
            if (text.Length == 0)
                return;
            float x = entry.Position.X, y = entry.Position.Y;
            float lineHeight = font.GetHeight(graphics);
            float room = pictureWidth - x - 4f;
            if (currentLabelWidth > 0)
                room = Math.Min(room, currentLabelWidth);
            foreach (LayoutLegendEntry other in all)
            {
                if (ReferenceEquals(other, entry) || string.IsNullOrEmpty(other.Description) && string.IsNullOrEmpty(other.Token))
                    continue;
                float dx = other.Position.X - x;
                if (dx > 4f && Math.Abs(other.Position.Y - y) < lineHeight * 0.8f)
                    room = Math.Min(room, dx - 6f);
            }
            SizeF size = graphics.MeasureString(text, font);
            if (room < 24f || size.Width <= room)
            {
                DrawLabel(graphics, text, font, brush, x, y, outline, background);
                return;
            }
            float scale = Math.Max(0.6f, room / size.Width);
            using (Font smaller = new Font(font.FontFamily, font.Size * scale, font.Style, font.Unit))
            {
                string shown = text;
                while (shown.Length > 1 && graphics.MeasureString(shown + (shown.Length < text.Length ? "..." : string.Empty), smaller).Width > room)
                    shown = shown.Substring(0, shown.Length - 1).TrimEnd();
                if (shown.Length < text.Length)
                    shown += "...";
                // Vertically centred on the line the full size would take.
                float offset = (lineHeight - smaller.GetHeight(graphics)) / 2f;
                DrawLabel(graphics, shown, smaller, brush, x, y + offset, outline, background);
            }
        }

        // A label like the layout editor shows it: optional box behind the text, optional outline around the glyphs.
        private static void DrawLabel(Graphics graphics, string text, Font font, Brush brush, float x, float y, Color outline, Color background)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (background != Color.Empty && background.A > 0)
            {
                SizeF size = graphics.MeasureString(text, font);
                RectangleF box = new RectangleF(x - 4f, y - 2f, size.Width + 6f, size.Height + 2f);
                using (SolidBrush fill = new SolidBrush(background))
                using (System.Drawing.Drawing2D.GraphicsPath rounded = RoundedRectangle(box, 4f))
                    graphics.FillPath(fill, rounded);
            }

            if (outline == Color.Empty || outline.A == 0)
            {
                graphics.DrawString(text, font, brush, x, y);
                return;
            }

            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            using (Pen pen = new Pen(outline, Math.Max(2f, font.Size / 5f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                float emSize = graphics.DpiY * font.SizeInPoints / 72f;
                path.AddString(text, font.FontFamily, (int)font.Style, emSize, new PointF(x, y), StringFormat.GenericDefault);
                graphics.DrawPath(pen, path);
                graphics.FillPath(brush, path);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            float d = radius * 2f;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static bool TryParseColor(string text, out Color color)
        {
            color = Color.White;
            string raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0 || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                return false;

            if (raw.StartsWith("#", StringComparison.Ordinal))
            {
                string hex = raw.Substring(1);
                if (hex.Length == 6 &&
                    int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) &&
                    int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) &&
                    int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
                {
                    color = Color.FromArgb(255, r, g, b);
                    return true;
                }

                if (hex.Length == 8 &&
                    int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int a) &&
                    int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                    int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                    int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                {
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }
            }

            try
            {
                color = ColorTranslator.FromHtml(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Font CreateFont(string fontName, float fontSize, bool bold = false)
        {
            float size = fontSize <= 0 ? 12f : fontSize;
            string family = string.IsNullOrWhiteSpace(fontName) ? "Arial" : fontName.Trim();
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            try
            {
                return new Font(family, size, style);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style);
            }
        }

        // A colour of the sheet style ("#AARRGGBB"), else the fallback.
        private static Color StyleColor(string value, Color fallback)
        {
            uint argb;
            return !string.IsNullOrWhiteSpace(value) && JoyPro.DeviceReference.SheetStyle.TryParse(value, out argb)
                ? Color.FromArgb(unchecked((int)argb))
                : fallback;
        }

        private static string ResolvePlaneLabel(string joystick, string alias, string token, string game, string plane)
        {
            if (string.Equals(token, "Game", StringComparison.OrdinalIgnoreCase))
                return game ?? string.Empty;
            if (string.Equals(token, "Plane", StringComparison.OrdinalIgnoreCase))
                return plane ?? string.Empty;
            if (string.Equals(token, "Joystick", StringComparison.OrdinalIgnoreCase))
                return alias ?? string.Empty;
            return GetDescriptionForJoystickButtonGamePlane(joystick, token, game, plane);
        }

        private static string ResolveOverviewLabel(string joystick, string alias, string token)
        {
            if (string.Equals(token, "Game", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Plane", StringComparison.OrdinalIgnoreCase))
                return "JoyPro";
            if (string.Equals(token, "Joystick", StringComparison.OrdinalIgnoreCase))
                return alias ?? string.Empty;
            return GetRelationNameForJoystickButton(joystick, token);
        }

        // A 2D layout variant (DB\DeviceLayouts2D, or the user's copy of one): its picture (a picture file, or the picture
        // inside a JoyPro 1 .layout), text style and positions as a layout file.
        internal static bool TryLoadLayoutVariant(string path, out LayoutFile layout, out string error)
        {
            layout = null;
            error = string.Empty;
            JoyPro.DeviceReference.LayoutVariant variant;
            try
            {
                variant = JoyPro.DeviceReference.LayoutVariant.Parse(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            string picture = variant.ResolvePicture(new[] { Path.GetDirectoryName(path), JoyPro.DeviceReference.DeviceCatalog.ImportedLayoutFolder(), JoyPro.DeviceReference.DeviceCatalog.CommunityLayoutFolder() });
            if (picture == null)
            {
                error = "The picture of the 2D layout '" + variant.Id + "' is not on this PC.";
                return false;
            }
            Bitmap bitmap;
            if (picture.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryLoadLayoutTemplate(picture, out LayoutFile inner, out error))
                    return false;
                bitmap = inner.backup;
            }
            else
            {
                // A copy, so the picture file is not kept open.
                using (Bitmap loaded = new Bitmap(picture))
                    bitmap = new Bitmap(loaded);
            }
            layout = new LayoutFile();
            layout.Joystick = variant.Title.Length > 0 ? variant.Title : variant.Id;
            layout.Font = variant.Font;
            layout.Size = Math.Max(1, (int)Math.Round(variant.FontSize));
            layout.Credit = variant.Credit ?? string.Empty;
            layout.LabelWidth = (float)Math.Max(0, variant.LabelWidth);
            if (TryParseColor(variant.TextColor, out Color color))
                layout.SetDrawingColor(color);
            layout.Positions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double[]> pair in variant.Positions)
                layout.Positions[pair.Key] = new System.Windows.Point(pair.Value[0], pair.Value[1]);
            layout.backup = bitmap;
            return true;
        }

        // The sheet's maker in small grey letters in the bottom left corner of the picture (a community 2D layout; the
        // sheets keep their own title in the bottom right).
        internal static void StampCredit(Bitmap picture, string credit)
        {
            if (picture == null || string.IsNullOrWhiteSpace(credit))
                return;
            using (Graphics graphics = Graphics.FromImage(picture))
            using (Font font = CreateFont("Segoe UI", Math.Max(8f, picture.Width / 160f)))
            using (Brush brush = new SolidBrush(Color.FromArgb(200, 110, 110, 110)))
            {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                string text = "Layout sheet: " + credit.Trim();
                SizeF size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, brush, picture.Width / 120f, picture.Height - size.Height - picture.Height / 200f);
            }
        }

        internal static bool TryLoadLayoutTemplate(string layoutPath, out LayoutFile layout, out string error)
        {
            layout = null;
            error = string.Empty;
            string fullPath = NormalizePath(layoutPath);
            if (!File.Exists(fullPath))
            {
                error = "File does not exist.";
                return false;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            try
            {
                if (ext == ".layout")
                {
                    using (FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        BinaryFormatter formatter = new BinaryFormatter();
                        formatter.Binder = new LegacyJoyProSerializationBinder();
                        layout = formatter.Deserialize(stream) as LayoutFile;
                    }
                }
                else if (ext == ".png" || ext == ".bmp" || ext == ".jpg" || ext == ".jpeg")
                {
                    layout = new LayoutFile();
                    layout.Joystick = Path.GetFileNameWithoutExtension(fullPath);
                    layout.Font = "Arial";
                    layout.Size = 12;
                    layout.Positions = new Dictionary<string, System.Windows.Point>();
                    layout.backup = new Bitmap(fullPath);
                }
                else if (ext == ".json" && JoyPro.DeviceReference.LayoutVariant.IsVariantFile(fullPath))
                {
                    if (!TryLoadLayoutVariant(fullPath, out layout, out error))
                        return false;
                }
                else
                {
                    error = "Unsupported layout format '" + ext + "'.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (layout == null || layout.backup == null)
            {
                error = "Layout has no image.";
                return false;
            }
            if (layout.Positions == null)
                layout.Positions = new Dictionary<string, System.Windows.Point>();
            return true;
        }

        internal static string ResolveJoystickAlias(string joystick)
        {
            string key = (joystick ?? string.Empty).Trim();
            if (key.Length == 0)
                return string.Empty;
            if (InternalDataManagement.JoystickAliases != null &&
                InternalDataManagement.JoystickAliases.TryGetValue(key, out string alias) &&
                !string.IsNullOrWhiteSpace(alias))
            {
                return alias.Trim();
            }
            return key;
        }

        private static string GetDescriptionForJoystickButtonGamePlane(string joystick, string modButton, string game, string plane)
        {
            if (string.IsNullOrWhiteSpace(joystick) || string.IsNullOrWhiteSpace(modButton) || string.IsNullOrWhiteSpace(plane))
                return string.Empty;

            string targetToken = NormalizeLayoutLabelToken(modButton, BuildModifierRenameMap());
            string targetGame = NormalizeGameName(game);
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                Bind bind = entry.Value;
                if (bind == null || bind.Rl == null)
                    continue;
                if (!string.Equals((bind.Joystick ?? string.Empty).Trim(), joystick.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(BuildBindToken(bind), targetToken, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!RelationHasActivePlane(bind.Rl, targetGame, plane))
                    continue;

                string desc = RelationDescriptionForGamePlane(bind.Rl, targetGame, plane);
                if (desc.Length > 0)
                    return desc;
            }

            return string.Empty;
        }

        private static string GetRelationNameForJoystickButton(string joystick, string modButton)
        {
            string targetToken = NormalizeLayoutLabelToken(modButton, BuildModifierRenameMap());
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                Bind bind = entry.Value;
                if (bind == null)
                    continue;
                if (!string.Equals((bind.Joystick ?? string.Empty).Trim(), (joystick ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(BuildBindToken(bind), targetToken, StringComparison.OrdinalIgnoreCase))
                    return entry.Key ?? string.Empty;
            }
            return string.Empty;
        }

        private static string BuildBindToken(Bind bind)
        {
            if (bind == null)
                return string.Empty;
            string input = bind.Rl != null && bind.Rl.ISAXIS ? (bind.JAxis ?? string.Empty) : (bind.JButton ?? string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                input = string.IsNullOrWhiteSpace(bind.JButton) ? (bind.JAxis ?? string.Empty) : bind.JButton;
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            List<string> modifiers = new List<string>();
            foreach (string reformer in bind.AllReformers ?? new List<string>())
            {
                string value = (reformer ?? string.Empty).Trim();
                if (value.Length == 0)
                    continue;
                int split = value.IndexOf('\u00A7');
                string name = split > 0 ? value.Substring(0, split).Trim() : value;
                if (name.Length == 0)
                    continue;
                if (!modifiers.Contains(name, StringComparer.OrdinalIgnoreCase))
                    modifiers.Add(name);
            }
            if (modifiers.Count == 0)
                return input;
            modifiers = modifiers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            return string.Join("+", modifiers) + "+" + input;
        }

        private static bool RelationHasActivePlane(Relation relation, string game, string plane)
        {
            string targetGame = NormalizeGameName(game);
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || node.AIRCRAFT == null)
                    continue;
                if (!string.Equals(NormalizeGameName(node.Game), targetGame, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (node.AIRCRAFT.ContainsKey(plane) && node.AIRCRAFT[plane])
                    return true;
            }
            return false;
        }

        private static string RelationDescriptionForGamePlane(Relation relation, string game, string plane)
        {
            string targetGame = NormalizeGameName(game);
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || node.AIRCRAFT == null)
                    continue;
                if (!string.Equals(NormalizeGameName(node.Game), targetGame, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!node.AIRCRAFT.ContainsKey(plane) || !node.AIRCRAFT[plane])
                    continue;

                if (string.Equals(targetGame, "DCS", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (DCSInput input in node.AllInputs ?? new DCSInput[0])
                    {
                        if (input != null && string.Equals((input.Plane ?? string.Empty).Trim(), plane, StringComparison.OrdinalIgnoreCase))
                        {
                            // Never the raw key or id: the DB's readable title, else the stored one (DcsCommandNames).
                            string stored = (input.Title ?? string.Empty).Trim();
                            return DcsCommandNames.IsRawTitle(stored, input.ID ?? node.ID)
                                ? InternalDataManagement.DcsDisplayTitle(input.ID ?? node.ID, plane, stored)
                                : stored;
                        }
                    }
                }
                else
                {
                    foreach (OtherGameInput input in node.OtherInputs ?? new OtherGameInput[0])
                    {
                        if (input != null && string.Equals((input.Plane ?? string.Empty).Trim(), plane, StringComparison.OrdinalIgnoreCase))
                            return (input.Title ?? string.Empty).Trim();
                    }
                }
            }

            return string.Empty;
        }

        private static Dictionary<string, string> BuildModifierRenameMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> rename in InternalDataManagement.ModifierNameChanges ?? new List<KeyValuePair<string, string>>())
            {
                string oldName = (rename.Key ?? string.Empty).Trim();
                string newName = (rename.Value ?? string.Empty).Trim();
                if (oldName.Length > 0 && newName.Length > 0)
                    map[oldName] = newName;
            }
            return map;
        }

        private static Dictionary<string, System.Windows.Point> NormalizeLayoutLabelPositions(Dictionary<string, System.Windows.Point> source)
        {
            Dictionary<string, string> renameMap = BuildModifierRenameMap();
            Dictionary<string, System.Windows.Point> output = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, System.Windows.Point> entry in source ?? new Dictionary<string, System.Windows.Point>())
            {
                string key = NormalizeLayoutLabelToken(entry.Key, renameMap);
                if (key.Length > 0)
                    output[key] = entry.Value;
            }
            return output;
        }

        private static string NormalizeLayoutLabelToken(string token, Dictionary<string, string> renameMap)
        {
            string raw = (token ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;
            string[] parts = raw
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .ToArray();
            if (parts.Length == 0)
                return string.Empty;
            if (parts.Length == 1)
                return parts[0];

            string input = parts[parts.Length - 1];
            List<string> modifiers = new List<string>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string name = parts[i];
                int guard = 0;
                while (renameMap != null && renameMap.TryGetValue(name, out string renamed) && !string.IsNullOrWhiteSpace(renamed) && guard < 8)
                {
                    if (string.Equals(name, renamed.Trim(), StringComparison.OrdinalIgnoreCase))
                        break;
                    name = renamed.Trim();
                    guard++;
                }
                if (name.Length > 0)
                    modifiers.Add(name);
            }

            modifiers = modifiers.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            if (modifiers.Count == 0)
                return input;
            return string.Join("+", modifiers) + "+" + input;
        }

        private static bool ContainsIgnoreCase(string value, string query)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query))
                return false;
            return value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool GameMatchesFilter(string gameName, string filter)
        {
            string game = NormalizeGameKey(gameName);
            string query = NormalizeGameKey(filter);
            if (query.Length == 0)
                return true;
            if (query == "dcs")
                return game == "dcs";
            if (query == "il2" || query == "il2game" || query == "il2sturmovik")
                return game == "il2game";
            if (query == "sc" || query == "starcitizen")
                return game == "starcitizen";
            return game.Contains(query) || query.Contains(game);
        }

        private static string NormalizeGameKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            string raw = value.Trim().ToLowerInvariant();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string NormalizeGameName(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        private static string BuildSafeFileName(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                raw = "joystick";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
                sb.Append(invalid.Contains(raw[i]) ? '_' : raw[i]);
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "joystick" : result;
        }

        private static List<string> NormalizeDistinct(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static void AddUnique(List<string> target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!target.Contains(value))
                target.Add(value);
        }
    }
}
