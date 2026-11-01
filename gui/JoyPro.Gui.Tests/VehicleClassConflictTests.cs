using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.1: the check's vehicle classes for games without aircraft lists (the game is the "aircraft" of every action) also
// know ground vehicles and ships, by the game catalog's category ("Tank", "Ship", Star Citizen's "Ground vehicle: ...",
// DB\Functions\function-map.json "classes"): one axis on the plane's throttle and the tank's or ship's is no conflict,
// two actions of the same class still are, and a space game's "Ship: flight" stays a spaceship. A real engine
// (relations conflicts) on a small action index (a copy of engine\tests\fixtures\function-map\DB with tank, ship and
// ground vehicle rows) and a profile in the temp folder.
public sealed class VehicleClassConflictTests : IDisposable
{
    private const string Quadrant = "VC Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "joypro-vehicle-classes-" + Guid.NewGuid().ToString("N"));

    public VehicleClassConflictTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AxisSharedAcrossVehicleClasses_IsNoConflict_SameClassStillIs()
    {
        string? engine = DuplicateFixture.EnginePath();
        if (engine == null)
            return;
        string index = Path.Combine(root, "DB");
        CopyFolder(Path.Combine(FixturesFolder(), "function-map", "DB"), index);
        File.AppendAllText(Path.Combine(index, "WarThunder", "actions.tsv"),
            "\ngm_throttle\tTank throttle\taxis\tTank\ngm_steering\tTank steering\taxis\tTank\nship_main_engine\tShip engine power\taxis\tShip\nship_rudder\tShip rudder\taxis\tShip\n");
        string sc = Path.Combine(index, "StarCitizen", "AllBinds.xml");
        File.WriteAllText(sc, File.ReadAllText(sc).Replace("</ActionMaps>",
            " <actionmap name=\"vehicle_driver\">\n  <action name=\"v_drive_throttle\"><!--@ui_v_drive_throttle//true-->\n  </action>\n" +
            "  <action name=\"v_drive_steering\"><!--@ui_v_drive_steering//true-->\n  </action>\n </actionmap>\n</ActionMaps>"));

        Dictionary<string, object> relations = new()
        {
            ["VC Plane Tank"] = Relation("VC Plane Tank", ("WarThunder", "throttle"), ("WarThunder", "gm_throttle")),
            ["VC Plane Ship"] = Relation("VC Plane Ship", ("WarThunder", "throttle"), ("WarThunder", "ship_main_engine")),
            ["VC Tank Two Axes"] = Relation("VC Tank Two Axes", ("WarThunder", "gm_throttle"), ("WarThunder", "gm_steering")),
            ["VC Ship Two Axes"] = Relation("VC Ship Two Axes", ("WarThunder", "ship_main_engine"), ("WarThunder", "ship_rudder")),
            ["VC Plane Throttle"] = Relation("VC Plane Throttle", ("WarThunder", "throttle")),
            ["VC Tank Throttle"] = Relation("VC Tank Throttle", ("WarThunder", "gm_throttle")),
            ["VC Ship Engine"] = Relation("VC Ship Engine", ("WarThunder", "ship_main_engine")),
            ["VC Tank Steering"] = Relation("VC Tank Steering", ("WarThunder", "gm_steering")),
            ["VC Ship Ground"] = Relation("VC Ship Ground", ("StarCitizen", "spaceship_movement|v_strafe_longitudinal"), ("StarCitizen", "vehicle_driver|v_drive_throttle")),
            ["VC Ground Two Axes"] = Relation("VC Ground Two Axes", ("StarCitizen", "vehicle_driver|v_drive_throttle"), ("StarCitizen", "vehicle_driver|v_drive_steering")),
            ["VC Elite Two Axes"] = Relation("VC Elite Two Axes", ("EliteDangerous", "ThrottleAxis"), ("EliteDangerous", "LateralThrustRaw")),
        };
        Dictionary<string, object> binds = new()
        {
            ["VC Plane Tank"] = Bind("JOY_Z"),
            ["VC Plane Ship"] = Bind("JOY_RZ"),
            ["VC Tank Two Axes"] = Bind("JOY_RX"),
            ["VC Ship Two Axes"] = Bind("JOY_RY"),
            // Two relations on one input: plane + tank and plane + ship are fine, tank + tank is not.
            ["VC Plane Throttle"] = Bind("JOY_SLIDER1"),
            ["VC Tank Throttle"] = Bind("JOY_SLIDER1"),
            ["VC Ship Engine"] = Bind("JOY_SLIDER1"),
            ["VC Tank Steering"] = Bind("JOY_X"),
            ["VC Ship Ground"] = Bind("JOY_Y"),
            ["VC Ground Two Axes"] = Bind("JOY_SLIDER2"),
            ["VC Elite Two Axes"] = Bind("JOY_BTN1"),
        };
        string profile = Path.Combine(root, "classes.pr0file");
        File.WriteAllText(profile, JsonSerializer.Serialize(new Dictionary<string, object> { ["Relations"] = relations, ["Binds"] = binds }));
        // A second tank axis on the tank throttle's input of another relation: a real conflict (same class).
        Dictionary<string, object> more = new(relations) { ["VC Tank Steering"] = Relation("VC Tank Steering", ("WarThunder", "gm_steering")) };
        Dictionary<string, object> moreBinds = new(binds) { ["VC Tank Steering"] = Bind("JOY_SLIDER1") };
        string profileSame = Path.Combine(root, "same-class.pr0file");
        File.WriteAllText(profileSame, JsonSerializer.Serialize(new Dictionary<string, object> { ["Relations"] = more, ["Binds"] = moreBinds }));

        using JsonDocument conflicts = Conflicts(engine, index, profile);
        List<string> actionConflicts = conflicts.RootElement.GetProperty("ActionConflicts").EnumerateArray().Select(c => c.GetProperty("Relation").GetString()!).ToList();
        Assert.DoesNotContain("VC Plane Tank", actionConflicts);
        Assert.DoesNotContain("VC Plane Ship", actionConflicts);
        Assert.DoesNotContain("VC Ship Ground", actionConflicts);
        Assert.Contains("VC Tank Two Axes", actionConflicts);
        Assert.Contains("VC Ship Two Axes", actionConflicts);
        Assert.Contains("VC Ground Two Axes", actionConflicts);
        // Elite's "Ship: flight" is a spaceship (space game), not a ship: its two flight axes stay one vehicle.
        Assert.Contains("VC Elite Two Axes", actionConflicts);
        List<string> inputs = conflicts.RootElement.GetProperty("InputConflicts").EnumerateArray().Select(c => c.GetProperty("Input").GetString()!).ToList();
        Assert.DoesNotContain("JOY_SLIDER1", inputs);

        using JsonDocument same = Conflicts(engine, index, profileSame);
        Assert.Contains(same.RootElement.GetProperty("InputConflicts").EnumerateArray(), c => c.GetProperty("Input").GetString() == "JOY_SLIDER1");
    }

