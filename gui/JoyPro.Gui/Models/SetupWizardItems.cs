using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Setup;

namespace JoyPro.Gui.Models;

// Items of the Setup wizard (SetupWizardViewModel, docs/setup-wizard.md).

public sealed partial class SetupStepItem : ObservableObject
{
    public SetupStepItem(int index, string title)
    {
        Index = index;
        Title = title;
    }

    public int Index { get; }

    public string Number => (Index + 1).ToString();

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodo))]
    private bool isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodo))]
    private bool isDone;

    public bool IsTodo => !IsCurrent && !IsDone;
}

public sealed partial class SetupGameChoice : ObservableObject
{
    public SetupGameChoice(string gameId, string name, bool watched, bool started = true)
    {
        GameId = gameId;
        Name = name;
        Watched = watched;
        Started = started;
        // Ticked: watched and started once (a game never started has no control files to set up yet).
        isChosen = watched && started;
    }

    public string GameId { get; }

    public string Name { get; }

    public bool Watched { get; }

    public bool Started { get; }

    public string Hint => !Started ? T("not started yet") : Watched ? T("watched on the Start page") : T("not watched");

    [ObservableProperty]
    private bool isChosen;
}

public sealed record SetupRoleOption(string Id, string Title)
{
    public override string ToString() => Title;
}

public sealed partial class SetupDeviceItem : ObservableObject
{
    public SetupDeviceItem(SetupDevice device, int inputs, IReadOnlyList<SetupRoleOption> roles)
    {
        Device = device;
        Inputs = inputs;
        Roles = roles;
        selectedRole = roles.FirstOrDefault(r => r.Id == device.Role) ?? roles.Last();
        Grips = device.Grips.Select(g => new SetupRoleOption(g.Id, g.Name)).ToList();
        selectedGrip = Grips.FirstOrDefault(g => g.Id == device.Grip);
    }

    public SetupDevice Device { get; }

    public string Name => SetupRoles.Short(Device);

    public string Details
    {
        get
        {
            List<string> parts = [KindName(Device.Kind)];
            if (Device.Hand.Length > 0 && Device.Hand != "none")
                parts.Add(Device.Hand switch { "left" => T("left hand"), "right" => T("right hand"), "either" => T("either hand"), _ => Device.Hand });
            if (Device.ForceFeedback)
                parts.Add(T("force feedback"));
            if (Device.Generic)
                parts.Add(T("not in the catalog"));
            parts.Add(T("{0} inputs", Inputs));
            if (!Device.Connected)
                parts.Add(T("not connected"));
            return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public string Reason => JoyPro.Gui.Services.EngineText.Tr(Device.Reason);

    // The device kind of the catalog in the app's language.
    private static string KindName(string kind) => kind switch
    {
        "stick" => T("stick"), "throttle" => T("throttle"), "pedals" => T("pedals"), "collective" => T("collective"), "panel" => T("panel"),
        "mfd" => T("MFD"), "yoke" => T("yoke"), "quadrant" => T("throttle quadrant"), "buttonbox" => T("button box"), "keyboard" => T("keyboard"),
        _ => kind
    };

    public int Inputs { get; }

    public IReadOnlyList<SetupRoleOption> Roles { get; }

    public IReadOnlyList<SetupRoleOption> Grips { get; }

    public bool HasGrips => Grips.Count > 0;

    public bool NeedsGrip => Device.NeedsGrip && SelectedGrip == null;

    public bool IsChanged => SelectedRole != null && SelectedRole.Id != Device.ProposedRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private SetupRoleOption? selectedRole;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsGrip))]
    private SetupRoleOption? selectedGrip;

    public Action? Changed { get; set; }

    partial void OnSelectedRoleChanged(SetupRoleOption? value)
    {
        if (value != null)
            Device.Role = value.Id;
        Changed?.Invoke();
    }

    partial void OnSelectedGripChanged(SetupRoleOption? value)
    {
        Device.Grip = value?.Id ?? string.Empty;
        Changed?.Invoke();
    }
}

// An aircraft of the chosen games (engine setup aircraft): its key, the name a person reads (DCS's module name, the
// cockpit database's name, a game's own name), its module, whether that module is on this PC, its rank among the popular
// DCS modules (-1: none) and whether it is a second seat (it comes with its module).
public sealed record SetupAircraftItem(string Key, string Game, string ClassTitle, string Name = "", string Module = "", bool Installed = false, int Popular = -1, bool Seat = false)
{
    public string Display => Game == Key ? T("{0} (game)", Name.Length > 0 ? Name : Key) : Name.Length > 0 ? Name : Key;

    public string Details => (Game == Key ? T("one setup for the whole game") : Game) + " | " + T(ClassTitle);

    public override string ToString() => Display;
}

