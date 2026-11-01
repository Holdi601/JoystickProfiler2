using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Detected control spots (docs/device-reference.md, experimental, off by default): while the marker assistant is open
// on a photo, JoyPro looks for the spots of the photo that look like buttons, hats, switches or knobs
// (ControlSpotDetector, offline) and rings them on the picture. A click or a dropped chip near a ring puts the marker
// on the ring's centre. The spots carry no input (the pilot found the right input for too few of them), so nothing is
// placed by itself and no curated or saved position changes.
public sealed partial class DeviceReferenceViewModel
{
    // A click this close to a spot (share of the picture's diagonal) snaps to it.
    public const double SpotSnapShare = 0.025;

    [ObservableProperty] private bool showDetectedSpots;

    // The spots of the shown photo in picture pixels; empty while off, on a drawing or without the assistant.
    [ObservableProperty] private IReadOnlyList<Point> detectedSpots = [];

    private List<ControlSpot> spots = [];
    private int spotsVersion;
    // The last photo looked at and its spots (the photo and the picture kind change one after the other).
    private Bitmap? spotsPhoto;
    private List<ControlSpot> spotsOfPhoto = [];

    // Whether the switch is written to the settings (off in tests).
    public bool PersistDetectedSpots { get; set; }

    partial void OnShowDetectedSpotsChanged(bool value)
    {
        if (PersistDetectedSpots)
            DeviceReferenceService.DetectedSpotsEnabled = value;
        _ = RefreshDetectedSpotsAsync();
    }

    // The picture or the assistant changed: the spots of the photo now shown (or none).
    public async Task RefreshDetectedSpotsAsync()
    {
        int run = ++spotsVersion;
        Bitmap? photo = ShowDetectedSpots && Assistant != null && !ShowsSchematic ? Photo : null;
        if (photo == null)
        {
            SetSpots([], run);
            return;
        }
        if (ReferenceEquals(photo, spotsPhoto))
        {
            SetSpots(spotsOfPhoto, run);
            return;
        }
        SpotScorer? scorer = SpotScorer.Cached(DeviceReferenceService.CatalogFolder());
        (byte[] Pixels, int Width, int Height)? pixels = scorer == null ? null : PixelsOf(photo);
        if (scorer == null || pixels == null)
        {
            SetSpots([], run);
            return;
        }
        int parts = Markers.Count;
        int max = Math.Clamp(2 * parts, 10, 80);
        (byte[] data, int width, int height) = pixels.Value;
        List<ControlSpot> found;
        try
        {
            found = await Task.Run(() => ControlSpotDetector.Detect(data, width, height, width * 4, scorer, max));
        }
        catch (Exception)
        {
            found = [];
        }
        if (run == spotsVersion)
        {
            spotsPhoto = photo;
            spotsOfPhoto = found;
        }
        SetSpots(found, run);
    }

    private void SetSpots(List<ControlSpot> found, int run)
    {
        if (run != spotsVersion)
            return;
        if (found.Count == 0 && spots.Count == 0 && DetectedSpots.Count == 0)
            return;
        spots = found;
        double width = PictureWidth, height = PictureHeight;
        DetectedSpots = found.Select(s => new Point(s.X * width, s.Y * height)).ToList();
    }

    // The point a placement goes to (0..1): the nearest spot's centre when one is close, else the point itself.
    public (double X, double Y) SnapToSpot(double x, double y)
    {
        if (!ShowDetectedSpots || spots.Count == 0)
            return (x, y);
        ControlSpot? spot = ControlSpotDetector.Nearest(spots, x, y, PictureWidth, PictureHeight, SpotSnapShare);
        return spot == null ? (x, y) : (spot.X, spot.Y);
    }

    // BGRA bytes with straight alpha of a decoded photo.
    internal static (byte[] Pixels, int Width, int Height)? PixelsOf(Bitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
            return null;
        int stride = size.Width * 4;
        byte[] data = new byte[stride * size.Height];
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), data.Length, stride);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
        bool rgba = bitmap.Format is { } format && format == PixelFormat.Rgba8888;
        bool premultiplied = bitmap.AlphaFormat is null or AlphaFormat.Premul;
        for (int i = 0; i < data.Length; i += 4)
        {
            if (rgba)
                (data[i], data[i + 2]) = (data[i + 2], data[i]);
            byte a = data[i + 3];
            if (premultiplied && a > 0 && a < 255)
            {
                for (int c = 0; c < 3; c++)
                    data[i + c] = (byte)Math.Min(255, data[i + c] * 255 / a);
            }
        }
        return (data, size.Width, size.Height);
    }
}
