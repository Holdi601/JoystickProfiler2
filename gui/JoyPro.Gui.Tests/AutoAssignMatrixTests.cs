using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The combination matrix of Auto Assign (docs/auto-assign.md#combination-matrix): device sets made of real catalog devices
// (engine/tests/fixtures/autoassign-matrix/*.tsv, JOYPRO_FAKE_DEVICES) x options, each planned by the real engine
// (setup plan) and checked by SetupPlanChecks (no input for two relations of one aircraft, at most two shift modifiers,
// trigger / pinky only for their functions, MFD keys only MFD functions in OSB order, printed labels only their
// functions, slew is not TMS, CMS forward = dispense, one device per hand, phase layers only their phase, no modifier
// layer for a game without modifier binds); the full run also lets Auto Assign (relations auto-assign) lay out the
// relations the plan made and checks its assignments, and writes a report a person can read.
//   always: three sets (small, a pit with panels and MFDs, two sticks for space) and the same plan twice
//   full:   set JOYPRO_AUTOASSIGN_MATRIX=<folder> & dotnet test gui/JoyPro.Gui.Tests --filter AutoAssignMatrixTests
public sealed class AutoAssignMatrixTests
{
    private const string Priorities = "F-16C_50>FA-18C_hornet|A-10C II>AH-64D_BLK_II_PLT|UH-1H>StarCitizen";

    private sealed record Combination(string Name, string Set, string Games, string[] Extra, string Description);

    private static readonly Combination[] All =
    [
        new("stick-only", "stick-only", "DCS,StarCitizen", [], "Logitech Extreme 3D Pro alone"),
        new("stick-only-no-modifiers", "stick-only", "DCS,StarCitizen", ["--modifiers", "0"], "the same with no modifier wanted (JoyPro may add up to 2 and says so)"),
        new("hotas-combo", "hotas-combo", "DCS,StarCitizen", [], "Thrustmaster T.Flight Hotas X (stick and throttle in one)"),
        new("hotas-xplane", "hotas-combo", "DCS,XPlane12", [], "the same for DCS and X-Plane 12 (X-Plane has no modifier binds)"),
        new("stick-throttle", "stick-throttle", "DCS,StarCitizen", [], "small: T.16000M + TWCS"),
        new("stick-throttle-pedals", "stick-throttle-pedals", "DCS,StarCitizen", [], "medium: Warthog stick + throttle + TFRP pedals"),
        new("stick-throttle-pedals-phase", "stick-throttle-pedals", "DCS,StarCitizen", ["--phase-layers", "on"], "the same with phase layers on"),
        new("stick-throttle-pedals-twist", "stick-throttle-pedals-twist", "DCS,StarCitizen", [], "T.16000M + TWCS + TFRP"),
        new("pit-panels-mfds", "pit-panels-mfds", "DCS,StarCitizen", [], "Warthog HOTAS + TFRP + WinWing take-off panel + 2 Cougar MFDs"),
        new("pit-keypads", "pit-panels-mfds", "DCS,StarCitizen", ["--add-device", "winwing-ufc1", "--add-device", "winwing-f18-combat-ready-panel"], "the pit + WinWing UFC + combat ready panel (from the catalog)"),
        new("space-dual-sticks", "space-dual-sticks", "DCS,StarCitizen", [], "two VKB Gladiator NXT (right and left) + TFRP"),
        new("heli-collective", "heli-collective", "DCS,StarCitizen", [], "VKB NXT + Virpil SharKa-50 collective + TFRP"),
        new("yoke-quadrant", "yoke-quadrant", "DCS,StarCitizen", [], "Honeycomb Alpha yoke + Bravo quadrant"),
        new("mode-switch-small", "mode-switch-small", "DCS,StarCitizen", [], "Logitech X56 stick + throttle (switches on the throttle), phase layers automatic"),
        new("mode-switch-small-phase", "mode-switch-small", "DCS,StarCitizen", ["--phase-layers", "on"], "the same with phase layers on"),
        new("generic-unknown", "generic-unknown", "DCS,StarCitizen", [], "an unknown USB stick + an Arduino button box"),
        new("author-pit", "../setupwizard/devices", "DCS,StarCitizen", [], "the author's pit: VKB Gunfighter MCG Pro, Rhino, WarBRD Alpha-L, MT-50CM, Crosswind, WinWing PTO, vJoy, Wooting")
    ];

    private static readonly string[] Always = ["stick-throttle", "pit-panels-mfds", "space-dual-sticks"];

    private static string? Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "tests", "fixtures")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? Engine(string root)
    {
        string exe = Path.Combine(root, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static (int Exit, string Output, double Seconds) Run(string exe, string devices, string data, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = data;
        info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_ENGINE_SERVER"] = "off";
        Stopwatch watch = Stopwatch.StartNew();
        using Process process = Process.Start(info)!;
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result, watch.Elapsed.TotalSeconds);
    }

    private sealed class Result
    {
        public Combination Combination = null!;
        public SetupPlan Plan = null!;
        public SetupCheckReport Report = null!;
        public double Seconds;
        public string Text = string.Empty;
        public string PlanJson = string.Empty;
        public int AutoAssigned;
        public int AutoUnassigned;
        public List<string> AutoModifiers = [];
        public List<string> AutoProblems = [];
    }

    private static Dictionary<string, SetupFunction> Functions(string root)
    {
        SetupCatalog.Load(Path.Combine(root, "EngineOnly_Runtime", "DB", "Functions"), [], out Dictionary<string, SetupFunction> functions, out _, out _);
        return functions;
    }

    private static Result Plan(string root, string exe, Combination combination, string folder, Dictionary<string, SetupFunction> functions, bool autoAssign)
    {
        string devices = Path.GetFullPath(Path.Combine(root, "engine", "tests", "fixtures", "autoassign-matrix", combination.Set + ".tsv"));
        string data = Path.Combine(folder, combination.Name, "data");
        Directory.CreateDirectory(data);
        string planFile = Path.Combine(folder, combination.Name, "plan.json");
        int at = Array.IndexOf(combination.Extra, "--modifiers");
        string modifiers = at >= 0 ? combination.Extra[at + 1] : "1";
        List<string> args = ["setup", "plan", "--games", combination.Games, "--priorities", Priorities, "--modifiers", modifiers, "--prefer-fewer", "--realism", "0.3", "--out-plan", planFile, "--text"];
        args.AddRange(combination.Extra.Where((x, i) => at < 0 || (i != at && i != at + 1)));
        (int exit, string output, double seconds) = Run(exe, devices, data, args);
        Assert.True(exit == 0, combination.Name + ": " + output);
        Result result = new() { Combination = combination, Seconds = seconds, Text = output, PlanJson = File.ReadAllText(planFile) };
        result.Plan = SetupPlanJson.Read(result.PlanJson);
        Dictionary<string, string> support = new(StringComparer.OrdinalIgnoreCase) { ["XPlane12"] = "no", ["XPlane11"] = "no", ["DCS"] = "yes", ["StarCitizen"] = "yes" };
        int wanted = combination.Extra.Contains("--modifiers") ? int.Parse(combination.Extra[Array.IndexOf(combination.Extra, "--modifiers") + 1], CultureInfo.InvariantCulture) : 1;
        result.Report = SetupPlanChecks.Check(result.Plan, functions, support, wanted);
        if (autoAssign)
            AutoAssign(exe, devices, data, planFile, Path.Combine(folder, combination.Name), functions, result);
        return result;
    }

    // Auto Assign on the relations the plan made: every relation laid out again, without the previous controls.
    private static void AutoAssign(string exe, string devices, string data, string planFile, string folder, Dictionary<string, SetupFunction> functions, Result result)
    {
        string profile = Path.Combine(folder, "profile.pr0file");
        (int exit, string output, _) = Run(exe, devices, data, ["--profile", profile, "setup", "apply", "--plan", planFile, "--force"]);
        if (exit != 0)
        {
            result.AutoProblems.Add("setup apply failed: " + output.Trim());
            return;
        }
        (exit, output, _) = Run(exe, devices, data, ["--profile", profile, "relations", "auto-assign", "--overwrite", "--no-style", "--dry-run"]);
        if (exit != 0)
        {
            result.AutoProblems.Add("relations auto-assign failed: " + output.Trim());
            return;
        }
        int start = output.IndexOf('{');
        using JsonDocument doc = JsonDocument.Parse(output.Substring(Math.Max(0, start)));
        JsonElement root = doc.RootElement;
        result.AutoUnassigned = root.GetProperty("Unassigned").GetArrayLength();
        result.AutoModifiers = root.GetProperty("ModifiersUsed").EnumerateArray().Select(m => m.GetString() ?? string.Empty).ToList();
        Regex dedicated = new(@"^stick\.trigger\.|^stick\.weapon_release$|\.pinky\.");
        Regex osb = new(@"\.osb\.([TBLR]\d+)$");
        foreach (JsonElement a in root.GetProperty("Assignments").EnumerateArray())
        {
            result.AutoAssigned++;
            string relation = a.GetProperty("Relation").GetString() ?? string.Empty;
            string slot = a.GetProperty("Slot").GetString() ?? string.Empty;
            string full = a.GetProperty("Control").GetString() ?? string.Empty;
            int quote = full.IndexOf(" \"", StringComparison.Ordinal);
            string control = quote >= 0 ? full.Substring(0, quote) : full;
            string label = quote >= 0 ? full.Substring(quote + 2).TrimEnd('"') : string.Empty;
            functions.TryGetValue(slot, out SetupFunction? f);
            bool listed = f != null && f.Prefs.Any(p => SetupPlanner.PatternMatch(null, p, control));
            bool mfdFunction = f != null && SetupPhases.IsMfdFunction(f.Prefs);
            if (dedicated.IsMatch(control) && !listed)
                result.AutoProblems.Add("C3 " + relation + " on " + control);
            if (SetupPhases.IsMfdControl(control) && !mfdFunction)
                result.AutoProblems.Add("C4 " + relation + " (not an MFD function) on " + control);
            if (mfdFunction && osb.Match(f!.Prefs[0]) is { Success: true } want && osb.Match(control) is { Success: true } got && want.Groups[1].Value != got.Groups[1].Value)
                result.AutoProblems.Add("C4 " + relation + " on " + control);
            bool exact = f != null && f.Prefs.Count > 0 && f.Prefs[0].IndexOf('*') < 0 && string.Equals(f.Prefs[0], control, StringComparison.OrdinalIgnoreCase);
            if (control.StartsWith("panel.", StringComparison.Ordinal) && SetupPhases.IsFunctionLabel(label) && !exact && !SetupPhases.LabelNames(f, label))
                result.AutoProblems.Add("C5 " + relation + " on \"" + label + "\"");
            else if (control.Contains(".base.", StringComparison.Ordinal) && SetupPhases.IsCockpitLabel(label) && !exact && f != null && !SetupPhases.LabelNames(f, label) &&
                     (SetupPhases.LabelContradicts(f.Labels, f.Id + " " + f.Name, label) || SetupPhases.IsCriticalFunction(f.Id, f.Crit, string.Empty)))
                result.AutoProblems.Add("C5 " + relation + " on \"" + label + "\"");
            if (f != null && !listed && SetupPhases.SlewTmsClash(slot, f.Group, control))
                result.AutoProblems.Add("C6 " + relation + " on " + control);
        }
    }

    [Fact]
    public void Matrix_ThreeSets_KeepEveryRule_AndTheSamePlanTwice()
    {
        string? root = Root();
        string? exe = root == null ? null : Engine(root);
        if (root == null || exe == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "autoassign-matrix", Guid.NewGuid().ToString("N"));
        Dictionary<string, SetupFunction> functions = Functions(root);
        foreach (Combination combination in All.Where(c => Always.Contains(c.Name)))
        {
            Result result = Plan(root, exe, combination, folder, functions, autoAssign: false);
            Assert.True(result.Report.Passed, combination.Name + Environment.NewLine + SetupPlanChecks.ToText(result.Report));
            Assert.True(result.Report.Placed > 50, combination.Name + ": only " + result.Report.Placed + " functions placed");
            if (combination.Name == "pit-panels-mfds")
            {
                Assert.Contains(result.Plan.Placed, r => r.Control == "mfd.left.osb.T1" && r.Function == "mfd_left_t1");
                Assert.Contains(result.Plan.Placed, r => r.Function == "gearin" && r.Label == "LDG GEAR UP");
            }
            if (combination.Name == "space-dual-sticks")
                Assert.Contains(result.Plan.Placed, r => r.Function == "space_strafe_hor" && result.Plan.Devices.Any(d => d.Device == r.Device && d.Role == SetupRoles.LeftStick));
        }
        Result first = Plan(root, exe, All.First(c => c.Name == "stick-throttle"), Path.Combine(folder, "again1"), functions, autoAssign: false);
        Result second = Plan(root, exe, All.First(c => c.Name == "stick-throttle"), Path.Combine(folder, "again2"), functions, autoAssign: false);
        Assert.Equal(first.PlanJson, second.PlanJson);
    }

    [Fact]
    public void Matrix_Full_Report()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_AUTOASSIGN_MATRIX");
        string? root = Root();
        string? exe = root == null ? null : Engine(root);
        if (string.IsNullOrWhiteSpace(outDir) || root == null || exe == null)
            return;
        Directory.CreateDirectory(outDir);
        Dictionary<string, SetupFunction> functions = Functions(root);
        List<Result> results = [];
        foreach (Combination combination in All)
        {
            Result result = Plan(root, exe, combination, outDir, functions, autoAssign: true);
            File.WriteAllText(Path.Combine(outDir, combination.Name, "plan.txt"), result.Text);
            File.WriteAllText(Path.Combine(outDir, combination.Name, "checks.txt"), SetupPlanChecks.ToText(result.Report) + Environment.NewLine +
                "Auto Assign: " + result.AutoAssigned + " assigned, " + result.AutoUnassigned + " not assigned, modifiers: " + string.Join("; ", result.AutoModifiers) + Environment.NewLine +
                string.Join(Environment.NewLine, result.AutoProblems));
            results.Add(result);
        }
        bool deterministic = Plan(root, exe, All.First(c => c.Name == "stick-throttle"), Path.Combine(outDir, "again"), functions, autoAssign: false).PlanJson ==
                             results.First(r => r.Combination.Name == "stick-throttle").PlanJson;
        File.WriteAllText(Path.Combine(outDir, "report.md"), Report(results, deterministic, root));
        foreach (Result result in results)
        {
            Assert.True(result.Report.Passed, result.Combination.Name + Environment.NewLine + SetupPlanChecks.ToText(result.Report));
            Assert.True(result.AutoProblems.Count == 0, result.Combination.Name + ": " + string.Join("; ", result.AutoProblems.Take(10)));
        }
        Assert.True(deterministic);
    }

    private static string Report(List<Result> results, bool deterministic, string root)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Auto Assign combination matrix");
        sb.AppendLine();
        sb.AppendLine("Made by `AutoAssignMatrixTests.Matrix_Full_Report` on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                      ". Every combination is planned by `engine setup plan` (games, priorities F-16C_50 > FA-18C_hornet = A-10C II > AH-64D_BLK_II_PLT = UH-1H > StarCitizen, 1 modifier as few as possible, realism 30 %) and checked by `SetupPlanChecks`; then `engine relations auto-assign --overwrite --no-style` lays out the same relations again and its assignments are checked too.");
        sb.AppendLine();
        sb.AppendLine("Checks: C1 no input for two relations of one aircraft, C2 at most 2 shift modifiers and 2 phase layers, C3 trigger / weapon release / pinky only their functions, C4 MFD keys only MFD functions in OSB order, C5 printed panel labels only their functions, C6 slew is not TMS, C7 CMS forward dispenses / left-right program, C8 one device per hand, C9 phase layers only their phase, C10 no modifier layer for a game without modifier binds, C11 directional sets together on one hat's matching directions.");
        sb.AppendLine();
        sb.AppendLine("| Combination | Devices and roles | Plan | Placed | Essential (F-16) | Time-critical on grips | Shift / phase | Checks | Auto Assign | s |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (Result r in results)
        {
            string roles = string.Join("; ", r.Plan.Devices.Where(d => SetupRoles.Binds(d.Role)).Select(d => d.Model + " = " + d.Role));
            string checks = r.Report.Passed ? "all pass" : string.Join(", ", r.Report.Checks.Where(c => !c.Passed).Select(c => c.Id + " (" + c.Problems.Count + ")"));
            string auto = r.AutoAssigned + " assigned, " + r.AutoUnassigned + " not" + (r.AutoModifiers.Count > 0 ? ", " + r.AutoModifiers.Count + " modifiers" : string.Empty) +
                          (r.AutoProblems.Count == 0 ? ", checks pass" : ", " + r.AutoProblems.Count + " problems");
            sb.AppendLine("| " + r.Combination.Name + " | " + r.Combination.Description + ": " + roles + " | " + string.Join(" ", r.Combination.Extra) + " | " + r.Report.Placed + " / " + (r.Report.Placed + r.Report.NotPlaced) +
                          " | " + r.Report.EssentialPlaced + "/" + r.Report.EssentialTotal + " | " + r.Report.InstantOnGrips + "/" + r.Report.Instant + " | " + r.Report.Modifiers + " / " + r.Report.PhaseLayers +
                          (r.Report.OnPhaseLayers > 0 ? " (" + r.Report.OnPhaseLayers + " functions)" : string.Empty) + " | " + checks + " | " + auto + " | " + r.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " |");
        }
        sb.AppendLine();
        sb.AppendLine("Same plan twice (stick-throttle): " + (deterministic ? "identical" : "DIFFERENT"));
        sb.AppendLine();
        sb.AppendLine("## Notes per combination");
        foreach (Result r in results)
        {
            sb.AppendLine();
            sb.AppendLine("### " + r.Combination.Name);
            foreach (string note in r.Plan.Notes.Where(n => !n.StartsWith("Realism", StringComparison.Ordinal) && !n.StartsWith("Function map", StringComparison.Ordinal)))
                sb.AppendLine("- " + note);
            foreach (SetupCheck check in r.Report.Checks.Where(c => !c.Passed))
                sb.AppendLine("- FAIL " + check.Id + ": " + string.Join("; ", check.Problems.Take(5)));
            foreach (string problem in r.AutoProblems.Take(5))
                sb.AppendLine("- Auto Assign: " + problem);
            List<SetupPlanRelation> phase = r.Plan.Placed.Where(p => r.Plan.Modifiers.Any(m => m.Switch && m.Name == p.Modifier)).ToList();
            if (phase.Count > 0)
                sb.AppendLine("- On phase layers: " + string.Join(", ", phase.Take(12).Select(p => p.Name + " (" + p.Modifier + ")")) + (phase.Count > 12 ? " ..." : string.Empty));
        }
        sb.AppendLine();
        sb.AppendLine("## Printed panel labels without a function (device catalog audit)");
        sb.AppendLine();
        sb.AppendLine("Cockpit replicas take only the functions their labels name; these labels match no label keyword of the function catalog (`DB/Functions/slot_controls.tsv`), so their controls stay free.");
        sb.AppendLine();
        Dictionary<string, SetupFunction> functions = Functions(root);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "EngineOnly_Runtime", "DB", "Devices"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("devices");
            foreach (JsonElement device in list.EnumerateArray())
            {
                string kind = device.TryGetProperty("kind", out JsonElement k) ? k.GetString() ?? string.Empty : string.Empty;
                if (kind != "panel" || !device.TryGetProperty("controls", out JsonElement controls))
                    continue;
                List<(string Control, string Label)> items = controls.EnumerateArray().Where(c => c.TryGetProperty("input", out JsonElement i) && i.GetString()?.Length > 0)
                    .Select(c => (c.GetProperty("control").GetString() ?? string.Empty, c.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? string.Empty : string.Empty)).ToList();
                if (items.Count == 0 || !SetupPhases.IsReplicaPanel(kind, items.Select(i => i.Label)))
                    continue;
                List<string> dead = items.Where(i => SetupPhases.IsFunctionLabel(i.Label) && !functions.Values.Any(f => SetupPhases.LabelNames(f, i.Label) ||
                                                                                                                  (f.Prefs.Count > 0 && string.Equals(f.Prefs[0], i.Control, StringComparison.OrdinalIgnoreCase))))
                    .Select(i => i.Label).Distinct().ToList();
                int printed = items.Count(i => SetupPhases.IsFunctionLabel(i.Label));
                sb.AppendLine("- " + device.GetProperty("id").GetString() + ": " + (printed - dead.Count) + " of " + printed + " printed labels have a function" +
                              (dead.Count > 0 ? "; without: " + string.Join(", ", dead.Take(14)) + (dead.Count > 14 ? " +" + (dead.Count - 14) : string.Empty) : string.Empty));
            }
        }
        return sb.ToString();
    }
}