// One answer of the quick path's "Which aircraft do you fly?": a plane by the name a person reads, with the keys it
// stands for (its seats and same-named variants come with it).
public sealed partial class SetupAircraftPick : ObservableObject
{
    public SetupAircraftPick(string name, IReadOnlyList<string> keys, bool installed, int popular)
    {
        Name = name;
        Keys = keys;
        Installed = installed;
        Popular = popular;
    }

    public string Name { get; }

    public IReadOnlyList<string> Keys { get; }

    public bool Installed { get; }

    public int Popular { get; }

    public string Hint => Installed ? T("on this PC") : string.Empty;

    public bool HasHint => Installed;

    [ObservableProperty]
    private bool isChosen;

    public Action? Changed { get; set; }

    partial void OnIsChosenChanged(bool value) => Changed?.Invoke();

    public override string ToString() => Name;
}

// One rank of the priority list; the aircraft in it count the same.
public sealed partial class SetupTierItem : ObservableObject
{
    public ObservableCollection<SetupAircraftItem> Aircraft { get; } = [];

    [ObservableProperty]
    private int rank;

    public string Title => T("Priority {0}{1}", Rank, (Aircraft.Count > 1 ? " (equal)" : string.Empty));

    partial void OnRankChanged(int value) => OnPropertyChanged(nameof(Title));

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

// One input of a device in the review: what the plan puts on it (one row per relation).
public sealed class SetupReviewRow
{
    public SetupReviewRow(SetupPlanRelation relation, bool plain = false, string nameOverride = "")
    {
        Relation = relation;
        Plain = plain;
        this.nameOverride = nameOverride ?? string.Empty;
    }

    // The quick review's own name for the row (an aircraft's only throttle lever is "Throttle", not "Throttle right").
    private readonly string nameOverride;

    // The quick path: reasons without JoyPro's planning words ("a fitting position (preference 4)").
    public bool Plain { get; }

    public SetupPlanRelation Relation { get; }

    // In plain words: "Hat 1 down + JoyPro Shift", "TMS hat down" or the printed label (never an input or control id).
    // The quick path: what the user holds, in the plainest words: an axis by its part ("Twist", not "Z rotation"), a
    // button by its printed label (L1, Square), else its number ("Hat 1 left", never the catalog's "Trim hat left" for a
    // hat that does no trim in this game).
    public string Input => (Plain ? PlainInput : JoyPro.Gui.Services.ReadableNames.Input(Relation.Input)) + (Relation.Modifier.Length > 0 ? " + " + Relation.Modifier : string.Empty);

    public string Control => !Plain ? FullControl : PlainControl;

    private string FullControl => JoyPro.Gui.Services.ReadableNames.Control(Relation.Control, Relation.Label);

    private string PlainInput
    {
        get
        {
            string input = JoyPro.Gui.Services.ReadableNames.Input(Relation.Input);
            if (Relation.Label.Trim().Length > 0 || (Relation.IsAxis && Relation.Control.Length > 0))
                return FullControl.Length > 0 ? FullControl : input;
            return input;
        }
    }

    // The second line of the quick path: the part's name when it says more than the first line ("Trigger first stage"
    // under "Button 1"); never a hat's catalog name.
    private string PlainControl
    {
        get
        {
            if (Relation.IsAxis || Relation.Label.Trim().Length > 0 || Relation.Control.Contains(".hat", StringComparison.Ordinal))
                return string.Empty;
            string control = FullControl;
            return string.Equals(control, PlainInput, StringComparison.OrdinalIgnoreCase) ? string.Empty : control;
        }
    }

    // The quick path: the game's function in plain words, in the language shown ("GearIn [A-10C, AJS37 +52]" -> "Landing
    // gear up", "TrimRudderLeft" -> "Trim rudder left"): never a slot id, never the list of aircraft in brackets.
    public string Name => (Plain ? (nameOverride.Length > 0 ? nameOverride : JoyPro.Gui.Services.FunctionNames.Plain(Relation.Name, Relation.Function)) : Relation.Name) +
                          (Relation.Removed ? T(" (removed)") : string.Empty);

    // "GearIn [A-10C, A-10C II, AJS37 +52]" -> "GearIn": the plan's relation name without the aircraft it is split for.
    internal static string WithoutAircraft(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty, @"\s*\[[^\[\]]*\]\s*$", string.Empty).Trim();

