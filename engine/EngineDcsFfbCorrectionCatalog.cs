using System;
using System.Collections.Generic;
using System.IO;

namespace JoyPro.Engine
{
    internal sealed class EngineDcsFfbPlaneCorrection
    {
        public bool InvertX { get; set; }
        public bool InvertY { get; set; }
        public bool SwapAxis { get; set; }

        public EngineDcsFfbPlaneCorrection()
        {
            InvertX = false;
            InvertY = false;
            SwapAxis = false;
        }
    }

    internal static class EngineDcsFfbCorrectionCatalog
    {
        public static Dictionary<string, EngineDcsFfbPlaneCorrection> Load(List<string> warnings = null)
        {
            Dictionary<string, EngineDcsFfbPlaneCorrection> output =
                new Dictionary<string, EngineDcsFfbPlaneCorrection>(StringComparer.OrdinalIgnoreCase);

            string path = ResolveCorrectionPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return output;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read DCS FFB correction file '" + path + "': " + ex.Message);
                return output;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? string.Empty;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                string[] parts = line.Split(new[] { '\u00A7' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                string plane = (parts[0] ?? string.Empty).Trim();
                if (plane.Length == 0)
                    continue;

                EngineDcsFfbPlaneCorrection correction = new EngineDcsFfbPlaneCorrection();
                for (int p = 1; p < parts.Length; p++)
                {
                    string part = (parts[p] ?? string.Empty).Trim();
                    if (part.Length == 0)
                        continue;

                    int equals = part.IndexOf('=');
                    if (equals <= 0 || equals >= part.Length - 1)
                        continue;

                    string key = part.Substring(0, equals).Trim().ToLowerInvariant();
                    string value = part.Substring(equals + 1).Trim();
                    if (!TryParseBoolToken(value, out bool parsed))
                        continue;

                    switch (key)
                    {
                        case "x":
                        case "invertx":
                            correction.InvertX = parsed;
                            break;
                        case "y":
                        case "inverty":
                            correction.InvertY = parsed;
                            break;
                        case "swap":
                        case "swapaxis":
                        case "swapaxes":
                            correction.SwapAxis = parsed;
                            break;
                    }
                }

                output[plane] = correction;
            }

            return output;
        }

        private static string ResolveCorrectionPath()
        {
            string root = MainStructure.PROGPATH;
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.CurrentDirectory;

            try
            {
                return Path.Combine(root, "TOOLS", "Conversions", "DCSFFBCorrection.ffb");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseBoolToken(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string token = value.Trim();
            if (bool.TryParse(token, out parsed))
                return true;

            if (token == "1")
            {
                parsed = true;
                return true;
            }
            if (token == "0")
            {
                parsed = false;
                return true;
            }

            return false;
        }
    }
}
