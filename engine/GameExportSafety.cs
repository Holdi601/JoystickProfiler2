using System;
using System.Collections.Generic;
using System.IO;

namespace JoyPro.Engine
{
    // Shared safety net for exporters that overwrite files owned by a game.
    internal static class GameExportSafety
    {
        public const string BackupExtension = ".jpbak";

        private static readonly HashSet<string> BackedUpThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Copies the current file to "<file>.jpbak" before the first overwrite in this engine run, so a bad
        // export can always be undone by hand, and into the versioned backup store (engine backups restore).
        // Later writes in the same run keep the original backup.
        // A new run in the same process (the engine server).
        public static void ResetRun()
        {
            BackedUpThisRun.Clear();
        }

        public static void BackupBeforeOverwrite(string filePath, List<string> warnings)
        {
            GameBackups.Protect(filePath, warnings);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            string fullPath = Path.GetFullPath(filePath);
            if (!BackedUpThisRun.Add(fullPath))
                return;

            try
            {
                // Beside the game's file, in the game's folder: the copy keeps the encryption the folder has (an
                // encrypted folder of a Store app must stay so), the game's files and folder are never changed.
                SafeFileIo.CopyBesideGameFile(fullPath, fullPath + BackupExtension);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not back up '" + fullPath + "' before export: " + ex.Message);
            }
        }
    }
}
