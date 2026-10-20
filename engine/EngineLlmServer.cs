using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace JoyPro.Engine
{
    // The warm model server: started from the app (JOYPRO_LLM_OWNER_PID names the app's process), llama-server stays
    // loaded between requests and the app stops it after JOYPRO_LLM_IDLE_MINUTES without a request, with "Stop now"
    // and when the app ends (the app also puts it into its kill-on-close job). Without an owner (command line) or with
    // 0 idle minutes every request starts and stops its own server as before. <data>\Llm\server.json describes the
    // running server: Pid, Port, Key (model + context), Model, OwnerPid, StartedUtc, LastUsedUtc, BusyPid, IdleMinutes.
    internal static partial class EngineLlm
    {
        public const string OwnerPidVariable = "JOYPRO_LLM_OWNER_PID";
        public const string IdleMinutesVariable = "JOYPRO_LLM_IDLE_MINUTES";

        public static string ServerStateFile() { return Path.Combine(DataRoot(), "server.json"); }

        public sealed class ServerState
        {
            public int Pid;
            public int Port;
            public string Key = string.Empty;
            public string Model = string.Empty;
            public int OwnerPid;
            public string StartedUtc = string.Empty;
            public string LastUsedUtc = string.Empty;
            public int BusyPid;
            public double IdleMinutes;
        }

        // Owner pid and idle minutes when this engine may leave the server running, else null.
        internal static ServerState WarmSettings()
        {
            int owner;
            double idle;
            if (!int.TryParse(Environment.GetEnvironmentVariable(OwnerPidVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out owner) || owner <= 0)
                return null;
            if (!double.TryParse(Environment.GetEnvironmentVariable(IdleMinutesVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out idle))
                idle = 2;
            if (idle <= 0 || !IsAlive(owner))
                return null;
            return new ServerState { OwnerPid = owner, IdleMinutes = idle };
        }

        internal static string ServerKey(ModelInfo model, int contextSize)
        {
            string fake = Environment.GetEnvironmentVariable(FakeResponseVariable) ?? string.Empty;
            string path = model.Local ? model.ServerPath + "|" + model.LocalModelPath : (fake.Length > 0 ? "fake:" + fake : ModelPath(model));
            return model.Id + "|" + path + "|" + contextSize.ToString(CultureInfo.InvariantCulture);
        }

        public static ServerState ReadServerState()
        {
            try
            {
                string file = ServerStateFile();
                if (!File.Exists(file))
                    return null;
                Dictionary<string, object> data = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                if (data == null)
                    return null;
                return new ServerState
                {
                    Pid = (int)Long(data, "Pid", 0),
                    Port = (int)Long(data, "Port", 0),
                    Key = EngineNlGroup.Text(data, "Key"),
                    Model = EngineNlGroup.Text(data, "Model"),
                    OwnerPid = (int)Long(data, "OwnerPid", 0),
                    StartedUtc = EngineNlGroup.Text(data, "StartedUtc"),
                    LastUsedUtc = EngineNlGroup.Text(data, "LastUsedUtc"),
                    BusyPid = (int)Long(data, "BusyPid", 0),
                    IdleMinutes = Double(data, "IdleMinutes", 2)
                };
            }
            catch
            {
                return null;
            }
        }

        public static void WriteServerState(ServerState state)
        {
            Directory.CreateDirectory(DataRoot());
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                { "Pid", state.Pid },
                { "Port", state.Port },
                { "Key", state.Key },
                { "Model", state.Model },
                { "OwnerPid", state.OwnerPid },
                { "StartedUtc", state.StartedUtc },
                { "LastUsedUtc", state.LastUsedUtc },
                { "BusyPid", state.BusyPid },
                { "IdleMinutes", state.IdleMinutes }
            };
            string file = ServerStateFile();
            string temp = file + ".tmp";
            File.WriteAllText(temp, EngineSerialization.ToJson(data), new UTF8Encoding(false));
            if (File.Exists(file))
                File.Replace(temp, file, null);
            else
                File.Move(temp, file);
        }

        internal static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        internal static bool IsAlive(int pid)
        {
            if (pid <= 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        // The recorded process, if it is still the one JoyPro started (not a later process with the reused pid).
        internal static Process OpenServerProcess(ServerState state)
        {
            if (state == null || state.Pid <= 0)
                return null;
            try
            {
                Process process = Process.GetProcessById(state.Pid);
                DateTime started;
                if (!process.HasExited && DateTime.TryParse(state.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started) &&
                    Math.Abs((process.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds) < 30)
                    return process;
                process.Dispose();
            }
            catch
            {
            }
            return null;
        }

        // engine llm stop: ends JoyPro's model server now. True when one was running.
        public static bool StopServer(out int pid)
        {
            ServerState state = ReadServerState();
            pid = state == null ? 0 : state.Pid;
            bool stopped = false;
            using (Process process = OpenServerProcess(state))
            {
                if (process != null)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(10000);
                        stopped = true;
                    }
                    catch
                    {
                    }
                }
            }
            try
            {
                File.Delete(ServerStateFile());
            }
            catch
            {
            }
            return stopped;
        }

        // engine llm server: what runs now.
        public static Dictionary<string, object> ServerStatus()
        {
            ServerState state = ReadServerState();
            Dictionary<string, object> status = new Dictionary<string, object> { { "Running", false } };
            using (Process process = OpenServerProcess(state))
            {
                if (process == null)
                    return status;
                status["Running"] = true;
                status["Pid"] = state.Pid;
                status["Model"] = state.Model;
                status["LastUsedUtc"] = state.LastUsedUtc;
                status["IdleMinutes"] = state.IdleMinutes;
                status["Busy"] = IsAlive(state.BusyPid);
                status["WorkingSetBytes"] = process.WorkingSet64;
            }
            return status;
        }
    }
}