    // "TrimRudderLeft" -> "Trim rudder left", "Gear TOGGLE (GearIn)" -> "Gear toggle", "Autopilot - Pitch ON" ->
    // "Autopilot - Pitch on", "FBW_Disconnect" -> "FBW disconnect", "SPACE_GEAR_DOWN" -> "Gear down"; short capitals
    // (EXT, TMS) stay.
    internal static string PlainName(string name)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(WithoutAircraft(name), @"\s*\([^()]*\)\s*$", string.Empty).Trim();
        if (text.Length == 0)
            return name ?? string.Empty;
        // A slot written with underscores (Star Citizen's SPACE_ actions, "FBW_Disconnect", "Trim_NoseDown"): words.
        if (text.Contains('_', StringComparison.Ordinal))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^space_", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string[] words = text.Split('_', StringSplitOptions.RemoveEmptyEntries);
            text = string.Join(" ", words.Select(w => w.Length > 1 && w.All(char.IsUpper) && w.Length >= 4 ? w.ToLowerInvariant() : w));
            if (text.Length == 0)
                return name ?? string.Empty;
        }
        // An id written together: split before each capital that follows a small letter or digit.
        if (!text.Contains(' ', StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(text, @"[a-z][A-Z]"))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length > 1 && parts[i].Skip(1).All(char.IsLower))
                    parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
            }
            text = string.Join(" ", parts);
        }
        // Shouted words (TOGGLE, ON, OFF) in small letters; short abbreviations stay.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b[A-Z]{2,}\b", m =>
            m.Value is "ON" or "OFF" || m.Value.Length >= 4 ? m.Value.ToLowerInvariant() : m.Value);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public string Category => JoyPro.Gui.Services.EngineText.Tr(SetupImportance.CategoryTitle(Relation.Category.Length > 0 ? Relation.Category : SetupImportance.Systems));

    public string Reason => Relation.Kept ? T("your bind, kept") : Plain ? PlainReason(Relation.Reason) : JoyPro.Gui.Services.EngineText.Tr(Relation.Reason);

    internal static string PlainReason(string reason)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(reason ?? string.Empty, @"^a fitting position \(preference \d+\)$"))
            return T("a good place for it");
        return JoyPro.Gui.Services.EngineText.Tr(reason ?? string.Empty);
    }

    public string Aircraft => Relation.Aircraft.Count <= 3 ? string.Join(", ", Relation.Aircraft) : string.Join(", ", Relation.Aircraft.Take(3)) + " +" + (Relation.Aircraft.Count - 3);

    public bool IsKept => Relation.Kept;

    public bool IsRemoved => Relation.Removed;

    public override string ToString() => Relation.Name;
}

public sealed class SetupQualityRow
{
    public SetupQualityRow(SetupQuality quality)
    {
        Quality = quality;
    }

    public SetupQuality Quality { get; }

    public string Aircraft => Quality.Aircraft + (Quality.Game != "DCS" && Quality.Game != Quality.Aircraft ? " (" + Quality.Game + ")" : string.Empty);

    public string Priority => Quality.Tier >= 0 ? T("Priority {0}", (Quality.Tier + 1)) : T("not listed");

    public string Score => Quality.Score.ToString("0") + " %";

    public double ScoreValue => Quality.Score;

    public string Placed => T("{0} / {1} placed", Quality.Placed, Quality.Total);

    public string Essential => T("{0} / {1} essential", Quality.EssentialPlaced, Quality.EssentialTotal);
}

public sealed class SetupNotPlacedRow
{
    public SetupNotPlacedRow(SetupNotPlaced item, bool plain = false)
    {
        Item = item;
        Plain = plain;
    }

    public SetupNotPlaced Item { get; }

    // The quick path: plain reasons (no "flight axes only take their own functions").
    public bool Plain { get; }

    public string Name => Plain ? JoyPro.Gui.Services.FunctionNames.Plain(Item.Relation, Item.Function) : Item.Relation;

    // The quick review's "important, no room": an essential of the aircraft that nothing else covers and that is no
    // hardware limit (a T.Flight has no spare lever for the propeller and mixture: the game has keys for them).
    public bool IsImportantGap => Item.Essential && !HardwareLimit(Item);

    // An extra lever the devices do not have (a second engine, the propeller, mixture, a wheel brake axis): not a gap
    // of the setup. The flight axes themselves (pitch, roll, rudder, collective) always count.
    internal static bool HardwareLimit(SetupNotPlaced item)
    {
        string reason = item.Reason ?? string.Empty;
        if (!reason.StartsWith("no fitting axis", StringComparison.Ordinal) && !reason.StartsWith("no axis on the devices", StringComparison.Ordinal))
            return false;
        string id = (item.Function ?? string.Empty).Trim().ToLowerInvariant();
        return id is not ("pitch" or "roll" or "rudder" or "yaw" or "collective") && !id.StartsWith("cyclic", StringComparison.Ordinal);
    }

    public string Category => JoyPro.Gui.Services.EngineText.Tr(SetupImportance.CategoryTitle(Item.Category.Length > 0 ? Item.Category : SetupImportance.Systems));

