using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Setup wizard's planner (engine\SetupWizard, linked into the GUI): device roles, the collective / left-stick rules,
// the modifier budget, the priority weighting, the realism slider with a fake cockpit database entry, the plan file and
// the review's edits. Fake devices and a small fake knowledge map; no engine, no files.
public sealed class SetupWizardPlannerTests
{
    // ---- fake data ----

    private const string Stick = "VKB Stick {00000000-0000-0000-0000-000000000001}";
    private const string Throttle = "VPC Throttle {00000000-0000-0000-0000-000000000002}";
    private const string LeftStick = "L-VPC Stick {00000000-0000-0000-0000-000000000003}";
    private const string Collective = "VPC Collective {00000000-0000-0000-0000-000000000004}";
    private const string Pedals = "MFG Crosswind {00000000-0000-0000-0000-000000000005}";
    private const string Panel = "WinWing Panel {00000000-0000-0000-0000-000000000006}";

    private static SetupDevice Device(string name, string kind, string hand = "none", int buttons = 20) =>
        new() { Device = name, Model = name.Split('{')[0].Trim(), Kind = kind, Hand = hand, Buttons = buttons, Connected = true };

    private static SetupInput Axis(string device, string input, string control) => new() { Device = device, Input = input, Control = control, IsAxis = true, Type = "analog" };

    private static SetupInput Button(string device, int number, string control, string label = "", string type = "momentary") =>
        new() { Device = device, Input = "JOY_BTN" + number, Control = control, Label = label, Type = type };

    private static SetupFunction Function(string id, string name, string side, string prefs, string frequency = "frequent", bool axis = false, string group = "", string pair = "", string labels = "")
    {
        string[] pairParts = pair.Split(':');
        return new SetupFunction
        {
            Id = id, Name = name, Side = side, IsAxis = axis, Frequency = frequency, Group = group,
            Prefs = prefs.Split(',').Where(p => p.Length > 0).ToList(),
            PairGroup = pairParts.Length == 2 ? pairParts[0] : string.Empty, PairRole = pairParts.Length == 2 ? pairParts[1] : string.Empty,
            Labels = labels.Split(';').Where(l => l.Length > 0).ToList()
        };
    }

