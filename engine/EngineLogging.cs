using System;
using System.Globalization;
using System.IO;
using System.Text;
using JoyPro.Logging;

namespace JoyPro.Engine
{
    // The engine's part of the background log (docs/logging.md): what the run printed goes into the log as well
    // (stdout up to StdoutLimit characters, stderr up to StderrLimit), so a log shows warnings, results and errors of
    // every engine call, also when it was started from a console instead of the GUI.
    internal static class EngineLogging
    {
        public const int StdoutLimit = 16 * 1024;
        public const int StderrLimit = 64 * 1024;

        private static CaptureWriter stdout;
        private static CaptureWriter stderr;

        public static void CaptureConsole()
        {
            if (!JoyProLog.IsStarted || stdout != null)
                return;
            try
            {
                stdout = new CaptureWriter(Console.Out, StdoutLimit);
                stderr = new CaptureWriter(Console.Error, StderrLimit);
                Console.SetOut(stdout);
                Console.SetError(stderr);
            }
            catch (Exception)
            {
                stdout = null;
                stderr = null;
            }
        }

        // Logs the captured output (once, at the end of the run).
        public static void LogCapturedOutput()
        {
            CaptureWriter outWriter = stdout;
            CaptureWriter errWriter = stderr;
            if (outWriter == null || errWriter == null)
                return;
            try
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
            catch (Exception)
            {
            }
            string errText = errWriter.Text;
            if (errText.Trim().Length > 0)
                JoyProLog.Info("Output", "stderr (" + errWriter.Total.ToString(CultureInfo.InvariantCulture) + " chars):" + Environment.NewLine + errText);
            string outText = outWriter.Text;
            if (outText.Trim().Length > 0)
                JoyProLog.Debug("Output", "stdout (" + outWriter.Total.ToString(CultureInfo.InvariantCulture) + " chars" +
                                          (outWriter.Total > StdoutLimit ? ", first " + StdoutLimit.ToString(CultureInfo.InvariantCulture) + " logged" : string.Empty) +
                                          "):" + Environment.NewLine + outText);
            else
                JoyProLog.Debug("Output", "stdout: (empty)");
        }

        // Forwards everything to the real console writer and keeps the first `limit` characters.
        private sealed class CaptureWriter : TextWriter
        {
            private readonly TextWriter inner;
            private readonly StringBuilder captured = new StringBuilder();
            private readonly int limit;
            private readonly object gate = new object();

            public CaptureWriter(TextWriter inner, int limit)
            {
                this.inner = inner;
                this.limit = limit;
            }

            public long Total { get; private set; }

            public string Text
            {
                get
                {
                    lock (gate)
                        return captured.ToString();
                }
            }

            public override Encoding Encoding
            {
                get { return inner.Encoding; }
            }

            public override IFormatProvider FormatProvider
            {
                get { return inner.FormatProvider; }
            }

            public override string NewLine
            {
                get { return inner.NewLine; }
                set { inner.NewLine = value; }
            }

            private void Keep(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return;
                lock (gate)
                {
                    Total += value.Length;
                    int room = limit - captured.Length;
                    if (room > 0)
                        captured.Append(value.Length <= room ? value : value.Substring(0, room));
                }
            }

            public override void Write(char value)
            {
                inner.Write(value);
                Keep(value.ToString());
            }

            public override void Write(string value)
            {
                inner.Write(value);
                Keep(value);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                inner.Write(buffer, index, count);
                if (buffer != null && count > 0)
                    Keep(new string(buffer, index, count));
            }

            public override void WriteLine()
            {
                inner.WriteLine();
                Keep(inner.NewLine);
            }

            public override void WriteLine(string value)
            {
                inner.WriteLine(value);
                // Progress lines are logged one by one (EngineProgress) and would only crowd out the warnings here.
                if (value != null && (value.StartsWith(EngineProgress.ProgressPrefix, StringComparison.Ordinal) ||
                                      value.StartsWith(EngineProgress.CancelPrefix, StringComparison.Ordinal)))
                    return;
                Keep((value ?? string.Empty) + inner.NewLine);
            }

            public override void Flush()
            {
                inner.Flush();
            }
        }
    }
}
