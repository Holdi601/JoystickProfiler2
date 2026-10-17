#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // Finds textures by name (without extension) in folders and zip archives of a game (read only), decodes them once.
    internal sealed class TextureLibrary : IDisposable
    {
        private static readonly string[] Extensions = { ".dds", ".tga", ".png" };
        private readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Tuple<string, string>> zipped = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ZipArchive> archives = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextureImage> cache = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        public int MaxSize = 2048;

        // Adds every texture file and zip under a folder; earlier additions win for a name.
        public void AddFolder(string folder, bool recursive)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            IEnumerable<string> all;
            try
            {
                all = Directory.EnumerateFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception)
            {
                return;
            }
            foreach (string f in all)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Extensions.Contains(ext))
                {
                    string key = Path.GetFileNameWithoutExtension(f);
                    if (!files.ContainsKey(key) || ext == ".dds")
                        files[key] = f;
                }
                else if (ext == ".zip")
                    AddZip(f);
            }
        }

        public void AddZip(string zip)
        {
            try
            {
                ZipArchive a = ZipFile.OpenRead(zip);
                archives[zip] = a;
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    string ext = Path.GetExtension(e.FullName).ToLowerInvariant();
                    if (!Extensions.Contains(ext))
                        continue;
                    string key = Path.GetFileNameWithoutExtension(e.FullName);
                    if (!zipped.ContainsKey(key) || ext == ".dds")
                        zipped[key] = Tuple.Create(zip, e.FullName);
                }
            }
            catch (Exception)
            {
                // An unreadable archive is skipped.
            }
        }

        public bool Has(string name)
        {
            string key = Key(name);
            return files.ContainsKey(key) || zipped.ContainsKey(key);
        }

        private static string Key(string name)
        {
            string n = (name ?? string.Empty).Replace('/', '\\');
            int slash = n.LastIndexOf('\\');
            if (slash >= 0)
                n = n.Substring(slash + 1);
            string ext = Path.GetExtension(n).ToLowerInvariant();
            if (Extensions.Contains(ext) || ext == ".bmp")
                n = Path.GetFileNameWithoutExtension(n);
            return n;
        }

        public TextureImage Get(string name)
        {
            string key = Key(name);
            lock (gate)
            {
                TextureImage img;
                if (cache.TryGetValue(key, out img))
                    return img;
                byte[] data = null;
                string hint = key;
                string file;
                Tuple<string, string> z;
                try
                {
                    if (files.TryGetValue(key, out file))
                    {
                        data = File.ReadAllBytes(file);
                        hint = file;
                    }
                    else if (zipped.TryGetValue(key, out z))
                    {
                        ZipArchiveEntry e = archives[z.Item1].GetEntry(z.Item2);
                        if (e != null)
                        {
                            using (Stream s = e.Open())
                            using (MemoryStream m = new MemoryStream())
                            {
                                s.CopyTo(m);
                                data = m.ToArray();
                            }
                            hint = z.Item2;
                        }
                    }
                }
                catch (Exception)
                {
                    data = null;
                }
                img = data == null ? null : TextureImage.Decode(data, hint, MaxSize);
                cache[key] = img;
                return img;
            }
        }

        public void Dispose()
        {
            foreach (ZipArchive a in archives.Values)
                a.Dispose();
            archives.Clear();
            cache.Clear();
        }
    }
}
