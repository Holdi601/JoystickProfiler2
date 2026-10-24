using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JoyPro
{
    public static class MainStructure
    {
        private const string EngineDataFolderName = "JoyPro";
        private const string EngineMetaFileName = "engine.meta.json";
        private const string EngineLastProfileFileName = "last.pr0file";

        public static bool EngineMode = false;
        public static Action<string> EngineNotification = null;
        public static bool loadKeyboard = false;
        public static string PROGPATH = Environment.CurrentDirectory;
        public static MetaSave msave = new MetaSave();

        public static void NoteError(Exception exception)
        {
            if (exception == null)
                return;

            Write(exception.ToString());
        }

        public static void Write(string message)
        {
            string line = "[" + DateTime.UtcNow.ToString("O") + "]\t" + (message ?? string.Empty);
            if (EngineNotification != null)
            {
                EngineNotification(line);
            }
            else
            {
                Console.Error.WriteLine(line);
            }
        }

        public static void LoadMetaLast()
        {
            string metaPath = GetMetaPath();
            if (!File.Exists(metaPath))
            {
                if (msave == null)
                    msave = new MetaSave();
                return;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                MetaSave loaded = serializer.Deserialize<MetaSave>(File.ReadAllText(metaPath));
                msave = loaded ?? new MetaSave();
            }
            catch
            {
                msave = new MetaSave();
            }
        }

        public static void SaveMetaOnly()
        {
            if (msave == null)
                msave = new MetaSave();

            string directory = GetEngineDataPath();
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string payload = serializer.Serialize(msave);
            try
            {
                WriteTextWithRetry(GetMetaPath(), payload);
            }
            catch (IOException)
            {
                // Metadata persistence is best-effort and must not break engine commands.
            }
            catch (UnauthorizedAccessException)
            {
                // Metadata persistence is best-effort and must not break engine commands.
            }
        }

        public static void SaveMetaLast()
        {
            SaveMetaOnly();
            InternalDataManagement.SaveProfileTo(GetLastProfilePath(), false);
        }

        public static void AfterLoad()
        {
            string preferred = msave == null ? string.Empty : (msave.lastOpenedLocation ?? string.Empty);
            if (TryLoadProfileIfExists(preferred))
                return;

            TryLoadProfileIfExists(GetLastProfilePath());
        }

        private static bool TryLoadProfileIfExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (!File.Exists(path))
                return false;

            try
            {
                InternalDataManagement.LoadProfile(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetEngineDataPath()
        {
            // JOYPRO_DATA_DIR (tests, portable setups) keeps the last-profile files with the rest of JoyPro's data;
            // without it parallel test runs shared Documents\JoyPro and wrote into the user's real one.
            string root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(root))
                return root;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), EngineDataFolderName);
        }

        internal static string GetMetaPath()
        {
            return Path.Combine(GetEngineDataPath(), EngineMetaFileName);
        }

        internal static string GetLastProfilePath()
        {
            return Path.Combine(GetEngineDataPath(), EngineLastProfileFileName);
        }

        private static void WriteTextWithRetry(string path, string text)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.Write(text ?? string.Empty);
                    }
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(25 * attempt);
                }
            }

            throw new IOException("Could not write metadata file after retries: " + path);
        }
    }
}
