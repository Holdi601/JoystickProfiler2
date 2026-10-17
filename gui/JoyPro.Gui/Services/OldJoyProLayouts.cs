using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// The user's JoyPro 1 layouts (docs/device-reference.md, "Your old JoyPro layouts"): .layout files with his picture and
// the text positions of every input. JoyPro finds JoyPro 1 folders next to its own install and in the usual places,
// and takes the layouts over by copying them into <data>\Layouts\JoyPro1, where the engine's layout matching finds
// them (by the device name stored in each layout). The user's old folder is only read, never written. These files are
// the user's own and are never shipped.
public static class OldJoyProLayouts
{
    private const string SourcesFile = "imported-from.txt";

    // Where JoyPro took layouts over to.
    public static string TargetFolder() => DeviceCatalog.ImportedLayoutFolder();

    // Folders with JoyPro 1 layouts: "LayoutImages" folders of JoyPro 1 installs next to JoyPro (walking up from its
    // folder) and in Documents, Desktop, Downloads, the program folders and the drive roots. Fast: only a few levels.
    public static List<string> Detect(string? startFolder = null)
    {
        List<string> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string own = Normalize(TargetFolder());

        void Consider(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;
            string path = Normalize(dir);
            if (path.Length == 0 || !seen.Add(path) || string.Equals(path, own, StringComparison.OrdinalIgnoreCase))
                return;
            if (HasLayouts(path))
                found.Add(path);
        }

        void LookInto(string? root, int depth)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;
            Consider(Path.Combine(root, "LayoutImages"));
            if (depth <= 0)
                return;
            foreach (string child in SafeDirectories(root).Where(IsJoyProName))
            {
                Consider(child);
                Consider(Path.Combine(child, "LayoutImages"));
                foreach (string grandChild in SafeDirectories(child).Where(d => IsJoyProName(d) || Path.GetFileName(d).Equals("LayoutImages", StringComparison.OrdinalIgnoreCase)))
                {
                    Consider(grandChild);
                    Consider(Path.Combine(grandChild, "LayoutImages"));
                }
            }
        }

        DirectoryInfo? walk = new(startFolder ?? AppContext.BaseDirectory);
        for (int i = 0; i < 8 && walk != null; i++, walk = walk.Parent)
            LookInto(walk.FullName, depth: 1);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     string.IsNullOrWhiteSpace(profile) ? string.Empty : Path.Combine(profile, "Downloads"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                 })
            LookInto(root, depth: 1);
        foreach (DriveInfo drive in SafeDrives())
            LookInto(drive.RootDirectory.FullName, depth: 1);
        return found;
    }

    // JoyPro 1 names: "JoyPro", "JoyPro1", "JoystickProfiler", "Joy Pro" (not JoyPro 2's own folders).
    private static bool IsJoyProName(string dir)
    {
        string name = Path.GetFileName(dir.TrimEnd('\\', '/')).ToLowerInvariant().Replace(" ", string.Empty);
        return (name.StartsWith("joypro", StringComparison.Ordinal) || name.StartsWith("joystickprofiler", StringComparison.Ordinal)) &&
               !name.Contains("joypro2", StringComparison.Ordinal) && name != "joystickprofiler2";
    }

    public static bool HasLayouts(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.layout", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static int CountLayouts(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.layout", SearchOption.TopDirectoryOnly).Count() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // The folders the layouts were taken over from before.
    public static List<string> ImportedSources()
    {
        string file = Path.Combine(TargetFolder(), SourcesFile);
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Copies the .layout files of a folder (only these; the user's pictures are inside them) into JoyPro's folder,
    // keeping their dates (the newest layout of a device wins). A file with the same name and content is skipped; one
    // with the same name and other content is kept under a numbered name. Returns how many were copied.
    public static int Import(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return 0;
        string target = TargetFolder();
        Directory.CreateDirectory(target);
        int copied = 0;
        foreach (string source in Directory.GetFiles(folder, "*.layout", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string name = Path.GetFileName(source);
                string destination = Path.Combine(target, name);
                if (File.Exists(destination))
                {
                    if (SameContent(source, destination))
                        continue;
                    int n = 2;
                    do
                        destination = Path.Combine(target, Path.GetFileNameWithoutExtension(name) + " (" + n++ + ").layout");
                    while (File.Exists(destination) && !SameContent(source, destination));
                    if (File.Exists(destination))
                        continue;
                }
                File.Copy(source, destination, overwrite: false);
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
                copied++;
            }
            catch (Exception)
            {
                // A file that cannot be read is left out.
            }
        }
        List<string> sources = ImportedSources();
        if (!sources.Contains(Normalize(folder), StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(Normalize(folder));
            File.WriteAllLines(Path.Combine(target, SourcesFile), sources, new UTF8Encoding(false));
        }
        return copied;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            FileInfo fa = new(a), fb = new(b);
            return fa.Length == fb.Length && DeviceLayoutSet.HashFile(a) == DeviceLayoutSet.HashFile(b);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
