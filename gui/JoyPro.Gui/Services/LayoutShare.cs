using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// "Share layout..." with the page of several views (docs/device-reference.md, "Storage and sharing"): the shared
// joypro-device-layout gets a "multiview" object with the page format and every view: its place and scale, and the
// view's own layout (positions, tables and lines; the user's photo inside, never a maker's photo or photo.local) or the
// user's plain image (base64). "Import layout..." puts the views' sheets and images back and saves the page.
//
//   "multiview": { "page": "a4-landscape", "views": [ { "title": "...", "pictureKey": "...", "x": 10, "y": 10,
//                  "scale": 0.8, "width": 2056, "height": 1656, "layout": { joypro-device-layout } | "image": "<base64>",
//                  "imageName": "front.png" } ] }
public static class LayoutShare
{
    // The shared file with the model's page (when it has two or more views).
    public static string WithMultiView(string sharedJson, string modelId)
    {
        MultiViewLayout? page = MultiViewStore.Load(modelId);
        if (page == null || page.Items.Count < 2)
            return sharedJson;
        if (MiniJson.Parse(sharedJson) is not Dictionary<string, object> root)
            return sharedJson;
        List<object> views = [];
        foreach (MultiViewItem item in page.Items)
        {
            Dictionary<string, object> view = new(StringComparer.Ordinal)
            {
                ["title"] = item.Title,
                ["pictureKey"] = item.PictureKey,
                ["x"] = Math.Round(item.X, 1),
                ["y"] = Math.Round(item.Y, 1),
                ["scale"] = Math.Round(item.Scale, 4),
                ["width"] = Math.Round(item.Width, 1),
                ["height"] = Math.Round(item.Height, 1)
            };
            if (item.Image.Length > 0)
            {
                if (!File.Exists(item.Image))
                    continue;
                view["image"] = Convert.ToBase64String(File.ReadAllBytes(item.Image));
                view["imageName"] = Path.GetFileName(item.Image);
            }
            else
            {
                DeviceLayoutSet? sheet = ReadSheet(item.Sheet);
                if (sheet == null)
                    continue;
                // The user's own photo goes along (from the sheet's local copy); a maker's photo only by its address.
                if (sheet.PhotoKind == "user" && sheet.PhotoFileBase64.Length == 0)
                {
                    string? photo = UserPhotoOf(sheet);
                    if (photo != null)
                    {
                        sheet.PhotoFileBase64 = Convert.ToBase64String(File.ReadAllBytes(photo));
                        sheet.PhotoFileName = Path.GetFileName(photo);
                    }
                }
                view["layout"] = MiniJson.Parse(sheet.ToJson());
            }
            views.Add(view);
        }
        if (views.Count < 2)
            return sharedJson;
        root["multiview"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["page"] = page.Page, ["views"] = views };
        return MiniJson.Write(root, true);
    }

    private static DeviceLayoutSet? ReadSheet(string file)
    {
        try
        {
            return file.Length > 0 && File.Exists(file) ? DeviceLayoutSet.Parse(File.ReadAllText(file, Encoding.UTF8), file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The user's photo a sheet was made on: his current photo of the model when its content matches, else the sheet's
    // local copy of it.
    private static string? UserPhotoOf(DeviceLayoutSet sheet)
    {
        string? photo = DeviceReferenceService.UserPhoto(sheet.Model);
        if (photo != null && string.Equals(DeviceLayoutSet.HashFile(photo), sheet.PhotoSha256, StringComparison.OrdinalIgnoreCase))
            return photo;
        return sheet.LocalPhotoFile.Length > 0 && File.Exists(sheet.LocalPhotoFile) ? sheet.LocalPhotoFile : null;
    }

    // Takes the page of a shared file over for its model: every view's sheet and image, then the page. Returns the
    // number of views (0 = the file has no page).
    public static int ImportMultiView(string sharedJson, string modelId)
    {
        if (MiniJson.Parse(sharedJson) is not Dictionary<string, object> root ||
            !root.TryGetValue("multiview", out object? value) || value is not Dictionary<string, object> shared)
            return 0;
        MultiViewLayout page = new() { Model = modelId, Page = DeviceCatalog.Text(shared, "page") is { Length: > 0 } p ? p : "a4-landscape" };
        foreach (Dictionary<string, object> view in DeviceCatalog.Objects(shared, "views"))
        {
            MultiViewItem item = new()
            {
                Title = DeviceCatalog.Text(view, "title"),
                PictureKey = DeviceCatalog.Text(view, "pictureKey"),
                X = Number(view, "x"),
                Y = Number(view, "y"),
                Scale = Math.Max(0.01, Number(view, "scale", 1)),
                Width = Number(view, "width"),
                Height = Number(view, "height")
            };
            string image = DeviceCatalog.Text(view, "image");
            if (image.Length > 0)
            {
                string name = DeviceCatalog.Text(view, "imageName");
                string temp = Path.Combine(Path.GetTempPath(), "JoyPro2-view-" + Guid.NewGuid().ToString("N") + (Path.GetExtension(name) is { Length: > 0 } ext ? ext : ".png"));
                File.WriteAllBytes(temp, Convert.FromBase64String(image));
                try
                {
                    item.Image = MultiViewStore.KeepImage(modelId, temp);
                }
                finally
                {
                    File.Delete(temp);
                }
            }
            else if (DeviceCatalog.Object(view, "layout") is { } layout)
            {
                DeviceLayoutSet set = DeviceLayoutSet.Parse(MiniJson.Write(layout, false), string.Empty);
                set.Model = modelId;
                item.Sheet = ImportSheet(set, item.PictureKey);
                if (item.Sheet.Length == 0)
                    continue;
            }
            else
            {
                continue;
            }
            page.Items.Add(item);
        }
        if (page.Items.Count < 2)
            return 0;
        MultiViewStore.Save(page);
        return page.Items.Count;
    }

    // One view's layout: the user's photo inside becomes his photo, the positions and the tables are kept for that
    // picture (as Import layout... does for one picture). Returns the sheet file.
    private static string ImportSheet(DeviceLayoutSet set, string pictureKey)
    {
        if (set.PhotoKind == "user" && set.PhotoFileBase64.Length > 0)
        {
            string temp = Path.Combine(Path.GetTempPath(), "JoyPro2-import-" + Guid.NewGuid().ToString("N") + Path.GetExtension(set.PhotoFileName is { Length: > 0 } n ? n : ".png"));
            File.WriteAllBytes(temp, Convert.FromBase64String(set.PhotoFileBase64));
            try
            {
                string stored = DeviceReferenceService.SetUserPhoto(set.Model, temp);
                set.PhotoSha256 = DeviceLayoutSet.HashFile(stored);
                set.LocalPhotoFile = stored;
            }
            finally
            {
                File.Delete(temp);
            }
            set.PhotoFileBase64 = string.Empty;
        }
        if (set.PhotoKind == "drawing")
            DeviceReferenceService.SavePictureMarkers(set.Model, "schematic", set.Markers);
        else if (set.PhotoSha256.Length > 0)
            DeviceReferenceService.SaveUserLayout(set);
        string key = pictureKey.Length > 0 ? pictureKey : set.PhotoKind == "drawing" ? "schematic" : set.PhotoSha256;
        if (key.Length == 0 || set.Callouts == null)
            return string.Empty;
        return DeviceReferenceService.SaveCallouts(set, key);
    }

    private static double Number(Dictionary<string, object> item, string key, double fallback = 0)
    {
        if (!item.TryGetValue(key, out object? value) || value == null)
            return fallback;
        try
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
