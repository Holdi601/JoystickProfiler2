using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace JoyPro.Engine
{
    // Structured progress of long commands (DB refreshes, command list builds, imports, exports) for frontends and
    // scripts, next to the human [engine-status] lines (engine/docs/ENGINE_CLI_WIKI.md, "--progress"):
    //   [engine-progress] <current>/<total> <phase>: <item>
    //     current = the 1-based number of the item being worked on (current == total on the last one), total = the
    //     number of items of this phase ("?" when unknown); phase = "<game>, <what is done>" and never contains ':';
    //     item = the aircraft, module, file or game (optional, the rest of the line).
    //   [engine-cancel] available | unavailable | cancelled
    //     whether the command may be cancelled now. A frontend asks for it by creating the file named in
    //     JOYPRO_CANCEL_FILE; the engine stops only at points where nothing is half written (before it writes, or
    //     between whole files), cleans up and exits with code 3. Once it writes, it finishes and ignores the request.
    // Written to stderr only with --status true or --progress true; always logged (Info, category "Progress").
    internal static class EngineProgress
    {
        public const string ProgressPrefix = "[engine-progress] ";
        public const string CancelPrefix = "[engine-cancel] ";
        public const string CancelFileVariable = "JOYPRO_CANCEL_FILE";
        public const int CancelledExitCode = 3;

        private static readonly object Gate = new object();
        private static bool cancelSeen;
        private static bool cancelAllowed;

        // Set from --status / --progress: progress lines go to stderr.
        public static bool Enabled { get; set; }

        // Reports the item being worked on. Safe to call from any thread (e.g. a child process's output handler).
        public static void Report(int current, int total, string phase, string item = null)
        {
            string cleanPhase = Clean(phase).Replace(':', ' ').Trim();
            string cleanItem = Clean(item);
            if (cleanPhase.Length == 0)
                cleanPhase = "Working";
            if (total > 0)
                current = Math.Max(0, Math.Min(current, total));
            else
                current = Math.Max(0, current);
            string line = current.ToString(CultureInfo.InvariantCulture) + "/" +
                          (total > 0 ? total.ToString(CultureInfo.InvariantCulture) : "?") + " " + cleanPhase +
                          (cleanItem.Length > 0 ? ": " + cleanItem : string.Empty);
            lock (Gate)
            {
                // The background log gets the progress also without --status (docs/logging.md): the first and last item
                // of every phase and the items in between at most every half second (an import reads thousands of files).
                int now = Environment.TickCount;
                if (!string.Equals(cleanPhase, loggedPhase, StringComparison.Ordinal) || current <= 1 || (total > 0 && current >= total) ||
                    unchecked(now - loggedAt) >= 500)
                {
                    JoyPro.Logging.JoyProLog.Info("Progress", line);
                    loggedPhase = cleanPhase;
                    loggedAt = now;
                }
                if (Enabled)
                    WriteLine(ProgressPrefix + line);
            }
        }

        private static string loggedPhase = string.Empty;
        private static int loggedAt;

        // A single step of a command without countable items ("1/3 War Thunder, reading the game's files").
        public static void Step(int step, int steps, string phase, string item = null)
        {
            Report(step, steps, phase, item);
        }

        // The command may be stopped from now on (true) or no longer (false: it writes files now).
        public static void SetCancelable(bool allowed)
        {
            lock (Gate)
            {
                if (cancelAllowed == allowed)
                    return;
                cancelAllowed = allowed;
                if (Enabled && !string.IsNullOrWhiteSpace(CancelFile))
                    WriteLine(CancelPrefix + (allowed ? "available" : "unavailable"));
            }
        }

        public static bool IsCancelable
        {
            get
            {
                lock (Gate)
                    return cancelAllowed;
            }
        }

        // The cancel file the frontend creates to ask the command to stop (JOYPRO_CANCEL_FILE).
        public static string CancelFile
        {
            get { return Environment.GetEnvironmentVariable(CancelFileVariable) ?? string.Empty; }
        }

        // True when the frontend asked to stop and the command is at a point where it may stop.
        public static bool CancelRequested
        {
            get
            {
                lock (Gate)
                {
                    if (!cancelAllowed)
                        return false;
                    if (cancelSeen)
                        return true;
                }
                string file = CancelFile;
                bool requested;
                try
                {
                    requested = file.Length > 0 && File.Exists(file);
                }
                catch (Exception)
                {
                    requested = false;
                }
                if (requested)
                {
                    lock (Gate)
                        cancelSeen = true;
                }
                return requested;
            }
        }

        // Stops the command (EngineCancelledException, exit code 3) when the frontend asked for it and stopping is safe.
        public static void ThrowIfCancelled(string where = null)
        {
            if (!CancelRequested)
                return;
            throw new EngineCancelledException("Cancelled by the user" + (string.IsNullOrWhiteSpace(where) ? "." : " while " + where.Trim() + ".") + " Nothing was written.");
        }

        // Called once when the run ends with a cancel.
        public static void WriteCancelled()
        {
            lock (Gate)
            {
                if (Enabled)
                    WriteLine(CancelPrefix + "cancelled");
            }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                cancelSeen = false;
                cancelAllowed = false;
            }
        }

        private static void WriteLine(string line)
        {
            try
            {
                Console.Error.WriteLine(line);
                Console.Error.Flush();
            }
            catch (Exception)
            {
                // A closed stderr never stops the command.
            }
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
            return sb.ToString().Trim();
        }
    }

    // The user cancelled a command at a safe point (EngineProgress.ThrowIfCancelled); exit code 3.
    internal sealed class EngineCancelledException : OperationCanceledException
    {
        public EngineCancelledException(string message)
            : base(message)
        {
        }
    }
}
