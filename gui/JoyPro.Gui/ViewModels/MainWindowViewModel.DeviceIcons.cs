using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;

namespace JoyPro.Gui.ViewModels;

// The device icon (docs/device-reference.md, "Device icon"): on the Layout page the user picks which of the shown
// device's pictures is its icon (photo, maker's photo, JoyPro's line drawing as lines, cel-shaded or two-colour,
// JoyPro's drawing, the layout's picture, or a picture file of his own), cut out and cropped square. Every place that
// shows a device's icon takes it: the device cards (Start page, Settings > Devices, the Binds page's detail), the bind
// list's device chips and the HUD designer's list. Without a choice they show what they showed before.
public sealed partial class MainWindowViewModel
{
    private const int IconRenderSize = 640;

    public ObservableCollection<DeviceIconOption> DeviceIconOptions { get; } = [];

    // The device cards that show a chosen icon (taken back: their automatic picture again).
    private readonly HashSet<DeviceCardItem> cardsWithChosenIcon = [];

    [ObservableProperty]
    private bool isDeviceIconPickerOpen;

    [ObservableProperty]
    private string deviceIconStatus = string.Empty;

    // The shown device's icon on the Layout page (next to "Icon").
    [ObservableProperty]
    private IImage? workspaceDeviceIcon;

    [ObservableProperty]
    private string workspaceDeviceIconTitle = T("Automatic");

    // The device the Layout page shows (whose icon the picker sets).
    private string IconDevice()
    {
        string reference = deviceReference?.DeviceName ?? string.Empty;
        return LayoutShowsLayoutFile || reference.Length == 0 ? ResolveLayoutEditorContextDevice() : reference;
    }

    // The icon of a device for the bind list and the HUD: its card's picture (which is the chosen icon when there is
    // one), else the chosen icon itself.
    internal IImage? DeviceIconFor(string? device)
    {
        string name = (device ?? string.Empty).Trim();
        if (name.Length == 0 || name.StartsWith('<'))
            return null;
        DeviceCardItem? card = DeviceCards.FirstOrDefault(c => OverlayResolver.SameDevice(c.Device, name)) ??
                               DeviceCards.FirstOrDefault(c => SameDeviceName(c.Device, name));
        if (card?.Picture != null)
            return card.Picture;
        return ChosenIconBitmap(name);
    }

    private readonly Dictionary<string, (string File, Bitmap? Bitmap)> chosenIconBitmaps = new(StringComparer.OrdinalIgnoreCase);

    private Bitmap? ChosenIconBitmap(string device)
    {
        DeviceIconService.IconChoice? choice = DeviceIconService.ChoiceOf(device);
        if (choice == null)
            return null;
        string key = DeviceIconService.KeyOf(device);
        if (chosenIconBitmaps.TryGetValue(key, out (string File, Bitmap? Bitmap) cached) && cached.File == choice.File + "|" + File.GetLastWriteTimeUtc(choice.File).Ticks)
            return cached.Bitmap;
        Bitmap? bitmap;
        try
        {
            bitmap = new Bitmap(choice.File);
        }
        catch (Exception)
        {
            bitmap = null;
        }
        chosenIconBitmaps[key] = (choice.File + "|" + File.GetLastWriteTimeUtc(choice.File).Ticks, bitmap);
        return bitmap;
    }

    // The cards' pictures changed (or an icon was chosen): the bind list, the HUD designer and the Layout page follow.
    private void OnDeviceIconsChanged()
    {
        foreach (HudWidget widget in HudWidgets.Where(w => w.Device != null))
            widget.Device!.Icon = DeviceIconFor(widget.Device.Device);
        if (Binds.Count > 0)
            RebuildVisibleBinds();
        OnPropertyChanged(nameof(SelectedBindDevicePicture));
        RefreshWorkspaceDeviceIcon();
    }

    private void RefreshWorkspaceDeviceIcon()
    {
        string device = IconDevice();
        DeviceIconService.IconChoice? choice = device.Length > 0 ? DeviceIconService.ChoiceOf(device) : null;
        WorkspaceDeviceIcon = device.Length > 0 ? DeviceIconFor(device) : null;
        WorkspaceDeviceIconTitle = choice?.Title ?? T("Automatic");
    }

