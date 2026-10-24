using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// 2D layouts of a model (docs/device-reference.md, "2D layouts"): the shipped ones (DB\DeviceLayouts2D) and the ones
// whose picture is on this PC in a JoyPro 1 folder. Picking one for a device takes it over: a copy of the variant with
// the picture's absolute path (the picture copied next to it) in <data>\Layouts\Variants, which the engine reads like a
// layout file (cheat sheets, the Layout page's layout file view). The pick is kept per device (choices.json) and
// survives a restart; saving edits writes the user's own .layout there.
public static class LayoutVariantStore
{
    public static string Folder() => Path.Combine(DeviceCatalog.DataRoot(), "Layouts", "Variants");

    private static string ChoicesFile() => Path.Combine(Folder(), "choices.json");

    // The shipped variants (replaceable for tests).
    public static Func<List<LayoutVariant>> Shipped { get; set; } =
        () => LayoutVariantDb.Cached(LayoutVariantDb.FolderOf(DeviceReferenceService.CatalogFolder()));

    // Where pictures that JoyPro does not ship may be: the JoyPro 1 layouts taken over and the folders they came from,
    // the community sheets folder (<data>\Layouts\Community),
    // plus folders found on this PC (set by the Layout page).
    public static List<string> ExtraPictureFolders { get; } = [];

    public static IEnumerable<string> LocalPictureFolders()
    {
        yield return OldJoyProLayouts.TargetFolder();
        yield return DeviceCatalog.CommunityLayoutFolder();
        foreach (string folder in OldJoyProLayouts.ImportedSources())
            yield return folder;
        foreach (string folder in ExtraPictureFolders.ToList())
            yield return folder;
    }

    // The variants of a model whose picture is available here.
    public static List<LayoutVariant> For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return [];
        List<string> folders = LocalPictureFolders().Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string where = string.Join("|", folders);
        return LayoutVariantDb.For(Shipped(), modelId).Where(v => Resolved(v, folders, where) != null).ToList();
    }

    // A local picture is found by its content hash: looked up once per variant and set of folders.
    private static readonly Dictionary<string, string?> resolvedPictures = new(StringComparer.OrdinalIgnoreCase);

    private static string? Resolved(LayoutVariant variant, List<string> folders, string where)
    {
        if (variant.IsShipped)
            return variant.ResolvePicture(folders);
        string key = variant.File + "|" + where;
        lock (resolvedPictures)
        {
            if (resolvedPictures.TryGetValue(key, out string? known) && known != null && File.Exists(known))
                return known;
        }
        // Not found is looked up again (cheap without the file): a sheet put into the Community folder shows at once.
        string? found = variant.ResolvePicture(folders);
        if (found != null)
        {
            lock (resolvedPictures)
                resolvedPictures[key] = found;
        }
        return found;
    }

    public static LayoutVariant? Find(string id) => Shipped().FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    // The shown name: who made it and where it is from.
    public static string TitleOf(LayoutVariant variant)
    {
        string title = variant.Credit.Length > 0 ? T("2D layout by {0}", variant.Credit)
            : variant.IsShipped ? T("2D layout from JoyPro 1")
            : T("2D layout from your JoyPro 1 folder");
        return variant.HasModifierColumn ? T("{0} (modifier column)", title) : title;
    }

    // The user's copy of a variant (absolute picture, the modifier column for his modifier). Returns its file.
    public static string TakeOver(LayoutVariant variant, string modifier)
    {
        string? picture = variant.ResolvePicture(LocalPictureFolders());
        if (picture == null)
            throw new FileNotFoundException(T("The picture of this 2D layout is not on this PC."));
        Directory.CreateDirectory(Folder());
        string slug = DeviceVocabulary.Slug(variant.Id);
        string keptPicture = Path.Combine(Folder(), slug + Path.GetExtension(picture).ToLowerInvariant());
        if (!string.Equals(Path.GetFullPath(picture), Path.GetFullPath(keptPicture), StringComparison.OrdinalIgnoreCase) &&
            (!File.Exists(keptPicture) || new FileInfo(keptPicture).Length != new FileInfo(picture).Length))
            File.Copy(picture, keptPicture, overwrite: true);
        LayoutVariant copy = variant.TakenOver(keptPicture, modifier);
        string file = Path.Combine(Folder(), slug + ".json");
        File.WriteAllText(file, copy.ToJson(), new UTF8Encoding(false));
        return file;
    }

    public static bool IsVariantCopy(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\'), Path.GetFullPath(Folder()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- the pick per device (device name without its GUID) ----

    public sealed class Choice
    {
        public string Variant { get; set; } = string.Empty;
        // The layout file in use: the variant's copy, or the user's saved .layout made from it.
        public string Layout { get; set; } = string.Empty;
        // The device's layout file before the pick (put back when the pick is undone).
        public string Previous { get; set; } = string.Empty;
    }

    private static string Key(string device) => DeviceProbe.RemoveGuid(device ?? string.Empty).Trim();

    private static Dictionary<string, Choice> ReadChoices()
    {
        try
        {
            string file = ChoicesFile();
            if (!File.Exists(file))
                return new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Choice>? map = JsonSerializer.Deserialize<Dictionary<string, Choice>>(File.ReadAllText(file, Encoding.UTF8));
            return map == null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, Choice>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteChoices(Dictionary<string, Choice> map)
    {
        Directory.CreateDirectory(Folder());
        File.WriteAllText(ChoicesFile(), JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static Choice? ChoiceOf(string device)
    {
        string key = Key(device);
        return key.Length > 0 && ReadChoices().TryGetValue(key, out Choice? choice) && File.Exists(choice.Layout) ? choice : null;
    }

    public static void SetChoice(string device, Choice choice)
    {
        string key = Key(device);
        if (key.Length == 0)
            return;
        Dictionary<string, Choice> map = ReadChoices();
        map[key] = choice;
        WriteChoices(map);
    }

    // The user saved his edits of the variant: his .layout is the device's layout from now on.
    public static void UpdateLayout(string device, string layout)
    {
        string key = Key(device);
        Dictionary<string, Choice> map = ReadChoices();
        if (key.Length == 0 || !map.TryGetValue(key, out Choice? choice))
            return;
        choice.Layout = layout;
        WriteChoices(map);
    }

    public static Choice? ClearChoice(string device)
    {
        string key = Key(device);
        Dictionary<string, Choice> map = ReadChoices();
        if (key.Length == 0 || !map.Remove(key, out Choice? old))
            return null;
        WriteChoices(map);
        return old;
    }
}
