using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // engine devices reference detect <model> [--photo <file | view | sha prefix>] [--photos <private archive>]
    //     [--max <n>] [--eval] [--text]
    // The detected control spots of a device photo (docs/device-reference.md, "Detected control spots", experimental):
    // where buttons, hats, switches and knobs probably are, each with a score. No input is assigned to a spot. --eval
    // compares the spots with JoyPro's curated positions of the same photo (a spot within 2 % of the diagonal finds a
    // marker) and then takes twice the curated markers as the number of spots, the pilot's measure.
    internal static partial class Program
    {
        private static int RunDeviceDetect(DeviceCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            string photoArg = PopOptionValue(rest, "--photo");
            string archive = PopOptionValue(rest, "--photos") ?? Environment.GetEnvironmentVariable("JOYPRO_PHOTO_BACKUP");
            string maxText = PopOptionValue(rest, "--max");
            bool eval = PopFlag(rest, "--eval");
            PopFlag(rest, "--json");
            if (rest.Count != 1)
                return Fail("Usage: devices reference detect <model> [--photo <file|view|sha>] [--photos <folder>] [--max <n>] [--eval] [--text]");
            RefDevice model = catalog.Find(rest[0]);
            if (model == null)
                return Fail("The model '" + rest[0] + "' is not in the device catalog.");
            SpotScorer scorer = SpotScorer.Cached(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH));
            if (scorer == null)
                return Fail("The spot scorer (DB\\" + SpotScorer.FolderName + "\\" + SpotScorer.FileName + ") is missing or broken.");
            List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH)), null)
                .Where(s => string.Equals(s.Model, model.Id, StringComparison.OrdinalIgnoreCase) && s.PhotoKind == "vendor").ToList();

            string file;
            DevicePhoto photo;
            string problem = ResolveDetectPhoto(model, sets, photoArg, archive, out file, out photo);
            if (problem != null)
                return Fail(problem);
            string sha = DeviceLayoutSet.HashFile(file);
            DeviceLayoutSet curated = sets.FirstOrDefault(s => string.Equals(s.PhotoSha256, sha, StringComparison.OrdinalIgnoreCase)) ??
                                      (photo != null && photo.Sha256.Length == 64 && photo.HasContent(sha) ? sets.FirstOrDefault(s => photo.HasContent(s.PhotoSha256)) : null);
            int parts = SchematicBuilder.Build(model, catalog.BaseOf(model)).Markers.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int max = Math.Max(10, Math.Min(80, 2 * parts));
            if (eval && curated != null)
                max = Math.Max(10, 2 * curated.Markers.Count);
            int n;
            if (maxText != null)
            {
                if (!int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n <= 0)
                    return Fail("Invalid --max value: " + maxText);
                max = n;
            }

            byte[] pixels;
            int width, height, stride;
            string decodeProblem = DecodeDetectPhoto(file, out pixels, out width, out height, out stride);
            if (decodeProblem != null)
                return Fail("The photo could not be read (" + decodeProblem + "): " + file);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            List<ControlSpot> spots = ControlSpotDetector.Detect(pixels, width, height, stride, scorer, max);
            watch.Stop();

            int expected = 0, found = 0, onMarkers = 0;
            List<string> missed = new List<string>();
            if (eval)
            {
                if (curated == null)
                    return Fail("--eval needs a photo with JoyPro's curated positions; " + model.Id + " has none on this photo.");
                expected = curated.Markers.Count;
                found = ControlSpotDetector.Found(curated.Markers.Values, spots, width, height);
                onMarkers = ControlSpotDetector.OnMarkers(curated.Markers.Values, spots, width, height);
                missed = curated.Markers.Where(m => ControlSpotDetector.Found(new[] { m.Value }, spots, width, height) == 0).Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Detected control spots: " + model.Id + " | " + Path.GetFileName(file) + " | " + width + "x" + height + " | " + spots.Count + " spots | " +
                              watch.ElapsedMilliseconds + " ms");
                sb.AppendLine("No input is assigned to a spot (experimental: snap targets for placing markers).");
                foreach (ControlSpot s in spots)
                    sb.AppendLine("  " + F3(s.X) + " " + F3(s.Y) + "  score " + F3(s.Score));
                if (eval)
                {
                    sb.AppendLine("Curated positions: " + Path.GetFileName(curated.File) + " | expected " + expected + " | found " + found + " (" + Percent(found, expected) +
                                  ") | spots on a marker " + onMarkers + "/" + spots.Count + " (" + Percent(onMarkers, spots.Count) + ")");
                    if (missed.Count > 0)
                        sb.AppendLine("Missed: " + string.Join(", ", missed));
                }
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Model = model.Id,
                    Photo = file,
                    Sha256 = sha,
                    View = photo != null ? photo.View : string.Empty,
                    Width = width,
                    Height = height,
                    Milliseconds = watch.ElapsedMilliseconds,
                    Assigned = false,
                    Spots = spots.Select(s => new { X = Math.Round(s.X, 4), Y = Math.Round(s.Y, 4), Radius = Math.Round(s.Radius, 4), Score = Math.Round(s.Score, 3) }).ToList(),
                    Eval = !eval ? null : new
                    {
                        Set = Path.GetFileName(curated.File),
                        Expected = expected,
                        Found = found,
                        OnMarkers = onMarkers,
                        Missed = missed
                    }
                }), outPath);
            }
            return 0;
        }

        private static string F3(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        private static string Percent(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("0", CultureInfo.InvariantCulture) + "%";

        // The photo: a file, else one of the model's photos (by view or the start of its hash; without --photo the photo of
        // its curated positions, else its first photo) from the reference photos on this PC or the private archive.
        private static string ResolveDetectPhoto(RefDevice model, List<DeviceLayoutSet> sets, string arg, string archive, out string file, out DevicePhoto photo)
        {
            file = null;
            photo = null;
            if (!string.IsNullOrWhiteSpace(arg) && File.Exists(arg))
            {
                file = Path.GetFullPath(arg);
                string sha = DeviceLayoutSet.HashFile(file);
                photo = model.Photos.FirstOrDefault(p => p.Sha256.Length == 64 && p.HasContent(sha));
                return null;
            }
            List<DevicePhoto> photos = model.Photos.Where(p => p.Sha256.Length == 64).ToList();
            if (string.IsNullOrWhiteSpace(arg))
                photo = photos.FirstOrDefault(p => sets.Any(s => p.HasContent(s.PhotoSha256))) ?? photos.FirstOrDefault();
            else
                photo = photos.FirstOrDefault(p => string.Equals(p.View, arg, StringComparison.OrdinalIgnoreCase)) ??
                        (arg.Length >= 6 ? photos.FirstOrDefault(p => p.Sha256.StartsWith(arg, StringComparison.OrdinalIgnoreCase)) : null);
            if (photo == null)
                return string.IsNullOrWhiteSpace(arg) ? model.Id + " has no reference photo with a recorded hash." : "No photo '" + arg + "' of " + model.Id + " (a file, a view or the start of a hash).";
            List<string> hashes = new List<string> { photo.Sha256 };
            hashes.AddRange(photo.Sha256Alt);
            List<string> folders = new List<string>();
            string store = Path.Combine(DeviceCatalog.DataRoot(), "DeviceReference", "ReferencePhotos", model.Id);
            if (Directory.Exists(store))
                folders.Add(store);
            if (!string.IsNullOrWhiteSpace(archive) && Directory.Exists(Path.Combine(archive, "sha256")))
                folders.Add(Path.Combine(archive, "sha256"));
            foreach (string folder in folders)
            {
                foreach (string candidate in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileNameWithoutExtension(candidate);
                    bool byName = hashes.Any(h => h.Length == 64 && (name.Equals(h, StringComparison.OrdinalIgnoreCase) || name.EndsWith("-" + h.Substring(0, 12), StringComparison.OrdinalIgnoreCase)));
                    if (byName && photo.HasContent(DeviceLayoutSet.HashFile(candidate)))
                    {
                        file = candidate;
                        return null;
                    }
                }
            }
            return "The photo " + photo.View + " (" + photo.Sha256.Substring(0, 12) + ") of " + model.Id + " is not on this PC; download it on the Layout page or pass --photo <file>.";
        }

        // BGRA pixels of a photo: Windows' imaging codecs (also WebP where Windows has the codec), else GDI+.
        private static string DecodeDetectPhoto(string file, out byte[] pixels, out int width, out int height, out int stride)
        {
            pixels = null;
            width = height = stride = 0;
            string wicProblem;
            try
            {
                using (FileStream stream = File.OpenRead(file))
                {
                    System.Windows.Media.Imaging.BitmapDecoder decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    System.Windows.Media.Imaging.FormatConvertedBitmap bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    width = bgra.PixelWidth;
                    height = bgra.PixelHeight;
                    stride = width * 4;
                    pixels = new byte[stride * height];
                    bgra.CopyPixels(pixels, stride, 0);
                    return null;
                }
            }
            catch (Exception ex)
            {
                wicProblem = ex.Message;
            }
            try
            {
                using (FileStream stream = File.OpenRead(file))
                using (Image image = Image.FromStream(stream))
                using (Bitmap bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        width = bitmap.Width;
                        height = bitmap.Height;
                        stride = Math.Abs(data.Stride);
                        pixels = new byte[stride * height];
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                return wicProblem + "; " + ex.Message;
            }
        }
    }
}
