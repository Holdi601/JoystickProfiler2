using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // The sheet style options of "layout render", "layout callouts" and "layout multiview" (SheetStyle; docs/ui/
    // layout-redesign.md "Sheet style"): --sheet-style <preset|file.json> as the base, the single options over it.
    //   --font <family> --font-size <n> --font-weight normal|semibold|bold
    //   --text-color --outline-color --label-background (labels on a layout picture; "none" = no halo/box)
    //   --page-background --table-background --table-border --header-background --header-text --table-text --line-color
    //   --marker-color-button --marker-color-axis --marker-color-hat (--marker-color-pov) --marker-color-slider
    //   --modifier-colors <name=#RRGGBB,...>
    // Colours: #RRGGBB or #AARRGGBB. The label options and --modifier-colors keep their old meaning (an unreadable
    // colour is left out); a new colour option with no colour is an error.
    internal static partial class Program
    {
        internal const string SheetStyleUsage =
            "[--sheet-style joypro|print|high-contrast|dark-sheet|<style.json>] [--font <family>] [--font-size <n>] [--font-weight normal|semibold|bold] " +
            "[--text-color <#RRGGBB|#AARRGGBB>] [--outline-color <color|none>] [--label-background <color|none>] [--page-background <color>] " +
            "[--table-background <color>] [--table-border <color>] [--header-background <color>] [--header-text <color>] [--table-text <color>] " +
            "[--line-color <color>] [--marker-color-button <color>] [--marker-color-axis <color>] [--marker-color-hat <color>] [--marker-color-slider <color>] " +
            "[--modifier-colors <modifier=#RRGGBB,...>]";

        // The style the options name; null when none of them was given. error: an unreadable value.
        internal static SheetStyle PopSheetStyleOptions(List<string> args, out string error)
        {
            error = string.Empty;
            bool any = false;
            SheetStyle style = new SheetStyle();
            string baseSpec = PopOptionValue(args, "--sheet-style");
            if (!string.IsNullOrWhiteSpace(baseSpec))
            {
                any = true;
                string spec = baseSpec.Trim();
                if (SheetStyle.IsPresetId(spec))
                {
                    style = SheetStyle.Preset(spec);
                }
                else
                {
                    string file = NormalizePath(spec);
                    SheetStyle loaded = File.Exists(file) ? SheetStyle.Parse(File.ReadAllText(file, Encoding.UTF8)) : null;
                    if (loaded == null)
                    {
                        error = "Invalid --sheet-style '" + spec + "': use joypro, print, high-contrast, dark-sheet or a sheet style file (.json).";
                        return null;
                    }
                    style = loaded;
                }
            }

            SheetStyle given = new SheetStyle();
            string font = PopOptionValue(args, "--font");
            if (font != null)
            {
                any = true;
                given.FontFamily = font.Trim();
            }
            string size = PopOptionValue(args, "--font-size");
            if (!string.IsNullOrWhiteSpace(size))
            {
                any = true;
                double parsed;
                if (!double.TryParse(size.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > 200)
                {
                    error = "Invalid value for option --font-size: '" + size + "'. Use a number (0 = the layout's own size).";
                    return null;
                }
                given.FontSize = parsed;
            }
            string weight = PopOptionValue(args, "--font-weight");
            if (!string.IsNullOrWhiteSpace(weight))
            {
                any = true;
                given.FontWeight = weight.Trim();
                if (given.NormalizedWeight.Length == 0 || (given.NormalizedWeight == "normal" && !string.Equals(weight.Trim(), "normal", StringComparison.OrdinalIgnoreCase) &&
                                                          !string.Equals(weight.Trim(), "regular", StringComparison.OrdinalIgnoreCase) && weight.Trim() != "400"))
                {
                    error = "Invalid value for option --font-weight: '" + weight + "'. Use normal, semibold or bold.";
                    return null;
                }
            }
            // The old label options: as before, a value that is no colour is left out.
            given.TextColor = Lenient(args, "--text-color", ref any);
            given.OutlineColor = Lenient(args, "--outline-color", ref any);
            given.LabelBackground = Lenient(args, "--label-background", ref any);
            string modifiers = PopOptionValue(args, "--modifier-colors");
            if (modifiers != null)
            {
                any = true;
                given.Modifiers = SheetStyle.ParseModifierCsv(modifiers);
            }

            string[] strict =
            {
                "--page-background", "--table-background", "--table-border", "--header-background", "--header-text", "--table-text", "--line-color",
                "--marker-color-button", "--marker-color-axis", "--marker-color-hat", "--marker-color-pov", "--marker-color-slider"
            };
            foreach (string option in strict)
            {
                string raw = PopOptionValue(args, option);
                if (raw == null)
                    continue;
                any = true;
                string color = SheetStyle.NormalizeColor(raw);
                if (color.Length == 0)
                {
                    error = "Invalid value for option " + option + ": '" + raw + "'. Use #RRGGBB or #AARRGGBB.";
                    return null;
                }
                switch (option)
                {
                    case "--page-background": given.PageBackground = color; break;
                    case "--table-background": given.TableBackground = color; break;
                    case "--table-border": given.TableBorder = color; break;
                    case "--header-background": given.HeaderBackground = color; break;
                    case "--header-text": given.HeaderText = color; break;
                    case "--table-text": given.TableText = color; break;
                    case "--line-color": given.LineColor = color; break;
                    case "--marker-color-button": given.MarkerButton = color; break;
                    case "--marker-color-axis": given.MarkerAxis = color; break;
                    case "--marker-color-hat":
                    case "--marker-color-pov": given.MarkerHat = color; break;
                    case "--marker-color-slider": given.MarkerSlider = color; break;
                }
            }
            if (!any)
                return null;
            style.Overlay(given);
            // "--font ''" names JoyPro's font again over a style file's font.
            if (font != null && font.Trim().Length == 0)
                style.FontFamily = string.Empty;
            return style;
        }

        private static string Lenient(List<string> args, string option, ref bool any)
        {
            string raw = PopOptionValue(args, option);
            if (raw == null)
                return string.Empty;
            any = true;
            return SheetStyle.NormalizeColor(raw);
        }
    }
}