    [RelayCommand]
    private async Task OpenDeviceIconPickerAsync()
    {
        string device = IconDevice();
        if (device.Length == 0)
        {
            DeviceIconStatus = T("Pick a device first.");
            return;
        }
        IsDeviceIconPickerOpen = true;
        DeviceIconStatus = T("Making the icons...");
        await BuildDeviceIconOptionsAsync(device);
        DeviceIconStatus = DeviceIconOptions.Count > 1
            ? T("Pick the icon of {0}, or use a picture file of your own.", DisplayText.ShortDevice(device))
            : T("No picture of this device yet: use a picture file of your own.");
    }

    [RelayCommand]
    private void CloseDeviceIconPicker() => IsDeviceIconPickerOpen = false;

    // One option per picture of the shown device (each line drawing three times: lines, cel-shaded, two-colour).
    internal async Task BuildDeviceIconOptionsAsync(string device)
    {
        DeviceIconOptions.Clear();
        DeviceIconService.IconChoice? current = DeviceIconService.ChoiceOf(device);
        DeviceIconOptions.Add(new DeviceIconOption { Kind = "auto", Title = T("Automatic"), IsCurrent = current == null });
        DeviceReferenceViewModel? reference = deviceReference;
        if (reference != null && reference.Model != null)
        {
            foreach (ReferencePictureOption picture in reference.Pictures.ToList())
            {
                foreach (DeviceIconOption option in await IconOptionsOfAsync(reference, picture))
                {
                    option.IsCurrent = current != null && current.Title == option.Title;
                    DeviceIconOptions.Add(option);
                }
            }
        }
        if (LayoutShowsLayoutFile && LayoutEditorPreviewImage is { } layoutPicture && RenderImage(layoutPicture) is { } rendered)
        {
            using (rendered)
                AddIconOption("layout", T("Your layout's picture"), DeviceIconService.IconFromBitmap(rendered, cutBackground: true), current);
        }
    }

    private void AddIconOption(string kind, string title, byte[] png, DeviceIconService.IconChoice? current)
    {
        DeviceIconOptions.Add(new DeviceIconOption
        {
            Kind = kind,
            Title = title,
            Png = png,
            Preview = DecodePng(png),
            IsCurrent = current != null && current.Title == title
        });
    }