    // (the quick path: a lever the devices do not have is said to be normal, not a missing place)
    public string Reason => !Plain ? JoyPro.Gui.Services.EngineText.Tr(Item.Reason)
        : HardwareLimit(Item) ? T("Your stick has no spare lever for it. That is normal: the game has keys for it too.")
        : PlainReason(Item.Reason);

    internal static string PlainReason(string reason)
    {
        string text = reason ?? string.Empty;
        if (text.StartsWith("no fitting axis", StringComparison.Ordinal) || text.StartsWith("no axis on the devices", StringComparison.Ordinal))
            return T("no free lever or axis left for it");
        if (text.StartsWith("every fitting input is taken", StringComparison.Ordinal) || text.StartsWith("no fitting input", StringComparison.Ordinal) ||
            text.StartsWith("no button on the devices", StringComparison.Ordinal) || text.StartsWith("no hat with the free", StringComparison.Ordinal))
            return T("no free button left for it");
        return JoyPro.Gui.Services.EngineText.Tr(text);
    }

    // A function family the plan places another member of for the same aircraft (the wheel brake axes next to the wheel
    // brake button, a game's zoom buttons next to its zoom axis): the throttle (ThrottleLeft,
    // ThrottleRight, ThrottleLeftNonAB ... and the combined throttle), so a missing one-engine lever is no gap.
    internal static bool CoveredByPlan(SetupPlan plan, SetupNotPlaced item)
    {
        // A game's zoom buttons (IL-2 "Cockpit view: zoom in / out / reset") next to its placed zoom axis.
        if (SetupReviewRow.WithoutAircraft(item.Relation).Contains("zoom", StringComparison.OrdinalIgnoreCase) &&
            item.Aircraft.Count > 0 && item.Aircraft.All(a => plan.Quality.Any(q => string.Equals(q.Aircraft, a, StringComparison.OrdinalIgnoreCase) && string.Equals(q.Game, a, StringComparison.OrdinalIgnoreCase))) &&
            plan.Relations.Any(r => r.Device.Length > 0 && r.Input.Length > 0 && r.IsAxis && string.Equals(r.Function, "zoom", StringComparison.OrdinalIgnoreCase) && // nonloc
                                    item.Aircraft.All(a => r.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase) || r.Overlap.Contains(a, StringComparer.OrdinalIgnoreCase))))
            return true;
        string family = Family(item.Function.Length > 0 ? item.Function : item.Relation);
        if (family.Length == 0)
            return false;
        return plan.Relations.Any(r => r.Device.Length > 0 && r.Input.Length > 0 && Family(r.Function.Length > 0 ? r.Function : r.Name) == family &&
                                       (item.Aircraft.Count == 0 || item.Aircraft.All(a => r.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase) || r.Overlap.Contains(a, StringComparer.OrdinalIgnoreCase))));
    }

    private static string Family(string function)
    {
        string id = (function ?? string.Empty).Trim().ToLowerInvariant();
        if (id.StartsWith("throttle", StringComparison.Ordinal) && !id.Contains("button") && !id.Contains("detent"))
            return "throttle";
        // The wheel brakes: the toe brake axes are covered by the wheel brake button (IL-2 "Wheel brakes" on R3).
        return id.StartsWith("wheelbrake", StringComparison.Ordinal) ? "wheelbrake" : string.Empty;
    }

    public string Aircraft => Item.Aircraft.Count <= 3 ? string.Join(", ", Item.Aircraft) : string.Join(", ", Item.Aircraft.Take(3)) + " +" + (Item.Aircraft.Count - 3);
}

public sealed record SetupTargetItem(string Device, string Input, string Modifier, string Text)
{
    public override string ToString() => Text;
}

public sealed record SetupSheetItem(string Device, string Title, string File);

// A game on the Start page's auto setup card (MainWindowViewModel.AutoSetupStart.cs): empty (nothing assigned), game (binds
// of the user's own in the game) or profile (already in the profile).
public sealed class AutoSetupGameItem
{
    public AutoSetupGameItem(string gameId, string name, string status, int gameBinds, int profileBinds)
    {
        GameId = gameId;
        Name = name;
        Status = status;
        GameBinds = gameBinds;
        ProfileBinds = profileBinds;
    }

    public string GameId { get; }

    public string Name { get; }

    public string Status { get; }

    public int GameBinds { get; }

    public int ProfileBinds { get; }

    public string Detail => Status == "game"
        ? Plural("{0} bind in the game", "{0} binds in the game", GameBinds, GameBinds)
        : Status == "profile" ? Plural("{0} bind in your profile", "{0} binds in your profile", ProfileBinds, ProfileBinds) : T("Nothing assigned yet");
}
