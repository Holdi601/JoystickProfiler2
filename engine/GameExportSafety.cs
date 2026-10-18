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
                SafeFileIo.CopyFile(fullPath, fullPath + BackupExtension, true, ownTarget: true);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not back up '" + fullPath + "' before export: " + ex.Message);
            }
        }
    }
}
