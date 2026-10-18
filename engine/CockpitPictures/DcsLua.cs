#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // Reads what a DCS module's own Lua files say, with the game's Lua interpreter (bin\luae.exe) and small harness
    // scripts that only print: the flyable aircraft and their cockpit folders (entry.lua), the clickable cockpit
    // elements (clickabledata.lua), the animation arguments of the grips (mainpanel_init.lua) and the pilot's eye
    // points (Views.lua). Nothing in the game folder is written; the harness lives in a temporary folder.
    internal static class DcsLua
    {
        public sealed class Flyable
        {
            public string Module = string.Empty;      // folder name under Mods\aircraft
            public string Plane = string.Empty;       // the game's aircraft name (F-16C_50)
            public string CockpitPath = string.Empty; // the cockpit scripts folder
        }

        public sealed class Clickable
        {
            public string Name = string.Empty;
            public string Hint = string.Empty;
            public int Device = -1;
            public string DeviceName = string.Empty;
            public List<string> Actions = new List<string>();
            public List<string> Args = new List<string>();
            public List<string> Classes = new List<string>();
            // Per action: the argument's step (arg_value) and its limits ("min:max", arg_lim); empty where not given.
            public List<string> Values = new List<string>();
            public List<string> Limits = new List<string>();
        }

        public sealed class Gauge
        {
            public string Name = string.Empty;
            public string Controller = string.Empty;
            public List<int> Args = new List<int>();
        }

        public sealed class Seat
        {
            // Where the pilot's eye is in the cockpit model: the seat's cockpit point less the model's anchor (the first
            // seat's point when Views.lua names none), plus the eye point.
            public double[] EyePoint;
        }

        private const string Common = @"
local function out(...)
  local t = {}
  for i = 1, select('#', ...) do t[#t + 1] = tostring((select(i, ...))) end
  io.write(table.concat(t, '|'), '\n')
end
local function clean(s) if type(s) ~= 'string' then return '' end return (s:gsub('[|\r\n]', ' ')) end
local dummy
local function zero() return 0 end
dummy = setmetatable({}, { __call = function() return dummy end, __index = function() return dummy end, __concat = function(a, b) return '' end,
  __add = zero, __sub = zero, __mul = zero, __div = zero, __mod = zero, __pow = zero, __unm = zero, __lt = function() return false end, __le = function() return false end })
local function sandbox(base)
  local env = {}
  for k, v in pairs(base or {}) do env[k] = v end
  setmetatable(env, { __index = function(t, k) local g = rawget(_G, k); if g ~= nil then return g end return dummy end })
  return env
end
local function run(path, env)
  local f, err = loadfile(path)
  if not f then return false, err end
  setfenv(f, env)
  return pcall(f)
end
package.preload['i_18n'] = function() return { translate = function(s) return s end, dtranslate = function(d, s) return s end } end
package.preload['log'] = function() return { error = function() end, warning = function() end, info = function() end, debug = function() end } end
";

        private const string EntryScript = Common + @"
local modsRoot = arg[1]
for line in io.lines(arg[2]) do
  local folder = line
  local env
  local function dofile_(p) local ok, err = run(p, env) end
  env = sandbox({
    current_mod_path = modsRoot .. folder,
    __DCS_VERSION__ = '2.9.0',
    _ = function(s) return s end,
  LOCALIZE = function(s) return s end,
    make_flyable = function(name, cockpit) out('FLY', folder, clean(tostring(name)), clean(tostring(cockpit))) end,
    declare_plugin = function(id, t)
      if type(t) == 'table' and type(t.InputProfiles) == 'table' then
        for k, v in pairs(t.InputProfiles) do out('INP', folder, clean(tostring(k)), clean(tostring(v))) end
      end
    end,
    dofile = dofile_,
    require = require,
  })
  local ok, err = run(modsRoot .. folder .. '/entry.lua', env)
  if not ok then out('ERR', folder, clean(tostring(err))) end
end
out('END')
";

        private const string ClickScript = Common + @"
local scriptPath, dcsPath = arg[1], arg[2]
local env
env = sandbox({
  class_type = setmetatable({}, { __index = function(t, k) return k end }),
  _ = function(s) return s end,
  LOCALIZE = function(s) return s end,
  LockOn_Options = {
    script_path = scriptPath,
    common_script_path = dcsPath .. 'Scripts/Aircrafts/_Common/Cockpit/',
    cockpit_language = 'english',
    screen = { width = 1920, height = 1080, aspect = 16 / 9 },
    init_conditions = {}, flight = {}, measurement_system = 'imperial', date = { year = 2024, month = 6, day = 1 },
  },
  require = require,
})
env.dofile = function(p) local f = loadfile(p); if f then setfenv(f, env); local ok, e = pcall(f) end end
env.loadfile = function(p) local f, e = loadfile(p); if f then setfenv(f, env) end; return f, e end
package.path = scriptPath .. '?.lua;' .. dcsPath .. 'Scripts/?.lua;' .. package.path
local ok, err = run(scriptPath .. 'clickabledata.lua', env)
if not ok then out('ERR', clean(tostring(err))) end
local devnames = {}
if type(rawget(env, 'devices')) == 'table' then for k, v in pairs(env.devices) do devnames[v] = k end end
local function list(v)
  if type(v) ~= 'table' then if v == nil or v == dummy then return '' end return clean(tostring(v)) end
  local t = {}
  for i = 1, #v do if v[i] ~= dummy then t[#t + 1] = clean(tostring(v[i])) end end
  return table.concat(t, ',')
end
local els = rawget(env, 'elements')
if type(els) == 'table' then
  for name, e in pairs(els) do
    if type(e) == 'table' then
      local dev = e.device
      if dev == dummy then dev = nil end
      local lims = {}
      if type(e.arg_lim) == 'table' then
        for i = 1, #e.arg_lim do
          local l = e.arg_lim[i]
          if type(l) == 'table' and type(l[1]) == 'number' and type(l[2]) == 'number' then lims[#lims + 1] = tostring(l[1]) .. ':' .. tostring(l[2]) else lims[#lims + 1] = '' end
        end
      end
      out('EL', clean(tostring(name)), clean(e.hint), tostring(dev), clean(devnames[dev] or ''), list(e.action), list(e.arg), list(e.class), list(e.arg_value), table.concat(lims, ';'))
    end
  end
end
out('END')
";

        private const string GaugeScript = Common + @"
local scriptPath, dcsPath = arg[1], arg[2]
local gauges = {}
local function newgauge() local g = { args = {} }; gauges[g] = true; return g end
local env
env = sandbox({
  _ = function(s) return s end,
  LOCALIZE = function(s) return s end,
  controllers = setmetatable({}, { __index = function(t, k) return k end }),
  CreateGauge = function() return newgauge() end,
  CreateGaugeLocal = function(a, i, o, c) local g = newgauge(); g.arg_number = a; g.controller = c; return g end,
  CreateSimpleConnectedGauge = function(a, b) local g = newgauge(); g.args = { a, b }; return g end,
  LockOn_Options = { script_path = scriptPath, common_script_path = dcsPath .. 'Scripts/Aircrafts/_Common/Cockpit/', cockpit_language = 'english',
                     screen = { width = 1920, height = 1080, aspect = 16 / 9 }, init_conditions = {}, flight = {}, measurement_system = 'imperial', date = {} },
  require = require,
})
env.dofile = function(p) local f = loadfile(p); if f then setfenv(f, env); pcall(f) end end
local ok, err = run(scriptPath .. 'mainpanel_init.lua', env)
if not ok then out('ERR', clean(tostring(err))) end
for k, v in pairs(env) do
  if type(v) == 'table' and gauges[v] then
    local a = {}
    if type(v.arg_number) == 'number' then a[#a + 1] = tostring(v.arg_number) end
    for _, x in ipairs(v.args or {}) do if type(x) == 'number' then a[#a + 1] = tostring(x) end end
    local c = v.controller
    if type(c) ~= 'string' then c = '' end
    out('G', clean(tostring(k)), clean(c), table.concat(a, ','))
  end
end
local sn = rawget(env, 'shape_name')
if type(sn) == 'string' then out('SHAPE', clean(sn)) end
out('END')
";

        private const string ViewsScript = Common + @"
local env = sandbox({ _ = function(s) return s end })
local ok, err = run(arg[1], env)
if not ok then out('ERR', clean(tostring(err))) end
local vs = rawget(env, 'ViewSettings')
if type(vs) == 'table' and type(vs.Cockpit) == 'table' then
  local a = vs.Cockpit.CockpitAnchorPoint
  if type(a) == 'table' then out('ANCHOR', tostring(a[1] or 0), tostring(a[2] or 0), tostring(a[3] or 0)) end
  for i, c in ipairs(vs.Cockpit) do
    local e = type(c) == 'table' and c.EyePoint or nil
    local l = type(c) == 'table' and c.CockpitLocalPoint or nil
    if type(e) ~= 'table' then e = {0, 0, 0} end
    if type(l) ~= 'table' then l = {0, 0, 0} end
    out('SEAT', i, tostring(e[1] or 0), tostring(e[2] or 0), tostring(e[3] or 0), tostring(l[1] or 0), tostring(l[2] or 0), tostring(l[3] or 0))
  end
end
out('END')
";

        public static string LuaePath(string dcsRoot)
        {
            foreach (string sub in new[] { "bin", "bin-mt" })
            {
                string p = Path.Combine(dcsRoot ?? string.Empty, sub, "luae.exe");
                if (File.Exists(p))
                    return p;
            }
            return string.Empty;
        }

        // Runs a harness with luae.exe in the DCS folder; returns the printed lines.
        private static List<string> Run(string dcsRoot, string script, IEnumerable<string> args, string workDir, int timeoutMs)
        {
            string luae = LuaePath(dcsRoot);
            List<string> lines = new List<string>();
            if (luae.Length == 0)
                return lines;
            Directory.CreateDirectory(workDir);
            string file = Path.Combine(workDir, "harness-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".lua");
            File.WriteAllText(file, script, new UTF8Encoding(false));
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(luae)
                {
                    WorkingDirectory = dcsRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                StringBuilder a = new StringBuilder(Quote(file));
                foreach (string s in args)
                    a.Append(' ').Append(Quote(s));
                psi.Arguments = a.ToString();
                using (Process p = Process.Start(psi))
                {
                    StringBuilder err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    System.Threading.Tasks.Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch (Exception)
                        {
                        }
                    }
                    read.Wait(5000);
                    if (read.IsCompleted)
                        lines.AddRange(read.Result.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0));
                }
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
            return lines;
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string Slash(string folder)
        {
            string f = (folder ?? string.Empty).Replace('\\', '/');
            return f.EndsWith("/") ? f : f + "/";
        }

        // The input profiles each module declares (entry.lua InputProfiles: the names JoyPro's DCS command lists use,
        // "A-10C II", and their folders), filled by Flyables.
        public static List<string[]> InputProfiles = new List<string[]>();

        public static List<Flyable> Flyables(string dcsRoot, string workDir)
        {
            InputProfiles = new List<string[]>();
            string mods = Path.Combine(dcsRoot, "Mods", "aircraft");
            List<Flyable> list = new List<Flyable>();
            if (!Directory.Exists(mods))
                return list;
            List<string> folders = Directory.GetDirectories(mods).Select(Path.GetFileName).Where(f => File.Exists(Path.Combine(mods, f, "entry.lua"))).ToList();
            Directory.CreateDirectory(workDir);
            string listFile = Path.Combine(workDir, "modules.txt");
            File.WriteAllLines(listFile, folders, new UTF8Encoding(false));
            foreach (string line in Run(dcsRoot, EntryScript, new[] { Slash(mods), listFile }, workDir, 120000))
            {
                string[] c = line.Split('|');
                if (c.Length >= 4 && c[0] == "INP" && c[2].Length > 0)
                    InputProfiles.Add(new[] { c[1], c[2], c[3] });
                if (c.Length >= 4 && c[0] == "FLY" && c[2].Length > 0 && c[2] != "nil")
                {
                    string path = c[3] == "nil" ? string.Empty : c[3].Replace('/', '\\');
                    if (list.Any(f => f.Plane == c[2] && f.Module == c[1]))
                        continue;
                    list.Add(new Flyable { Module = c[1], Plane = c[2], CockpitPath = path });
                }
            }
            try
            {
                File.Delete(listFile);
            }
            catch (Exception)
            {
            }
            return list;
        }

        public static List<Clickable> Clickables(string dcsRoot, string cockpitPath, string workDir, List<string> errors)
        {
            List<Clickable> list = new List<Clickable>();
            foreach (string line in Run(dcsRoot, ClickScript, new[] { Slash(cockpitPath), Slash(dcsRoot) }, workDir, 60000))
            {
                string[] c = line.Split('|');
                if (c[0] == "ERR" && c.Length > 1)
                    errors.Add(c[1]);
                if (c[0] != "EL" || c.Length < 8)
                    continue;
                int dev;
                list.Add(new Clickable
                {
                    Name = c[1],
                    Hint = c[2].Trim(),
                    Device = int.TryParse(c[3], out dev) ? dev : -1,
                    DeviceName = c[4],
                    Actions = Split(c[5]),
                    Args = Split(c[6]),
                    Classes = Split(c[7]),
                    Values = c.Length > 8 ? Split(c[8]) : new List<string>(),
                    Limits = c.Length > 9 ? c[9].Split(';').ToList() : new List<string>()
                });
            }
            return list;
        }

        public static List<Gauge> Gauges(string dcsRoot, string cockpitPath, string workDir, out string shapeName)
        {
            shapeName = string.Empty;
            List<Gauge> list = new List<Gauge>();
            foreach (string line in Run(dcsRoot, GaugeScript, new[] { Slash(cockpitPath), Slash(dcsRoot) }, workDir, 60000))
            {
                string[] c = line.Split('|');
                if (c[0] == "SHAPE" && c.Length > 1)
                    shapeName = c[1];
                if (c[0] != "G" || c.Length < 4)
                    continue;
                Gauge g = new Gauge { Name = c[1], Controller = c[2] };
                foreach (string a in Split(c[3]))
                {
                    int v;
                    if (int.TryParse(a, out v))
                        g.Args.Add(v);
                }
                list.Add(g);
            }
            return list;
        }

        public static List<Seat> Seats(string dcsRoot, string viewsLua, string workDir)
        {
            List<Seat> seats = new List<Seat>();
            if (!File.Exists(viewsLua))
                return seats;
            double[] anchor = null;
            List<double[]> raw = new List<double[]>();
            foreach (string line in Run(dcsRoot, ViewsScript, new[] { viewsLua.Replace(Path.DirectorySeparatorChar, '/') }, workDir, 30000))
            {
                string[] c = line.Split('|');
                double[] v = c.Skip(c[0] == "SEAT" ? 2 : 1).Select(x => Num(x)).ToArray();
                if (c[0] == "ANCHOR" && v.Length >= 3)
                    anchor = v;
                else if (c[0] == "SEAT" && v.Length >= 6)
                    raw.Add(v);
            }
            if (raw.Count == 0)
                return seats;
            double[] origin = anchor ?? new[] { raw[0][3], raw[0][4], raw[0][5] };
            foreach (double[] v in raw)
                seats.Add(new Seat { EyePoint = new[] { v[3] - origin[0] + v[0], v[4] - origin[1] + v[1], v[5] - origin[2] + v[2] } });
            return seats;
        }

        private static double Num(string s)
        {
            double d;
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private static List<string> Split(string s)
        {
            return (s ?? string.Empty).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "nil").ToList();
        }
    }
}
