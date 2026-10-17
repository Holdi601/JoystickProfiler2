using System;
using System.Collections.Generic;
using System.IO;

namespace JoyPro.Engine
{
    internal sealed class EngineState
    {
        public bool Initialized { get; private set; }
        public string ProgramPath { get; private set; }

        public void Initialize(string profilePath)
        {
            if (Initialized)
            {
                if (!string.IsNullOrWhiteSpace(profilePath))
                {
                    if (File.Exists(profilePath))
                    {
                        InternalDataManagement.LoadProfile(profilePath);
                    }
                    else
                    {
                        InternalDataManagement.NewFile(true);
                        // Explicit profile path was requested but does not exist yet.
                        // Keep a fresh in-memory profile instead of loading JoyPro's last.pr0file.
                    }
                }
                return;
            }

            MainStructure.EngineMode = true;
            MainStructure.EngineNotification = msg => Console.Error.WriteLine(msg);
            MainStructure.loadKeyboard = false;

            ProgramPath = ResolveProgramPath();
            MainStructure.PROGPATH = ProgramPath;
            Environment.CurrentDirectory = ProgramPath;
            // Game data built on this PC lives in <data>\GameData; lists an older JoyPro left in the program folder
            // are copied there before an update or rebuild replaces the program folder (GameDataPaths).
            GameDataPaths.EnsureMigrated();

            MainStructure.LoadMetaLast();
            if (MainStructure.msave == null)
                MainStructure.msave = new MetaSave();

            InternalDataManagement.NewFile(true);

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                if (File.Exists(profilePath))
                {
                    InternalDataManagement.LoadProfile(profilePath);
                }
                else
                {
                    // Explicit profile path was requested but does not exist yet.
                    // Keep a fresh in-memory profile instead of loading JoyPro's last.pr0file.
                }
            }
            else
            {
                MainStructure.AfterLoad();
            }

            Initialized = true;
        }

        private static string ResolveProgramPath()
        {
            List<string> candidates = new List<string>();
            string cwd = Environment.CurrentDirectory;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            candidates.Add(cwd);
            candidates.Add(Path.Combine(cwd, "JoyPro"));
            candidates.Add(baseDir);
            candidates.Add(Path.Combine(baseDir, "JoyPro"));
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..\\..\\..\\..\\JoyPro")));
            candidates.Add(Path.GetFullPath(Path.Combine(cwd, "..\\JoyPro")));
            candidates.Add(Path.GetFullPath(Path.Combine(cwd, "..\\..\\JoyPro")));

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (LooksLikeProgramDataRoot(candidate))
                    return candidate;
            }

            return cwd;
        }

        private static bool LooksLikeProgramDataRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            // DB holds JoyPro's own data (Functions, Devices, Llm). A release package ships no game data, so CleanProfile
            // (built by "dcs refresh clean") may not exist yet; DB\Functions marks the root as well.
            return Directory.Exists(path) &&
                   Directory.Exists(Path.Combine(path, "DB")) &&
                   (Directory.Exists(Path.Combine(path, "CleanProfile")) || Directory.Exists(Path.Combine(path, "DB", "Functions")));
        }
    }
}
