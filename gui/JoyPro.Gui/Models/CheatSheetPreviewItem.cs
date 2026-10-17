using Avalonia.Media.Imaging;

namespace JoyPro.Gui.Models;

// One cheat sheet picture (engine layout render) in the Layout page's preview gallery.
public sealed class CheatSheetPreviewItem
{
    public CheatSheetPreviewItem(string filePath, string title, string subtitle, Bitmap? thumbnail)
    {
        FilePath = filePath;
        Title = title;
        Subtitle = subtitle;
        Thumbnail = thumbnail;
    }

    public string FilePath { get; }

    // The device (the file name).
    public string Title { get; }

    // "Overview" or the game and aircraft ("DCS · F-16C_50").
    public string Subtitle { get; }

    public Bitmap? Thumbnail { get; }

    public bool HasThumbnail => Thumbnail != null;

    public bool HasNoThumbnail => Thumbnail == null;
}
