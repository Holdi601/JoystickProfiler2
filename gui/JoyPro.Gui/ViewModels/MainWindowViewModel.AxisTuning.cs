using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The axis curve manager (Binds page, "Axis Tuning"): the selected axis relation's response per scope (all games, a
// game, a DCS aircraft, a game family), edited with the scope's game model (engine/AxisTuning.cs, linked) and previewed
// live without the engine; the engine only loads (binds tuning show) and stores (binds tuning set / clear).
public sealed partial class MainWindowViewModel
{
    public const int AxisTuningPreviewPoints = 201;

    // Families of games that share one response file format (engine/BindTuning.cs).
    private static readonly (string Family, string Label, string[] Games)[] AxisTuningFamilies =
    [
        ("il2", N("IL-2 family (Great Battles + Korea)"), ["IL2Game", "IL2Korea"]),
        ("vtolvr", N("VTOL VR family"), ["VTOLVR", "VTOLVRNative"]),
        // Arma 3 has its own model and no family; an old "family:arma" (all three) is still read by the engine.
        ("arma2", N("Arma 2 family (Arma 2, Arma 2 OA)"), ["Arma2", "Arma2OA"]),
        // One game per version; a tuning saved for the single game before the split is in the family now.
        ("xplane", N("X-Plane family (X-Plane 12, X-Plane 11)"), ["XPlane12", "XPlane11"]),
        ("msfs", N("MSFS family (MSFS 2024, MSFS 2020)"), ["MSFS2024", "MSFS2020"])
    ];