    private static Dictionary<string, object> Relation(string name, params (string Game, string Id)[] nodes) => new()
    {
        ["NAME"] = name,
        ["ISAXIS"] = true,
        ["Groups"] = Array.Empty<string>(),
        ["NODES"] = nodes.Select(n => new Dictionary<string, object> { ["ID"] = n.Id, ["Game"] = n.Game, ["AIRCRAFT"] = new Dictionary<string, bool> { [n.Game] = true } }).ToArray()
    };

    private static Dictionary<string, object> Bind(string input) => new()
    {
        ["Joystick"] = Quadrant,
        ["JAxis"] = input.StartsWith("JOY_BTN", StringComparison.Ordinal) ? string.Empty : input,
        ["JButton"] = input.StartsWith("JOY_BTN", StringComparison.Ordinal) ? input : string.Empty,
        ["AllReformers"] = Array.Empty<string>(),
        ["Curvature"] = new[] { 0.0 },
        ["AdditionalImportInfo"] = string.Empty,
        ["AliasJoystick"] = string.Empty
    };

    private JsonDocument Conflicts(string engine, string index, string profile)
    {
        string output = Path.Combine(root, "conflicts-" + Guid.NewGuid().ToString("N") + ".json");
        ProcessStartInfo info = new(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        foreach (string arg in new[] { "--profile", profile, "--load-corrections", "false", "--remap-missing-devices", "false", "--out", output, "relations", "conflicts" })
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_ACTION_INDEX_DIR"] = index;
        info.Environment["JOYPRO_FUNCTION_MAP_CACHE"] = "off";

        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(180000), "engine relations conflicts did not finish.");
        Assert.True(process.ExitCode == 0, "engine relations conflicts failed: " + stdout + stderr.Result);
        return JsonDocument.Parse(File.ReadAllText(output));
    }

    private static string FixturesFolder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "engine", "tests", "fixtures");
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
    }
}
