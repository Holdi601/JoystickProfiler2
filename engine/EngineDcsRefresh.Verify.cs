using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    public sealed class DcsLeftDefault
    {
        public string Profile { get; set; }
        public string Device { get; set; }
        public string Kind { get; set; }
        public string Command { get; set; }
        public string Key { get; set; }
        public bool JoyProFile { get; set; }
    }

    public sealed class DcsVerifyCleanResult
    {
        public string SavedGamesPath { get; set; }
        public string InstallPath { get; set; }
        public int ProfilesChecked { get; set; }
        public List<string> ProfileNames { get; set; } = new List<string>();
        public List<string> Devices { get; set; } = new List<string>();
        public int DefaultsLeft { get; set; }
        // Defaults left on devices JoyPro wrote a file for (a .jp next to the .diff.lua): these must be 0.
        public int DefaultsLeftOnJoyProDevices { get; set; }
        public Dictionary<string, int> LeftPerDevice { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<DcsLeftDefault> Left { get; set; } = new List<DcsLeftDefault>();
        // --dump-active: every bind DCS's own Lua has active on the checked joysticks (all of them, not only defaults),
        // one sorted line "profile|device|kind|command hash|key|modifiers|filter" each, written to this file.
        public string ActiveDumpFile { get; set; }
        public int ActiveBinds { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public static partial class EngineDcsRefresh
    {
        // engine dcs verify-clean: DCS's own Lua (bin\luae.exe, no game window) loads every input profile with a copy
        // of the given Saved Games\DCS\Config\Input and lists every default bind that is still active on a joystick:
        // an effective bind of a device that the device's .diff.lua does not add itself. The copy is temporary; the
        // given folder is only read. Checked devices: the joysticks connected now (as DCS names them), and with
        // includeDisconnected the other devices with files, through a fake stick of their type.
        public static DcsVerifyCleanResult VerifyClean(DcsRefreshOptions options, IList<string> deviceFilter, bool includeDisconnected, int maxListed, string dumpActivePath = null)
        {
            List<string> activeLines = string.IsNullOrWhiteSpace(dumpActivePath) ? null : new List<string>();
            DcsRefreshOptions normalized = NormalizeOptions(options);
            DcsVerifyCleanResult verify = new DcsVerifyCleanResult();
            verify.SavedGamesPath = normalized.SavedGamesPath;
            verify.InstallPath = normalized.InstallPath;
            DcsRefreshResult discovery = BuildRefreshResult("verify-clean", normalized);
            if (!ValidateRequiredPaths(discovery, requireSavedGames: true, requireInstall: true))
            {
                verify.Errors.AddRange(discovery.Errors);
                return verify;
            }
            if (!TryDiscoverAndFilterHeadlessProfileCandidates(normalized, discovery, out List<DcsHeadlessProfileCandidate> candidates))
            {
                verify.Errors.AddRange(discovery.Errors);
                return verify;
            }
            verify.Steps.AddRange(discovery.Steps);

            string sourceInput = Path.Combine(normalized.SavedGamesPath, "Config", "Input");
            string scratch = Path.Combine(Path.GetTempPath(), "JoyPro_dcs_verify_" + Guid.NewGuid().ToString("N"));
            try
            {
                string scratchInput = Path.Combine(scratch, "Config", "Input");
                Directory.CreateDirectory(scratchInput);
                // What DCS reads of the user's input folder: the device diffs, modifiers, wizard and disabled devices.
                Dictionary<string, List<string>> filesByDevice = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                HashSet<string> joyProFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(sourceInput))
                {
                    foreach (string file in new[] { "wizard.lua", "disabled.lua" })
                        if (File.Exists(Path.Combine(sourceInput, file)))
                            File.Copy(Path.Combine(sourceInput, file), Path.Combine(scratchInput, file));
                    foreach (string profile in Directory.GetDirectories(sourceInput))
                    {
                        string name = Path.GetFileName(profile);
                        string modifiers = Path.Combine(profile, "modifiers.lua");
                        string joystick = Path.Combine(profile, "joystick");
                        if (File.Exists(modifiers))
                        {
                            Directory.CreateDirectory(Path.Combine(scratchInput, name));
                            File.Copy(modifiers, Path.Combine(scratchInput, name, "modifiers.lua"));
                        }
                        if (!Directory.Exists(joystick))
                            continue;
                        string target = Path.Combine(scratchInput, name, "joystick");
                        Directory.CreateDirectory(target);
                        foreach (string diff in Directory.GetFiles(joystick, "*.diff.lua"))
                        {
                            string fileName = Path.GetFileName(diff);
                            File.Copy(diff, Path.Combine(target, fileName));
                            string device = fileName.Substring(0, fileName.Length - ".diff.lua".Length);
                            List<string> list;
                            if (!filesByDevice.TryGetValue(device, out list))
                                filesByDevice[device] = list = new List<string>();
                            list.Add(name + "/joystick/" + fileName);
                            if (File.Exists(Path.Combine(joystick, device + ".jp")))
                                joyProFiles.Add(name + "|" + device);
                        }
                    }
                }

                List<string> filters = (deviceFilter ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
                const int chunkSize = 8;
                Dictionary<string, string> fakeToDevice = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int start = 0; start < candidates.Count; start += chunkSize)
                {
                    List<DcsHeadlessProfileCandidate> chunk = candidates.Skip(start).Take(chunkSize).ToList();
                    EngineProgress.Report(Math.Min(candidates.Count, start + chunk.Count), candidates.Count, "DCS World, checking the default binds", chunk[0].UnitName);
                    string script = BuildVerifyScript(scratch, chunk, start == 0, includeDisconnected ? filesByDevice : null, filters, activeLines != null);
                    List<string> lines = RunLuaScript(normalized, script, verify.Errors);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split('|');
                        if (line.StartsWith("JOYPRO_FAKE|", StringComparison.Ordinal) && parts.Length >= 3)
                            fakeToDevice[parts[1]] = parts[2];
                        else if (line.StartsWith("JOYPRO_DEVICE|", StringComparison.Ordinal) && parts.Length >= 2)
                        {
                            if (!verify.Devices.Contains(parts[1]))
                                verify.Devices.Add(parts[1]);
                        }
                        else if (line.StartsWith("JOYPRO_CHECKED|", StringComparison.Ordinal))
                        {
                            verify.ProfilesChecked++;
                            if (parts.Length >= 2) verify.ProfileNames.Add(parts[1]);
                        }
                        else if (line.StartsWith("JOYPRO_LEFT|", StringComparison.Ordinal) && parts.Length >= 7)
                        {
                            string device = fakeToDevice.TryGetValue(parts[2], out string real) ? real : parts[2];
                            DcsLeftDefault left = new DcsLeftDefault
                            {
                                Profile = parts[1],
                                Device = device,
                                Kind = parts[3],
                                Command = parts[4],
                                Key = parts[5] + (parts[6].Length > 0 ? " + " + parts[6] : string.Empty),
                                JoyProFile = joyProFiles.Contains(parts[7 < parts.Length ? 7 : 1] + "|" + device)
                            };
                            verify.DefaultsLeft++;
                            if (left.JoyProFile)
                                verify.DefaultsLeftOnJoyProDevices++;
                            verify.LeftPerDevice[device] = (verify.LeftPerDevice.TryGetValue(device, out int count) ? count : 0) + 1;
                            if (verify.Left.Count < maxListed)
                                verify.Left.Add(left);
                        }
                        else if (activeLines != null && line.StartsWith("JOYPRO_ACTIVE|", StringComparison.Ordinal) && parts.Length >= 8)
                        {
                            parts[2] = fakeToDevice.TryGetValue(parts[2], out string realDevice) ? realDevice : parts[2];
                            activeLines.Add(string.Join("|", parts.Skip(1)));
                        }
                        else if (line.StartsWith("JOYPRO_PROFILE_ERR|", StringComparison.Ordinal))
                            verify.Warnings.Add("[headless-lua] " + line.Substring("JOYPRO_PROFILE_ERR|".Length));
                        else if (line.StartsWith("JOYPRO_FATAL|", StringComparison.Ordinal))
                            verify.Errors.Add("Headless Lua fatal error: " + line.Substring("JOYPRO_FATAL|".Length));
                    }
                    if (verify.Errors.Count > 0)
                        break;
                }
                verify.Devices = verify.Devices.Select(d => fakeToDevice.TryGetValue(d, out string real) ? real : d).Distinct(StringComparer.Ordinal).ToList();
                if (activeLines != null && verify.Errors.Count == 0)
                {
                    List<string> sorted = activeLines.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
                    File.WriteAllLines(dumpActivePath, sorted, new System.Text.UTF8Encoding(false));
                    verify.ActiveDumpFile = dumpActivePath;
                    verify.ActiveBinds = sorted.Count;
                }
            }
            catch (Exception ex)
            {
                verify.Errors.Add("DCS default-bind check failed: " + ex.Message);
            }
            finally
            {
                List<string> ignored = new List<string>();
                SafeDeleteDirectory(scratch, ignored, ignored);
            }
            return verify;
        }

        private static string BuildVerifyScript(string scratchSavedGames, List<DcsHeadlessProfileCandidate> chunk, bool checkDefaultSet,
            Dictionary<string, List<string>> disconnectedFiles, List<string> filters, bool dumpActive = false)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("-- JoyPro headless DCS default-bind check");
            AppendHeadlessPrelude(script, scratchSavedGames, chunk, null, false);
            script.AppendLine("  local function clean(s) return (tostring(s or ''):gsub('[|\\r\\n]', ' ')) end");
            script.AppendLine("  local function copyfile(src, dst)");
            script.AppendLine("    local f = io.open(src, 'rb'); if not f then return false end");
            script.AppendLine("    local data = f:read('*a'); f:close()");
            script.AppendLine("    local o = io.open(dst, 'wb'); if not o then return false end");
            script.AppendLine("    o:write(data); o:close(); return true");
            script.AppendLine("  end");
            script.AppendLine("  local filters = {");
            foreach (string filter in filters)
                script.AppendLine("    '" + EscapeLuaString(filter) + "',");
            script.AppendLine("  }");
            script.AppendLine("  local function wanted(d)");
            script.AppendLine("    if #filters == 0 then return true end");
            script.AppendLine("    local t = InputUtils.getDeviceTemplateName(d)");
            script.AppendLine("    for i = 1, #filters do");
            script.AppendLine("      local f = filters[i]");
            script.AppendLine("      if d == f or t == f or (t:gsub('^%s+', ''):gsub('%s+$', '')) == f or string.find(d, f, 1, true) then return true end");
            script.AppendLine("    end");
            script.AppendLine("    return false");
            script.AppendLine("  end");
            script.AppendLine("  local real = {}");
            script.AppendLine("  for _, d in ipairs(rawGetDevices() or {}) do");
            script.AppendLine("    if d ~= keyboardName and rawGetDeviceTypeName(d) == 'joystick' then real[d] = true end");
            script.AppendLine("  end");
            // A disconnected device: a fake stick of its type gets a copy of each of its files.
            script.AppendLine("  local disconnected = {");
            if (disconnectedFiles != null)
            {
                foreach (KeyValuePair<string, List<string>> device in disconnectedFiles.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    script.Append("    { device = '" + EscapeLuaString(device.Key) + "', files = {");
                    foreach (string file in device.Value)
                        script.Append(" '" + EscapeLuaString(file) + "',");
                    script.AppendLine(" } },");
                }
            }
            script.AppendLine("  }");
            script.AppendLine("  for i = 1, #disconnected do");
            script.AppendLine("    local entry = disconnected[i]");
            script.AppendLine("    if not real[entry.device] and wanted(entry.device) then");
            script.AppendLine("      local before = {}");
            script.AppendLine("      for _, d in ipairs(rawGetDevices() or {}) do before[d] = true end");
            script.AppendLine("      pcall(Input.addFakeJoystick, InputUtils.getDeviceTemplateName(entry.device))");
            script.AppendLine("      for _, d in ipairs(rawGetDevices() or {}) do");
            script.AppendLine("        if not before[d] then");
            script.AppendLine("          print('JOYPRO_FAKE|' .. clean(d) .. '|' .. clean(entry.device))");
            script.AppendLine("          for _, rel in ipairs(entry.files) do");
            script.AppendLine("            local dir = rel:match('^(.*)/[^/]*$')");
            script.AppendLine("            copyfile(userConfigPath .. rel, userConfigPath .. dir .. '/' .. d .. '.diff.lua')");
            script.AppendLine("          end");
            script.AppendLine("        end");
            script.AppendLine("      end");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  local checkDevices = {}");
            script.AppendLine("  for _, d in ipairs(rawGetDevices() or {}) do");
            script.AppendLine("    if d ~= keyboardName and rawGetDeviceTypeName(d) == 'joystick' then");
            script.AppendLine("      if string.find(d, '{FAKE-JOYSTICK-', 1, true) or wanted(d) then");
            script.AppendLine("        table.insert(checkDevices, d)");
            script.AppendLine("        print('JOYPRO_DEVICE|' .. clean(d))");
            script.AppendLine("      end");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  local unitProfiles = {}");
            script.AppendLine("  for i = 1, #extraProfiles do unitProfiles[extraProfiles[i].unit] = true end");
            script.AppendLine("  local checkDefaultSet = " + (checkDefaultSet ? "true" : "false"));
            script.AppendLine("  for i = 1, #merged do");
            script.AppendLine("    local info = merged[i]");
            script.AppendLine("    local okCreate, createErr = pcall(InputData.createProfile, info)");
            script.AppendLine("    if not okCreate then print('JOYPRO_PROFILE_ERR|' .. clean(info and info.name) .. '|createProfile failed: ' .. clean(createErr)) end");
            script.AppendLine("  end");
            script.AppendLine("  local function sig(hash, key, reformers)");
            script.AppendLine("    local r = {}");
            script.AppendLine("    for _, v in ipairs(reformers or {}) do table.insert(r, v) end");
            script.AppendLine("    table.sort(r)");
            script.AppendLine("    return tostring(hash) .. '|' .. tostring(key) .. '|' .. table.concat(r, ',')");
            script.AppendLine("  end");
            script.AppendLine("  for _, profileName in ipairs(InputData.getProfileNames() or {}) do");
            script.AppendLine("    local unitName = InputData.getProfileUnitName(profileName) or profileName");
            script.AppendLine("    if unitProfiles[unitName] or checkDefaultSet then");
            script.AppendLine("      local folder = userConfigPath .. string.gsub(unitName, '([%*/%?<>%|%\\\\%:\"])', '') .. '/joystick/'");
            script.AppendLine("      local okKeys, keyCommands = pcall(InputData.getProfileKeyCommands, profileName)");
            script.AppendLine("      local okAxes, axisCommands = pcall(InputData.getProfileAxisCommands, profileName)");
            script.AppendLine("      if okKeys and okAxes then");
            script.AppendLine("        for _, d in ipairs(checkDevices) do");
            script.AppendLine("          local added = {}");
            script.AppendLine("          local f = rawLoadfile(folder .. d .. '.diff.lua')");
            script.AppendLine("          if f then");
            script.AppendLine("            setfenv(f, {})");
            script.AppendLine("            local okDiff, diff = pcall(f)");
            script.AppendLine("            if okDiff and type(diff) == 'table' then");
            script.AppendLine("              for _, section in ipairs({ 'keyDiffs', 'axisDiffs' }) do");
            script.AppendLine("                for hash, info in pairs(diff[section] or {}) do");
            script.AppendLine("                  for _, a in ipairs(info.added or {}) do added[sig(hash, a.key, a.reformers)] = true end");
            script.AppendLine("                  for _, a in ipairs(info.changed or {}) do added[sig(hash, a.key, a.reformers)] = true end");
            script.AppendLine("                end");
            script.AppendLine("              end");
            script.AppendLine("            end");
            script.AppendLine("          end");
            script.AppendLine("          for _, kind in ipairs({ 'key', 'axis' }) do");
            script.AppendLine("            local list = kind == 'key' and keyCommands or axisCommands");
            script.AppendLine("            for _, command in ipairs(list or {}) do");
            script.AppendLine("              local combos = command.combos and command.combos[d]");
            script.AppendLine("              if combos and #combos > 0 then");
            script.AppendLine("                local hash = kind == 'key' and InputUtils.getKeyCommandHash(command) or InputUtils.getAxisCommandHash(command)");
            script.AppendLine("                for _, combo in ipairs(combos) do");
            if (dumpActive)
            {
                script.AppendLine("                  local r = {}");
                script.AppendLine("                  for _, v in ipairs(combo.reformers or {}) do table.insert(r, v) end");
                script.AppendLine("                  table.sort(r)");
                script.AppendLine("                  local fl = ''");
                script.AppendLine("                  local ft = combo.filter");
                script.AppendLine("                  if type(ft) == 'table' then");
                script.AppendLine("                    local c = {}");
                script.AppendLine("                    for _, v in ipairs(ft.curvature or {}) do table.insert(c, string.format('%.4f', v)) end");
                script.AppendLine("                    fl = 'c=' .. table.concat(c, ',') .. ';d=' .. string.format('%.4f', ft.deadzone or 0) .. ';i=' .. tostring(ft.invert and true or false) .. ';s=' .. tostring(ft.slider and true or false) .. ';x=' .. string.format('%.4f', ft.saturationX or 1) .. ';y=' .. string.format('%.4f', ft.saturationY or 1)");
                script.AppendLine("                  end");
                script.AppendLine("                  print('JOYPRO_ACTIVE|' .. clean(profileName) .. '|' .. clean(d) .. '|' .. kind .. '|' .. clean(hash) .. '|' .. clean(combo.key) .. '|' .. clean(table.concat(r, ',')) .. '|' .. clean(fl))");
            }
            script.AppendLine("                  if not added[sig(hash, combo.key, combo.reformers)] then");
            script.AppendLine("                    print('JOYPRO_LEFT|' .. clean(profileName) .. '|' .. clean(d) .. '|' .. kind .. '|' .. clean(command.name) .. '|' .. clean(combo.key) .. '|' .. clean(table.concat(combo.reformers or {}, ',')) .. '|' .. clean(unitName))");
            script.AppendLine("                  end");
            script.AppendLine("                end");
            script.AppendLine("              end");
            script.AppendLine("            end");
            script.AppendLine("          end");
            script.AppendLine("        end");
            script.AppendLine("        print('JOYPRO_CHECKED|' .. clean(profileName))");
            script.AppendLine("      else");
            script.AppendLine("        print('JOYPRO_PROFILE_ERR|' .. clean(profileName) .. '|reading the commands failed')");
            script.AppendLine("      end");
            script.AppendLine("    end");
            script.AppendLine("  end");
            script.AppendLine("  pcall(InputData.unloadProfiles)");
            script.AppendLine("  print('JOYPRO_DONE|verify')");
            script.AppendLine("end)");
            script.AppendLine("if not ok then");
            script.AppendLine("  print('JOYPRO_FATAL|' .. tostring(fatal))");
            script.AppendLine("  os.exit(1)");
            script.AppendLine("end");
            return script.ToString();
        }

        // Runs a luae script in the DCS folder and returns its stdout lines (errors: no luae, timeout, crash).
        private static List<string> RunLuaScript(DcsRefreshOptions options, string scriptContent, List<string> errors)
        {
            List<string> lines = new List<string>();
            string luaePath = ResolveLuaExecutable(options);
            if (string.IsNullOrWhiteSpace(luaePath) || !File.Exists(luaePath))
            {
                errors.Add("luae.exe was not found under the DCS install's bin folder.");
                return lines;
            }
            string scriptPath = Path.Combine(Path.GetTempPath(), "joypro_dcs_verify_" + Guid.NewGuid().ToString("N") + ".lua");
            try
            {
                File.WriteAllText(scriptPath, scriptContent, Encoding.Default);
                ProcessStartInfo start = new ProcessStartInfo(luaePath, "\"" + scriptPath + "\"")
                {
                    WorkingDirectory = NormalizePath(options.InstallPath),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default,
                    CreateNoWindow = true
                };
                object sync = new object();
                using (Process process = new Process())
                {
                    process.StartInfo = start;
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sync) lines.Add(e.Data); };
                    process.ErrorDataReceived += (s, e) => { };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!WaitForExitOrCancel(process, 15 * 60 * 1000, "checking the DCS default binds"))
                    {
                        try { process.Kill(); } catch { }
                        errors.Add("The DCS default-bind check timed out.");
                        return lines;
                    }
                    process.WaitForExit();
                    lock (sync)
                    {
                        if (process.ExitCode != 0 && !lines.Any(l => l.StartsWith("JOYPRO_FATAL|", StringComparison.Ordinal)))
                            errors.Add("luae.exe ended with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                        return new List<string>(lines);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("Running luae.exe failed: " + ex.Message);
                return lines;
            }
            finally
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            }
        }

        public static string ToText(DcsVerifyCleanResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS default-bind check");
            sb.AppendLine("Saved Games Path: " + result.SavedGamesPath);
            sb.AppendLine("Install Path: " + result.InstallPath);
            sb.AppendLine("Profiles Checked: " + result.ProfilesChecked + (result.ProfileNames.Count > 0 && result.ProfileNames.Count <= 20 ? " (" + string.Join(", ", result.ProfileNames) + ")" : string.Empty));
            sb.AppendLine("Devices Checked: " + result.Devices.Count);
            foreach (string device in result.Devices)
                sb.AppendLine("  " + device + ": " + (result.LeftPerDevice.TryGetValue(device, out int n) ? n : 0) + " default bind(s) left");
            sb.AppendLine("Defaults Left: " + result.DefaultsLeft);
            sb.AppendLine("Defaults Left On JoyPro Devices: " + result.DefaultsLeftOnJoyProDevices);
            if (result.Left.Count > 0)
            {
                sb.AppendLine("Left (first " + result.Left.Count + "):");
                foreach (DcsLeftDefault left in result.Left)
                    sb.AppendLine("  " + left.Profile + " | " + left.Device + " | " + left.Kind + " | " + left.Command + " | " + left.Key + (left.JoyProFile ? " | JoyPro file" : string.Empty));
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings.Take(40))
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }
    }
}