    private static readonly Dictionary<string, string> AxisTuningGameLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DCS"] = "DCS",
        ["IL2Game"] = "IL-2 Great Battles",
        ["IL2Korea"] = "IL-2 Korea",
        ["VTOLVR"] = "VTOL VR",
        ["VTOLVRNative"] = "VTOL VR (native)",
        ["StarCitizen"] = "Star Citizen",
        ["Arma3"] = "Arma 3",
        ["Arma2"] = "Arma 2",
        ["Arma2OA"] = "Arma 2 OA",
        ["ArmaReforger"] = "Arma Reforger",
        ["XPlane12"] = "X-Plane 12",
        ["XPlane11"] = "X-Plane 11",
        ["MSFS2024"] = "MSFS 2024",
        ["MSFS2020"] = "MSFS 2020"
    };

    public ObservableCollection<AxisTuningScopeItem> AxisTuningScopes { get; } = [];

    public ObservableCollection<AxisParameterEditor> AxisTuningEditors { get; } = [];

    [ObservableProperty]
    private AxisTuningScopeItem? selectedAxisTuningScope;

    [ObservableProperty]
    private bool isAxisTuningExpanded;

    [ObservableProperty]
    private string axisTuningStatus = "Select an axis relation.";

    [ObservableProperty]
    private string axisTuningModelText = string.Empty;

    [ObservableProperty]
    private string axisTuningValidation = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<Point> axisTuningCurve = [];

    // The base curve, dashed, while the scope inherits.
    [ObservableProperty]
    private IReadOnlyList<Point>? axisTuningReferenceCurve;

    // Live stick position -1..1 (NaN: none) and what the curve makes of it.
    [ObservableProperty]
    private double axisTuningStickInput = double.NaN;

    [ObservableProperty]
    private double axisTuningStickOutput;

    [ObservableProperty]
    private string axisTuningLiveText = string.Empty;

    [ObservableProperty]
    private bool axisTuningIsOwn;

    [ObservableProperty]
    private bool axisTuningLoaded;

    // The editors differ from what was loaded (not saved yet).
    [ObservableProperty]
    private bool axisTuningHasEdits;

    // Free control points of a game curve with any number of stops (X-Plane; AxisModel.CurveStops): a table and handles on
    // the preview; saved with binds tuning set --curve-points.
    public ObservableCollection<AxisControlPointEditor> AxisTuningControlPoints { get; } = [];

    [ObservableProperty]
    private bool axisTuningUseControlPoints;

    // Draggable points on the preview (curve coordinates): the free control points, or the 11 user-curve points.
    [ObservableProperty]
    private IReadOnlyList<Point>? axisTuningHandles;

    public bool AxisTuningSupportsControlPoints => SelectedAxisTuningScope?.Model.CurveStops != null;

    public bool HasAxisTuningValidation => AxisTuningValidation.Length > 0;

    public bool CanClearAxisTuning => AxisTuningLoaded && SelectedAxisTuningScope is { IsBase: false } && AxisTuningIsOwn;

    public bool CanSaveAxisTuning => AxisTuningLoaded && SelectedAxisTuningScope != null && AxisTuningValidation.Length == 0 && axisTuningHasBind;

    private string axisTuningRelation = string.Empty;
    private bool axisTuningHasBind;
    private AxisTuning axisTuningLoadedValues = new();
    private AxisTuning axisTuningCurrent = new();
    private string axisTuningEditorBaseline = string.Empty;
    private int axisTuningScopesVersion;
    private int axisTuningLoadVersion;
    private bool axisTuningFillingEditors;
    private CancellationTokenSource? axisTuningMonitorCts;
    private string axisTuningMonitorDevice = string.Empty;
    // The live stick position comes from the live input monitor (MainWindowViewModel.LiveInput.cs), not an own one.
    private bool axisTuningSharesLiveMonitor;

    // The curve's live position runs (tests).
    internal bool IsAxisTuningMonitoring => axisTuningMonitorCts != null;
    private int axisTuningMonitorAxis = -1;

    partial void OnIsAxisTuningExpandedChanged(bool value)
    {
        if (value && SelectedBindIsAxis && SelectedBind != null &&
            !string.Equals(axisTuningRelation, SelectedBind.RelationName, StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadAxisTuningScopesAsync();
        }
        UpdateAxisTuningMonitor();
    }

    partial void OnSelectedAxisTuningScopeChanged(AxisTuningScopeItem? value)
    {
        OnPropertyChanged(nameof(AxisTuningSupportsControlPoints));
        RaiseAxisTuningCommandState();
        if (value != null)
            _ = LoadAxisTuningScopeAsync();
    }

    partial void OnAxisTuningValidationChanged(string value)
    {
        OnPropertyChanged(nameof(HasAxisTuningValidation));
        RaiseAxisTuningCommandState();
    }

    partial void OnAxisTuningIsOwnChanged(bool value) => RaiseAxisTuningCommandState();

    partial void OnAxisTuningLoadedChanged(bool value) => RaiseAxisTuningCommandState();

    private void RaiseAxisTuningCommandState()
    {
        OnPropertyChanged(nameof(CanClearAxisTuning));
        OnPropertyChanged(nameof(CanSaveAxisTuning));
    }

    // Called when the bind selection changes (not for a re-selection after a list rebuild).
    private void OnAxisTuningBindSelectionChanged()
    {
        StopAxisTuningMonitor();
        axisTuningScopesVersion++;
        axisTuningLoadVersion++;
        axisTuningRelation = string.Empty;
        AxisTuningLoaded = false;
        AxisTuningScopes.Clear();
        AxisTuningEditors.Clear();
        ClearAxisTuningControlPoints();
        AxisTuningCurve = [];
        AxisTuningReferenceCurve = null;
        AxisTuningHandles = null;
        AxisTuningValidation = string.Empty;
        AxisTuningModelText = string.Empty;
        AxisTuningStatus = SelectedBindIsAxis ? T("Loading...") : T("Select an axis relation.");
        if (IsAxisTuningExpanded && SelectedBindIsAxis && SelectedBind != null)
            _ = LoadAxisTuningScopesAsync();
        UpdateAxisTuningMonitor();
    }

    // The scopes of the selected relation: from its nodes (relations show).
    private async Task LoadAxisTuningScopesAsync()
    {
        BindListItem? bind = SelectedBind;
        if (bind == null || !SelectedBindIsAxis)
            return;
        string relation = bind.RelationName;
        int version = ++axisTuningScopesVersion;
        axisTuningRelation = relation;
        AxisTuningStatus = T("Loading...");

        EngineCommandResult? result = await RunEngineAsync(["relations", "show", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading axis tuning scopes..."));
        if (version != axisTuningScopesVersion)
            return;
        if (result == null || result.ExitCode != 0)
        {
            AxisTuningStatus = T("Could not load the relation's games.");
            return;
        }

        List<(string Game, IReadOnlyList<string> Planes)> nodes = ParseAxisTuningNodes(result.StandardOutput);
        string previous = SelectedAxisTuningScope?.Scope ?? string.Empty;
        AxisTuningScopes.Clear();
        // Games the user does not have (Settings > Games) get no scope of their own.
        foreach (AxisTuningScopeItem item in BuildAxisTuningScopes(nodes.Where(n => !IsGameHidden(n.Game))))
            AxisTuningScopes.Add(item);
        SelectedAxisTuningScope = AxisTuningScopes.FirstOrDefault(s => s.Scope == previous) ?? AxisTuningScopes.FirstOrDefault();
    }

    internal static List<(string Game, IReadOnlyList<string> Planes)> ParseAxisTuningNodes(string output)
    {
        List<(string, IReadOnlyList<string>)> nodes = [];
        foreach (string raw in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = RelationNodeLine.Match(raw.TrimEnd());
            if (!match.Success)
                continue;
            string aircraft = match.Groups["aircraft"].Value.Trim();
            string[] planes = aircraft.Length == 0 || aircraft.Equals("<none>", StringComparison.OrdinalIgnoreCase)
                ? []
                : aircraft.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            nodes.Add((match.Groups["game"].Value.Trim(), planes));
        }
        return nodes;
    }

    // All games (base), then every game of the relation (DCS followed by its active aircraft), then the families.
    internal static List<AxisTuningScopeItem> BuildAxisTuningScopes(IEnumerable<(string Game, IReadOnlyList<string> Planes)> nodes)
    {
        List<AxisTuningScopeItem> scopes = [new AxisTuningScopeItem("base", T("All games (base)"), "DCS", string.Empty)];
        List<string> games = [];
        Dictionary<string, SortedSet<string>> planes = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string game, IReadOnlyList<string> nodePlanes) in nodes)
        {
            if (string.IsNullOrWhiteSpace(game))
                continue;
            string id = game.Trim();
            if (!games.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                games.Add(id);
                planes[id] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            foreach (string plane in nodePlanes)
                planes[id].Add(plane);
        }

        foreach (string game in games)
        {
            scopes.Add(new AxisTuningScopeItem("game:" + game, AxisTuningGameLabel(game), game, string.Empty));
            if (!game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string plane in planes[game])
                scopes.Add(new AxisTuningScopeItem("plane:" + game + ":" + plane, T("DCS / {0}", plane), game, plane));
        }

        foreach ((string family, string label, string[] members) in AxisTuningFamilies)
        {
            List<string> present = games.Where(g => members.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
            if (present.Count == 0)
                continue;
            // The family is edited with the first researched model among its games (Great Battles and Korea read each other's keys).
            string preview = present.FirstOrDefault(g => AxisModels.ForGame(g).Id != AxisModels.Generic) ?? present[0];
            scopes.Add(new AxisTuningScopeItem("family:" + family, T(label), preview, string.Empty));
        }
        return scopes;
    }

    // The label of a scope as BuildAxisTuningScopes makes it, in the language now.
    internal static string AxisTuningScopeLabelNow(AxisTuningScopeItem item)
    {
        if (item.IsBase)
            return T("All games (base)");
        if (item.Scope.StartsWith("game:", StringComparison.Ordinal))
            return AxisTuningGameLabel(item.Game);
        if (item.Scope.StartsWith("plane:", StringComparison.Ordinal))
            return T("DCS / {0}", item.Plane);
        if (item.IsFamily)
        {
            string family = item.Scope["family:".Length..];
            return AxisTuningFamilies.FirstOrDefault(f => f.Family == family).Label is { } familyLabel ? T(familyLabel) : item.Label;
        }
        return item.Label;
    }

    private static string AxisTuningGameLabel(string game) =>
        AxisTuningGameLabels.TryGetValue(game, out string? label) ? label : game;

    private string AxisTuningScopeLabel(string scope)
    {
        AxisTuningScopeItem? item = AxisTuningScopes.FirstOrDefault(s => string.Equals(s.Scope, scope, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            return item.Label;
        if (scope == "base")
            return T("All games (base)");
        string[] parts = scope.Split(':');
        if (parts.Length >= 2 && parts[0] == "game")
            return AxisTuningGameLabel(parts[1]);
        if (parts.Length >= 3 && parts[0] == "plane")
            return AxisTuningGameLabel(parts[1]) + " / " + string.Join(":", parts.Skip(2));
        if (parts.Length >= 2 && parts[0] == "family")
            return AxisTuningFamilies.FirstOrDefault(f => f.Family == parts[1]).Label is { } familyLabel ? T(familyLabel) : scope;
        return scope;
    }

    // binds tuning show: every stored scope, and (for a game / aircraft) what that game uses.
    private async Task LoadAxisTuningScopeAsync()
    {
        AxisTuningScopeItem? scope = SelectedAxisTuningScope;
        string relation = axisTuningRelation;
        if (scope == null || relation.Length == 0)
            return;
        int version = ++axisTuningLoadVersion;
        AxisTuningLoaded = false;

        List<string> args = ["binds", "tuning", "show", relation];
        bool resolves = scope.Scope.StartsWith("game:", StringComparison.Ordinal) || scope.Scope.StartsWith("plane:", StringComparison.Ordinal);
        if (resolves)
        {
            args.AddRange(["--game", scope.Game]);
            if (scope.Plane.Length > 0)
                args.AddRange(["--plane", scope.Plane]);
        }
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading axis tuning..."), forceText: false);
        if (version != axisTuningLoadVersion || !ReferenceEquals(scope, SelectedAxisTuningScope))
            return;
        if (result == null || result.ExitCode != 0)
        {
            AxisTuningStatus = T("Could not load the axis tuning (see output).");
            return;
        }

        Dictionary<string, string> stored = new(StringComparer.OrdinalIgnoreCase);
        string resolvedTuning = string.Empty;
        string resolvedSource = string.Empty;
        bool fitted = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("Scopes", out JsonElement scopes) && scopes.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in scopes.EnumerateObject())
                    stored[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? string.Empty : string.Empty;
            }
            if (root.TryGetProperty("Resolved", out JsonElement resolved) && resolved.ValueKind == JsonValueKind.Object)
            {
                resolvedTuning = GetJsonString(resolved, "Tuning");
                resolvedSource = GetJsonString(resolved, "Source");
                fitted = resolved.TryGetProperty("Fitted", out JsonElement fit) && fit.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            AxisTuningStatus = T("The engine's axis tuning answer could not be read.");
            return;
        }

        AxisModel model = scope.Model;
        AxisTuning baseTuning = AxisTuning.Parse(stored.TryGetValue("base", out string? baseText) ? baseText : string.Empty);
        AxisTuning values;
        string source;
        if (stored.TryGetValue(scope.Scope, out string? own) && !scope.IsBase)
        {
            values = AxisTuning.Parse(own);
            source = scope.Scope;
            fitted = false;
        }
        else if (scope.IsBase)
        {
            values = baseTuning;
            source = "base";
            fitted = false;
        }
        else if (resolves && resolvedTuning.Length > 0)
        {
            values = AxisTuning.Parse(resolvedTuning);
            source = resolvedSource.Length > 0 ? resolvedSource : "base";
        }
        else
        {
            // A family without its own values: the base, fitted to the family's model (as the engine resolves it).
            values = model.Fit == null ? baseTuning.Clone() : AxisModels.Inherit(baseTuning, model);
            source = "base";
            fitted = model.Fit != null;
        }

        bool isOwn = string.Equals(source, scope.Scope, StringComparison.OrdinalIgnoreCase);
        AxisTuningIsOwn = isOwn;
        axisTuningHasBind = SelectedBind?.HasBind ?? false;
        AxisTuningStatus = isOwn
            ? T("Own settings for {0}", scope.Label)
            : T("Inherited from {0}{1}", AxisTuningScopeLabel(source), (fitted ? T(" — fitted to this game's model") : string.Empty));
        AxisTuningModelText = T("Model: {0}. {1}{2}", model.Title, model.Description, (axisTuningHasBind ? string.Empty : T(" Bind a device axis before saving.")));
        AxisTuningReferenceCurve = isOwn ? null : SampleAxisCurve(AxisModels.Get(AxisModels.Dcs), baseTuning);
        LoadAxisTuningEditors(model, values);
        AxisTuningLoaded = true;
    }

    // ---- editors ------------------------------------------------------------------------------------------------

    private void LoadAxisTuningEditors(AxisModel model, AxisTuning tuning)
    {
        axisTuningFillingEditors = true;
        axisTuningLoadedValues = tuning.Clone();
        foreach (AxisParameterEditor old in AxisTuningEditors)
        {
            old.Changed -= OnAxisTuningEditorChanged;
            old.SwitchedOn -= OnAxisTuningUserCurveSwitchedOn;
        }
        AxisTuningEditors.Clear();
        foreach (AxisParameter parameter in model.Parameters)
        {
            AxisParameterEditor editor = new(parameter);
            switch (parameter.Kind)
            {
                case AxisParameterKind.Toggle:
                    editor.IsOn = parameter.Key switch
                    {
                        "invert" => tuning.Inverted,
                        "slider" => tuning.Slider,
                        _ => tuning.NativeOr(parameter.Key, 0) != 0
                    };
                    break;
                case AxisParameterKind.UserCurve:
                    editor.IsOn = tuning.IsUserCurve;
                    editor.LoadCurve(tuning.IsUserCurve ? tuning.Curvature : CurvatureAsUserCurve(CurvatureOf(tuning)));
                    break;
                default:
                    editor.Load(ReadAxisParameter(tuning, parameter));
                    break;
            }
            editor.Changed += OnAxisTuningEditorChanged;
            editor.SwitchedOn += OnAxisTuningUserCurveSwitchedOn;
            AxisTuningEditors.Add(editor);
        }
        ClearAxisTuningControlPoints();
        if (model.CurveStops != null && tuning.HasControlPoints)
        {
            foreach (KeyValuePair<double, double> point in tuning.ControlPoints().OrderBy(p => p.Key))
                AddAxisTuningControlPointEditor(point.Key, point.Value);
        }
        AxisTuningUseControlPoints = AxisTuningControlPoints.Count > 0;
        axisTuningFillingEditors = false;
        UpdateAxisTuningEditorStates();
        axisTuningEditorBaseline = BuildAxisTuningFromEditors(out _)?.Format() ?? string.Empty;
        RecomputeAxisTuningPreview();
    }

    private void OnAxisTuningEditorChanged(object? sender, EventArgs e)
    {
        if (axisTuningFillingEditors)
            return;
        UpdateAxisTuningEditorStates();
        RecomputeAxisTuningPreview();
    }

    // Switching the user curve on starts it from the current curvature.
    private void OnAxisTuningUserCurveSwitchedOn(object? sender, EventArgs e)
    {
        if (axisTuningFillingEditors || sender is not AxisParameterEditor curveEditor)
            return;
        axisTuningFillingEditors = true;
        curveEditor.LoadCurve(CurvatureAsUserCurve(AxisTuningEditors.FirstOrDefault(p => p.Key == "curvature")?.Value ?? 0));
        axisTuningFillingEditors = false;
    }

    private void UpdateAxisTuningEditorStates()
    {
        bool userCurve = AxisTuningEditors.Any(e => e.IsUserCurve && e.IsOn);
        // Free curve points replace dead zone, saturation, curvature and the 11-point curve (X-Plane writes only the stops).
        bool freeCurve = AxisTuningSupportsControlPoints && AxisTuningUseControlPoints;
        foreach (AxisParameterEditor editor in AxisTuningEditors)
        {
            // Star Citizen's exponent and curve exclude each other like DCS's curvature and user curve.
            if (editor.Key is "curvature" or "exponent")
                editor.IsActive = !userCurve && !freeCurve;
            else if (editor.IsUserCurve || editor.Key is "deadzone" or "saturationX" or "saturationY")
                editor.IsActive = !freeCurve;
        }
    }

    // ---- free curve points --------------------------------------------------------------------------------------

    partial void OnAxisTuningUseControlPointsChanged(bool value)
    {
        if (axisTuningFillingEditors)
            return;
        // Switching on starts from the stops the game gets now (dead zone, saturation, curvature / user curve).
        AxisModel? model = SelectedAxisTuningScope?.Model;
        if (value && AxisTuningControlPoints.Count == 0 && model?.CurveStops != null)
        {
            AxisTuning? current = BuildAxisTuningFromEditors(out _, withControlPoints: false);
            if (current != null)
            {
                axisTuningFillingEditors = true;
                foreach (KeyValuePair<double, double> stop in model.CurveStops(current))
                    AddAxisTuningControlPointEditor(Math.Round(stop.Key, 4), Math.Round(stop.Value, 4));
                axisTuningFillingEditors = false;
            }
        }
        UpdateAxisTuningEditorStates();
        RecomputeAxisTuningPreview();
    }

    private void AddAxisTuningControlPointEditor(double x, double y, int index = -1)
    {
        AxisControlPointEditor point = new(x, y);
        point.Changed += OnAxisTuningControlPointChanged;
        if (index < 0 || index > AxisTuningControlPoints.Count)
            AxisTuningControlPoints.Add(point);
        else
            AxisTuningControlPoints.Insert(index, point);
    }

    private void ClearAxisTuningControlPoints()
    {
        foreach (AxisControlPointEditor point in AxisTuningControlPoints)
            point.Changed -= OnAxisTuningControlPointChanged;
        AxisTuningControlPoints.Clear();
    }

    private void OnAxisTuningControlPointChanged(object? sender, EventArgs e)
    {
        if (!axisTuningFillingEditors)
            RecomputeAxisTuningPreview();
    }

    // A new stop in the middle of the widest gap, on the current curve.
    [RelayCommand]
    private void AddAxisTuningControlPoint()
    {
        AxisModel? model = SelectedAxisTuningScope?.Model;
        if (model?.CurveStops == null || !AxisTuningUseControlPoints || AxisTuningControlPoints.Count < 2 || AxisTuningControlPoints.Any(p => !p.IsValid))
            return;
        int gap = 1;
        for (int i = 1; i < AxisTuningControlPoints.Count; i++)
        {
            if (AxisTuningControlPoints[i].X - AxisTuningControlPoints[i - 1].X > AxisTuningControlPoints[gap].X - AxisTuningControlPoints[gap - 1].X)
                gap = i;
        }
        double x = Math.Round((AxisTuningControlPoints[gap - 1].X + AxisTuningControlPoints[gap].X) / 2.0, 4);
        if (x <= AxisTuningControlPoints[gap - 1].X || x >= AxisTuningControlPoints[gap].X)
            return;
        double y = Math.Round((AxisTuningControlPoints[gap - 1].Y + AxisTuningControlPoints[gap].Y) / 2.0, 4);
        AddAxisTuningControlPointEditor(x, y, gap);
        RecomputeAxisTuningPreview();
    }

    // At least two stops stay.
    [RelayCommand]
    private void RemoveAxisTuningControlPoint(AxisControlPointEditor? point)
    {
        if (point == null || AxisTuningControlPoints.Count <= 2 || !AxisTuningControlPoints.Contains(point))
            return;
        point.Changed -= OnAxisTuningControlPointChanged;
        AxisTuningControlPoints.Remove(point);
        RecomputeAxisTuningPreview();
    }

    // The engine's rules for --curve-points: x and y in 0..1, x increasing, at least two points.
    private string AxisTuningControlPointError()
    {
        AxisControlPointEditor? invalid = AxisTuningControlPoints.FirstOrDefault(p => !p.IsValid);
        if (invalid != null)
            return invalid.ValidationMessage;
        if (AxisTuningControlPoints.Count < 2)
            return T("Curve points: at least two points.");
        for (int i = 1; i < AxisTuningControlPoints.Count; i++)
        {
            if (AxisTuningControlPoints[i].X <= AxisTuningControlPoints[i - 1].X)
                return T("Curve points: x must increase from point to point.");
        }
        return string.Empty;
    }

    // ---- dragging on the preview ----------------------------------------------------------------------------------

    // The draggable points: per point its stick travel 0..1 and how to set its output in a tuning.
    private List<(double Travel, Action<AxisTuning, double> SetOutput)> AxisTuningHandlePoints(AxisTuning tuning)
    {
        List<(double, Action<AxisTuning, double>)> points = [];
        AxisModel? model = SelectedAxisTuningScope?.Model;
        if (model?.CurveStops != null && AxisTuningUseControlPoints)
        {
            if (!tuning.HasControlPoints)
                return points;
            List<KeyValuePair<double, double>> stops = tuning.ControlPoints().OrderBy(p => p.Key).ToList();
            for (int i = 0; i < stops.Count; i++)
            {
                int index = i;
                points.Add((stops[i].Key, (t, v) =>
                {
                    List<KeyValuePair<double, double>> own = t.ControlPoints().OrderBy(p => p.Key).ToList();
                    own[index] = new KeyValuePair<double, double>(own[index].Key, v);
                    t.SetControlPoints(own);
                }));
            }
            return points;
        }
        AxisParameterEditor? curve = AxisTuningEditors.FirstOrDefault(e => e.IsUserCurve && e.IsOn && e.IsActive);
        if (curve == null || !tuning.IsUserCurve)
            return points;
        double dz = Math.Clamp(tuning.Deadzone, 0.0, 0.99);
        double sx = tuning.SaturationX <= 0 ? 1.0 : Math.Min(1.0, tuning.SaturationX);
        if (sx <= dz)
            sx = Math.Min(1.0, dz + 0.01);
        for (int i = 0; i < AxisModels.UserCurvePoints; i++)
        {
            int index = i;
            // Just inside the ends: at the dead zone / saturation themselves the models answer 0 / the saturation.
            double position = Math.Clamp(i / (double)(AxisModels.UserCurvePoints - 1), 0.002, 0.998);
            points.Add((dz + (sx - dz) * position, (t, v) => t.Curvature[index] = v));
        }
        return points;
    }

    // Where a point shows on the preview (stick x, the curve there) and how the output there follows the point's value
    // (output = offset + slope * value): the stick side where the point moves the curve most (reversed axes, sliders).
    private static (double X, double Y, double Sign, double Slope, double Offset) AxisTuningHandleGeometry(
        AxisModel model, AxisTuning tuning, double travel, Action<AxisTuning, double> setOutput)
    {
        double side = tuning.Slider ? 2.0 * travel - 1.0 : travel;
        (double X, double Y, double Sign, double Slope, double Offset) best = (side, model.Evaluate(tuning, side), 1.0, 0.0, 0.0);
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            double x = sign * side;
            AxisTuning low = tuning.Clone();
            setOutput(low, 0.0);
            AxisTuning high = tuning.Clone();
            setOutput(high, 1.0);
            double y0 = model.Evaluate(low, x);
            double slope = model.Evaluate(high, x) - y0;
            if (Math.Abs(slope) > Math.Abs(best.Slope) + 1e-9)
                best = (x, model.Evaluate(tuning, x), sign, slope, y0);
        }
        return best;
    }

    private IReadOnlyList<Point>? BuildAxisTuningHandles(AxisModel model, AxisTuning tuning)
    {
        List<(double Travel, Action<AxisTuning, double> SetOutput)> points = AxisTuningHandlePoints(tuning);
        if (points.Count == 0)
            return null;
        return points.Select(p =>
        {
            (double x, double y, _, _, _) = AxisTuningHandleGeometry(model, tuning, p.Travel, p.SetOutput);
            return new Point(x, y);
        }).ToList();
    }

    // A handle dragged on the preview to (x, y) in curve coordinates: the point's output follows y; a free curve point
    // also moves along the stick between its neighbours.
    [RelayCommand]
    private void DragAxisTuningHandle(AxisCurveHandleMove? move)
    {
        AxisModel? model = SelectedAxisTuningScope?.Model;
        if (move == null || model == null || !AxisTuningLoaded)
            return;
        AxisTuning tuning = axisTuningCurrent;
        List<(double Travel, Action<AxisTuning, double> SetOutput)> points = AxisTuningHandlePoints(tuning);
        if (move.Index < 0 || move.Index >= points.Count)
            return;
        (_, _, double sign, double slope, double offset) = AxisTuningHandleGeometry(model, tuning, points[move.Index].Travel, points[move.Index].SetOutput);
        double value = Math.Abs(slope) < 1e-6 ? double.NaN : Math.Round(Math.Clamp((move.Y - offset) / slope, 0.0, 1.0), 3);

        if (model.CurveStops != null && AxisTuningUseControlPoints)
        {
            if (move.Index >= AxisTuningControlPoints.Count)
                return;
            AxisControlPointEditor point = AxisTuningControlPoints[move.Index];
            double travel = tuning.Slider ? (sign * move.X + 1.0) / 2.0 : sign * move.X;
            double low = move.Index > 0 ? AxisTuningControlPoints[move.Index - 1].X + 0.005 : 0.0;
            double high = move.Index < AxisTuningControlPoints.Count - 1 ? AxisTuningControlPoints[move.Index + 1].X - 0.005 : 1.0;
            double x = low <= high ? Math.Round(Math.Clamp(travel, low, high), 3) : point.X;
            point.Set(x, double.IsNaN(value) ? point.Y : value);
            return;
        }
        AxisParameterEditor? curve = AxisTuningEditors.FirstOrDefault(e => e.IsUserCurve && e.IsOn && e.IsActive);
        if (curve != null && !double.IsNaN(value) && move.Index < curve.Points.Count)
            curve.Points[move.Index].Value = value;
    }

    private static double ReadAxisParameter(AxisTuning tuning, AxisParameter parameter) => parameter.Key switch
    {
        "deadzone" => tuning.Deadzone,
        "saturationX" => tuning.SaturationX,
        "saturationY" => tuning.SaturationY,
        "curvature" => CurvatureOf(tuning),
        _ => tuning.NativeOr(parameter.Key, parameter.Default)
    };

    private static double CurvatureOf(AxisTuning tuning) =>
        tuning.IsUserCurve || tuning.Curvature == null || tuning.Curvature.Count == 0 ? 0.0 : tuning.Curvature[0];

    // The 11 user-curve outputs that draw the same shape as a curvature.
    private static List<double> CurvatureAsUserCurve(double curvature)
    {
        AxisModel dcs = AxisModels.Get(AxisModels.Dcs);
        AxisTuning shape = new() { Curvature = [curvature] };
        return Enumerable.Range(0, AxisModels.UserCurvePoints)
            .Select(i => Math.Round(dcs.Evaluate(shape, i / (double)(AxisModels.UserCurvePoints - 1)), 4))
            .ToList();
    }

    // The editors' values as a tuning (null when a value is invalid). withControlPoints false: without free curve points.
    internal AxisTuning? BuildAxisTuningFromEditors(out string error, bool withControlPoints = true)
    {
        error = string.Join(" ", AxisTuningEditors.Where(e => !e.IsValid).Select(e => e.ValidationMessage));
        bool freeCurve = withControlPoints && AxisTuningSupportsControlPoints && AxisTuningUseControlPoints;
        if (freeCurve)
            error = (error + " " + AxisTuningControlPointError()).Trim();
        if (error.Length > 0)
            return null;
        AxisTuning tuning = axisTuningLoadedValues.Clone();
        AxisParameterEditor? curve = AxisTuningEditors.FirstOrDefault(e => e.IsUserCurve);
        bool userCurve = curve is { IsOn: true };
        foreach (AxisParameterEditor editor in AxisTuningEditors)
        {
            switch (editor.Parameter.Kind)
            {
                case AxisParameterKind.Toggle:
                    if (editor.Key == "invert")
                        tuning.Inverted = editor.IsOn;
                    else if (editor.Key == "slider")
                        tuning.Slider = editor.IsOn;
                    else
                        tuning.Native[editor.Key] = editor.IsOn ? 1 : 0;
                    break;
                case AxisParameterKind.UserCurve:
                    if (editor.IsOn)
                        tuning.Curvature = editor.Points.Select(p => Math.Round(p.Value, 4)).ToList();
                    else if (!AxisTuningEditors.Any(e => e.Key == "curvature"))
                        tuning.Curvature = [0.0];
                    break;
                default:
                    switch (editor.Key)
                    {
                        case "deadzone": tuning.Deadzone = editor.Value; break;
                        case "saturationX": tuning.SaturationX = editor.Value; break;
                        case "saturationY": tuning.SaturationY = editor.Value; break;
                        case "curvature":
                            if (!userCurve)
                                tuning.Curvature = [editor.Value];
                            break;
                        default: tuning.Native[editor.Key] = editor.Value; break;
                    }
                    break;
            }
        }
        if (freeCurve)
            tuning.SetControlPoints(AxisTuningControlPoints.Select(p => new KeyValuePair<double, double>(p.X, p.Y)).ToList());
        else if (!withControlPoints || AxisTuningSupportsControlPoints)
            tuning.ClearControlPoints();
        return tuning;
    }

    private void RecomputeAxisTuningPreview()
    {
        AxisTuning? tuning = BuildAxisTuningFromEditors(out string error);
        AxisTuningValidation = error;
        if (tuning == null)
            return;
        axisTuningCurrent = tuning;
        AxisTuningHasEdits = tuning.Format() != axisTuningEditorBaseline;
        AxisModel model = SelectedAxisTuningScope?.Model ?? AxisModels.Get(AxisModels.Dcs);
        AxisTuningCurve = SampleAxisCurve(model, tuning);
        AxisTuningHandles = BuildAxisTuningHandles(model, tuning);
        UpdateAxisTuningLiveOutput();
    }

    internal static IReadOnlyList<Point> SampleAxisCurve(AxisModel model, AxisTuning tuning) =>
        AxisModels.Sample(model, tuning, AxisTuningPreviewPoints).Select(p => new Point(p.Key, p.Value)).ToList();

    // ---- save / inherit -----------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveAxisTuningAsync()
    {
        AxisTuningScopeItem? scope = SelectedAxisTuningScope;
        if (scope == null || axisTuningRelation.Length == 0)
            return;
        List<string>? args = BuildAxisTuningSetArguments(out string error);
        if (args == null)
        {
            AxisTuningValidation = error;
            StatusText = error;
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Saving axis tuning..."));
        if (result?.ExitCode != 0)
            return;
        if (scope.IsBase)
            SyncBaseTuningToBindEditor(axisTuningCurrent);
        StatusText = T("Axis tuning saved for {0}.", scope.Label);
        await LoadAxisTuningScopeAsync();
    }

    internal List<string>? BuildAxisTuningSetArguments(out string error)
    {
        AxisTuningScopeItem? scope = SelectedAxisTuningScope;
        error = string.Empty;
        if (scope == null || axisTuningRelation.Length == 0)
        {
            error = T("Select an axis relation and a scope first.");
            return null;
        }
        if (!axisTuningHasBind)
        {
            error = T("The relation has no bind yet; bind a device axis first.");
            return null;
        }
        AxisTuning? tuning = BuildAxisTuningFromEditors(out error);
        if (tuning == null)
            return null;

        List<string> args = ["binds", "tuning", "set", axisTuningRelation, "--scope", scope.Scope];
        bool userCurve = AxisTuningEditors.Any(e => e.IsUserCurve && e.IsOn);
        foreach (AxisParameterEditor editor in AxisTuningEditors)
        {
            switch (editor.Parameter.Kind)
            {
                case AxisParameterKind.Toggle:
                    if (editor.Key == "invert")
                        args.AddRange(["--inverted", editor.IsOn ? "true" : "false"]);
                    else if (editor.Key == "slider")
                        args.AddRange(["--slider", editor.IsOn ? "true" : "false"]);
                    break;
                case AxisParameterKind.UserCurve:
                    if (userCurve)
                        args.AddRange(["--curvature", string.Join(",", tuning.Curvature.Select(Num))]);
                    else if (!AxisTuningEditors.Any(e => e.Key == "curvature"))
                        args.AddRange(["--curvature", "0"]); // a model without curvature (Star Citizen): curve off
                    break;
                default:
                    switch (editor.Key)
                    {
                        case "deadzone": args.AddRange(["--deadzone", Num(editor.Value)]); break;
                        case "saturationX": args.AddRange(["--saturation-x", Num(editor.Value)]); break;
                        case "saturationY": args.AddRange(["--saturation-y", Num(editor.Value)]); break;
                        case "curvature":
                            if (!userCurve)
                                args.AddRange(["--curvature", Num(editor.Value)]);
                            break;
                        default: args.AddRange(["--param", editor.Key + "=" + Num(editor.Value)]); break;
                    }
                    break;
            }
        }
        // Free curve points (X-Plane): the stops as shown, or "none" to drop stops the scope had.
        if (scope.Model.CurveStops != null)
        {
            if (AxisTuningUseControlPoints)
                args.AddRange(["--curve-points", string.Join(",", AxisTuningControlPoints.Select(p => Num(p.X) + ":" + Num(p.Y)))]);
            else if (axisTuningLoadedValues.HasControlPoints)
                args.AddRange(["--curve-points", "none"]);
        }
        return args;

        static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task ClearAxisTuningAsync()
    {
        AxisTuningScopeItem? scope = SelectedAxisTuningScope;
        if (scope == null || scope.IsBase || axisTuningRelation.Length == 0)
            return;
        EngineCommandResult? result = await RunEngineAsync(["binds", "tuning", "clear", axisTuningRelation, "--scope", scope.Scope],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Clearing axis tuning..."));
        if (result?.ExitCode != 0)
            return;
        StatusText = scope.Label + " inherits its axis tuning again.";
        await LoadAxisTuningScopeAsync();
    }

    [RelayCommand]
    private async Task ReloadAxisTuningAsync()
    {
        if (SelectedAxisTuningScope != null)
            await LoadAxisTuningScopeAsync();
        else if (SelectedBindIsAxis)
            await LoadAxisTuningScopesAsync();
    }

    // Set Bind sends invert / slider from the behavior checkboxes: they must follow a saved base.
    private void SyncBaseTuningToBindEditor(AxisTuning tuning)
    {
        BindInverted = tuning.Inverted;
        BindSlider = tuning.Slider;
        if (SelectedBind != null)
        {
            SelectedBind.Inverted = tuning.Inverted;
            SelectedBind.Slider = tuning.Slider;
            SelectedBind.Deadzone = tuning.Deadzone;
            SelectedBind.SaturationX = tuning.SaturationX;
            SelectedBind.SaturationY = tuning.SaturationY;
            SelectedBind.CurvatureCsv = string.Join(",", tuning.Curvature.Select(c => c.ToString(CultureInfo.InvariantCulture)));
        }
        BindDeadzoneValue = tuning.Deadzone.ToString(CultureInfo.InvariantCulture);
        BindSaturationXValue = tuning.SaturationX.ToString(CultureInfo.InvariantCulture);
        BindSaturationYValue = tuning.SaturationY.ToString(CultureInfo.InvariantCulture);
        BindCurvatureCsv = string.Join(",", tuning.Curvature.Select(c => c.ToString(CultureInfo.InvariantCulture)));
    }

    // After Set Bind (base values or device may have changed).
    private async Task ReloadAxisTuningAfterBindChangeAsync()
    {
        if (!IsAxisTuningExpanded || SelectedAxisTuningScope == null)
            return;
        await LoadAxisTuningScopeAsync();
        StopAxisTuningMonitor();
        UpdateAxisTuningMonitor();
    }

    // ---- live stick position ------------------------------------------------------------------------------------

    private void UpdateAxisTuningMonitor()
    {
        BindListItem? bind = SelectedBind;
        int axis = bind == null ? -1 : HudDevice.AxisIndex((bind.Input ?? string.Empty).Trim());
        bool hasDevice = bind != null && bind.HasBind && !string.IsNullOrWhiteSpace(bind.Device) &&
                         !bind.Device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase);
        bool wanted = IsAxisTuningExpanded && IsBindsStage && SelectedBindIsAxis && hasDevice && axis >= 0;
        if (!wanted)
        {
            StopAxisTuningMonitor();
            AxisTuningLiveText = SelectedBindIsAxis && !(hasDevice && axis >= 0)
                ? T("No device axis bound: the live position needs a bound device axis.")
                : string.Empty;
            return;
        }
        if (axisTuningMonitorCts != null)
            return;
        axisTuningMonitorDevice = bind!.Device.Trim();
        axisTuningMonitorAxis = axis;
        AxisTuningLiveText = T("Move {0} to see it on the curve.", bind.Input);
        axisTuningMonitorCts = new CancellationTokenSource();
        // The Binds page's live input monitor (follow input) already reads the devices: it feeds the curve too
        // (OnLiveAxes), one DirectInput poller instead of two.
        axisTuningSharesLiveMonitor = liveInputCts != null;
        if (!axisTuningSharesLiveMonitor)
            _ = MonitorAxisTuningAsync(axisTuningMonitorDevice, axisTuningMonitorCts.Token);
    }

    private async Task MonitorAxisTuningAsync(string device, CancellationToken token)
    {
        try
        {
            await joystickCaptureService.MonitorAsync(
                _ => { },
                snapshot =>
                {
                    if (!token.IsCancellationRequested && OverlayResolver.SameDevice(device, snapshot.Device))
                        Dispatcher.UIThread.Post(() => OnAxisTuningAxes(snapshot));
                },
                token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AxisTuningLiveText = T("Live position unavailable: {0}", ex.Message);
        }
    }

    private void StopAxisTuningMonitor()
    {
        axisTuningMonitorCts?.Cancel();
        axisTuningMonitorCts?.Dispose();
        axisTuningMonitorCts = null;
        AxisTuningStickInput = double.NaN;
    }

    internal void OnAxisTuningAxes(JoystickAxisSnapshot snapshot)
    {
        if (axisTuningMonitorCts == null || axisTuningMonitorAxis < 0 || axisTuningMonitorAxis >= snapshot.Axes.Length ||
            !OverlayResolver.SameDevice(axisTuningMonitorDevice, snapshot.Device))
        {
            return;
        }
        AxisTuningStickInput = Math.Clamp(2.0 * snapshot.Axes[axisTuningMonitorAxis] - 1.0, -1.0, 1.0);
        UpdateAxisTuningLiveOutput();
    }

    private void UpdateAxisTuningLiveOutput()
    {
        if (double.IsNaN(AxisTuningStickInput))
            return;
        AxisModel model = SelectedAxisTuningScope?.Model ?? AxisModels.Get(AxisModels.Dcs);
        AxisTuningStickOutput = model.Evaluate(axisTuningCurrent, AxisTuningStickInput);
        AxisTuningLiveText = T("in {0} -> out {1}", AxisTuningStickInput.ToString("0.00", CultureInfo.InvariantCulture), AxisTuningStickOutput.ToString("0.00", CultureInfo.InvariantCulture));
    }
}