    private async Task<List<DeviceIconOption>> IconOptionsOfAsync(DeviceReferenceViewModel reference, ReferencePictureOption picture)
    {
        List<DeviceIconOption> options = [];
        void Add(string kind, string title, byte[]? png)
        {
            if (png != null)
                options.Add(new DeviceIconOption { Kind = kind, Title = title, Png = png, Preview = DecodePng(png) });
        }
        // Several photos or drawings of a kind: the view ("front") tells them apart.
        string view = reference.Pictures.Count(p => p.Kind == picture.Kind) > 1 && picture.View.Length > 0 ? " · " + picture.View : string.Empty;
        try
        {
            switch (picture.Kind)
            {
                case "user":
                    Add("user", T("Your photo"), await Task.Run(() => DeviceIconService.IconFromFile(picture.Source)));
                    break;
                case "vendor":
                    string? file = await reference.FetchVendorPhoto(picture.Source);
                    if (file != null)
                        Add("vendor", T("Maker's photo") + view, await Task.Run(() => DeviceIconService.IconFromFile(file)));
                    break;
                case "lineart" when picture.LineArt != null:
                    DrawingStyle screen = DeviceReferenceService.ScreenDrawingStyle;
                    Add("lineart", T("Line drawing") + view, LineArtIcon(picture.LineArt, new DrawingStyle { Mode = DrawingStyle.Lines, Preset = screen.Preset, Ink = screen.Ink, Paper = screen.Paper, Accent = screen.Accent }, page: true));
                    Add("celart", T("Cel-shaded") + view, LineArtIcon(picture.LineArt, new DrawingStyle { Mode = DrawingStyle.Cel, Preset = screen.Preset, Ink = screen.Ink, Paper = screen.Paper, Accent = screen.Accent }, page: false));
                    Add("twocolour", T("Two-colour") + view, LineArtIcon(picture.LineArt, new DrawingStyle { Mode = DrawingStyle.Lines, Preset = "print" }, page: true));
                    break;
                case "schematic" when reference.Schematic != null:
                    Add("schematic", T("Drawing"), SchematicIcon(reference.Schematic));
                    break;
                case "class" when reference.ClassSchematic != null:
                    Add("schematic", T("Outline"), SchematicIcon(reference.ClassSchematic));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            JoyPro.Logging.JoyProLog.Warn("DeviceIcon", "No icon from '" + picture.Title + "': " + ex.Message);
        }
        return options;
    }

    [RelayCommand]
    private void ChooseDeviceIcon(DeviceIconOption? option)
    {
        string device = IconDevice();
        if (option == null || device.Length == 0)
            return;
        if (option.IsAutomatic)
        {
            DeviceIconService.ClearIcon(device);
            DeviceIconStatus = T("The icon of {0} is chosen automatically again.", DisplayText.ShortDevice(device));
        }
        else if (option.Png != null)
        {
            DeviceIconService.SetIcon(device, option.Kind, option.Title, option.Png);
            DeviceIconStatus = T("Icon of {0}: {1}.", DisplayText.ShortDevice(device), option.Title);
        }
        foreach (DeviceIconOption other in DeviceIconOptions)
            other.IsCurrent = ReferenceEquals(other, option);
        _ = LoadDeviceCardPicturesAsync(lastCardPictures);
        OnDeviceIconsChanged();
    }

    // A picture file of the user's own as the icon (from the picker's "Your own picture..." button).
    public async Task<bool> ChooseDeviceIconFileAsync(string file)
    {
        string device = IconDevice();
        if (device.Length == 0 || !File.Exists(file))
            return false;
        byte[]? png = await Task.Run(() => DeviceIconService.IconFromFile(file));
        if (png == null)
        {
            DeviceIconStatus = T("That picture could not be read.");
            return false;
        }
        DeviceIconOption option = new() { Kind = "file", Title = T("Your picture {0}", Path.GetFileName(file)), Png = png, Preview = DecodePng(png) };
        DeviceIconOptions.Add(option);
        ChooseDeviceIcon(option);
        return true;
    }

    // JoyPro's line drawing as an icon: square around the drawn device; with its paper (lines, two-colour) or on
    // nothing (cel-shaded).
    private static byte[]? LineArtIcon(DeviceLineArt art, DrawingStyle style, bool page)
    {
        using SKBitmap? bare = RenderImage(new LineArtImage(art, style) { Page = false, Dark = false });
        if (bare == null)
            return null;
        if (!page)
            return DeviceIconService.IconFromBitmap(bare, cutBackground: false);
        using SKBitmap? paper = RenderImage(new LineArtImage(art, style) { Page = true, Dark = false });
        return paper == null ? null : DeviceIconService.IconFromBitmap(paper, cutBackground: false, boundsFrom: bare);
    }

    private static byte[]? SchematicIcon(DeviceSchematic schematic)
    {
        SchematicPainter painter = new();
        using SKBitmap? bitmap = Render(new Size(IconRenderSize, IconRenderSize), (context, bounds) =>
        {
            Rect target = painter.FitContent(schematic, bounds);
            if (target.Width > 0)
                painter.Draw(context, schematic, target, dark: false, thumbnail: true);
        });
        return bitmap == null ? null : DeviceIconService.IconFromBitmap(bitmap, cutBackground: false);
    }

    // A picture (photo, drawing) drawn at IconRenderSize on nothing.
    private static SKBitmap? RenderImage(IImage image)
    {
        if (image.Size.Width <= 0 || image.Size.Height <= 0)
            return null;
        double scale = IconRenderSize / Math.Max(image.Size.Width, image.Size.Height);
        Size size = new(Math.Max(1, Math.Round(image.Size.Width * scale)), Math.Max(1, Math.Round(image.Size.Height * scale)));
        return Render(size, (context, bounds) => context.DrawImage(image, new Rect(image.Size), bounds));
    }

    private static SKBitmap? Render(Size size, Action<DrawingContext, Rect> draw)
    {
        PixelSize pixels = new((int)size.Width, (int)size.Height);
        using RenderTargetBitmap target = new(pixels, new Vector(96, 96));
        using (DrawingContext context = target.CreateDrawingContext())
            draw(context, new Rect(0, 0, pixels.Width, pixels.Height));
        using MemoryStream stream = new();
        target.Save(stream);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }

    private static Bitmap? DecodePng(byte[] png)
    {
        try
        {
            using MemoryStream stream = new(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
