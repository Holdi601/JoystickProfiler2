using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// MSFS 2024 import: relations are for all aircraft unless the user splits them (docs/games/msfs.md "Per-aircraft
// binds"); add-on aircraft bring their own actions, tagged with their aircraft ("Add-on aircraft").
public sealed partial class MsfsProfileImportTests
{
    private const string AddonAction = "PMDG_737_EFIS_CPT_MODE";

    private static string Bound(string action, int button) =>
        "\t\t<Action ActionName=\"" + action + "\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Button " +
        button.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\">" + (button - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n";

    private static JsonElement Json(string output)
    {
        // The JSON on stdout (stderr notes follow it).
        Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(output[output.IndexOf('{')..]), new JsonReaderOptions { AllowMultipleValues = true });
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Steam2024_BindOnlyInSomeAircraftsProfiles_IsImportedForAllAircraft_AndListedToLimit()
    {
        (string home, string remote, _, _) = SteamPerAircraftSetup();
        // The user's own binds in the sim (JoyPro never exported them): taxi lights only in the C172's profile, the
        // add-on action (no KEY_ id) too.
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000101"), AirplaneProfile("C172 Planes", Bound("KEY_TOGGLE_TAXI_LIGHTS", 7) + Bound(AddonAction, 8)));
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000104"), AirplaneProfile("Bonanza Planes"));

        string clean = Path.Combine(root, "general.pr0file");
        (int code, string import) = Engine(home, "--profile", clean, "msfs2024", "import");
        Assert.True(code == 0, import);
        JsonElement result = Json(import);
        JsonElement[] only = result.GetProperty("OnlyInAircraft").EnumerateArray().ToArray();
        JsonElement taxi = Assert.Single(only, o => o.GetProperty("ActionId").GetString() == "KEY_TOGGLE_TAXI_LIGHTS");
        Assert.Equal([C172], taxi.GetProperty("Aircraft").EnumerateArray().Select(a => a.GetString()).ToArray());
        Assert.Equal("C172 Test", taxi.GetProperty("AircraftText").GetString());
        string relation = taxi.GetProperty("Relation").GetString()!;
        // Imported for all aircraft.
        Assert.Equal("all aircraft", NodeOf(Engine(home, "--profile", clean, "relations", "aircraft", relation).Output).GetProperty("Badge").GetString());
        // The add-on action exists only for the C172 (only its profile has it): the catalog says so, the relation is
        // limited on its own and not listed.
        Assert.DoesNotContain(only, o => o.GetProperty("ActionId").GetString() == AddonAction);
        string catalog = File.ReadAllText(Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv"));
        Assert.Matches("(?m)^" + AddonAction + "\t[^\t]*\tbutton\tAIRCRAFT\t\t" + C172 + "\r?$", catalog);
        (_, string relations) = Engine(home, "--profile", clean, "relations", "list");
        string addonRelation = Json(relations).GetProperty("Relations").EnumerateArray()
            .First(r => r.GetProperty("Nodes").EnumerateArray().Any(n => n.GetProperty("Id").GetString() == AddonAction)).GetProperty("Name").GetString()!;
        Assert.Equal("1 of 2 aircraft", NodeOf(Engine(home, "--profile", clean, "relations", "aircraft", addonRelation).Output).GetProperty("Badge").GetString());

        // The text output names it with the command that limits it; the command does.
        (_, string text) = Engine(home, "--profile", Path.Combine(root, "text.pr0file"), "msfs2024", "import", "--text");
        Assert.Contains("Only in the sim's profile(s) of C172 Test: " + relation + " (" + Vmax + " JOY_BTN7), imported for all aircraft. Limit: engine relations aircraft set \"" + relation +
                        "\" --node MSFS2024:KEY_TOGGLE_TAXI_LIGHTS --only \"" + C172 + "\"", text);
        Assert.Equal(0, Engine(home, "--profile", clean, "relations", "aircraft", "set", relation, "--node", "MSFS2024:KEY_TOGGLE_TAXI_LIGHTS", "--only", C172).Code);
        JsonElement limited = NodeOf(Engine(home, "--profile", clean, "relations", "aircraft", relation).Output);
        Assert.Equal("1 of 2 aircraft", limited.GetProperty("Badge").GetString());
        Assert.Contains(limited.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == C172 && a.GetProperty("State").GetString() == "on");
        // The GUI's hint: one line per relation, the command per button.
        JoyPro.Gui.Models.MsfsAircraftOnlyBind hint = Assert.Single(MainWindowViewModel.ParseMsfsAircraftOnlyBinds(import), h => h.ActionId == "KEY_TOGGLE_TAXI_LIGHTS");
        Assert.Equal(["relations", "aircraft", "set", relation, "--node", "MSFS2024:KEY_TOGGLE_TAXI_LIGHTS", "--only", C172], hint.LimitArgs);

        // --limit-to-profile-aircraft limits them right away.
        string split = Path.Combine(root, "split.pr0file");
        (int splitCode, string splitImport) = Engine(home, "--profile", split, "msfs2024", "import", "--limit-to-profile-aircraft");
        Assert.True(splitCode == 0, splitImport);
        Assert.Empty(Json(splitImport).GetProperty("OnlyInAircraft").EnumerateArray());
        Assert.Equal("1 of 2 aircraft", NodeOf(Engine(home, "--profile", split, "relations", "aircraft", relation).Output).GetProperty("Badge").GetString());
    }

    [Fact]
    public void Steam2024_RefreshDb_ReadsAddonPackagePresets_ActionsTaggedWithTheirAircraft()
    {
        (string home, _, string community, string profile) = SteamPerAircraftSetup();
        const string Pmdg = "pmdg-aircraft-737-800";
        string package = Path.Combine(community, Pmdg);
        Directory.CreateDirectory(Path.Combine(package, "InputProfiles", "PMDG"));
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{ \"content_type\": \"AIRCRAFT\", \"title\": \"PMDG 737-800\" }");
        File.WriteAllText(Path.Combine(package, "InputProfiles", "PMDG", "737.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version Num=\"-1\"/>\n<FriendlyName>PMDG 737</FriendlyName>\n<Device DeviceName=\"VPC VMAX Prime Throttle\" GUID=\"{" + VmaxGuid + "}\" ProductID=\"413\">\n" +
            "\t<AircraftInfo CategoryName=\"AIRPLANE\"/>\n\t<Context ContextName=\"AIRCRAFT\">\n" + Bound(AddonAction, 3) + Bound("KEY_GEAR_TOGGLE", 4) + "\t</Context>\n</Device>\n");
        // A Marketplace package of encrypted archives only: skipped with a note.
        string locked = Path.Combine(community, "vendor-aircraft-locked");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "content.fsarchive"), [1, 2, 3]);

        (int code, string refresh) = Engine(home, "msfs2024", "refresh-db", "--text");
        Assert.True(code == 0, refresh);
        Assert.Contains("Add-on input presets (Community and Marketplace packages): 1 package(s) read, 1 new action(s) (" + Pmdg + "); 1 encrypted package(s) skipped (.fsarchive, not readable: vendor-aircraft-locked).", refresh);
        string catalog = File.ReadAllText(Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv"));
        Assert.Matches("(?m)^" + AddonAction + "\t[^\t]*\tbutton\tAIRCRAFT\t\t" + Pmdg + "\r?$", catalog);
        // The sim's own action stays for every aircraft.
        Assert.Matches("(?m)^KEY_GEAR_TOGGLE\t[^\t]*\tbutton\t[A-Z_]+\r?$", catalog);

        // Added in the relation editor, the action comes in for its aircraft alone.
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "EFIS mode", "--node", "MSFS2024:" + AddonAction, "--device", Vmax, "--input", "JOY_BTN12").Code);
        JsonElement node = NodeOf(Engine(home, "--profile", profile, "relations", "aircraft", "EFIS mode").Output);
        Assert.Contains(node.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == Pmdg && a.GetProperty("State").GetString() == "on");
        Assert.DoesNotContain(node.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == C172 && a.GetProperty("State").GetString() == "on");
        // The relation editor's search shows "only for".
        Assert.Equal([Pmdg], MainWindowViewModel.CatalogActionAircraft(Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv"))[AddonAction]);
    }
}
