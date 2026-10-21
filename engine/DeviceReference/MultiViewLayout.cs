#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // A page with several views of one device (docs/device-reference.md "Several views on one page"): JoyPro 1 users
    // combined front and side pictures in one layout so every button is covered. Each view is one picture of the model
    // with its own callout sheet (markers, tables and lines: DeviceReference\Callouts\<model>.<picture>.json), placed on
    // the page at X/Y (page pixels, the view's top left) and scaled; the view's tables and lines move and scale with it.
    // A view can also be a plain image of the user's (no markers). The page has a print or screen format. Saved per
    // model (DeviceReference\MultiView\<model>.json); the cheat sheets draw the whole page (engine layout render).
    public sealed class MultiViewItem
    {
        // The view's callout sheet (a device layout set file with "callouts"); empty for a plain image.
        public string Sheet = string.Empty;
        // A plain image (no markers): its file.
        public string Image = string.Empty;
        public string PictureKey = string.Empty;
        public string Title = string.Empty;
        // Top left on the page and the factor of the sheet's (image's) own pixels.
        public double X;
        public double Y;
        public double Scale = 1.0;
        // The sheet's (image's) own size when it was placed (for the page without loading it).
        public double Width;
        public double Height;

        public MultiViewItem Clone()
        {
            return (MultiViewItem)MemberwiseClone();
        }
    }

    public sealed class MultiViewPage
    {
        public string Id;
        public string Title;
        public double Width;
        public double Height;
    }

    public sealed class MultiViewLayout
    {
        public const string Format = "joypro-multiview";

        // Print at 300 dpi, screens at their usual pixel size.
        public static readonly List<MultiViewPage> Pages = new List<MultiViewPage>
        {
            new MultiViewPage { Id = "a4-landscape", Title = "A4 landscape", Width = 3508, Height = 2480 },
            new MultiViewPage { Id = "a4-portrait", Title = "A4 portrait", Width = 2480, Height = 3508 },
            new MultiViewPage { Id = "letter-landscape", Title = "Letter landscape", Width = 3300, Height = 2550 },
            new MultiViewPage { Id = "letter-portrait", Title = "Letter portrait", Width = 2550, Height = 3300 },
            new MultiViewPage { Id = "16x9", Title = "Screen 16:9", Width = 3840, Height = 2160 },
            new MultiViewPage { Id = "21x9", Title = "Screen 21:9", Width = 5040, Height = 2160 },
            new MultiViewPage { Id = "32x9", Title = "Screen 32:9", Width = 7680, Height = 2160 }
        };

        public string Model = string.Empty;
        public string Page = "a4-landscape";
        public List<MultiViewItem> Items = new List<MultiViewItem>();

        public MultiViewPage PageFormat
        {
            get { return Pages.FirstOrDefault(p => string.Equals(p.Id, Page, StringComparison.OrdinalIgnoreCase)) ?? Pages[0]; }
        }

        public double Width { get { return PageFormat.Width; } }

        public double Height { get { return PageFormat.Height; } }

        public MultiViewLayout Clone()
        {
            MultiViewLayout copy = (MultiViewLayout)MemberwiseClone();
            copy.Items = Items.Select(i => i.Clone()).ToList();
            return copy;
        }

        // The views side by side, each as big as fits (the order kept), centred on the page: the start of a page and
        // "Arrange" after a format change.
        public void Arrange()
        {
            List<MultiViewItem> items = Items.Where(i => i.Width > 0 && i.Height > 0).ToList();
            if (items.Count == 0)
                return;
            double margin = Math.Min(Width, Height) * 0.03;
            double gap = margin;
            // Try every number of rows; keep the one with the biggest common scale.
            double bestScale = 0;
            int bestRows = 1;
            for (int rows = 1; rows <= items.Count; rows++)
            {
                int perRow = (int)Math.Ceiling(items.Count / (double)rows);
                double scale = double.MaxValue;
                for (int r = 0; r < rows; r++)
                {
                    List<MultiViewItem> row = items.Skip(r * perRow).Take(perRow).ToList();
                    if (row.Count == 0)
                        continue;
                    double width = row.Sum(i => i.Width), height = row.Max(i => i.Height);
                    scale = Math.Min(scale, (Width - 2 * margin - gap * (row.Count - 1)) / width);
                    scale = Math.Min(scale, (Height - 2 * margin - gap * (rows - 1)) / rows / height);
                }
                if (scale > bestScale)
                {
                    bestScale = scale;
                    bestRows = rows;
                }
            }
            int count = (int)Math.Ceiling(items.Count / (double)bestRows);
            double rowHeight = 0;
            List<List<MultiViewItem>> rowsOf = new List<List<MultiViewItem>>();
            for (int r = 0; r < bestRows; r++)
                rowsOf.Add(items.Skip(r * count).Take(count).ToList());
            rowsOf = rowsOf.Where(r => r.Count > 0).ToList();
            double totalHeight = rowsOf.Sum(r => r.Max(i => i.Height) * bestScale) + gap * (rowsOf.Count - 1);
            double y = (Height - totalHeight) / 2;
            foreach (List<MultiViewItem> row in rowsOf)
            {
                rowHeight = row.Max(i => i.Height) * bestScale;
                double rowWidth = row.Sum(i => i.Width * bestScale) + gap * (row.Count - 1);
                double x = (Width - rowWidth) / 2;
                foreach (MultiViewItem item in row)
                {
                    item.Scale = Math.Round(bestScale, 4);
                    item.X = Math.Round(x, 1);
                    item.Y = Math.Round(y + (rowHeight - item.Height * bestScale) / 2, 1);
                    x += item.Width * bestScale + gap;
                }
                y += rowHeight + gap;
            }
        }

        public static bool IsMultiView(string json)
        {
            try
            {
                Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
                return root != null && string.Equals(DeviceCatalog.Text(root, "format"), Format, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static MultiViewLayout Parse(string json)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), Format, StringComparison.Ordinal))
                throw new FormatException("Not a " + Format + " file.");
            MultiViewLayout layout = new MultiViewLayout
            {
                Model = DeviceCatalog.Text(root, "model"),
                Page = DeviceCatalog.Text(root, "page")
            };
            if (layout.Page.Length == 0)
                layout.Page = "a4-landscape";
            object items;
            if (root.TryGetValue("views", out items) && items is List<object>)
            {
                foreach (Dictionary<string, object> item in ((List<object>)items).OfType<Dictionary<string, object>>())
                {
                    layout.Items.Add(new MultiViewItem
                    {
                        Sheet = DeviceCatalog.Text(item, "sheet"),
                        Image = DeviceCatalog.Text(item, "image"),
                        PictureKey = DeviceCatalog.Text(item, "pictureKey"),
                        Title = DeviceCatalog.Text(item, "title"),
                        X = Number(item, "x", 0),
                        Y = Number(item, "y", 0),
                        Scale = Math.Max(0.01, Number(item, "scale", 1)),
                        Width = Number(item, "width", 0),
                        Height = Number(item, "height", 0)
                    });
                }
            }
            return layout;
        }

        public string ToJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "format", Format },
                { "version", 1 },
                { "model", Model },
                { "page", Page },
                { "views", Items.Select(i => (object)new Dictionary<string, object>
                    {
                        { "sheet", i.Sheet },
                        { "image", i.Image },
                        { "pictureKey", i.PictureKey },
                        { "title", i.Title },
                        { "x", Math.Round(i.X, 1) },
                        { "y", Math.Round(i.Y, 1) },
                        { "scale", Math.Round(i.Scale, 4) },
                        { "width", Math.Round(i.Width, 1) },
                        { "height", Math.Round(i.Height, 1) }
                    }).ToList() }
            };
            return MiniJson.Write(root, indented: true);
        }

        private static double Number(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            if (!item.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