    private static SetupRequest Request(List<SetupDevice> devices, List<SetupInput> inputs, params (string Function, string Aircraft)[] commands)
    {
        SetupRequest request = new() { Devices = devices, Inputs = inputs };
        foreach (SetupFunction function in new[]
                 {
                     Function("pitch", "Pitch", "stick", "stick.axis.pitch,yoke.axis.pitch", "constant", axis: true),
                     Function("roll", "Roll", "stick", "stick.axis.roll,yoke.axis.roll", "constant", axis: true),
                     Function("rudder", "Rudder", "pedals", "pedals.axis.rudder,stick.axis.twist", "constant", axis: true),
                     Function("throttleleft", "ThrottleLeft", "throttle", "throttle.axis.left,throttle.axis.main,quadrant.axis.lever.1,collective.axis.main", "constant", axis: true),
                     Function("throttleleftnonab", "ThrottleLeftNonAB", "collective", "collective.axis.main,throttle.axis.left,throttle.axis.main,quadrant.axis.lever.1", "constant", axis: true),
                     Function("space_strafe_hor", "SPACE_STRAFE_HOR", "throttle", "throttle.axis.ministick.x,stick.axis.ministick.x,stick.axis.ministick2.x", axis: true, group: "strafe_axes:x"),
                     Function("space_strafe_ver", "SPACE_STRAFE_VER", "throttle", "throttle.axis.ministick.y,stick.axis.ministick.y,stick.axis.ministick2.y", axis: true, group: "strafe_axes:y"),
                     Function("space_strafe_fwdaft", "SPACE_STRAFE_FWDAFT", "throttle", "throttle.axis.main,throttle.axis.right,throttle.axis.left,quadrant.axis.lever.1", "occasional", axis: true),
                     Function("trigger1ststage", "Trigger1stStage", "stick", "stick.trigger.stage1"),
                     Function("cms_fwd", "CMS FWD", "stick", "stick.hat.cms.up,stick.hat.*.up", group: "cms:up"),
                     Function("cms_aft", "CMS AFT", "stick", "stick.hat.cms.down,stick.hat.*.down", group: "cms:down"),
                     Function("radar_lock", "Radar Lock", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("radar_unlock", "Radar Unlock", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("weaponselector1", "WeaponSelector1", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("weaponselector2", "WeaponSelector2", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("weaponselector3", "WeaponSelector3", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("weaponselector4", "WeaponSelector4", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("select_gun", "Select Gun", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("select_heater", "Select Heater", "stick", "stick.button.thumb.*,stick.button.*"),
                     Function("gearin", "GearIn", "panel", "panel.lever.gear.up,panel.toggle.*.up,panel.button.*", pair: "gear:in", labels: "GEAR;LDG GEAR"),
                     Function("gearout", "GearOut", "panel", "panel.lever.gear.down,panel.toggle.*.down,panel.button.*", pair: "gear:out", labels: "GEAR;LDG GEAR"),
                     Function("antiskidon", "AntiSkidOn", "panel", "panel.toggle.*.up,panel.button.*", "occasional", pair: "antiskid:on", labels: "ANTI SKID"),
                     Function("antiskidoff", "AntiSkidOff", "panel", "panel.toggle.*.down,panel.button.*", "occasional", pair: "antiskid:off", labels: "ANTI SKID"),
                     Function("zoom", "Zoom", "throttle", "throttle.axis.rotary.1,throttle.axis.slider.1", "occasional", axis: true)
                 })
            request.Functions[function.Id] = function;
        request.Aircraft["F-16C_50"] = new SetupAircraft { Key = "F-16C_50", Game = "DCS", Type = "fixed_wing" };
        request.Aircraft["FA-18C_hornet"] = new SetupAircraft { Key = "FA-18C_hornet", Game = "DCS", Type = "fixed_wing" };
        request.Aircraft["F-15C"] = new SetupAircraft { Key = "F-15C", Game = "DCS", Type = "fixed_wing" };
        request.Aircraft["UH-1H"] = new SetupAircraft { Key = "UH-1H", Game = "DCS", Type = "rotary_wing" };
        request.Aircraft["StarCitizen"] = new SetupAircraft { Key = "StarCitizen", Game = "StarCitizen", Type = "spaceship" };
        foreach ((string function, string aircraft) in commands)
        {
            request.Commands.Add(new SetupCommand
            {
                Function = function, Aircraft = aircraft, Game = request.Aircraft[aircraft].Game, Action = "cmd_" + function + "_" + aircraft, Status = "confirmed",
                Frequency = request.Functions[function].Frequency
            });
        }
        request.Options = new SetupOptions { Games = ["DCS", "StarCitizen"], Realism = 0, Modifiers = 0, PreferFewer = true };
        return request;
    }

    private static List<SetupInput> StickInputs(string device, int thumbButtons)
    {
        List<SetupInput> inputs = [Axis(device, "JOY_X", "stick.axis.roll"), Axis(device, "JOY_Y", "stick.axis.pitch"), Axis(device, "JOY_RZ", "stick.axis.twist"),
            Button(device, 1, "stick.trigger.stage1"), Button(device, 2, "stick.pinky.button"), Button(device, 3, "stick.hat.cms.up"), Button(device, 4, "stick.hat.cms.down"),
            Button(device, 5, "stick.hat.cms.left"), Button(device, 6, "stick.hat.cms.right")];
        for (int i = 1; i <= thumbButtons; i++)
            inputs.Add(Button(device, 10 + i, "stick.button.thumb." + i));
        return inputs;
    }

    private static List<SetupInput> ThrottleInputs(string device) =>
    [
        Axis(device, "JOY_RX", "throttle.axis.left"), Axis(device, "JOY_RY", "throttle.axis.right"), Axis(device, "JOY_X", "throttle.axis.ministick.x"),
        Axis(device, "JOY_Y", "throttle.axis.ministick.y"), Axis(device, "JOY_Z", "throttle.axis.rotary.1"),
        Button(device, 1, "throttle.button.1"), Button(device, 2, "throttle.button.2"), Button(device, 3, "throttle.hat.1.up"), Button(device, 4, "throttle.hat.1.down")
    ];

    private static SetupPlanRelation Placed(SetupPlan plan, string function, string? aircraft = null) =>
        plan.Relations.Single(r => r.Function == function && r.Device.Length > 0 && (aircraft == null || r.Aircraft.Contains(aircraft)));

    // ---- roles ----

    [Fact]
    public void Roles_UserLikeSetup_StickThrottleLeftStickPedalsPanelWootingVJoy()
    {
        List<SetupDevice> devices =
        [
            Device("VKB-Sim Gunfighter Modern Combat Pro Twist {1}", "stick", "right", 40),
            new SetupDevice { Device = "Rhino FFB Joystick {2}", Model = "Rhino FFB Joystick", Kind = "base", Hand = "either", NeedsGrip = true, ForceFeedback = true, Buttons = 32 },
            Device("L-VPC Stick WarBRD {3}", "stick", "left", 31),
            Device("LEFT VPC Throttle MT-50 CM {4}", "throttle", "left", 64),
            Device("MFG Crosswind V2 {5}", "pedals", "none", 0),
            Device("WINWING F18 TAKEOFF PANEL {6}", "panel", "none", 32),
            new SetupDevice { Device = "vJoy Device {7}", Model = "vJoy Virtual Joystick", Kind = "virtual", VidPid = "1234:BEAD" },
            new SetupDevice { Device = "Wooting Two HE (ARM) {8}", Model = "Wooting analog keyboard", Kind = "keyboard" }
        ];
        SetupRoles.Propose(devices);
        Dictionary<string, string> roles = devices.ToDictionary(d => d.Device.Split(' ')[0], d => d.Role);
        Assert.Equal(SetupRoles.MainStick, roles["VKB-Sim"]);
        Assert.Equal(SetupRoles.Unused, roles["Rhino"]);
        Assert.Equal(SetupRoles.LeftStick, roles["L-VPC"]);
        Assert.Equal(SetupRoles.Throttle, roles["LEFT"]);
        Assert.Equal(SetupRoles.Pedals, roles["MFG"]);
        Assert.Equal(SetupRoles.Panel, roles["WINWING"]);
        Assert.Equal(SetupRoles.Virtual, roles["vJoy"]);
        Assert.Equal(SetupRoles.KeyboardJoystick, roles["Wooting"]);
        Assert.Contains("Stick for helicopters", devices[1].Reason);

        List<string> rules = SetupRoles.Rules(devices);
        Assert.Contains(rules, r => r.StartsWith("Helicopters: no collective: the collective goes on the throttle axis", StringComparison.Ordinal));
        Assert.Contains(rules, r => r.StartsWith("Space sims: translation on the left stick", StringComparison.Ordinal));
        Assert.Contains(rules, r => r.Contains("kept for your keyboard binds"));
    }

    [Fact]
    public void Roles_CollectiveAndLeftHandedOnly()
    {
        List<SetupDevice> withCollective = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left"), Device(Collective, "collective")];
        SetupRoles.Propose(withCollective);
        Assert.Equal(SetupRoles.Collective, withCollective[2].Role);
        Assert.Same(withCollective[2], SetupRoles.LeftHand(withCollective, SetupClasses.Rotary));
        Assert.Same(withCollective[1], SetupRoles.LeftHand(withCollective, SetupClasses.Fixed));
        Assert.Contains(SetupRoles.Rules(withCollective), r => r.StartsWith("Helicopters: the collective is VPC Collective", StringComparison.Ordinal));

        // Only a left-hand stick: it is the main stick (left-handed pilot); no throttle: space translation on the throttle rule is absent.
        List<SetupDevice> leftOnly = [Device(LeftStick, "stick", "left")];
        SetupRoles.Propose(leftOnly);
        Assert.Equal(SetupRoles.MainStick, leftOnly[0].Role);

        // A user's choice is kept when the roles are proposed again.
        List<SetupDevice> chosen = [Device(Stick, "stick", "right"), Device(LeftStick, "stick", "left")];
        chosen[1].Role = SetupRoles.Unused;
        SetupRoles.Propose(chosen);
        Assert.Equal(SetupRoles.Unused, chosen[1].Role);
        Assert.Equal(SetupRoles.LeftStick, chosen[1].ProposedRole);
    }

    // ---- collective and space translation ----

    [Fact]
    public void Plan_HelicopterCollectiveOnCollective_AircraftThrottleOnThrottle()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left"), Device(Collective, "collective")];
        List<SetupInput> inputs = [.. StickInputs(Stick, 4), .. ThrottleInputs(Throttle), Axis(Collective, "JOY_Z", "collective.axis.main"), Button(Collective, 1, "collective.button.1")];
        SetupPlan plan = SetupPlanner.Plan(Request(devices, inputs, ("throttleleftnonab", "UH-1H"), ("throttleleft", "F-16C_50"), ("pitch", "UH-1H"), ("pitch", "F-16C_50")));
        Assert.Equal(Collective, Placed(plan, "throttleleftnonab").Device);
        Assert.Equal("collective.axis.main", Placed(plan, "throttleleftnonab").Control);
        Assert.Equal(Throttle, Placed(plan, "throttleleft").Device);
        Assert.Equal("stick.axis.pitch", Placed(plan, "pitch").Control);

        // Without a collective the helicopter's collective goes on the throttle.
        List<SetupDevice> noCollective = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left")];
        SetupPlan plan2 = SetupPlanner.Plan(Request(noCollective, [.. StickInputs(Stick, 4), .. ThrottleInputs(Throttle)], ("throttleleftnonab", "UH-1H"), ("throttleleft", "F-16C_50")));
        Assert.Equal(Throttle, Placed(plan2, "throttleleftnonab").Device);
    }

    [Fact]
    public void Plan_SpaceTranslation_LeftStickElseThrottle()
    {
        (string, string)[] commands = [("space_strafe_hor", "StarCitizen"), ("space_strafe_ver", "StarCitizen"), ("space_strafe_fwdaft", "StarCitizen"), ("pitch", "StarCitizen")];
        List<SetupDevice> devices = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left"), Device(LeftStick, "stick", "left")];
        List<SetupInput> left = [Axis(LeftStick, "JOY_X", "stick.axis.roll"), Axis(LeftStick, "JOY_Y", "stick.axis.pitch"), Axis(LeftStick, "JOY_RZ", "stick.axis.twist"), Button(LeftStick, 1, "stick.trigger.stage1")];
        SetupPlan plan = SetupPlanner.Plan(Request(devices, [.. StickInputs(Stick, 4), .. ThrottleInputs(Throttle), .. left], commands));
        Assert.Equal((LeftStick, "stick.axis.roll"), (Placed(plan, "space_strafe_hor").Device, Placed(plan, "space_strafe_hor").Control));
        Assert.Equal((LeftStick, "stick.axis.pitch"), (Placed(plan, "space_strafe_fwdaft").Device, Placed(plan, "space_strafe_fwdaft").Control));
        Assert.Equal((LeftStick, "stick.axis.twist"), (Placed(plan, "space_strafe_ver").Device, Placed(plan, "space_strafe_ver").Control));
        Assert.Equal(Stick, Placed(plan, "pitch").Device);

        // No left stick: translation on the throttle's mini-stick and main axis.
        List<SetupDevice> noLeft = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left")];
        SetupPlan plan2 = SetupPlanner.Plan(Request(noLeft, [.. StickInputs(Stick, 4), .. ThrottleInputs(Throttle)], commands));
        Assert.Equal("throttle.axis.ministick.x", Placed(plan2, "space_strafe_hor").Control);
        Assert.Equal("throttle.axis.ministick.y", Placed(plan2, "space_strafe_ver").Control);
        Assert.Equal(Throttle, Placed(plan2, "space_strafe_fwdaft").Device);
    }

    // ---- modifier budget ----

    private static readonly string[] EightWeapons = ["radar_lock", "radar_unlock", "weaponselector1", "weaponselector2", "weaponselector3", "weaponselector4", "select_gun", "select_heater"];

    [Fact]
    public void Modifiers_FewButtons_ForceAModifierAndSayWhy()
    {
        // A stick with 2 thumb buttons, a pinky and a 4-way hat for 8 frequent weapon functions of the F-16.
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 9)];
        SetupRequest request = Request(devices, StickInputs(Stick, 2), EightWeapons.Select(f => (f, "F-16C_50")).ToArray());
        request.Options.Modifiers = 0;
        SetupPlan plan = SetupPlanner.Plan(request);
        Assert.NotEmpty(plan.Modifiers);
        Assert.All(plan.Modifiers, m => Assert.True(m.Forced));
        Assert.Equal("stick.pinky.button", plan.Modifiers[0].Control);
        Assert.Contains(plan.Notes, n => n.Contains("although you asked for none"));
        Assert.Contains(plan.Relations, r => r.Modifier.Length > 0);
        Assert.Equal(EightWeapons.Length, plan.Relations.Count(r => r.Device.Length > 0));
    }

    [Fact]
    public void Modifiers_EnoughButtons_NoneEvenWhenAllowed()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 30)];
        SetupRequest request = Request(devices, StickInputs(Stick, 12), EightWeapons.Select(f => (f, "F-16C_50")).ToArray());
        request.Options.Modifiers = 2;
        request.Options.PreferFewer = true;
        SetupPlan plan = SetupPlanner.Plan(request);
        Assert.Empty(plan.Modifiers);
        Assert.Contains(plan.Notes, n => n.StartsWith("No modifier needed", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Relations, r => r.Modifier.Length > 0);
    }

    [Fact]
    public void Modifiers_ProfilesOwnModifierComesFirst()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 9)];
        SetupRequest request = Request(devices, StickInputs(Stick, 2), EightWeapons.Select(f => (f, "F-16C_50")).ToArray());
        request.ExistingModifiers.Add(new SetupModifierChoice { Name = "MyShift", Device = Stick, Input = "JOY_BTN11", Uses = 12 });
        request.Options.Modifiers = 1;
        SetupPlan plan = SetupPlanner.Plan(request);
        Assert.Equal("MyShift", plan.Modifiers[0].Name);
        Assert.True(plan.Modifiers[0].Existing);
        Assert.False(plan.Modifiers[0].Forced);
    }

    // ---- priority weighting ----

    [Fact]
    public void Priority_TheHigherAircraftGetsTheBetterPlace()
    {
        // One good input (the CMS hat up) for two functions that share the F-15C (so they cannot share the input):
        // CMS FWD of the F-16 + F-15C and Radar Lock of the F/A-18 + F-15C, both prefer the hat.
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 10)];
        List<SetupInput> inputs = [Button(Stick, 1, "stick.hat.cms.up"), Button(Stick, 2, "stick.base.button.1"), Button(Stick, 3, "stick.base.button.2")];
        (string, string)[] commands = [("cms_fwd", "F-16C_50"), ("cms_fwd", "F-15C"), ("radar_unlock", "FA-18C_hornet"), ("radar_unlock", "F-15C")];
        SetupFunction hatLover = Function("radar_unlock", "Radar Unlock", "stick", "stick.hat.cms.up,stick.button.*");

        SetupRequest f16First = Request(devices, inputs, commands);
        f16First.Functions["radar_unlock"] = hatLover;
        f16First.Options.Priorities = [["F-16C_50"], ["FA-18C_hornet"]];
        SetupPlan a = SetupPlanner.Plan(f16First);
        Assert.Equal("stick.hat.cms.up", Placed(a, "cms_fwd").Control);
        Assert.NotEqual("stick.hat.cms.up", Placed(a, "radar_unlock").Control);
        SetupQuality f16 = a.Quality.Single(q => q.Aircraft == "F-16C_50");
        SetupQuality f18 = a.Quality.Single(q => q.Aircraft == "FA-18C_hornet");
        Assert.Equal(0, f16.Tier);
        Assert.True(f16.Score > f18.Score);

        SetupRequest f18First = Request(devices, inputs, commands);
        f18First.Functions["radar_unlock"] = hatLover;
        f18First.Options.Priorities = [["FA-18C_hornet"], ["F-16C_50"]];
        SetupPlan b = SetupPlanner.Plan(f18First);
        Assert.Equal("stick.hat.cms.up", Placed(b, "radar_unlock").Control);
        Assert.NotEqual("stick.hat.cms.up", Placed(b, "cms_fwd").Control);

        // Unordered = equal weight: both aircraft in one tier.
        Dictionary<string, int> tierOf;
        Dictionary<string, double> weights = SetupImportance.Weights([["F-16C_50", "FA-18C_hornet"], ["StarCitizen"]], f18First.Aircraft.Values, out tierOf);
        Assert.Equal(weights["F-16C_50"], weights["FA-18C_hornet"]);
        Assert.True(weights["FA-18C_hornet"] > weights["StarCitizen"]);
        Assert.True(weights["StarCitizen"] > weights["F-15C"]);
    }

    // ---- realism ----

    [Fact]
    public void Realism_ZeroGroupsByFunction_OneFollowsTheCockpitDatabase()
    {
        // Fake cockpit database entry: the F-16's real Weapon Selector 1 is the CMS hat up, the F/A-18's the throttle hat.
        CockpitPositionTable cockpits = new();
        List<string> read = cockpits.AddJson(
            "[{\"aircraft\":\"F-16C_50\",\"positions\":[{\"control\":\"stick.hat.cms.up\",\"functions\":[\"weaponselector1\"]}]}," +
            " {\"Aircraft\":\"FA-18C_hornet\",\"Positions\":[{\"Control\":\"throttle.hat.1.up\",\"Function\":\"weaponselector1\"}]}]", "fake", null);
        Assert.Equal(["F-16C_50", "FA-18C_hornet"], read);
        List<SetupDevice> devices = [Device(Stick, "stick", "right"), Device(Throttle, "throttle", "left")];
        List<SetupInput> inputs = [.. StickInputs(Stick, 4), .. ThrottleInputs(Throttle)];
        (string, string)[] commands = [("weaponselector1", "F-16C_50"), ("weaponselector1", "FA-18C_hornet")];

        SetupRequest byFunction = Request(devices, inputs, commands);
        byFunction.Cockpits = cockpits;
        byFunction.Options.Realism = 0;
        SetupPlan zero = SetupPlanner.Plan(byFunction);
        SetupPlanRelation shared = Assert.Single(zero.Relations, r => r.Function == "weaponselector1");
        Assert.Equal("WeaponSelector1", shared.Name);
        Assert.Equal(["F-16C_50", "FA-18C_hornet"], shared.Aircraft);
        Assert.StartsWith("stick.button.thumb.", shared.Control, StringComparison.Ordinal);

        SetupRequest real = Request(devices, inputs, commands);
        real.Cockpits = cockpits;
        real.Options.Realism = 1;
        SetupPlan one = SetupPlanner.Plan(real);
        List<SetupPlanRelation> split = one.Relations.Where(r => r.Function == "weaponselector1").ToList();
        Assert.Equal(2, split.Count);
        SetupPlanRelation f16 = split.Single(r => r.Aircraft.Contains("F-16C_50"));
        SetupPlanRelation f18 = split.Single(r => r.Aircraft.Contains("FA-18C_hornet"));
        Assert.Equal("stick.hat.cms.up", f16.Control);
        Assert.Equal("throttle.hat.1.up", f18.Control);
        Assert.Equal(1.0, f16.RealMatch);
        Assert.Contains("cockpit database", one.CockpitSource);
    }

    // ---- panels, existing binds, determinism, the plan file and the review ----

    [Fact]
    public void Plan_LabelledPanelSwitchesAndPairs()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right"), Device(Panel, "panel")];
        List<SetupInput> inputs = [.. StickInputs(Stick, 2),
            Button(Panel, 1, "panel.toggle.1.up", "LAUNCH BAR RETRACT", "latched"), Button(Panel, 2, "panel.toggle.1.down", "LAUNCH BAR EXTEND", "latched"),
            Button(Panel, 3, "panel.toggle.2.up", "ANTI SKID ON", "latched"), Button(Panel, 4, "panel.toggle.2.down", "ANTI SKID OFF", "latched"),
            Button(Panel, 5, "panel.lever.gear.up", "LDG GEAR UP", "latched"), Button(Panel, 6, "panel.lever.gear.down", "LDG GEAR DOWN", "latched")];
        SetupPlan plan = SetupPlanner.Plan(Request(devices, inputs, ("gearin", "F-16C_50"), ("gearout", "F-16C_50"), ("antiskidon", "F-16C_50"), ("antiskidoff", "F-16C_50")));
        Assert.Equal("LDG GEAR UP", Placed(plan, "gearin").Label);
        Assert.Equal("LDG GEAR DOWN", Placed(plan, "gearout").Label);
        Assert.Equal("ANTI SKID ON", Placed(plan, "antiskidon").Label);
        Assert.Equal("ANTI SKID OFF", Placed(plan, "antiskidoff").Label);
    }

    [Fact]
    public void Plan_KeepsTheUsersBindsAndCommands()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right")];
        SetupRequest request = Request(devices, StickInputs(Stick, 4), ("cms_fwd", "F-16C_50"), ("cms_aft", "F-16C_50"), ("trigger1ststage", "F-16C_50"));
        request.Existing.Add(new SetupExistingRelation
        {
            Name = "My Dispense", Device = Stick, Input = "JOY_BTN4", OnSetup = true, Function = "cms_fwd",
            Nodes = [new SetupNode { Game = "DCS", Action = "cmd_cms_fwd_F-16C_50", Aircraft = "F-16C_50" }]
        });
        SetupPlan plan = SetupPlanner.Plan(request);
        SetupPlanRelation kept = plan.Relations.Single(r => r.Name == "My Dispense");
        Assert.True(kept.Kept);
        Assert.Equal("JOY_BTN4", kept.Input);
        // Its command stays in the user's relation: no second CMS FWD relation.
        Assert.DoesNotContain(plan.Relations, r => r.Function == "cms_fwd" && !r.Kept);
        // Its input is taken for the F-16: CMS AFT does not land there.
        Assert.NotEqual("JOY_BTN4", Placed(plan, "cms_aft").Input);
        Assert.Empty(SetupPlanEdit.Validate(plan));
    }

    [Fact]
    public void Plan_IsDeterministic_AndTheFileRoundTrips()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 9), Device(Throttle, "throttle", "left")];
        Func<SetupPlan> make = () =>
        {
            SetupRequest request = Request(devices.Select(d => new SetupDevice { Device = d.Device, Model = d.Model, Kind = d.Kind, Hand = d.Hand, Buttons = d.Buttons }).ToList(),
                [.. StickInputs(Stick, 2), .. ThrottleInputs(Throttle)], EightWeapons.Select(f => (f, "F-16C_50")).Concat(EightWeapons.Select(f => (f, "FA-18C_hornet"))).ToArray());
            request.Options.Seed = 7;
            request.Options.Realism = 0.6;
            return SetupPlanner.Plan(request);
        };
        string first = SetupPlanJson.Write(make(), false);
        string second = SetupPlanJson.Write(make(), false);
        Assert.Equal(first, second);

        SetupPlan read = SetupPlanJson.Read(first);
        Assert.Equal(first, SetupPlanJson.Write(read, false));
        Assert.Contains("Setup Plan", SetupPlanText.Write(read, 10));
    }

    [Fact]
    public void Review_MoveSwapRemove_RefuseConflicts()
    {
        List<SetupDevice> devices = [Device(Stick, "stick", "right", 30)];
        SetupPlan plan = SetupPlanner.Plan(Request(devices, StickInputs(Stick, 6), ("radar_lock", "F-16C_50"), ("radar_unlock", "F-16C_50"), ("select_gun", "FA-18C_hornet")));
        SetupPlanRelation lockRelation = Placed(plan, "radar_lock");
        SetupPlanRelation unlockRelation = Placed(plan, "radar_unlock");
        SetupPlanRelation gun = Placed(plan, "select_gun");

        // The same aircraft on one input: refused; another aircraft's relation may share it.
        Assert.StartsWith("Already used for the same aircraft", SetupPlanEdit.Move(plan, lockRelation, unlockRelation.Device, unlockRelation.Input, string.Empty), StringComparison.Ordinal);
        Assert.Equal(string.Empty, SetupPlanEdit.Move(plan, gun, lockRelation.Device, lockRelation.Input, string.Empty));
        Assert.Equal(lockRelation.Input, gun.Input);
        Assert.Equal("An axis and a button cannot be swapped.", SetupPlanEdit.Swap(plan, lockRelation, new SetupPlanRelation { IsAxis = true, Device = Stick, Input = "JOY_X" }));

        string before = lockRelation.Input;
        Assert.Equal(string.Empty, SetupPlanEdit.Swap(plan, lockRelation, unlockRelation));
        Assert.Equal(before, unlockRelation.Input);
        Assert.Empty(SetupPlanEdit.Validate(plan));

        Assert.Equal(string.Empty, SetupPlanEdit.Remove(plan, gun));
        Assert.DoesNotContain(gun, plan.Placed);
        Assert.Equal(string.Empty, SetupPlanEdit.Restore(plan, gun));
        Assert.Contains(gun, plan.Placed);
    }
}
