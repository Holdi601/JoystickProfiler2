using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// "Reset to default" of the Layout page (docs/device-reference.md "Reset to default"): what the user changed of a
// device's picture goes, JoyPro's default comes back:
//   Layouts\<model>.*.json     marker positions on photos and drawings (moved, placed, removed)
//   Markers\<model>.*.json     marker positions on the schematic, the class outline, the old user photo
//   Callouts\<model>.*         tables, lines, row settings of the sheets (and their local picture copies)
//   Callouts\devices.json      which picture the device's cheat sheets use (the picture variant last shown)
//   Legend\<model>.json        renamed parts and own texts
//   Photos\<model>.*           the user's own photo of the device
//   Devices\<model>.json       the user's own definition of the model (marker assistant, mapped presses)
// Nothing is deleted at once: the files move to ResetBackups\<time>\ (same layout), so Undo puts them back. The
// profile's layout file of the device (engine layout image) is reset by the host. "Reset all my layout changes"
// (Settings > Drawings) does the same for every model.
public static class DeviceLayoutReset
{
    public sealed class Backup
    {
        public string Folder { get; init; } = string.Empty;
        public string Device { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        // original path -> saved copy
        public List<KeyValuePair<string, string>> Files { get; } = [];
        // The cheat sheet pointer of the device(s) before ("" = none), by device name without GUID.
        public Dictionary<string, string> CalloutPointers { get; } = new(StringComparer.OrdinalIgnoreCase);
        // The profile's layout files of the device(s) before (engine layout image), by device.
        public Dictionary<string, string> LayoutImages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Count => Files.Count + CalloutPointers.Count + LayoutImages.Count(p => p.Value.Length > 0);
    }

    public static string BackupRoot() => Path.Combine(DeviceReferenceService.DataFolder(), "ResetBackups");

    // The files of the user's changes of a model (empty: none).
    public static List<string> FilesOf(string modelId)
    {
        List<string> files = [];
        if (string.IsNullOrWhiteSpace(modelId))
            return files;
        string slug = DeviceVocabulary.Slug(modelId);
        string data = DeviceReferenceService.DataFolder();
        void Add(string folder, string pattern)
        {
            if (Directory.Exists(folder))
                files.AddRange(Directory.GetFiles(folder, pattern).Where(f => !string.Equals(Path.GetFileName(f), "devices.json", StringComparison.OrdinalIgnoreCase)));
        }
        Add(Path.Combine(data, "Layouts"), slug + ".*");
        Add(Path.Combine(data, "Markers"), slug + ".*");
        Add(Path.Combine(data, "Callouts"), slug + ".*");
        Add(Path.Combine(data, "Legend"), slug + ".json");
        Add(Path.Combine(data, "Photos"), slug + ".*");
        Add(Path.Combine(data, "MultiView"), slug + ".*");
        Add(DeviceReferenceService.UserDefinitionFolder(), slug + ".json");
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Every file of the user's layout changes (all models).
    public static List<string> AllFiles()
    {
        List<string> files = [];
        string data = DeviceReferenceService.DataFolder();
        foreach (string folder in new[] { "Layouts", "Markers", "Callouts", "Legend", "Photos", "MultiView" })
        {
            string path = Path.Combine(data, folder);
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path).Where(f => !string.Equals(Path.GetFileName(f), "devices.json", StringComparison.OrdinalIgnoreCase)));
        }
        string definitions = DeviceReferenceService.UserDefinitionFolder();
        if (Directory.Exists(definitions))
            files.AddRange(Directory.GetFiles(definitions, "*.json"));
        return files;
    }

    // Moves the files into a new backup (and forgets the cheat sheet pointers of the given devices). Returns the backup.
    public static Backup Reset(IEnumerable<string> files, IEnumerable<string> devices, string device = "", string modelId = "")
    {
        string folder = Path.Combine(BackupRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Backup backup = new() { Folder = folder, Device = device, ModelId = modelId };
        string data = Path.GetFullPath(DeviceReferenceService.DataFolder());
        string definitions = Path.GetFullPath(DeviceReferenceService.UserDefinitionFolder());
        foreach (string file in files.Where(File.Exists))
        {
            string full = Path.GetFullPath(file);
            string relative = full.StartsWith(data, StringComparison.OrdinalIgnoreCase) ? Path.Combine("data", Path.GetRelativePath(data, full))
                : full.StartsWith(definitions, StringComparison.OrdinalIgnoreCase) ? Path.Combine("definitions", Path.GetRelativePath(definitions, full))
                : Path.Combine("other", Path.GetFileName(full));
            string saved = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Move(full, saved, overwrite: true);
            backup.Files.Add(new KeyValuePair<string, string>(full, saved));
        }
        foreach (string name in devices.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? pointer = DeviceReferenceService.CalloutFileOf(name);
            if (pointer == null)
                continue;
            backup.CalloutPointers[DeviceProbe.RemoveGuid(name).Trim()] = pointer;
            DeviceReferenceService.ClearCalloutFileOf(name);
        }
        DeviceReferenceService.Reload();
        return backup;
    }

    // Puts everything of a backup back (Undo).
    public static int Restore(Backup backup)
    {
        int restored = 0;
        foreach (KeyValuePair<string, string> file in backup.Files)
        {
            if (!File.Exists(file.Value))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file.Key)!);
            File.Move(file.Value, file.Key, overwrite: true);
            restored++;
        }
        foreach (KeyValuePair<string, string> pointer in backup.CalloutPointers)
            DeviceReferenceService.SetCalloutFileOf(pointer.Key, pointer.Value);
        try
        {
            if (Directory.Exists(backup.Folder) && !Directory.EnumerateFiles(backup.Folder, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(backup.Folder, recursive: true);
        }
        catch (IOException)
        {
        }
        DeviceReferenceService.Reload();
        return restored;
    }
}
