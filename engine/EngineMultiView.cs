using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // A page with several views of a device (MultiViewLayout, docs/device-reference.md "Several views on one page"):
    // each view's sheet is drawn like a cheat sheet of its own (picture, tables, lines; no header), placed and scaled on
    // the page; the page gets the GAME / PLANE / DEVICE band like every sheet. Used by "layout render" (a --layout-map to
    // a multiview file) and by the Layout page (layout multiview: the views as pictures to place, the page).
    internal static class EngineMultiView
    {
        public static bool IsMultiViewFile(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                       MultiViewLayout.IsMultiView(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static MultiViewLayout Load(string path)
        {
            return MultiViewLayout.Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        // One view at its own size: its sheet with the binds of the device (game null = relation names), or its image.
        public static Bitmap RenderView(MultiViewItem item, string joystick, string game, string plane, bool dark, List<string> warnings)
        {
            if (item.Image.Length > 0)
            {
                if (!File.Exists(item.Image))
                {
                    warnings.Add("The picture of a view is missing: " + item.Image);
                    return null;
                }
                using (Image image = Image.FromFile(item.Image))
                    return new Bitmap(image);
            }
            if (item.Sheet.Length == 0 || !File.Exists(item.Sheet))
            {
                warnings.Add("The sheet of a view is missing: " + item.Sheet);
                return null;
            }
            DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(item.Sheet, Encoding.UTF8), item.Sheet);
            CalloutSheet sheet = EngineCalloutRenderer.SheetOf(set, false, warnings);
            if (sheet == null)
            {
                warnings.Add("A view has no tables: " + item.Sheet);
                return null;
            }
            Func<string, int, string> text = string.IsNullOrWhiteSpace(joystick)
                ? (Func<string, int, string>)((input, column) => string.Empty)
                : EngineLayoutRenderer.CalloutText(joystick, sheet, game, plane);
            CalloutRenderOptions options = new CalloutRenderOptions { Dark = dark, PrintWidth = 0 };
            EngineCalloutRenderer.ApplyStyle(options, set);
            using (Bitmap picture = EngineCalloutRenderer.LoadPicture(set, sheet, null, dark, warnings))
                return EngineCalloutRenderer.Render(sheet, picture, text, options);
        }

        // The whole page with its header band (options: print width, style, game and plane of the header).
        public static Bitmap RenderPage(MultiViewLayout layout, string joystick, string game, string plane, CalloutRenderOptions options, List<string> warnings)
        {
            int w = (int)Math.Round(layout.Width), h = (int)Math.Round(layout.Height);
            using (Bitmap page = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(page))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(EngineCalloutRenderer.PageColor(options.Dark));
                    foreach (MultiViewItem item in layout.Items)
                    {
                        using (Bitmap view = RenderView(item, joystick, game, plane, options.Dark, warnings))
                        {
                            if (view == null)
                                continue;
                            g.DrawImage(view, new RectangleF((float)item.X, (float)item.Y, (float)(view.Width * item.Scale), (float)(view.Height * item.Scale)));
                        }
                    }
                }
                // The band and the print width like every cheat sheet: the page as the picture of an empty sheet.
                CalloutSheet frame = new CalloutSheet
                {
                    Width = w,
                    Height = h,
                    PictureX = 0,
                    PictureY = 0,
                    PictureWidth = w,
                    PictureHeight = h,
                    RowHeight = Math.Max(20, Math.Min(56, Math.Round(Math.Max(w, h) * 0.012)))
                };
                return EngineCalloutRenderer.Render(frame, page, (Func<string, int, string>)null, options);
            }
        }
    }

    // layout multiview <file.json> [--views <folder>] [--png <file>] [--device <name>] [--game <g> --plane <p>]
    //     [--light] [--print-width <px>]: the views at their own size as view-<n>.png (the Layout page places them) and
    //     the page as one picture.
    internal static partial class Program
    {
        private static int RunLayoutMultiView(List<string> args, string outPath, bool textOutput)
        {
            string views = PopOptionValue(args, "--views") ?? string.Empty;
            string png = PopOptionValue(args, "--png") ?? string.Empty;
            string device = PopOptionValue(args, "--device") ?? string.Empty;
            string game = PopOptionValue(args, "--game") ?? string.Empty;
            string plane = PopOptionValue(args, "--plane") ?? string.Empty;
            int printWidth = PopOptionInt(args, "--print-width", 3508);
            bool light = PopFlag(args, "--light");
            if (args.Count != 1)
                return Fail("layout multiview <file.json> [--views <folder>] [--png <file>] [--device <name>] [--game <g> --plane <p>] [--light] [--print-width <px>]");
            string file = NormalizePath(args[0]);
            if (!EngineMultiView.IsMultiViewFile(file))
                return Fail("Not a page with several views: " + args[0]);
            MultiViewLayout layout = EngineMultiView.Load(file);
            List<string> warnings = new List<string>();
            List<object> written = new List<object>();
            if (views.Length > 0)
            {
                string folder = NormalizePath(views);
                Directory.CreateDirectory(folder);
                for (int i = 0; i < layout.Items.Count; i++)
                {
                    using (Bitmap view = EngineMultiView.RenderView(layout.Items[i], device, game.Length > 0 ? game : null, plane, !light, warnings))
                    {
                        if (view == null)
                            continue;
                        string target = Path.Combine(folder, "view-" + i.ToString(CultureInfo.InvariantCulture) + ".png");
                        view.Save(target, ImageFormat.Png);
                        written.Add(new Dictionary<string, object> { { "Index", i }, { "File", target }, { "Width", view.Width }, { "Height", view.Height } });
                    }
                }
            }
            if (png.Length > 0)
            {
                string alias = device.Length > 0 ? EngineLayoutRenderer.ResolveJoystickAlias(device) : layout.Model;
                LayoutVisualOptions visual = new LayoutVisualOptions { LightStyle = light, PrintWidth = Math.Max(0, Math.Min(12000, printWidth)) };
                CalloutRenderOptions options = game.Length > 0
                    ? EngineLayoutRenderer.CalloutOptions(visual, game, plane, alias)
                    : EngineLayoutRenderer.CalloutOptions(visual, "All games", "Relation names", alias);
                string target = NormalizePath(png);
                string directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using (Bitmap page = EngineMultiView.RenderPage(layout, device, game.Length > 0 ? game : null, plane, options, warnings))
                    page.Save(target, ImageFormat.Png);
            }
            EmitPayload(EngineSerialization.ToJson(new Dictionary<string, object>
            {
                { "Model", layout.Model }, { "Page", layout.Page }, { "Width", layout.Width }, { "Height", layout.Height }, { "Views", written }, { "Png", png },
                { "Warnings", warnings }
            }), outPath);
            return 0;
        }
    }
}
