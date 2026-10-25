using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// Lay out with AI (docs/layout-with-ai.md):
// - Phrases_*: the user's words -> the preferences JoyPro understands and what they compile into (roles, rules, options,
//   curve / force feedback / vJoy wishes), on four device sets (small: stick + throttle; medium: + pedals; the author's
//   big pit; a space setup with two sticks). Plain C#, no engine.
// - Engine_*: the same phrases through the real engine (engine arrange ask, fake devices, the shipped catalogs) and the
//   layout decisions in the plan: trim on a hat, no modifier, curves only for helicopters, rudder on the twist without
//   pedals, space yaw on the left stick's twist, the saved preferences used again. The big pit runs on a copy of a real
//   profile when JOYPRO_LAYOUT_BIG_PROFILE names one.
// - ViewModel_*: the Binds page panel with a fake engine (preview, untick, Apply with --skip, Undo, forget a preference)
//   and that its choices survive a restart.
// - RenderLayoutWithAi: offscreen pictures at 1280 and 2267, light and dark (JOYPRO_LAYOUT_AI_OUT).
[SupportedOSPlatform("windows")]
public sealed class LayoutWithAiTests
{
    private static readonly Dictionary<string, string[]> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DCS"] = ["dcs"],
        ["StarCitizen"] = ["star citizen", "sc"],
        ["IL2Game"] = ["il-2", "il2"]
    };

    private static readonly string[] Planes = ["F-16C_50", "FA-18C_hornet", "A-10C II", "AH-64D_BLK_II_PLT", "UH-1H", "StarCitizen"];

    // ---- device sets ----

    private static SetupDevice Device(string name, string kind, string hand = "none", bool ffb = false) =>
        new() { Device = name + " {00000000-0000-0000-0000-000000000001}", Model = name, Kind = kind, Hand = hand, ForceFeedback = ffb, Buttons = 16, Axes = 3 };

    private static List<SetupDevice> Small() => [Device("T.16000M", "stick", "either"), Device("TWCS Throttle", "throttle", "left")];

    private static List<SetupDevice> Medium() => [.. Small(), Device("T-Rudder", "pedals")];

    private static List<SetupDevice> Big() =>
    [
        Device("VKB-Sim Gunfighter Modern Combat Pro Twist", "stick", "right"), Device("Rhino FFB Joystick", "stick", "right", ffb: true),
        Device("L-VPC Stick WarBRD", "stick", "left"), Device("LEFT VPC Throttle MT-50 CM", "throttle", "left"), Device("MFG Crosswind V2", "pedals"),
        Device("WINWING F18 TAKEOFF PANEL", "panel")
    ];

    private static List<SetupDevice> Space() => [Device("VKBsim Gladiator NXT R", "stick", "right"), Device("VKBsim Gladiator NXT L", "stick", "left"), Device("T-Rudder", "pedals")];

    private static LayoutUnderstanding Read(string text) => LayoutPhrases.Read(text, Games, Planes);

    // ---- phrases ----

    [Fact]
    public void Phrases_ThrottleAndLeftHandStick_BothLeftHandDevicesTakePart()
    {
        LayoutUnderstanding read = Read("throttle and left-hand stick");
        LayoutPreference p = Assert.Single(read.Preferences);
        Assert.Equal("devices", p.Id);
        Assert.Equal("throttle+left-stick", p.Value);
        Assert.Empty(read.NotUnderstood);

        // Big pit: the MT-50 is the throttle, the WarBRD the left stick (space games).
        List<SetupDevice> big = Big();
        LayoutCompiled compiled = LayoutPhrases.Compile(read.Preferences, big);
        Assert.Equal(SetupRoles.Throttle, compiled.Roles[big.First(d => d.Model.StartsWith("LEFT VPC")).Device]);
        Assert.Equal(SetupRoles.LeftStick, compiled.Roles[big.First(d => d.Model.StartsWith("L-VPC")).Device]);
        Assert.Empty(compiled.Notes);

        // Small: no left-hand stick (says so); space: no throttle (says so).
        Assert.Contains(LayoutPhrases.Compile(read.Preferences, Small()).Notes, n => n.StartsWith("No left-hand stick found", StringComparison.Ordinal));
        Assert.Contains(LayoutPhrases.Compile(read.Preferences, Space()).Notes, n => n.StartsWith("No throttle found", StringComparison.Ordinal));
    }

    [Fact]
    public void Phrases_SpaceLeftStickTwistInsteadOfPedals_YawRuleForSpaceships()
    {
        LayoutUnderstanding read = Read("for space games use the left stick and its twist axis instead of pedals");
        Assert.Empty(read.NotUnderstood);
        LayoutPreference yaw = Assert.Single(read.Preferences, p => p.Id == "function-on");
        Assert.Equal(SetupClasses.Space, yaw.Class);
        Assert.Equal(SetupRoles.LeftStick, yaw.Role);
        Assert.Matches(yaw.Function, "space_yaw");
        Assert.Contains("twist", yaw.Controls);
        Assert.Contains(read.Preferences, p => p.Id == "devices" && p.Value == "left-stick-space");

        // Space setup: yaw on the left stick's twist (stronger than the previous control), up/down translation off it.
        LayoutCompiled compiled = LayoutPhrases.Compile(read.Preferences, Space());
        SetupRule rule = Assert.Single(compiled.Rules, r => r.Functions.Contains("space_yaw") && !r.Forbid);
        Assert.Equal(SetupRoles.LeftStick, rule.Role);
        Assert.True(rule.Bonus > 5);
        Assert.Contains(compiled.Rules, r => r.Functions == "^space_strafe_ver$" && r.Forbid && r.Controls.Contains("twist"));
        // Small: no left stick -> the note says so, the rule stays for any twist.
        LayoutCompiled small = LayoutPhrases.Compile(read.Preferences, Small());
        Assert.Contains(small.Notes, n => n.Contains("left-hand stick", StringComparison.Ordinal));
        Assert.Contains(small.Rules, r => r.Functions.Contains("space_yaw") && r.Role.Length == 0);
    }

    [Fact]
    public void Phrases_TrimOnTheHatCurvesSofterForHelis_RuleAndTuningWish()
    {
        LayoutUnderstanding read = Read("trim on the hat, curves softer for helis");
        Assert.Empty(read.NotUnderstood);
        Assert.Equal(2, read.Preferences.Count);
        LayoutPreference trim = read.Preferences[0];
        Assert.Equal("function-on", trim.Id);
        Assert.Matches(trim.Function, "trimleft");
        Assert.Contains("hat", trim.Controls);
        Assert.Equal("Every aircraft: trim on the hat.", trim.Describe());
        LayoutPreference curve = read.Preferences[1];
        Assert.Equal("curve", curve.Id);
        Assert.Equal(SetupClasses.Rotary, curve.Class);
        Assert.Equal("Helicopters: softer curves for pitch, roll (curvature +0.15).", curve.Describe());

        foreach (List<SetupDevice> devices in new[] { Small(), Medium(), Big(), Space() })
        {
            LayoutCompiled compiled = LayoutPhrases.Compile(read.Preferences, devices);
            LayoutTuningIntent tuning = Assert.Single(compiled.Tuning);
            Assert.Equal(0.15, tuning.CurveDelta, 3);
            Assert.Equal(SetupClasses.Rotary, tuning.Class);
            Assert.Contains(compiled.Rules, r => r.Functions == trim.Function && r.Controls == trim.Controls && r.Bonus > 5);
        }
    }

    [Fact]
    public void Phrases_OptionsDevicesFfbVJoyAndPriorities()
    {
        LayoutCompiled compiled = LayoutPhrases.Compile(Read("no modifiers. use a mode switch. like the real aircraft. keep my layout").Preferences, Small());
        Assert.Equal(0, compiled.Modifiers);
        Assert.Equal("on", compiled.PhaseLayers);
        Assert.Equal(1.0, compiled.Realism);
        Assert.True(compiled.KeepLayout);

        List<SetupDevice> medium = Medium();
        compiled = LayoutPhrases.Compile(Read("no pedals").Preferences, medium);
        Assert.Equal(SetupRoles.Unused, compiled.Roles[medium.First(d => d.Kind == "pedals").Device]);
        Assert.Contains(compiled.Rules, r => r.Functions.Contains("rudder") && r.Controls.Contains("twist"));

        compiled = LayoutPhrases.Compile(Read("less ffb shake for helis, stronger ffb in DCS, no ffb in IL-2").Preferences, Big());
        Assert.Contains(compiled.Ffb, f => f.Kind == "shake" && f.Class == SetupClasses.Rotary && f.Factor < 1);
        Assert.Contains(compiled.Ffb, f => f.Kind == "force" && f.Game == "DCS" && f.Factor > 1);
        Assert.Contains(compiled.Ffb, f => f.Kind == "off" && f.Game == "IL2Game");

        compiled = LayoutPhrases.Compile(Read("vjoy for Star Citizen, native for DCS").Preferences, Space());
        Assert.Contains(compiled.VJoy, v => v.Game == "StarCitizen" && v.Mode == "virtual");
        Assert.Contains(compiled.VJoy, v => v.Game == "DCS" && v.Mode == "native");

        compiled = LayoutPhrases.Compile(Read("the F-16 first").Preferences, Small());
        Assert.Equal("F-16C_50", Assert.Single(Assert.Single(compiled.Priorities)));

        LayoutUnderstanding nonsense = Read("make it pretty please");
        Assert.Empty(nonsense.Preferences);
        Assert.Equal("make it pretty please", Assert.Single(nonsense.NotUnderstood));
    }

    // The German test set (engine/tests/fixtures/phrases/layout-de.json): each German text gives exactly the preferences
    // of its English text, nothing is left not understood, and the expected preferences are there.
    [Fact]
    public void Phrases_German_GiveTheSamePreferencesAsEnglish()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        using JsonDocument set = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "engine", "tests", "fixtures", "phrases", "layout-de.json")));
        static string Signature(LayoutPreference p) =>
            string.Join("|", p.Id, p.Key, p.Value, p.Class, p.Game, p.Function, p.Controls, p.Role, p.Negative ? "not" : "yes");
        List<string> failures = [];
        int cases = 0;
        foreach (JsonElement item in set.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases++;
            string de = item.GetProperty("de").GetString()!;
            string en = item.GetProperty("en").GetString()!;
            LayoutUnderstanding german = Read(de);
            LayoutUnderstanding english = Read(en);
            List<string> got = german.Preferences.Select(Signature).ToList();
            List<string> want = english.Preferences.Select(Signature).ToList();
            if (german.NotUnderstood.Count > 0 || english.NotUnderstood.Count > 0)
                failures.Add(de + ": not understood: " + string.Join(" / ", german.NotUnderstood.Concat(english.NotUnderstood)));
            if (!got.SequenceEqual(want))
                failures.Add(de + ": " + string.Join(" ; ", got) + "  <>  " + en + ": " + string.Join(" ; ", want));
            foreach (JsonElement expected in item.GetProperty("expect").EnumerateArray())
            {
                string[] parts = expected.GetString()!.Split('=', 2);
                if (!german.Preferences.Any(p => p.Id == parts[0] && (parts.Length == 1 || p.Value == parts[1])))
                    failures.Add(de + ": missing " + expected.GetString());
            }
        }
        Assert.True(cases >= 15, "the German test set has " + cases + " cases");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // The required five, explicitly.
        Assert.Equal("throttle+left-stick", Assert.Single(Read("Gas und linker Stick").Preferences).Value);
        LayoutUnderstanding space = Read("bei Weltraumspielen linker Stick mit Twist statt Pedalen");
        LayoutPreference yaw = Assert.Single(space.Preferences, p => p.Id == "function-on");
        Assert.Equal(SetupClasses.Space, yaw.Class);
        Assert.Equal(SetupRoles.LeftStick, yaw.Role);
        Assert.Contains("twist", yaw.Controls);
        LayoutPreference trim = Assert.Single(Read("Trimmung auf den Hat").Preferences);
        Assert.Matches(trim.Function, "trimleft");
        Assert.Contains("hat", trim.Controls);
        LayoutPreference curve = Assert.Single(Read("weichere Kurven für Helis").Preferences);
        Assert.Equal(SetupClasses.Rotary, curve.Class);
        Assert.Equal("0.15", curve.Value);
        LayoutUnderstanding modes = Read("keine Modifier, nimm einen Moduswahlschalter");
        Assert.Contains(modes.Preferences, p => p.Id == "modifiers" && p.Value == "0");
        Assert.Contains(modes.Preferences, p => p.Id == "phase-layers" && p.Value == "on");
        Assert.Empty(modes.NotUnderstood);

        // English texts stay as they are (no German word is read into them).
        Assert.Equal(" throttle and left hand stick ", LayoutPhrases.Normalize("throttle and left-hand stick"));
    }

    [Fact]
    public void Phrases_NewerPreferenceReplacesTheSavedOne_AndNegativePlacements()
    {
        List<LayoutPreference> saved = Read("curves softer for helis, trim on the hat").Preferences;
        List<LayoutPreference> merged = LayoutPhrases.Merge(saved, Read("curves much softer for helis").Preferences);
        Assert.Equal(2, merged.Count);
        Assert.Equal("0.3", merged.First(p => p.Id == "curve").Value);

        LayoutPreference never = Assert.Single(Read("no countermeasures on the throttle hat").Preferences);
        Assert.True(never.Negative);
        Assert.Equal(SetupRoles.Throttle, never.Role);
        SetupRule rule = Assert.Single(LayoutPhrases.Compile([never], Big()).Rules);
        Assert.True(rule.Forbid);
        Assert.Equal(0, rule.Bonus);
    }

    // ---- through the engine ----

    private static string? RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "tests", "fixtures")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? EngineExe()
    {
        string? root = RepoRoot();
        string? exe = root == null ? null : Path.Combine(root, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return exe != null && File.Exists(exe) ? exe : null;
    }

    private sealed record Ask(JsonDocument Proposal, SetupPlan Plan, string Text);

    private static (int Exit, string Output) Engine(string exe, string devices, string data, params string[] args)
    {
        ProcessStartInfo info = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = data;
        info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_ENGINE_SERVER"] = "off";
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    private static Ask? AskEngine(string set, string text, string? profile = null, bool addMissing = true, string folderName = "")
    {
        string? exe = EngineExe();
        string? root = RepoRoot();
        if (exe == null || root == null)
            return null;
        string devices = set.EndsWith(".tsv", StringComparison.Ordinal) ? set : Path.Combine(root, "engine", "tests", "fixtures", "autoassign-matrix", set + ".tsv");
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-ai", folderName.Length > 0 ? folderName : Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(data);
        string proposal = Path.Combine(folder, "proposal.json");
        List<string> args = ["--confirm-corrections", "false", "--confirm-device-remap", "false", "--profile", profile ?? Path.Combine(folder, "profile.pr0file"),
            "arrange", "ask", text, "--llm", "off", "--proposal", proposal];
        if (profile == null)
            args.AddRange(["--games", "DCS,StarCitizen"]);
        if (addMissing)
            args.Add("--add-missing");
        (int exit, string output) = Engine(exe, devices, data, [.. args]);
        Assert.True(exit == 0, output);
        JsonDocument doc = JsonDocument.Parse(File.ReadAllText(proposal));
        SetupPlan plan = SetupPlanJson.Read(File.ReadAllText(doc.RootElement.GetProperty("PlanFile").GetString()!));
        return new Ask(doc, plan, output);
    }

    private static IEnumerable<JsonElement> Changes(Ask ask, string kind) =>
        ask.Proposal.RootElement.GetProperty("Changes").EnumerateArray().Where(c => c.GetProperty("Kind").GetString() == kind);

    private static Dictionary<string, SetupAircraft> Aircraft()
    {
        SetupCatalog.Load(Path.Combine(RepoRoot()!, "EngineOnly_Runtime", "DB", "Functions"), [], out _, out Dictionary<string, SetupAircraft> aircraft, out _);
        return aircraft;
    }

    [Fact]
    public void Engine_SmallSetup_TrimOnTheHat_NoModifiers_SofterCurvesOnlyForHelicopters()
    {
        Ask? ask = AskEngine("stick-throttle", "trim on the hat, curves softer for helis, no modifiers");
        if (ask == null)
            return;
        Assert.Empty(ask.Proposal.RootElement.GetProperty("NotUnderstood").EnumerateArray());
        Assert.Equal(3, ask.Proposal.RootElement.GetProperty("Preferences").GetArrayLength());
        // No modifier, not even a forced one: the user said so.
        Assert.DoesNotContain(ask.Plan.Modifiers, m => !m.Switch);
        // Every trim direction that got a place is on a hat.
        List<SetupPlanRelation> trim = ask.Plan.Placed.Where(r => r.Function is "trimleft" or "trimright" or "trimnoseup" or "trim_nosedown").ToList();
        Assert.NotEmpty(trim);
        Assert.All(trim, r => Assert.Contains(".hat.", r.Control));
        // Curves only for helicopters (their own DCS scope).
        Dictionary<string, SetupAircraft> aircraft = Aircraft();
        List<JsonElement> tuning = Changes(ask, "tuning").ToList();
        Assert.NotEmpty(tuning);
        foreach (JsonElement change in tuning)
        {
            string scope = change.GetProperty("Scope").GetString()!;
            Assert.StartsWith("plane:DCS:", scope);
            SetupAircraft plane = aircraft[scope.Substring("plane:DCS:".Length)];
            string cls = plane.VariantOf.Length > 0 ? aircraft[plane.VariantOf].Class : plane.Class;
            Assert.Equal(SetupClasses.Rotary, cls);
            Assert.Contains("cv=0.15", change.GetProperty("New").GetString());
        }
        // The plan keeps every rule (checked by the same checks as the combination matrix).
        SetupCheckReport report = SetupPlanChecks.Check(ask.Plan, LoadFunctions(), null, 2);
        Assert.True(report.Passed, SetupPlanChecks.ToText(report));

        // What the preview shows is in plain words: no input token, control id, scope key or tuning code.
        foreach (JsonElement change in ask.Proposal.RootElement.GetProperty("Changes").EnumerateArray())
        {
            string kind = change.GetProperty("Kind").GetString()!;
            string Shown(string text, string value) => change.GetProperty(text).GetString() is { Length: > 0 } t ? t : change.GetProperty(value).GetString()!;
            AssertReadable(Shown("OldText", "Old"));
            AssertReadable(Shown("NewText", "New"));
            if (kind != "bind")
                AssertReadable(Shown("ScopeText", "Scope"));
            AssertReadable(change.GetProperty("Reason").GetString()!);
        }
        foreach (SetupPlanRelation relation in ask.Plan.Placed)
            AssertReadable(relation.Reason);
        foreach (string note in ask.Plan.Notes)
            AssertReadable(note);
    }

    // No raw id in a text the app shows ("JOY_BTN_POV1_D", "stick.hat.tms.press", "plane:DCS:UH-1H", "cv=0.15").
    internal static void AssertReadable(string text)
    {
        Assert.DoesNotMatch(@"JOY_|\b[a-z]+\.[a-z_]+\.[a-z0-9_.]+|\bplane:|\bgame:|\b(inv|cv|dz|sx|sy)=", text);
    }

    [Fact]
    public void ReadableNames_InputsAndControls_InPlainWords()
    {
        Assert.Equal("Hat 1 down", JoyPro.DeviceReference.DeviceVocabulary.InputName("JOY_BTN_POV1_D"));
        Assert.Equal("Button 25", JoyPro.DeviceReference.DeviceVocabulary.InputName("JOY_BTN25"));
        Assert.Equal("X axis", JoyPro.DeviceReference.DeviceVocabulary.InputName("JOY_X"));
        Assert.Equal("Z rotation", JoyPro.DeviceReference.DeviceVocabulary.InputName("JOY_RZ"));
        Assert.Equal("Slider 1", JoyPro.DeviceReference.DeviceVocabulary.InputName("JOY_SLIDER1"));
        Assert.Equal("LCtrl", JoyPro.DeviceReference.DeviceVocabulary.InputName("LCtrl"));
        Assert.Equal("Thumb stick down", ReadableNames.Control("stick.ministick.down"));
        Assert.Equal("Base button 25", ReadableNames.Control("stick.base.button.in25"));
        Assert.Equal("TMS hat press", ReadableNames.Control("stick.hat.tms.press"));
        Assert.Equal("Hat 1 up", ReadableNames.Control("stick.hat.pov1.up"));
        Assert.Equal("Hat 2 left", ReadableNames.Control("panel.button.pov2left"));
        Assert.Equal("Left throttle", ReadableNames.Control("throttle.axis.left"));
        Assert.Equal("Stick roll", ReadableNames.Control("stick.axis.roll"));
        Assert.Equal("Thumb button 3", ReadableNames.Control("stick.button.thumb.3"));
        Assert.Equal("GEAR UP", ReadableNames.Control("panel.lever.gear.up", "GEAR UP"));
        Assert.Equal("Hat 1 down (TMS hat down)", ReadableNames.Where("JOY_BTN_POV1_D", "stick.hat.tms.down"));

        // The Setup wizard's review rows.
        SetupReviewRow row = new(new SetupPlanRelation { Name = "TMS Down", Input = "JOY_BTN_POV1_D", Control = "stick.hat.tms.down", Modifier = "JoyPro Shift" });
        Assert.Equal("Hat 1 down + JoyPro Shift", row.Input);
        Assert.Equal("TMS hat down", row.Control);
        // The Auto Assign preview (engine JSON -> plain words).
        string preview = MainWindowViewModel.FormatAutoAssignResult(
            "{\"DryRun\":true,\"KeptExisting\":1,\"StyleKept\":0,\"Devices\":[{\"Device\":\"VKB Stick {00000000-0000-0000-0000-000000000001}\",\"Connected\":true,\"Model\":\"VKB Gladiator\",\"Kind\":\"stick\",\"Generic\":false,\"Note\":\"\",\"AlternativeTo\":\"\",\"GripChoices\":[]}]," +
            "\"Assignments\":[{\"Relation\":\"TMS Down\",\"Device\":\"VKB Stick {00000000-0000-0000-0000-000000000001}\",\"Input\":\"JOY_BTN_POV1_D\",\"Modifier\":\"\",\"Control\":\"stick.hat.tms.down\",\"Where\":\"\"}]," +
            "\"Unassigned\":[\"Gear\"],\"ModifiersUsed\":[],\"Explanations\":[],\"Warnings\":[],\"Errors\":[]}");
        Assert.Contains("TMS Down => VKB Stick Hat 1 down (TMS hat down)", preview);
        Assert.Contains("Gear", preview);
        AssertReadable(preview);
    }

    // A printed label names a function only with its whole meaning: "LDG GEAR WARN SILENCE" silences the gear warning
    // horn and is no place for the gear (WARN, SILENCE, HORN, TEST, LIGHT, BRT / DIM, ALT, EMERG must be the function's own).
    [Fact]
    public void Labels_ModifierWordsMustBeTheFunctionsOwn()
    {
        string[] gear = ["GEAR", "LDG GEAR", "LANDING GEAR"];
        Assert.True(SetupPhases.LabelNames(gear, "gear_toggle", "LDG GEAR"));
        Assert.False(SetupPhases.LabelNames(gear, "gear_toggle", "LDG GEAR WARN SILENCE"));
        Assert.False(SetupPhases.LabelNames(gear, "gearin", "GEAR LT TEST"));
        Assert.True(SetupPhases.LabelNames(["JETT", "JETTISON", "EMER JETT"], "jettison_weapons", "EMERG STORES JETT"));
        Assert.False(SetupPhases.LabelNames(["JETT STORES", "STORES"], "jettisonstores", "EMERG STORES JETT"));
        Assert.False(SetupPhases.LabelNames(["RDR", "RADAR"], "radar_on", "ALT RDR"));
        Assert.False(SetupPhases.LabelNames(["STROBE"], "lights_ext_collision_on", "STROBE BRT"));
        Assert.True(SetupPhases.LabelNames(["HORN SILENCE", "WARN SILENCE"], "gear_horn_silence", "LDG GEAR WARN SILENCE"));

        // Through the real engine on the author's pit (Warthog throttle): no gear function on the warning-silence button.
        Ask? ask = AskEngine("pit-panels-mfds", "trim on the hat");
        if (ask == null)
            return;
        // No gear function (the label contradicts it) and no time-critical / weapon function on that button; other
        // functions only once the unlabelled inputs are used up, with the reason saying so.
        foreach (SetupPlanRelation r in ask.Plan.Placed.Where(r => r.Label.Contains("WARN SILENCE", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain("gear", r.Function, StringComparison.OrdinalIgnoreCase);
            Assert.False(SetupPhases.IsCriticalFunction(r.Function, string.Empty, r.Category), r.Name + " on " + r.Label);
            Assert.StartsWith("on a labelled switch (LDG GEAR WARN SILENCE): no free unlabelled input left", r.Reason);
        }
        Assert.True(SetupPhases.IsCockpitLabel("LDG GEAR WARN SILENCE"));
        Assert.False(SetupPhases.IsCockpitLabel("left base 1"));
        Assert.False(SetupPhases.IsCockpitLabel("MODE 2"));
        SetupCheckReport report = SetupPlanChecks.Check(ask.Plan, LoadFunctions(), null, 2);
        Assert.True(report.Passed, SetupPlanChecks.ToText(report));
    }

    private static void AssertText(string expected, string actual) => Assert.True(expected == actual, "expected: " + expected + Environment.NewLine + "actual:   " + actual);

    // In the German app every engine text of a real plan and proposal (reasons, notes, rules, roles, Lay out changes) is
    // German: nothing stays English (Services/EngineText.cs, Models/EngineTexts.cs).
    [Fact]
    public void German_EngineTexts_AreTranslated()
    {
        Ask? ask = AskEngine("mode-switch-small", "trim on the hat, softer curves for helis");
        if (ask == null)
            return;
        try
        {
            Loc.SetLanguage("de");
            List<string> english = [];
            List<string> dump = [];
            void Check(string text)
            {
                string german = EngineText.Tr(text);
                dump.Add(text + "  =>  " + german);
                if (text.Length > 0 && german == text)
                    english.Add(text);
            }
            foreach (SetupPlanRelation r in ask.Plan.Relations.Where(r => r.Reason.Length > 0))
                Check(r.Reason);
            foreach (SetupNotPlaced n in ask.Plan.NotPlaced)
                Check(n.Reason);
            foreach (string note in ask.Plan.Notes.Concat(ask.Plan.Warnings).Concat(ask.Plan.Rules))
                Check(note);
            foreach (SetupPlanModifier m in ask.Plan.Modifiers)
                Check(m.Reason);
            foreach (SetupDevice d in ask.Plan.Devices.Where(d => d.Reason.Length > 0))
                Check(d.Reason);
            foreach (string role in SetupRoles.All)
                Check(SetupRoles.Title(role));
            foreach (JsonElement change in ask.Proposal.RootElement.GetProperty("Changes").EnumerateArray())
                Check(change.GetProperty("Reason").GetString()!);
            foreach (string note in ask.Proposal.RootElement.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()!))
                Check(note);
            // JOYPRO_GERMAN_DUMP=<file>: every text with its German, to read through.
            if (Environment.GetEnvironmentVariable("JOYPRO_GERMAN_DUMP") is { Length: > 0 } dumpFile)
                File.WriteAllLines(dumpFile, dump.Distinct());
            Assert.True(english.Count == 0, "English in the German app:" + Environment.NewLine + string.Join(Environment.NewLine, english.Distinct().Take(40)));
            Assert.Equal("Hat 1 unten (TMS-Hat unten)", EngineText.Tr("Hat 1 down (TMS hat down)"));
            Assert.Equal("Basis-Kippschalter 2 oben", ReadableNames.Control("throttle.base.toggle.2.up"));
            Assert.Equal("Taste 3 (E (right side of right lever))", EngineText.Tr("Button 3 (E (right side of right lever))"));
            AssertText("357 neue Relationen für 83 Flugzeuge / Spiele; 226 Funktionen platziert, 131 nicht platziert, 0 Ihrer Belegungen behalten.",
                EngineText.Tr("357 new relations for 83 aircraft / games; 226 functions placed, 131 not placed, 0 of your binds kept."));
            AssertText("Phasen-Ebenen auf dem Moduswahlschalter X56 (Basis-Kippschalter 1): unten = Luft-Boden; die Mitte / die andere Stellung ist die normale Ebene. Flug-, Start- und Landefunktionen bleiben auf der normalen Ebene.",
                EngineText.Tr("Phase layers on the mode switch X56 (Base toggle 1): down = air to ground; the centre / the other position is the normal layer. Flight, take-off and landing functions stay on the normal layer."));
            Assert.Equal("Taste 5 (Pinky-Hebel)", EngineText.Tr("Button 5 (Pinky lever)"));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    // The same words in German give the same preferences and the same layout through the real engine.
    [Fact]
    public void Engine_German_SameLayoutAsEnglish()
    {
        Ask? german = AskEngine("stick-throttle", "Trimmung auf den Hat, weichere Kurven für Helis, keine Modifier");
        Ask? english = AskEngine("stick-throttle", "trim on the hat, softer curves for helis, no modifiers");
        if (german == null || english == null)
            return;
        Assert.Empty(german.Proposal.RootElement.GetProperty("NotUnderstood").EnumerateArray());
        static List<string> Keys(Ask ask) => ask.Proposal.RootElement.GetProperty("Preferences").EnumerateArray().Select(p => p.GetProperty("Key").GetString()!).ToList();
        Assert.Equal(Keys(english), Keys(german));
        static List<string> Binds(Ask ask) => ask.Plan.Placed.Select(r => r.Name + "|" + r.Device + "|" + r.Input + "|" + r.Modifier).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(Binds(english), Binds(german));
    }

    [Fact]
    public void Engine_MediumSetup_NoPedals_RudderOnTheTwist()
    {
        Ask? ask = AskEngine("stick-throttle-pedals-twist", "no pedals");
        if (ask == null)
            return;
        Assert.Equal(SetupRoles.Unused, ask.Plan.Devices.First(d => d.Kind == "pedals").Role);
        SetupPlanRelation rudder = Assert.Single(ask.Plan.Placed, r => r.Function == "rudder");
        Assert.Equal("stick.axis.twist", rudder.Control);
        Assert.DoesNotContain(ask.Plan.Placed, r => r.Device.StartsWith("T-Rudder", StringComparison.Ordinal));

        // The medium set without the wish: rudder on the pedals.
        Ask? plain = AskEngine("stick-throttle-pedals-twist", "trim on the hat");
        Assert.StartsWith("pedals.", Assert.Single(plain!.Plan.Placed, r => r.Function == "rudder").Control);
    }

    [Fact]
    public void Engine_SpaceSetup_YawOnTheLeftStickTwist_TranslationOffTheTwist()
    {
        Ask? ask = AskEngine("space-dual-sticks", "for space games use the left stick and its twist axis instead of pedals");
        if (ask == null)
            return;
        string left = ask.Plan.Devices.First(d => d.Role == SetupRoles.LeftStick).Device;
        SetupPlanRelation yaw = Assert.Single(ask.Plan.Placed, r => r.Function == "space_yaw");
        Assert.Equal(left, yaw.Device);
        Assert.Equal("stick.axis.twist", yaw.Control);
        SetupPlanRelation vertical = Assert.Single(ask.Plan.Placed, r => r.Function == "space_strafe_ver");
        Assert.NotEqual("stick.axis.twist", vertical.Control);
        Assert.DoesNotContain(ask.Plan.Placed, r => r.Function == "space_yaw" && r.Device.StartsWith("T-Rudder", StringComparison.Ordinal));
        // Throttle and left-hand stick on a set without a throttle: the note says so.
        Ask? both = AskEngine("space-dual-sticks", "throttle and left-hand stick");
        Assert.Contains(both!.Proposal.RootElement.GetProperty("Notes").EnumerateArray(), n => n.GetString()!.StartsWith("No throttle found", StringComparison.Ordinal));
    }

    [Fact]
    public void Engine_SavedPreferences_AreUsedAgain_AndGiveTheSameLayout()
    {
        string? exe = EngineExe();
        if (exe == null)
            return;
        string folderName = "saved-" + Guid.NewGuid().ToString("N");
        Ask first = AskEngine("stick-throttle", "trim on the hat, curves softer for helis", folderName: folderName)!;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-ai", folderName);
        string devices = Path.Combine(RepoRoot()!, "engine", "tests", "fixtures", "autoassign-matrix", "stick-throttle.tsv");
        (int exit, string output) = Engine(exe, devices, Path.Combine(folder, "data"), "--profile", Path.Combine(folder, "profile.pr0file"), "arrange", "apply", "--proposal", Path.Combine(folder, "proposal.json"), "--text");
        Assert.True(exit == 0, output);
        Assert.Contains("Axis Curves Set: ", output);
        (exit, output) = Engine(exe, devices, Path.Combine(folder, "data"), "--profile", Path.Combine(folder, "profile.pr0file"), "arrange", "prefs", "--text");
        Assert.Contains("Saved preferences: 2", output);
        // Asked again without words: the kept preferences, nothing left to change (no second curve step).
        (exit, output) = Engine(exe, devices, Path.Combine(folder, "data"), "--profile", Path.Combine(folder, "profile.pr0file"), "arrange", "ask", "--llm", "off", "--games", "DCS,StarCitizen", "--text");
        Assert.True(exit == 0, output);
        Assert.Contains("[saved] Every aircraft: trim on the hat.", output);
        Assert.Contains("Changes: 0 binds, 0 splits, 0 axis curves", output);
        Assert.True(first.Plan.Placed.Any());
    }

    // The author's pit on a copy of a real profile (set JOYPRO_LAYOUT_BIG_PROFILE=<profile.pr0file>; the test copies it).
    [Fact]
    public void Engine_BigPit_LeftHandAndSpaceYaw_OnlyWhatTheWordsConcernMoves()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_BIG_PROFILE");
        string? root = RepoRoot();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || root == null || EngineExe() == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-ai", "big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "big.pr0file");
        File.Copy(source, copy);
        Ask ask = AskEngine(Path.Combine(root, "engine", "tests", "fixtures", "setupwizard", "devices.tsv"),
            "throttle and left-hand stick. for space games use the left stick and its twist axis instead of pedals", copy, addMissing: false)!;
        Assert.Equal(SetupRoles.Throttle, ask.Plan.Devices.First(d => d.Device.StartsWith("LEFT VPC Throttle", StringComparison.Ordinal)).Role);
        Assert.Equal(SetupRoles.LeftStick, ask.Plan.Devices.First(d => d.Device.StartsWith("L-VPC Stick WarBRD", StringComparison.Ordinal)).Role);
        // The space yaw moves to the WarBRD's twist; every bind that moves is a space ship's.
        JsonElement yaw = Changes(ask, "bind").First(c => c.GetProperty("Title").GetString()!.Contains("Yaw", StringComparison.OrdinalIgnoreCase) && c.GetProperty("New").GetString()!.Contains("stick.axis.twist"));
        Assert.StartsWith("L-VPC Stick WarBRD JOY_Z", yaw.GetProperty("New").GetString());
        Dictionary<string, SetupAircraft> aircraft = Aircraft();
        foreach (JsonElement moved in Changes(ask, "bind").Where(c => c.GetProperty("Old").GetString()!.Length > 0))
        {
            SetupPlanRelation relation = ask.Plan.Relations.First(r => r.Name == moved.GetProperty("Title").GetString());
            Assert.All(relation.Aircraft, a => Assert.Equal(SetupClasses.Space, aircraft.TryGetValue(a, out SetupAircraft? p) ? p.Class : SetupClasses.Space));
        }
    }

    private static Dictionary<string, SetupFunction> LoadFunctions()
    {
        SetupCatalog.Load(Path.Combine(RepoRoot()!, "EngineOnly_Runtime", "DB", "Functions"), [], out Dictionary<string, SetupFunction> functions, out _, out _);
        return functions;
    }

    // ---- the Binds page panel ----

    internal const string ProposalJson = """
    {"Format":"joypro-layout-proposal","Text":"throttle and left-hand stick, curves softer for helis","Method":"JoyPro's rules",
     "Preferences":[
      {"Id":"devices","Key":"devices|left","Text":"throttle and left-hand stick","Template":"Left hand: the throttle in aircraft and helicopters, the left-hand stick in space games.","Args":[],"Class":"","Game":"","Source":"rules"},
      {"Id":"curve","Key":"curve|^(pitch|roll)$|rotary|*","Text":"curves softer for helis","Template":"{0}: softer curves for {1} (curvature +{2}).","Args":["Helicopters","pitch, roll","0.15"],"Class":"rotary","Game":"","Source":"rules"},
      {"Id":"function-on","Key":"function-on|^trim|*|*","Text":"trim on the hat","Template":"{0}: {1} on the {2}.","Args":["Every aircraft","trim","hat"],"Class":"","Game":"","Source":"rules"}],
     "Fresh":["devices|left","curve|^(pitch|roll)$|rotary|*"],
     "NotUnderstood":["make it pretty"],
     "Games":["DCS","StarCitizen"],
     "PlanFile":"C:\\nowhere\\plan.json",
     "Changes":[
      {"Id":"bind:Space_Yaw","Kind":"bind","Title":"Space_Yaw","Scope":"","Old":"VKB-Sim Gunfighter Z axis","New":"L-VPC Stick WarBRD Z axis (Twist)","Reason":"your preference","Preference":"function-on","NewRelation":false},
      {"Id":"bind:Gear TOGGLE","Kind":"bind","Title":"Gear TOGGLE","Scope":"","Old":"","New":"WINWING F18 TAKEOFF PANEL Button 2 (LDG GEAR)","Reason":"the panel label \"LDG GEAR\"","Preference":"","NewRelation":false},
      {"Id":"bind:TrimLeft","Kind":"bind","Title":"TrimLeft","Scope":"","Old":"","New":"VKB-Sim Gunfighter Hat 1 left (Trim hat left)","Reason":"together with its group on one hat; your preference","Preference":"function-on","NewRelation":false},
      {"Id":"tuning:Pitch|plane:DCS:UH-1H","Kind":"tuning","Title":"Pitch","Scope":"plane:DCS:UH-1H","Old":"inv=0;sl=0;dz=0;sx=1;sy=1;cv=0","New":"inv=0;sl=0;dz=0;sx=1;sy=1;cv=0.15","OldText":"linear","NewText":"curve +0.15","ScopeText":"UH-1H (DCS)","Reason":"","Preference":"curve","NewRelation":false},
      {"Id":"vjoy:StarCitizen","Kind":"vjoy","Title":"Star Citizen","Scope":"StarCitizen","Old":"native","New":"virtual","OldText":"your devices directly","NewText":"the virtual joystick (vJoy)","ScopeText":"Star Citizen","Reason":"vJoy","Preference":"vjoy","NewRelation":false}],
     "Notes":["No modifier needed: every function fits without one."],"Warnings":[]}
    """;

    internal sealed class LayoutFakeEngine : IEngineCliClient
    {
        public List<string[]> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output = "{}";
            if (Has(arguments, "arrange", "ask"))
            {
                // JOYPRO_LAYOUT_AI_PROPOSAL: a proposal the real engine made (the render shows real texts).
                string? real = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_PROPOSAL");
                output = real != null && File.Exists(real) ? File.ReadAllText(real) : ProposalJson;
            }
            else if (Has(arguments, "arrange", "prefs"))
                output = "[{\"Id\":\"function-on\",\"Key\":\"function-on|^trim|*|*\",\"Text\":\"trim on the hat\",\"Template\":\"{0}: {1} on the {2}.\",\"Args\":[\"Every aircraft\",\"trim\",\"hat\"],\"Class\":\"\",\"Game\":\"\",\"Source\":\"rules\"}]";
            else if (Has(arguments, "arrange", "apply"))
                output = "Lay out with AI: Apply";
            else if (Has(arguments, "relations", "list"))
                output = "{\"Relations\":[]}";
            else if (Has(arguments, "binds", "list"))
                output = "{\"Binds\":[]}";
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, Arguments = arguments.ToArray() });
        }

        private static bool Has(IReadOnlyList<string> args, string area, string action)
        {
            int i = args.ToList().FindIndex(a => a == area);
            return i >= 0 && i + 1 < args.Count && args[i + 1] == action;
        }
    }

    [Fact]
    public void ViewModel_PreviewShowsWhatJoyProUnderstood_UntickedChangesAreSkipped()
    {
        LayoutFakeEngine engine = new();
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = "engine.exe", ProfilePath = Path.Combine(Path.GetTempPath(), "layout-ai-vm.pr0file") };
        vm.ToggleLayoutPanelCommand.Execute(null);
        Assert.True(vm.IsLayoutPanelOpen);
        Assert.True(vm.IsBindsStage);
        vm.UseLayoutExampleCommand.Execute("throttle and left-hand stick");
        vm.UseLayoutExampleCommand.Execute("trim on the hat, curves softer for helis");
        Assert.Equal("throttle and left-hand stick, trim on the hat, curves softer for helis", vm.LayoutRequest);
        vm.LlmEnabled = false;
        vm.AskLayoutCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        string[] ask = engine.Calls.Last(c => c.Contains("ask"));
        Assert.Contains("--llm", ask);
        Assert.Equal("off", ask[Array.IndexOf(ask, "--llm") + 1]);
        Assert.Contains("--proposal", ask);

        Assert.Equal(3, vm.LayoutPreferences.Count);
        Assert.Equal("Helicopters: softer curves for pitch, roll (curvature +0.15).", vm.LayoutPreferences[1].Text);
        Assert.True(vm.LayoutPreferences[0].IsNew);
        Assert.False(vm.LayoutPreferences[2].IsNew);
        Assert.Equal("make it pretty", Assert.Single(vm.LayoutNotUnderstood));
        Assert.Equal(["bind-moved", "bind-new", "tuning", "vjoy"], vm.LayoutChangeGroups.Select(g => g.Kind).ToArray());
        Assert.True(vm.CanApplyLayout);
        // The preview shows the plain-word texts, not apply's values (tuning code, scope key).
        LayoutChangeItem curve = vm.LayoutChangeGroups.First(g => g.Kind == "tuning").Changes[0];
        Assert.Equal("curve +0.15", curve.After);
        Assert.Equal("UH-1H (DCS)", curve.Scope);
        foreach (LayoutChangeItem shown in vm.LayoutChangeGroups.SelectMany(g => g.Changes))
        {
            AssertReadable(shown.Before);
            AssertReadable(shown.After);
            AssertReadable(shown.Scope);
        }

        // Untick the vJoy change and the new bind; Apply passes them as --skip.
        vm.LayoutChangeGroups.First(g => g.Kind == "vjoy").AllSelected = false;
        vm.LayoutChangeGroups.First(g => g.Kind == "bind-new").Changes[0].IsSelected = false;
        Assert.False(vm.LayoutChangeGroups.First(g => g.Kind == "vjoy").AllSelected);
        vm.ApplyLayoutCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        string[] apply = engine.Calls.Last(c => c.Contains("apply"));
        Assert.Contains("vjoy:StarCitizen", apply);
        Assert.Contains("bind:Gear TOGGLE", apply);
        Assert.DoesNotContain("bind:Space_Yaw", apply);
        Assert.True(vm.LayoutApplied);
        Assert.False(vm.CanApplyLayout);
    }

    [Fact]
    public void ViewModel_ForgetSavedPreference_AsksTheEngine()
    {
        LayoutFakeEngine engine = new();
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = "engine.exe", ProfilePath = Path.Combine(Path.GetTempPath(), "layout-ai-vm.pr0file") };
        vm.ShowLayoutProposal(ProposalJson);
        LayoutPreferenceItem saved = vm.LayoutPreferences.First(p => !p.IsNew);
        vm.RemoveLayoutPreferenceCommand.ExecuteAsync(saved).GetAwaiter().GetResult();
        Assert.Contains(engine.Calls, c => c.Contains("prefs") && c.Contains("remove") && c.Contains("function-on|^trim|*|*"));
        Assert.DoesNotContain(vm.LayoutPreferences, p => p.Key == saved.Key);
        Assert.False(vm.HasLayoutPreview);
    }

    [Fact]
    public void Settings_AutoAssignWizardAndLayoutChoices_SurviveARestart()
    {
        string settingsFile = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.Delete(settingsFile);
            MainWindowViewModel first = new(new LayoutFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            first.AutoAssignPhaseLayers = PhaseLayerChoice.Always;
            first.AutoAssignMaxModifiers = 1;
            first.AutoAssignOverwrite = true;
            first.LayoutAddMissing = true;
            first.LayoutRequest = "trim on the hat";
            SetupWizardViewModel wizard = first.CreateSetupWizard();
            wizard.PhaseLayers = PhaseLayerChoice.Off;
            wizard.Modifiers = 2;
            wizard.Realism = 80;

            MainWindowViewModel second = new(new LayoutFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(PhaseLayerChoice.Always, second.AutoAssignPhaseLayers);
            Assert.Equal(1, second.AutoAssignMaxModifiers);
            Assert.True(second.AutoAssignOverwrite);
            Assert.True(second.LayoutAddMissing);
            Assert.Equal("trim on the hat", second.LayoutRequest);
            SetupWizardViewModel again = second.CreateSetupWizard();
            Assert.Equal(PhaseLayerChoice.Off, again.PhaseLayers);
            Assert.Equal(2, again.Modifiers);
            Assert.Equal(80, again.Realism);
        }
        finally
        {
            if (File.Exists(settingsFile))
                File.Delete(settingsFile);
        }
    }

    // ---- offscreen pictures ----

    // set JOYPRO_LAYOUT_AI_OUT=<folder> & dotnet test gui/JoyPro.Gui.Tests --filter LayoutWithAiTests.RenderLayoutWithAi
    // Real engine output instead of the built-in sample: JOYPRO_LAYOUT_AI_PROPOSAL (arrange ask --proposal),
    // JOYPRO_LAYOUT_AI_AUTOASSIGN (relations auto-assign --dry-run JSON), JOYPRO_LAYOUT_AI_PLAN (setup plan --out-plan).
    [Fact]
    public void RenderLayoutWithAi()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        // JOYPRO_LAYOUT_AI_LANG=de: the pictures in that language (file names end in -de).
        string lang = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_LANG") ?? "en";
        string suffix = lang == "en" ? string.Empty : "-" + lang;
        AvaloniaRenderThread.Run(() =>
        {
            Loc.SetLanguage(lang);
            try
            {
            foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
            {
                Application.Current!.RequestedThemeVariant = variant;
                MainWindowViewModel vm = new(new LayoutFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = "engine.exe", ProfilePath = Path.Combine(Path.GetTempPath(), "layout-ai-render.pr0file") };
                vm.CurrentStage = UiStage.Binds;
                vm.IsLayoutPanelOpen = true;
                vm.LayoutRequest = "Gas und linker Stick, Trimmung auf den Hat, weichere Kurven für Helis";
                string? realProposal = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_PROPOSAL");
                vm.ShowLayoutProposal(realProposal != null && File.Exists(realProposal) ? File.ReadAllText(realProposal) : ProposalJson);
                foreach (LayoutChangeGroupItem group in vm.LayoutChangeGroups)
                    group.IsExpanded = true;
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    Snap(new MainWindow { DataContext = vm, Width = w, Height = h, RequestedThemeVariant = variant }, w, h, Path.Combine(outDir, "layout-with-ai-" + w + "-" + theme + suffix + ".png"));

                // Auto Assign with the mode switch choice.
                vm.IsLayoutPanelOpen = false;
                vm.IsAutoAssignPanelOpen = true;
                vm.AutoAssignPhaseLayers = PhaseLayerChoice.Always;
                string? realAssign = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_AUTOASSIGN");
                if (realAssign != null && File.Exists(realAssign))
                    vm.AutoAssignPreview = MainWindowViewModel.FormatAutoAssignResult(MainWindowViewModel.ExtractJsonPayload(File.ReadAllText(realAssign)));
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                    Snap(new MainWindow { DataContext = vm, Width = w, Height = h, RequestedThemeVariant = variant }, w, h, Path.Combine(outDir, "auto-assign-mode-switch-" + w + "-" + theme + suffix + ".png"));

                // The Setup wizard's priorities step with the mode switch.
                SetupWizardViewModel wizard = vm.CreateSetupWizard();
                wizard.CurrentStep = 2;
                wizard.PhaseLayers = PhaseLayerChoice.Automatic;
                AvaloniaRenderThread.Pump();
                foreach ((int w, int h) in new[] { (1280, 800), (2267, 1250) })
                    Snap(new SetupWizardWindow { DataContext = wizard, Width = w, Height = h, RequestedThemeVariant = variant }, w, h, Path.Combine(outDir, "setup-wizard-mode-switch-" + w + "-" + theme + suffix + ".png"));

                // The Setup wizard's plan and review of a real plan: inputs and controls in plain words.
                string? realPlan = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_AI_PLAN");
                if (realPlan != null && File.Exists(realPlan))
                {
                    wizard.LoadPlan(SetupPlanJson.Read(File.ReadAllText(realPlan)));
                    foreach ((int step, string name) in new[] { (3, "plan"), (4, "review") })
                    {
                        wizard.CurrentStep = step;
                        if (step == 4)
                        {
                            // The review of the plan's throttle (the wizard builds this when Next goes to the review).
                            wizard.ReviewDevices.Clear();
                            foreach (SetupDevice d in wizard.Plan!.Devices.Where(d => SetupRoles.Binds(d.Role)))
                                wizard.ReviewDevices.Add(new SetupRoleOption(d.Device, SetupRoles.Short(d) + " (" + SetupRoles.Title(d.Role) + ")"));
                            wizard.SelectedReviewDevice = wizard.ReviewDevices.LastOrDefault();
                            wizard.RefreshReviewRows();
                            Assert.NotEmpty(wizard.ReviewRows);
                        }
                        AvaloniaRenderThread.Pump();
                        foreach ((int w, int h) in new[] { (1280, 800), (2267, 1250) })
                            Snap(new SetupWizardWindow { DataContext = wizard, Width = w, Height = h, RequestedThemeVariant = variant }, w, h, Path.Combine(outDir, "setup-wizard-" + name + "-" + w + "-" + theme + suffix + ".png"));
                    }
                    foreach (SetupReviewRow row in wizard.ReviewRows)
                    {
                        AssertReadable(row.Input);
                        AssertReadable(row.Control);
                    }
                }
            }
            }
            finally
            {
                Loc.SetLanguage("en");
            }
        });
    }

    private static void Snap(Avalonia.Controls.Window window, int width, int height, string file)
    {
        using HiddenWindow hidden = HiddenWindow.Create(window, new Size(width, height));
        hidden.Settle();
        using RenderTargetBitmap bitmap = hidden.Render();
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }
}
