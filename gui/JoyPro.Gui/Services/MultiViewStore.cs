using System;
using System.IO;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// Where a model's page with several views lives (MultiViewLayout): <data>\DeviceReference\MultiView\<model>.json, its
// own images next to it (<model>.image-<n>.<ext>). Reset to default moves them with the model's other files.
public static class MultiViewStore
{
    public static string Folder() => Path.Combine(DeviceReferenceService.DataFolder(), "MultiView");

    public static string FileOf(string modelId) => Path.Combine(Folder(), DeviceVocabulary.Slug(modelId) + ".json");

    public static MultiViewLayout? Load(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;
        string file = FileOf(modelId);
        if (!File.Exists(file))
            return null;
        try
        {
            return MultiViewLayout.Parse(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Save(MultiViewLayout layout)
    {
        string file = FileOf(layout.Model);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, layout.ToJson(), new UTF8Encoding(false));
        return file;
    }

    public static void Delete(string modelId)
    {
        string file = FileOf(modelId);
        if (File.Exists(file))
            File.Delete(file);
    }

    // A copy of the user's own image for a view (the original may move).
    public static string KeepImage(string modelId, string source)
    {
        Directory.CreateDirectory(Folder());
        string slug = DeviceVocabulary.Slug(modelId);
        string extension = Path.GetExtension(source);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";
        int n = 1;
        string target;
        do
        {
            target = Path.Combine(Folder(), slug + ".image-" + n + extension.ToLowerInvariant());
            n++;
        }
        while (File.Exists(target));
        File.Copy(source, target);
        return target;
    }

    // The page a device's cheat sheets use: the page of the model of the device's sheet, when it has several views.
    public static string? PageForSheet(string? sheetFile)
    {
        if (string.IsNullOrWhiteSpace(sheetFile) || !File.Exists(sheetFile))
            return null;
        try
        {
            DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(sheetFile, Encoding.UTF8), sheetFile);
            MultiViewLayout? page = Load(set.Model);
            return page != null && page.Items.Count >= 2 ? FileOf(set.Model) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
