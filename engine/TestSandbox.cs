using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JoyPro.Engine
{
    // The write guard of tests, the quality gate and the smoke test (docs/testing.md "Test sandbox").
    // JOYPRO_TEST_SANDBOX=<root>: every write of this engine (file, folder, game registry key) outside the root, the temp
    // folder, JOYPRO_DATA_DIR and the folders of JOYPRO_TEST_SANDBOX_ALLOW (';'-separated) is refused with a
    // TestSandboxViolationException before anything is touched. A refusal is loud even where a caller swallows the
    // exception: it is printed on stderr, appended to <root>\sandbox-violations.log, and the command ends with exit code
    // ViolationExitCode. Game registry keys (HKCU) are never written under the sandbox; JOYPRO_TEST_REGISTRY_ROOT
    // (a key under HKCU, e.g. Software\JoyProTest) redirects them instead.
    //
    // The chokepoint: SandboxedIo.cs declares File, Directory and FileStream in the root namespace JoyPro, so every
    // "File.WriteAllText", "Directory.Delete", "new FileStream(...)" of the engine's code (JoyPro and all its sub
    // namespaces: Engine, Games, CockpitPictures, Logging, Usage, ...) goes through the guard; System.IO's own types are only reached when a file spells them out
    // ("System.IO.File..."), which only reads may do.
    internal static class TestSandbox
    {
        public const string Variable = "JOYPRO_TEST_SANDBOX";
        public const string AllowVariable = "JOYPRO_TEST_SANDBOX_ALLOW";
        public const string RegistryRootVariable = "JOYPRO_TEST_REGISTRY_ROOT";
        public const string ViolationLogName = "sandbox-violations.log";
        public const int ViolationExitCode = 77;

        private static int violations;
        private static string cachedKey;
        private static List<string> cachedRoots = new List<string>();
        private static readonly object Gate = new object();

        public static bool IsActive
        {
            get { return Root.Length > 0; }
        }

        public static string Root
        {
            get
            {
                string root = Environment.GetEnvironmentVariable(Variable);
                return string.IsNullOrWhiteSpace(root) ? string.Empty : root.Trim().Trim('"');
            }
        }

        // At the start of the process: with the sandbox and no data folder of its own, the data folder (logs, backups,
        // sync records, game data) goes into the sandbox, never %LOCALAPPDATA%\JoyPro2.
        public static void Initialize()
        {
            string root = Root;
            if (root.Length == 0)
                return;
            // Neither a data folder nor a stand-in %LOCALAPPDATA%: the stand-in goes into the sandbox, so the data folder
            // is <root>\LocalAppData\JoyPro2 (and the move of an old JoyPro folder only ever sees the sandbox).
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(JoyPro.Logging.JoyProDataFolder.LocalAppDataVariable)))
                Environment.SetEnvironmentVariable(JoyPro.Logging.JoyProDataFolder.LocalAppDataVariable, Path.Combine(root, "LocalAppData"));
        }

        // Each command (also each command of the engine server) counts its own refusals.
        public static void ResetRun()
        {
            Interlocked.Exchange(ref violations, 0);
        }

        public static int Violations
        {
            get { return Volatile.Read(ref violations); }
        }

        // A command that wrote nothing outside but had a refusal (maybe swallowed by a catch) still fails.
        public static int AdjustExitCode(int exitCode)
        {
            if (Violations == 0 || exitCode == ViolationExitCode)
                return exitCode;
            Console.Error.WriteLine("engine error: the test sandbox refused " + Violations.ToString(CultureInfo.InvariantCulture) +
                                    " write(s) outside " + Root + " (see " + Path.Combine(Root, ViolationLogName) + ").");
            return ViolationExitCode;
        }

        // The roots a write may go to (sandbox, temp, data folder, extra allowed folders).
        public static List<string> AllowedRoots()
        {
            string root = Root;
            if (root.Length == 0)
                return new List<string>();
            string key = root + "|" + Path.GetTempPath() + "|" + Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") + "|" +
                         Environment.GetEnvironmentVariable(AllowVariable);
            lock (Gate)
            {
                if (string.Equals(key, cachedKey, StringComparison.Ordinal))
                    return cachedRoots;
                List<string> roots = new List<string>();
                Action<string> add = folder =>
                {
                    string normalized = Normalize(folder);
                    if (normalized.Length > 0 && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        roots.Add(normalized);
                };
                add(root);
                add(Path.GetTempPath());
                add(Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR"));
                foreach (string extra in (Environment.GetEnvironmentVariable(AllowVariable) ?? string.Empty).Split(';'))
                    add(extra);
                cachedKey = key;
                cachedRoots = roots;
                return roots;
            }
        }

        public static bool IsAllowed(string path)
        {
            if (!IsActive)
                return true;
            string full = Normalize(path);
            if (full.Length == 0)
                return true;
            foreach (string root in AllowedRoots())
            {
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Before every write: refuses a target outside the sandbox (nothing is touched then).
        public static string EnsureWritable(string path, string operation)
        {
            if (!IsActive || string.IsNullOrWhiteSpace(path) || IsAllowed(path))
                return path;
            Refuse(operation + " " + Normalize(path), "a write to " + Normalize(path));
            return path;
        }

        // Game settings in HKCU: never written under the sandbox, unless JOYPRO_TEST_REGISTRY_ROOT redirects them. Returns
        // the sub key to open (redirected or the game's own when there is no sandbox).
        public static string RegistrySubKeyForWrite(string subKey, string operation)
        {
            if (!IsActive)
                return subKey;
            string redirect = RegistryRoot();
            if (redirect.Length > 0)
                return redirect + "\\" + (subKey ?? string.Empty).TrimStart('\\');
            Refuse(operation + " HKCU\\" + subKey, "a write to the registry key HKCU\\" + subKey);
            return subKey;
        }

        // The sub key a read uses: the redirected one while the sandbox redirects the registry.
        public static string RegistrySubKeyForRead(string subKey)
        {
            if (!IsActive)
                return subKey;
            string redirect = RegistryRoot();
            return redirect.Length > 0 ? redirect + "\\" + (subKey ?? string.Empty).TrimStart('\\') : subKey;
        }

        private static string RegistryRoot()
        {
            string value = Environment.GetEnvironmentVariable(RegistryRootVariable);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('\\');
        }

        private static void Refuse(string what, string description)
        {
            Interlocked.Increment(ref violations);
            string root = Root;
            string message = "JOYPRO_TEST_SANDBOX refused " + description + ": only " + string.Join(", ", AllowedRoots()) +
                             " may be written while the test sandbox is on.";
            try
            {
                Console.Error.WriteLine("[test-sandbox] " + message);
            }
            catch (Exception)
            {
            }
            try
            {
                System.IO.Directory.CreateDirectory(root);
                System.IO.File.AppendAllText(Path.Combine(root, ViolationLogName),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "\tengine pid " +
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "\t" + what + Environment.NewLine +
                    Environment.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "\t") + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
            // Not into JoyProLog: its own file writes go through the guard too.
            throw new TestSandboxViolationException(message);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return path.Trim();
            }
        }
    }

    // Not an IOException or UnauthorizedAccessException: the usual "could not write" catches do not take it for a
    // locked file and go on.
    internal sealed class TestSandboxViolationException : Exception
    {
        public TestSandboxViolationException(string message) : base(message)
        {
        }
    }
}
