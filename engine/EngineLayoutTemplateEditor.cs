using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace JoyPro.Engine
{
    public sealed class LayoutTemplateMarkerDto
    {
        public string Token { get; set; }
        public string Type { get; set; }
        public string Modifier { get; set; }
        public float X { get; set; }
        public float Y { get; set; }

        public LayoutTemplateMarkerDto()
        {
            Token = string.Empty;
            Type = string.Empty;
            Modifier = string.Empty;
            X = 0;
            Y = 0;
        }
    }

    public sealed class LayoutTemplateTableDto
    {
        public string Name { get; set; }
        public string Mode { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public int Columns { get; set; }
        public string BackgroundColor { get; set; }
        public float BackgroundOpacity { get; set; }
        public bool DropShadow { get; set; }

        public LayoutTemplateTableDto()
        {
            Name = string.Empty;
            Mode = "panel";
            X = 0;
            Y = 0;
            Width = 320;
            Height = 220;
            Columns = 1;
            BackgroundColor = "#F6F8FB";
            BackgroundOpacity = 0.93f;
            DropShadow = true;
        }
    }

    public sealed class LayoutTemplateConnectorPointDto
    {
        public float X { get; set; }
        public float Y { get; set; }

        public LayoutTemplateConnectorPointDto()
        {
            X = 0;
            Y = 0;
        }
    }

    public sealed class LayoutTemplateConnectorDto
    {
        public string Name { get; set; }
        public string Color { get; set; }
        public float Width { get; set; }
        public string Route { get; set; }
        public int Steps { get; set; }
        public string TargetToken { get; set; }
        public List<LayoutTemplateConnectorPointDto> Points { get; set; }

        public LayoutTemplateConnectorDto()
        {
            Name = "Connector";
            Color = "#E2E8F0";
            Width = 2f;
            Route = "orthogonal";
            Steps = 2;
            TargetToken = string.Empty;
            Points = new List<LayoutTemplateConnectorPointDto>();
        }
    }

    public sealed class LayoutTemplateInspectResult
    {
        public string SourcePath { get; set; }
        public string PreviewImagePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        // The device the layout was made for (JoyPro 1: the DirectInput name with its GUID), and the text look JoyPro 1
        // wrote the bound commands with (font, size in picture pixels, #AARRGGBB).
        public string Joystick { get; set; }
        public string FontName { get; set; }
        public int FontSize { get; set; }
        public string TextColor { get; set; }
        // Who made the sheet (a 2D layout variant), shown with the picture.
        public string Credit { get; set; }
        public int MarkerCount { get; set; }
        public List<LayoutTemplateMarkerDto> Markers { get; set; }
        public int TableCount { get; set; }
        public List<LayoutTemplateTableDto> Tables { get; set; }
        public int ConnectorCount { get; set; }
        public List<LayoutTemplateConnectorDto> Connectors { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public LayoutTemplateInspectResult()
        {
            SourcePath = string.Empty;
            PreviewImagePath = string.Empty;
            Width = 0;
            Height = 0;
            Joystick = string.Empty;
            FontName = string.Empty;
            FontSize = 0;
            TextColor = string.Empty;
            Credit = string.Empty;
            MarkerCount = 0;
            Markers = new List<LayoutTemplateMarkerDto>();
            TableCount = 0;
            Tables = new List<LayoutTemplateTableDto>();
            ConnectorCount = 0;
            Connectors = new List<LayoutTemplateConnectorDto>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class LayoutTemplateSaveResult
    {
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public int MarkerCount { get; set; }
        public int TableCount { get; set; }
        public int ConnectorCount { get; set; }
        public bool ReplacedAll { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public LayoutTemplateSaveResult()
        {
            SourcePath = string.Empty;
            OutputPath = string.Empty;
            MarkerCount = 0;
            TableCount = 0;
            ConnectorCount = 0;
            ReplacedAll = true;
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal static class EngineLayoutTemplateEditor
    {
        public static LayoutTemplateInspectResult Inspect(string sourcePath, int maxMarkers)
        {
            LayoutTemplateInspectResult result = new LayoutTemplateInspectResult();
            string normalizedSource = NormalizePath(sourcePath);
            result.SourcePath = normalizedSource;
            if (string.IsNullOrWhiteSpace(normalizedSource) || !File.Exists(normalizedSource))
            {
                result.Errors.Add("Layout source not found: " + sourcePath);
                return result;
            }

            if (!TryLoadLayoutOrImage(normalizedSource, out LayoutFile layout, out string error))
            {
                result.Errors.Add("Failed to load layout source: " + error);
                return result;
            }

            try
            {
                if (layout == null || layout.backup == null)
                {
                    result.Errors.Add("Layout source has no image data.");
                    return result;
                }

                result.Width = layout.backup.Width;
                result.Height = layout.backup.Height;
                result.Joystick = (layout.Joystick ?? string.Empty).Trim();
                result.FontName = (layout.Font ?? string.Empty).Trim();
                result.FontSize = layout.Size;
                result.Credit = (layout.Credit ?? string.Empty).Trim();
                Color text = layout.ToDrawingColor();
                result.TextColor = "#" + text.A.ToString("X2", CultureInfo.InvariantCulture) + text.R.ToString("X2", CultureInfo.InvariantCulture) +
                                   text.G.ToString("X2", CultureInfo.InvariantCulture) + text.B.ToString("X2", CultureInfo.InvariantCulture);
                result.PreviewImagePath = EnsurePreviewImage(normalizedSource, layout.backup, result.Warnings);

                int markerLimit = maxMarkers <= 0 ? 5000 : Math.Min(maxMarkers, 20000);
                IEnumerable<KeyValuePair<string, System.Windows.Point>> sourceMarkers = layout.Positions ?? new Dictionary<string, System.Windows.Point>();
                foreach (KeyValuePair<string, System.Windows.Point> pair in sourceMarkers
                             .OrderBy(v => v.Value.Y)
                             .ThenBy(v => v.Value.X)
                             .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                             .Take(markerLimit))
                {
                    string rawToken = (pair.Key ?? string.Empty).Trim();
                    if (rawToken.Length == 0)
                        continue;
                    string token = ExtractInputToken(rawToken);
                    if (token.Length == 0)
                        continue;
                    LayoutTemplateMarkerDto marker = new LayoutTemplateMarkerDto();
                    marker.Token = token;
                    marker.Type = InferMarkerType(token);
                    marker.Modifier = ExtractModifiers(rawToken);
                    marker.X = (float)pair.Value.X;
                    marker.Y = (float)pair.Value.Y;
                    result.Markers.Add(marker);
                }

                result.MarkerCount = (layout.Positions ?? new Dictionary<string, System.Windows.Point>()).Count;
                if (result.MarkerCount > result.Markers.Count)
                {
                    result.Warnings.Add(
                        "Marker list truncated to " +
                        result.Markers.Count.ToString(CultureInfo.InvariantCulture) +
                        " entries.");
                }

                result.Tables = LoadTablesForSource(normalizedSource, result.Warnings);
                result.TableCount = result.Tables.Count;
                result.Connectors = LoadConnectorsForSource(normalizedSource, result.Warnings);
                result.ConnectorCount = result.Connectors.Count;
            }
            finally
            {
                if (layout != null && layout.backup != null)
                    layout.backup.Dispose();
            }

            return result;
        }

        public static LayoutTemplateSaveResult Save(
            string sourcePath,
            string outputPath,
            List<LayoutTemplateMarkerDto> markers,
            List<LayoutTemplateTableDto> tables,
            List<LayoutTemplateConnectorDto> connectors,
            bool replaceAll)
        {
            LayoutTemplateSaveResult result = new LayoutTemplateSaveResult();
            string normalizedSource = NormalizePath(sourcePath);
            string normalizedOutput = NormalizePath(outputPath);
            result.SourcePath = normalizedSource;
            result.OutputPath = normalizedOutput;
            result.ReplacedAll = replaceAll;

            if (string.IsNullOrWhiteSpace(normalizedSource) || !File.Exists(normalizedSource))
            {
                result.Errors.Add("Layout source not found: " + sourcePath);
                return result;
            }

            if (string.IsNullOrWhiteSpace(normalizedOutput))
            {
                result.Errors.Add("Output path is required.");
                return result;
            }

            if (!normalizedOutput.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
                normalizedOutput += ".layout";
            result.OutputPath = normalizedOutput;

            if (!TryLoadLayoutOrImage(normalizedSource, out LayoutFile layout, out string error))
            {
                result.Errors.Add("Failed to load layout source: " + error);
                return result;
            }

            try
            {
                if (layout == null || layout.backup == null)
                {
                    result.Errors.Add("Layout source has no image data.");
                    return result;
                }

                Dictionary<string, System.Windows.Point> rebuilt = replaceAll
                    ? new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, System.Windows.Point>(layout.Positions ?? new Dictionary<string, System.Windows.Point>(), StringComparer.OrdinalIgnoreCase);

                foreach (LayoutTemplateMarkerDto marker in markers ?? new List<LayoutTemplateMarkerDto>())
                {
                    if (marker == null)
                        continue;
                    string token = (marker.Token ?? string.Empty).Trim();
                    if (token.Length == 0)
                        continue;
                    string storedToken = BuildStoredMarkerToken(token, marker.Modifier);
                    rebuilt[storedToken] = new System.Windows.Point(marker.X, marker.Y);
                }

                layout.Positions = rebuilt;
                if (string.IsNullOrWhiteSpace(layout.Joystick))
                    layout.Joystick = Path.GetFileNameWithoutExtension(normalizedOutput);
                if (string.IsNullOrWhiteSpace(layout.Font))
                    layout.Font = "Arial";
                if (layout.Size <= 0)
                    layout.Size = 12;

                string directory = Path.GetDirectoryName(normalizedOutput);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                using (FileStream stream = File.Open(normalizedOutput, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    formatter.Binder = new LegacyJoyProSerializationBinder();
                    formatter.Serialize(stream, layout);
                }

                result.MarkerCount = rebuilt.Count;
                List<LayoutTemplateTableDto> normalizedTables = NormalizeTables(tables);
                SaveTablesForOutput(normalizedOutput, normalizedTables, result.Warnings);
                result.TableCount = normalizedTables.Count;
                List<LayoutTemplateConnectorDto> normalizedConnectors = NormalizeConnectors(connectors);
                SaveConnectorsForOutput(normalizedOutput, normalizedConnectors, result.Warnings);
                result.ConnectorCount = normalizedConnectors.Count;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed to write layout template: " + ex.Message);
            }
            finally
            {
                if (layout != null && layout.backup != null)
                    layout.backup.Dispose();
            }

            return result;
        }

        public static string ToText(LayoutTemplateInspectResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Layout Template Inspect");
            sb.AppendLine("Source: " + result.SourcePath);
            sb.AppendLine("Preview: " + (string.IsNullOrWhiteSpace(result.PreviewImagePath) ? "<none>" : result.PreviewImagePath));
            sb.AppendLine("Size: " + result.Width.ToString(CultureInfo.InvariantCulture) + "x" + result.Height.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Device: " + (string.IsNullOrWhiteSpace(result.Joystick) ? "<none>" : result.Joystick));
            sb.AppendLine("Text: " + (string.IsNullOrWhiteSpace(result.FontName) ? "<default>" : result.FontName) + " " + result.FontSize.ToString(CultureInfo.InvariantCulture) + " " + result.TextColor);
            sb.AppendLine("Markers: " + result.MarkerCount.ToString(CultureInfo.InvariantCulture));
            foreach (LayoutTemplateMarkerDto marker in result.Markers.Take(200))
            {
                sb.AppendLine(
                    "  - " +
                    marker.Token +
                    " | type=" +
                    marker.Type +
                    " | x=" +
                    marker.X.ToString(CultureInfo.InvariantCulture) +
                    " | y=" +
                    marker.Y.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("Tables: " + result.TableCount.ToString(CultureInfo.InvariantCulture));
            foreach (LayoutTemplateTableDto table in result.Tables.Take(100))
            {
                sb.AppendLine(
                    "  - " +
                    table.Name +
                    " | mode=" +
                    table.Mode +
                    " | x=" +
                    table.X.ToString(CultureInfo.InvariantCulture) +
                    " | y=" +
                    table.Y.ToString(CultureInfo.InvariantCulture) +
                    " | w=" +
                    table.Width.ToString(CultureInfo.InvariantCulture) +
                    " | h=" +
                    table.Height.ToString(CultureInfo.InvariantCulture) +
                    " | cols=" +
                    table.Columns.ToString(CultureInfo.InvariantCulture) +
                    " | bg=" +
                    table.BackgroundColor +
                    " | opacity=" +
                    table.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture) +
                    " | shadow=" +
                    table.DropShadow.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("Connectors: " + result.ConnectorCount.ToString(CultureInfo.InvariantCulture));
            foreach (LayoutTemplateConnectorDto connector in result.Connectors.Take(200))
            {
                sb.AppendLine(
                    "  - " +
                    (string.IsNullOrWhiteSpace(connector.Name) ? "Connector" : connector.Name) +
                    " | color=" +
                    (connector.Color ?? string.Empty) +
                    " | width=" +
                    connector.Width.ToString(CultureInfo.InvariantCulture) +
                    " | route=" +
                    connector.Route +
                    " | steps=" +
                    connector.Steps.ToString(CultureInfo.InvariantCulture) +
                    " | target=" +
                    connector.TargetToken +
                    " | points=" +
                    (connector.Points == null ? 0 : connector.Points.Count).ToString(CultureInfo.InvariantCulture));
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }

        public static string ToText(LayoutTemplateSaveResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Layout Template Save");
            sb.AppendLine("Source: " + result.SourcePath);
            sb.AppendLine("Output: " + result.OutputPath);
            sb.AppendLine("ReplaceAll: " + result.ReplacedAll.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("MarkerCount: " + result.MarkerCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("TableCount: " + result.TableCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ConnectorCount: " + result.ConnectorCount.ToString(CultureInfo.InvariantCulture));
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }

        private static string EnsurePreviewImage(string sourcePath, Bitmap image, List<string> warnings)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (IsImageExtension(ext))
                return sourcePath;

            try
            {
                string dir = PreviewFolder();
                Directory.CreateDirectory(dir);

                string hash = Math.Abs((sourcePath + "|" + File.GetLastWriteTimeUtc(sourcePath).Ticks.ToString(CultureInfo.InvariantCulture)).GetHashCode())
                    .ToString("X8", CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, "preview_" + hash + ".png");
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                RemoveOldPreviews(dir, path);
                return path;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not write preview image: " + ex.Message);
                return string.Empty;
            }
        }

        // The preview pictures of layout files: <data>\LayoutPreview (JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro2).
        internal static string PreviewFolder()
        {
            return Path.Combine(global::JoyPro.Logging.JoyProDataFolder.Root(), "LayoutPreview");
        }

        // Previews are a cache: the ones not written for 30 days go (a new one is made when the layout is opened again).
        private static void RemoveOldPreviews(string dir, string keep)
        {
            try
            {
                DateTime limit = DateTime.UtcNow.AddDays(-30);
                foreach (string file in Directory.GetFiles(dir, "preview_*.png"))
                {
                    if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.GetLastWriteTimeUtc(file) < limit)
                        File.Delete(file);
                }
            }
            catch (Exception)
            {
                // A preview in use or a folder that cannot be listed stays; nothing depends on it.
            }
        }

        private static bool TryLoadLayoutOrImage(string sourcePath, out LayoutFile layout, out string error)
        {
            layout = null;
            error = string.Empty;

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (IsImageExtension(ext))
            {
                try
                {
                    layout = new LayoutFile();
                    layout.Joystick = Path.GetFileNameWithoutExtension(sourcePath);
                    layout.Font = "Arial";
                    layout.Size = 12;
                    layout.KneeboardPostfix = string.Empty;
                    layout.Positions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
                    layout.backup = new Bitmap(sourcePath);
                    return true;
                }
                catch (Exception ex)
                {
                    error = "failed to load image source '" + sourcePath + "': " + ex.Message;
                    return false;
                }
            }

            if (EngineLayoutRenderer.TryLoadLayoutTemplate(sourcePath, out LayoutFile loadedLayout, out string layoutError))
            {
                layout = loadedLayout;
                return true;
            }

            // As a fallback, try to open unknown extensions as plain image files.
            try
            {
                Bitmap image = new Bitmap(sourcePath);
                layout = new LayoutFile();
                layout.Joystick = Path.GetFileNameWithoutExtension(sourcePath);
                layout.Font = "Arial";
                layout.Size = 12;
                layout.KneeboardPostfix = string.Empty;
                layout.Positions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
                layout.backup = image;
                return true;
            }
            catch
            {
                error = layoutError;
                return false;
            }
        }

        private static bool IsImageExtension(string ext)
        {
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildTablesSidecarPath(string sourceOrOutputPath)
        {
            return NormalizePath(sourceOrOutputPath) + ".tables.json";
        }

        private static string BuildConnectorsSidecarPath(string sourceOrOutputPath)
        {
            return NormalizePath(sourceOrOutputPath) + ".connectors.json";
        }

        private static List<LayoutTemplateTableDto> LoadTablesForSource(string sourcePath, List<string> warnings)
        {
            List<LayoutTemplateTableDto> tables = new List<LayoutTemplateTableDto>();
            string sidecarPath = BuildTablesSidecarPath(sourcePath);
            if (!File.Exists(sidecarPath))
                return tables;

            try
            {
                string json = File.ReadAllText(sidecarPath);
                if (string.IsNullOrWhiteSpace(json))
                    return tables;

                List<LayoutTemplateTableDto> parsed = EngineSerialization.FromJson<List<LayoutTemplateTableDto>>(json);
                if (parsed == null)
                    return tables;

                tables = NormalizeTables(parsed);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not load table sidecar '" + sidecarPath + "': " + ex.Message);
            }

            return tables;
        }

        private static List<LayoutTemplateConnectorDto> LoadConnectorsForSource(string sourcePath, List<string> warnings)
        {
            List<LayoutTemplateConnectorDto> connectors = new List<LayoutTemplateConnectorDto>();
            string sidecarPath = BuildConnectorsSidecarPath(sourcePath);
            if (!File.Exists(sidecarPath))
                return connectors;

            try
            {
                string json = File.ReadAllText(sidecarPath);
                if (string.IsNullOrWhiteSpace(json))
                    return connectors;

                List<LayoutTemplateConnectorDto> parsed = EngineSerialization.FromJson<List<LayoutTemplateConnectorDto>>(json);
                if (parsed == null)
                    return connectors;

                connectors = NormalizeConnectors(parsed);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not load connector sidecar '" + sidecarPath + "': " + ex.Message);
            }

            return connectors;
        }

        private static void SaveTablesForOutput(string outputPath, List<LayoutTemplateTableDto> tables, List<string> warnings)
        {
            string sidecarPath = BuildTablesSidecarPath(outputPath);
            try
            {
                string directory = Path.GetDirectoryName(sidecarPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string json = EngineSerialization.ToJson(tables ?? new List<LayoutTemplateTableDto>());
                File.WriteAllText(sidecarPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not save table sidecar '" + sidecarPath + "': " + ex.Message);
            }
        }

        private static void SaveConnectorsForOutput(string outputPath, List<LayoutTemplateConnectorDto> connectors, List<string> warnings)
        {
            string sidecarPath = BuildConnectorsSidecarPath(outputPath);
            try
            {
                string directory = Path.GetDirectoryName(sidecarPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string json = EngineSerialization.ToJson(connectors ?? new List<LayoutTemplateConnectorDto>());
                File.WriteAllText(sidecarPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not save connector sidecar '" + sidecarPath + "': " + ex.Message);
            }
        }

        private static List<LayoutTemplateTableDto> NormalizeTables(IEnumerable<LayoutTemplateTableDto> tables)
        {
            return (tables ?? Enumerable.Empty<LayoutTemplateTableDto>())
                .Where(t => t != null)
                .Select(t =>
                {
                    LayoutTemplateTableDto normalized = new LayoutTemplateTableDto();
                    normalized.Name = string.IsNullOrWhiteSpace(t.Name) ? "Table" : t.Name.Trim();
                    normalized.Mode = NormalizeTableMode(t.Mode);
                    normalized.X = Math.Max(0, t.X);
                    normalized.Y = Math.Max(0, t.Y);
                    normalized.Width = Math.Max(120, t.Width);
                    normalized.Height = Math.Max(70, t.Height);
                    normalized.Columns = Math.Max(1, t.Columns);
                    normalized.BackgroundColor = NormalizeColorHex(t.BackgroundColor, "#F6F8FB");
                    normalized.BackgroundOpacity = Clamp01(t.BackgroundOpacity, 0.93f);
                    normalized.DropShadow = t.DropShadow;
                    return normalized;
                })
                .ToList();
        }

        private static List<LayoutTemplateConnectorDto> NormalizeConnectors(IEnumerable<LayoutTemplateConnectorDto> connectors)
        {
            return (connectors ?? Enumerable.Empty<LayoutTemplateConnectorDto>())
                .Where(c => c != null)
                .Select(c =>
                {
                    LayoutTemplateConnectorDto normalized = new LayoutTemplateConnectorDto();
                    normalized.Name = string.IsNullOrWhiteSpace(c.Name) ? "Connector" : c.Name.Trim();
                    normalized.Color = string.IsNullOrWhiteSpace(c.Color) ? "#E2E8F0" : c.Color.Trim();
                    normalized.Width = ClampConnectorWidth(c.Width);
                    normalized.Route = NormalizeConnectorRoute(c.Route);
                    normalized.Steps = ClampConnectorSteps(c.Steps);
                    normalized.TargetToken = string.IsNullOrWhiteSpace(c.TargetToken) ? string.Empty : c.TargetToken.Trim();
                    normalized.Points = NormalizeConnectorPoints(c.Points);
                    return normalized;
                })
                .Where(c => c.Points.Count >= 2)
                .ToList();
        }

        private static List<LayoutTemplateConnectorPointDto> NormalizeConnectorPoints(IEnumerable<LayoutTemplateConnectorPointDto> points)
        {
            List<LayoutTemplateConnectorPointDto> normalized = new List<LayoutTemplateConnectorPointDto>();
            foreach (LayoutTemplateConnectorPointDto point in points ?? Enumerable.Empty<LayoutTemplateConnectorPointDto>())
            {
                if (point == null)
                    continue;
                LayoutTemplateConnectorPointDto next = new LayoutTemplateConnectorPointDto();
                next.X = Math.Max(0f, point.X);
                next.Y = Math.Max(0f, point.Y);
                normalized.Add(next);
            }

            return normalized;
        }

        private static string NormalizeTableMode(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "list")
                return "list";
            return "panel";
        }

        private static string NormalizeColorHex(string value, string fallback)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                raw = fallback;
            if (!raw.StartsWith("#", StringComparison.Ordinal))
                raw = "#" + raw;
            if (raw.Length != 7 && raw.Length != 9)
                return fallback;
            return raw.ToUpperInvariant();
        }

        private static float Clamp01(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return fallback;
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }

        private static string NormalizeConnectorRoute(string route)
        {
            string normalized = (route ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "diagonal")
                return "diagonal";
            if (normalized == "manual")
                return "manual";
            return "orthogonal";
        }

        private static int ClampConnectorSteps(int steps)
        {
            if (steps < 0)
                return 0;
            if (steps > 8)
                return 8;
            return steps;
        }

        private static float ClampConnectorWidth(float width)
        {
            if (width < 1f)
                return 1f;
            if (width > 12f)
                return 12f;
            return width;
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

        private static string ExtractInputToken(string token)
        {
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;
            string[] parts = value
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .ToArray();
            if (parts.Length == 0)
                return string.Empty;
            return parts[parts.Length - 1];
        }

        private static List<string> ExtractModifierNames(string token)
        {
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
                return new List<string>();
            string[] parts = value
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .ToArray();
            if (parts.Length <= 1)
                return new List<string>();
            return parts
                .Take(parts.Length - 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ExtractModifierNamesFromCsv(string csv)
        {
            return (csv ?? string.Empty)
                .Split(new[] { ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ExtractModifiers(string token)
        {
            List<string> modifiers = ExtractModifierNames(token);
            if (modifiers.Count == 0)
                return string.Empty;
            return string.Join(",", modifiers);
        }

        private static string BuildStoredMarkerToken(string token, string modifiersCsv)
        {
            string input = ExtractInputToken(token);
            if (input.Length == 0)
                input = (token ?? string.Empty).Trim();
            if (input.Length == 0)
                return string.Empty;

            List<string> modifiers = ExtractModifierNamesFromCsv(modifiersCsv);
            if (modifiers.Count == 0)
                modifiers = ExtractModifierNames(token);
            if (modifiers.Count == 0)
                return input;
            return string.Join("+", modifiers) + "+" + input;
        }
    }
}
